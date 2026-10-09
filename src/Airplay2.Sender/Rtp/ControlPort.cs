using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Sender.Rtp;

/// <summary>
/// The process-wide control socket (SPEC-audio.md 2.3, 2.4; SPEC.md D8). It sends the sync packets the stream
/// transport builds (the cadence is not decided here), receives 0xD5 retransmit requests and answers them from the
/// requesting receiver's <see cref="IPacketHistory"/>: by default wrapped as 0xD6 to the request's source endpoint
/// (what iOS and pyatv do, <c>[pyatv] stream_client.py:155-170</c>), or, per receiver quirk, the original packet raw to
/// the receiver's data port (owntone's choice for an amplifier that does not understand 0xD6,
/// <c>[ot] airplay.c:2052-2063</c>). Anything else arriving here is logged and ignored.
/// </summary>
/// <remarks>
/// <para>
/// Lookup rule (see <see cref="IControlPort"/>): registered receivers are matched by source address first; the source
/// port is compared with the announced control port only when several registered receivers share the address. When
/// that comparison does not single one out (several receivers behind one address and a request from an unannounced
/// port) the request cannot be attributed: answering from the wrong history would send a packet under the wrong key,
/// so it is counted in <see cref="UnmatchedRequests"/> and dropped.
/// </para>
/// <para>
/// The sync packets must leave from this socket: shairport-sync learns our control address from the source of the
/// first control packet and sends its retransmit requests there (<c>[sps] rtp.c:1672-1677</c>). The raw retransmit form
/// also leaves from this socket rather than the audio socket owntone uses (<c>[ot] airplay.c:2039-2078</c> writes to the
/// session's audio socket); shairport-sync does not check the source of data packets (<c>[sps] rtp.c:1826-1828</c>).
/// Known gap: an Apple-SDK receiver has not been tried with the raw form. If probe P14 shows a receiver ignoring raw
/// retransmissions from the control socket, the contract gains a way to send them through the session's data socket.
/// </para>
/// <para>Socket lifetime and receive errors: see <see cref="UdpReceiveLoop"/>.</para>
/// </remarks>
public sealed partial class ControlPort : IControlPort
{
    private readonly ILogger _logger;
    private readonly UdpReceiveLoop _loop;
    private readonly Lock _gate = new();
    private readonly ulong[] _unknownTypesSeen = new ulong[4];
    // One reply buffer: the loop hands over one request at a time. 4-byte 0xD6 wrapper + the largest packet.
    private readonly byte[] _reply = new byte[RetransmitReply.HeaderSize + RealtimeStreamConstants.MaxPacketSize];
    private ImmutableArray<Registration> _registrations = ImmutableArray<Registration>.Empty;
    private long _requests;
    private long _retransmitted;
    private long _missing;
    private long _unmatched;
    private long _syncs;
    private long _unknown;

    /// <summary>Creates the control port; nothing is bound until <see cref="StartAsync"/>.</summary>
    /// <param name="logger">Logger; null for none.</param>
    /// <param name="bindAddress">Local address; null binds the wildcard (dual-mode IPv6 where available).</param>
    /// <param name="port">Local port; 0 lets the OS choose.</param>
    public ControlPort(ILogger<ControlPort>? logger = null, IPAddress? bindAddress = null, int port = 0)
    {
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _loop = new UdpReceiveLoop("control port", _logger, bindAddress, port, HandleAsync);
    }

    /// <inheritdoc />
    public int Port => _loop.Port;

    /// <inheritdoc />
    public long RetransmitRequestsReceived => Interlocked.Read(ref _requests);

    /// <summary>Packets re-sent in answer to retransmit requests (either form).</summary>
    public long PacketsRetransmitted => Interlocked.Read(ref _retransmitted);

    /// <summary>Requested packets that were no longer (or never) in the history.</summary>
    public long PacketsMissing => Interlocked.Read(ref _missing);

    /// <summary>Retransmit requests that matched no registered receiver, or more than one.</summary>
    public long UnmatchedRequests => Interlocked.Read(ref _unmatched);

