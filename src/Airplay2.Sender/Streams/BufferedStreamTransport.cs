using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Sender.Streams;

/// <summary>Options of a <see cref="BufferedStreamTransport"/>.</summary>
public sealed record BufferedStreamOptions
{
    /// <summary>
    /// The lead of the anchor the transport derives from a paced packet, in frames of <see cref="Format"/>: the packet that
    /// is due at <c>T</c> plays at <c>T + L</c>, as on the realtime stream (SPEC.md D1: 77 175). Keep it equal to the group
    /// player's <c>GroupPlayerOptions.LatencyFrames</c>; the player uses that value for its own "frame at the speaker".
    /// </summary>
    public int LatencyFrames { get; init; } = RealtimeStreamConstants.DefaultLatencyFrames;

    /// <summary>The audio format announced in the stream SETUP and written as SSRC (SPEC-audio.md 4.1, 4.2; probe P16).</summary>
    public BufferedAudioFormat Format { get; init; } = BufferedAudioFormat.Alac44100S16;

    /// <summary><c>audioBufferSize</c> to assume when the stream SETUP answer has none.</summary>
    public long DefaultAudioBufferSize { get; init; } = BufferedStreamConstants.DefaultAudioBufferSize;

    /// <summary>
    /// Run-ahead limit as a fraction of the receiver's <c>audioBufferSize</c> (SPEC-audio.md 4.2: the receiver's TCP window
    /// and its buffer are the only flow control); the rest is headroom for packets our play-time estimate counts as played
    /// before the receiver has discarded them.
    /// </summary>
    public double WindowFraction { get; init; } = 0.75;

    /// <summary>Timeout of one TCP connect to the receiver's data port (the SpeakerA needed more than 5 s once, SPEC-audio.md appendix A).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Connect attempts before the data connection counts as failed (shairport-sync listens before it answers the SETUP, <c>[sps] rtsp.c:2935-2961</c>).</summary>
    public int ConnectAttempts { get; init; } = 3;

    /// <summary>Delay between connect attempts.</summary>
    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound of one SETRATEANCHORTIME or FLUSHBUFFERED round trip; a request that takes longer fails with
    /// <see cref="TimeoutException"/>. The SpeakerA answered FLUSH after 2-17 s (SPEC-control.md 4.8 note), so the default is
    /// generous.
    /// </summary>
    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A flush driven through <see cref="BufferedStreamTransport.FlushReceiverAsync"/> (the group player's pause and seek)
    /// sends <c>SETRATEANCHORTIME rate 0</c> before <c>FLUSHBUFFERED</c>: SPEC-audio.md 4.5 pauses with rate 0 and seeks
    /// with FLUSHBUFFERED; the group player drops the queued audio in both cases, so both are sent. shairport-sync's
    /// immediate flush stops playing by itself (<c>[sps] rtsp.c:1352-1366</c>); for Apple-SDK receivers this is not known.
    /// </summary>
    public bool PauseBeforeFlush { get; init; } = true;

    /// <summary>
    /// Send a receiver's <c>SETRATEANCHORTIME rate 1</c> only once audio has been written to its data connection since the
    /// anchor was set (or <see cref="AnchorAudioWait"/> has passed), so the anchor never names audio the receiver cannot
    /// have yet. The Test Speaker ABCD answers rate 1 with <c>400 Bad Request</c> while its data connection is open and
    /// empty (it answers 200 while no data connection exists); with ALAC audio already written it still answered 400, so
    /// this order is necessary there but not sufficient (live, 2026-10-09, SPEC-audio.md 4.3 note, probe P16).
    /// shairport-sync accepts either order. The anchor's network time is absolute, so sending it later does not move the
    /// timeline.
    /// </summary>
    public bool AnchorAfterAudio { get; init; } = true;

    /// <summary>The longest an anchor waits for audio (<see cref="AnchorAfterAudio"/>) before it is sent anyway.</summary>
    public TimeSpan AnchorAudioWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The data socket's send buffer in bytes.</summary>
    public int SendBufferSize { get; init; } = 256 * 1024;
}

/// <summary>What the buffered transport observed (diagnostics, tests, probe P16).</summary>
public abstract record BufferedStreamEvent
{
    /// <summary>The TCP data connection to a receiver is up.</summary>
    public sealed record DataConnected(string ReceiverId, IPEndPoint Remote) : BufferedStreamEvent;

    /// <summary>The data connection failed or closed; the receiver gets no more audio until it is added again.</summary>
    public sealed record DataClosed(string ReceiverId, string Reason) : BufferedStreamEvent;

    /// <summary>A SETRATEANCHORTIME was answered 2xx.</summary>
    public sealed record Anchored(string ReceiverId, int Rate, uint RtpTime, ulong NetworkTimeNanos) : BufferedStreamEvent;

    /// <summary>A FLUSHBUFFERED was answered 2xx (sequence numbers are the 23-bit wire values).</summary>
    public sealed record Flushed(string ReceiverId, uint UntilSequence, uint UntilTimestamp, uint? FromSequence) : BufferedStreamEvent;

    /// <summary>A SETRATEANCHORTIME or FLUSHBUFFERED failed.</summary>
    public sealed record ControlFailed(string ReceiverId, string Request, string Error) : BufferedStreamEvent;

