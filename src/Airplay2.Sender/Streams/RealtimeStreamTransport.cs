using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Sender.Streams;

/// <summary>
/// Type 96 over UDP (SPEC-audio.md 1-3). Timing is pluggable (SPEC.md D1): with <see cref="Ptp"/> null the session is
/// NTP-timed (0xD4 syncs, the timing responder answers 0xD2); with an <see cref="IPtpMaster"/> the session is PTP-timed
/// and the syncs are the 28-byte 0xD7 <see cref="PtpSyncPacket"/> carrying the PTP time of the sync instant and the
/// master's clock id (SPEC-audio.md 4.4). Probe P1 (wave 3) settled NTP for the Test Speaker ABCD; the choice is this
/// constructor argument, not a code path.
/// </summary>
/// <remarks>
/// <para>
/// Per receiver (SPEC-audio.md 3.1, SPEC.md D3): its own <see cref="RtpAudioPacketBuilder"/> (key <c>shk</c>, 64-bit
/// nonce counter), its own <see cref="PacketHistory"/> registered with the shared <see cref="IControlPort"/> (0xD5
/// answered from it, SPEC.md D8), its data/control endpoints, its <c>offset_frames</c>, and a state:
/// <b>CONNECTED</b> after <see cref="AddReceiver"/> or <see cref="Resync"/>, <b>STREAMING</b> once it got its initial sync
/// and marker packet. The plaintext ALAC frame is shared; only the encryption differs.
/// </para>
/// <para>
/// Sync cadence (SPEC-audio.md 2.3, 3.1; SPEC.md D11): in every <see cref="Send"/>, a receiver in CONNECTED first gets an
/// initial sync with byte 0 = <c>0x90</c> and then the packet with the RTP marker bit; every 44 100 frames of timeline
/// every STREAMING receiver gets a periodic <c>0x80</c> sync. A sync describes the instant the packet being sent is due:
/// <c>now_frame = ts - L - offset_i</c> at <c>T</c>, <c>next_frame = ts</c>. The syncs leave from the control port's
/// socket, which is where receivers learn our control address from (SPEC-audio.md 1.6). Audio leaves from this
/// transport's own UDP socket. The flags field is 7 (A-Q9; <see cref="SyncFlagsOverride"/> for probe P7).
/// </para>
/// <para>
/// Threading: <see cref="Send"/> and <see cref="SendSync"/> run on the group player's pacer thread and never block on
/// I/O (UDP sends complete synchronously; a sync send that does not is observed by a continuation). Add, remove and
/// resync come from async control code; one short lock serialises them with the pacer.
/// </para>
/// <para>
/// Ownership: the clock, the timing responder, the control port and the PTP master are process-wide (SPEC-audio.md 3.1)
/// and are started (idempotently) but never disposed here; <see cref="DisposeAsync"/> releases only the audio socket, the
/// per-receiver builders and their control-port registrations.
/// </para>
/// </remarks>
public sealed partial class RealtimeStreamTransport : IStreamTransport
{
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly uint _ssrc;
    private readonly byte[] _packet = new byte[RealtimeStreamConstants.MaxPacketSize];
    private readonly Dictionary<IPAddress, int> _timingPeers = [];
    private ImmutableArray<Receiver> _receivers = ImmutableArray<Receiver>.Empty;
    private Socket? _audio;
    private ClockMapping? _mapping;
    private Task? _starting;
    private uint _lastSyncTimestamp;
    private bool _haveSynced;
    private long _freshPackets;
    private long _packetsSent;
    private long _packetsWithheld;
    private long _syncsSent;
    private long _sendErrors;
    private bool _disposed;

    /// <summary>Creates the transport over the shared sockets.</summary>
    public RealtimeStreamTransport(IMasterClock clock, ITimingResponder timing, IControlPort control, int latencyFrames = RealtimeStreamConstants.DefaultLatencyFrames, IPtpMaster? ptp = null)
        : this(clock, timing, control, latencyFrames, ptp, logger: null)
    {
    }

