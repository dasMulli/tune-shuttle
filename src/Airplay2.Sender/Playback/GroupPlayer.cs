using System.Collections.Concurrent;
using System.Threading.Channels;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Playback;

/// <summary>State of one receiver inside a group.</summary>
public enum GroupMemberState
{
    /// <summary>Control plane set up; waits for its initial sync and marker packet.</summary>
    Connected,

    /// <summary>Receiving audio.</summary>
    Streaming,

    /// <summary>Flushed; will be re-anchored on the next packet.</summary>
    Flushed,

    /// <summary>Lost (feedback failed / socket closed) or removed; the group keeps playing without it.</summary>
    Lost,
}

/// <summary>Events the group player raises for the bridge and the CLI.</summary>
public abstract record GroupPlayerEvent
{
    /// <summary>A member changed state.</summary>
    public sealed record MemberStateChanged(string ReceiverId, GroupMemberState State, string? Reason) : GroupPlayerEvent;

    /// <summary>A receiver reported a level change on its own (SPEC.md open question C-7.4 / A-Q5).</summary>
    public sealed record ReceiverVolumeReported(string ReceiverId, int Level) : GroupPlayerEvent;

    /// <summary>The receiver's buttons: play, paus, nitm, pitm.</summary>
    public sealed record RemoteCommand(string ReceiverId, string Command) : GroupPlayerEvent;

    /// <summary>Pacer statistics (underruns, late wakeups, retransmits) every few seconds.</summary>
    public sealed record Statistics(int Members, long PacketsSent, long SilencePackets, long LateWakeups, long RetransmitRequests) : GroupPlayerEvent;
}

/// <summary>Options of a group player.</summary>
public sealed record GroupPlayerOptions
{
    /// <summary>Sender latency L in frames.</summary>
    public int LatencyFrames { get; init; } = RealtimeStreamConstants.DefaultLatencyFrames;

    /// <summary>PCM ring high-water mark in frames; <see cref="IGroupPlayer.Write"/> blocks above it.</summary>
    public int RingHighWaterFrames { get; init; } = 44100;

    /// <summary>Pre-fill before the pacer starts, in frames.</summary>
    public int PrefillFrames { get; init; } = 8820;

