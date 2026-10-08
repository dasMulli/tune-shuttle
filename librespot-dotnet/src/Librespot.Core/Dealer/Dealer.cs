// Port of core/src/dealer/mod.rs.
//
// tokio-tungstenite -> System.Net.WebSockets.ClientWebSocket. Deliberate deviations, each also noted inline:
//   * Ping/pong: ClientWebSocket neither lets callers send PING frames nor surfaces PONG frames, so upstream's
//     ping task (PING every PING_INTERVAL, give up when no PONG arrives within PING_TIMEOUT) is expressed with the
//     built-in keep-alive (KeepAliveInterval = PING_INTERVAL, KeepAliveTimeout = PING_TIMEOUT). A missing pong
//     aborts the socket, which ends the receive loop exactly like upstream's ping task ending the receive task.
//   * Rust drops: dropping the last Arc<DealerShared> drops every subscriber/handler sender, which ends the
//     subscription streams. Here Dealer.CloseAsync (and a failed launch) call DealerShared.Release explicitly.
//     A Responder that was never answered replies "success: false" from Dispose (upstream Drop).
//   * TimeoutOnDrop: a connection that is replaced gets WEBSOCKET_CLOSE_TIMEOUT to finish its tasks, then the
//     socket is aborted. The final join on shutdown uses the same bound (upstream join_all waits for the peer to
//     answer the close frame without a limit).
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core.Dealer;

/// <summary>Upstream <c>Response</c>: the reply to a dealer request.</summary>
internal readonly record struct Response(bool Success);

/// <summary>A websocket frame queued for the send task (upstream <c>tungstenite::Message</c>, the variants dealer uses).</summary>
internal abstract record WsMessage
{
    private WsMessage()
    {
    }

    public sealed record Text(string Value) : WsMessage;

    /// <summary>Close the websocket (upstream <c>Close(None)</c>).</summary>
    public sealed record Close : WsMessage;
}

/// <summary>
/// Upstream <c>Responder</c>: replies to one dealer request. Must answer once; if it is disposed without an answer it
/// replies <c>success: false</c> (upstream <c>Drop</c>).
/// </summary>
internal sealed class Responder : IDisposable
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Responder>();

    private readonly string _key;
    private readonly ChannelWriter<WsMessage> _tx;
    private int _sent;

    public Responder(string key, ChannelWriter<WsMessage> tx)
    {
        _key = key;
        _tx = tx;
    }

    // Should only be called once
    private void SendInternal(Response response)
    {
        // serde_json's json! map is a BTreeMap, so keys are written sorted: key, payload, type.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("key", _key);
            writer.WriteStartObject("payload");
            writer.WriteBoolean("success", response.Success);
            writer.WriteEndObject();
            writer.WriteString("type", "reply");
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (!_tx.TryWrite(new WsMessage.Text(json)))
        {
            Log.LogWarning("Wasn't able to reply to dealer request: {Error}", "channel closed");
        }
    }

    /// <summary>Upstream <c>send(response)</c>.</summary>
    public void Send(Response response)
    {
        if (Interlocked.Exchange(ref _sent, 1) == 0)
        {
            SendInternal(response);
        }
    }

    /// <summary>Upstream <c>force_unanswered()</c>: marks the request as answered without replying.</summary>
    public void ForceUnanswered() => Interlocked.Exchange(ref _sent, 1);

    /// <summary>Upstream <c>Drop</c>: replies <c>success: false</c> if nothing was sent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _sent, 1) == 0)
        {
            SendInternal(new Response(false));
        }
    }
}

/// <summary>Upstream <c>RequestHandler</c> trait.</summary>
internal interface IRequestHandler
{
    /// <summary>Handles a request; takes ownership of <paramref name="responder"/>.</summary>
    void HandleRequest(Request request, Responder responder);

    /// <summary>Called when the dealer that owns the handler goes away (upstream: the handler is dropped).</summary>
    void Release()
    {
    }
}

