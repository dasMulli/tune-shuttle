using Airplay2.Receiver.Timing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Playout;

/// <summary>
/// The one place where decoded frames wait (SPEC-receiver.md 5.3): both stream readers <see cref="Enqueue"/> S16 stereo
/// frames with their RTP timestamp; the anchor (SETRATEANCHORTIME or 0xD7) and the PTP offset (<see cref="IPtpSlave.TryMapToLocal"/>,
/// else the anchor-only mapping of R6) give each frame its play time <c>T</c>; one release thread writes a frame to the
/// <see cref="IReceiverAudioOutput"/> when <c>now &gt;= T - Lpipe + D</c> (R6), learns <c>D</c> from the first frame after
/// a (re-)anchor, drops late frames, fills gaps with silence, corrects drift and resumes a paused output before the first
/// frame (5.1, 5.5).
/// </summary>
/// <remarks>
/// <para>
/// The release thread is a dedicated thread, like the group player's pacer (DEVELOPMENT.md "Code conventions": the
/// documented exception to "no blocking waits"): it sleeps on a wake-up event for the coarse part of a wait and on a
/// <see cref="PrecisionSleeper"/> for the last milliseconds before a release time, and calls the output synchronously so that
/// the output's backpressure holds the thread. All state is guarded by one lock; output calls are made outside it.
/// </para>
/// <para>
/// Mapping (SPEC-receiver.md R6, 5.3): <c>T(rtp) = local(anchorSenderNs) + (rtp - anchorRtp) / 44100</c>, where
/// <c>local</c> is the slave's mapping when it has an estimate (read at every evaluation, so a moving offset shows up as
/// drift), else the last PTP offset once PTP was used, else the anchor-only offset fixed by the first anchor
/// (<c>now + AnchorLead</c>) and kept for later anchors; the first PTP estimate after anchor-only timing is a re-anchor.
/// Before any anchor a realtime stream is timed by its first frame (<c>now + AnchorLead</c>, 5.1:
/// "until the first sync, frames are released anchor-only"); a buffered stream waits for its SETRATEANCHORTIME.
/// A periodic anchor that agrees with the current mapping within <see cref="PlayoutOptions.DriftThreshold"/> while the
/// stream plays (the 0xD7 syncs a realtime sender sends every second) refreshes the mapping and keeps <c>D</c>; any
/// other anchor is a re-anchor and <c>D</c> is learnt again.
/// </para>
/// </remarks>
public sealed class PlayoutBuffer : IAsyncDisposable
{
    private const long NanosPerSecond = 1_000_000_000L;
    private const uint BufferedSequenceMask = 0x7FFFFF;
    private const uint BufferedSequenceHalf = 0x400000;

    /// <summary>How long the release thread waits for a wake-up when nothing is scheduled.</summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(250);

    /// <summary>Below this remaining time the release thread hands over to the <see cref="PrecisionSleeper"/>.</summary>
    private const long PrecisionWindowNanos = 3_000_000;

    /// <summary>The retry interval while the output's queue is at the cap of SPEC-receiver.md 5.4.</summary>
    private const long CapRetryNanos = 1_000_000;

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SortedList<long, Entry> _queue = [];
    private readonly Queue<OutputCommand> _commands = new();
    private readonly List<(uint From, uint Until)> _splices = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly long _lpipeNanos;
    private Thread? _thread;
    private TaskCompletionSource? _threadDone;
    private bool _disposed;

    // Mapping (R6).
    private Anchor? _anchor;
    private (uint Rtp, long LocalNs)? _implicitAnchor;
    private long? _anchorOnlyOffset;
    private long? _lastPtpOffset;
    private bool _anchorOnlyInUse;
    private ulong _loggedTimelineMismatch;
    private long? _offsetD;

    // Sequence bookkeeping.
    private bool _hasReference;
    private uint _referenceRtp;
    private long _referenceExt;
    private long? _nextExt;
    private uint? _flushFence;

    // Output state (5.5).
    private bool _paused;
    private bool _outputPaused = true;
    private long _lastActivityNs;

