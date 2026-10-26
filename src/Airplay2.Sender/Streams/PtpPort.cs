using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Rtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Sender.Streams;

/// <summary>Options of the shared <see cref="PtpPort"/> (SPEC-receiver.md 6.1, 8.5 <c>Bridge:Airplay:PtpEventPort</c>/<c>PtpGeneralPort</c>).</summary>
public sealed record PtpPortOptions
{
    /// <summary>Local address of both sockets; null binds the wildcard (SPEC-receiver.md 8.5: <c>Bridge:LocalAddress</c>).</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>Local event port (Sync, Delay_Req): 319 by the standard; 0 lets the OS choose (tests).</summary>
    public int EventPort { get; init; } = PtpMessages.EventPort;

    /// <summary>Local general port (Announce, Follow_Up, Delay_Resp, Signaling): 320 by the standard; 0 lets the OS choose (tests).</summary>
    public int GeneralPort { get; init; } = PtpMessages.GeneralPort;
}

/// <summary>
/// A handler the <see cref="IPtpPort"/> routes datagrams to (SPEC-receiver.md 6.1): the receiver's <c>PtpSlave</c> for Sync,
/// Follow_Up, Announce and Signaling from a registered source address, the <see cref="PtpMaster"/> as the default handler
/// for everything else.
/// </summary>
public interface IPtpEndpoint
{
    /// <summary>One datagram with a valid PTP header (SPEC-receiver.md 6.1).</summary>
    /// <param name="header">The parsed header.</param>
    /// <param name="datagram">The whole datagram; valid only during the call.</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="eventPort">True when it arrived on the event port (319), false for the general port (320).</param>
    /// <param name="receivedAtNanos">
    /// Arrival on the port's arrival clock (<see cref="PtpPort.NowNanos"/>, <see cref="Stopwatch"/> nanoseconds); an
    /// endpoint on another clock (the master's PTP timeline, a slave's local clock) subtracts
    /// <see cref="PtpPort.ElapsedSince"/> from its own reading, so the time spent before the call stays out of its timestamps.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the port closes.</param>
    ValueTask HandleAsync(PtpHeader header, ReadOnlyMemory<byte> datagram, IPEndPoint source, bool eventPort, ulong receivedAtNanos, CancellationToken cancellationToken);
}

/// <summary>
/// One PTP port per process (SPEC-receiver.md R5, 6.1): owns UDP 319/320 for both roles and routes every datagram by
/// message type and source address. <see cref="PtpPort"/> is the implementation; the bridge creates one and hands it to
/// the sender (<see cref="AirplaySenderOptions.PtpPort"/>) and the receiver.
/// </summary>
public interface IPtpPort : IAsyncDisposable
{
    /// <summary>The bound event port, or 0 before <see cref="EnsureBoundAsync"/>.</summary>
    int EventPort { get; }

    /// <summary>The bound general port, or 0 before <see cref="EnsureBoundAsync"/>.</summary>
    int GeneralPort { get; }

    /// <summary>True once both sockets are bound.</summary>
    bool IsBound { get; }

    /// <summary>Binds both sockets on first use by either role; idempotent (SPEC-receiver.md 6.1).</summary>
    /// <exception cref="PtpPortException">A port could not be bound (the receiver then runs anchor-only, R6; the sender fails the connect as today).</exception>
    Task EnsureBoundAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Routes Sync, Follow_Up, Announce and Signaling from <paramref name="source"/> to <paramref name="slave"/>; replaces an
    /// existing handler for the address (SPEC-receiver.md 4.3: the newest session's slave wins).
    /// </summary>
    void Register(IPAddress source, IPtpEndpoint slave);

    /// <summary>Removes the handler of <paramref name="source"/> only while it is still <paramref name="handler"/> (SPEC-receiver.md 6.1); true when removed.</summary>
    bool Unregister(IPAddress source, IPtpEndpoint handler);

    /// <summary>
    /// The handler of everything not routed to a slave: Delay_Req, Pdelay_Req, Delay_Resp and any message from an
    /// unregistered address go to the master (SPEC-receiver.md 6.1); without one such datagrams are counted and dropped.
    /// </summary>
    void SetDefaultHandler(IPtpEndpoint? master);