// Upstream's blanket `impl RequestHandler for Fn(Request) -> impl IntoResponse` (IntoResponse for Response and for
// futures) is not ported: nothing in librespot registers a closure handler, only DealerRequestHandler is used.

/// <summary>
/// Upstream <c>Subscription</c>: the stream of messages for the subscribed uris. When the enumerator is disposed
/// (whether or not it was ever advanced), the subscription is closed and unregistered on the next dispatch attempt
/// (upstream: dropped receiver; "Maybe it's possible to unregister subscription directly when they are dropped
/// instead of on next failed attempt").
/// </summary>
internal sealed class Subscription : IAsyncEnumerable<Message>
{
    private readonly Channel<Message> _channel =
        Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>The sending side registered in the <see cref="SubscriberMap{T}"/>.</summary>
    public ChannelWriter<Message> Writer => _channel.Writer;

    public IAsyncEnumerator<Message> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(_channel, cancellationToken);

    // Not a compiler-generated iterator: its `finally` would only run once MoveNextAsync was called, so an
    // enumerator disposed before the first MoveNextAsync would never close the channel and never be pruned.
    private sealed class Enumerator(Channel<Message> channel, CancellationToken cancellationToken) : IAsyncEnumerator<Message>
    {
        public Message Current { get; private set; } = null!;

        public async ValueTask<bool> MoveNextAsync()
        {
            var reader = channel.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.TryRead(out var message))
                {
                    Current = message;
                    return true;
                }
            }

            return false;
        }

        public ValueTask DisposeAsync()
        {
            // upstream: the receiver is dropped
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Upstream <c>AddHandlerError</c>.</summary>
internal sealed class AddHandlerException : LibrespotException
{
    public enum Reason
    {
        AlreadyHandled,
        InvalidUri,
    }

    public Reason Cause { get; }

    private AddHandlerException(Reason cause, ErrorKind kind, string detail) : base(kind, detail)
    {
        Cause = cause;
    }

    public static AddHandlerException AlreadyHandled() =>
        new(Reason.AlreadyHandled, ErrorKind.Aborted, "There is already a handler for the given uri");

    public static AddHandlerException InvalidUri(string uri) =>
        new(Reason.InvalidUri, ErrorKind.InvalidArgument, $"The specified uri {uri} is invalid");
}

/// <summary>Upstream <c>SubscriptionError</c> (kind InvalidArgument).</summary>
internal sealed class SubscriptionException : LibrespotException
{
    public enum Reason
    {
        InvalidUri,
    }

    public Reason Cause { get; }

    public string Uri { get; }

    private SubscriptionException(Reason cause, string uri, string detail) : base(ErrorKind.InvalidArgument, detail)
    {
        Cause = cause;
        Uri = uri;
    }

    public static SubscriptionException InvalidUri(string uri) => new(Reason.InvalidUri, uri, "The specified uri is invalid");
}

/// <summary>Upstream <c>GetUrlResult</c>-returning closure: produces a fresh dealer url (with a fresh token).</summary>
internal delegate Task<Uri> GetDealerUrl(CancellationToken cancellationToken);

/// <summary>A live websocket and its two tasks (upstream <c>(JoinHandle, JoinHandle)</c> + the split stream).</summary>
internal sealed record DealerConnection(ClientWebSocket Socket, Task SendTask, Task ReceiveTask)
{
    /// <summary>
    /// Upstream <c>TimeoutOnDrop</c>: gives both tasks <paramref name="timeout"/> to finish, then aborts the socket.
    /// Never throws.
    /// </summary>
    public async Task DropAsync(TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(SendTask, ReceiveTask).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Socket.Abort();
        }
        catch (Exception)
        {
            // the loop tasks never fault; nothing to observe
        }
        finally
        {
            Socket.Dispose();
        }
    }
}

/// <summary>Upstream <c>Builder</c>: collects subscriptions and handlers before the websocket is launched.</summary>
internal sealed class Builder
{
    internal SubscriberMap<ChannelWriter<Message>> MessageHandlers { get; } = new();

