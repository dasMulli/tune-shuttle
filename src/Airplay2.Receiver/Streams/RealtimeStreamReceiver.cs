using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Rtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Streams;

/// <summary>Options of a <see cref="RealtimeStreamReceiver"/> (SPEC-receiver.md 5.1).</summary>
public sealed record RealtimeStreamReceiverOptions
{
    /// <summary>Local address of the data and control sockets (loopback by default; the product receiver binds its LAN address).</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Label used in socket names and log messages.</summary>
    public string Name { get; init; } = "receiver";

    /// <summary>Send 0xD5 retransmit requests for sequence gaps (SPEC-receiver.md 5.1, SPEC-audio.md 2.4).</summary>
    public bool RequestRetransmits { get; init; } = true;

    /// <summary>
    /// How many 0xD5 requests one missing packet gets at most (SPEC-receiver.md 5.1: three). The first is sent as soon
    /// as the gap is detected (well within the 30 ms of 5.1), each further one <see cref="RetransmitInterval"/> after the
    /// previous; a packet still missing <see cref="RetransmitInterval"/> after the last request is given up and counted
    /// as lost. The fake receiver sets 1 (one request per gap, the behaviour its tests count on).
    /// </summary>
    public int MaxRetransmitRequests { get; init; } = 3;

    /// <summary>
    /// The wait between two requests for the same missing packet (SPEC-receiver.md 5.1). 50 ms puts all three requests
    /// inside the 200 ms <c>LateThreshold</c> of R6, which bounds how late a recovered packet may still play.
    /// </summary>
    public TimeSpan RetransmitInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Send the 16-byte retransmit request form with an NTP time (airplay2-receiver) instead of the 8-byte form
    /// (shairport-sync and the product receiver, SPEC-audio.md 2.4). The NTP field holds <see cref="Clock"/>'s time at
    /// sending; senders ignore it. A test switch of the fake receiver.
    /// </summary>
    public bool ExtendedRetransmitRequests { get; init; }

    /// <summary>
    /// Send retransmit requests from a second UDP socket instead of the announced control port, to exercise the sender's
    /// address-first lookup (SPEC-audio.md 2.4); wrapped 0xD6 replies are accepted on both sockets. A test switch of the fake receiver.
    /// </summary>
    public bool RetransmitFromSecondSocket { get; init; }

    /// <summary>
    /// Simulated loss (a test hook of the fake receiver): called for every fresh packet that is neither the first of the
    /// stream nor marked (a re-anchor), with the 1-based count of fresh packets so far and the packet's sequence; true
    /// drops the packet before it is tracked, so its gap is requested like a real loss.
    /// </summary>
    public Func<long, ushort, bool>? SimulateLoss { get; init; }

    /// <summary>The clock for the NTP field of the extended retransmit request form; a new <see cref="MasterClock"/> when null.</summary>
    public IMasterClock? Clock { get; init; }
}

/// <summary>
/// Counters of a <see cref="RealtimeStreamReceiver"/> since it was created (SPEC-receiver.md 5.1; the realtime part of
/// the Diagnostics statistics, 5.3, 9.1).
/// </summary>
/// <param name="PacketsReceived">Audio datagrams that reached the active stream (fresh, retransmitted and duplicate).</param>
/// <param name="PacketsDecoded">Packets decrypted, ALAC-decoded and handed on (to the playout buffer and the observer).</param>
/// <param name="RetransmitRequestsSent">0xD5 requests sent (a request may cover several packets).</param>
/// <param name="PacketsRequested">Packets asked for, counted once per request that names them.</param>
/// <param name="PacketsRecovered">Requested packets that arrived (wrapped 0xD6 on the control port or raw on the data port).</param>
/// <param name="PacketsLost">Missing packets given up after the last request (or not requested: beyond the history or requests off).</param>
/// <param name="DuplicatePackets">Packets behind the expected sequence that were not outstanding (duplicates, answers to a repeated request, packets already given up): dropped.</param>
/// <param name="EarlyPackets">Packets dropped for a corrupt timestamp: more than 2 s ahead of the next due frame (5.1).</param>
/// <param name="LatePackets">Packets dropped because their release time (R6) was more than <c>LateThreshold</c> in the past (5.1).</param>
/// <param name="BadPackets">Packets that failed authentication or ALAC decoding, or arrived without a usable <c>shk</c>.</param>
/// <param name="Syncs">0xD7 sync packets received.</param>
/// <param name="AnchorsApplied">0xD7 syncs that (re-)anchored the stream: its first sync, the first after FLUSH or a marker packet, or one flagged first (<c>0x90</c>). Every sync goes to <see cref="PlayoutBuffer.SetAnchor"/>; the periodic ones in between refresh the mapping and are not counted here.</param>
public sealed record RealtimeStreamStatistics(
    long PacketsReceived,
    long PacketsDecoded,
    long RetransmitRequestsSent,
    long PacketsRequested,
    long PacketsRecovered,
    long PacketsLost,
    long DuplicatePackets,
    long EarlyPackets,
    long LatePackets,
    long BadPackets,
    long Syncs,
    long AnchorsApplied);

