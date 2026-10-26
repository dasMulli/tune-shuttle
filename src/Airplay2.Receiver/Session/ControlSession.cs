using Airplay2.Receiver.Rtsp;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Session;

/// <summary>
/// The state machine behind one control connection (SPEC-receiver.md 4.1-4.9): the pairing POSTs, <c>/info</c>, session
/// SETUP (event channel and PTP slave started before the answer), stream SETUP (the claim of R8), RECORD, SETPEERS,
/// FLUSH, SETRATEANCHORTIME, FLUSHBUFFERED, SET_PARAMETER/GET_PARAMETER, <c>/feedback</c> liveness (R16), TEARDOWN and the
/// end of the session. Request handlers and <see cref="EndAsync"/> are serialised with one <c>SemaphoreSlim</c>; a handler
/// that finds the session ended answers 451 (4.9).
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the behaviour is the wave 1 <c>control-plane</c> package (SPEC-receiver.md 13).
/// </remarks>
public sealed class ControlSession : IAsyncDisposable
{
    private readonly ILogger _logger;

    /// <summary>Creates the session state for a connection (SPEC-receiver.md 4.1).</summary>
    /// <param name="connection">The control connection.</param>
    /// <param name="logger">Logger.</param>
    public ControlSession(RtspServerConnection connection, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The control connection.</summary>
    public RtspServerConnection Connection { get; }

    /// <summary>The sender after its session SETUP (SPEC-receiver.md 4.3), or null.</summary>
    public SenderInfo? Sender => null;

    /// <summary>True once the connection completed a session SETUP (SPEC-receiver.md 4.1 "the session connection").</summary>
    public bool IsSessionConnection => false;

    /// <summary>True while a stream is set up (SPEC-receiver.md 4.6: status flag 17, the output claimed).</summary>
    public bool HasStream => false;

    /// <summary>The <see cref="RtspRequestHandler"/> for this connection: dispatches one request per SPEC-receiver.md 4.1-4.9.</summary>
    public ValueTask<RtspServerResponse> HandleAsync(RtspServerConnection connection, RtspIncomingRequest request, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 4.1-4.9, wave 1");

    /// <summary>
    /// Ends the session (SPEC-receiver.md 4.9): stream, event channel, PTP slave, output released, <c>SenderGone(reason)</c>.
    /// Runs for TEARDOWN <c>{}</c>, a closed connection, the liveness timer (R16), the host's request (R8) and the receiver's stop.
    /// </summary>
    public Task EndAsync(string reason, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 4.9, wave 1");

    /// <summary>Ends the session when it is still running.</summary>
    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("Control session of {Remote} disposed", Connection.RemoteEndPoint);
        return ValueTask.CompletedTask;
    }
}
