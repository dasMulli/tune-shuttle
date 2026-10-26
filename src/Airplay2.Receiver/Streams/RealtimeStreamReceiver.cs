using System.Net;
using System.Net.Sockets;
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
/// The receive path of a realtime (type 96) stream (SPEC-receiver.md 5.1, SPEC-audio.md 1-2): a UDP data port that
/// decrypts each RTP packet with the stream's <c>shk</c> (ChaCha20-Poly1305) and ALAC-decodes its 352 frames, tracks the
/// 16-bit sequence and asks for gaps with 0xD5 requests to the sender's control port, and a UDP control port that reports
/// 0xD4/0xD7 syncs and accepts wrapped 0xD6 replies (raw retransmissions arrive on the data port). The ports are
/// receiver-wide and serve the most recent stream (<see cref="BeginStream"/>). Promoted from the fake receiver's audio
/// path (SPEC-receiver.md R2); playout timing (R6) is not done here.
/// </summary>
public sealed class RealtimeStreamReceiver : IAsyncDisposable
{
    /// <summary>Datagrams of this size or less are dropped, as shairport does [sps:rtp.c:1803]: an RTP packet carries at least one payload byte.</summary>
    private const int MinimumAudioPacket = RealtimeStreamConstants.HeaderSize + RealtimeStreamConstants.TrailerSize;

    private readonly RealtimeStreamReceiverOptions _options;
    private readonly IRealtimeStreamObserver _observer;
    private readonly ILogger _logger;
    private readonly IMasterClock _clock;

    // Decode buffers, used under _audioLock only: one packet at a time, no allocation per packet.
    private readonly Lock _audioLock = new();
    private readonly byte[] _alacBuffer = new byte[4096];
    private readonly short[] _pcmBuffer = new short[RealtimeStreamConstants.FramesPerPacket * 2];
    private UdpReceiveLoop? _dataLoop;
    private UdpReceiveLoop? _controlLoop;
    private UdpReceiveLoop? _secondControlLoop;
    private StreamState? _stream;
    private int _started;
    private int _disposed;

    /// <summary>Creates the receiver; nothing is bound until <see cref="Start"/>.</summary>
    /// <param name="options">Options.</param>
    /// <param name="observer">Receives the decoded packets and what else happens on the ports.</param>
    /// <param name="logger">Logger.</param>
    public RealtimeStreamReceiver(RealtimeStreamReceiverOptions options, IRealtimeStreamObserver observer, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(observer);
        _options = options;
        _observer = observer;
        _logger = logger ?? NullLogger.Instance;
        _clock = options.Clock ?? new MasterClock();
    }

    /// <summary>The ALAC decoder of the data port (not thread-safe: used under the audio lock; do not decode with it while a stream runs).</summary>
    public AlacDecoder Decoder { get; } = new(AlacMagicCookie.Default44100Stereo16);

    /// <summary>The UDP data port announced in stream SETUP answers (0 before <see cref="Start"/>).</summary>
    public int DataPort => _dataLoop?.Port ?? 0;

    /// <summary>The UDP control port announced in stream SETUP answers (0 before <see cref="Start"/>).</summary>
    public int ControlPort => _controlLoop?.Port ?? 0;