    /// <summary>Sends a datagram from the event or the general socket (SPEC-receiver.md 6.1).</summary>
    ValueTask SendAsync(IPEndPoint target, ReadOnlyMemory<byte> datagram, bool eventPort, CancellationToken cancellationToken);
}

/// <summary>A PTP port could not be bound (SPEC-receiver.md 6.1); <see cref="PtpMasterException"/> wraps it on the sender path.</summary>
public sealed class PtpPortException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="port">The port that could not be bound.</param>
    /// <param name="innerException">The socket error.</param>
    public PtpPortException(string message, int port, Exception? innerException = null)
        : base(message, innerException)
    {
        Port = port;
    }

    /// <summary>The port that could not be bound.</summary>
    public int Port { get; }
}

/// <summary>Counters of a <see cref="PtpPort"/> (SPEC-receiver.md 6.1).</summary>
/// <param name="Received">Datagrams received on either socket.</param>
/// <param name="RoutedToSlaves">Datagrams handed to a registered slave.</param>
/// <param name="RoutedToDefault">Datagrams handed to the default handler (the master).</param>
/// <param name="Unrouted">PTP datagrams dropped because no handler was set for them.</param>
/// <param name="NotPtp">Datagrams that were not PTPv2 and were dropped.</param>
public sealed record PtpPortStatistics(long Received, long RoutedToSlaves, long RoutedToDefault, long Unrouted, long NotPtp);

/// <summary>
/// The shared PTP port (SPEC-receiver.md R5, 6.1): two <c>UdpReceiveLoop</c>s on 319/320 (or the ports of
/// <see cref="PtpPortOptions"/>), routing by message type and source so one port serves the grandmaster toward our
/// receivers and the slave toward the sender, even when both peers are 127.0.0.1 (the loopback test of 11.1).
/// </summary>
/// <remarks>
/// <para>
/// Routing (SPEC-receiver.md 6.1): Sync, Follow_Up, Announce and Signaling from an address registered with
/// <see cref="Register"/> go to that address's slave, and so does a Delay_Resp from it: the answer to the slave's
/// optional Delay_Req (RQ5), which our grandmaster never asks for. Everything else (Delay_Req, Pdelay_*, and every message
/// from an unregistered address) goes to the default handler; without one it is counted in
/// <see cref="PtpPortStatistics.Unrouted"/> and dropped. Non-PTPv2 datagrams are counted in
/// <see cref="PtpPortStatistics.NotPtp"/> and dropped.
/// </para>
/// <para>
/// Threads: each socket's loop calls the handlers for one datagram at a time (the master's per-socket reply buffers
/// rely on that); the routing table is a concurrent dictionary, so <see cref="Register"/>/<see cref="Unregister"/> never
/// wait for a loop.
/// </para>
/// </remarks>
public sealed partial class PtpPort : IPtpPort
{
    private const string EventLoopName = "PTP event port";
    private const string GeneralLoopName = "PTP general port";

    private static readonly double NanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly ILogger _logger;
    private readonly UdpReceiveLoop _eventLoop;
    private readonly UdpReceiveLoop _generalLoop;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<IPAddress, IPtpEndpoint> _slaves = new();
    private IPtpEndpoint? _default;
    private bool _bound;
    private bool _disposed;
    private long _received;
    private long _routedToSlaves;
    private long _routedToDefault;
    private long _unrouted;
    private long _notPtp;

    /// <summary>Creates the port; nothing is bound until <see cref="EnsureBoundAsync"/> (SPEC-receiver.md 6.1).</summary>
    /// <param name="options">Bind address and ports; the defaults (319/320, wildcard) when null.</param>
    /// <param name="logger">Logger.</param>
    public PtpPort(PtpPortOptions? options = null, ILogger<PtpPort>? logger = null)
    {
        Options = options ?? new PtpPortOptions();
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _eventLoop = new UdpReceiveLoop(EventLoopName, _logger, Options.BindAddress, Options.EventPort, HandleEventAsync);
        _generalLoop = new UdpReceiveLoop(GeneralLoopName, _logger, Options.BindAddress, Options.GeneralPort, HandleGeneralAsync);
    }

