using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Airplay2.Receiver;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Session;
using Bridge.Contracts;
using Bridge.Service;
using Bridge.Service.Airplay;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Bridge.Service.Store;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.Tests.Service;

/// <summary>
/// The AirPlay fields of the API (SPEC-receiver.md 9.1, acceptance of 11.1 and 13 wave 3 <c>api</c>): settings masking and
/// null-keeps, the status/playback/diagnostics JSON shapes, <c>GET /api/playback/artwork</c> with its <c>ETag</c>, the 409
/// of <c>POST /api/playback</c> while AirPlay owns the output, and the WebSocket snapshot. The sources are a stub
/// <see cref="IAirplayApiView"/>; nothing binds an AirPlay port (R13).
/// </summary>
public sealed class AirplayApiTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    private static readonly SenderInfo TestSender = new("Test Phone", "iPhone15,2", "02:00:00:00:00:01", IPAddress.Parse("192.0.2.10"));

    [Fact]
    public async Task Settings_carry_the_receiver_fields_and_never_the_password()
    {
        var view = new StubAirplayView();
        await using var host = await TestHost.StartAsync(view);
        var client = host.Client;

        // Bridge:Airplay:Enabled is false in the tests; the name follows the device name while none is stored.
        var settings = await client.GetJsonAsync<SettingsDto>(ApiRoutes.Settings);
        Assert.Equal(new SettingsDto("TuneShuttle", 77175, false, "Realtime", 3, false, "TuneShuttle", null, false), settings);

        // Set: enabled, a name and a password; the answer says the password is set and never carries it.
        using (var response = await client.SendJsonAsync(HttpMethod.Put, ApiRoutes.Settings, settings with { AirplayEnabled = true, AirplayName = "  Küche  ", AirplayPassword = "s3cret-pass" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("s3cret-pass", json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.True(root.GetProperty("airplayEnabled").GetBoolean());
            Assert.Equal("Küche", root.GetProperty("airplayName").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("airplayPassword").ValueKind);
            Assert.True(root.GetProperty("airplayPasswordSet").GetBoolean());
        }

        var store = host.Get<IBridgeStore>();
        Assert.Equal("s3cret-pass", await store.GetSettingAsync(BridgeSettingKeys.AirplayPassword, CancellationToken.None));
        var applied = Assert.Single(view.Applied);
        Assert.Equal((true, "Küche", "s3cret-pass"), (applied.Enabled, applied.Name, applied.Password));

        var raw = await client.GetStringAsync(ApiRoutes.Settings);
        Assert.DoesNotContain("s3cret-pass", raw, StringComparison.Ordinal);

        // Null keeps every receiver field (a client that predates them), AirplayPasswordSet in a PUT is ignored.
        var kept = await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, new SettingsDto("TuneShuttle", 77175, false, "Realtime", AirplayPasswordSet: false));
        Assert.Equal(new SettingsDto("TuneShuttle", 77175, false, "Realtime", 3, true, "Küche", null, true), kept);
        Assert.Single(view.Applied);

        // "" clears the password; the receiver hears of it.
        var cleared = await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, kept with { AirplayPassword = string.Empty });
        Assert.False(cleared.AirplayPasswordSet);
        Assert.Null(cleared.AirplayPassword);
        Assert.Null(await store.GetSettingAsync(BridgeSettingKeys.AirplayPassword, CancellationToken.None));
        Assert.Equal(2, view.Applied.Count);
        Assert.Null(view.Applied[^1].Password);
        Assert.Equal(cleared, await client.GetJsonAsync<SettingsDto>(ApiRoutes.Settings));

        // Validation: the name takes 1 to 63 characters, the password up to 63; nothing is stored on a 422.
        foreach (var invalid in new[]
        {
            cleared with { AirplayName = string.Empty },
            cleared with { AirplayName = "   " },
            cleared with { AirplayName = new string('n', 64) },
            cleared with { AirplayPassword = new string('p', 64) },
        })
        {
            using var response = await client.SendJsonAsync(HttpMethod.Put, ApiRoutes.Settings, invalid);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(cleared, await client.GetJsonAsync<SettingsDto>(ApiRoutes.Settings));
        var longest = await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, cleared with { AirplayName = new string('n', 63), AirplayPassword = new string('p', 63) });
        Assert.Equal(new string('n', 63), longest.AirplayName);
        Assert.True(longest.AirplayPasswordSet);

        // Disabling reaches the receiver; changes while it stays disabled are only stored.
        var disabled = await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, longest with { AirplayEnabled = false });
        Assert.False(disabled.AirplayEnabled);
        var count = view.Applied.Count;
        Assert.False(view.Applied[^1].Enabled);
        var renamed = await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, disabled with { AirplayName = "Bad" });
        Assert.Equal("Bad", renamed.AirplayName);
        Assert.Equal(count, view.Applied.Count);
    }

    [Fact]
    public async Task Status_reports_the_active_source_and_the_receiver_in_camel_case()
    {
        var view = new StubAirplayView();
        await using var host = await TestHost.StartAsync(view);
        var client = host.Client;

        using (var document = JsonDocument.Parse(await client.GetStringAsync(ApiRoutes.Status)))
        {
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("activeSource").ValueKind);
            var airplay = root.GetProperty("airplay");
            Assert.Equal("off", airplay.GetProperty("state").GetString());
            Assert.Equal("TuneShuttle", airplay.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, airplay.GetProperty("senderName").ValueKind);
            Assert.Equal(JsonValueKind.Null, airplay.GetProperty("senderModel").ValueKind);
            Assert.False(airplay.GetProperty("aacAvailable").GetBoolean());
        }

        view.Owner = AudioSource.Airplay;
        view.Status = ReceiverStatus.Streaming;
        view.Sender = TestSender;
        view.AacAvailable = true;
        view.Name = "Wohnzimmer";
        var status = await client.GetJsonAsync<StatusDto>(ApiRoutes.Status);
        Assert.Equal("airplay", status.ActiveSource);
        Assert.Equal(new AirplayReceiverDto("streaming", "Wohnzimmer", "Test Phone", "iPhone15,2", true), status.Airplay);

        view.Owner = AudioSource.Spotify;
        view.Status = ReceiverStatus.Advertising;
        view.Sender = null;
        status = await client.GetJsonAsync<StatusDto>(ApiRoutes.Status);
        Assert.Equal("spotify", status.ActiveSource);
        Assert.Equal("advertising", status.Airplay?.State);

        foreach (var (state, name) in new[]
        {
            (ReceiverStatus.Off, "off"), (ReceiverStatus.Starting, "starting"), (ReceiverStatus.Advertising, "advertising"),
            (ReceiverStatus.Connected, "connected"), (ReceiverStatus.Streaming, "streaming"), (ReceiverStatus.Error, "error"),
        })
        {
            Assert.Equal(name, Mapping.ToApiName(state));
        }
    }

    [Fact]
    public async Task Artwork_is_404_until_an_airplay_track_brings_some_then_200_with_an_etag()
    {
        var view = new StubAirplayView();
        await using var host = await TestHost.StartAsync(view);
        var client = host.Client;
        var dtos = host.Get<DtoFactory>();
        var runtime = host.Get<RuntimeState>();

        using (var missing = await client.GetAsync(ApiRoutes.PlaybackArtwork))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // AirPlay owns the output but no artwork arrived yet.
        view.Owner = AudioSource.Airplay;
        using (var missing = await client.GetAsync(ApiRoutes.PlaybackArtwork))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // The stubbed now-playing of AirplaySource (SPEC-receiver.md 8.3): DMAP, artwork and progress of rtptime 12345.
        var metadata = new TrackMetadata("Synthetic Song", "Test Artist", "Test Album", TimeSpan.FromSeconds(200));
        runtime.Playback = dtos.AirplayPlayback(metadata, playing: true, TimeSpan.FromSeconds(12.5), Jpeg, "image/jpeg", 12345u, level: 40);

        using (var document = JsonDocument.Parse(await client.GetStringAsync(ApiRoutes.Playback)))
        {
            var root = document.RootElement;
            Assert.Equal("airplay", root.GetProperty("source").GetString());
            Assert.Equal("/api/playback/artwork?v=12345", root.GetProperty("artworkUrl").GetString());
            Assert.Equal("Synthetic Song", root.GetProperty("title").GetString());
            Assert.Equal("Test Artist", root.GetProperty("artist").GetString());
            Assert.Equal("Test Album", root.GetProperty("album").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("trackId").ValueKind);
            Assert.Equal(12500, root.GetProperty("positionMs").GetInt64());
            Assert.Equal(200000, root.GetProperty("durationMs").GetInt64());
            Assert.True(root.GetProperty("playing").GetBoolean());
        }

        using (var artwork = await client.GetAsync("/api/playback/artwork?v=12345"))
        {
            Assert.Equal(HttpStatusCode.OK, artwork.StatusCode);
            Assert.Equal("image/jpeg", artwork.Content.Headers.ContentType?.MediaType);
            Assert.Equal("\"12345\"", artwork.Headers.ETag?.Tag);
            Assert.Equal(Jpeg, await artwork.Content.ReadAsByteArrayAsync());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.PlaybackArtwork))
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"12345\""));
            using var notModified = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        }

        // Metadata of the same rtptime without the bytes keeps the artwork; a new track without artwork drops it.
        runtime.Playback = dtos.AirplayPlayback(metadata, playing: false, TimeSpan.FromSeconds(13), null, null, 12345u, level: 40);
        Assert.Equal("/api/playback/artwork?v=12345", (await client.GetJsonAsync<PlaybackDto>(ApiRoutes.Playback)).ArtworkUrl);
        runtime.Playback = dtos.AirplayPlayback(metadata with { Title = "Next" }, playing: true, TimeSpan.Zero, null, null, 99999u, level: 40);
        Assert.Null((await client.GetJsonAsync<PlaybackDto>(ApiRoutes.Playback)).ArtworkUrl);
        using (var missing = await client.GetAsync(ApiRoutes.PlaybackArtwork))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // Spotify took over: the AirPlay artwork is not served, even while it is still kept.
        runtime.Playback = dtos.AirplayPlayback(metadata, playing: true, TimeSpan.Zero, Jpeg, "image/png", 777u, level: 40);
        using (var png = await client.GetAsync(ApiRoutes.PlaybackArtwork))
        {
            Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        }

        view.Owner = AudioSource.Spotify;
        using (var missing = await client.GetAsync(ApiRoutes.PlaybackArtwork))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        view.Owner = AudioSource.Airplay;
        dtos.ClearAirplayArtwork();
        using (var missing = await client.GetAsync(ApiRoutes.PlaybackArtwork))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // The artwork the source keeps itself (AirplaySource.Artwork) is served the same way.
        view.Artwork = new AirplayArtwork(Jpeg, "image/jpeg", 4242u);
        using (var kept = await client.GetAsync(ApiRoutes.PlaybackArtwork))
        {
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
            Assert.Equal("\"4242\"", kept.Headers.ETag?.Tag);
        }
    }

    [Fact]
    public async Task Playback_commands_answer_409_while_airplay_owns_the_output()
    {
        var view = new StubAirplayView { Owner = AudioSource.Airplay };
        await using var host = await TestHost.StartAsync(view);
        var client = host.Client;

        foreach (var command in new[] { "play", "pause", "next", "prev" })
        {
            using var conflict = await client.SendJsonAsync(HttpMethod.Post, ApiRoutes.Playback, new PlaybackCommandRequest(command));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("AirPlay", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        }

        // An unknown command is still a validation error.
        using (var invalid = await client.SendJsonAsync(HttpMethod.Post, ApiRoutes.Playback, new PlaybackCommandRequest("dance")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        // Without AirPlay the command goes to Spotify (not connected in the tests: the other 409).
        view.Owner = AudioSource.None;
        using var spotify = await client.SendJsonAsync(HttpMethod.Post, ApiRoutes.Playback, new PlaybackCommandRequest("play"));
        Assert.Equal(HttpStatusCode.Conflict, spotify.StatusCode);
        Assert.Contains("Spotify", (await spotify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_carry_the_playout_and_ptp_statistics()
    {
        var view = new StubAirplayView();
        await using var host = await TestHost.StartAsync(view);
        var client = host.Client;

        var idle = (await client.GetJsonAsync<DiagnosticsDto>(ApiRoutes.Diagnostics)).Airplay;
        Assert.NotNull(idle);
        Assert.Equal(new AirplayDiagnosticsDto(0, false, null, null, null, 0, 0, 0, 0, 0, StubAirplayView.NoDecoder), idle);

        view.RtspPort = 7000;
        view.PtpBound = true;
        view.PtpStatistics = new PtpSlaveStatistics(-1_234_567, 12, TimeSpan.FromMilliseconds(120), 0x0011223344556677);
        view.Statistics = new PlayoutStatistics(1750, TimeSpan.FromMilliseconds(452.4), 3, 4, 5, 6, "ALAC", StreamType.Realtime, 1000);
        using var document = JsonDocument.Parse(await client.GetStringAsync(ApiRoutes.Diagnostics));
        var airplay = document.RootElement.GetProperty("airplay");
        Assert.Equal(7000, airplay.GetProperty("rtspPort").GetInt32());
        Assert.True(airplay.GetProperty("ptpBound").GetBoolean());
        Assert.Equal(-1_234_567, airplay.GetProperty("ptpOffsetNs").GetInt64());
        Assert.Equal("realtime", airplay.GetProperty("streamType").GetString());
        Assert.Equal("ALAC", airplay.GetProperty("format").GetString());
        Assert.Equal(1750, airplay.GetProperty("bufferedMs").GetInt32());
        Assert.Equal(452, airplay.GetProperty("streamOffsetMs").GetInt32());
        Assert.Equal(3, airplay.GetProperty("lateFrames").GetInt64());
        Assert.Equal(4, airplay.GetProperty("missingFrames").GetInt64());
        Assert.Equal(11, airplay.GetProperty("driftCorrections").GetInt64());
        Assert.Equal(StubAirplayView.NoDecoder, airplay.GetProperty("decoderStatus").GetString());

        Assert.Equal("buffered", Mapping.ToApiName(StreamType.Buffered));
        Assert.Null(Mapping.ToApiName((StreamType?)null));
    }

    [Fact]
    public async Task The_websocket_snapshot_carries_the_new_fields()
    {
        var view = new StubAirplayView { Owner = AudioSource.Airplay, Status = ReceiverStatus.Streaming, Sender = TestSender };
        await using var host = await TestHost.StartAsync(view);
        var dtos = host.Get<DtoFactory>();
        host.Get<RuntimeState>().Playback = dtos.AirplayPlayback(new TrackMetadata("Synthetic Song", "Test Artist", null, null), true, TimeSpan.Zero, Jpeg, "image/jpeg", 5u, 50);

        var socketClient = host.App.Server.CreateWebSocketClient();
        using var socket = await socketClient.ConnectAsync(new Uri(host.App.Server.BaseAddress, ApiRoutes.Events.TrimStart('/')).ToWebSocketUri(), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        JsonElement? status = null;
        JsonElement? playback = null;
        while (status is null || playback is null)
        {
            using var document = JsonDocument.Parse(await ReceiveTextAsync(socket, timeout.Token));
            var root = document.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "statusChanged":
                    status = root.GetProperty("status").Clone();
                    break;
                case "playbackChanged":
                    playback = root.GetProperty("playback").Clone();
                    break;
            }
        }

        Assert.Equal("airplay", status.Value.GetProperty("activeSource").GetString());
        Assert.Equal("streaming", status.Value.GetProperty("airplay").GetProperty("state").GetString());
        Assert.Equal("Test Phone", status.Value.GetProperty("airplay").GetProperty("senderName").GetString());
        Assert.Equal("airplay", playback.Value.GetProperty("source").GetString());
        Assert.Equal("/api/playback/artwork?v=5", playback.Value.GetProperty("artworkUrl").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task Playback_names_the_owner_when_the_view_does_not()
    {
        var view = new StubAirplayView();
        await using var host = await TestHost.StartAsync(view);
        var client = host.Client;

        Assert.Null((await client.GetJsonAsync<PlaybackDto>(ApiRoutes.Playback)).Source);
        view.Owner = AudioSource.Spotify;
        Assert.Equal("spotify", (await client.GetJsonAsync<PlaybackDto>(ApiRoutes.Playback)).Source);
    }

    [Fact]
    public async Task The_openapi_document_lists_the_artwork_route()
    {
        await using var host = await TestHost.StartAsync(new StubAirplayView());
        using var openApi = await host.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty(ApiRoutes.PlaybackArtwork, out _));
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        var length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
            length += result.Count;
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// <summary>The bridge in-process with a stub <see cref="IAirplayApiView"/>.</summary>
    private sealed class TestHost : IAsyncDisposable
    {
        private TestHost(BridgeFactory bridge, WebApplicationFactory<BridgeController> app)
        {
            Bridge = bridge;
            App = app;
            Client = app.CreateClient();
        }

        public BridgeFactory Bridge { get; }

        public WebApplicationFactory<BridgeController> App { get; }

        public HttpClient Client { get; }

        public static Task<TestHost> StartAsync(StubAirplayView view)
        {
            var bridge = new BridgeFactory();
            var app = bridge.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAirplayApiView>(view)));
            return Task.FromResult(new TestHost(bridge, app));
        }

        /// <summary>Resolves from the derived host (the one serving <see cref="Client"/>).</summary>
        public T Get<T>()
            where T : notnull => App.Services.GetRequiredService<T>();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            await Bridge.DisposeAsync();
        }
    }

    /// <summary>A settable stand-in for the switch and the receiver (SPEC-receiver.md 8.2, 8.3).</summary>
    private sealed class StubAirplayView : IAirplayApiView
    {
        public const string NoDecoder = "AAC decoder not loaded: ALAC only";

        public List<AirplaySettings> Applied { get; } = [];

        public AudioSource Owner { get; set; }

        public ReceiverStatus Status { get; set; }

        public string? Name { get; set; }

        public SenderInfo? Sender { get; set; }

        public bool AacAvailable { get; set; }

        public int RtspPort { get; set; }

        public bool PtpBound { get; set; }

        public PlayoutStatistics? Statistics { get; set; }

        public PtpSlaveStatistics? PtpStatistics { get; set; }

        public string? DecoderStatus { get; set; } = NoDecoder;

        public AirplayArtwork? Artwork { get; set; }

        public Task ApplySettingsAsync(AirplaySettings settings, CancellationToken cancellationToken)
        {
            lock (Applied)
            {
                Applied.Add(settings);
            }

            return Task.CompletedTask;
        }
    }
}
