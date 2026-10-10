using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Bridge.Contracts;
using Microsoft.Extensions.Logging;

namespace Bridge.Web;

/// <summary>Tuning of <see cref="BridgeEventClient"/>.</summary>
public sealed record BridgeEventClientOptions
{
    /// <summary>First delay after a lost or failed connection; doubled per failed attempt.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>Longest delay between attempts.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one connect attempt may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A <c>ping</c> is sent this often. Browsers expose no WebSocket keep-alive, so the app pings itself and treats a
    /// connection without any message for <see cref="PingInterval"/> + <see cref="PongTimeout"/> as dead (a sleeping
    /// laptop, a restarted service behind a proxy).
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Silence tolerated after a ping before the connection is dropped and re-opened.</summary>
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Largest event accepted (a receiver list event is a few KB; the log replay is line by line).</summary>
    public int MaxMessageBytes { get; init; } = 1024 * 1024;
}

/// <summary>
/// The web app's side of <c>/ws/events</c> (docs/ARCHITECTURE.md,
/// "Bridge API"): connects, feeds every <see cref="BridgeEvent"/> into <see cref="BridgeState"/>, keeps the connection
/// alive with <c>ping</c>, re-sends the log subscription after a reconnect, and reconnects with exponential back-off
/// (<see cref="BridgeEventClientOptions.InitialRetryDelay"/> doubling up to <see cref="BridgeEventClientOptions.MaxRetryDelay"/>)
/// until disposed. On every new connection the service sends its snapshot first, which <see cref="BridgeState.BeginResync"/>
/// uses to drop entries that disappeared while the app was offline.
/// </summary>
public sealed class BridgeEventClient : IAsyncDisposable
{
    private readonly BridgeState _state;
    private readonly Uri _uri;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;
    private readonly BridgeEventClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BridgeEventClient> _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private WebSocket? _socket;
    private Task? _run;
    private long _lastMessageTicks;
    private int _logSubscribers;
    private bool _disposed;

    /// <summary>Creates the client; <see cref="Start"/> begins connecting.</summary>
    /// <param name="state">The state the events go into.</param>
    /// <param name="uri">The <c>ws://</c>/<c>wss://</c> address of <c>/ws/events</c> (see <see cref="EventsUri"/>).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Tuning; defaults when null.</param>
    /// <param name="connect">Opens the socket; defaults to <see cref="ClientWebSocket"/> (tests pass the test server's client).</param>
    /// <param name="time">Clock for the ping watchdog and the back-off.</param>
    public BridgeEventClient(
        BridgeState state,
        Uri uri,
        ILogger<BridgeEventClient> logger,
        BridgeEventClientOptions? options = null,
        Func<Uri, CancellationToken, Task<WebSocket>>? connect = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(logger);
        _state = state;
        _uri = uri;
        _logger = logger;
        _options = options ?? new BridgeEventClientOptions();
        _connect = connect ?? ConnectClientWebSocketAsync;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Connections opened so far.</summary>
    public long Connections { get; private set; }

    /// <summary>Events received so far (pongs included).</summary>
    public long EventsReceived { get; private set; }

    /// <summary>True while log lines are requested (the diagnostics page is open).</summary>
    public bool LogSubscribed => Volatile.Read(ref _logSubscribers) > 0;

    /// <summary>The <c>/ws/events</c> address for an app base address (<c>http</c> → <c>ws</c>, <c>https</c> → <c>wss</c>).</summary>
    public static Uri EventsUri(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var builder = new UriBuilder(new Uri(baseAddress, ApiRoutes.Events.TrimStart('/')))
        {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        return builder.Uri;
    }

    /// <summary>Starts the connect/reconnect loop (idempotent).</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null)
            {
                return;
            }

            // Connecting from the start: RunAsync yields before its first attempt, and the shell would otherwise render
            // (and a screen reader announce) the offline pill and banner of the initial Disconnected state in between.
            SetConnection(EventConnectionState.Connecting);
            _run = RunAsync(_stop.Token);
        }
    }