    /// <summary>
    /// The port's arrival clock in nanoseconds (<see cref="Stopwatch"/>-based, arbitrary origin): the timeline of the
    /// <c>receivedAtNanos</c> handed to <see cref="IPtpEndpoint.HandleAsync"/> (SPEC-receiver.md 6.1).
    /// </summary>
    public static ulong NowNanos => (ulong)(Stopwatch.GetTimestamp() * NanosPerTick);

    /// <summary>Nanoseconds since <paramref name="receivedAtNanos"/> on <see cref="NowNanos"/>; 0 when it lies in the future (SPEC-receiver.md 6.1).</summary>
    /// <param name="receivedAtNanos">An arrival time handed to <see cref="IPtpEndpoint.HandleAsync"/>.</param>
    public static ulong ElapsedSince(ulong receivedAtNanos)
    {
        var now = NowNanos;
        return now > receivedAtNanos ? now - receivedAtNanos : 0;
    }

    /// <summary>The options (SPEC-receiver.md 6.1).</summary>
    public PtpPortOptions Options { get; }

    /// <inheritdoc />
    public int EventPort => _eventLoop.Port;

    /// <inheritdoc />
    public int GeneralPort => _generalLoop.Port;

    /// <inheritdoc />
    public bool IsBound
    {
        get
        {
            lock (_gate)
            {
                return _bound;
            }
        }
    }