    /// <summary>A packet was not sent to a receiver because its run-ahead window was full.</summary>
    public sealed record WindowFull(string ReceiverId, long BufferedBytes, long Window) : BufferedStreamEvent;
}

/// <summary>A snapshot of one receiver of a <see cref="BufferedStreamTransport"/>.</summary>
/// <param name="ReceiverId">The receiver.</param>
/// <param name="DataConnected">The TCP data connection is up.</param>
/// <param name="Failed">The data connection failed; audio to it is dropped.</param>
/// <param name="Anchored">A SETRATEANCHORTIME rate 1 was issued since the last flush/pause/resync.</param>
/// <param name="HasSession">A control session is attached (anchors and flushes can be sent).</param>
/// <param name="BufferedBytes">Bytes sent whose estimated play time has not passed.</param>
/// <param name="QueuedBytes">Bytes handed to the writer but not yet written to the socket (since the last immediate flush; older ones are discarded unwritten).</param>
/// <param name="Window">The run-ahead limit in bytes.</param>
public sealed record BufferedReceiverStatus(string ReceiverId, bool DataConnected, bool Failed, bool Anchored, bool HasSession, long BufferedBytes, long QueuedBytes, long Window);

/// <summary>
/// Type 103 over TCP with PTP timing, SETRATEANCHORTIME and FLUSHBUFFERED (SPEC-audio.md 4.1-4.3, 4.5; SPEC-control.md
/// 4.7, 4.8). The second implementation of <see cref="IStreamTransport"/> (docs/ARCHITECTURE.md, "Design decisions": realtime first, buffered if needed).
/// </summary>
/// <remarks>
/// <para>
/// Data (SPEC-audio.md 4.2): per receiver one TCP connection to the <c>dataPort</c> of its stream SETUP answer, opened by
/// <see cref="AddReceiver"/> in the background (connect timeout and retries from <see cref="BufferedStreamOptions"/>), and
/// one writer task that coalesces queued packets into socket writes. Every packet is
/// <see cref="BufferedAudioPacket"/>: length prefix including itself, marker + 23-bit sequence number, RTP timestamp, the
/// format code as SSRC, the ALAC frame encrypted per receiver as on the realtime stream (own key, own 64-bit nonce
/// counter, SPEC.md D9). The group's 16-bit sequence numbers are extended to 23 bits.
/// </para>
/// <para>
/// Anchoring (SPEC-audio.md 4.3): there are no sync packets; a receiver plays frame <c>x</c> at
/// <c>networkTime + (x - rtpTime) / sr</c> on our PTP timeline (<c>[sps] rtp.c:1456-1457, 1510-1526</c>,
/// <c>frame_to_ptp_local_time</c>), named by <c>networkTimeTimelineID = </c><see cref="IPtpMaster.ClockId"/>. A receiver
/// that is not anchored (just added, flushed, paused or resynced) gets <c>SETRATEANCHORTIME rate 1</c> with the next
/// packet it is sent: <c>rtpTime</c> = that packet's timestamp, <c>networkTime</c> = its due instant plus
/// <c>L + offset_i</c> frames, the same relation the realtime stream's <c>0x90</c> sync states (SPEC-audio.md 1.5, 3.3),
/// so a group can mix receivers that joined at different times. <see cref="AnchorAsync"/> sets an explicit anchor instead
/// (run-ahead producers). The requests go over each receiver's <see cref="IReceiverSession"/> (attached with
/// <see cref="AttachSession"/> or <see cref="WrapSessionFactory"/>), in order, from one control loop per receiver, and a
/// rate-1 anchor waits until audio has been written to that receiver's data connection (the Test Speaker ABCD rejects it
/// with 400 while the connection is still empty, <see cref="BufferedStreamOptions.AnchorAfterAudio"/>).
/// </para>
/// <para>
/// Flushing (SPEC-audio.md 4.5): <see cref="FlushReceiverAsync"/> (what the group player's FLUSH turns into through
/// <see cref="BufferedReceiverSession"/>), <see cref="FlushAsync"/>, <see cref="PauseAsync"/> and
/// <see cref="FlushRangeAsync"/> (the deferred splice form). An immediate flush also drops the packets still queued for
/// the socket.
/// </para>
/// <para>
/// Run-ahead (SPEC-audio.md 4.2): the receiver buffers up to <c>audioBufferSize</c> (8 MiB on shairport-sync); the sender
/// may push that much ahead and top up as it plays. Per receiver the transport counts the bytes whose play time has not
/// passed (from its anchor) plus the bytes not yet written; <see cref="Send"/> never blocks and drops a packet for a
/// receiver whose window is full (<see cref="BufferedStreamEvent.WindowFull"/>); <see cref="WaitForWindowAsync"/> lets a
/// producer that runs ahead of real time wait for room instead. The group player paces in real time, so its run-ahead is
/// <c>L</c> and the window is a safety net.
/// </para>
/// <para>
/// Timing: the session is PTP-timed (<see cref="TimingProtocol"/>), the PTP grandmaster's peers are reference-counted
/// through <see cref="AddTimingPeer"/>. Ownership as in <see cref="RealtimeStreamTransport"/>: the clock, the control port
/// and the PTP master are started here but never disposed. The control port is announced in the stream SETUP and kept
/// running; the TCP stream needs no retransmissions, so no receiver is registered with it.
/// </para>
/// </remarks>
public sealed partial class BufferedStreamTransport : IStreamTransport, IStreamLatency
{
    private const int CoalesceBytes = 64 * 1024;
    private const int MinimumWindow = 64 * 1024;
    private const long NanosPerSecond = 1_000_000_000L;

    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly Channel<BufferedStreamEvent> _events = Channel.CreateBounded<BufferedStreamEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Dictionary<string, SessionLink> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _bufferSizes = new(StringComparer.Ordinal);
    private readonly Dictionary<IPAddress, int> _timingPeers = [];
    private readonly ConcurrentDictionary<Task, string> _background = new();
    private ImmutableArray<Receiver> _receivers = ImmutableArray<Receiver>.Empty;
    private ClockMapping? _mapping;
    private long _ptpMinusMonotonic;
    private Task? _starting;
    private (uint RtpTime, ulong PtpNanos)? _explicitAnchor;
    private bool _haveSent;
    private ushort _lastSequence;
    private uint _lastWireSequence;
    private uint _nextTimestamp;
    private long _packetsSent;
    private long _bytesWritten;
    private long _packetsDropped;
    private long _packetsDiscarded;
    private long _anchorsSent;
    private long _flushesSent;
    private long _controlErrors;
    private bool _disposed;

    /// <summary>Creates the transport with default options.</summary>
    public BufferedStreamTransport(IMasterClock clock, IPtpMaster ptp, IControlPort control)
        : this(clock, ptp, control, options: null, logger: null)
    {
    }

    /// <summary>Creates the transport.</summary>
    /// <param name="clock">The process-wide master clock (the PTP grandmaster and the anchors read it).</param>
    /// <param name="ptp">The PTP grandmaster whose clock id names the anchors' timeline.</param>
    /// <param name="control">The process-wide control port (its port goes into the stream SETUP).</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="logger">Logger; null for none.</param>
    public BufferedStreamTransport(IMasterClock clock, IPtpMaster ptp, IControlPort control, BufferedStreamOptions? options, ILogger<BufferedStreamTransport>? logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ptp);
        ArgumentNullException.ThrowIfNull(control);
        options ??= new BufferedStreamOptions();
        ArgumentNullException.ThrowIfNull(options.Format, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.LatencyFrames, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DefaultAudioBufferSize, 0L, nameof(options));
        if (options.WindowFraction is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WindowFraction, "WindowFraction is in (0, 1].");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.ConnectAttempts, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ConnectTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ControlTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Format.SampleRate, 0, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Format.FramesPerPacket, 0, nameof(options));
        Clock = clock;
        Ptp = ptp;
        Control = control;
        Options = options;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <inheritdoc />
    public IMasterClock Clock { get; }

    /// <summary>The PTP grandmaster.</summary>
    public IPtpMaster Ptp { get; }

    /// <summary>The control port (announced in the stream SETUP; the receiver may still send 0xD5 there, which a TCP stream never needs).</summary>
    public IControlPort Control { get; }

    /// <summary>The options.</summary>
    public BufferedStreamOptions Options { get; }

    /// <summary>The audio format (SSRC, SETUP fields).</summary>
    public BufferedAudioFormat Format => Options.Format;

    /// <summary>L in frames of <see cref="Format"/> (<see cref="IStreamLatency"/>: a group player must use the same value).</summary>
    public int LatencyFrames => Options.LatencyFrames;

    /// <inheritdoc />
    public StreamType Type => StreamType.Buffered;

    /// <inheritdoc />
    public TimingProtocol TimingProtocol => TimingProtocol.Ptp;

    /// <inheritdoc />
    public int ControlPort => Control.Port;

    /// <inheritdoc />
    public int TimingPort => 0;

    /// <summary>What the transport observed (bounded, oldest dropped).</summary>
    public ChannelReader<BufferedStreamEvent> Events => _events.Reader;

    /// <summary>Packets handed to the writers (all receivers counted).</summary>
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    /// <summary>Bytes written to the data sockets (all receivers counted).</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Packets not sent to a receiver because its window was full or its data connection had failed.</summary>
    public long PacketsDropped => Interlocked.Read(ref _packetsDropped);

    /// <summary>Queued packets dropped by an immediate flush before they were written.</summary>
    public long PacketsDiscarded => Interlocked.Read(ref _packetsDiscarded);

