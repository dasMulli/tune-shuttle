using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Bridge.Contracts;
using Bridge.Service.Diagnostics;

namespace Bridge.Service.Api;

/// <summary>
/// Fans <see cref="BridgeEvent"/>s out to every connected WebSocket as JSON, one message per frame (WORK-PLAN.md section
/// 7, <c>/ws/events</c>). Each client has its own bounded queue that drops the oldest event when the client cannot keep
/// up, so a slow browser never blocks the publisher. Log lines go only to clients that sent <c>subscribe {log: true}</c>;
/// a <c>ping</c> is answered with <c>pong</c>.
/// </summary>
public sealed class EventHub
{
    /// <summary>Events queued per client before the oldest is dropped.</summary>
    public const int ClientQueueCapacity = 512;

    /// <summary>Largest client message accepted (the client only sends tiny control messages).</summary>
    public const int MaxClientMessageBytes = 16 * 1024;

    /// <summary>JSON options of the wire format: web defaults (camelCase), the <c>type</c> discriminator from the contracts.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<long, Client> _clients = new();
    private readonly LogBuffer? _logBuffer;
    private readonly ILogger<EventHub> _logger;
    private long _nextId;

    /// <summary>Creates the hub and attaches it to the log buffer, so subscribed clients get the live log.</summary>
    public EventHub(LogBuffer? logBuffer, ILogger<EventHub> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _logBuffer = logBuffer;
        if (logBuffer is not null)
        {
            logBuffer.Listener = Publish;
        }
    }

    /// <summary>Clients currently connected.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>Publishes an event to all clients (log lines only to subscribers). Never blocks.</summary>
    public void Publish(BridgeEvent bridgeEvent)
    {
        ArgumentNullException.ThrowIfNull(bridgeEvent);
        foreach (var client in _clients.Values)
        {
            if (bridgeEvent is BridgeEvent.LogLine && !client.LogSubscribed)
            {
                continue;
            }

            client.Queue.Writer.TryWrite(bridgeEvent);
        }
    }

    /// <summary>Serves one client until it disconnects, without an initial snapshot.</summary>
    public Task ServeAsync(WebSocket socket, CancellationToken cancellationToken) =>
        ServeAsync(socket, static _ => Task.FromResult<IReadOnlyList<BridgeEvent>>([]), cancellationToken);

    /// <summary>
    /// Serves one client until it disconnects: registers it first (so nothing published meanwhile is lost), then sends the
    /// snapshot (<c>statusChanged</c>, the receivers, groups and output, section 7), then every published event.
    /// </summary>
    public async Task ServeAsync(WebSocket socket, Func<CancellationToken, Task<IReadOnlyList<BridgeEvent>>> snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(snapshot);
        var id = Interlocked.Increment(ref _nextId);
        var client = new Client();
        _clients[id] = client;
        _logger.LogDebug("Event client {Client} connected ({Count} connected)", id, _clients.Count);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var initial = await snapshot(lifetime.Token).ConfigureAwait(false);
            var sending = SendLoopAsync(socket, client, initial, lifetime.Token);
            var receiving = ReceiveLoopAsync(socket, client, lifetime.Token);
            var first = await Task.WhenAny(sending, receiving).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            client.Queue.Writer.TryComplete();
            await ObserveAsync(sending).ConfigureAwait(false);
            await ObserveAsync(receiving).ConfigureAwait(false);
            await CloseQuietlyAsync(socket, first == receiving ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable).ConfigureAwait(false);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Queue.Writer.TryComplete();
            _logger.LogDebug("Event client {Client} disconnected ({Count} connected)", id, _clients.Count);
        }
    }

    /// <summary>Serializes one event as the wire format sends it.</summary>
    public static byte[] Serialize(BridgeEvent bridgeEvent) => JsonSerializer.SerializeToUtf8Bytes(bridgeEvent, JsonOptions);

    private async Task SendLoopAsync(WebSocket socket, Client client, IReadOnlyList<BridgeEvent> initial, CancellationToken cancellationToken)
    {
        foreach (var bridgeEvent in initial)
        {
            await SendAsync(socket, bridgeEvent, cancellationToken).ConfigureAwait(false);
        }

        await foreach (var bridgeEvent in client.Queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendAsync(socket, bridgeEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendAsync(WebSocket socket, BridgeEvent bridgeEvent, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            throw new WebSocketException(WebSocketError.InvalidState, "The client went away.");
        }

        await socket.SendAsync(Serialize(bridgeEvent), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(WebSocket socket, Client client, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxClientMessageBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == MaxClientMessageBytes)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    result = await socket.ReceiveAsync(buffer.AsMemory(length, MaxClientMessageBytes - length), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    length += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    Handle(client, buffer.AsSpan(0, length));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Handle(Client client, ReadOnlySpan<byte> json)
    {
        BridgeClientMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BridgeClientMessage>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("Ignoring a malformed client message: {Error}", ex.Message);
            return;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogDebug("Ignoring a client message without a known type: {Error}", ex.Message);
            return;
        }

        switch (message)
        {
            case BridgeClientMessage.Subscribe subscribe:
                var wasSubscribed = client.LogSubscribed;
                client.LogSubscribed = subscribe.Log;
                if (subscribe.Log && !wasSubscribed && _logBuffer is not null)
                {
                    // The diagnostics page starts with the recent history, then follows the live lines.
                    foreach (var line in _logBuffer.Recent(100))
                    {
                        client.Queue.Writer.TryWrite(line);
                    }
                }

                break;
            case BridgeClientMessage.Ping:
                client.Queue.Writer.TryWrite(new BridgeEvent.Pong(DateTimeOffset.UtcNow));
                break;
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            _logger.LogDebug("Event client loop ended: {Error}", ex.Message);
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseOutputAsync(status, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // the client is gone already
        }
    }

    private sealed class Client
    {
        private volatile bool _logSubscribed;

        public Channel<BridgeEvent> Queue { get; } = Channel.CreateBounded<BridgeEvent>(new BoundedChannelOptions(ClientQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        public bool LogSubscribed
        {
            get => _logSubscribed;
            set => _logSubscribed = value;
        }
    }
}
