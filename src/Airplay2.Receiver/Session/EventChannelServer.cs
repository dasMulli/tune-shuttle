using System.Net;
using System.Net.Sockets;
using Airplay2.Receiver.Rtsp;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Session;

/// <summary>Options of an <see cref="EventChannelServer"/> (SPEC-receiver.md 4.4).</summary>
public sealed record EventChannelServerOptions
{
    /// <summary>Local address the listener binds to (loopback by default; the product receiver binds its LAN address).</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>The <c>Server</c> header on answers the channel writes.</summary>
    public string ServerHeader { get; init; } = "AirTunes/366.0";

    /// <summary>Label used in log messages.</summary>
    public string Name { get; init; } = "receiver";

    /// <summary>Close the event channel when a request is not answered within this time (SPEC-control.md 4.3).</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Write <c>CSeq</c> on event-channel requests (Apple TV: <c>CSeq: 0</c>; shairport-sync: none, SPEC-control.md 4.3).</summary>
    public bool SendCSeqOnRequests { get; init; }

    /// <summary>
    /// Send <c>updateInfo</c> right after the sender connects (shairport-sync, the product behaviour of
    /// SPEC-receiver.md 4.4); when false it is sent after RECORD (<see cref="EventChannelServer.UpdateInfoAfterRecord"/>, Apple TV).
    /// </summary>
    public bool UpdateInfoOnConnect { get; init; } = true;

    /// <summary>
    /// Send <c>updateInfo</c> as one HAP frame larger than 1024 bytes (padding the plist when needed), as an Apple-SDK
    /// receiver may (SPEC-control.md 2.5, D9); a test switch of the fake receiver.
    /// </summary>
    public bool LargeUpdateInfoFrames { get; init; }
}

/// <summary>
/// What an <see cref="EventChannelServer"/> needs from the receiver that owns it (SPEC-receiver.md 4.4): the
/// <c>/info</c> dictionary for <c>updateInfo</c>, a place to track background work, and notifications of what happened.
/// </summary>
public interface IEventChannelHost
{
    /// <summary>The <c>value</c> of an <c>updateInfo</c> command: the <c>/info</c> dictionary with <c>txtAirPlay</c> (SPEC-receiver.md 3.2, 4.4).</summary>
    /// <param name="forceVolume">Include <c>initialVolume</c> even when the receiver would normally leave it out.</param>
    PlistDictionary BuildUpdateInfo(bool forceVolume);

    /// <summary>Tracks a background task of the channel (its failure is logged; the owner awaits it when it stops).</summary>
    void Track(Task task, string what);

    /// <summary>The sender connected to the event port.</summary>
    void OnConnected(EventChannelServer channel, IPEndPoint remote)
    {
    }

    /// <summary>The sender's event connection closed.</summary>
    void OnClosed(EventChannelServer channel, string reason)
    {
    }

    /// <summary>
    /// A <c>POST /command</c> the channel sent was answered (<paramref name="statusCode"/> 0: no answer within
    /// <see cref="EventChannelServerOptions.AnswerTimeout"/>, the channel was closed); <paramref name="largeFrame"/>: sent as one HAP frame above 1024 bytes.
    /// </summary>
    void OnCommandAnswered(EventChannelServer channel, string type, int statusCode, bool largeFrame)
    {
    }
}

/// <summary>
/// The receiver side of the event channel of one session (SPEC-receiver.md 4.4, SPEC-control.md 4.3): a TCP listener
/// opened at the session SETUP (before the answer, as shairport does [sps:rtsp.c:2626-2638]); the connection the sender
/// makes to it is HAP-encrypted from its first byte with the Events keys in the receiver's view, and carries
/// RTSP-shaped <c>POST /command</c> requests from the receiver to the sender. An unanswered request closes the channel
/// after <see cref="EventChannelServerOptions.AnswerTimeout"/> [protocols.md:1786-1795]. A reconnect replaces the previous
/// connection. Promoted from the fake receiver's <c>FakeEventChannel</c> (SPEC-receiver.md R2).
/// </summary>
public sealed class EventChannelServer : IAsyncDisposable
{
    /// <summary>Plist size below which <see cref="EventChannelServerOptions.LargeUpdateInfoFrames"/> pads the <c>updateInfo</c> body, so the one frame is surely above 1024 bytes.</summary>
    private const int LargeFrameMinimumBody = 1400;