    internal HandlerMap<IRequestHandler> RequestHandlers { get; } = new();

    /// <summary>Upstream <c>add_handler(uri, handler)</c>.</summary>
    public void AddHandler(string uri, IRequestHandler handler) => Dealer.AddHandler(RequestHandlers, uri, handler);

    /// <summary>Upstream <c>subscribe(uris)</c>.</summary>
    public Subscription Subscribe(params string[] uris) => Dealer.Subscribe(MessageHandlers, uris);

    /// <summary>Upstream <c>handles(uri)</c>.</summary>
    public bool Handles(string uri) => Dealer.Handles(RequestHandlers, MessageHandlers, uri);

    /// <summary>Upstream <c>launch_in_background(get_url, proxy)</c>: connects (and reconnects) in the background.</summary>
    public Dealer LaunchInBackground(GetDealerUrl getUrl, Uri? proxy)
    {
        var shared = new DealerShared(MessageHandlers, RequestHandlers);
        var handle = Task.Run(() => Dealer.RunAsync(shared, null, getUrl, proxy));
        return new Dealer(shared, handle);
    }

    /// <summary>
    /// Upstream <c>launch(get_url, proxy)</c>: performs the first connection attempt, failing if it fails, and
    /// continues (reconnecting) in the background.
    /// </summary>
    public async Task<Dealer> LaunchAsync(GetDealerUrl getUrl, Uri? proxy, CancellationToken cancellationToken = default)
    {
        var shared = new DealerShared(MessageHandlers, RequestHandlers);
        DealerConnection tasks;
        try
        {
            // Try to connect.
            var url = await getUrl(cancellationToken).ConfigureAwait(false);
            tasks = await Dealer.ConnectAsync(url, proxy, shared, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // upstream: the builder (and with it every subscriber/handler sender) is dropped
            shared.Release();
            if (e is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw LibrespotException.From(e);
        }

        // If a connection is established, continue in a background task.
        var handle = Task.Run(() => Dealer.RunAsync(shared, tasks, getUrl, proxy));
        return new Dealer(shared, handle);
    }
}

/// <summary>Upstream <c>DealerShared</c>: the handler maps plus the "dealer was dropped" signal.</summary>
internal sealed class DealerShared
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<DealerShared>();

    private readonly Lock _messageHandlersLock = new();
    private readonly Lock _requestHandlersLock = new();

    // upstream: `notify_drop: Semaphore` with 0 permits, closed to signal that the Dealer was dropped.
    private readonly CancellationTokenSource _notifyDrop = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _released;

    public DealerShared(SubscriberMap<ChannelWriter<Message>> messageHandlers, HandlerMap<IRequestHandler> requestHandlers)
    {
        MessageHandlers = messageHandlers;
        RequestHandlers = requestHandlers;
    }

    private SubscriberMap<ChannelWriter<Message>> MessageHandlers { get; }

    private HandlerMap<IRequestHandler> RequestHandlers { get; }

    /// <summary>Completes when the dealer is closed (upstream <c>closed()</c>).</summary>
    public Task Closed => _closed.Task;

    /// <summary>Cancelled when the dealer is closed.</summary>
    public CancellationToken ClosedToken => _notifyDrop.Token;

    /// <summary>Upstream <c>is_closed()</c>.</summary>
    public bool IsClosed => _closed.Task.IsCompleted;

    /// <summary>Upstream <c>notify_drop.close()</c>.</summary>
    public void Close()
    {
        if (_closed.TrySetResult())
        {
            _notifyDrop.Cancel();
        }
    }

    /// <summary>Runs <paramref name="action"/> on the request handler map under its lock.</summary>
    public T WithRequestHandlers<T>(Func<HandlerMap<IRequestHandler>, T> action)
    {
        lock (_requestHandlersLock)
        {
            return action(RequestHandlers);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the request handler map under its lock.</summary>
    public void WithRequestHandlers(Action<HandlerMap<IRequestHandler>> action)
    {
        lock (_requestHandlersLock)
        {
            action(RequestHandlers);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the subscriber map under its lock.</summary>
    public T WithMessageHandlers<T>(Func<SubscriberMap<ChannelWriter<Message>>, T> action)
    {
        lock (_messageHandlersLock)
        {
            return action(MessageHandlers);
        }
    }

    /// <summary>Upstream <c>Handles</c> on the shared maps (both locks, request handlers first).</summary>
    public bool Handles(string uri)
    {
        lock (_requestHandlersLock)
        {
            lock (_messageHandlersLock)
            {
                return Dealer.Handles(RequestHandlers, MessageHandlers, uri);
            }
        }
    }

    /// <summary>
    /// Ends every subscription stream and request receiver (upstream: dropping the last <c>Arc&lt;DealerShared&gt;</c>
    /// drops their senders). Idempotent.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        lock (_messageHandlersLock)
        {
            foreach (var tx in MessageHandlers.Values())
            {
                tx.TryComplete();
            }
        }

        lock (_requestHandlersLock)
        {
            foreach (var handler in RequestHandlers.Values())
            {
                handler.Release();
            }
        }
    }

    private void DispatchMessage(WebsocketMessage msg)
    {
        Message message;
        try
        {
            message = new Message { Headers = msg.Headers, Payload = msg.HandlePayload(), Uri = msg.Uri };
        }
        catch (LibrespotException why)
        {
            Log.LogWarning("failure during data parsing for {Uri}: {Why}", msg.Uri, why.Message);
            return;
        }

        var split = Dealer.SplitUri(message.Uri);
        if (split is not null)
        {
            lock (_messageHandlersLock)
            {
                if (MessageHandlers.Retain(split, tx => tx.TryWrite(message)))
                {
                    return;
                }
            }
        }

        Log.LogDebug("No subscriber for msg.uri: {Uri}", message.Uri);
    }

    private void DispatchRequest(WebsocketRequest request, ChannelWriter<WsMessage> sendTx)
    {
        Log.LogTrace("dealer request {MessageIdent}", request.MessageIdent);

        Request payloadRequest;
        try
        {
            payloadRequest = request.HandlePayload();
        }
        catch (LibrespotException why)
        {
            Log.LogWarning("request payload handling failed because of {Why}", why.Message);
            return;
        }

        // The responder will automatically send "success: false" if it is disposed without an answer.
        var responder = new Responder(request.Key, sendTx);

        var split = Dealer.SplitUri(request.MessageIdent);
        if (split is null)
        {
            Log.LogWarning("Dealer request with invalid message_ident: {MessageIdent}", request.MessageIdent);
            responder.Dispose();
            return;
        }

        lock (_requestHandlersLock)
        {
            var handler = RequestHandlers.Get(split);
            if (handler is not null)
            {
                handler.HandleRequest(payloadRequest, responder);
                return;
            }

            Log.LogWarning("No handler for message_ident: {MessageIdent}", request.MessageIdent);
        }

        responder.Dispose();
    }

    /// <summary>Upstream <c>dispatch</c>.</summary>
    public void Dispatch(MessageOrRequest m, ChannelWriter<WsMessage> sendTx)
    {
        switch (m)
        {
            case MessageOrRequest.Message(var message):
                DispatchMessage(message);
                break;
            case MessageOrRequest.Request(var request):
                DispatchRequest(request, sendTx);
                break;
        }
    }
}

/// <summary>Upstream <c>Dealer</c>: the launched websocket with its background reconnect task.</summary>
internal sealed class Dealer
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Dealer>();

    /// <summary>Upstream <c>WEBSOCKET_CLOSE_TIMEOUT</c>.</summary>
    public static readonly TimeSpan WebsocketCloseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Upstream <c>PING_INTERVAL</c>.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    /// <summary>Upstream <c>PING_TIMEOUT</c>.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Upstream <c>RECONNECT_INTERVAL</c>.</summary>
    public static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);

    private readonly DealerShared _shared;
    private Task? _handle;

    internal Dealer(DealerShared shared, Task handle)
    {
        _shared = shared;
        _handle = handle;
    }

    /// <summary>Upstream <c>add_handler(uri, handler)</c>.</summary>
    public void AddHandler(string uri, IRequestHandler handler) =>
        _shared.WithRequestHandlers(map => AddHandler(map, uri, handler));

    /// <summary>Upstream <c>remove_handler(uri)</c>.</summary>
    public IRequestHandler? RemoveHandler(string uri) => _shared.WithRequestHandlers(map => RemoveHandler(map, uri));

    /// <summary>Upstream <c>subscribe(uris)</c>.</summary>
    public Subscription Subscribe(params string[] uris) => _shared.WithMessageHandlers(map => Subscribe(map, uris));

    /// <summary>Upstream <c>handles(uri)</c>.</summary>
    public bool Handles(string uri) => _shared.Handles(uri);

    /// <summary>Upstream <c>close()</c>: signals shutdown and waits for the background task.</summary>
    public async Task CloseAsync()
    {
        Log.LogDebug("closing dealer");

        _shared.Close();

        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is not null)
        {
            try
            {
                await handle.ConfigureAwait(false);
            }
            catch (LibrespotException)
            {
                // upstream only reports a JoinError here; the run loop's own Err (a failed get_url) is ignored
            }
            catch (Exception e)
            {
                Log.LogError("error aborting dealer operations: {Error}", e.Message);
            }
        }

        // upstream: the Dealer (and the last Arc<DealerShared>) is dropped here
        _shared.Release();
    }

    /// <summary>
    /// Upstream <c>split_uri</c>: splits <c>hm://a/b/c</c> into <c>[hm, a, b, c]</c>, <c>spotify:a:b</c> into
    /// <c>[spotify, a, b]</c> and any other uri containing <c>/</c> into <c>["", ...]</c>; trailing separators are
    /// ignored. Returns <c>null</c> for anything else.
    /// </summary>
    internal static string[]? SplitUri(string s)
    {
        string scheme;
        char sep;
        string rest;
        if (s.StartsWith("hm://", StringComparison.Ordinal))
        {
            (scheme, sep, rest) = ("hm", '/', s["hm://".Length..]);
        }
        else if (s.StartsWith("spotify:", StringComparison.Ordinal))
        {
            (scheme, sep, rest) = ("spotify", ':', s["spotify:".Length..]);
        }
        else if (s.Contains('/'))
        {
            (scheme, sep, rest) = ("", '/', s);
        }
        else
        {
            return null;
        }

        rest = rest.TrimEnd(sep);
        return [scheme, .. rest.Split(sep)];
    }

    internal static void AddHandler(HandlerMap<IRequestHandler> map, string uri, IRequestHandler handler)
    {
        var split = SplitUri(uri) ?? throw AddHandlerException.InvalidUri(uri);
        map.Insert(split, handler);
    }

    internal static IRequestHandler? RemoveHandler(HandlerMap<IRequestHandler> map, string uri)
    {
        var split = SplitUri(uri);
        return split is null ? null : map.Remove(split);
    }

    internal static Subscription Subscribe(SubscriberMap<ChannelWriter<Message>> map, IEnumerable<string> uris)
    {
        var subscription = new Subscription();
        foreach (var uri in uris)
        {
            var split = SplitUri(uri);
            if (split is null)
            {
                // upstream: the receiver is dropped, so already inserted senders are pruned on the next dispatch
                subscription.Writer.TryComplete();
                throw SubscriptionException.InvalidUri(uri);
            }

            map.Insert(split, subscription.Writer);
        }

        return subscription;
    }

    internal static bool Handles(
        HandlerMap<IRequestHandler> reqMap,
        SubscriberMap<ChannelWriter<Message>> msgMap,
        string uri)
    {
        // upstream quirk kept: the request map is only checked for a first-level key equal to the whole uri
        if (reqMap.Contains(uri))
        {
            return true;
        }

        var split = SplitUri(uri);
        return split is not null && msgMap.Contains(split);
    }

    /// <summary>Initializes a connection and returns tasks that will finish when the connection is closed/lost.</summary>
    internal static async Task<DealerConnection> ConnectAsync(
        Uri address,
        Uri? proxy,
        DealerShared shared,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(address.Host))
        {
            throw LibrespotException.FailedPrecondition("URL error: No host name in the URL");
        }

        if (address.Scheme is not ("ws" or "wss"))
        {
            throw LibrespotException.FailedPrecondition("URL error: URL scheme not supported");
        }

        var socket = new ClientWebSocket();
        // Ping/pong: see the file header. A PING every PingInterval; no PONG within PingTimeout aborts the socket.
        socket.Options.KeepAliveInterval = PingInterval;
        socket.Options.KeepAliveTimeout = PingTimeout;
        if (proxy is not null)
        {
            Log.LogInformation("Using proxy \"{Proxy}\"", proxy);
            socket.Options.Proxy = new WebProxy(proxy);
        }
        else
        {
            // upstream connects a plain TcpStream; do not pick up the system proxy
            socket.Options.Proxy = null;
        }

        try
        {
            await socket.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            socket.Dispose();
            throw e switch
            {
                WebSocketException => LibrespotException.FailedPrecondition(e), // upstream: WsError -> failed_precondition
                OperationCanceledException when cancellationToken.IsCancellationRequested => e,
                _ => LibrespotException.From(e),
            };
        }

        var sendChannel = Channel.CreateUnbounded<WsMessage>(new UnboundedChannelOptions { SingleReader = true });

        // A task that forwards messages from the channel to the websocket.
        var sendTask = Task.Run(() => SendLoopAsync(socket, sendChannel, shared));

        // A task that receives messages from the web socket.
        var receiveTask = Task.Run(() => ReceiveLoopAsync(socket, sendChannel.Writer, shared));

        return new DealerConnection(socket, sendTask, receiveTask);
    }