    /// <summary>The pacer starts after this long even when <see cref="PrefillFrames"/> are not buffered (SPEC-audio.md 8.2: 100 ms).</summary>
    public TimeSpan PrefillTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Silence sent for longer than this is treated as a pause (flush + stop pacer).</summary>
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Feedback interval.</summary>
    public TimeSpan FeedbackInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Progress re-send interval (Apple TV / HomePod keep-alive).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Poll <c>GET_PARAMETER volume</c> at this interval while playing (fallback for device-side changes); null disables.</summary>
    public TimeSpan? VolumePollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Stable group UUID sent in every member's session SETUP.</summary>
    public string GroupUuid { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>
    /// Who we say we are in each member's session SETUP (SPEC-control.md 4.2). The default is a placeholder for tests;
    /// hosts (CLI, bridge) supply a stable identity per installation, because every player in a process would otherwise
    /// announce the same <c>deviceID</c>/<c>macAddress</c>.
    /// </summary>
    public SenderIdentity Sender { get; init; } = new("airplay2-streamer", "AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF");

    /// <summary><c>groupContainsGroupLeader</c> in the session SETUP (SPEC.md D3: false; probe P12 toggles it).</summary>
    public bool GroupContainsGroupLeader { get; init; }

    /// <summary><c>isMultiSelectAirPlay</c> in the session SETUP (pyatv sends true; probe P12 toggles it).</summary>
    public bool IsMultiSelectAirPlay { get; init; } = true;

    /// <summary>Use 32 random bytes as <c>shk</c> instead of the shared secret (SPEC.md D4 test option, probe P15 / A-Q4).</summary>
    public bool RandomStreamKey { get; init; }

    /// <summary>Minimum spacing of volume requests to one receiver (SPEC.md D10: coalesced to at most 10 per second).</summary>
    public TimeSpan VolumeMinInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How often a <see cref="GroupPlayerEvent.Statistics"/> event is published.</summary>
    public TimeSpan StatisticsInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound for the TEARDOWN of a member that is removed (D7), so a dead receiver cannot hang removal.</summary>
    public TimeSpan TeardownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A pacer that wakes later than this (process stalled) skips the timestamps it missed instead of sending a burst the
    /// receivers would only drop as late (SPEC-audio.md 1.5: <c>[sps]</c> drops packets whose play time has passed). The
    /// sequence numbers and the anchor stay; no FLUSH is sent (a deliberate deviation, SPEC-audio.md 8.3 note).
    /// </summary>
    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Plays one PCM source to N receivers in sync. Owns the timeline, the encoder, the
/// pacer thread, the transport and one <see cref="IReceiverSession"/> per member; applies the
/// <see cref="GroupVolumeModel"/>.
/// </summary>
public interface IGroupPlayer : IAsyncDisposable
{
    /// <summary>Events.</summary>
    ChannelReader<GroupPlayerEvent> Events { get; }

    /// <summary>The volume model.</summary>
    GroupVolumeModel Volume { get; }

    /// <summary>Members by receiver id.</summary>
    IReadOnlyDictionary<string, GroupMemberState> Members { get; }

    /// <summary>Connects, pairs, sets up and starts streaming to a receiver (may join mid-track, no catch-up).</summary>
    Task AddReceiverAsync(DiscoveredReceiver receiver, int level, CancellationToken cancellationToken);

    /// <summary>Tears a receiver down and removes it.</summary>
    Task RemoveReceiverAsync(string receiverId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues interleaved 16-bit stereo PCM. Blocks while the ring is above the high-water mark so the producer
    /// (librespot's player thread) is paced by the master clock. Audio is discarded when the group has no members.
    /// </summary>
    void Write(ReadOnlySpan<short> interleavedPcm);

    /// <summary>Starts (or resumes) the pacer; sends initial syncs and marker packets.</summary>
    /// <exception cref="InvalidOperationException">The pacer thread failed earlier; dispose the player and build a new one.</exception>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Pauses: flushes every receiver with the next seq/timestamp, stops the pacer, drops the ring, keeps sessions alive.</summary>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Discards queued audio (seek/skip): flush, re-anchor on the next write. The player cannot know the new track
    /// position: after a seek the caller sends <see cref="SetNowPlayingAsync"/> again with the new elapsed time
    /// (SPEC-control.md 5.4: "on seek/resume re-send progress"); until then the progress keep-alive describes the old
    /// position.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Sets the group master level G (SPEC.md volume model).</summary>
    Task SetMasterLevelAsync(int level, CancellationToken cancellationToken);

    /// <summary>Sets one receiver's level r_i.</summary>
    Task SetReceiverLevelAsync(string receiverId, int level, CancellationToken cancellationToken);

    /// <summary>Sends text, artwork and progress for a track starting at the next timestamp.</summary>
    Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken);

    /// <summary>
    /// Frames queued in the ring ahead of the pacer (SPEC-receiver.md 5.4, 8.6): the AirPlay receiver's release thread keeps
    /// the ring shallow by never writing more than <c>PrefillFrames + 352</c> frames ahead of this count.
    /// </summary>
    int QueuedFrames { get; }
}

/// <summary>
/// Implementation of <see cref="IGroupPlayer"/> for the realtime stream (SPEC-audio.md 3, 7, 8; SPEC.md D3, D5, D10-D12).
/// </summary>
/// <remarks>
/// <para>
/// Pipeline (SPEC-audio.md 8.2): <see cref="Write"/> (librespot's player thread) → PCM ring (blocks above
/// <see cref="GroupPlayerOptions.RingHighWaterFrames"/>) → pacer thread: every 352 frames of master-clock time take 352
/// frames (silence on underrun), ALAC-encode once, <see cref="IStreamTransport.Send"/> to every member with its own
/// encryption, initial/periodic syncs and retransmit history. The pacer's clock is <see cref="IStreamTransport.Clock"/>
/// (SPEC.md D3, SPEC-audio.md 8.5): the player never creates a clock of its own.
/// </para>
/// <para>
/// Start (8.2): after <see cref="StartAsync"/> the pacer waits until <see cref="GroupPlayerOptions.PrefillFrames"/> are
/// buffered or <see cref="GroupPlayerOptions.PrefillTimeout"/> has passed, then anchors the timeline at the current
/// instant. Packet k is due at <c>T0 + 352 k / 44100</c>; a late wakeup sends the missed packets at once (the timestamps
/// matter, not the send instants), a stall beyond <see cref="GroupPlayerOptions.StallThreshold"/> skips the missed
/// timestamps. Silence (empty ring) is sent for up to <see cref="GroupPlayerOptions.SilenceTimeout"/>, then the group
/// is flushed and the pacer waits for audio (8.3).
/// </para>
/// <para>
/// Pause, seek, skip (8.4; SPEC-control.md 4.8): the pacer stops at a packet boundary, every member gets <c>FLUSH</c>
/// with the next <c>seq</c>/<c>rtptime</c> and goes back to CONNECTED (transport <see cref="IStreamTransport.Resync"/>),
/// the volume is re-sent, and on resume the timeline is re-anchored with timestamps still monotonic, so each member gets
/// a <c>0x90</c> sync and a marker packet again.
/// </para>
/// <para>
/// Members (D5): <see cref="AddReceiverAsync"/> takes a paired session from the factory (GET /info and pairing are the
/// factory's, as <c>AirplaySender.ConnectAsync</c> does them) and runs session SETUP → event channel → RECORD →
/// (SETPEERS, PTP) → stream SETUP → volume → transport registration (initial sync + marker with the next packet) →
/// metadata of the current track; then feedback every 2 s, progress every 25 s, the volume poll and the event channel run
/// per member until it is removed or lost.
/// </para>
/// </remarks>
public sealed partial class GroupPlayer : IGroupPlayer
{
    private readonly Channel<GroupPlayerEvent> _events = Channel.CreateBounded<GroupPlayerEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<PlayerCommand> _commands = Channel.CreateUnbounded<PlayerCommand>(new UnboundedChannelOptions { SingleReader = true });

    // Guards the ring, the pacer state and the timeline (Monitor: Write and the pacer wait on it).
    private readonly object _sync = new();
    private readonly PcmRing _ring;
    private readonly NowPlayingTracker _nowPlaying = new();
    private readonly ConcurrentDictionary<string, Member> _members = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GroupMemberState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, string> _background = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _volumeLock = new();
    private readonly CancellationTokenSource _disposing = new();
    private readonly TaskCompletionSource _pacerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ClockMapping _mapping;
    private readonly ILogger _logger;
    private readonly Task _worker;
    private readonly Task _statistics;

    private Thread? _pacerThread;
    private PacerState _pacer = PacerState.Stopped;
    private TaskCompletionSource? _stopRequest;
    private bool _started;
    private bool _hasPlayed;
    private bool _autoPaused;
    private bool _waitForAudio;
    private bool _prefilling;
    private long _prefillSince;
    private bool _disposed;
    private bool _pacerFailed;
    private int _promotionsPending;
    private long _packetsSent;
    private long _silencePackets;
    private long _lateWakeups;
    private long _stalls;
    private long _framesDiscarded;

    /// <summary>Creates a player.</summary>
    /// <param name="transport">The stream transport; the player owns it from now on and disposes it.</param>
    /// <param name="sessionFactory">Opens a control connection to a receiver and runs GET /info and pairing (no SETUP yet).</param>
    /// <param name="options">Options.</param>
    /// <param name="logger">Logger.</param>
    public GroupPlayer(IStreamTransport transport, Func<DiscoveredReceiver, CancellationToken, Task<IReceiverSession>> sessionFactory, GroupPlayerOptions options, ILogger<GroupPlayer> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RingHighWaterFrames, RealtimeStreamConstants.FramesPerPacket, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.PrefillFrames, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.FeedbackInterval, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ProgressInterval, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StatisticsInterval, TimeSpan.Zero, nameof(options));
        if (options.VolumePollInterval is { } poll)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(poll, TimeSpan.Zero, nameof(options));
        }

        Transport = transport;
        SessionFactory = sessionFactory;
        _logger = logger;

        // The syncs carry the transport's L; the player's view of "frame at the speaker" must use the same value.
        var latency = transport is IStreamLatency streamLatency ? streamLatency.LatencyFrames : options.LatencyFrames;
        if (latency != options.LatencyFrames)
        {
            LogLatencyMismatch(options.LatencyFrames, latency);
            options = options with { LatencyFrames = latency };
        }

        Options = options;
        Timeline = new Timeline(transport.Clock, options.LatencyFrames); // one clock per process (SPEC.md D3)
        Encoder = new AlacEncoder(RealtimeStreamConstants.FramesPerPacket, RealtimeStreamConstants.SampleRate);
        _ring = new PcmRing(options.RingHighWaterFrames);
        _mapping = new ClockMapping(transport.Clock);
        _worker = RunWorkerAsync();
        _statistics = RunStatisticsAsync();
    }

