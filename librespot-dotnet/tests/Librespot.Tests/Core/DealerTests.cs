using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Core.Dealer;
using Xunit;

namespace Librespot.Tests.Core;

// Upstream core/src/dealer/mod.rs and manager.rs have no tests. These drive the dealer against an in-process
// websocket server on 127.0.0.1 (no network access).
public class DealerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string PauseRequest =
        """{"message_id":1,"sent_by_device_id":"phone","command":{"endpoint":"pause","logging_params":{}}}""";

    private static string Reply(string key, bool success) =>
        $$"""{"key":"{{key}}","payload":{"success":{{(success ? "true" : "false")}}},"type":"reply"}""";

    [Fact]
    public void Responder_replies_exactly_once()
    {
        var channel = Channel.CreateUnbounded<WsMessage>();

        var answered = new Responder("k1", channel.Writer);
        answered.Send(new Response(true));
        answered.Dispose();

        var dropped = new Responder("k2", channel.Writer);
        dropped.Dispose();
        dropped.Dispose();

        var unanswered = new Responder("k3", channel.Writer);
        unanswered.ForceUnanswered();
        unanswered.Dispose();

        channel.Writer.TryComplete();
        var sent = new List<string>();
        while (channel.Reader.TryRead(out var m))
        {
            sent.Add(Assert.IsType<WsMessage.Text>(m).Value);
        }

        Assert.Equal(new[] { Reply("k1", true), Reply("k2", false) }, sent);

        // a closed send channel only logs
        new Responder("k4", channel.Writer).Dispose();
    }

    [Fact]
    public async Task Dispatch_routes_messages_and_prunes_dropped_subscriptions()
    {
        var builder = new Builder();
        var subscription = builder.Subscribe("hm://pusher/v1/connections/");
        var shared = new DealerShared(builder.MessageHandlers, builder.RequestHandlers);
        var sendTx = Channel.CreateUnbounded<WsMessage>();

        shared.Dispatch(
            MessageOrRequest.Parse("""{"type":"message","uri":"hm://pusher/v1/connections/abc","headers":{"Spotify-Connection-Id":"abc"}}"""),
            sendTx.Writer);

        var enumerator = subscription.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout));
        Assert.Equal("abc", enumerator.Current.Headers["Spotify-Connection-Id"]);
        Assert.True(shared.Handles("hm://pusher/v1/connections/"));

        // the consumer goes away: the next dispatch fails to deliver and unregisters the subscription
        await enumerator.DisposeAsync();
        shared.Dispatch(MessageOrRequest.Parse("""{"type":"message","uri":"hm://pusher/v1/connections/def"}"""), sendTx.Writer);
        Assert.False(shared.Handles("hm://pusher/v1/connections/"));
    }

    [Fact]
    public void Dispatch_answers_unroutable_requests_with_failure()
    {
        var builder = new Builder();
        var shared = new DealerShared(builder.MessageHandlers, builder.RequestHandlers);
        var sendTx = Channel.CreateUnbounded<WsMessage>();

        shared.Dispatch(MessageOrRequest.Parse(DealerProtocolTests.RequestEnvelope(PauseRequest, "hm://nobody/home", "k1")), sendTx.Writer);
        shared.Dispatch(MessageOrRequest.Parse(DealerProtocolTests.RequestEnvelope(PauseRequest, "invalid", "k2")), sendTx.Writer);

        Assert.True(sendTx.Reader.TryRead(out var first));
        Assert.Equal(Reply("k1", false), Assert.IsType<WsMessage.Text>(first).Value);
        Assert.True(sendTx.Reader.TryRead(out var second));
        Assert.Equal(Reply("k2", false), Assert.IsType<WsMessage.Text>(second).Value);
    }

    [Fact]
    public async Task Subscription_disposed_before_first_read_is_pruned()
    {
        var builder = new Builder();
        var subscription = builder.Subscribe("hm://pusher/v1/connections/");
        var shared = new DealerShared(builder.MessageHandlers, builder.RequestHandlers);
        var sendTx = Channel.CreateUnbounded<WsMessage>();

        // never advanced, only disposed (e.g. a consumer that gives up before the first message)
        await subscription.GetAsyncEnumerator().DisposeAsync();

        Assert.True(shared.Handles("hm://pusher/v1/connections/"));
        shared.Dispatch(MessageOrRequest.Parse("""{"type":"message","uri":"hm://pusher/v1/connections/abc"}"""), sendTx.Writer);
        Assert.False(shared.Handles("hm://pusher/v1/connections/"));
    }

    [Fact]
    public async Task Release_answers_unconsumed_requests_with_failure()
    {
        var builder = new Builder();
        var (handler, requests) = DealerRequestHandler.Create();
        builder.AddHandler("hm://connect-state/v1/player/command", handler);
        var shared = new DealerShared(builder.MessageHandlers, builder.RequestHandlers);
        var sendTx = Channel.CreateUnbounded<WsMessage>();

        shared.Dispatch(MessageOrRequest.Parse(DealerProtocolTests.RequestEnvelope(PauseRequest, key: "k1")), sendTx.Writer);
        Assert.False(sendTx.Reader.TryRead(out _));

        // the consumer never reads the request; closing the dealer must still release its responder
        shared.Release();

        var reply = await sendTx.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal(Reply("k1", false), Assert.IsType<WsMessage.Text>(reply).Value);
        Assert.True(requests.Completion.IsCompleted);
        Assert.False(requests.TryRead(out _));
    }

    [Fact]
    public async Task Dealer_end_to_end_over_loopback_websocket()
    {
        await using var server = new LoopbackWebSocketServer();

        var builder = new Builder();
        var subscription = builder.Subscribe("hm://pusher/v1/connections/");
        var (handler, requests) = DealerRequestHandler.Create();
        builder.AddHandler("hm://connect-state/v1/player/command", handler);

        var accept = server.AcceptAsync();
        var urls = 0;
        var dealer = await builder.LaunchAsync(_ =>
        {
            Interlocked.Increment(ref urls);
            return Task.FromResult(server.Url);
        }, null).WaitAsync(Timeout);
        var ws = await accept.WaitAsync(Timeout);
        Assert.Equal("/?access_token=test", server.LastRequestTarget);

        // message -> subscription
        await ws.SendTextAsync("""{"type":"message","uri":"hm://pusher/v1/connections/abc","headers":{"Spotify-Connection-Id":"abc"}}""");
        await using var messages = subscription.GetAsyncEnumerator();
        Assert.True(await messages.MoveNextAsync().AsTask().WaitAsync(Timeout));
        Assert.Equal("hm://pusher/v1/connections/abc", messages.Current.Uri);

        // request -> handler -> reply
        await ws.SendTextAsync(DealerProtocolTests.RequestEnvelope(PauseRequest, key: "k1"));
        var request = await requests.ReadAsync().AsTask().WaitAsync(Timeout);
        Assert.IsType<Command.Pause>(request.Request.Command);
        Assert.Equal("phone", request.Request.SentByDeviceId);
        Assert.True(request.ReplyTo.TryWrite(Librespot.Core.Dealer.Reply.Success));
        Assert.Equal(Reply("k1", true), await ws.ReceiveTextAsync().WaitAsync(Timeout));

        // a dropped (completed) reply channel answers "failure"
        await ws.SendTextAsync(DealerProtocolTests.RequestEnvelope(PauseRequest, key: "k2"));
        request = await requests.ReadAsync().AsTask().WaitAsync(Timeout);
        request.ReplyTo.TryComplete();
        Assert.Equal(Reply("k2", false), await ws.ReceiveTextAsync().WaitAsync(Timeout));

        // unanswered: nothing is sent, so the next frame is the reply to the following request
        await ws.SendTextAsync(DealerProtocolTests.RequestEnvelope(PauseRequest, key: "k3"));
        request = await requests.ReadAsync().AsTask().WaitAsync(Timeout);
        request.ReplyTo.TryWrite(Librespot.Core.Dealer.Reply.Unanswered);
        await ws.SendTextAsync(DealerProtocolTests.RequestEnvelope(PauseRequest, "hm://nobody/home", "k4"));
        Assert.Equal(Reply("k4", false), await ws.ReceiveTextAsync().WaitAsync(Timeout));

        // unparsable frames are logged and skipped
        await ws.SendTextAsync("""{"type":"pong"}""");
        await ws.SendTextAsync("""{"type":"message","uri":"hm://pusher/v1/connections/def"}""");
        Assert.True(await messages.MoveNextAsync().AsTask().WaitAsync(Timeout));
        Assert.Equal("hm://pusher/v1/connections/def", messages.Current.Uri);

        // close: the dealer sends a close frame, the streams end
        var close = dealer.CloseAsync();
        Assert.Equal(WebSocketMessageType.Close, (await ws.ReceiveAsync(new byte[16], default).WaitAsync(Timeout)).MessageType);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
        await close.WaitAsync(Timeout);

        Assert.False(await messages.MoveNextAsync().AsTask().WaitAsync(Timeout));
        await requests.Completion.WaitAsync(Timeout);
        Assert.Equal(1, urls);
    }

    [Fact]
    public async Task Dealer_reconnects_with_a_fresh_url_when_the_connection_is_lost()
    {
        await using var server = new LoopbackWebSocketServer();

        var builder = new Builder();
        var subscription = builder.Subscribe("hm://a/b");
        var urls = 0;
        var accept = server.AcceptAsync();
        var dealer = await builder.LaunchAsync(_ =>
        {
            Interlocked.Increment(ref urls);
            return Task.FromResult(server.Url);
        }, null).WaitAsync(Timeout);
        var first = await accept.WaitAsync(Timeout);

        // the server goes away
        accept = server.AcceptAsync();
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);

        var second = await accept.WaitAsync(Timeout);
        Assert.Equal(2, urls);

        // subscriptions survive the reconnect
        await second.SendTextAsync("""{"type":"message","uri":"hm://a/b/c","payloads":["AQI="]}""");
        await using var messages = subscription.GetAsyncEnumerator();
        Assert.True(await messages.MoveNextAsync().AsTask().WaitAsync(Timeout));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<PayloadValue.Raw>(messages.Current.Payload).Value);

        var close = dealer.CloseAsync();
        Assert.Equal(WebSocketMessageType.Close, (await second.ReceiveAsync(new byte[16], default).WaitAsync(Timeout)).MessageType);
        await second.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
        await close.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Launch_failure_is_reported_and_ends_subscriptions()
    {
        var builder = new Builder();
        var subscription = builder.Subscribe("hm://a/b");

        var e = await Assert.ThrowsAnyAsync<LibrespotException>(() =>
            builder.LaunchAsync(_ => Task.FromResult(new Uri("http://127.0.0.1/")), null));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
        Assert.Equal("URL error: URL scheme not supported", e.Detail);

        await using var messages = subscription.GetAsyncEnumerator();
        Assert.False(await messages.MoveNextAsync().AsTask().WaitAsync(Timeout));
    }

    [Fact]
    public void DealerManager_registers_on_the_builder_before_start()
    {
        var session = new Session(SessionConfig.Default(), null);
        var manager = session.Dealer;

        Assert.False(manager.Handles("hm://pusher/v1/connections/"));
        _ = manager.AddListenFor("hm://pusher/v1/connections/");
        Assert.True(manager.Handles("hm://pusher/v1/connections/abc"));

        var invalid = Assert.ThrowsAny<LibrespotException>(() => manager.AddListenFor("not-a-uri"));
        Assert.Equal(ErrorKind.InvalidArgument, invalid.Kind);

        _ = manager.HandleFor("hm://connect-state/v1/player/command");
        // upstream quirk: request handlers are not found by `handles`
        Assert.False(manager.Handles("hm://connect-state/v1/player/command"));
        var twice = Assert.ThrowsAny<LibrespotException>(() => manager.AddHandleFor("hm://connect-state/v1/player/command"));
        Assert.Equal(ErrorKind.Aborted, twice.Kind);

        Assert.Equal(ErrorKind.InvalidArgument,
            Assert.ThrowsAny<LibrespotException>(() => manager.AddHandleFor("nope")).Kind);
    }

    [Fact]
    public async Task DealerManager_close_without_start_is_a_no_op()
    {
        var session = new Session(SessionConfig.Default(), null);
        await session.Dealer.CloseAsync().WaitAsync(Timeout);
    }
}

/// <summary>A minimal RFC 6455 server on 127.0.0.1 for dealer tests.</summary>
internal sealed class LoopbackWebSocketServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<IDisposable> _resources = [];

    public LoopbackWebSocketServer()
    {
        _listener.Start();
    }

    public Uri Url => new($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/?access_token=test");

    public string? LastRequestTarget { get; private set; }

    public async Task<WebSocket> AcceptAsync()
    {
        var client = await _listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        lock (_resources)
        {
            _resources.Add(client);
        }

        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                throw new IOException("client closed during handshake");
            }

            head.Append((char)one[0]);
        }

        var lines = head.ToString().Split("\r\n");
        LastRequestTarget = lines[0].Split(' ')[1];
        var key = lines.First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));

        var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
        lock (_resources)
        {
            _resources.Add(ws);
        }

        return ws;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        lock (_resources)
        {
            foreach (var resource in _resources)
            {
                resource.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }
}

internal static class WebSocketTestExtensions
{
    public static Task SendTextAsync(this WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);

    public static async Task<string> ReceiveTextAsync(this WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, default);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.ToArray());
    }
}