    /// <summary>
    /// Asks for (or stops) the live log. Counted, so two components can each subscribe; the subscription is re-sent after
    /// every reconnect, and the service replays the last 100 lines on subscribe (duplicates are dropped by the state).
    /// </summary>
    public async Task SetLogSubscriptionAsync(bool subscribe, CancellationToken cancellationToken)
    {
        var count = subscribe ? Interlocked.Increment(ref _logSubscribers) : Interlocked.Decrement(ref _logSubscribers);
        if (count < 0)
        {
            Interlocked.Exchange(ref _logSubscribers, 0);
            return;
        }

        // Only the transitions 0 -> 1 and 1 -> 0 change what the service sends.
        if ((subscribe && count == 1) || (!subscribe && count == 0))
        {
            var socket = _socket;
            if (socket is not null)
            {
                await TrySendAsync(socket, new BridgeClientMessage.Subscribe(subscribe), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops the loop and closes the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            run = _run;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (run is not null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        _stop.Dispose();
        _sendGate.Dispose();
    }

    /// <summary>
    /// The connect/receive/reconnect loop; ends only on <paramref name="cancellationToken"/>. Any failure of a connection
    /// (a broken socket or an unexpected exception) is logged and followed by the back-off, so live updates never end
    /// silently; the state shows <see cref="EventConnectionState.Disconnected"/> whenever the loop is not serving.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Yield(); // never run the first connect inside Start's caller
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? error = null;
                try
                {
                    // Only the first attempt says Connecting: a retry keeps the "paused, retrying" state set before
                    // its delay, so the banner does not flip (and announce) twice per back-off cycle during an outage.
                    if (failures == 0)
                    {
                        SetConnection(EventConnectionState.Connecting);
                    }

                    using var socket = await ConnectWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
                    failures = 0;
                    Connections++;
                    _logger.LogInformation("Connected to the bridge events at {Uri}", _uri);
                    await ServeAsync(socket, cancellationToken).ConfigureAwait(false);
                    error = "The bridge closed the connection.";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (IsConnectionFailure(ex))
                {
                    error = ex.Message;
                    _logger.LogWarning("Bridge events: {Error}", ex.Message);
                }
                catch (Exception ex)
                {
                    // Not a network failure (a bug): log it and reconnect rather than leave the app "connected" but deaf.
                    error = ex.Message;
                    _logger.LogError(ex, "The bridge event connection failed unexpectedly");
                }
                finally
                {
                    _socket = null;
                }

                var delay = RetryDelay(failures++);
                SetConnection(EventConnectionState.Disconnected, error, _time.GetUtcNow() + delay);
                try
                {
                    await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            SetConnection(EventConnectionState.Disconnected, "Stopped.");
        }
    }

    /// <summary>Records the connection state; a failing <see cref="BridgeState.Changed"/> subscriber must not end the loop.</summary>
    private void SetConnection(EventConnectionState connection, string? error = null, DateTimeOffset? nextAttemptAt = null)
    {
        try
        {
            _state.SetConnection(connection, error, nextAttemptAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording the event connection state {State} failed", connection);
        }
    }

    /// <summary>The delay before attempt <paramref name="failures"/> + 1: doubling from the initial delay, capped, with ±20 % jitter.</summary>
    public TimeSpan RetryDelay(int failures)
    {
        var exponent = Math.Min(Math.Max(failures, 0), 16);
        var ticks = Math.Min(_options.InitialRetryDelay.Ticks * (1L << exponent), _options.MaxRetryDelay.Ticks);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromTicks((long)(ticks * jitter));
    }

    private async Task<WebSocket> ConnectWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            return await _connect(_uri, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No connection to {_uri} within {_options.ConnectTimeout.TotalSeconds:0} s.");
        }
    }

    private async Task ServeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        Touch();
        SetConnection(EventConnectionState.Connected);
        _state.BeginResync();
        _socket = socket;
        if (LogSubscribed)
        {
            await TrySendAsync(socket, new BridgeClientMessage.Subscribe(true), cancellationToken).ConfigureAwait(false);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchdog = WatchdogAsync(socket, lifetime.Token);
        try
        {
            await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the receive loop ended first
            }

            await CloseQuietlyAsync(socket).ConfigureAwait(false);
        }
    }

    private async Task ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                    {
                        if (buffer.Length >= _options.MaxMessageBytes)
                        {
                            throw new WebSocketException(WebSocketError.Faulted, $"An event larger than {_options.MaxMessageBytes} bytes arrived.");
                        }

                        var larger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, _options.MaxMessageBytes));
                        buffer.AsSpan(0, length).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = larger;
                    }

                    result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("The bridge closed the event connection ({Status})", socket.CloseStatus);
                        return;
                    }