    /// <summary>SETRATEANCHORTIME requests answered 2xx.</summary>
    public long AnchorsSent => Interlocked.Read(ref _anchorsSent);

    /// <summary>FLUSHBUFFERED requests answered 2xx.</summary>
    public long FlushesSent => Interlocked.Read(ref _flushesSent);

    /// <summary>SETRATEANCHORTIME/FLUSHBUFFERED requests that failed.</summary>
    public long ControlErrors => Interlocked.Read(ref _controlErrors);

    /// <summary>The 23-bit wire sequence number of the next packet (the extension of the next 16-bit sequence).</summary>
    public uint NextWireSequence
    {
        get
        {
            lock (_gate)
            {
                return _haveSent ? (_lastWireSequence + 1) & BufferedAudioPacket.SequenceMask : 0;
            }
        }
    }

    /// <summary>The receivers and their state (diagnostics, tests).</summary>
    public IReadOnlyList<BufferedReceiverStatus> ReceiverStates
    {
        get
        {
            lock (_gate)
            {
                var now = Clock.NowNanos;
                var list = new List<BufferedReceiverStatus>(_receivers.Length);
                foreach (var r in _receivers)
                {
                    Release(r, now);
                    list.Add(new BufferedReceiverStatus(r.Id, r.Connected, r.Failed, r.Anchored, _links.ContainsKey(r.Id), r.BufferedBytes, Interlocked.Read(ref r.Batch.Bytes), r.Window));
                }

                return list;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Starts the control port and the PTP grandmaster (idempotent by contract) and measures the clock's PTP offset.</remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_starting is null || _starting.IsFaulted || _starting.IsCanceled)
            {
                _starting = StartCoreAsync(cancellationToken);
            }

            return _starting;
        }
    }

    /// <summary>
    /// Wraps a session factory (the group player's) so every session it returns is a <see cref="BufferedReceiverSession"/>
    /// attached to this transport: it drives SETRATEANCHORTIME/FLUSHBUFFERED for the receiver, makes it a PTP peer, applies
    /// <see cref="Format"/> to the stream SETUP and turns the group player's FLUSH into FLUSHBUFFERED.
    /// </summary>
    public Func<DiscoveredReceiver, CancellationToken, Task<IReceiverSession>> WrapSessionFactory(Func<DiscoveredReceiver, CancellationToken, Task<IReceiverSession>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return async (receiver, cancellationToken) =>
        {
            var inner = await factory(receiver, cancellationToken).ConfigureAwait(false);
            try
            {
                return new BufferedReceiverSession(this, inner, receiver.Id);
            }
            catch
            {
                // e.g. the transport was disposed meanwhile: nobody else owns the session yet
                await inner.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        };
    }

    /// <summary>
    /// Creates a group player over this transport with what the buffered stream needs: the session factory is wrapped by
    /// <see cref="WrapSessionFactory"/> (anchors, FLUSHBUFFERED, PTP peers, the type-103 SETUP), and the player's
    /// <see cref="GroupPlayerOptions.LatencyFrames"/> is set to <see cref="LatencyFrames"/> so its "frame at the speaker"
    /// matches the anchors (SPEC-audio.md 1.5, 4.3). The player owns and disposes the transport.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Format"/> is not the 44.1 kHz/16-bit ALAC the group player encodes.</exception>
    public GroupPlayer CreateGroupPlayer(Func<DiscoveredReceiver, CancellationToken, Task<IReceiverSession>> sessionFactory, GroupPlayerOptions options, ILogger<GroupPlayer> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        if (Format.SampleRate != RealtimeStreamConstants.SampleRate || Format.FramesPerPacket != RealtimeStreamConstants.FramesPerPacket || Format.BitDepth != 16)
        {
            throw new InvalidOperationException($"The group player encodes ALAC 44100/16 in {RealtimeStreamConstants.FramesPerPacket}-frame packets; the transport announces {Format.Name}.");
        }

        if (options.LatencyFrames != LatencyFrames)
        {
            LogLatencyAligned(options.LatencyFrames, LatencyFrames);
            options = options with { LatencyFrames = LatencyFrames };
        }

        return new GroupPlayer(this, WrapSessionFactory(sessionFactory), options, logger);
    }

    /// <summary>
    /// Attaches the control session of <paramref name="receiverId"/> (the id <see cref="AddReceiver"/> uses): anchors and
    /// flushes for it go over this session, in order. Replaces an earlier attachment.
    /// </summary>
    public void AttachSession(string receiverId, IReceiverSession session)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        ArgumentNullException.ThrowIfNull(session);
        SessionLink? replaced;
        var link = new SessionLink(receiverId, session);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _links.TryGetValue(receiverId, out replaced);
            _links[receiverId] = link;
            var loop = RunControlLoopAsync(link);
            link.Stop.DisposeWhenDone(loop);
            Track(loop, "control loop of " + receiverId);
        }

        replaced?.Close();
        LogSessionAttached(receiverId);
    }

    /// <summary>Detaches the session of <paramref name="receiverId"/> (only when it is <paramref name="session"/>, if given); pending requests fail.</summary>
    public void DetachSession(string receiverId, IReceiverSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        SessionLink? link;
        lock (_gate)
        {
            if (!_links.TryGetValue(receiverId, out link) || (session is not null && !ReferenceEquals(link.Session, session)))
            {
                return;
            }

            _links.Remove(receiverId);
            _bufferSizes.Remove(receiverId);
        }

        link.Close();
    }

    /// <summary>
    /// Records the receiver's <c>audioBufferSize</c> from its stream SETUP answer (null: <see cref="BufferedStreamOptions.DefaultAudioBufferSize"/>);
    /// the run-ahead window is <see cref="BufferedStreamOptions.WindowFraction"/> of it.
    /// </summary>
    public void SetAudioBufferSize(string receiverId, long? audioBufferSize)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        var size = audioBufferSize is > 0 ? audioBufferSize.Value : Options.DefaultAudioBufferSize;
        lock (_gate)
        {
            _bufferSizes[receiverId] = size;
            if (Find(_receivers, receiverId) is { } receiver)
            {
                receiver.Window = WindowOf(size);
            }
        }