/// <summary>
/// What a <see cref="RealtimeStreamReceiver"/> reports (SPEC-receiver.md 5.1). Every method has an empty default, so an
/// observer implements only what it uses. Methods are called on the socket loops; the packet methods are called under the
/// receiver's audio lock, in arrival order, so they must not block.
/// </summary>
public interface IRealtimeStreamObserver
{
    /// <summary>One decrypted and decoded packet (also an answered retransmission); <paramref name="pcm"/> is interleaved stereo S16, valid only during the call.</summary>
    void OnAudioPacket(ushort sequence, uint timestamp, bool marker, ReadOnlySpan<short> pcm)
    {
    }

    /// <summary>A 0xD4 NTP sync packet arrived on the control port (SPEC-audio.md 2.3).</summary>
    void OnNtpSync(SyncPacket sync)
    {
    }

    /// <summary>A 0xD7 PTP sync packet arrived on the control port (SPEC-audio.md 4.4): the anchor source of SPEC-receiver.md 5.1.</summary>
    void OnPtpSync(PtpSyncPacket sync)
    {
    }

    /// <summary>
    /// A 0xD7 sync was applied as the playout anchor (SPEC-receiver.md 5.1: the first sync of a stream, a sync flagged
    /// first (<c>0x90</c>), or the next sync after a marker packet); <paramref name="sync"/> is that sync.
    /// </summary>
    void OnAnchor(PtpSyncPacket sync)
    {
    }

    /// <summary>A packet we asked for arrived, wrapped (0xD6 on the control port) or raw (on the data port).</summary>
    void OnRetransmitAnswered(ushort sequence, bool wrapped)
    {
    }

    /// <summary>A 0xD5 retransmit request is about to be sent (reported before sending, so its answer never precedes it).</summary>
    void OnRetransmitRequested(ushort firstSequence, ushort count)
    {
    }

    /// <summary>A packet dropped by <see cref="RealtimeStreamReceiverOptions.SimulateLoss"/>.</summary>
    void OnPacketDropped(ushort sequence)
    {
    }

    /// <summary>
    /// A packet dropped for its timing (SPEC-receiver.md 5.1): <paramref name="late"/> true when its release time (R6) was
    /// more than <c>LateThreshold</c> in the past, false when its timestamp was more than 2 s ahead of the next due frame.
    /// </summary>
    void OnPacketRejected(ushort sequence, uint timestamp, bool late)
    {
    }

    /// <summary>A packet that failed authentication or ALAC decoding, or arrived without a usable <c>shk</c>.</summary>
    void OnBadPacket(ushort sequence, int length, string reason)
    {
    }

    /// <summary>A control-port datagram that is neither 0xD4, 0xD6 nor 0xD7 (logged and ignored, SPEC-audio.md 2.4).</summary>
    void OnUnknownControlPacket(byte type, int length)
    {
    }
}

/// <summary>
/// Where a <see cref="RealtimeStreamReceiver"/> puts decoded frames and anchors (SPEC-receiver.md 5.1, 5.3): the
/// <see cref="PlayoutBuffer"/> in the product (<see cref="RealtimeStreamReceiver.ForBuffer"/>), a recorder in tests. The
/// early/late test asks the target, so the receiver applies the buffer's own R6 mapping (anchor, PTP or anchor-only
/// offset, learnt <c>D</c>) instead of a copy of it.
/// </summary>
internal interface IRealtimePlayoutTarget
{
    IMonotonicClock Clock { get; }

    PlayoutOptions Options { get; }

    void Enqueue(uint rtpTimestamp, uint sequence, ReadOnlyMemory<short> frames);

    void SetAnchor(uint rtpTime, ulong senderTimeNs, ulong timelineId);

    /// <summary>R6: true when a frame at <paramref name="rtpTimestamp"/> is past <c>release + LateThreshold</c> (false before <c>D</c> is learnt).</summary>
    bool IsLate(uint rtpTimestamp);

    /// <summary>
    /// The R6 release time of <paramref name="rtpTimestamp"/> in local nanoseconds (<c>T - Lpipe + D</c>, with <c>D</c> taken
    /// as 0 until it is learnt), or false when the target has no mapping yet.
    /// </summary>
    bool TryGetReleaseTime(uint rtpTimestamp, out long releaseNanos);
}

