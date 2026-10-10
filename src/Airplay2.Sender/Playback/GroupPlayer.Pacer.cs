using Airplay2.Sender.Rtp;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Playback;

/// <content>
/// The pacer (SPEC-audio.md 8.2, 8.3, 8.5): a dedicated highest-priority thread that sleeps until each packet's deadline
/// on <see cref="Streams.IStreamTransport.Clock"/>, takes 352 frames from the ring (silence when it runs dry), encodes
/// them once and hands them to the transport with the deadline as the sync time. This is one of the two documented
/// blocking exceptions of docs/DEVELOPMENT.md, "Code conventions" (the other is <see cref="Write"/>).
/// </content>
public sealed partial class GroupPlayer
{
    private const long NanosPerSecond = 1_000_000_000L;
    private const long PacketNanos = RealtimeStreamConstants.FramesPerPacket * NanosPerSecond / RealtimeStreamConstants.SampleRate;

    // Pacer thread only: members whose initial sync and marker go out with the packet being sent.
    private readonly List<Member> _promoting = [];

    private void EnsurePacerThread()
    {
        lock (_sync)
        {
            if (_pacerFailed)
            {
                throw new InvalidOperationException("The pacer thread failed; dispose this player and create a new one.");
            }

            if (_pacerThread is not null)
            {
                return;
            }

            _pacerThread = new Thread(PacerMain)
            {
                IsBackground = true,
                Name = "AirPlay pacer",
                Priority = ThreadPriority.Highest,
            };
            _pacerThread.Start();
        }
    }

    private void PacerMain()
    {
        try
        {
            RunPacer();
        }
        catch (Exception ex)
        {
            // A bug, not a network condition (send errors are absorbed by the transport). The thread is gone for good: mark
            // the player faulted so Write discards instead of blocking forever (with members present it would otherwise
            // wait for a reader that never comes) and StartAsync throws instead of waiting for a pre-fill nobody runs.
            LogPacerFailed(ex);
            lock (_sync)
            {
                _pacerFailed = true;
                _pacer = PacerState.Stopped;
                _started = false;
                Timeline.ClearAnchor();
                _ring.Clear();
                Monitor.PulseAll(_sync);
            }
        }
        finally
        {
            TaskCompletionSource? stop;
            lock (_sync)
            {
                stop = _stopRequest;
                _stopRequest = null;
            }

            stop?.TrySetResult();
            _pacerExited.TrySetResult();
        }
    }