                    length += result.Count;
                }
                while (!result.EndOfMessage);

                Touch();
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    Handle(buffer.AsSpan(0, length));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Handle(ReadOnlySpan<byte> json)
    {
        BridgeEvent? bridgeEvent;
        try
        {
            bridgeEvent = JsonSerializer.Deserialize<BridgeEvent>(json, BridgeApiClient.JsonOptions);
        }
        catch (JsonException ex) when (IsUnknownEventType(ex))
        {
            // A newer service may send event types this app does not know yet (additive contract changes).
            _logger.LogDebug("Ignoring an event of an unknown type: {Error}", ex.Message);
            return;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger.LogWarning("Ignoring an unreadable event: {Error}", ex.Message);
            return;
        }

        if (bridgeEvent is null)
        {
            return;
        }

        EventsReceived++;
        try
        {
            _state.Apply(bridgeEvent);
        }
        catch (Exception ex)
        {
            // An event that cannot be applied (or a failing Changed subscriber) must not end the connection.
            _logger.LogError(ex, "Applying the event {Event} failed", bridgeEvent.GetType().Name);
        }
    }

    /// <summary>
    /// True for System.Text.Json's "Read unrecognized type discriminator id" error, the way an event type added by a newer
    /// service surfaces; a missing discriminator (<see cref="NotSupportedException"/>) or broken JSON is not that.
    /// </summary>
    private static bool IsUnknownEventType(JsonException ex) =>
        ex.Message.Contains("type discriminator", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pings every <see cref="BridgeEventClientOptions.PingInterval"/> and aborts a connection that went silent.</summary>
    private async Task WatchdogAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.PingInterval, _time, cancellationToken).ConfigureAwait(false);
            var silence = _time.GetUtcNow() - new DateTimeOffset(Interlocked.Read(ref _lastMessageTicks), TimeSpan.Zero);
            if (silence > _options.PingInterval + _options.PongTimeout)
            {
                _logger.LogWarning("No event or pong for {Seconds:0} s; reconnecting", silence.TotalSeconds);
                socket.Abort();
                return;
            }

            await TrySendAsync(socket, new BridgeClientMessage.Ping(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrySendAsync(WebSocket socket, BridgeClientMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, BridgeApiClient.JsonOptions);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            // the receive loop notices the broken connection and reconnects
            _logger.LogDebug("Sending {Message} failed: {Error}", message.GetType().Name, ex.Message);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastMessageTicks, _time.GetUtcNow().UtcTicks);

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // the server is gone already
        }
    }

    private static bool IsConnectionFailure(Exception ex) =>
        ex is WebSocketException or IOException or TimeoutException or HttpRequestException or ObjectDisposedException
            or InvalidOperationException or OperationCanceledException;

    private static async Task<WebSocket> ConnectClientWebSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        // Browsers do not let the app set KeepAliveInterval or headers; the watchdog above replaces the keep-alive.
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