/// <summary>
/// The receive path of a realtime (type 96) stream (SPEC-receiver.md 5.1, SPEC-audio.md 1-2): a UDP data port that
/// decrypts each RTP packet with the stream's <c>shk</c> (ChaCha20-Poly1305) and ALAC-decodes its 352 frames
/// (<see cref="AlacMagicCookie.Default44100Stereo16"/>), tracks the 16-bit sequence and asks for gaps with 8-byte 0xD5
/// requests to the sender's control port (at once, then up to three times per gap), and a UDP control port that takes
/// 0xD7 syncs as playout anchors and accepts wrapped 0xD6 replies (raw retransmissions arrive on the data port). The ports
/// are receiver-wide and serve the most recent stream (<see cref="BeginStream"/>). Promoted from the fake receiver's audio
/// path (SPEC-receiver.md R2).
/// </summary>
/// <remarks>
/// With a <see cref="PlayoutBuffer"/> (<see cref="RealtimeStreamReceiver(RealtimeStreamReceiverOptions, PlayoutBuffer, IRealtimeStreamObserver?, ILogger?)"/>)
/// the receiver is the product's realtime reader: decoded packets go to <see cref="PlayoutBuffer.Enqueue"/> with their RTP
/// timestamp and sequence, the anchor <c>(now_frame, T, grandmaster)</c> of every 0xD7 sync goes to
/// <see cref="PlayoutBuffer.SetAnchor"/> (which tells a periodic refresh from a re-anchor; the receiver marks the
/// re-anchors at stream start and after a marker packet, the re-anchor after FLUSH), and packets more than 2 s early or
/// already late (R6, as the buffer maps them) are counted and dropped before decrypting. Without one (the fake
/// receiver) nothing is dropped for timing and the observer gets everything.
/// </remarks>
public sealed class RealtimeStreamReceiver : IAsyncDisposable
{
    /// <summary>Datagrams of this size or less are dropped, as shairport does [sps:rtp.c:1803]: an RTP packet carries at least one payload byte.</summary>
    private const int MinimumAudioPacket = RealtimeStreamConstants.HeaderSize + RealtimeStreamConstants.TrailerSize;

    /// <summary>How often the retransmit loop looks for requests to repeat or give up.</summary>
    private static readonly TimeSpan RetransmitTick = TimeSpan.FromMilliseconds(10);

    private static readonly IRealtimeStreamObserver NoObserver = new NullObserver();

    private readonly RealtimeStreamReceiverOptions _options;
    private readonly IRealtimeStreamObserver _observer;
    private readonly IRealtimePlayoutTarget? _playout;
    private readonly ILogger _logger;
    private readonly IMasterClock _clock;
    private readonly CancellationTokenSource _stopping = new();

    // Decode buffers, used under _audioLock only: one packet at a time, no allocation per packet.
    private readonly Lock _audioLock = new();
    private readonly byte[] _alacBuffer = new byte[4096];
    private readonly short[] _pcmBuffer = new short[RealtimeStreamConstants.FramesPerPacket * 2];
    private UdpReceiveLoop? _dataLoop;
    private UdpReceiveLoop? _controlLoop;
    private UdpReceiveLoop? _secondControlLoop;
    private Task? _retransmitLoop;
    private StreamState? _stream;
    private int _started;
    private int _disposed;

    private long _packetsReceived;
    private long _packetsDecoded;
    private long _requestsSent;
    private long _packetsRequested;
    private long _packetsRecovered;
    private long _packetsLost;
    private long _duplicates;
    private long _early;
    private long _late;
    private long _bad;
    private long _syncs;
    private long _anchors;

    /// <summary>Creates the receiver without a playout buffer (the fake receiver's form); nothing is bound until <see cref="Start"/>.</summary>
    /// <param name="options">Options.</param>
    /// <param name="observer">Receives the decoded packets and what else happens on the ports.</param>
    /// <param name="logger">Logger.</param>
    public RealtimeStreamReceiver(RealtimeStreamReceiverOptions options, IRealtimeStreamObserver observer, ILogger? logger = null)
        : this(options, (IRealtimePlayoutTarget?)null, observer ?? throw new ArgumentNullException(nameof(observer)), logger)
    {
    }

    /// <summary>
    /// Creates the product's realtime reader (SPEC-receiver.md 5.1, 5.3): decoded frames are enqueued into
    /// <paramref name="playout"/>, 0xD7 anchors set on it, and early or late packets dropped by its clock, slave and
    /// options; nothing is bound until <see cref="Start"/>.
    /// </summary>
    /// <param name="options">Options.</param>
    /// <param name="playout">The stream's playout buffer.</param>
    /// <param name="observer">Also told what happens (optional).</param>
    /// <param name="logger">Logger.</param>
    public RealtimeStreamReceiver(RealtimeStreamReceiverOptions options, PlayoutBuffer playout, IRealtimeStreamObserver? observer = null, ILogger? logger = null)
        : this(options, ForBuffer(playout ?? throw new ArgumentNullException(nameof(playout))), observer, logger)
    {
    }