    /// <summary>Binds the data and control ports (and the second control socket when configured) on port 0.</summary>
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
    }

    /// <summary>
    /// Makes a stream SETUP the active stream (SPEC-receiver.md 4.6), replacing any previous one.
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
    /// FLUSH of <paramref name="owner"/>'s stream (SPEC-receiver.md 4.5): forgets outstanding retransmit requests; the
    /// next packet is expected at <paramref name="nextSequence"/>, or re-anchors on arrival when it is null.
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
        }
    }

    /// <summary>Ends the active stream and closes the sockets.</summary>
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

        foreach (var loop in new[] { _dataLoop, _controlLoop, _secondControlLoop })
        {
            if (loop is not null)
            {
                await loop.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

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
        return request is { } r ? SendRetransmitRequestAsync(stream, r, stop) : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The control port (and the second socket): 0xD4 / 0xD7 syncs are reported, 0xD6 replies unwrapped and decoded,
    /// anything else logged and ignored (SPEC-audio.md 2.4). Like shairport, the primary port learns the sender's control
    /// address from the first sync and sends retransmit requests there [sps:rtp.c:1672-1677].
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
                _observer.OnPtpSync(ptp);
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

    /// <summary>Gap tracking, simulated loss and decoding of one packet; returns the retransmit request to send, if any.</summary>
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

            var answered = stream.Outstanding.Remove(sequence);
            if (wrapped && !answered)
            {
                _logger.LogDebug("Receiver {Name}: unrequested or duplicate retransmission of {Sequence}", _options.Name, sequence);
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

                request = TrackSequence(stream, sequence, marker);
            }

            Decode(stream, packet, sequence, timestamp, marker);
            if (answered)
            {
                _observer.OnRetransmitAnswered(sequence, wrapped);
            }
        }

        return request;
    }

    /// <summary>Advances the expected sequence (wrap-safe); a jump forward within the history window is a gap to request.</summary>
    private RetransmitRequest? TrackSequence(StreamState stream, ushort sequence, bool marker)
    {
        if (marker || !stream.HaveExpected)
        {
            // First packet, or a re-anchor after a join/flush (marker bit, SPEC-audio.md 1.2): no gap.
            stream.HaveExpected = true;
            stream.Expected = unchecked((ushort)(sequence + 1));
            return null;
        }

        var gap = unchecked((short)(sequence - stream.Expected));
        if (gap == 0)
        {
            stream.Expected = unchecked((ushort)(sequence + 1));
            return null;
        }

        if (gap < 0)
        {
            _logger.LogDebug("Receiver {Name}: late or duplicate packet {Sequence} (expected {Expected})", _options.Name, sequence, stream.Expected);
            return null;
        }

        var first = stream.Expected;
        stream.Expected = unchecked((ushort)(sequence + 1));
        if (!_options.RequestRetransmits)
        {
            return null;
        }

        if (gap >= RealtimeStreamConstants.HistoryPackets)
        {
            // Beyond the sender's history (SPEC-audio.md 2.4): resynchronise instead of asking.
            _logger.LogWarning("Receiver {Name}: {Gap} packets missing before {Sequence}; resynchronising", _options.Name, gap, sequence);
            return null;
        }

        for (var i = 0; i < gap; i++)
        {
            stream.Outstanding.Add(unchecked((ushort)(first + i)));
        }

        return new RetransmitRequest(first, (ushort)gap);
    }

    private void Decode(StreamState stream, ReadOnlySpan<byte> packet, ushort sequence, uint timestamp, bool marker)
    {
        if (stream.Key is not { } key)
        {
            _observer.OnBadPacket(sequence, packet.Length, "no usable shk");
            return;
        }

        if (!RtpAudioPacketBuilder.TryDecrypt(key, packet, _alacBuffer, out var length))
        {
            _logger.LogWarning("Receiver {Name}: packet {Sequence} failed authentication", _options.Name, sequence);
            _observer.OnBadPacket(sequence, packet.Length, "authentication failed");
            return;
        }

        var bits = new BitBuffer(_alacBuffer, length);
        var frames = Decoder.Decode(ref bits, _pcmBuffer, (int)Decoder.Config.FrameLength, 2);
        if (frames < 0)
        {
            _logger.LogWarning("Receiver {Name}: packet {Sequence} is not a valid ALAC frame", _options.Name, sequence);
            _observer.OnBadPacket(sequence, packet.Length, "ALAC decode failed");
            return;
        }

        _observer.OnAudioPacket(sequence, timestamp, marker, _pcmBuffer.AsSpan(0, frames * 2));
    }

    /// <summary>Sends a 0xD5 request (8- or 16-byte form) from the control port or the second socket to the sender's control address.</summary>
    private async ValueTask SendRetransmitRequestAsync(StreamState stream, RetransmitRequest request, CancellationToken stop)
    {
        var socket = (_options.RetransmitFromSecondSocket ? _secondControlLoop : _controlLoop)?.Socket;
        if (socket is null)
        {
            return;
        }

        var buffer = new byte[RetransmitRequest.ExtendedSize];
        var length = request.Write(buffer, _options.ExtendedRetransmitRequests ? _clock.NtpNow : null);
        IPEndPoint target;
        lock (_audioLock)
        {
            target = stream.SenderControl;
        }

        // Reported before sending so the answer can never precede the request.
        _observer.OnRetransmitRequested(request.FirstSequence, request.Count);
        await socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, target, stop).ConfigureAwait(false);
    }

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

        public HashSet<ushort> Outstanding { get; } = [];
    }
}