    private enum PacerState
    {
        Stopped,
        Prefill,
        Running,
    }

    private enum PlayerCommand
    {
        SilenceTimeout,
    }

    /// <summary>The transport.</summary>
    public IStreamTransport Transport { get; }

    /// <summary>The timeline.</summary>
    public Timeline Timeline { get; }

    /// <summary>The shared encoder (used by the pacer thread only).</summary>
    public AlacEncoder Encoder { get; }

    /// <summary>The options.</summary>
    public GroupPlayerOptions Options { get; }

    /// <inheritdoc />
    public ChannelReader<GroupPlayerEvent> Events => _events.Reader;

    /// <inheritdoc />
    /// <remarks>
    /// The player mutates it under its own lock; <see cref="GroupVolumeModel.Levels"/> is an immutable snapshot, safe to
    /// read and iterate from any thread.
    /// </remarks>
    public GroupVolumeModel Volume { get; } = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, GroupMemberState> Members => _states;

    /// <summary>True between <see cref="StartAsync"/> and <see cref="PauseAsync"/>.</summary>
    public bool IsStarted
    {
        get
        {
            lock (_sync)
            {
                return _started;
            }
        }
    }

    /// <summary>
    /// True after the pacer thread failed (a bug, not a network condition): <see cref="Write"/> discards audio and
    /// <see cref="StartAsync"/> throws; the host disposes the player and builds a new one.
    /// </summary>
    public bool IsFaulted
    {
        get
        {
            lock (_sync)
            {
                return _pacerFailed;
            }
        }
    }