    private static async Task SendLoopAsync(ClientWebSocket socket, Channel<WsMessage> sendChannel, DealerShared shared)
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                // upstream: `biased;` - the closed signal wins over queued messages
                if (shared.IsClosed)
                {
                    break;
                }

                WsMessage msg;
                try
                {
                    msg = await sendChannel.Reader.ReadAsync(shared.ClosedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // shared.closed()
                }
                catch (ChannelClosedException)
                {
                    break; // all senders gone
                }

                // New message arrived through channel
                if (msg is WsMessage.Close)
                {
                    break;
                }

                if (msg is WsMessage.Text(var text))
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception e)
        {
            error = e;
        }

        // upstream: send_rx.close()
        sendChannel.Writer.TryComplete();

        try
        {
            if (error is null)
            {
                await SendCloseAsync(socket).ConfigureAwait(false);
            }
            else if (IsAlreadyClosed(error))
            {
                // upstream flushes here; ClientWebSocket has no buffered frames to flush
            }
            else
            {
                Log.LogWarning("Dealer finished with an error: {Error}", error.Message);
                await SendCloseAsync(socket).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning("Error while closing websocket: {Error}", e.Message);
        }

        Log.LogDebug("Dropping send task");
    }

    private static async Task SendCloseAsync(ClientWebSocket socket)
    {
        // ClientWebSocket tracks the close handshake itself; only send our close frame while that is still valid.
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            // upstream sends Close(None) (no status). ClientWebSocket would put 1005 (Empty) on the wire, which peers
            // must reject, so the plain "normal closure" status is sent instead.
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool IsAlreadyClosed(Exception e) =>
        e is ObjectDisposedException
        || e is WebSocketException
        {
            WebSocketErrorCode: WebSocketError.InvalidState or WebSocketError.ConnectionClosedPrematurely,
        };

    private static async Task ReceiveLoopAsync(ClientWebSocket socket, ChannelWriter<WsMessage> sendTx, DealerShared shared)
    {
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();

        while (true)
        {
            ValueWebSocketReceiveResult result;
            frame.SetLength(0);
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                    frame.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
            }
            catch (Exception e)
            {
                // includes the keep-alive timeout (upstream: "Websocket peer does not respond.")
                Log.LogWarning("Websocket connection failed: {Error}", e.Message);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Log.LogDebug("Websocket connection closed.");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                Log.LogInformation("Received invalid binary message");
                continue;
            }

            var text = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
            MessageOrRequest m;
            try
            {
                m = MessageOrRequest.Parse(text);
            }
            catch (JsonException e)
            {
                Log.LogWarning("Message couldn't be parsed: {Error}. Message was {Text}", e.Message, text);
                continue;
            }

            try
            {
                shared.Dispatch(m, sendTx);
            }
            catch (Exception e)
            {
                // upstream would panic the receive task here; end the connection the same way
                Log.LogError("Dispatching dealer message failed: {Error}", e.Message);
                break;
            }
        }

        // Try to take send_task down with us, in case it's still alive.
        sendTx.TryWrite(new WsMessage.Close());

        Log.LogDebug("Dropping receive task");
    }