    /// <summary>Sync packets sent.</summary>
    public long SyncsSent => Interlocked.Read(ref _syncs);

    /// <summary>Datagrams that were not retransmit requests.</summary>
    public long UnknownPacketsReceived => Interlocked.Read(ref _unknown);

    /// <summary>The receivers currently registered (diagnostics).</summary>
    public int RegisteredCount => _registrations.Length;

    /// <inheritdoc />
    /// <remarks>Registering the same control endpoint again replaces the earlier registration.</remarks>
    public void Register(IPEndPoint receiverControl, IPacketHistory history, bool replyOnDataPort, IPEndPoint receiverData)
    {
        ArgumentNullException.ThrowIfNull(receiverControl);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(receiverData);
        var registration = new Registration(UdpSockets.Normalize(receiverControl), history, replyOnDataPort, UdpSockets.Normalize(receiverData));
        lock (_gate)
        {
            var current = _registrations;
            var index = IndexOf(current, registration.Control);
            _registrations = index >= 0 ? current.SetItem(index, registration) : current.Add(registration);
        }

        LogRegistered(registration.Control, registration.Data, replyOnDataPort ? "raw on the data port" : "wrapped 0xD6");
    }

    /// <inheritdoc />
    /// <remarks>The history is not cleared here; the caller clears it on TEARDOWN (SPEC.md D8).</remarks>
    public void Unregister(IPEndPoint receiverControl)
    {
        ArgumentNullException.ThrowIfNull(receiverControl);
        var control = UdpSockets.Normalize(receiverControl);
        bool removed;
        lock (_gate)
        {
            var current = _registrations;
            var index = IndexOf(current, control);
            removed = index >= 0;
            if (removed)
            {
                _registrations = current.RemoveAt(index);
            }
        }

        if (removed)
        {
            LogUnregistered(control);
        }
    }