    /// <inheritdoc />
    public int QueuedFrames
    {
        get
        {
            lock (_sync)
            {
                return _ring.Count;
            }
        }
    }

    /// <summary>Packets the pacer produced (each sent to every member).</summary>
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    /// <summary>Packets that carried silence because the ring ran dry (fully or partly).</summary>
    public long SilencePackets => Interlocked.Read(ref _silencePackets);

    /// <summary>Pacer wakeups later than one packet duration.</summary>
    public long LateWakeups => Interlocked.Read(ref _lateWakeups);

    /// <summary>Stalls beyond <see cref="GroupPlayerOptions.StallThreshold"/> whose timestamps were skipped.</summary>
    public long Stalls => Interlocked.Read(ref _stalls);

    /// <summary>Frames discarded because nothing was playing to (no members and not started) or the pacer failed.</summary>
    public long FramesDiscarded => Interlocked.Read(ref _framesDiscarded);

    /// <summary>
    /// Test hook: called on the pacer thread after every packet with its deadline (<see cref="IMasterClock.NowNanos"/>)
    /// and the instant it was handed to the transport. Keep it cheap.
    /// </summary>
    internal Action<long, long>? PacketObserver { get; set; }

    private Func<DiscoveredReceiver, CancellationToken, Task<IReceiverSession>> SessionFactory { get; }

