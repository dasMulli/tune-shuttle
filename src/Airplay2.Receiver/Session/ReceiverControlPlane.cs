using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Receiver.Pairing;
using Airplay2.Sender.Pairing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Session;

/// <summary>
/// The receiver-wide control plane (SPEC-receiver.md 4.1-4.9): the RTSP listener (TCP, <see cref="AirplayReceiverOptions.RtspPort"/>,
/// 0 in tests), one <see cref="ControlSession"/> per accepted connection, the pairing state of R7 against the
/// <see cref="IControllerStore"/> (4.2), the per-process stream id counter, the eviction of an older session by a newer
/// sender's stream SETUP (R8), <see cref="EndSessionAsync"/> for the host's eviction, and the stop. All request handlers of
/// all connections and every end of a session run under one <see cref="SemaphoreSlim"/> (4.9), so session state never
/// changes under a running handler. <see cref="AirplayReceiver"/> owns one instance while it runs.
/// </summary>
public sealed class ReceiverControlPlane : IAsyncDisposable
{
    private static int s_nextStreamId;

    private readonly IControllerStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<ControlSession> _sessions = [];
    private readonly List<Task> _background = [];
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private int _nextConnectionId;
    private int _started;
    private int _stopped;

    /// <summary>Creates the control plane; nothing is bound until <see cref="StartAsync"/> (SPEC-receiver.md 4.1).</summary>
    /// <param name="options">The receiver options: identity, password, bind address, RTSP port, liveness timeout, connection and pairing switches, controller store.</param>
    /// <param name="host">The receiver that hosts the control plane.</param>
    /// <param name="loggerFactory">Logger factory; null logs nothing.</param>
    public ReceiverControlPlane(AirplayReceiverOptions options, IControlPlaneHost host, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.SessionTimeout, TimeSpan.Zero);
        Options = options;
        Host = host;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ReceiverControlPlane>();
        _store = options.ControllerStore ?? new InMemoryControllerStore();
        Pairing = CreatePairing(options.Password);
    }

    /// <summary>The options.</summary>
    public AirplayReceiverOptions Options { get; }

    /// <summary>The host.</summary>
    public IControlPlaneHost Host { get; }

    /// <summary>
    /// The pairing state new connections get (SPEC-receiver.md R7, 4.2); replaced by <see cref="UpdatePasswordAsync"/>, while
    /// running connections keep theirs (3.3).
    /// </summary>
    public ReceiverPairing Pairing { get; private set; }

    /// <summary>The bound RTSP port after <see cref="StartAsync"/> (the OS-chosen one for port 0), else 0 (SPEC-receiver.md 4.1, R13).</summary>
    public int Port { get; private set; }

    /// <summary>The open control connections (SPEC-receiver.md 4.1), oldest first.</summary>
    public IReadOnlyList<ControlSession> Sessions
    {
        get
        {
            lock (_lock)
            {
                return [.. _sessions];
            }
        }
    }

    /// <summary>The session connection that has a stream set up, or null (SPEC-receiver.md 4.6, R8: at most one).</summary>
    public ControlSession? StreamingSession => Sessions.FirstOrDefault(s => s.HasStream);

    /// <summary>True while a stream is set up: status flag bit 17 in <c>/info</c> (SPEC-receiver.md 4.6).</summary>
    public bool HasActiveStream => StreamingSession is not null;

    /// <summary>The one lock of SPEC-receiver.md 4.9: request handlers and session ends of all connections.</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>
    /// Loads the registered controllers (SPEC-receiver.md 4.2), binds the RTSP listener and starts accepting connections
    /// (4.1). <see cref="AirplayReceiverOptions.BindAddress"/> null binds every interface (IPv6 dual mode when available).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The control plane is already started.");
        }

        await Pairing.LoadAsync(cancellationToken).ConfigureAwait(false);
        var listener = Bind(Options.BindAddress, Options.RtspPort);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        _acceptLoop = AcceptLoopAsync(listener, _stop.Token);
        _logger.LogInformation("Receiver {Name}: RTSP listener on port {Port} ({Mode} pairing)", Options.Name, Port, Options.Password is null ? "transient" : "password");
    }

    /// <summary>
    /// Applies a password change (SPEC-receiver.md 3.3, R7): new connections get pairing options for <paramref name="password"/>
    /// (the controllers reloaded from the store); running connections and their pairing stay.
    /// </summary>
    public async Task UpdatePasswordAsync(string? password, CancellationToken cancellationToken)
    {
        var pairing = CreatePairing(password);
        await pairing.LoadAsync(cancellationToken).ConfigureAwait(false);
        Pairing = pairing;
    }

    /// <summary>
    /// Ends the current session as the host asks (SPEC-receiver.md R8, 4.9: <c>EndSessionAsync(reason)</c>): every session
    /// connection's stream, event channel and PTP slave are closed, the output released and <c>SenderGone(reason)</c> raised;
    /// their connections are closed. A no-op without a session. Completes after the output was released.
    /// </summary>
    public async Task EndSessionAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var closing = new List<Task>();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var session in Sessions.Where(s => s.IsSessionConnection))
            {
                closing.Add(await session.EndCoreAsync(reason, closeConnection: true).ConfigureAwait(false));
            }
        }
        finally
        {
            Gate.Release();
        }

        await Task.WhenAll(closing).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops accepting, ends every session with <paramref name="reason"/> and closes every connection (SPEC-receiver.md 3.3
    /// <c>enabled → false</c>, 4.9).
    /// </summary>
    public async Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        await _acceptLoop.ConfigureAwait(false);
        var closing = new List<Task>();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var session in Sessions)
            {
                closing.Add(await session.EndCoreAsync(reason, closeConnection: true).ConfigureAwait(false));
            }
        }
        finally
        {
            Gate.Release();
        }

        await Task.WhenAll(closing).ConfigureAwait(false);
        Task[] background;
        lock (_lock)
        {
            background = [.. _background];
        }

        await Task.WhenAll(background).ConfigureAwait(false);
    }

    /// <summary>Stops the control plane (<see cref="StopAsync"/> with the reason "receiver stopped").</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync("receiver stopped", CancellationToken.None).ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>The next stream id: a per-process counter starting at 1 (SPEC-receiver.md 4.6).</summary>
    internal static uint NextStreamId() => unchecked((uint)Interlocked.Increment(ref s_nextStreamId));

    /// <summary>
    /// The claim of R8 (SPEC-receiver.md 4.6): every other session connection is ended with <paramref name="reason"/>
    /// before <paramref name="claimant"/> gets its stream. Runs under <see cref="Gate"/>; the evicted connections' close is
    /// not awaited (their read loops may be waiting for the gate).
    /// </summary>
    internal async Task EvictOthersAsync(ControlSession claimant, string reason)
    {
        foreach (var other in Sessions.Where(s => !ReferenceEquals(s, claimant) && s.IsSessionConnection))
        {
            _logger.LogInformation("Receiver {Name}: ending the session of {Remote}: {Reason}", Options.Name, other.Connection.RemoteEndPoint, reason);
            Track(await other.EndCoreAsync(reason, closeConnection: true).ConfigureAwait(false), "evicted connection close");
        }
    }

    /// <summary>Forgets a connection whose session ended or whose socket closed.</summary>
    internal void Remove(ControlSession session)
    {
        lock (_lock)
        {
            _sessions.Remove(session);
        }
    }

    /// <summary>Tracks background work (logged on failure, awaited by <see cref="StopAsync"/>).</summary>
    internal void Track(Task task, string what)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
            {
                _logger.LogWarning(task.Exception.GetBaseException(), "Receiver {Name}: {What} failed", Options.Name, what);
            }

            return;
        }

        var logged = task.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    _logger.LogWarning(t.Exception.GetBaseException(), "Receiver {Name}: {What} failed", Options.Name, what);
                }

                lock (_lock)
                {
                    _background.RemoveAll(b => b.IsCompleted);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        lock (_lock)
        {
            _background.Add(logged);
        }
    }

    internal ILogger CreateLogger<T>() => _loggerFactory.CreateLogger<T>();

    private ReceiverPairing CreatePairing(string? password)
    {
        // R7: without a password the PIN is a random 8-digit string per start that nobody knows (regular pair-setup fails
        // at M3 with Error 2); with one, the password is the SRP PIN and transient M1 is refused with 470.
        var pin = password ?? RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        var accessory = new HapAccessoryOptions(Options.Identity.ToAccessoryIdentity(), pin, AllowTransient: password is null) { MaxControllers = 16 };
        return new ReceiverPairing(accessory, _store, Options.Pairing with { Name = Options.Name }, _loggerFactory.CreateLogger<ReceiverPairing>());
    }

    private static Socket Bind(IPAddress? address, int port)
    {
        Socket socket;
        if (address is null && Socket.OSSupportsIPv6)
        {
            socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
            address = IPAddress.IPv6Any;
        }
        else
        {
            address ??= IPAddress.Any;
            socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        try
        {
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(16);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken stop)
    {
        await Task.Yield();
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException && stop.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("Receiver {Name}: accept failed: {Error}", Options.Name, ex.Message);
                continue;
            }

            var id = Interlocked.Increment(ref _nextConnectionId);
            var session = new ControlSession(this, Pairing.ForConnection(), id, _loggerFactory.CreateLogger<ControlSession>());
            lock (_lock)
            {
                _sessions.Add(session);
            }

            session.Start(socket);
            _logger.LogDebug("Receiver {Name}: control connection #{Id} from {Remote}", Options.Name, id, socket.RemoteEndPoint);
        }
    }
}
