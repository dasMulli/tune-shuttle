using System.Net;
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
    /// <param name="receivedAtNanos">The port's clock at arrival (the <c>t2</c> of a Sync).</param>
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

/// <summary>
/// The shared PTP port (SPEC-receiver.md R5, 6.1): two <c>UdpReceiveLoop</c>s on 319/320 (or the ports of
/// <see cref="PtpPortOptions"/>), routing by message type and source so one port serves the grandmaster toward our
/// receivers and the slave toward the sender, even when both peers are 127.0.0.1 (the loopback test of 11.1).
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the types exist so the bridge can create and pass one; <see cref="PtpMaster"/> keeps its own sockets
/// until the wave 1 <c>ptp-port</c> package refactors it onto this port (SPEC-receiver.md 13).
/// </remarks>
public sealed class PtpPort : IPtpPort
{
    private readonly ILogger _logger;

    /// <summary>Creates the port; nothing is bound until <see cref="EnsureBoundAsync"/>.</summary>
    /// <param name="options">Bind address and ports; the defaults (319/320, wildcard) when null.</param>
    /// <param name="logger">Logger.</param>
    public PtpPort(PtpPortOptions? options = null, ILogger<PtpPort>? logger = null)
    {
        Options = options ?? new PtpPortOptions();
        _logger = logger ?? NullLogger<PtpPort>.Instance;
    }

    /// <summary>The options.</summary>
    public PtpPortOptions Options { get; }

    /// <inheritdoc />
    public int EventPort => 0;

    /// <inheritdoc />
    public int GeneralPort => 0;

    /// <inheritdoc />
    public bool IsBound => false;

    /// <inheritdoc />
    public Task EnsureBoundAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 6.1, wave 1");

    /// <inheritdoc />
    public void Register(IPAddress source, IPtpEndpoint slave) => throw new NotImplementedException("SPEC-receiver.md 6.1, wave 1");

    /// <inheritdoc />
    public bool Unregister(IPAddress source, IPtpEndpoint handler) => throw new NotImplementedException("SPEC-receiver.md 6.1, wave 1");

    /// <inheritdoc />
    public void SetDefaultHandler(IPtpEndpoint? master) => throw new NotImplementedException("SPEC-receiver.md 6.1, wave 1");

    /// <inheritdoc />
    public ValueTask SendAsync(IPEndPoint target, ReadOnlyMemory<byte> datagram, bool eventPort, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 6.1, wave 1");

    /// <summary>Closes both sockets when they are bound.</summary>
    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("PTP port disposed (configured {EventPort}/{GeneralPort})", Options.EventPort, Options.GeneralPort);
        return ValueTask.CompletedTask;
    }
}