    /// <inheritdoc />
    public async ValueTask SendSyncAsync(IPEndPoint receiverControl, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverControl);
        var socket = _loop.Socket ?? throw new InvalidOperationException("The control port is not started.");
        ObjectDisposedException.ThrowIf(_loop.IsDisposed, this);
        await socket.SendToAsync(packet, SocketFlags.None, UdpSockets.Target(socket, receiverControl), cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _syncs);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            LogSyncSent(receiverControl, packet.Length > 1 ? packet.Span[1] : (byte)0, packet.Length);
        }
    }

    /// <summary>
    /// Binds the socket and starts the receive loop; idempotent. The token only covers starting: the loop runs until
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _loop.Start(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _loop.DisposeAsync();

    /// <summary>
    /// The lookup rule: address first, port only to disambiguate. Returns null when no registration has the address, or
    /// several do and none of them has the source port.
    /// </summary>
    internal static Registration? Find(ImmutableArray<Registration> registrations, IPEndPoint source)
    {
        Registration? byAddress = null;
        var matches = 0;
        foreach (var registration in registrations)
        {
            if (registration.Control.Address.Equals(source.Address))
            {
                byAddress = registration;
                matches++;
            }
        }

        if (matches <= 1)
        {
            return byAddress;
        }

        foreach (var registration in registrations)
        {
            if (registration.Control.Address.Equals(source.Address) && registration.Control.Port == source.Port)
            {
                return registration;
            }
        }

        return null;
    }

    private static int IndexOf(ImmutableArray<Registration> registrations, IPEndPoint control)
    {
        for (var i = 0; i < registrations.Length; i++)
        {
            if (registrations[i].Control.Equals(control))
            {
                return i;
            }
        }

        return -1;
    }

    private ValueTask HandleAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop)
    {
        if (!RetransmitRequest.TryParse(datagram.Span, out var request))
        {
            OnUnknown(datagram.Span, source);
            return ValueTask.CompletedTask;
        }

        Interlocked.Increment(ref _requests);
        return AnswerAsync(socket, request, source, _reply, stop);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AnswerAsync(Socket socket, RetransmitRequest request, SocketAddress source, byte[] reply, CancellationToken stop)
    {
        // Retransmit requests are rare (a lost packet now and then); the endpoint allocation here is not on the audio path.
        var sourceEndPoint = UdpSockets.ToEndPoint(source);
        var registration = Find(_registrations, sourceEndPoint);
        if (registration is null)
        {
            Interlocked.Increment(ref _unmatched);
            LogUnmatched(sourceEndPoint, request.FirstSequence, request.Count);
            return;
        }

        // A request can never usefully ask for more than the history holds; clamp a corrupt count.
        var count = Math.Min((int)request.Count, RealtimeStreamConstants.HistoryPackets);
        var sent = 0;
        var missing = 0;
        EndPoint? dataTarget = registration.ReplyOnDataPort ? UdpSockets.Target(socket, registration.Data) : null;
        for (var i = 0; i < count; i++)
        {
            var sequence = (ushort)(request.FirstSequence + i);
            if (!registration.History.TryGet(sequence, reply.AsSpan(RetransmitReply.HeaderSize), out var length))
            {
                missing++;
                continue;
            }

            if (dataTarget is not null)
            {
                await socket.SendToAsync(reply.AsMemory(RetransmitReply.HeaderSize, length), SocketFlags.None, dataTarget, stop).ConfigureAwait(false);
            }
            else
            {
                RetransmitReply.WriteHeader(reply, reply.AsSpan(RetransmitReply.HeaderSize, length));
                await socket.SendToAsync(reply.AsMemory(0, RetransmitReply.HeaderSize + length), SocketFlags.None, source, stop).ConfigureAwait(false);
            }

            sent++;
            Interlocked.Increment(ref _retransmitted);
        }

        if (missing > 0)
        {
            Interlocked.Add(ref _missing, missing);
        }

        LogAnswered(sourceEndPoint, request.FirstSequence, request.Count, registration.Control, sent, missing, dataTarget is null ? "wrapped" : "raw");
    }

    private void OnUnknown(ReadOnlySpan<byte> datagram, SocketAddress source)
    {
        Interlocked.Increment(ref _unknown);
        var type = datagram.Length > 1 ? datagram[1] : (byte)0;
        var word = type >> 6;
        var bit = 1UL << (type & 63);
        var firstOfType = (Interlocked.Or(ref _unknownTypesSeen[word], bit) & bit) == 0;
        if (firstOfType)
        {
            // First of its type at Information (probe P14 asks what else arrives on the control port), the rest at Debug.
            LogUnknownFirst(type, datagram.Length, UdpSockets.ToEndPoint(source));
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogUnknown(type, datagram.Length, UdpSockets.ToEndPoint(source));
        }
    }

    /// <summary>One registered receiver.</summary>
    internal sealed record Registration(IPEndPoint Control, IPacketHistory History, bool ReplyOnDataPort, IPEndPoint Data);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Registered receiver control {Control}, data {Data}, retransmits {Mode}")]
    private partial void LogRegistered(IPEndPoint control, IPEndPoint data, string mode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Unregistered receiver control {Control}")]
    private partial void LogUnregistered(IPEndPoint control);

    [LoggerMessage(EventId = 3, Level = LogLevel.Trace, Message = "Sync 0x{Type:X2} ({Length} bytes) to {Control}")]
    private partial void LogSyncSent(IPEndPoint control, byte type, int length);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Retransmit request {First}+{Count} from {Source} (receiver {Control}): {Sent} re-sent {Mode}, {Missing} not in history")]
    private partial void LogAnswered(IPEndPoint source, ushort first, ushort count, IPEndPoint control, int sent, int missing, string mode);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Retransmit request {First}+{Count} from {Source} matches no single registered receiver; dropped")]
    private partial void LogUnmatched(IPEndPoint source, ushort first, ushort count);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "First control-port datagram of type 0x{Type:X2} ({Length} bytes) from {Source}; ignored")]
    private partial void LogUnknownFirst(byte type, int length, IPEndPoint source);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Ignored control-port datagram of type 0x{Type:X2} ({Length} bytes) from {Source}")]
    private partial void LogUnknown(byte type, int length, IPEndPoint source);
}