    private IMasterClock Clock => Transport.Clock;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">An odd number of samples (not whole stereo frames).</exception>
    public void Write(ReadOnlySpan<short> interleavedPcm)
    {
        if ((interleavedPcm.Length & 1) != 0)
        {
            throw new ArgumentException("Interleaved stereo PCM has an even number of samples.", nameof(interleavedPcm));
        }

        lock (_sync)
        {
            while (!interleavedPcm.IsEmpty)
            {
                if (_disposed || _pacerFailed || (!_started && _members.IsEmpty))
                {
                    // docs/ARCHITECTURE.md, "Design decisions": audio is silently discarded while no receiver is connected. Also after a pacer failure:
                    // nobody would ever drain the ring, and blocking would freeze librespot's player thread.
                    _framesDiscarded += interleavedPcm.Length / 2;
                    return;
                }

                if (_ring.Count >= Options.RingHighWaterFrames)
                {
                    // Backpressure: the only clock librespot sees (SPEC-audio.md 8.1, 8.2). The pacer pulses after each read.
                    Monitor.Wait(_sync);
                    continue;
                }

                // Never above the high-water mark: the ring holds at most that much (SPEC-audio.md 8.2: >= 1 s, blocking above it).
                var room = Options.RingHighWaterFrames - _ring.Count;
                var frames = _ring.Write(interleavedPcm[..(Math.Min(room, interleavedPcm.Length / 2) * 2)]);
                interleavedPcm = interleavedPcm[(frames * 2)..];
                Monitor.PulseAll(_sync);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent. Starts the transport and the pacer thread; the pacer pre-fills and then anchors the timeline, and every
    /// member in CONNECTED/FLUSHED gets its initial sync and marker packet with the first packet. On resume every active
    /// member is put back to CONNECTED in the transport whatever happened to its FLUSH (cancelled, failed), so each one
    /// gets a <c>0x90</c> sync and a marker packet with the new anchor (SPEC-audio.md 3.4, 8.4). After a pause the
    /// progress item is re-sent (SPEC-control.md 5.4: "on seek/resume re-send progress").
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfPacerFailed();
            await Transport.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsurePacerThread();
            bool resumed;
            lock (_sync)
            {
                if (_started)
                {
                    return;
                }

                ThrowIfPacerFailed();
                _started = true;
                _autoPaused = false;
                resumed = _hasPlayed;
                if (resumed)
                {
                    ResyncActiveMembers();
                }

                BeginPrefill(waitForAudio: false);
            }

            LogStarting(resumed ? "resume" : "start", _members.Count);
            if (resumed)
            {
                Track(ResendProgressAsync(_disposing.Token), "progress after resume");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-audio.md 8.4 (<c>Paused</c>): the pacer stops at the next packet boundary; every member gets <c>FLUSH</c>
    /// with the next <c>seq</c>/<c>rtptime</c> so the audio already sent is discarded, goes back to CONNECTED, and its volume
    /// is re-sent; the ring is dropped; sessions, event channels and feedback stay alive. A blocked <see cref="Write"/>
    /// resumes and refills the ring up to the high-water mark, then blocks until <see cref="StartAsync"/>.
    /// <paramref name="cancellationToken"/> bounds only the wait for other player operations: once the pacer has stopped,
    /// the FLUSH fan-out runs to completion (each request is bounded by the RTSP request timeout) so a cancelled caller
    /// cannot leave the group half-flushed.
    /// </remarks>
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopPacerAsync().ConfigureAwait(false);
            ushort sequence;
            uint timestamp;
            int dropped;
            lock (_sync)
            {
                _started = false;
                _autoPaused = false;
                dropped = _ring.Clear();
                _nowPlaying.Shift(-dropped);
                sequence = Timeline.NextSequence;
                timestamp = Timeline.NextTimestamp;
                Monitor.PulseAll(_sync);
            }

            LogFlushing("pause", sequence, timestamp, dropped);
            await FlushMembersAsync(sequence, timestamp, _disposing.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-audio.md 8.4 (<c>Seeked</c>, manual skip): the frames queued <b>before this call</b> are dropped (frames written
    /// after it belong to the new position and stay), the pacer stops at a packet boundary, every member gets
    /// <c>FLUSH</c> with the next values and goes back to CONNECTED, and a started player re-anchors after the pre-fill
    /// (every active member gets <c>0x90</c> + marker again, even one whose FLUSH failed). Cancellation as in
    /// <see cref="PauseAsync"/>. Progress is not re-sent here: after a seek the caller sends
    /// <see cref="SetNowPlayingAsync"/> with the new elapsed time.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        long mark;
        lock (_sync)
        {
            mark = _ring.WrittenFrames;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopPacerAsync().ConfigureAwait(false);
            ushort sequence;
            uint timestamp;
            int dropped;
            bool restart;
            lock (_sync)
            {
                _autoPaused = false;
                dropped = _ring.DropUntil(mark);
                _nowPlaying.Shift(-dropped);
                sequence = Timeline.NextSequence;
                timestamp = Timeline.NextTimestamp;
                restart = _started;
                Monitor.PulseAll(_sync);
            }

            LogFlushing("flush", sequence, timestamp, dropped);
            try
            {
                await FlushMembersAsync(sequence, timestamp, _disposing.Token).ConfigureAwait(false);
            }
            finally
            {
                if (restart)
                {
                    lock (_sync)
                    {
                        if (_started && !_disposed && _pacer == PacerState.Stopped)
                        {
                            ResyncActiveMembers();
                            BeginPrefill(waitForAudio: false);
                        }
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Scales every r_i (<see cref="GroupVolumeModel.SetMasterLevel"/>) and sends the changed levels, coalesced per
    /// receiver (SPEC.md D10). A member that fails to take its level is logged; the call does not fail for it.
    /// </remarks>
    public async Task SetMasterLevelAsync(int level, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<(string ReceiverId, int Level)> changed;
        lock (_volumeLock)
        {
            changed = Volume.SetMasterLevel(level);
        }

        LogMasterLevel(Volume.MasterLevel, changed.Count);
        var sends = new List<Task>(changed.Count);
        foreach (var (id, receiverLevel) in changed)
        {
            if (_members.TryGetValue(id, out var member) && member.State != GroupMemberState.Lost)
            {
                sends.Add(SendLevelBestEffortAsync(member, receiverLevel, force: false, cancellationToken));
            }
        }

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">No such member.</exception>
    public async Task SetReceiverLevelAsync(string receiverId, int level, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_members.TryGetValue(receiverId, out var member))
        {
            throw new KeyNotFoundException($"{receiverId} is not a member of this group.");
        }

        level = Math.Clamp(level, 0, 100);
        lock (_volumeLock)
        {
            Volume.SetReceiverLevel(receiverId, level);
        }

        if (member.State == GroupMemberState.Lost)
        {
            return;
        }

        await RequestVolumeAsync(member, level, force: false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-audio.md 7: the track's <c>start</c> is the timestamp the next written frame gets minus <paramref name="elapsed"/>
    /// (the track position of the samples the caller writes next); every member gets DMAP text, artwork and progress
    /// (each only when its TXT records ask for it), all with <c>RTP-Info: rtptime=start</c>, best effort. A member's first
    /// track uses the startup display lead (15 360), later ones the switch lead (30 720). The track is remembered for
    /// members that join later and for the 25 s progress keep-alive.
    /// </remarks>
    public async Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (artwork is { IsEmpty: false } && string.IsNullOrEmpty(artworkContentType))
        {
            throw new ArgumentException("Artwork needs a content type (image/jpeg or image/png).", nameof(artworkContentType));
        }

        NowPlayingTrack track;
        uint speaker;
        lock (_sync)
        {
            var writeTimestamp = unchecked(Timeline.NextTimestamp + (uint)_ring.Count);
            track = _nowPlaying.SetTrack(metadata, artwork is { IsEmpty: false } ? artwork : null, artworkContentType, writeTimestamp, elapsed);
            speaker = Timeline.FrameAtSpeaker(Clock.NowNanos);
        }

        LogNowPlaying(metadata.Title, metadata.Artist, track.Start, track.DurationFrames);
        var sends = new List<Task>();
        foreach (var member in _members.Values)
        {
            if (member.State != GroupMemberState.Lost && member.InTransport)
            {
                sends.Add(SendNowPlayingToAsync(member, track, speaker, cancellationToken));
            }
        }

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Stops the pacer, tears every member down (D7, bounded by <see cref="GroupPlayerOptions.TeardownTimeout"/>) and disposes the transport.</remarks>
    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource? stop;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stop = _stopRequest;
            _stopRequest = null;
            Monitor.PulseAll(_sync);
        }

        stop?.TrySetResult();
        await _disposing.CancelAsync().ConfigureAwait(false);
        if (_pacerThread is not null)
        {
            await _pacerExited.Task.ConfigureAwait(false);
        }

        _commands.Writer.TryComplete();
        await AwaitQuietlyAsync(_worker).ConfigureAwait(false);
        await AwaitQuietlyAsync(_statistics).ConfigureAwait(false);

        foreach (var member in _members.Values.ToArray())
        {
            Transport.RemoveReceiver(member.Id);
            await RemoveMemberCoreAsync(member, CancellationToken.None).ConfigureAwait(false);
        }

        await Transport.DisposeAsync().ConfigureAwait(false);
        await DrainBackgroundAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        LogDisposed(PacketsSent, SilencePackets, LateWakeups);
    }

    /// <summary>Sets the pre-fill state; call under <see cref="_sync"/>.</summary>
    private void BeginPrefill(bool waitForAudio)
    {
        _pacer = PacerState.Prefill;
        _waitForAudio = waitForAudio;
        _prefilling = false;
        Monitor.PulseAll(_sync);
    }

    /// <summary>Throws when the pacer thread failed (it is never restarted).</summary>
    private void ThrowIfPacerFailed()
    {
        lock (_sync)
        {
            if (_pacerFailed)
            {
                throw new InvalidOperationException("The pacer thread failed; dispose this player and create a new one.");
            }
        }
    }

    /// <summary>Stops the pacer at the next packet boundary; completes when it acknowledged (immediately when it was not running).</summary>
    private Task StopPacerAsync()
    {
        lock (_sync)
        {
            if (_pacer == PacerState.Stopped || _pacerThread is null)
            {
                _pacer = PacerState.Stopped;
                Timeline.ClearAnchor();
                return Task.CompletedTask;
            }

            _stopRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Monitor.PulseAll(_sync);
            return _stopRequest.Task;
        }
    }

    private async Task RunWorkerAsync()
    {
        await Task.Yield();
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_disposing.Token).ConfigureAwait(false))
            {
                switch (command)
                {
                    case PlayerCommand.SilenceTimeout:
                        await HandleSilenceTimeoutAsync().ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            // disposed
        }
    }

    /// <summary>SPEC-audio.md 8.3: silence for longer than the timeout is a pause: FLUSH, stop, re-anchor when audio returns.</summary>
    private async Task HandleSilenceTimeoutAsync()
    {
        try
        {
            await _gate.WaitAsync(_disposing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            ushort sequence;
            uint timestamp;
            lock (_sync)
            {
                if (!_autoPaused || _disposed)
                {
                    return;
                }

                sequence = Timeline.NextSequence;
                timestamp = Timeline.NextTimestamp;
            }

            LogSilenceTimeout(Options.SilenceTimeout.TotalSeconds, sequence, timestamp);
            try
            {
                await FlushMembersAsync(sequence, timestamp, _disposing.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (_autoPaused && _started && !_disposed && _pacer == PacerState.Stopped)
                    {
                        ResyncActiveMembers();
                        BeginPrefill(waitForAudio: true);
                    }

                    _autoPaused = false;
                }
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            // disposed while flushing
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunStatisticsAsync()
    {
        await Task.Yield();
        try
        {
            using var timer = new PeriodicTimer(Options.StatisticsInterval);
            while (await timer.WaitForNextTickAsync(_disposing.Token).ConfigureAwait(false))
            {
                var active = 0;
                foreach (var state in _states.Values)
                {
                    if (state != GroupMemberState.Lost)
                    {
                        active++;
                    }
                }

                var retransmits = Transport is RealtimeStreamTransport realtime ? realtime.Control.RetransmitRequestsReceived : 0;
                var statistics = new GroupPlayerEvent.Statistics(active, PacketsSent, SilencePackets, LateWakeups, retransmits);
                _events.Writer.TryWrite(statistics);
                if (IsStarted)
                {
                    LogStatistics(active, statistics.PacketsSent, statistics.SilencePackets, statistics.LateWakeups, retransmits, QueuedFrames);
                }
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            // disposed
        }
    }

    /// <summary>Tracks a background task: its failure is logged, and <see cref="DisposeAsync"/> awaits it.</summary>
    private void Track(Task task, string what)
    {
        _background[task] = what;
        _ = task.ContinueWith(
            static (t, state) =>
            {
                var (self, name) = ((GroupPlayer, string))state!;
                self._background.TryRemove(t, out _);
                if (t.IsFaulted && t.Exception?.GetBaseException() is not OperationCanceledException)
                {
                    self.LogBackgroundFailed(t.Exception!, name);
                }
            },
            (this, what),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainBackgroundAsync()
    {
        for (var round = 0; round < 4; round++)
        {
            var pending = _background.Keys.ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            await AwaitQuietlyAsync(Task.WhenAll(pending)).ConfigureAwait(false);
        }
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Failures were logged where they happened.
        }
    }

    private void Publish(GroupPlayerEvent e) => _events.Writer.TryWrite(e);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "GroupPlayerOptions.LatencyFrames {Options} differs from the transport's L {Transport}; using the transport's")]
    private partial void LogLatencyMismatch(int options, int transport);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Playback {What} with {Members} member(s)")]
    private partial void LogStarting(string what, int members);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Group {What}: FLUSH seq {Sequence} rtptime {Timestamp}, {Dropped} queued frames dropped")]
    private partial void LogFlushing(string what, ushort sequence, uint timestamp, int dropped);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Master level G = {Level}; {Changed} receiver level(s) changed")]
    private partial void LogMasterLevel(int level, int changed);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Now playing '{Title}' by '{Artist}': start rtptime {Start}, {Duration} frames")]
    private partial void LogNowPlaying(string? title, string? artist, uint start, uint? duration);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Silence for {Seconds} s: treating it as a pause (FLUSH seq {Sequence} rtptime {Timestamp}); playback re-anchors when audio returns")]
    private partial void LogSilenceTimeout(double seconds, ushort sequence, uint timestamp);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Statistics: {Members} member(s), {Packets} packets, {Silence} silence, {Late} late wakeups, {Retransmits} retransmit requests, {Queued} frames queued")]
    private partial void LogStatistics(int members, long packets, long silence, long late, long retransmits, int queued);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Background task '{What}' failed")]
    private partial void LogBackgroundFailed(Exception exception, string what);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Group player disposed: {Packets} packets, {Silence} silence packets, {Late} late wakeups")]
    private partial void LogDisposed(long packets, long silence, long late);
}
