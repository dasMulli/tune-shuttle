using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Bridge.Contracts;
using Bridge.Tests.Service;
using Bridge.Web;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>DTO builders for the web app tests.</summary>
internal static class Dto
{
    public static readonly ReceiverCapabilitiesDto Caps = new(true, true, false, false, false, true, true, true, true, "0x1C340405D4A00", "0x4");

    public static ReceiverDto Receiver(string id, string name, int level = 50, ReceiverConnectionState state = ReceiverConnectionState.Available, PairingState pairing = PairingState.Transient, string? groupId = null, bool online = true, int offset = 0, string? error = null) =>
        new(id, name, "Model", "Maker", "192.0.2.1", 7000, online, pairing, state, level, offset, groupId, error, Caps);

    public static GroupDto Group(string id, string name, int master, bool active, params (string Id, string Name, int Level)[] members) =>
        new(id, name, master, active, [.. members.Select(m => new GroupMemberDto(m.Id, m.Name, m.Level, ReceiverConnectionState.Available))]);

    public static StatusDto Status(string deviceName = "AirPlay Bridge", bool spotify = false) =>
        new("0.1.0", spotify, spotify ? "martin" : null, deviceName, false, 1, DateTimeOffset.UnixEpoch);

    public static PlaybackDto Playback(bool playing = false, long position = 0, long duration = 0, string? title = null, int volume = 40) =>
        new(playing, title is null ? null : "spotify:track:1", title, title is null ? null : "Artist", title is null ? null : "Album", position, duration, null, volume);

    public static OutputDto NoOutput { get; } = new(null, null, 0, []);
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An HTTP handler that answers from a function and records every request (method, path, body).</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
        : this((request, body) => Task.FromResult(respond(request, body)))
    {
    }

    public ConcurrentQueue<(HttpMethod Method, string Path, string? Body)> Requests { get; } = new();

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: BridgeApiClient.JsonOptions) };

    public static HttpResponseMessage Problem(HttpStatusCode status, string title, string detail) =>
        new(status) { Content = new StringContent($"{{\"title\":\"{title}\",\"status\":{(int)status},\"detail\":\"{detail}\"}}", System.Text.Encoding.UTF8, "application/problem+json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        Requests.Enqueue((request.Method, path, body));
        return await respond(request, body);
    }
}

/// <summary>Builds the web app's clients against the in-process bridge.</summary>
internal static class WebClients
{
    public static BridgeApiClient Api(BridgeFactory factory) => new(factory.CreateClient());

    /// <summary>The event client over the test server's WebSocket; <paramref name="opened"/> collects every socket it opens.</summary>
    public static BridgeEventClient Events(BridgeFactory factory, BridgeState state, BridgeEventClientOptions? options = null, ConcurrentQueue<WebSocket>? opened = null, ILogger<BridgeEventClient>? logger = null, Func<Task>? beforeConnect = null) =>
        new(
            state,
            BridgeEventClient.EventsUri(factory.Server.BaseAddress),
            logger ?? NullLogger<BridgeEventClient>.Instance,
            options ?? new BridgeEventClientOptions { InitialRetryDelay = TimeSpan.FromMilliseconds(50), MaxRetryDelay = TimeSpan.FromMilliseconds(200) },
            async (uri, ct) =>
            {
                if (beforeConnect is not null)
                {
                    await beforeConnect();
                }

                var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct);
                opened?.Enqueue(socket);
                return socket;
            });

    /// <summary>Polls until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? RecordedFake.Patience);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(25);
        }
    }
}