        LogBufferSize(receiverId, audioBufferSize, WindowOf(size));
    }

    /// <summary>
    /// Makes <paramref name="address"/> a unicast peer of the grandmaster (SPEC-audio.md 4.4); reference-counted, so
    /// several sessions on one host are fine.
    /// </summary>
    public void AddTimingPeer(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = PtpSessionWiring.Normalize(address);
        bool first;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var count = (_timingPeers.TryGetValue(address, out var c) ? c : 0) + 1;
            _timingPeers[address] = count;
            first = count == 1;
        }

        if (first)
        {
            Ptp.AddPeer(address);
            LogPtpPeer(address, "added");
        }
    }

    /// <summary>Undoes one <see cref="AddTimingPeer"/>; the peer leaves the grandmaster with its last user.</summary>
    public void RemoveTimingPeer(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = PtpSessionWiring.Normalize(address);
        bool last;
        lock (_gate)
        {
            if (!_timingPeers.TryGetValue(address, out var count))
            {
                return;
            }

            last = count <= 1;
            if (last)
            {
                _timingPeers.Remove(address);
            }
            else
            {
                _timingPeers[address] = count - 1;
            }
        }

        if (last)
        {
            Ptp.RemovePeer(address);
            LogPtpPeer(address, "removed");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="endpoint"/>'s <c>Data</c> is the receiver's TCP data port; the connection is opened in the
    /// background. The receiver is not anchored: the next <see cref="Send"/> anchors it. Replaces an earlier registration.
    /// </remarks>
    /// <exception cref="ArgumentException">The stream key is not 32 bytes.</exception>
    /// <exception cref="InvalidOperationException">The transport is not started.</exception>
    public void AddReceiver(string receiverId, ReceiverStreamEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpoint.Data);
        var receiver = new Receiver(receiverId, endpoint, new RtpAudioPacketBuilder(endpoint.StreamKey, Format.Ssrc));
        Receiver? replaced;
        lock (_gate)
        {
            if (_disposed || _mapping is null)
            {
                receiver.Builder.Dispose();
                ObjectDisposedException.ThrowIf(_disposed, this);
                throw new InvalidOperationException("Start the transport before adding receivers.");
            }

            receiver.Window = WindowOf(_bufferSizes.TryGetValue(receiverId, out var size) ? size : Options.DefaultAudioBufferSize);
            replaced = Find(_receivers, receiverId);
            _receivers = (replaced is null ? _receivers : _receivers.Remove(replaced)).Add(receiver);
            replaced?.Builder.Dispose();
            var writer = RunWriterAsync(receiver);
            receiver.Stop.DisposeWhenDone(writer);
            Track(writer, "data connection of " + receiverId);
        }

        replaced?.Close();
        LogAdded(receiverId, endpoint.Data, endpoint.OffsetFrames, Format.Name, receiver.Window);
    }

    /// <inheritdoc />
    /// <remarks>Closes the data connection (queued packets are dropped); the control session stays attached. Unknown ids are ignored.</remarks>
    public void RemoveReceiver(string receiverId)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        Receiver? receiver;
        lock (_gate)
        {
            receiver = Find(_receivers, receiverId);
            if (receiver is null)
            {
                return;
            }

            _receivers = _receivers.Remove(receiver);
            receiver.Builder.Dispose();
        }

        receiver.Close();
        LogRemoved(receiverId);
    }

    /// <inheritdoc />
    /// <remarks>The receiver is anchored again with the next packet. Unknown ids are ignored.</remarks>
    public void Resync(string receiverId)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        lock (_gate)
        {
            if (Find(_receivers, receiverId) is { } receiver)
            {
                receiver.Anchored = false;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Encrypts the frame per receiver into a length-prefixed packet with the 23-bit extension of
    /// <paramref name="sequence"/> and hands it to the receiver's writer; never blocks. A receiver that is not anchored first
    /// gets SETRATEANCHORTIME rate 1 (queued on its control loop): the explicit anchor of <see cref="AnchorAsync"/> when one
    /// is set, else <c>rtpTime = timestamp</c> at <c>ntpNow + L + offset_i</c>. A receiver whose window is full or whose
    /// data connection failed misses the packet (counted in <see cref="PacketsDropped"/>).
    /// </remarks>
    public void Send(ushort sequence, uint timestamp, ReadOnlySpan<byte> alacFrame, ulong ntpNow)
    {
        if (alacFrame.IsEmpty || alacFrame.Length > BufferedAudioPacket.MaxFrameSize)
        {
            throw new ArgumentException($"An ALAC frame of 1..{BufferedAudioPacket.MaxFrameSize} bytes is required.", nameof(alacFrame));
        }

        var length = BufferedAudioPacket.PacketLength(alacFrame.Length);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var mapping = _mapping ?? throw new InvalidOperationException("The transport is not started.");
            var wire = _haveSent ? WireSequenceLocked(sequence) : sequence;
            _haveSent = true;
            _lastSequence = sequence;
            _lastWireSequence = wire;
            _nextTimestamp = unchecked(timestamp + (uint)Format.FramesPerPacket);
            var now = Clock.NowNanos;
            foreach (var receiver in _receivers)
            {
                if (!receiver.Anchored)
                {
                    var (rtp, ptp) = _explicitAnchor ?? (timestamp, mapping.PtpOfNtp(ntpNow) + (ulong)FramesToNanos(Options.LatencyFrames));
                    AnchorLocked(receiver, rtp, ptp, awaited: false);
                }

                Release(receiver, now);
                if (receiver.Failed)
                {
                    Interlocked.Increment(ref _packetsDropped);
                    continue;
                }

                var batch = receiver.Batch;
                var queued = Interlocked.Read(ref batch.Bytes);
                if (receiver.BufferedBytes + length > receiver.Window || queued + length > receiver.Window)
                {
                    Interlocked.Increment(ref _packetsDropped);
                    if (!receiver.WindowFull)
                    {
                        receiver.WindowFull = true;
                        _events.Writer.TryWrite(new BufferedStreamEvent.WindowFull(receiver.Id, receiver.BufferedBytes, receiver.Window));
                        LogWindowFull(receiver.Id, receiver.BufferedBytes, queued, receiver.Window);
                    }

                    continue;
                }

                receiver.WindowFull = false;
                var buffer = ArrayPool<byte>.Shared.Rent(length);
                BufferedAudioPacket.Build(receiver.Builder, buffer, wire, timestamp, alacFrame);
                receiver.InFlight.Enqueue((timestamp, length));
                receiver.BufferedBytes += length;
                Interlocked.Add(ref batch.Bytes, length);
                if (receiver.Queue.Writer.TryWrite(new Outgoing(buffer, length, batch)))
                {
                    Interlocked.Increment(ref _packetsSent);
                }
                else
                {
                    Interlocked.Add(ref batch.Bytes, -length);
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The buffered stream has no sync packets: every receiver is re-anchored (SETRATEANCHORTIME rate 1, queued) with
    /// <c>rtpTime = nextTimestamp</c> at <c>ntpNow + L + offset_i</c>, and an explicit anchor is cleared.
    /// <paramref name="first"/> makes no difference.
    /// </remarks>
    public void SendSync(uint nextTimestamp, ulong ntpNow, bool first)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var mapping = _mapping ?? throw new InvalidOperationException("The transport is not started.");
            _explicitAnchor = null;
            var ptp = mapping.PtpOfNtp(ntpNow) + (ulong)FramesToNanos(Options.LatencyFrames);
            foreach (var receiver in _receivers)
            {
                AnchorLocked(receiver, nextTimestamp, ptp, awaited: false);
            }
        }
    }

    /// <summary>
    /// Sets an explicit anchor for the whole group: frame <paramref name="rtpTime"/> plays at
    /// <paramref name="ptpTimeNanos"/> on our PTP timeline (plus each receiver's offset). Every registered receiver gets
    /// SETRATEANCHORTIME rate 1 now, receivers added later get it with their first packet, until <see cref="PauseAsync"/>,
    /// <see cref="FlushAsync"/> or <see cref="SendSync"/> clears it. Completes when every request was answered; the first
    /// failure is rethrown (the others are logged). With <see cref="BufferedStreamOptions.AnchorAfterAudio"/> (default) a
    /// request goes out only after audio has been written to that receiver, so start sending before awaiting this task.
    /// </summary>
    public Task AnchorAsync(uint rtpTime, ulong ptpTimeNanos, CancellationToken cancellationToken)
    {
        var pending = new List<Task>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _explicitAnchor = (rtpTime, ptpTimeNanos);
            foreach (var receiver in _receivers)
            {
                if (AnchorLocked(receiver, rtpTime, ptpTimeNanos, awaited: true) is { } task)
                {
                    pending.Add(task);
                }
            }
        }

        return Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Pauses every receiver: SETRATEANCHORTIME rate 0 (SPEC-audio.md 4.5), the explicit anchor is cleared, the receivers
    /// keep what they buffered and are anchored again with the next packet (or by <see cref="AnchorAsync"/>).
    /// </summary>
    public Task PauseAsync(CancellationToken cancellationToken)
    {
        var pending = new List<Task>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _explicitAnchor = null;
            var now = Clock.PtpNowNanos;
            foreach (var receiver in _receivers)
            {
                Release(receiver, Clock.NowNanos);
                receiver.Anchored = false;
                receiver.AnchorKnown = false;
                if (_links.TryGetValue(receiver.Id, out var link))
                {
                    pending.Add(Enqueue(link, ControlAction.Rate(0, _haveSent ? _nextTimestamp : 0, now), awaited: true)!);
                }
            }
        }

        return Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Immediate flush of everything sent so far, all receivers (SPEC-audio.md 4.3, 4.5): optional rate 0, then
    /// <c>FLUSHBUFFERED {flushUntilSeq: next, flushUntilTS: next}</c>; queued packets are dropped and the receivers are
    /// anchored again with the next packet. A no-op before the first packet.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        var pending = new List<Task>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _explicitAnchor = null;
            if (!_haveSent)
            {
                return Task.CompletedTask;
            }

            var sequence = unchecked((ushort)(_lastSequence + 1));
            foreach (var receiver in _receivers)
            {
                if (_links.TryGetValue(receiver.Id, out var link))
                {
                    pending.Add(FlushLocked(link, receiver, sequence, _nextTimestamp));
                }
            }
        }

        return Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The immediate flush for one receiver, as the group player's <c>FLUSH</c> (pause, seek, skip; SPEC-audio.md 8.4) maps
    /// to the buffered stream (4.5): <c>SETRATEANCHORTIME rate 0</c> when <see cref="BufferedStreamOptions.PauseBeforeFlush"/>,
    /// then <c>FLUSHBUFFERED {flushUntilSeq, flushUntilTS}</c> with the 23-bit extension of
    /// <paramref name="nextSequence"/>; the receiver's queued packets are dropped and it is anchored again with the next
    /// packet. <paramref name="cancellationToken"/> bounds only the wait; the requests run to completion.
    /// </summary>
    /// <exception cref="InvalidOperationException">No session is attached for the receiver.</exception>
    /// <exception cref="TimeoutException">A request took longer than <see cref="BufferedStreamOptions.ControlTimeout"/>.</exception>
    public Task FlushReceiverAsync(string receiverId, ushort nextSequence, uint nextTimestamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        Task pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_links.TryGetValue(receiverId, out var link))
            {
                throw new InvalidOperationException($"No control session is attached for {receiverId}.");
            }

            pending = FlushLocked(link, Find(_receivers, receiverId), nextSequence, nextTimestamp);
        }

        return pending.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// The deferred flush (SPEC-audio.md 4.3; <c>[sps] rtsp.c:1367-1388</c>): when the packet <paramref name="fromSequence"/>
    /// arrives, the receivers discard up to but excluding <paramref name="untilSequence"/> without stopping playback; Apple
    /// senders splice tracks with it. Sequence numbers are the group's 16-bit values (extended to the wire's 23 bits).
    /// </summary>
    public Task FlushRangeAsync(ushort fromSequence, uint fromTimestamp, ushort untilSequence, uint untilTimestamp, CancellationToken cancellationToken)
    {
        var pending = new List<Task>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var from = _haveSent ? WireSequenceLocked(fromSequence) : fromSequence;
            var until = _haveSent ? WireSequenceLocked(untilSequence) : untilSequence;
            foreach (var receiver in _receivers)
            {
                if (_links.TryGetValue(receiver.Id, out var link))
                {
                    pending.Add(Enqueue(link, ControlAction.Flush(until, untilTimestamp, from, fromTimestamp), awaited: true)!);
                }
            }
        }

        return Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    /// <summary>The 23-bit wire sequence number of the group sequence <paramref name="sequence"/> (near the last one sent).</summary>
    public uint WireSequence(ushort sequence)
    {
        lock (_gate)
        {
            return _haveSent ? WireSequenceLocked(sequence) : sequence;
        }
    }

    /// <summary>
    /// Run-ahead flow control (SPEC-audio.md 4.2): completes when every receiver whose data connection is alive can take
    /// <paramref name="bytes"/> more without exceeding its window. Space frees as the anchored receivers play; a receiver
    /// that is not anchored frees nothing, so a producer that pushes ahead must anchor (<see cref="AnchorAsync"/>) before it
    /// fills the window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> exceeds a receiver's whole window.</exception>
    public async ValueTask WaitForWindowAsync(int bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        while (true)
        {
            long waitNanos = 0;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = Clock.NowNanos;
                foreach (var receiver in _receivers)
                {
                    if (receiver.Failed)
                    {
                        continue;
                    }

                    ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, receiver.Window);
                    Release(receiver, now);
                    var queued = Interlocked.Read(ref receiver.Batch.Bytes);
                    if (receiver.BufferedBytes + bytes <= receiver.Window && queued + bytes <= receiver.Window)
                    {
                        continue;
                    }

                    var wait = 20_000_000L; // nothing plays yet, or the socket is slow: poll
                    if (receiver.AnchorKnown && receiver.BufferedBytes + bytes > receiver.Window && receiver.InFlight.TryPeek(out var head))
                    {
                        wait = Math.Max(1_000_000L, PlayTime(receiver, head.Timestamp) - now);
                    }

                    waitNanos = Math.Max(waitNanos, wait);
                }
            }

            if (waitNanos <= 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromTicks(Math.Min(waitNanos, 100_000_000L) / 100), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>Closes every data connection and control loop and releases the PTP peers; the shared clock, control port and grandmaster stay up.</remarks>
    public async ValueTask DisposeAsync()
    {
        ImmutableArray<Receiver> receivers;
        SessionLink[] links;
        IPAddress[] peers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            receivers = _receivers;
            _receivers = ImmutableArray<Receiver>.Empty;
            links = [.. _links.Values];
            _links.Clear();
            peers = [.. _timingPeers.Keys];
            _timingPeers.Clear();
            foreach (var receiver in receivers)
            {
                receiver.Builder.Dispose();
            }
        }

        foreach (var receiver in receivers)
        {
            receiver.Close();
        }

        foreach (var link in links)
        {
            link.Close();
        }

        foreach (var peer in peers)
        {
            Ptp.RemovePeer(peer);
        }

        for (var round = 0; round < 4 && !_background.IsEmpty; round++)
        {
            await AwaitQuietlyAsync(Task.WhenAll(_background.Keys.ToArray())).ConfigureAwait(false);
        }

        if (_starting is { } starting)
        {
            await AwaitQuietlyAsync(starting).ConfigureAwait(false);
        }

        _events.Writer.TryComplete();
        LogDisposed(PacketsSent, BytesWritten, PacketsDropped, AnchorsSent, FlushesSent);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Control.StartAsync(cancellationToken).ConfigureAwait(false);
        await Ptp.StartAsync(cancellationToken).ConfigureAwait(false);
        var mapping = new ClockMapping(Clock);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _mapping ??= mapping;
            _ptpMinusMonotonic = unchecked((long)_mapping.PtpAt(0));
        }

        LogStarted(Format.Name, Format.Ssrc, Control.Port, LatencyFrames, Ptp.ClockId);
    }

    /// <summary>Queues SETRATEANCHORTIME rate 1 for a receiver (offset applied) and updates its play-time estimate; under <see cref="_gate"/>.</summary>
    private Task? AnchorLocked(Receiver receiver, uint rtpTime, ulong ptpNanos, bool awaited)
    {
        if (!_links.TryGetValue(receiver.Id, out var link))
        {
            // Nothing is sent, so the receiver stays unanchored: the next packet after AttachSession anchors it.
            if (!receiver.WarnedNoSession)
            {
                receiver.WarnedNoSession = true;
                LogNoSession(receiver.Id);
            }

            return awaited ? Task.FromException(new InvalidOperationException($"No control session is attached for {receiver.Id}.")) : null;
        }

        var networkTime = unchecked(ptpNanos + (ulong)FramesToNanos(receiver.Endpoint.OffsetFrames));
        receiver.Anchored = true;
        receiver.AnchorKnown = true;
        receiver.AnchorRtp = rtpTime;
        receiver.AnchorMonotonic = unchecked((long)networkTime - _ptpMinusMonotonic);
        var action = ControlAction.Rate(1, rtpTime, networkTime);
        if (Options.AnchorAfterAudio)
        {
            action = action with { AfterAudioOf = receiver, WrittenMark = Interlocked.Read(ref receiver.WrittenPackets) };
        }

        return Enqueue(link, action, awaited);
    }

    /// <summary>
    /// Optional rate 0, then the immediate FLUSHBUFFERED; drops the receiver's queued packets (a new queue batch: the writer
    /// discards the old one unwritten and it no longer counts against the window); under <see cref="_gate"/>.
    /// </summary>
    private Task FlushLocked(SessionLink link, Receiver? receiver, ushort nextSequence, uint nextTimestamp)
    {
        var until = _haveSent ? WireSequenceLocked(nextSequence) : nextSequence;
        if (receiver is not null)
        {
            Volatile.Write(ref receiver.Batch, new QueueBatch());
            receiver.Anchored = false;
            receiver.AnchorKnown = false;
            receiver.InFlight.Clear();
            receiver.BufferedBytes = 0;
            receiver.WindowFull = false;
        }

        Task? pause = null;
        if (Options.PauseBeforeFlush)
        {
            pause = Enqueue(link, ControlAction.Rate(0, nextTimestamp, Clock.PtpNowNanos), awaited: true);
        }

        var flush = Enqueue(link, ControlAction.Flush(until, nextTimestamp, null, null), awaited: true)!;
        return pause is null ? flush : Task.WhenAll(pause, flush);
    }

    private Task? Enqueue(SessionLink link, ControlAction action, bool awaited)
    {
        if (awaited)
        {
            action = action with { Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        }

        if (!link.Actions.Writer.TryWrite(action))
        {
            var failure = new ObjectDisposedException(nameof(BufferedStreamTransport), $"The control session of {link.ReceiverId} is closed.");
            return awaited ? Task.FromException(failure) : null;
        }

        return action.Done?.Task;
    }

    private async Task RunControlLoopAsync(SessionLink link)
    {
        await Task.Yield();
        var reader = link.Actions.Reader;
        try
        {
            while (await reader.WaitToReadAsync(link.Stop.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var action))
                {
                    await ExecuteAsync(link, action).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (link.Stop.IsCancellationRequested)
        {
            // detached or disposed
        }
        finally
        {
            // However the loop ended, nothing may wait on it: refuse new work, forget the link, fail what is queued.
            link.Close();
            lock (_gate)
            {
                if (_links.TryGetValue(link.ReceiverId, out var current) && ReferenceEquals(current, link))
                {
                    _links.Remove(link.ReceiverId);
                }
            }

            while (reader.TryRead(out var left))
            {
                left.Done?.TrySetException(new ObjectDisposedException(nameof(BufferedStreamTransport), $"The control session of {link.ReceiverId} was detached."));
            }
        }
    }

    /// <summary>Runs one action; never throws: every outcome completes <see cref="ControlAction.Done"/>.</summary>
    private async Task ExecuteAsync(SessionLink link, ControlAction action)
    {
        var name = action.Kind == ControlKind.Rate ? "SETRATEANCHORTIME" : "FLUSHBUFFERED";
        try
        {
            if (action.AfterAudioOf is { } audio && !await WaitForAudioAsync(audio, action.WrittenMark, link.Stop.Token).ConfigureAwait(false))
            {
                LogAnchorWithoutAudio(link.ReceiverId, Options.AnchorAudioWait.TotalSeconds);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(link.Stop.Token);
            timeout.CancelAfter(Options.ControlTimeout);
            if (action.Kind == ControlKind.Rate)
            {
                var (seconds, fraction) = NetworkTime.FromNanoseconds(action.NetworkTimeNanos);
                await link.Session.SetRateAnchorTimeAsync(action.RateValue, action.RtpTime, seconds, fraction, Ptp.ClockId, timeout.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _anchorsSent);
                _events.Writer.TryWrite(new BufferedStreamEvent.Anchored(link.ReceiverId, action.RateValue, action.RtpTime, action.NetworkTimeNanos));
                LogAnchor(link.ReceiverId, action.RateValue, action.RtpTime, action.NetworkTimeNanos);
            }
            else
            {
                await link.Session.FlushBufferedAsync(action.UntilSequence, action.RtpTime, action.FromSequence, action.FromTimestamp, timeout.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _flushesSent);
                _events.Writer.TryWrite(new BufferedStreamEvent.Flushed(link.ReceiverId, action.UntilSequence, action.RtpTime, action.FromSequence));
                LogFlush(link.ReceiverId, action.UntilSequence, action.RtpTime, action.FromSequence);
            }

            action.Done?.TrySetResult();
        }
        catch (OperationCanceledException) when (link.Stop.IsCancellationRequested)
        {
            action.Done?.TrySetException(new ObjectDisposedException(nameof(BufferedStreamTransport), $"The control session of {link.ReceiverId} was detached."));
        }
        catch (OperationCanceledException)
        {
            OnControlFailed(link, action, name, new TimeoutException($"{name} to {link.ReceiverId} was not answered within {Options.ControlTimeout.TotalSeconds:0.#} s."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // IOException, RtspStatusException, a closed session's ObjectDisposedException, or anything else the session
            // throws: the caller gets it, the loop goes on with the next request.
            OnControlFailed(link, action, name, ex);
        }
    }

    private void OnControlFailed(SessionLink link, ControlAction action, string name, Exception ex)
    {
        Interlocked.Increment(ref _controlErrors);
        _events.Writer.TryWrite(new BufferedStreamEvent.ControlFailed(link.ReceiverId, name, ex.Message));
        LogControlFailed(name, link.ReceiverId, ex.Message);
        action.Done?.TrySetException(ex);
    }

    private async Task RunWriterAsync(Receiver receiver)
    {
        await Task.Yield();
        Socket? socket = null;
        var reader = receiver.Queue.Reader;
        try
        {
            socket = await ConnectAsync(receiver, receiver.Stop.Token).ConfigureAwait(false);
            receiver.Connected = true;
            _events.Writer.TryWrite(new BufferedStreamEvent.DataConnected(receiver.Id, receiver.Endpoint.Data));
            LogDataConnected(receiver.Id, receiver.Endpoint.Data);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var buffer = new byte[CoalesceBytes];
            while (await reader.WaitToReadAsync(receiver.Stop.Token).ConfigureAwait(false))
            {
                var used = 0;
                var count = 0;
                while (reader.TryRead(out var packet))
                {
                    try
                    {
                        if (!ReferenceEquals(packet.Batch, Volatile.Read(ref receiver.Batch)))
                        {
                            // Before an immediate flush: the receiver would discard it anyway (SPEC-audio.md 4.3).
                            Interlocked.Increment(ref _packetsDiscarded);
                            continue;
                        }

                        if (used + packet.Length > buffer.Length)
                        {
                            await WriteAsync(stream, receiver, buffer.AsMemory(0, used), count).ConfigureAwait(false);
                            used = 0;
                            count = 0;
                        }

                        packet.Buffer.AsSpan(0, packet.Length).CopyTo(buffer.AsSpan(used));
                        used += packet.Length;
                        count++;
                    }
                    finally
                    {
                        Interlocked.Add(ref packet.Batch.Bytes, -packet.Length);
                        ArrayPool<byte>.Shared.Return(packet.Buffer);
                    }
                }

                if (used > 0)
                {
                    await WriteAsync(stream, receiver, buffer.AsMemory(0, used), count).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (receiver.Stop.IsCancellationRequested)
        {
            // removed or disposed
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ObjectDisposedException && receiver.Stop.IsCancellationRequested)
        {
            // the socket went down because the receiver was removed
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ObjectDisposedException)
        {
            receiver.Failed = true;
            _events.Writer.TryWrite(new BufferedStreamEvent.DataClosed(receiver.Id, ex.Message));
            LogDataFailed(receiver.Id, receiver.Endpoint.Data, ex.Message);
        }
        finally
        {
            receiver.Connected = false;
            receiver.Close(); // a failed connection takes no more packets
            while (reader.TryRead(out var left))
            {
                Interlocked.Add(ref left.Batch.Bytes, -left.Length);
                ArrayPool<byte>.Shared.Return(left.Buffer);
            }

            if (socket is not null)
            {
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException)
                {
                    // already reset by the receiver
                }

                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// Waits until the receiver's writer has written a packet after <paramref name="mark"/> (see
    /// <see cref="BufferedStreamOptions.AnchorAfterAudio"/>); false after <see cref="BufferedStreamOptions.AnchorAudioWait"/>
    /// or when the data connection is gone. Cancellation (detach, dispose) propagates.
    /// </summary>
    private async Task<bool> WaitForAudioAsync(Receiver receiver, long mark, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)Options.AnchorAudioWait.TotalMilliseconds;
        while (true)
        {
            var signal = Volatile.Read(ref receiver.WrittenSignal).Task;
            if (Interlocked.Read(ref receiver.WrittenPackets) > mark)
            {
                return true;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0 || receiver.Failed || receiver.Stop.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await signal.WaitAsync(TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private async ValueTask WriteAsync(NetworkStream stream, Receiver receiver, ReadOnlyMemory<byte> data, int packets)
    {
        await stream.WriteAsync(data, receiver.Stop.Token).ConfigureAwait(false);
        Interlocked.Add(ref _bytesWritten, data.Length);
        Interlocked.Add(ref receiver.WrittenPackets, packets);
        Interlocked.Exchange(ref receiver.WrittenSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private async Task<Socket> ConnectAsync(Receiver receiver, CancellationToken cancellationToken)
    {
        var target = receiver.Endpoint.Data;
        if (target.Address.IsIPv4MappedToIPv6)
        {
            target = new IPEndPoint(target.Address.MapToIPv4(), target.Port);
        }

        for (var attempt = 1; ; attempt++)
        {
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, SendBufferSize = Options.SendBufferSize };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Options.ConnectTimeout);
            Exception failure;
            try
            {
                await socket.ConnectAsync(target, timeout.Token).ConfigureAwait(false);
                return socket;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                failure = new TimeoutException($"Connecting to {target} timed out after {Options.ConnectTimeout.TotalSeconds:0.#} s.");
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                failure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            if (attempt >= Options.ConnectAttempts)
            {
                throw failure is TimeoutException ? failure : new IOException($"Connecting to {target} failed after {attempt} attempts: {failure.Message}", failure);
            }

            LogConnectRetry(receiver.Id, target, attempt, failure.Message);
            await Task.Delay(Options.ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Frees the bytes whose play time has passed; under <see cref="_gate"/>.</summary>
    private void Release(Receiver receiver, long now)
    {
        if (!receiver.AnchorKnown)
        {
            return;
        }

        while (receiver.InFlight.TryPeek(out var head) && PlayTime(receiver, head.Timestamp) <= now)
        {
            receiver.InFlight.Dequeue();
            receiver.BufferedBytes -= head.Bytes;
        }
    }

    private long PlayTime(Receiver receiver, uint timestamp) =>
        receiver.AnchorMonotonic + FramesToNanos(unchecked((int)(timestamp - receiver.AnchorRtp)));

    private long FramesToNanos(long frames) => frames * NanosPerSecond / Format.SampleRate;

    private long WindowOf(long audioBufferSize) => Math.Max(MinimumWindow, (long)(audioBufferSize * Options.WindowFraction));

    private uint WireSequenceLocked(ushort sequence) =>
        unchecked((_lastWireSequence + (uint)(short)(sequence - _lastSequence)) & BufferedAudioPacket.SequenceMask);

    private void Track(Task task, string what)
    {
        _background[task] = what;
        _ = task.ContinueWith(
            static (t, state) =>
            {
                var (self, name) = ((BufferedStreamTransport, string))state!;
                self._background.TryRemove(t, out _);
                if (t.IsFaulted)
                {
                    self.LogBackgroundFailed(t.Exception!, name);
                }
            },
            (this, what),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

    private static Receiver? Find(ImmutableArray<Receiver> receivers, string id)
    {
        foreach (var receiver in receivers)
        {
            if (string.Equals(receiver.Id, id, StringComparison.Ordinal))
            {
                return receiver;
            }
        }

        return null;
    }

    private enum ControlKind
    {
        Rate,
        Flush,
    }

    /// <summary>A packet handed to a writer: a pooled buffer and the queue batch (flush generation) it belongs to.</summary>
    private readonly record struct Outgoing(byte[] Buffer, int Length, QueueBatch Batch);

    /// <summary>The packets queued between two immediate flushes; only the current batch counts against the window.</summary>
    private sealed class QueueBatch
    {
        /// <summary>Bytes of this batch not yet written or discarded (Interlocked: Send adds, the writer subtracts).</summary>
        public long Bytes;
    }

    /// <summary>
    /// The stop signal of a writer or control loop: cancelled once by <see cref="Cancel"/> (from any thread, idempotent)
    /// and disposed once the task that uses its token has ended.
    /// </summary>
    private sealed class StopSignal
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _source = new();
        private bool _cancelled;
        private bool _disposed;

        public StopSignal()
        {
            Token = _source.Token;
        }

        public CancellationToken Token { get; }

        public bool IsCancellationRequested => Token.IsCancellationRequested;

        public void Cancel()
        {
            lock (_gate)
            {
                if (_cancelled || _disposed)
                {
                    return;
                }

                _cancelled = true;
                _source.Cancel();
            }
        }

        /// <summary>Disposes the source after <paramref name="user"/> has ended (on the thread pool, never inline in a <see cref="Cancel"/>).</summary>
        public void DisposeWhenDone(Task user) =>
            _ = user.ContinueWith(
                static (_, state) => ((StopSignal)state!).Dispose(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

        private void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _source.Dispose();
            }
        }
    }

    /// <summary>One SETRATEANCHORTIME or FLUSHBUFFERED for a receiver's control loop.</summary>
    private sealed record ControlAction(ControlKind Kind, int RateValue, uint RtpTime, ulong NetworkTimeNanos, uint UntilSequence, uint? FromSequence, uint? FromTimestamp)
    {
        public TaskCompletionSource? Done { get; init; }

        /// <summary>For an anchor: the receiver whose audio must be written first (<see cref="BufferedStreamOptions.AnchorAfterAudio"/>).</summary>
        public Receiver? AfterAudioOf { get; init; }

        /// <summary>The receiver's written-packet count when the anchor was set; the anchor waits until it grows.</summary>
        public long WrittenMark { get; init; }

        public static ControlAction Rate(int rate, uint rtpTime, ulong networkTimeNanos) => new(ControlKind.Rate, rate, rtpTime, networkTimeNanos, 0, null, null);

        public static ControlAction Flush(uint untilSequence, uint untilTimestamp, uint? fromSequence, uint? fromTimestamp) =>
            new(ControlKind.Flush, 0, untilTimestamp, 0, untilSequence, fromSequence, fromTimestamp);
    }

    /// <summary>A receiver's control session and the loop that sends its requests in order.</summary>
    private sealed class SessionLink(string receiverId, IReceiverSession session)
    {
        public string ReceiverId { get; } = receiverId;

        public IReceiverSession Session { get; } = session;

        public Channel<ControlAction> Actions { get; } = Channel.CreateUnbounded<ControlAction>(new UnboundedChannelOptions { SingleReader = true });

        public StopSignal Stop { get; } = new();

        /// <summary>Refuses new requests and stops the loop; idempotent.</summary>
        public void Close()
        {
            Actions.Writer.TryComplete();
            Stop.Cancel();
        }
    }

    /// <summary>One receiver's data connection and run-ahead bookkeeping (fields guarded by <see cref="_gate"/> unless noted).</summary>
    private sealed class Receiver(string id, ReceiverStreamEndpoint endpoint, RtpAudioPacketBuilder builder)
    {
        /// <summary>The current queue batch (replaced under the gate by an immediate flush, Volatile read by the writer).</summary>
        public QueueBatch Batch = new();

        /// <summary>Packets written to the socket (Interlocked: the writer adds, anchors read).</summary>
        public long WrittenPackets;

        /// <summary>Completed and replaced by the writer after every socket write (anchors wait on it).</summary>
        public TaskCompletionSource WrittenSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        public ReceiverStreamEndpoint Endpoint { get; } = endpoint;

        public RtpAudioPacketBuilder Builder { get; } = builder;

        public Channel<Outgoing> Queue { get; } = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public StopSignal Stop { get; } = new();

        public Queue<(uint Timestamp, int Bytes)> InFlight { get; } = new();

        public long BufferedBytes { get; set; }

        public long Window { get; set; }

        /// <summary>A rate-1 anchor was issued since the last flush/pause/resync.</summary>
        public bool Anchored { get; set; }

        /// <summary>The play-time estimate has an anchor (stays through a resync, cleared by flush and pause).</summary>
        public bool AnchorKnown { get; set; }

        public uint AnchorRtp { get; set; }

        public long AnchorMonotonic { get; set; }

        public bool WindowFull { get; set; }

        public bool WarnedNoSession { get; set; }

        /// <summary>Written by the writer task, read anywhere.</summary>
        public volatile bool Connected;

        /// <summary>Written by the writer task, read anywhere.</summary>
        public volatile bool Failed;

        /// <summary>Takes no more packets and stops the writer; idempotent.</summary>
        public void Close()
        {
            Queue.Writer.TryComplete();
            Stop.Cancel();
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Buffered transport started: {Format} (SSRC 0x{Ssrc:X8}), control port {ControlPort}, L = {Latency} frames, PTP clock {ClockId:X16}")]
    private partial void LogStarted(string format, uint ssrc, int controlPort, int latency, ulong clockId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Receiver {Receiver} added: TCP data {Data}, offset {Offset} frames, {Format}, window {Window} bytes")]
    private partial void LogAdded(string receiver, IPEndPoint data, int offset, string format, long window);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Receiver {Receiver} removed")]
    private partial void LogRemoved(string receiver);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Buffered data connection to {Receiver} at {Data} is up")]
    private partial void LogDataConnected(string receiver, IPEndPoint data);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Buffered data connection to {Receiver} at {Data} failed: {Error}; its audio is dropped")]
    private partial void LogDataFailed(string receiver, IPEndPoint data, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Connecting to {Receiver} at {Data} failed (attempt {Attempt}): {Error}; retrying")]
    private partial void LogConnectRetry(string receiver, IPEndPoint data, int attempt, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "SETRATEANCHORTIME to {Receiver} answered: rate {Rate}, rtpTime {RtpTime}, network time {NetworkTime} ns")]
    private partial void LogAnchor(string receiver, int rate, uint rtpTime, ulong networkTime);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "FLUSHBUFFERED to {Receiver} answered: until seq {Until} ts {Timestamp}, from seq {From}")]
    private partial void LogFlush(string receiver, uint until, uint timestamp, uint? from);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "{Request} to {Receiver} failed: {Error}")]
    private partial void LogControlFailed(string request, string receiver, string error);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Run-ahead window of {Receiver} is full ({Buffered} bytes buffered, {Queued} unwritten, window {Window}); dropping audio until it drains")]
    private partial void LogWindowFull(string receiver, long buffered, long queued, long window);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "No control session is attached for {Receiver}: it cannot be anchored and will not play (AttachSession / WrapSessionFactory)")]
    private partial void LogNoSession(string receiver);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "Control session of {Receiver} attached")]
    private partial void LogSessionAttached(string receiver);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "audioBufferSize of {Receiver}: {Size} bytes (null: default); run-ahead window {Window} bytes")]
    private partial void LogBufferSize(string receiver, long? size, long window);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "PTP peer {Address} {Action}")]
    private partial void LogPtpPeer(IPAddress address, string action);

    [LoggerMessage(EventId = 15, Level = LogLevel.Error, Message = "Background task '{What}' failed")]
    private partial void LogBackgroundFailed(Exception exception, string what);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug, Message = "No audio reached {Receiver} within {Seconds} s of its anchor; sending SETRATEANCHORTIME anyway")]
    private partial void LogAnchorWithoutAudio(string receiver, double seconds);

    [LoggerMessage(EventId = 18, Level = LogLevel.Warning, Message = "GroupPlayerOptions.LatencyFrames {Options} differs from the buffered transport's L {Transport}; using the transport's")]
    private partial void LogLatencyAligned(int options, int transport);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "Buffered transport disposed: {Packets} packets, {Bytes} bytes written, {Dropped} dropped, {Anchors} anchors, {Flushes} flushes")]
    private partial void LogDisposed(long packets, long bytes, long dropped, long anchors, long flushes);
}
