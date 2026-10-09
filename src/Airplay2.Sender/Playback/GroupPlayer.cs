using System.Threading.Channels;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Playback;

/// <summary>
/// The shared RTP timeline of a group (SPEC-audio.md 3.1, 8): one sequence counter, one timestamp counter, one ALAC
/// encoder, one pacer. Timestamps only ever advance; a flush announces the next values and re-anchors.
/// </summary>
public sealed class Timeline
{
    /// <summary>Creates a timeline starting at random sequence/timestamp values.</summary>
    public Timeline(IMasterClock clock, int latencyFrames = RealtimeStreamConstants.DefaultLatencyFrames)
    {
        Clock = clock;
        LatencyFrames = latencyFrames;
        Span<byte> r = stackalloc byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(r);
        NextSequence = BitConverter.ToUInt16(r);
        NextTimestamp = BitConverter.ToUInt32(r[2..]);
    }

    /// <summary>The clock that paces this timeline.</summary>
    public IMasterClock Clock { get; }

    /// <summary>L in frames.</summary>
    public int LatencyFrames { get; }

    /// <summary>Sequence number of the next packet.</summary>
    public ushort NextSequence { get; private set; }

    /// <summary>RTP timestamp of the next packet.</summary>
    public uint NextTimestamp { get; private set; }

    /// <summary>The frame that should be at the loudspeakers now: <c>NextTimestamp - L</c>.</summary>
    public uint NowFrame => unchecked(NextTimestamp - (uint)LatencyFrames);

    /// <summary>Consumes one packet's worth of sequence/timestamp.</summary>
    public (ushort Sequence, uint Timestamp) Advance()
    {
        var r = (NextSequence, NextTimestamp);
        NextSequence = unchecked((ushort)(NextSequence + 1));
        NextTimestamp = unchecked(NextTimestamp + RealtimeStreamConstants.FramesPerPacket);
        return r;
    }

    /// <summary>Jumps the timestamp forward by <paramref name="frames"/> (never backwards) after a seek/pause so stale audio is discarded.</summary>
    public void Skip(uint frames) => NextTimestamp = unchecked(NextTimestamp + frames);
}

/// <summary>State of one receiver inside a group.</summary>
public enum GroupMemberState
{
    /// <summary>Control plane set up; waits for its initial sync and marker packet.</summary>
    Connected,

    /// <summary>Receiving audio.</summary>
    Streaming,

    /// <summary>Flushed; will be re-anchored on the next packet.</summary>
    Flushed,

    /// <summary>Lost (feedback failed / socket closed); the group keeps playing without it.</summary>
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
}

/// <summary>
/// Plays one PCM source to N receivers in sync (PLAN.md milestones Sender M1-M3). Owns the timeline, the encoder, the
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
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Pauses: flushes every receiver with the next seq/timestamp, stops the pacer, drops the ring, keeps sessions alive.</summary>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>Discards queued audio (seek/skip): flush, re-anchor on the next write.</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Sets the group master level G (SPEC.md volume model).</summary>
    Task SetMasterLevelAsync(int level, CancellationToken cancellationToken);

    /// <summary>Sets one receiver's level r_i.</summary>
    Task SetReceiverLevelAsync(string receiverId, int level, CancellationToken cancellationToken);

    /// <summary>Sends text, artwork and progress for a track starting at the next timestamp.</summary>
    Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken);
}

/// <summary>Implementation of <see cref="IGroupPlayer"/> for the realtime stream.</summary>
public sealed class GroupPlayer : IGroupPlayer
{
    private readonly Channel<GroupPlayerEvent> _events = Channel.CreateBounded<GroupPlayerEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Creates a player.</summary>
    public GroupPlayer(IStreamTransport transport, Func<DiscoveredReceiver, CancellationToken, Task<IReceiverSession>> sessionFactory, GroupPlayerOptions options, ILogger<GroupPlayer> logger)
    {
        Transport = transport;
        SessionFactory = sessionFactory;
        Options = options;
        Logger = logger;
        Timeline = new Timeline(transport.Clock, options.LatencyFrames); // one clock per process (SPEC.md D3)
        Encoder = new AlacEncoder(RealtimeStreamConstants.FramesPerPacket, RealtimeStreamConstants.SampleRate);
    }

    /// <summary>The transport.</summary>
    public IStreamTransport Transport { get; }

    /// <summary>The timeline.</summary>
    public Timeline Timeline { get; }

    /// <summary>The shared encoder.</summary>
    public AlacEncoder Encoder { get; }

    /// <summary>The options.</summary>
    public GroupPlayerOptions Options { get; }

    private Func<DiscoveredReceiver, CancellationToken, Task<IReceiverSession>> SessionFactory { get; }

    private ILogger Logger { get; }

    /// <inheritdoc />
    public ChannelReader<GroupPlayerEvent> Events => _events.Reader;

    /// <inheritdoc />
    public GroupVolumeModel Volume { get; } = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, GroupMemberState> Members { get; } = new Dictionary<string, GroupMemberState>();

    /// <inheritdoc />
    public Task AddReceiverAsync(DiscoveredReceiver receiver, int level, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public Task RemoveReceiverAsync(string receiverId, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public void Write(ReadOnlySpan<short> interleavedPcm) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public Task SetReceiverLevelAsync(string receiverId, int level, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