    private readonly IEventChannelHost _host;
    private readonly EventChannelServerOptions _options;
    private readonly ILogger _logger;
    private readonly HapChannelKeys? _keys;
    private readonly int _sessionId;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private Socket? _listener;
    private RtspServerConnection? _connection;
    private bool _updateInfoAfterRecord;
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    /// <summary>Creates the channel; nothing is bound until <see cref="Start"/>.</summary>
    /// <param name="host">The receiver that owns the channel.</param>
    /// <param name="options">Channel options.</param>
    /// <param name="keys">The Events keys in the receiver's view (<c>HapAccessoryPairing.EventKeysFor</c>), or null for a plaintext channel (an unpaired connection when pairing is not required).</param>
    /// <param name="sessionId">The control connection number, for logs.</param>
    /// <param name="logger">Logger.</param>
    public EventChannelServer(IEventChannelHost host, EventChannelServerOptions options, HapChannelKeys? keys, int sessionId, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.AnswerTimeout, TimeSpan.Zero);
        _host = host;
        _options = options;
        _keys = keys;
        _sessionId = sessionId;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The listening port announced as <c>eventPort</c> (0 before <see cref="Start"/>).</summary>
    public int Port { get; private set; }

    /// <summary>True while a sender connection is open.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _connection is { Closed.IsCompleted: false };
            }
        }
    }

    /// <summary>Binds and starts accepting (SPEC-receiver.md 4.4: before the session SETUP is answered).</summary>
    public void Start()
    {
        var listener = new Socket(_options.BindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(_options.BindAddress, 0));
            listener.Listen(4);
            Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        _listener = listener;
        _acceptLoop = AcceptLoopAsync(listener, _stop.Token);
        _host.Track(_acceptLoop, $"event accept loop (session #{_sessionId})");
    }

    /// <summary>
    /// RECORD arrived and <see cref="EventChannelServerOptions.UpdateInfoOnConnect"/> is off: send <c>updateInfo</c> now,
    /// or as soon as the sender connects.
    /// </summary>
    public void UpdateInfoAfterRecord()
    {
        bool connected;
        lock (_lock)
        {
            // Checked and flagged in one critical section, so a connect racing RECORD either sees the flag or is seen here.
            connected = _connection is { Closed.IsCompleted: false };
            _updateInfoAfterRecord = !connected;
        }

        if (connected)
        {
            _host.Track(SendUpdateInfoQuietlyAsync(), "updateInfo after RECORD");
        }
    }

    /// <summary>
    /// Sends <c>{type: updateInfo, value: &lt;the /info dict plus txtAirPlay&gt;}</c> [sps:ap2_event_message_handler.c:109-139]
    /// (SPEC-receiver.md 4.4); the dictionary comes from <see cref="IEventChannelHost.BuildUpdateInfo"/>.
    /// </summary>
    public Task<RtspResponse> SendUpdateInfoAsync(bool forceVolume, CancellationToken cancellationToken)
    {
        var body = new PlistDictionary { ["type"] = "updateInfo", ["value"] = _host.BuildUpdateInfo(forceVolume) };
        return SendCommandAsync(body, _options.LargeUpdateInfoFrames, cancellationToken);
    }

    /// <summary>
    /// Sends <c>POST /command</c> with a bplist body (Content-Length, Content-Type, no CSeq unless
    /// <see cref="EventChannelServerOptions.SendCSeqOnRequests"/> [sps:ap2_event_message_handler.c:88-95]) and waits for
    /// the answer (SPEC-receiver.md 4.4). With <paramref name="large"/> the message goes out as one HAP frame (SPEC-control.md 2.5).
    /// </summary>
    /// <exception cref="InvalidOperationException">No sender is connected.</exception>
    /// <exception cref="TimeoutException">No answer within the event timeout; the channel has been closed.</exception>
    public async Task<RtspResponse> SendCommandAsync(PlistDictionary body, bool large, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        RtspServerConnection connection;
        lock (_lock)
        {
            connection = _connection is { Closed.IsCompleted: false } open ? open : throw new InvalidOperationException("No event channel is connected.");
        }

        var type = body.GetString("type") ?? "(none)";
        var encoded = BinaryPlist.Encode(body);
        if (large && encoded.Length < LargeFrameMinimumBody)
        {
            var padded = new PlistDictionary(body) { ["fakeReceiverPadding"] = new byte[LargeFrameMinimumBody - encoded.Length] };
            encoded = BinaryPlist.Encode(padded);
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (large)
            {
                connection.MaxSendFramePlaintext = HapFrameCipher.MaxReceiveFramePlaintext;
            }

            try
            {
                var response = await connection.SendRequestAsync("POST", "/command", RtspContentTypes.BinaryPlist, encoded, null, cancellationToken).ConfigureAwait(false);
                _host.OnCommandAnswered(this, type, response.StatusCode, large);
                return response;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Receiver {Name}: event command {Type} not answered within {Timeout}; closing the event channel",
                    _options.Name,
                    type,
                    _options.AnswerTimeout);
                _host.OnCommandAnswered(this, type, 0, large);
                await connection.CloseAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (large && !connection.Closed.IsCompleted)
                {
                    connection.MaxSendFramePlaintext = HapFrameCipher.MaxSendFramePlaintext;
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Closes the listener and the sender connection (the session ended, SPEC-receiver.md 4.9).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Receiver {Name}: event accept loop ended with an error", _options.Name);
        }

        RtspServerConnection? connection;
        lock (_lock)
        {
            connection = _connection;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException or SocketException) && stop.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                break;
            }

            var options = new RtspServerConnectionOptions
            {
                ServerHeader = _options.ServerHeader,
                Name = $"{_options.Name} events #{_sessionId}",
                RequestTimeout = _options.AnswerTimeout,
                SendCSeqOnRequests = _options.SendCSeqOnRequests,
            };
            var connection = RtspServerConnection.Start(socket, HandleAsync, options, _logger);
            if (_keys is not null)
            {
                // Encrypted from the first byte (SPEC-control.md 4.3); nothing has been sent or received yet.
                connection.EnableEncryption(_keys);
            }

            RtspServerConnection? previous;
            bool sendUpdateInfo;
            lock (_lock)
            {
                previous = _connection;
                _connection = connection;
                sendUpdateInfo = _options.UpdateInfoOnConnect || _updateInfoAfterRecord;
                _updateInfoAfterRecord = false;
            }

            if (previous is not null)
            {
                _logger.LogDebug("Receiver {Name}: event channel reconnected; closing the previous connection", _options.Name);
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            _host.OnConnected(this, connection.RemoteEndPoint);
            _host.Track(MonitorAsync(connection), $"event channel monitor (session #{_sessionId})");
            if (sendUpdateInfo)
            {
                // shairport sends it right after accepting [sps:ap2_event_receiver.c:219].
                _host.Track(SendUpdateInfoQuietlyAsync(), "updateInfo on connect");
            }
        }
    }

    private async Task MonitorAsync(RtspServerConnection connection)
    {
        var reason = await connection.Closed.ConfigureAwait(false);
        _host.OnClosed(this, reason);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task SendUpdateInfoQuietlyAsync()
    {
        try
        {
            await SendUpdateInfoAsync(forceVolume: false, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("Receiver {Name}: updateInfo not delivered: {Error}", _options.Name, ex.Message);
        }
    }

    /// <summary>The sender sends no requests on the event channel; anything that arrives is logged and answered 501.</summary>
    private ValueTask<RtspServerResponse> HandleAsync(RtspServerConnection connection, RtspIncomingRequest request, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Receiver {Name}: unexpected request {Method} {Uri} on the event channel", _options.Name, request.Method, request.Uri);
        return ValueTask.FromResult(RtspServerResponse.Status(RtspStatus.NotImplemented));
    }
}