    /// <summary>Creates the transport over the shared sockets, with a logger.</summary>
    /// <param name="clock">The process-wide master clock (timing replies, syncs and the pacer all read it).</param>
    /// <param name="timing">The process-wide timing responder (its port goes into NTP session SETUPs).</param>
    /// <param name="control">The process-wide control port (syncs out, 0xD5 in).</param>
    /// <param name="latencyFrames">L in frames (SPEC.md D1: 77 175).</param>
    /// <param name="ptp">The PTP grandmaster for a PTP-timed realtime stream; null for NTP.</param>
    /// <param name="logger">Logger; null for none.</param>
    public RealtimeStreamTransport(IMasterClock clock, ITimingResponder timing, IControlPort control, int latencyFrames, IPtpMaster? ptp, ILogger<RealtimeStreamTransport>? logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentOutOfRangeException.ThrowIfNegative(latencyFrames);
        Clock = clock;
        Timing = timing;
        Control = control;
        LatencyFrames = latencyFrames;
        Ptp = ptp;
        _logger = logger ?? (ILogger)NullLogger.Instance;

        // owntone uses a random SSRC for NTP sessions and 0 for PTP sessions (rtp_common.c:78-82, SPEC-audio.md 1.2);
        // receivers ignore it on the realtime stream.
        _ssrc = ptp is null ? BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)) : 0;
    }

    /// <inheritdoc />
    public IMasterClock Clock { get; }

    /// <summary>The timing responder (used when <see cref="Ptp"/> is null).</summary>
    public ITimingResponder Timing { get; }

    /// <summary>The control port.</summary>
    public IControlPort Control { get; }

    /// <summary>L in frames.</summary>
    public int LatencyFrames { get; }

    /// <summary>The PTP grandmaster when the realtime stream runs under PTP; null for NTP timing.</summary>
    public IPtpMaster? Ptp { get; }

    /// <inheritdoc />
    public StreamType Type => StreamType.Realtime;

    /// <inheritdoc />
    public TimingProtocol TimingProtocol => Ptp is null ? TimingProtocol.Ntp : TimingProtocol.Ptp;

    /// <inheritdoc />
    public int ControlPort => Control.Port;

    /// <inheritdoc />
    public int TimingPort => Ptp is null ? Timing.Port : 0;

    /// <summary>The local port audio packets leave from (0 before <see cref="StartAsync"/>).</summary>
    public int AudioPort => _audio?.LocalEndPoint is IPEndPoint local ? local.Port : 0;

    /// <summary>The SSRC written into every packet.</summary>
    public uint Ssrc => _ssrc;

    /// <summary>
    /// Probe P18 (A-Q10): while true, periodic syncs are not sent (initial <c>0x90</c> syncs for receivers in CONNECTED
    /// still are). Lets a probe stop syncs for 3/6/10 s mid-track and observe the receiver.
    /// </summary>
    public bool PeriodicSyncsSuspended { get; set; }

    /// <summary>
    /// Probe P14 (A-Q6, C-7.14): when greater than 0, every n-th packet (never a marker packet) is stored in the histories
    /// but not sent, so the receivers must ask for it with 0xD5. 0 (default) sends everything.
    /// </summary>
    public int WithholdEveryNthPacket { get; set; }

    /// <summary>
    /// Probe P7 (A-Q9): replaces the 0xD4 sync's flags field (7 by default; shairport-sync adds 11 025 frames for 7 and
    /// not for 4). Null keeps <see cref="SyncPacket.Flags"/>. Ignored for 0xD7.
    /// </summary>
    public ushort? SyncFlagsOverride { get; set; }

    /// <summary>Audio packets sent (all receivers counted).</summary>
    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    /// <summary>Packets withheld by <see cref="WithholdEveryNthPacket"/> (all receivers counted).</summary>
    public long PacketsWithheld => Interlocked.Read(ref _packetsWithheld);

    /// <summary>Sync packets sent (all receivers counted).</summary>
    public long SyncsSent => Interlocked.Read(ref _syncsSent);

    /// <summary>UDP send errors (a receiver that went away; the group carries on).</summary>
    public long SendErrors => Interlocked.Read(ref _sendErrors);

    /// <summary>The receivers currently registered and whether each is streaming (diagnostics, tests).</summary>
    public IReadOnlyDictionary<string, bool> ReceiverStates
    {
        get
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var receiver in _receivers)
            {
                map[receiver.Id] = receiver.Streaming;
            }

            return map;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Starts the control port and, for NTP, the timing responder or, for PTP, the grandmaster (all idempotent by contract;
    /// the PTP master is started once per transport), binds the audio socket and measures the clock's NTP/PTP offsets.
    /// </remarks>
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
    /// PTP only: makes <paramref name="address"/> a unicast peer of the grandmaster (owntone adds the receiver's address
    /// from the session SETUP answer, SPEC-audio.md 4.4). Reference-counted, so several sessions on one host are fine;
    /// a no-op for NTP.
    /// </summary>
    public void AddTimingPeer(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (Ptp is not { } ptp)
        {
            return;
        }

        address = Normalize(address);
        bool first;
        lock (_gate)
        {
            _timingPeers[address] = (_timingPeers.TryGetValue(address, out var count) ? count : 0) + 1;
            first = _timingPeers[address] == 1;
        }

        if (first)
        {
            ptp.AddPeer(address);
            LogPtpPeer(address, "added");
        }
    }

    /// <summary>Undoes one <see cref="AddTimingPeer"/>; the peer is removed from the grandmaster with its last user.</summary>
    public void RemoveTimingPeer(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (Ptp is not { } ptp)
        {
            return;
        }

        address = Normalize(address);
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
            ptp.RemovePeer(address);
            LogPtpPeer(address, "removed");
        }
    }

    /// <inheritdoc />
    /// <remarks>Replaces an earlier registration of the same id. The receiver is CONNECTED until the next <see cref="Send"/>.</remarks>
    /// <exception cref="ArgumentException">The stream key is not 32 bytes.</exception>
    public void AddReceiver(string receiverId, ReceiverStreamEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpoint.Data);
        ArgumentNullException.ThrowIfNull(endpoint.Control);
        var socket = _audio ?? throw new InvalidOperationException("Start the transport before adding receivers.");
        var receiver = new Receiver(receiverId, endpoint, new RtpAudioPacketBuilder(endpoint.StreamKey, _ssrc), new PacketHistory(), DataAddress(socket, endpoint.Data));
        Receiver? replaced;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            replaced = Find(_receivers, receiverId);
            var current = replaced is null ? _receivers : _receivers.Remove(replaced);
            _receivers = current.Add(receiver);
            if (replaced is not null)
            {
                Release(replaced);
            }

            Control.Register(endpoint.Control, receiver.History, endpoint.RetransmitOnDataPort, endpoint.Data);
        }

        LogAdded(receiverId, endpoint.Data, endpoint.Control, endpoint.OffsetFrames, endpoint.RetransmitOnDataPort ? "raw on the data port" : "wrapped 0xD6", TimingProtocol);
    }

    /// <inheritdoc />
    /// <remarks>Unregisters it from the control port and clears its history (SPEC.md D8: on TEARDOWN). Unknown ids are ignored.</remarks>
    public void RemoveReceiver(string receiverId)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        lock (_gate)
        {
            if (Find(_receivers, receiverId) is not { } receiver)
            {
                return;
            }

            _receivers = _receivers.Remove(receiver);
            Release(receiver);
        }

        LogRemoved(receiverId);
    }

    /// <inheritdoc />
    /// <remarks>The history is kept (SPEC.md D8: cleared on TEARDOWN, not FLUSH). Unknown ids are ignored.</remarks>
    public void Resync(string receiverId)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        lock (_gate)
        {
            if (Find(_receivers, receiverId) is { } receiver)
            {
                receiver.Streaming = false;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="ntpNow"/> is the instant the packet is due (the pacer's deadline as an NTP timestamp), which is the
    /// <c>T</c> of any sync sent with it. Errors sending to one receiver are counted and logged, never thrown, so one
    /// unreachable receiver does not stop the group.
    /// </remarks>
    public void Send(ushort sequence, uint timestamp, ReadOnlySpan<byte> alacFrame, ulong ntpNow)
    {
        if (alacFrame.IsEmpty || alacFrame.Length > RealtimeStreamConstants.MaxAlacFrame)
        {
            throw new ArgumentException($"An ALAC frame of 1..{RealtimeStreamConstants.MaxAlacFrame} bytes is required.", nameof(alacFrame));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var socket = _audio ?? throw new InvalidOperationException("The transport is not started.");
            var receivers = _receivers;

            // Periodic sync every 44 100 frames of timeline (SPEC-audio.md 2.3: owntone sends one every sample_rate frames).
            var periodic = !_haveSynced || unchecked(timestamp - _lastSyncTimestamp) >= RealtimeStreamConstants.SyncIntervalFrames;
            if (periodic)
            {
                _haveSynced = true;
                _lastSyncTimestamp = timestamp;
            }

            foreach (var receiver in receivers)
            {
                if (!receiver.Streaming)
                {
                    SendSyncTo(receiver, first: true, timestamp, ntpNow);
                }
                else if (periodic && !PeriodicSyncsSuspended)
                {
                    SendSyncTo(receiver, first: false, timestamp, ntpNow);
                }
            }

            var withholdEvery = WithholdEveryNthPacket;
            var withhold = false;
            if (receivers.Length > 0)
            {
                _freshPackets++;
                withhold = withholdEvery > 0 && _freshPackets % withholdEvery == 0;
            }

            foreach (var receiver in receivers)
            {
                var marker = !receiver.Streaming;
                var length = receiver.Builder.Build(_packet, sequence, timestamp, marker, alacFrame);
                var packet = _packet.AsSpan(0, length);
                receiver.History.Store(sequence, packet);
                receiver.Streaming = true;
                if (withhold && !marker)
                {
                    Interlocked.Increment(ref _packetsWithheld);
                    continue;
                }

                SendAudio(socket, receiver, packet, sequence);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends a sync with <c>next_frame = nextTimestamp</c> at <c>T = ntpNow</c> to every receiver now and restarts the
    /// periodic cadence from it. With <paramref name="first"/> the syncs carry <c>0x90</c>; the receivers' states are not
    /// changed (a receiver in CONNECTED still gets its marker packet with the next <see cref="Send"/>).
    /// </remarks>
    public void SendSync(uint nextTimestamp, ulong ntpNow, bool first)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audio is null)
            {
                throw new InvalidOperationException("The transport is not started.");
            }

            foreach (var receiver in _receivers)
            {
                SendSyncTo(receiver, first, nextTimestamp, ntpNow);
            }

            _haveSynced = true;
            _lastSyncTimestamp = nextTimestamp;
        }
    }

    /// <inheritdoc />
    /// <remarks>Releases the audio socket and every receiver registration; the shared sockets and the PTP master stay up.</remarks>
    public async ValueTask DisposeAsync()
    {
        ImmutableArray<Receiver> receivers;
        IPAddress[] peers;
        Socket? audio;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            receivers = _receivers;
            _receivers = ImmutableArray<Receiver>.Empty;
            peers = [.. _timingPeers.Keys];
            _timingPeers.Clear();
            audio = _audio;
            _audio = null;
            foreach (var receiver in receivers)
            {
                Release(receiver);
            }
        }

        if (Ptp is { } ptp)
        {
            foreach (var peer in peers)
            {
                ptp.RemovePeer(peer);
            }
        }

        audio?.Dispose();
        if (_starting is { } starting)
        {
            try
            {
                await starting.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failed start was reported to its caller.
            }
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await Control.StartAsync(cancellationToken).ConfigureAwait(false);
        if (Ptp is { } ptp)
        {
            await ptp.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Timing.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        var mapping = new ClockMapping(Clock);
        var socket = UdpSockets.Bind(null, 0);
        lock (_gate)
        {
            if (_disposed)
            {
                socket.Dispose();
                throw new ObjectDisposedException(nameof(RealtimeStreamTransport));
            }

            _mapping = mapping;
            _audio ??= socket;
            if (!ReferenceEquals(_audio, socket))
            {
                socket.Dispose();
            }
        }

        LogStarted(TimingProtocol, Control.Port, TimingPort, AudioPort, LatencyFrames, Ptp?.ClockId ?? 0);
    }

    private void SendSyncTo(Receiver receiver, bool first, uint nextTimestamp, ulong ntpTime)
    {
        // SPEC-audio.md 2.3 / 3.3: now_frame = next_ts - L - offset_i; next_frame = next_ts.
        var nowFrame = unchecked(nextTimestamp - (uint)LatencyFrames - (uint)receiver.Endpoint.OffsetFrames);
        var buffer = receiver.SyncBuffer;
        int length;
        if (Ptp is { } ptp)
        {
            var ptpTime = (_mapping ?? throw new InvalidOperationException("The transport is not started.")).PtpOfNtp(ntpTime);
            new PtpSyncPacket(first, nowFrame, ptpTime, nextTimestamp, ptp.ClockId).Write(buffer);
            length = PtpSyncPacket.Size;
        }
        else
        {
            new SyncPacket(first, nowFrame, ntpTime, nextTimestamp).Write(buffer);
            if (SyncFlagsOverride is { } flags)
            {
                buffer[2] = (byte)(flags >> 8);
                buffer[3] = (byte)flags;
            }

            length = SyncPacket.Size;
        }

        ValueTask pending;
        try
        {
            pending = Control.SendSyncAsync(receiver.Endpoint.Control, buffer.AsMemory(0, length), CancellationToken.None);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            OnSendError(receiver, "sync", ex);
            return;
        }

        Interlocked.Increment(ref _syncsSent);
        if (pending.IsCompletedSuccessfully)
        {
            pending.GetAwaiter().GetResult();
        }
        else if (pending.IsCompleted)
        {
            try
            {
                pending.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                OnSendError(receiver, "sync", ex);
            }
        }
        else
        {
            // The buffer stays with the pending send; the next sync to this receiver gets a fresh one.
            receiver.SyncBuffer = new byte[PtpSyncPacket.Size];
            _ = ObserveAsync(pending, receiver);
        }

        if (first)
        {
            LogInitialSync(receiver.Id, nowFrame, nextTimestamp, Ptp is null ? "0xD4" : "0xD7");
        }
    }

    private async Task ObserveAsync(ValueTask pending, Receiver receiver)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            OnSendError(receiver, "sync", ex);
        }
    }

    private void SendAudio(Socket socket, Receiver receiver, ReadOnlySpan<byte> packet, ushort sequence)
    {
        try
        {
            socket.SendTo(packet, SocketFlags.None, receiver.DataAddress);
            Interlocked.Increment(ref _packetsSent);
        }
        catch (SocketException ex)
        {
            OnSendError(receiver, $"audio packet {sequence}", ex);
        }
    }

    private void OnSendError(Receiver receiver, string what, Exception ex)
    {
        var errors = Interlocked.Increment(ref _sendErrors);
        // The first few at Warning, then one in a thousand: a gone receiver must not flood the log at 125 packets/s.
        if (errors <= 5 || errors % 1000 == 0)
        {
            LogSendError(what, receiver.Id, errors, ex.Message);
        }
    }

    private void Release(Receiver receiver)
    {
        Control.Unregister(receiver.Endpoint.Control);
        receiver.History.Clear();
        receiver.Builder.Dispose();
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

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>The serialized destination for <see cref="Socket.SendTo(ReadOnlySpan{byte}, SocketFlags, SocketAddress)"/> (no allocation per packet).</summary>
    private static SocketAddress DataAddress(Socket socket, IPEndPoint data)
    {
        var target = new IPEndPoint(Normalize(data.Address), data.Port);
        if (socket.AddressFamily == AddressFamily.InterNetworkV6 && target.AddressFamily == AddressFamily.InterNetwork)
        {
            target = new IPEndPoint(target.Address.MapToIPv6(), target.Port);
        }

        return target.Serialize();
    }

    /// <summary>One receiver of the group.</summary>
    private sealed class Receiver(string id, ReceiverStreamEndpoint endpoint, RtpAudioPacketBuilder builder, PacketHistory history, SocketAddress dataAddress)
    {
        public string Id { get; } = id;

        public ReceiverStreamEndpoint Endpoint { get; } = endpoint;

        public RtpAudioPacketBuilder Builder { get; } = builder;

        public PacketHistory History { get; } = history;

        public SocketAddress DataAddress { get; } = dataAddress;

        /// <summary>False: CONNECTED (next packet gets an initial sync and the marker); true: STREAMING.</summary>
        public bool Streaming { get; set; }

        /// <summary>Reused for every sync (large enough for 0xD7).</summary>
        public byte[] SyncBuffer { get; set; } = new byte[PtpSyncPacket.Size];
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Realtime transport started: {Timing} timing, control port {ControlPort}, timing port {TimingPort}, audio from port {AudioPort}, L = {Latency} frames, PTP clock {ClockId:X16}")]
    private partial void LogStarted(TimingProtocol timing, int controlPort, int timingPort, int audioPort, int latency, ulong clockId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Receiver {Receiver} added (CONNECTED): data {Data}, control {Control}, offset {Offset} frames, retransmits {Mode}, {Timing} syncs")]
    private partial void LogAdded(string receiver, IPEndPoint data, IPEndPoint control, int offset, string mode, TimingProtocol timing);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Receiver {Receiver} removed")]
    private partial void LogRemoved(string receiver);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Initial sync ({Type}, 0x90) to {Receiver}: now_frame {NowFrame}, next_frame {NextFrame}; marker packet follows")]
    private partial void LogInitialSync(string receiver, uint nowFrame, uint nextFrame, string type);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Sending {What} to {Receiver} failed ({Errors} send errors so far): {Error}")]
    private partial void LogSendError(string what, string receiver, long errors, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "PTP peer {Address} {Action}")]
    private partial void LogPtpPeer(IPAddress address, string action);
}