    /// <summary>A snapshot of the routing counters (SPEC-receiver.md 6.1).</summary>
    public PtpPortStatistics Statistics => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _routedToSlaves),
        Interlocked.Read(ref _routedToDefault),
        Interlocked.Read(ref _unrouted),
        Interlocked.Read(ref _notPtp));

    /// <inheritdoc />
    /// <remarks>
    /// Binds the event socket, then the general socket. When the general socket fails the event socket stays bound and a
    /// later call retries only the general one; <see cref="DisposeAsync"/> closes whatever is bound.
    /// </remarks>
    public Task EnsureBoundAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bound)
            {
                return Task.CompletedTask;
            }

            Bind(_eventLoop, EventLoopName, Options.EventPort);
            Bind(_generalLoop, GeneralLoopName, Options.GeneralPort);
            _bound = true;
        }

        LogBound(EventPort, GeneralPort);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Register(IPAddress source, IPtpEndpoint slave)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(slave);
        var key = Key(source);
        _slaves[key] = slave;
        LogRegistered(key);
    }

    /// <inheritdoc />
    public bool Unregister(IPAddress source, IPtpEndpoint handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        var key = Key(source);
        var removed = _slaves.TryRemove(new KeyValuePair<IPAddress, IPtpEndpoint>(key, handler));
        if (removed)
        {
            LogUnregistered(key);
        }

        return removed;
    }

    /// <inheritdoc />
    public void SetDefaultHandler(IPtpEndpoint? master) => Volatile.Write(ref _default, master);

    /// <summary>
    /// Clears the default handler only while it is still <paramref name="handler"/> (SPEC-receiver.md 6.1), so a disposed
    /// grandmaster never removes its successor; true when cleared.
    /// </summary>
    /// <param name="handler">The handler that is going away.</param>
    public bool ClearDefaultHandler(IPtpEndpoint handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ReferenceEquals(Interlocked.CompareExchange(ref _default, null, handler), handler);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The port is not bound (call <see cref="EnsureBoundAsync"/> first).</exception>
    /// <exception cref="SocketException">The datagram could not be sent; the caller counts and logs it.</exception>
    public async ValueTask SendAsync(IPEndPoint target, ReadOnlyMemory<byte> datagram, bool eventPort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var socket = (eventPort ? _eventLoop : _generalLoop).Socket
            ?? throw new InvalidOperationException("The PTP port is not bound (SPEC-receiver.md 6.1: EnsureBoundAsync first).");
        await socket.SendToAsync(datagram, SocketFlags.None, UdpSockets.Target(socket, target), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes both sockets when they are bound; idempotent (SPEC-receiver.md 6.1: the bridge disposes the port after both roles).</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _bound = false;
        }

        await _eventLoop.DisposeAsync().ConfigureAwait(false);
        await _generalLoop.DisposeAsync().ConfigureAwait(false);
        Volatile.Write(ref _default, null);
        _slaves.Clear();
        LogDisposed(Options.EventPort, Options.GeneralPort);
    }

    /// <summary>The routing key: IPv4-mapped addresses as IPv4, IPv6 without its scope id (a received link-local address carries the interface scope, a configured one may not).</summary>
    internal static IPAddress Key(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;
    }

    /// <summary>True for the message types a registered slave receives (SPEC-receiver.md 6.1, plus Delay_Resp, see the class remarks).</summary>
    internal static bool IsSlaveMessage(PtpMessageType type) => type is
        PtpMessageType.Sync or PtpMessageType.FollowUp or PtpMessageType.Announce or PtpMessageType.Signaling or PtpMessageType.DelayResp;

    private void Bind(UdpReceiveLoop loop, string name, int port)
    {
        try
        {
            loop.Start(this);
        }
        catch (SocketException ex)
        {
            var why = ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => "it is in use (nqptp, ptp4l or another PTP grandmaster on this host?)",
                SocketError.AccessDenied => "access was denied (ports below 1024 need root or CAP_NET_BIND_SERVICE on Linux)",
                _ => ex.SocketErrorCode.ToString(),
            };
            var what = port == 0 ? "an OS-chosen UDP port" : $"UDP {port}";
            LogBindFailed(name, what, why);
            throw new PtpPortException($"The PTP port cannot bind its {name} ({what}): {why}.", port, ex);
        }
    }

    private ValueTask HandleEventAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop) =>
        RouteAsync(datagram, source, eventPort: true, stop);

    private ValueTask HandleGeneralAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop) =>
        RouteAsync(datagram, source, eventPort: false, stop);

    private ValueTask RouteAsync(ReadOnlyMemory<byte> datagram, SocketAddress source, bool eventPort, CancellationToken stop)
    {
        var receivedAt = NowNanos;
        Interlocked.Increment(ref _received);
        var from = UdpSockets.ToEndPoint(source);
        if (!PtpHeader.TryRead(datagram.Span, out var header))
        {
            Interlocked.Increment(ref _notPtp);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogNotPtp(datagram.Length, from, eventPort ? EventPort : GeneralPort);
            }

            return ValueTask.CompletedTask;
        }

        if (IsSlaveMessage(header.MessageType) && !_slaves.IsEmpty && _slaves.TryGetValue(Key(from.Address), out var slave))
        {
            Interlocked.Increment(ref _routedToSlaves);
            return slave.HandleAsync(header, datagram, from, eventPort, receivedAt, stop);
        }

        if (Volatile.Read(ref _default) is { } master)
        {
            Interlocked.Increment(ref _routedToDefault);
            return master.HandleAsync(header, datagram, from, eventPort, receivedAt, stop);
        }

        Interlocked.Increment(ref _unrouted);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogUnrouted(header.MessageType, from);
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 230, Level = LogLevel.Information, Message = "PTP port bound on UDP {EventPort}/{GeneralPort}")]
    private partial void LogBound(int eventPort, int generalPort);

    [LoggerMessage(EventId = 231, Level = LogLevel.Debug, Message = "PTP port cannot bind its {Loop} ({Port}): {Reason}")]
    private partial void LogBindFailed(string loop, string port, string reason);

    [LoggerMessage(EventId = 232, Level = LogLevel.Debug, Message = "PTP port: messages from {Address} now go to a slave")]
    private partial void LogRegistered(IPAddress address);

    [LoggerMessage(EventId = 233, Level = LogLevel.Debug, Message = "PTP port: the slave of {Address} was unregistered")]
    private partial void LogUnregistered(IPAddress address);

    [LoggerMessage(EventId = 234, Level = LogLevel.Debug, Message = "PTP port: ignored a {Length}-byte non-PTPv2 datagram from {Source} on UDP {Port}")]
    private partial void LogNotPtp(int length, IPEndPoint source, int port);

    [LoggerMessage(EventId = 235, Level = LogLevel.Debug, Message = "PTP port: {Type} from {Source} dropped, no handler")]
    private partial void LogUnrouted(PtpMessageType type, IPEndPoint source);

    [LoggerMessage(EventId = 236, Level = LogLevel.Debug, Message = "PTP port disposed (configured {EventPort}/{GeneralPort})")]
    private partial void LogDisposed(int eventPort, int generalPort);
}