    private void RunPacer()
    {
        using var sleeper = new PrecisionSleeper();
        LogPacerStarted(sleeper.Mechanism, sleeper.SpinWindow.TotalMilliseconds);
        var pcm = new short[RealtimeStreamConstants.FramesPerPacket * 2];
        var frame = new byte[Math.Max(Encoder.MaxOutputBytes, RealtimeStreamConstants.MaxAlacFrame)];
        var stallNanos = Options.StallThreshold.Ticks * 100;
        var silenceLimitFrames = (long)(Options.SilenceTimeout.TotalSeconds * RealtimeStreamConstants.SampleRate);
        var silentFrames = 0L;

        while (true)
        {
            long deadline;
            lock (_sync)
            {
                if (!WaitUntilRunning())
                {
                    return;
                }

                deadline = Timeline.NextDeadline;
            }

            sleeper.SleepUntil(Clock, deadline);
            var woke = Clock.NowNanos;
            ushort sequence;
            uint timestamp;
            int read;
            lock (_sync)
            {
                if (_stopRequest is not null || _pacer != PacerState.Running || _disposed)
                {
                    continue; // stop at the packet boundary: nothing is sent for this deadline
                }

                var late = woke - deadline;
                if (late > stallNanos)
                {
                    // A stall the receivers cannot absorb: skip the missed timestamps, keep the anchor and the sequence
                    // numbers, no FLUSH. The (now_frame, T) relation stays valid, so the receivers play silence for the
                    // gap. A deliberate deviation from "flush + re-anchor" (SPEC-audio.md 3.4), see the 8.3 note.
                    var missed = late / PacketNanos;
                    Timeline.Skip(unchecked((uint)(missed * RealtimeStreamConstants.FramesPerPacket)));
                    deadline = Timeline.NextDeadline;
                    Interlocked.Increment(ref _stalls);
                    LogStall(late / 1_000_000.0, missed);
                }
                else if (late > PacketNanos)
                {
                    // Late by more than a packet: the loop sends the missed packets back to back (SPEC-audio.md 8.2).
                    Interlocked.Increment(ref _lateWakeups);
                }

                read = _ring.Read(pcm);
                (sequence, timestamp) = Timeline.Advance();
                if (_promotionsPending != 0)
                {
                    // Decided under _sync, where registrations and resyncs happen: whoever is pending now is CONNECTED in
                    // the transport and gets its 0x90 sync and marker with this very packet (SPEC-audio.md 3.1 step 4).
                    TakePromotions();
                }

                Monitor.PulseAll(_sync);
            }

            if (read < RealtimeStreamConstants.FramesPerPacket)
            {
                pcm.AsSpan(read * 2).Clear();
                Interlocked.Increment(ref _silencePackets);
            }

            var length = Encoder.Encode(pcm, frame);
            Transport.Send(sequence, timestamp, frame.AsSpan(0, length), _mapping.NtpAt(deadline));
            Interlocked.Increment(ref _packetsSent);
            PacketObserver?.Invoke(deadline, Clock.NowNanos);

            if (_promoting.Count != 0)
            {
                PromoteMembers();
            }

            silentFrames = read == 0 ? silentFrames + RealtimeStreamConstants.FramesPerPacket : 0;
            if (silenceLimitFrames > 0 && silentFrames >= silenceLimitFrames)
            {
                silentFrames = 0;
                lock (_sync)
                {
                    if (_pacer == PacerState.Running && _stopRequest is null && !_disposed)
                    {
                        _pacer = PacerState.Stopped;
                        Timeline.ClearAnchor();
                        _autoPaused = true;
                        _commands.Writer.TryWrite(PlayerCommand.SilenceTimeout);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Under <see cref="_sync"/>: handles stop requests and waits until the pacer may send; anchors the timeline when the
    /// pre-fill is done (SPEC-audio.md 8.2: 200 ms buffered or 100 ms elapsed). False when the player is disposed.
    /// </summary>
    private bool WaitUntilRunning()
    {
        var prefillTimeout = Options.PrefillTimeout.Ticks * 100;
        while (true)
        {
            if (_disposed)
            {
                return false;
            }

            if (_stopRequest is { } stop)
            {
                _pacer = PacerState.Stopped;
                Timeline.ClearAnchor();
                _stopRequest = null;
                stop.TrySetResult();
                continue;
            }

            switch (_pacer)
            {
                case PacerState.Running:
                    return true;

                case PacerState.Prefill:
                    if (_waitForAudio && _ring.Count == 0)
                    {
                        // After a silence timeout: nothing happens until the producer writes again.
                        Monitor.Wait(_sync);
                        continue;
                    }

                    var now = Clock.NowNanos;
                    if (!_prefilling)
                    {
                        _prefilling = true;
                        _prefillSince = now;
                    }

                    var waited = now - _prefillSince;
                    if (_ring.Count >= Options.PrefillFrames || waited >= prefillTimeout)
                    {
                        Timeline.Anchor(now);
                        _pacer = PacerState.Running;
                        _hasPlayed = true;
                        _prefilling = false;
                        LogAnchored(Timeline.NextSequence, Timeline.NextTimestamp, _ring.Count, waited / 1_000_000.0);
                        return true;
                    }

                    Monitor.Wait(_sync, TimeSpan.FromTicks(Math.Max(1, (prefillTimeout - waited) / 100)));
                    continue;

                default:
                    Monitor.Wait(_sync);
                    continue;
            }
        }
    }

    /// <summary>Under <see cref="_sync"/>, pacer thread: collects the members whose initial sync and marker go out with the next send.</summary>
    private void TakePromotions()
    {
        _promotionsPending = 0;
        foreach (var member in _members.Values)
        {
            if (member.PendingStream && member.InTransport && member.State != GroupMemberState.Lost)
            {
                member.PendingStream = false;
                _promoting.Add(member);
            }
        }
    }

    /// <summary>Pacer thread, after the send: the members collected by <see cref="TakePromotions"/> are STREAMING now.</summary>
    private void PromoteMembers()
    {
        foreach (var member in _promoting)
        {
            if (member.State != GroupMemberState.Lost)
            {
                SetState(member, GroupMemberState.Streaming, null);
            }
        }

        _promoting.Clear();
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Debug, Message = "Pacer thread started ({Mechanism}, spin window {Spin} ms)")]
    private partial void LogPacerStarted(string mechanism, double spin);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Timeline anchored: next packet seq {Sequence} rtptime {Timestamp} due now ({Queued} frames queued after {Waited} ms pre-fill)")]
    private partial void LogAnchored(ushort sequence, uint timestamp, int queued, double waited);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Pacer stalled for {Milliseconds} ms: skipping {Packets} packets' worth of timestamps")]
    private partial void LogStall(double milliseconds, long packets);

    [LoggerMessage(EventId = 23, Level = LogLevel.Critical, Message = "The pacer thread failed; playback stopped")]
    private partial void LogPacerFailed(Exception exception);
}