    /// <summary>The core constructor; tests pass a recording <see cref="IRealtimePlayoutTarget"/>.</summary>
    internal RealtimeStreamReceiver(RealtimeStreamReceiverOptions options, IRealtimePlayoutTarget? playout, IRealtimeStreamObserver? observer, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRetransmitRequests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.RetransmitInterval, TimeSpan.Zero);
        _options = options;
        _playout = playout;
        _observer = observer ?? NoObserver;
        _logger = logger ?? NullLogger.Instance;
        _clock = options.Clock ?? new MasterClock();
    }

    /// <summary>The ALAC decoder of the data port (not thread-safe: used under the audio lock; do not decode with it while a stream runs).</summary>
    public AlacDecoder Decoder { get; } = new(AlacMagicCookie.Default44100Stereo16);

    /// <summary>The UDP data port announced in stream SETUP answers (0 before <see cref="Start"/>).</summary>
    public int DataPort => _dataLoop?.Port ?? 0;

    /// <summary>The UDP control port announced in stream SETUP answers (0 before <see cref="Start"/>).</summary>
    public int ControlPort => _controlLoop?.Port ?? 0;

    /// <summary>The counters since creation (SPEC-receiver.md 5.1; Diagnostics, 9.1).</summary>
    public RealtimeStreamStatistics Statistics => new(
        Interlocked.Read(ref _packetsReceived),
        Interlocked.Read(ref _packetsDecoded),
        Interlocked.Read(ref _requestsSent),
        Interlocked.Read(ref _packetsRequested),
        Interlocked.Read(ref _packetsRecovered),
        Interlocked.Read(ref _packetsLost),
        Interlocked.Read(ref _duplicates),
        Interlocked.Read(ref _early),
        Interlocked.Read(ref _late),
        Interlocked.Read(ref _bad),
        Interlocked.Read(ref _syncs),
        Interlocked.Read(ref _anchors));

    /// <summary>Binds the data and control ports (and the second control socket when configured) on port 0 and starts the retransmit loop.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var name = _options.Name;
        _dataLoop = new UdpReceiveLoop($"{name} data port", _logger, _options.BindAddress, 0, HandleDataAsync);
        _controlLoop = new UdpReceiveLoop($"{name} control port", _logger, _options.BindAddress, 0, (s, d, a, c) => HandleControlAsync(d, a, primary: true));
        _dataLoop.Start(this);
        _controlLoop.Start(this);
        if (_options.RetransmitFromSecondSocket)
        {
            _secondControlLoop = new UdpReceiveLoop($"{name} second control socket", _logger, _options.BindAddress, 0, (s, d, a, c) => HandleControlAsync(d, a, primary: false));
            _secondControlLoop.Start(this);
        }

        if (_options.RequestRetransmits)
        {
            _retransmitLoop = RunRetransmitLoopAsync(_stopping.Token);
        }
    }

    /// <summary>
    /// Makes a stream SETUP the active stream (SPEC-receiver.md 4.6), replacing any previous one. The first 0xD7 sync of
    /// the stream becomes its anchor (5.1).
    /// </summary>
    /// <param name="owner">The session that set the stream up (compared by reference in <see cref="EndStream"/>, <see cref="HasStream"/> and <see cref="Flush"/>).</param>
    /// <param name="key">The 32-byte <c>shk</c>, or null when none was usable (every packet is then reported as bad).</param>
    /// <param name="senderControl">The sender's announced control port; replaced by the source of the first sync on the control port.</param>
    public void BeginStream(object owner, byte[]? key, IPEndPoint senderControl)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(senderControl);
        var stream = new StreamState(owner, key, senderControl);
        lock (_audioLock)
        {
            _stream = stream;
        }
    }

    /// <summary>Ends the active stream if <paramref name="owner"/> set it up (TEARDOWN, session end); false otherwise.</summary>
    public bool EndStream(object owner)
    {
        lock (_audioLock)
        {
            if (_stream is { } current && ReferenceEquals(current.Owner, owner))
            {
                _stream = null;
                return true;
            }
        }

        return false;
    }

    /// <summary>True while <paramref name="owner"/>'s stream is the active one.</summary>
    public bool HasStream(object owner)
    {
        lock (_audioLock)
        {
            return _stream is { } current && ReferenceEquals(current.Owner, owner);
        }
    }

    /// <summary>
    /// FLUSH of <paramref name="owner"/>'s stream (SPEC-receiver.md 4.5, 5.1): forgets outstanding retransmit requests and
    /// waits for a new anchor (the next sync, or the one after the next marker packet); the next packet is expected at
    /// <paramref name="nextSequence"/>, or re-anchors the sequence on arrival when it is null. The playout buffer is
    /// flushed by the caller (<see cref="PlayoutBuffer.Flush"/>), not here.
    /// </summary>
    public void Flush(object owner, ushort? nextSequence)
    {
        lock (_audioLock)
        {
            if (_stream is not { } stream || !ReferenceEquals(stream.Owner, owner))
            {
                return;
            }

            stream.Outstanding.Clear();
            stream.HaveExpected = nextSequence.HasValue;
            stream.Expected = nextSequence ?? 0;
            stream.HaveLast = false;
            stream.AnchorPending = true;
            stream.AnchorFresh = false;
            stream.Anchored = false;
        }
    }

    /// <summary>Ends the active stream, stops the retransmit loop and closes the sockets.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_audioLock)
        {
            _stream = null;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_retransmitLoop is not null)
        {
            await _retransmitLoop.ConfigureAwait(false); // ends quietly on cancellation
        }

        foreach (var loop in new[] { _dataLoop, _controlLoop, _secondControlLoop })
        {
            if (loop is not null)
            {
                await loop.DisposeAsync().ConfigureAwait(false);
            }
        }

        _stopping.Dispose();
    }

    /// <summary>The <see cref="PlayoutBuffer"/> as the receiver's playout target (SPEC-receiver.md 5.3 input and anchor).</summary>
    internal static IRealtimePlayoutTarget ForBuffer(PlayoutBuffer buffer) => new BufferTarget(buffer);

    private ValueTask HandleDataAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            _logger.LogDebug("Receiver {Name}: {Length}-byte datagram on the data port without a stream", _options.Name, datagram.Length);
            return ValueTask.CompletedTask;
        }

        if (datagram.Length <= MinimumAudioPacket)
        {
            _logger.LogDebug("Receiver {Name}: dropping a {Length}-byte datagram on the data port", _options.Name, datagram.Length);
            return ValueTask.CompletedTask;
        }

        // A raw retransmission on the data port (SPEC-audio.md 2.4 form 2) is recognised by its sequence being outstanding.
        var request = ProcessAudio(stream, datagram.Span, wrapped: false);
        return request is { } r ? SendRetransmitRequestAsync(stream, [r], stop) : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The control port (and the second socket): 0xD4 syncs are reported, 0xD7 syncs reported and applied as anchors,
    /// 0xD6 replies unwrapped and decoded, anything else logged and ignored (SPEC-audio.md 2.4). Like shairport, the
    /// primary port learns the sender's control address from the first sync and sends retransmit requests there
    /// [sps:rtp.c:1672-1677].
    /// </summary>
    private ValueTask HandleControlAsync(ReadOnlyMemory<byte> datagram, SocketAddress source, bool primary)
    {
        var span = datagram.Span;
        if (span.Length < 4)
        {
            _observer.OnUnknownControlPacket(span.Length > 1 ? span[1] : (byte)0, span.Length);
            return ValueTask.CompletedTask;
        }

        var stream = Volatile.Read(ref _stream);
        switch (span[1] & 0x7F)
        {
            case RtpPacketTypes.Sync & 0x7F when SyncPacket.TryParse(span, out var sync):
                LearnControlAddress(stream, source, primary);
                _observer.OnNtpSync(sync);
                break;
            case RtpPacketTypes.PtpSync & 0x7F when PtpSyncPacket.TryParse(span, out var ptp):
                LearnControlAddress(stream, source, primary);
                Interlocked.Increment(ref _syncs);
                _observer.OnPtpSync(ptp);
                if (stream is not null)
                {
                    ApplySync(stream, ptp);
                }

                break;
            case RtpPacketTypes.RetransmitReply & 0x7F when RetransmitReply.TryUnwrap(span, out var original):
                if (stream is not null && original.Length > MinimumAudioPacket)
                {
                    // A wrapped reply never yields a new request: only outstanding sequences are accepted.
                    ProcessAudio(stream, original, wrapped: true);
                }
                else
                {
                    _logger.LogDebug("Receiver {Name}: retransmit reply without a stream or payload ignored", _options.Name);
                }

                break;
            default:
                _logger.LogDebug("Receiver {Name}: unknown control packet 0x{Type:X2} ({Length} bytes)", _options.Name, span[1], span.Length);
                _observer.OnUnknownControlPacket(span[1], span.Length);
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The anchor rule of SPEC-receiver.md 5.1: <c>(T, now_frame)</c> of every 0xD7 sync is the anchor and goes to
    /// <see cref="PlayoutBuffer.SetAnchor"/>, which keeps <c>D</c> for a periodic sync that agrees with the current mapping
    /// (a refresh) and re-learns it otherwise, so a sender timeline that moves without a marker is followed. The stream's
    /// first sync, the first after a FLUSH or a marker packet, and one flagged first (<c>0x90</c>, which our sender sends
    /// right before the marker packet of a resync) are the re-anchors: counted and reported with
    /// <see cref="IRealtimeStreamObserver.OnAnchor"/>.
    /// </summary>
    private void ApplySync(StreamState stream, PtpSyncPacket sync)
    {
        lock (_audioLock)
        {
            if (!ReferenceEquals(_stream, stream))
            {
                return;
            }

            stream.Anchored = true;
            _playout?.SetAnchor(sync.NowFrame, sync.PtpTimeNanos, sync.ClockId);
            if (!(stream.AnchorPending || sync.First))
            {
                return; // a periodic refresh
            }

            stream.AnchorPending = false;
            stream.AnchorFresh = true;
            Interlocked.Increment(ref _anchors);
            _observer.OnAnchor(sync);
        }

        _logger.LogDebug("Receiver {Name}: anchor now_frame {Frame} at {Time} ns (clock {Clock:X16})", _options.Name, sync.NowFrame, sync.PtpTimeNanos, sync.ClockId);
    }

    private void LearnControlAddress(StreamState? stream, SocketAddress source, bool primary)
    {
        if (!primary || stream is null)
        {
            return;
        }

        lock (_audioLock)
        {
            if (!stream.LearnedControl)
            {
                stream.LearnedControl = true;
                stream.SenderControl = UdpSockets.ToEndPoint(source);
            }
        }
    }

    /// <summary>Gap tracking, simulated loss, the timing test and decoding of one packet; returns the retransmit request to send, if any.</summary>
    private RetransmitRequest? ProcessAudio(StreamState stream, ReadOnlySpan<byte> packet, bool wrapped)
    {
        var sequence = RtpAudioPacketBuilder.ReadSequence(packet);
        var timestamp = RtpAudioPacketBuilder.ReadTimestamp(packet);
        var marker = RtpAudioPacketBuilder.ReadMarker(packet);
        RetransmitRequest? request = null;
        lock (_audioLock)
        {
            if (!ReferenceEquals(_stream, stream))
            {
                return null;
            }

            Interlocked.Increment(ref _packetsReceived);
            var answered = stream.Outstanding.Remove(sequence);
            if (wrapped && !answered)
            {
                _logger.LogDebug("Receiver {Name}: unrequested or duplicate retransmission of {Sequence}", _options.Name, sequence);
                Interlocked.Increment(ref _duplicates);
                return null;
            }

            if (!answered)
            {
                stream.FreshPackets++;
                if (_options.SimulateLoss is { } loss && stream.HaveExpected && !marker && loss(stream.FreshPackets, sequence))
                {
                    _observer.OnPacketDropped(sequence);
                    return null;
                }

                if (!TrackSequence(stream, sequence, marker, out request))
                {
                    Interlocked.Increment(ref _duplicates);
                    return request;
                }
            }
            else
            {
                Interlocked.Increment(ref _packetsRecovered);
            }

            if (marker)
            {
                // The first packet after FLUSH re-anchors on the next sync, unless the sync that announced it (0x90) already did (5.1).
                if (!stream.AnchorFresh)
                {
                    stream.AnchorPending = true;
                    stream.Anchored = false;
                }
            }

            stream.AnchorFresh = false;
            if (!PassesTiming(stream, sequence, timestamp, marker))
            {
                return request;
            }

            Decode(stream, packet, sequence, timestamp, marker);
            if (answered)
            {
                _observer.OnRetransmitAnswered(sequence, wrapped);
            }
        }

        return request;
    }

    /// <summary>
    /// The timing test of SPEC-receiver.md 5.1 (under the audio lock): with an anchor, the playout target's R6 mapping
    /// decides (release more than <c>MaxTimestampAhead</c> in the future: a corrupt timestamp; more than
    /// <c>LateThreshold</c> in the past: late); before the first anchor (and after a marker, until the next sync), a
    /// timestamp more than 2 s ahead of what the previous accepted packet predicts is corrupt. Without a playout target
    /// nothing is dropped.
    /// </summary>
    private bool PassesTiming(StreamState stream, ushort sequence, uint timestamp, bool marker)
    {
        if (_playout is not { } playout)
        {
            return true;
        }

        var options = playout.Options;
        bool? late = null;
        if (stream.Anchored && playout.TryGetReleaseTime(timestamp, out var release))
        {
            // A corrupt timestamp never reaches the buffer, so D is never learnt from it (R6).
            if (release - unchecked((long)playout.Clock.NowNanoseconds) > options.MaxTimestampAhead.Ticks * 100)
            {
                late = false;
            }
            else if (playout.IsLate(timestamp))
            {
                late = true;
            }
        }
        else if (stream.HaveLast && !marker)
        {
            var predicted = unchecked(stream.LastTimestamp + (uint)((short)(sequence - stream.LastSequence) * RealtimeStreamConstants.FramesPerPacket));
            var ahead = unchecked((int)(timestamp - predicted));
            if (ahead > options.MaxTimestampAhead.TotalSeconds * PlayoutOptions.SampleRate)
            {
                late = false;
            }
        }

        if (late is { } isLate)
        {
            if (isLate)
            {
                Interlocked.Increment(ref _late);
            }
            else
            {
                Interlocked.Increment(ref _early);
            }

            _logger.LogDebug("Receiver {Name}: packet {Sequence} (timestamp {Timestamp}) dropped as {Kind}", _options.Name, sequence, timestamp, isLate ? "late" : "too early");
            _observer.OnPacketRejected(sequence, timestamp, isLate);
            return false;
        }

        if (!stream.HaveLast || unchecked((short)(sequence - stream.LastSequence)) > 0)
        {
            stream.HaveLast = true;
            stream.LastSequence = sequence;
            stream.LastTimestamp = timestamp;
        }

        return true;
    }

    /// <summary>
    /// Advances the expected sequence (wrap-safe); a jump forward within the history window is a gap to request. False
    /// when the packet is behind the expected sequence and not outstanding: a duplicate (or an answer to a repeated
    /// request, or a packet already given up), dropped.
    /// </summary>
    private bool TrackSequence(StreamState stream, ushort sequence, bool marker, out RetransmitRequest? request)
    {
        request = null;
        if (marker || !stream.HaveExpected)
        {
            // First packet, or a re-anchor after a join/flush (marker bit, SPEC-audio.md 1.2): no gap.
            stream.HaveExpected = true;
            stream.Expected = unchecked((ushort)(sequence + 1));
            return true;
        }

        var gap = unchecked((short)(sequence - stream.Expected));
        if (gap == 0)
        {
            stream.Expected = unchecked((ushort)(sequence + 1));
            return true;
        }

        if (gap < 0)
        {
            _logger.LogDebug("Receiver {Name}: duplicate or given-up packet {Sequence} (expected {Expected}) dropped", _options.Name, sequence, stream.Expected);
            return false;
        }

        var first = stream.Expected;
        stream.Expected = unchecked((ushort)(sequence + 1));
        if (!_options.RequestRetransmits)
        {
            Interlocked.Add(ref _packetsLost, gap);
            return true;
        }

        if (gap >= RealtimeStreamConstants.HistoryPackets)
        {
            // Beyond the sender's history (SPEC-audio.md 2.4): resynchronise instead of asking.
            _logger.LogWarning("Receiver {Name}: {Gap} packets missing before {Sequence}; resynchronising", _options.Name, gap, sequence);
            Interlocked.Add(ref _packetsLost, gap);
            return true;
        }

        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < gap; i++)
        {
            stream.Outstanding[unchecked((ushort)(first + i))] = new Pending(now, 1);
        }

        request = new RetransmitRequest(first, (ushort)gap);
        return true;
    }

    private void Decode(StreamState stream, ReadOnlySpan<byte> packet, ushort sequence, uint timestamp, bool marker)
    {
        if (stream.Key is not { } key)
        {
            Interlocked.Increment(ref _bad);
            _observer.OnBadPacket(sequence, packet.Length, "no usable shk");
            return;
        }

        if (!RtpAudioPacketBuilder.TryDecrypt(key, packet, _alacBuffer, out var length))
        {
            _logger.LogWarning("Receiver {Name}: packet {Sequence} failed authentication", _options.Name, sequence);
            Interlocked.Increment(ref _bad);
            _observer.OnBadPacket(sequence, packet.Length, "authentication failed");
            return;
        }

        var bits = new BitBuffer(_alacBuffer, length);
        var frames = Decoder.Decode(ref bits, _pcmBuffer, (int)Decoder.Config.FrameLength, 2);
        if (frames < 0)
        {
            _logger.LogWarning("Receiver {Name}: packet {Sequence} is not a valid ALAC frame", _options.Name, sequence);
            Interlocked.Increment(ref _bad);
            _observer.OnBadPacket(sequence, packet.Length, "ALAC decode failed");
            return;
        }

        Interlocked.Increment(ref _packetsDecoded);

        // PlayoutBuffer.Enqueue copies (SPEC-receiver.md 5.3), so the shared decode buffer can be handed over as is.
        _playout?.Enqueue(timestamp, sequence, _pcmBuffer.AsMemory(0, frames * 2));
        _observer.OnAudioPacket(sequence, timestamp, marker, _pcmBuffer.AsSpan(0, frames * 2));
    }

    /// <summary>
    /// Repeats requests for packets still missing <see cref="RealtimeStreamReceiverOptions.RetransmitInterval"/> after the
    /// previous one, up to <see cref="RealtimeStreamReceiverOptions.MaxRetransmitRequests"/> per packet, and gives up on
    /// packets whose last request went unanswered (SPEC-receiver.md 5.1).
    /// </summary>
    private async Task RunRetransmitLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(RetransmitTick);
        var interval = (long)(_options.RetransmitInterval.TotalSeconds * Stopwatch.Frequency);
        var due = new List<ushort>();
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                StreamState? stream;
                List<RetransmitRequest>? requests = null;
                lock (_audioLock)
                {
                    stream = _stream;
                    if (stream is null || stream.Outstanding.Count == 0)
                    {
                        continue;
                    }

                    var now = Stopwatch.GetTimestamp();
                    due.Clear();
                    List<ushort>? givenUp = null;
                    foreach (var (sequence, pending) in stream.Outstanding)
                    {
                        if (now - pending.LastSent < interval)
                        {
                            continue;
                        }

                        if (pending.Attempts >= _options.MaxRetransmitRequests)
                        {
                            (givenUp ??= []).Add(sequence);
                        }
                        else
                        {
                            due.Add(sequence);
                        }
                    }

                    if (givenUp is not null)
                    {
                        foreach (var sequence in givenUp)
                        {
                            stream.Outstanding.Remove(sequence);
                        }

                        Interlocked.Add(ref _packetsLost, givenUp.Count);
                        _logger.LogDebug("Receiver {Name}: gave up on {Count} missing packets", _options.Name, givenUp.Count);
                    }

                    foreach (var sequence in due)
                    {
                        var pending = stream.Outstanding[sequence];
                        stream.Outstanding[sequence] = new Pending(now, pending.Attempts + 1);
                    }

                    requests = Runs(due, stream.Expected);
                }

                if (requests is { Count: > 0 })
                {
                    await SendRetransmitRequestAsync(stream, requests, stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>Consecutive runs of <paramref name="sequences"/>, ordered by their distance behind <paramref name="expected"/> (wrap-safe).</summary>
    private static List<RetransmitRequest> Runs(List<ushort> sequences, ushort expected)
    {
        var requests = new List<RetransmitRequest>();
        if (sequences.Count == 0)
        {
            return requests;
        }

        sequences.Sort((a, b) => unchecked((ushort)(expected - b)).CompareTo(unchecked((ushort)(expected - a))));
        var first = sequences[0];
        ushort count = 1;
        for (var i = 1; i < sequences.Count; i++)
        {
            if (sequences[i] == unchecked((ushort)(first + count)))
            {
                count++;
                continue;
            }

            requests.Add(new RetransmitRequest(first, count));
            first = sequences[i];
            count = 1;
        }

        requests.Add(new RetransmitRequest(first, count));
        return requests;
    }

    /// <summary>Sends 0xD5 requests (8- or 16-byte form) from the control port or the second socket to the sender's control address.</summary>
    private async ValueTask SendRetransmitRequestAsync(StreamState stream, IReadOnlyList<RetransmitRequest> requests, CancellationToken stop)
    {
        var socket = (_options.RetransmitFromSecondSocket ? _secondControlLoop : _controlLoop)?.Socket;
        if (socket is null)
        {
            return;
        }

        IPEndPoint target;
        lock (_audioLock)
        {
            target = stream.SenderControl;
        }

        var buffer = new byte[RetransmitRequest.ExtendedSize];
        foreach (var request in requests)
        {
            var length = request.Write(buffer, _options.ExtendedRetransmitRequests ? _clock.NtpNow : null);
            Interlocked.Increment(ref _requestsSent);
            Interlocked.Add(ref _packetsRequested, request.Count);

            // Reported before sending so the answer can never precede the request.
            _observer.OnRetransmitRequested(request.FirstSequence, request.Count);
            try
            {
                await socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, target, stop).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug("Receiver {Name}: retransmit request to {Target} failed: {Error}", _options.Name, target, ex.SocketErrorCode);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>One outstanding missing packet: when it was last requested and how often.</summary>
    private readonly record struct Pending(long LastSent, int Attempts);

    /// <summary>The realtime stream currently served by the UDP ports.</summary>
    private sealed class StreamState(object owner, byte[]? key, IPEndPoint senderControl)
    {
        public object Owner { get; } = owner;

        public byte[]? Key { get; } = key;

        /// <summary>Where retransmit requests go: the announced <c>controlPort</c>, replaced by the source of the first sync (see <see cref="HandleControlAsync"/>).</summary>
        public IPEndPoint SenderControl { get; set; } = senderControl;

        public bool LearnedControl { get; set; }

        public bool HaveExpected { get; set; }

        public ushort Expected { get; set; }

        public long FreshPackets { get; set; }

        public Dictionary<ushort, Pending> Outstanding { get; } = [];

        /// <summary>The next 0xD7 sync becomes the anchor (stream start, FLUSH, marker packet; 5.1).</summary>
        public bool AnchorPending { get; set; } = true;

        /// <summary>An anchor was applied and no packet has arrived since (the 0x90 sync before a resync's marker packet).</summary>
        public bool AnchorFresh { get; set; }

        /// <summary>The newest accepted packet, for the corrupt-timestamp test before the first anchor.</summary>
        public bool HaveLast { get; set; }

        public ushort LastSequence { get; set; }

        public uint LastTimestamp { get; set; }

        /// <summary>A sync anchored the stream and no marker packet or FLUSH has asked for a new one since: the target's R6 mapping applies.</summary>
        public bool Anchored { get; set; }
    }

    private sealed class BufferTarget(PlayoutBuffer buffer) : IRealtimePlayoutTarget
    {
        private readonly long _lpipeNanos = buffer.Options.Lpipe * 1_000_000_000L / PlayoutOptions.SampleRate;

        public IMonotonicClock Clock => buffer.Clock;

        public PlayoutOptions Options => buffer.Options;

        public void Enqueue(uint rtpTimestamp, uint sequence, ReadOnlyMemory<short> frames) => buffer.Enqueue(rtpTimestamp, sequence, frames);

        public void SetAnchor(uint rtpTime, ulong senderTimeNs, ulong timelineId) => buffer.SetAnchor(rtpTime, senderTimeNs, timelineId);

        public bool IsLate(uint rtpTimestamp) => buffer.IsLate(rtpTimestamp);

        public bool TryGetReleaseTime(uint rtpTimestamp, out long releaseNanos)
        {
            if (!buffer.TryGetPlayTime(rtpTimestamp, out var playTime))
            {
                releaseNanos = 0;
                return false;
            }

            // D reads 0 until it is learnt: the earliest release R6 allows, so nothing is called early wrongly.
            releaseNanos = playTime - _lpipeNanos + (buffer.Statistics.StreamOffset.Ticks * 100);
            return true;
        }
    }

    private sealed class NullObserver : IRealtimeStreamObserver;
}