    // Drift (5.3).
    private bool _driftBaselineSet;
    private long _driftStartNs;
    private long _driftWritten;
    private long _nextDriftCheckNs;
    private bool _driftCorrecting;

    // Counters.
    private long _bufferedFrames;
    private long _lateFrames;
    private long _missingFrames;
    private long _droppedForDrift;
    private long _repeatedForDrift;
    private long _rejectedFrames;

    /// <summary>Creates the buffer; the release thread starts with <see cref="StartAsync"/>.</summary>
    /// <param name="output">Where released frames go (SPEC-receiver.md 5.5).</param>
    /// <param name="clock">The process's one clock as an <see cref="IMonotonicClock"/> (SPEC-receiver.md 5.3: the pacer's timeline).</param>
    /// <param name="streamType">The stream type this buffer serves (SPEC-receiver.md 4.6, 5.3); see <see cref="StreamType"/>.</param>
    /// <param name="slave">The session's PTP slave for <see cref="IPtpSlave.TryMapToLocal"/>, or null for anchor-only timing (R5, R6).</param>
    /// <param name="options">Timing options; the defaults when null.</param>
    /// <param name="logger">Logger.</param>
    public PlayoutBuffer(IReceiverAudioOutput output, IMonotonicClock clock, StreamType streamType, IPtpSlave? slave = null, PlayoutOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);
        Output = output;
        Clock = clock;
        StreamType = streamType;
        Slave = slave;
        Options = options ?? new PlayoutOptions();
        _logger = logger ?? NullLogger.Instance;
        _lpipeNanos = FramesToNanos(Options.Lpipe);
    }

    /// <summary>The output.</summary>
    public IReceiverAudioOutput Output { get; }

    /// <summary>The clock the release rule compares against (SPEC-receiver.md 5.3).</summary>
    public IMonotonicClock Clock { get; }

    /// <summary>The PTP slave, or null in anchor-only timing.</summary>
    public IPtpSlave? Slave { get; }

    /// <summary>The timing options.</summary>
    public PlayoutOptions Options { get; }

    /// <summary>
    /// The stream type this buffer serves, shown in the statistics (SPEC-receiver.md 5.3). A <see cref="Sender.Session.StreamType.Buffered"/>
    /// stream holds its frames until the first SETRATEANCHORTIME (4.7) and is never paused for silence; a realtime one
    /// plays anchor-only from its first frame until the first 0xD7 sync (5.1) and pauses the output after
    /// <see cref="PlayoutOptions.SilencePause"/> without frames (5.5).
    /// </summary>
    public StreamType StreamType { get; }

    /// <summary>The decoder name shown in the statistics (SPEC-receiver.md 5.3, 9.1), or null; set by the stream reader.</summary>
    public string? Decoder { get; set; }

    /// <summary>
    /// The decoded frames queued now, read without the lock or a snapshot (SPEC-receiver.md 5.2: the buffered reader's
    /// decode-ahead check, polled while the buffer is full).
    /// </summary>
    internal long BufferedFrames => Volatile.Read(ref _bufferedFrames);

    /// <summary>The snapshot for Diagnostics (SPEC-receiver.md 5.3, 9.1).</summary>
    public PlayoutStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new PlayoutStatistics(
                    (int)(_bufferedFrames * 1000 / PlayoutOptions.SampleRate),
                    TimeSpan.FromTicks((_offsetD ?? 0) / 100),
                    _lateFrames,
                    _missingFrames,
                    _droppedForDrift,
                    _repeatedForDrift,
                    Decoder,
                    StreamType,
                    _queue.Count > 0 ? _queue.Values[0].Rtp : null);
            }
        }
    }

    /// <summary>Starts the release thread (one per buffer, <c>PrecisionSleeper</c>; SPEC-receiver.md 5.3). Idempotent.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                return Task.CompletedTask;
            }

            _threadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(ReleaseLoop) { IsBackground = true, Name = "Playout release", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        return Task.CompletedTask;
    }

    /// <summary>Queues decoded frames from either stream reader (SPEC-receiver.md 5.3 input).</summary>
    /// <param name="rtpTimestamp">The RTP timestamp of the first frame.</param>
    /// <param name="sequence">The packet or block sequence (16-bit realtime, 23-bit buffered) for gap and splice handling.</param>
    /// <param name="frames">Interleaved S16 stereo; copied.</param>
    /// <remarks>
    /// Frames below an immediate flush's <c>flushUntilSeq</c> or inside a registered splice are discarded (4.7); a frame
    /// whose slot was already released or filled with silence (typically a duplicate retransmission) is discarded without
    /// counting it as late, since its slot was already counted as played or missing (R6); a frame with an RTP timestamp
    /// already queued replaces it.
    /// </remarks>
    public void Enqueue(uint rtpTimestamp, uint sequence, ReadOnlyMemory<short> frames)
    {
        if (frames.Length % 2 != 0)
        {
            throw new ArgumentException("Interleaved stereo needs an even sample count.", nameof(frames));
        }

        var count = frames.Length / 2;
        if (count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastActivityNs = NowNs();
            if (_flushFence is { } fence)
            {
                if (IsBufferedBefore(sequence, fence))
                {
                    return;
                }

                _flushFence = null;
            }

            for (var i = _splices.Count - 1; i >= 0; i--)
            {
                var (from, until) = _splices[i];
                if (InBufferedRange(sequence, from, until))
                {
                    return;
                }

                if (!IsBufferedBefore(sequence, from))
                {
                    // At or beyond until: the splice is complete.
                    _splices.RemoveAt(i);
                }
            }

            var ext = Extend(rtpTimestamp);
            if (_nextExt is { } next && ext < next)
            {
                _rejectedFrames += count;
                return;
            }

            if (_queue.Remove(ext, out var replaced))
            {
                _bufferedFrames -= replaced.Frames;
            }

            _queue.Add(ext, new Entry(rtpTimestamp, ext, sequence, frames.ToArray()));
            _bufferedFrames += count;
        }

        Wake();
    }

    /// <summary>
    /// Sets the anchor <c>(rtpTime, senderTimeNs)</c> from SETRATEANCHORTIME or a 0xD7 sync (SPEC-receiver.md 4.7, 5.1, 5.3)
    /// and forgets the learnt <c>D</c> (R6). A <paramref name="timelineId"/> different from the slave's grandmaster is logged, the anchor applied anyway (RQ4).
    /// </summary>
    /// <remarks>
    /// An anchor that agrees with the current mapping within <see cref="PlayoutOptions.DriftThreshold"/> while the stream
    /// plays is a refresh (the periodic 0xD7 sync): the mapping is updated and <c>D</c> is kept.
    /// </remarks>
    public void SetAnchor(uint rtpTime, ulong senderTimeNs, ulong timelineId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = NowNs();
            var master = Slave?.MasterClockId ?? 0;
            if (timelineId != 0 && master != 0 && timelineId != master && _loggedTimelineMismatch != timelineId)
            {
                _loggedTimelineMismatch = timelineId;
                _logger.LogInformation("Anchor timeline {Timeline:X16} differs from the grandmaster {Master:X16}; applied anyway (RQ4)", timelineId, master);
            }

            long? previous = null;
            if (_anchor is not null)
            {
                previous = PlayTimeWith(_anchor, rtpTime, now);
            }

            var sender = unchecked((long)senderTimeNs);
            if (!TryMapPtp(sender, out _) && _anchorOnlyOffset is null && _lastPtpOffset is null)
            {
                _anchorOnlyOffset = now + Options.AnchorLead.Ticks * 100 - sender;
                _logger.LogDebug("Anchor-only timing: the first anchor plays {Lead} from now (R6)", Options.AnchorLead);
            }

            var anchor = new Anchor(rtpTime, sender);
            var mapped = PlayTimeWith(anchor, rtpTime, now);
            var refresh = previous is { } p && _offsetD is not null && !_paused && !_outputPaused
                && Math.Abs(mapped - p) <= Options.DriftThreshold.Ticks * 100;
            _anchor = anchor;
            _implicitAnchor = null;
            if (!refresh)
            {
                _offsetD = null;
                ResetDrift();
            }
        }

        Wake();
    }

    /// <summary><c>rate 0</c>: pauses the output and keeps the buffered frames (SPEC-receiver.md 4.7, 5.5).</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _paused = true;
            _outputPaused = true;
            ResetDrift();
            _commands.Enqueue(OutputCommand.Pause);
        }

        Wake();
    }

    /// <summary><c>rate 1</c>: resumes the output, then frames per R6 (SPEC-receiver.md 4.7, 5.5).</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _paused = false;
            _outputPaused = false;
            _lastActivityNs = NowNs();
            ResetDrift();
            _commands.Enqueue(OutputCommand.Resume);
        }

        Wake();
    }

    /// <summary>
    /// Drops buffered frames and flushes the output (SPEC-receiver.md 4.5, 4.7, 5.5): every frame for <c>FLUSH</c>
    /// (<paramref name="untilSequence"/> null), frames with a sequence below <paramref name="untilSequence"/> (23-bit modular
    /// compare) for an immediate <c>FLUSHBUFFERED</c>, which also pauses the output.
    /// </summary>
    /// <remarks>
    /// Both forget <c>D</c> and the gap position; after <c>FLUSH</c> the next released frame resumes the output (5.5),
    /// after an immediate <c>FLUSHBUFFERED</c> nothing plays until the next <see cref="Resume"/>. Frames below
    /// <paramref name="untilSequence"/> that are still in flight are discarded when they arrive.
    /// </remarks>
    public void Flush(uint? untilSequence)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (untilSequence is { } until)
            {
                until &= BufferedSequenceMask;
                var drop = new List<long>();
                foreach (var entry in _queue.Values)
                {
                    if (IsBufferedBefore(entry.Sequence, until))
                    {
                        drop.Add(entry.Ext);
                    }
                }

                foreach (var key in drop)
                {
                    _bufferedFrames -= _queue[key].Frames;
                    _queue.Remove(key);
                }

                _flushFence = until;
                _paused = true;
                _commands.Enqueue(OutputCommand.Flush);
                _commands.Enqueue(OutputCommand.Pause);
            }
            else
            {
                _queue.Clear();
                _bufferedFrames = 0;
                _splices.Clear();
                _flushFence = null;
                _commands.Enqueue(OutputCommand.Flush);
            }

            _outputPaused = true;
            _offsetD = null;
            _nextExt = null;
            _hasReference = false;
            ResetDrift();
            if (_queue.Count > 0)
            {
                RebaseReference();
            }
        }

        Wake();
    }

    /// <summary>
    /// Registers a deferred <c>FLUSHBUFFERED</c> splice (SPEC-receiver.md 4.7, 5.5): when the block <paramref name="fromSequence"/>
    /// arrives, blocks up to but excluding <paramref name="untilSequence"/> are dropped without flushing or pausing the output.
    /// </summary>
    /// <remarks>
    /// Blocks of the range already queued are removed at once; later arrivals in the range are discarded until a block at
    /// or after <paramref name="untilSequence"/> arrives. Assumption (not pinned down by SPEC-receiver.md or SPEC-audio.md
    /// 4.3): the kept blocks continue the RTP timestamps where the dropped range began (<c>flushFromTS</c>), as the
    /// sender's own track change does. If a sender continued the timestamp series past the dropped range instead, the
    /// gap would be filled with silence (up to <see cref="PlayoutOptions.MaxTimestampAhead"/>); the RP probes are to
    /// confirm this and, if needed, pass <c>flushFromTS</c>/<c>flushUntilTS</c> here to rebase the gap position.
    /// </remarks>
    public void Splice(uint fromSequence, uint untilSequence)
    {
        var from = fromSequence & BufferedSequenceMask;
        var until = untilSequence & BufferedSequenceMask;
        lock (_gate)
        {
            if (_disposed || from == until)
            {
                return;
            }

            var drop = new List<long>();
            foreach (var entry in _queue.Values)
            {
                if (InBufferedRange(entry.Sequence, from, until))
                {
                    drop.Add(entry.Ext);
                }
            }

            foreach (var key in drop)
            {
                _bufferedFrames -= _queue[key].Frames;
                _queue.Remove(key);
            }

            _splices.Add((from, until));
        }

        Wake();
    }

    /// <summary>Stops the release thread; buffered frames are dropped.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? done;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.Clear();
            _bufferedFrames = 0;
            _commands.Clear();
            done = _threadDone?.Task;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        Wake();
        if (done is not null)
        {
            await done.ConfigureAwait(false);
        }

        _stop.Dispose();
        _wake.Dispose();
        _logger.LogDebug("Playout buffer disposed");
    }

    /// <summary>
    /// R6 for the buffered reader (SPEC-receiver.md 5.2, added by the buffered-input package): true when a frame at
    /// <paramref name="rtpTimestamp"/> is already past <c>release + LateThreshold</c>, so the reader drops its block without
    /// decrypting it; false before an anchor and before <c>D</c> is learnt (D is then learnt from an earlier frame, which
    /// makes this one not late).
    /// </summary>
    internal bool IsLate(uint rtpTimestamp)
    {
        lock (_gate)
        {
            if (_offsetD is not { } d || !TryGetPlayTime(rtpTimestamp, out var playTime))
            {
                return false;
            }

            return NowNs() > playTime - _lpipeNanos + d + (Options.LateThreshold.Ticks * 100);
        }
    }

    /// <summary>Frames discarded because their slot was already released or filled with silence (duplicates); for tests.</summary>
    internal long RejectedFrames
    {
        get
        {
            lock (_gate)
            {
                return _rejectedFrames;
            }
        }
    }

    /// <summary>
    /// The play time <c>T</c> of an RTP timestamp in local nanoseconds under the current mapping (SPEC-receiver.md R6), or
    /// false before any anchor; for tests.
    /// </summary>
    internal bool TryGetPlayTime(uint rtpTimestamp, out long localNanos)
    {
        lock (_gate)
        {
            if (_anchor is not null)
            {
                localNanos = PlayTimeWith(_anchor, rtpTimestamp, NowNs());
                return true;
            }

            if (_implicitAnchor is { } implicitAnchor)
            {
                localNanos = implicitAnchor.LocalNs + RtpDeltaNanos(rtpTimestamp, implicitAnchor.Rtp);
                return true;
            }

            localNanos = 0;
            return false;
        }
    }

    /// <summary>
    /// One pass of the release thread (SPEC-receiver.md 5.3): runs the pending output commands and writes every frame that
    /// is due now; returns the local time of the next deadline, or null when only a new input can make something due.
    /// Tests on a fake clock call it directly instead of starting the thread.
    /// </summary>
    internal async ValueTask<long?> PumpAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutputCommand? command = null;
            Release? release = null;
            long? deadline = null;
            lock (_gate)
            {
                if (_disposed)
                {
                    return null;
                }

                if (_commands.Count > 0)
                {
                    command = _commands.Dequeue();
                }
                else
                {
                    deadline = NextAction(out release);
                }
            }

            if (command is { } c)
            {
                await RunCommandAsync(c, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (release is null)
            {
                return deadline;
            }

            if (release.Value.ResumeFirst)
            {
                await Output.ResumeAsync(cancellationToken).ConfigureAwait(false);
            }

            if (release.Value.PauseOnly)
            {
                await Output.PauseAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Output.WriteAsync(release.Value.Samples, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Decides the next step under the lock: a release to perform, or the deadline to sleep until.</summary>
    private long? NextAction(out Release? release)
    {
        release = null;
        var now = NowNs();
        if (_paused)
        {
            return null;
        }

        while (true)
        {
            if (_queue.Count == 0)
            {
                // 5.5: a realtime stream silent for SilencePause pauses the output; the next frame resumes it. A buffered
                // stream is paused only by the sender's rate 0.
                if (_outputPaused || StreamType == Sender.Session.StreamType.Buffered)
                {
                    return null;
                }

                var silenceDeadline = _lastActivityNs + Options.SilencePause.Ticks * 100;
                if (now < silenceDeadline)
                {
                    return silenceDeadline;
                }

                _logger.LogDebug("Stream silent for {Silence}; pausing the output (5.5)", Options.SilencePause);
                _outputPaused = true;
                _nextExt = null;
                ResetDrift();
                release = new Release(Array.Empty<short>(), ResumeFirst: false, PauseOnly: true);
                return null;
            }

            var head = _queue.Values[0];

            // The next slot is either the head or a gap before it (5.3: gaps become silence when due).
            var gapFrames = 0L;
            if (_nextExt is { } nextExt && head.Ext > nextExt)
            {
                gapFrames = head.Ext - nextExt;
                if (gapFrames > (long)(Options.MaxTimestampAhead.TotalSeconds * PlayoutOptions.SampleRate))
                {
                    // A discontinuity rather than lost packets: start again at the head.
                    _logger.LogDebug("RTP jump of {Frames} frames; not filled with silence", gapFrames);
                    _nextExt = null;
                    gapFrames = 0;
                }
            }

            var slotRtp = gapFrames > 0 ? unchecked(head.Rtp - (uint)gapFrames) : head.Rtp;
            if (!TryPlayTimeLocked(slotRtp, now, out var playTime))
            {
                return null; // buffered stream before its first anchor
            }

            if (_offsetD is null)
            {
                // R6: D = max(0, Lpipe - (T(first) - now)), learnt from the first frame after a (re-)anchor.
                _offsetD = Math.Max(0, _lpipeNanos - (playTime - now));
                _logger.LogDebug("Learnt stream offset D = {OffsetMs} ms (R6)", _offsetD.Value / 1_000_000);
            }

            var releaseAt = playTime - _lpipeNanos + _offsetD.Value;
            if (now > releaseAt + Options.LateThreshold.Ticks * 100)
            {
                if (gapFrames > 0)
                {
                    _missingFrames += gapFrames;
                    _nextExt = head.Ext;
                }
                else
                {
                    _lateFrames += head.Frames;
                    RemoveHead(head);
                    _nextExt = head.Ext + head.Frames;
                }

                ResetDrift();
                continue;
            }

            if (now < releaseAt)
            {
                return releaseAt;
            }

            var frames = gapFrames > 0 ? (int)Math.Min(gapFrames, PlayoutOptions.FramesPerPacket) : head.Frames;
            var queued = Output.QueuedFrames;
            if (queued > 0 && queued + frames > Options.PrefillFrames + PlayoutOptions.FramesPerPacket)
            {
                return now + CapRetryNanos; // 5.4: never more than PrefillFrames + 352 ahead of the pacer
            }

            short[] samples;
            if (gapFrames > 0)
            {
                samples = new short[frames * 2];
                _missingFrames += frames;
                _nextExt = _nextExt!.Value + frames;
            }
            else
            {
                samples = ApplyDrift(head.Samples, now);
                RemoveHead(head);
                _nextExt = head.Ext + head.Frames;
            }

            var resumeFirst = _outputPaused;
            _outputPaused = false;
            _lastActivityNs = Math.Max(_lastActivityNs, now);
            CountWritten(samples.Length / 2, now);
            release = new Release(samples, resumeFirst, PauseOnly: false);
            return null;
        }
    }

    /// <summary>The 10 s drift check of SPEC-receiver.md 5.3 and the single-frame correction per block.</summary>
    private short[] ApplyDrift(short[] samples, long now)
    {
        if (!_driftBaselineSet)
        {
            return samples;
        }

        if (!_driftCorrecting)
        {
            if (now < _nextDriftCheckNs)
            {
                return samples;
            }

            _nextDriftCheckNs = now + Options.DriftCheckInterval.Ticks * 100;
        }

        var aheadFrames = _driftWritten - ((now - _driftStartNs) * PlayoutOptions.SampleRate / NanosPerSecond);
        var threshold = (long)(Options.DriftThreshold.TotalSeconds * PlayoutOptions.SampleRate);
        if (aheadFrames > threshold && samples.Length >= 4)
        {
            if (!_driftCorrecting)
            {
                _logger.LogDebug("Playout {Frames} frames ahead; dropping single frames (5.3)", aheadFrames);
            }

            _driftCorrecting = true;
            _droppedForDrift++;
            return samples.AsSpan(0, samples.Length - 2).ToArray();
        }

        if (aheadFrames < -threshold && samples.Length >= 2)
        {
            if (!_driftCorrecting)
            {
                _logger.LogDebug("Playout {Frames} frames behind; repeating single frames (5.3)", -aheadFrames);
            }

            _driftCorrecting = true;
            _repeatedForDrift++;
            var longer = new short[samples.Length + 2];
            samples.CopyTo(longer, 0);
            longer[^2] = samples[^2];
            longer[^1] = samples[^1];
            return longer;
        }

        if (_driftCorrecting)
        {
            _driftCorrecting = false;
            _nextDriftCheckNs = now + Options.DriftCheckInterval.Ticks * 100;
        }

        return samples;
    }

    private void CountWritten(int frames, long now)
    {
        if (!_driftBaselineSet)
        {
            _driftBaselineSet = true;
            _driftStartNs = now;
            _driftWritten = 0;
            _nextDriftCheckNs = now + Options.DriftCheckInterval.Ticks * 100;
        }

        _driftWritten += frames;
    }

    private void ResetDrift()
    {
        _driftBaselineSet = false;
        _driftCorrecting = false;
        _driftWritten = 0;
    }

    private void RemoveHead(Entry head)
    {
        _queue.RemoveAt(0);
        _bufferedFrames -= head.Frames;
    }

    private async ValueTask RunCommandAsync(OutputCommand command, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case OutputCommand.Pause:
                await Output.PauseAsync(cancellationToken).ConfigureAwait(false);
                break;
            case OutputCommand.Resume:
                await Output.ResumeAsync(cancellationToken).ConfigureAwait(false);
                break;
            case OutputCommand.Flush:
                await Output.FlushAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private bool TryPlayTimeLocked(uint rtp, long now, out long playTime)
    {
        if (_anchor is not null)
        {
            playTime = PlayTimeWith(_anchor, rtp, now);
            return true;
        }

        if (StreamType == Sender.Session.StreamType.Buffered)
        {
            playTime = 0;
            return false;
        }

        // 5.1: until the first sync the stream is timed anchor-only by its first frame.
        _implicitAnchor ??= (rtp, now + Options.AnchorLead.Ticks * 100);
        playTime = _implicitAnchor.Value.LocalNs + RtpDeltaNanos(rtp, _implicitAnchor.Value.Rtp);
        return true;
    }

    private long PlayTimeWith(Anchor anchor, uint rtp, long now) =>
        MapSender(anchor.SenderNs, now) + RtpDeltaNanos(rtp, anchor.Rtp);

    /// <summary>
    /// Sender time to local time: the slave's estimate, else the last PTP offset once PTP was used, else the anchor-only
    /// offset of R6. The first PTP estimate after anchor-only timing moves the timeline, so it is a re-anchor: the
    /// anchor-only offset is dropped and <c>D</c> is learnt again.
    /// </summary>
    private long MapSender(long senderNs, long now)
    {
        if (TryMapPtp(senderNs, out var local))
        {
            if (_anchorOnlyInUse)
            {
                _logger.LogDebug("PTP estimate available; leaving anchor-only timing and learning D again (R6)");
                _anchorOnlyInUse = false;
                _anchorOnlyOffset = null;
                _offsetD = null;
                ResetDrift();
            }

            return local;
        }

        if (_lastPtpOffset is { } lastOffset)
        {
            return senderNs + lastOffset;
        }

        _anchorOnlyInUse = true;
        _anchorOnlyOffset ??= now + Options.AnchorLead.Ticks * 100 - senderNs;
        return senderNs + _anchorOnlyOffset.Value;
    }

    private bool TryMapPtp(long senderNs, out long localNs)
    {
        if (Slave is not null && Slave.TryMapToLocal(unchecked((ulong)senderNs), out var mapped))
        {
            localNs = unchecked((long)mapped);
            _lastPtpOffset = localNs - senderNs;
            return true;
        }

        localNs = 0;
        return false;
    }

    private long Extend(uint rtp)
    {
        if (!_hasReference)
        {
            _hasReference = true;
            _referenceRtp = rtp;
            _referenceExt = 0;
            return 0;
        }

        var ext = _referenceExt + unchecked((int)(rtp - _referenceRtp));
        if (ext > _referenceExt)
        {
            _referenceRtp = rtp;
            _referenceExt = ext;
        }

        return ext;
    }

    private void RebaseReference()
    {
        var last = _queue.Values[^1];
        _hasReference = true;
        _referenceRtp = last.Rtp;
        _referenceExt = last.Ext;
    }

    /// <summary>Wakes the release thread; a call racing <see cref="DisposeAsync"/> has nothing left to wake.</summary>
    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the state change and the signal: the thread has stopped.
        }
    }

    private long NowNs() => unchecked((long)Clock.NowNanoseconds);

    private static long RtpDeltaNanos(uint rtp, uint anchorRtp) =>
        FramesToNanos(unchecked((int)(rtp - anchorRtp)));

    private static long FramesToNanos(long frames) => frames * NanosPerSecond / PlayoutOptions.SampleRate;

    /// <summary>23-bit modular "a comes before b" (SPEC-receiver.md 4.7).</summary>
    private static bool IsBufferedBefore(uint a, uint b)
    {
        var distance = (b - a) & BufferedSequenceMask;
        return distance != 0 && distance < BufferedSequenceHalf;
    }

    /// <summary>23-bit modular <c>from &lt;= seq &lt; until</c>.</summary>
    private static bool InBufferedRange(uint sequence, uint from, uint until) =>
        ((sequence - from) & BufferedSequenceMask) < ((until - from) & BufferedSequenceMask);

    private void ReleaseLoop()
    {
        using var sleeper = new PrecisionSleeper();
        var masterClock = new MasterClockAdapter(Clock);
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                long? deadline;
                try
                {
                    deadline = PumpAsync(token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Playout output call failed");
                    _wake.WaitOne(TimeSpan.FromMilliseconds(20));
                    continue;
                }

                if (deadline is not { } at)
                {
                    _wake.WaitOne(IdleWait);
                    continue;
                }

                var remaining = at - NowNs();
                if (remaining > PrecisionWindowNanos)
                {
                    _wake.WaitOne(TimeSpan.FromTicks((remaining - (PrecisionWindowNanos / 2)) / 100));
                }
                else if (remaining > 0)
                {
                    sleeper.SleepUntil(masterClock, at);
                }
            }
        }
        finally
        {
            _threadDone?.TrySetResult();
        }
    }

    private enum OutputCommand
    {
        Pause,
        Resume,
        Flush,
    }

    private sealed record Anchor(uint Rtp, long SenderNs);

    private readonly record struct Release(short[] Samples, bool ResumeFirst, bool PauseOnly);

    private sealed class Entry(uint rtp, long ext, uint sequence, short[] samples)
    {
        public uint Rtp { get; } = rtp;

        public long Ext { get; } = ext;

        public uint Sequence { get; } = sequence;

        public short[] Samples { get; } = samples;

        public int Frames => Samples.Length / 2;
    }

    /// <summary>The <see cref="IMasterClock"/> view of the buffer's clock that <see cref="PrecisionSleeper"/> sleeps against.</summary>
    private sealed class MasterClockAdapter(IMonotonicClock clock) : IMasterClock
    {
        public long NowNanos => unchecked((long)clock.NowNanoseconds);

        public ulong NtpNow => throw new NotSupportedException();

        public ulong PtpNowNanos => clock.NowNanoseconds;
    }
}