    /// <summary>The main background task for <see cref="Dealer"/>, which coordinates reconnecting.</summary>
    internal static async Task RunAsync(
        DealerShared shared,
        DealerConnection? initialTasks,
        GetDealerUrl getUrl,
        Uri? proxy)
    {
        var connection = initialTasks;
        var t0 = initialTasks?.SendTask;
        var t1 = initialTasks?.ReceiveTask;

        try
        {
            while (!shared.IsClosed)
            {
                if (t0 is not null && t1 is not null)
                {
                    var done = await Task.WhenAny(shared.Closed, t0, t1).ConfigureAwait(false);
                    if (done == shared.Closed)
                    {
                        break;
                    }

                    if (done == t0)
                    {
                        if (t0.IsFaulted)
                        {
                            Log.LogError("timeout on task 0: {Error}", t0.Exception?.GetBaseException().Message);
                        }

                        t0 = null;
                    }
                    else
                    {
                        if (t1.IsFaulted)
                        {
                            Log.LogError("timeout on task 1: {Error}", t1.Exception?.GetBaseException().Message);
                        }

                        t1 = null;
                    }
                }
                else
                {
                    Uri url;
                    try
                    {
                        url = await getUrl(shared.ClosedToken).WaitAsync(shared.ClosedToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (shared.IsClosed)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        // a get_url failure propagates and ends the loop (upstream `?`, which is silent); log it so a
                        // dead dealer is visible even if nobody awaits the task
                        Log.LogError("dealer could not obtain a new url, giving up: {Error}", e.Message);
                        throw;
                    }

                    try
                    {
                        var next = await ConnectAsync(url, proxy, shared).ConfigureAwait(false);
                        if (connection is not null)
                        {
                            // upstream: the previous TimeoutOnDrop handles are dropped by the reassignment
                            _ = connection.DropAsync(WebsocketCloseTimeout);
                        }

                        connection = next;
                        t0 = next.SendTask;
                        t1 = next.ReceiveTask;
                    }
                    catch (LibrespotException e)
                    {
                        Log.LogError("Error while connecting: {Error}", e.Message);
                        try
                        {
                            // upstream sleeps uninterruptibly; the loop condition exits right after a close either way
                            await Task.Delay(ReconnectInterval, shared.ClosedToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                }
            }
        }
        finally
        {
            // upstream: join_all(tasks) on a normal exit, TimeoutOnDrop when `?` returns early - both bounded by
            // WEBSOCKET_CLOSE_TIMEOUT here, see the file header
            if (connection is not null)
            {
                await connection.DropAsync(WebsocketCloseTimeout).ConfigureAwait(false);
            }
        }
    }
}
