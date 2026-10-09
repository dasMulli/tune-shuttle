using System.Net;
using System.Net.WebSockets;
using Airplay2.FakeReceiver;
using Bridge.Contracts;
using Bridge.Tests.Service;
using Bridge.Web;
using Bridge.Web.Components;
using Bridge.Web.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// bUnit component tests of the pages and the PIN dialog (WORK-PLAN.md web-app "Verify": bUnit 2.11.3 supports .NET 10).
/// The API is a stub handler that records requests; the state is fed directly, as the event client would.
/// </summary>
public sealed class WebComponentTests : IAsyncDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly BridgeState _state = new();

    public WebComponentTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddLogging();
        _ctx.Services.AddSingleton(_state);
    }

    public ValueTask DisposeAsync() => _ctx.DisposeAsync();

    private StubHandler UseApi(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        _ctx.Services.AddSingleton(new BridgeApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://bridge.test/") }));
        _ctx.Services.AddSingleton(new BridgeEventClient(
            _state,
            new Uri("ws://bridge.test/ws/events"),
            NullLogger<BridgeEventClient>.Instance,
            connect: (_, _) => Task.FromException<WebSocket>(new WebSocketException("not started in component tests"))));
        _ctx.Services.AddSingleton<BridgeCommands>();
        return handler;
    }

    private StubHandler UseApi(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) => UseApi((r, b) => Task.FromResult(respond(r, b)));

    private static string PathOf(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);

    [Fact]
    public void Receivers_page_lists_receivers_and_connect_applies_the_answer()
    {
        var api = UseApi((request, _) => PathOf(request) switch
        {
            "/api/receivers/02:FA:CE:00:00:01/connect" => StubHandler.Json(Dto.Receiver("02:FA:CE:00:00:01", "Fake Receiver 1", state: ReceiverConnectionState.Connected)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status("Wohnzimmer")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("02:FA:CE:00:00:01", "Fake Receiver 1")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("02:FA:CE:00:00:02", "Fake Receiver 2", pairing: PairingState.PinRequired, online: false)));

        var cut = _ctx.Render<Receivers>();

        Assert.Contains("Wohnzimmer", cut.Markup, StringComparison.Ordinal);
        var cards = cut.FindAll("article.receiver");
        Assert.Equal(2, cards.Count);
        Assert.Contains("PIN pairing needed", cards[1].TextContent, StringComparison.Ordinal);
        Assert.True(cards[1].QuerySelector("button.primary")!.HasAttribute("disabled")); // offline: no connect

        cut.Find("article[data-receiver-id='02:FA:CE:00:00:01'] button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/receivers/02:FA:CE:00:00:01/connect");
            Assert.Contains("Disconnect", cut.Find("article[data-receiver-id='02:FA:CE:00:00:01']").TextContent, StringComparison.Ordinal);
            Assert.Equal("Connected", cut.Find("article[data-receiver-id='02:FA:CE:00:00:01'] .badge").TextContent);
        });
    }

    [Fact]
    public void Receiver_slider_and_offset_send_their_values()
    {
        var api = UseApi((request, body) => PathOf(request) switch
        {
            "/api/receivers/a/volume" => StubHandler.Json(Dto.Receiver("a", "A", level: 73)),
            "/api/receivers/a/offset" => StubHandler.Json(Dto.Receiver("a", "A", offset: -441)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A", level: 50)));
        var cut = _ctx.Render<Receivers>();

        cut.Find("input[type=range]").Input("73");
        cut.WaitForAssertion(() =>
        {
            var request = Assert.Single(api.Requests, r => r.Path == "/api/receivers/a/volume");
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Contains("\"level\":73", request.Body, StringComparison.Ordinal);
            Assert.Equal("73", cut.Find(".level output").TextContent);
        });

        // An offset beyond ±2 s is refused before it reaches the service.
        cut.Find("form.offset input").Change("100000");
        cut.Find("form.offset").Submit();
        Assert.Contains("within", cut.Find("article.receiver p.error").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Requests, r => r.Path == "/api/receivers/a/offset");

        cut.Find("form.offset input").Change("-441");
        cut.Find("form.offset").Submit();
        cut.WaitForAssertion(() => Assert.Contains(api.Requests, r => r.Path == "/api/receivers/a/offset" && r.Body!.Contains("-441", StringComparison.Ordinal)));
        Assert.Equal(-441, _state.Receiver("a")!.OffsetFrames);
    }

    [Fact]
    public void Pairing_dialog_opens_on_pairing_required_handles_a_wrong_pin_and_pairs()
    {
        var api = UseApi((request, body) => PathOf(request) switch
        {
            "/api/receivers/pin/pair/pin" when body!.Contains("\"pin\":\"0000\"", StringComparison.Ordinal) =>
                StubHandler.Problem(HttpStatusCode.UnprocessableEntity, "Wrong PIN", "The receiver rejected the PIN; start the pairing again."),
            "/api/receivers/pin/pair/pin" => StubHandler.Json(Dto.Receiver("pin", "PIN Speaker", pairing: PairingState.Paired)),
            "/api/receivers/pin/pair" => StubHandler.Json(new PairingStartedDto("token-2", "Enter the PIN that PIN Speaker shows.")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("pin", "PIN Speaker", pairing: PairingState.PinRequired)));
        var cut = _ctx.Render<PairingDialog>();
        Assert.Empty(cut.FindAll("[role=dialog]"));

        // The service announces that a connect needs a PIN.
        _state.Apply(new BridgeEvent.PairingRequired("pin", "token-1"));
        cut.WaitForAssertion(() => Assert.Contains("Pair with PIN Speaker", cut.Find("[role=dialog]").TextContent, StringComparison.Ordinal));

        cut.Find("input.pin").Input("0000");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("rejected", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
            Assert.Equal("Start again", cut.Find("button.primary").TextContent);
        });
        Assert.Contains(api.Requests, r => r.Path == "/api/receivers/pin/pair/pin" && r.Body!.Contains("token-1", StringComparison.Ordinal));

        // Start again: the receiver shows a new PIN; the field is empty and the error gone.
        cut.Find("button.primary").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("token-2", _state.Pairing?.Token);
            Assert.Equal("Pair", cut.Find("button.primary").TextContent);
            Assert.Empty(cut.FindAll("[role=alert]"));
        });

        cut.Find("input.pin").Input("4711");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=dialog]")));
        Assert.Equal(PairingState.Paired, _state.Receiver("pin")!.Pairing);
        Assert.Contains(api.Requests, r => r.Path == "/api/receivers/pin/pair/pin" && r.Body!.Contains("token-2", StringComparison.Ordinal) && r.Body.Contains("4711", StringComparison.Ordinal));
    }

    [Fact]
    public void Pairing_dialog_cancel_closes_without_calling_the_service()
    {
        var api = UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.OpenPairing(new PairingPrompt("pin", "t", "Enter the PIN.", PairingPromptOrigin.User));
        var cut = _ctx.Render<PairingDialog>();
        cut.FindAll("button").Single(b => b.TextContent == "Cancel").Click();
        Assert.Null(_state.Pairing);
        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void Group_master_slider_previews_the_scaled_member_levels_before_the_service_answers()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, _) => PathOf(request) == "/api/groups/g/volume" ? answer.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Living room")));
        _state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "Downstairs", 50, false, ("a", "Kitchen", 30), ("b", "Living room", 80))));
        var cut = _ctx.Render<Groups>();

        string[] MemberLevels() => [.. cut.FindAll("li.member .level output").Select(o => o.TextContent)];
        Assert.Equal(["30", "80"], MemberLevels());

        // Drag G from 50 to 60: r_i * 60/50 shows at once (36, 96); the request is still in flight.
        cut.Find("article.group .level input[type=range]").Input("60");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["36", "96"], MemberLevels());
            Assert.NotEmpty(cut.FindAll("li.member.previewing"));
        });
        cut.Find("article.group .level input[type=range]").Input("70");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["42", "100"], MemberLevels()); // 80 * 70/50 = 112 clamps at 100
            Assert.Contains("clamp", cut.Markup, StringComparison.Ordinal);
        });

        // The service answers the first request; the newest value (70) follows; then the preview gives way to its data.
        answer.SetResult(StubHandler.Json(Dto.Group("g", "Downstairs", 60, false, ("a", "Kitchen", 36), ("b", "Living room", 96))));
        cut.WaitForAssertion(() => Assert.Equal(2, api.Requests.Count(r => r.Path == "/api/groups/g/volume")));
        var bodies = api.Requests.Where(r => r.Path == "/api/groups/g/volume").Select(r => r.Body).ToList();
        Assert.Contains("60", bodies[0], StringComparison.Ordinal);
        Assert.Contains("70", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Groups_page_creates_adds_members_and_activates()
    {
        var created = Dto.Group("g", "Upstairs", 50, false);
        var withMember = Dto.Group("g", "Upstairs", 50, false, ("a", "Bedroom", 50));
        var api = UseApi((request, body) => (request.Method.Method, PathOf(request)) switch
        {
            ("POST", "/api/groups") => StubHandler.Json(created, HttpStatusCode.Created),
            ("POST", "/api/groups/g/members") => StubHandler.Json(withMember),
            ("POST", "/api/groups/g/activate") => StubHandler.Json(new OutputDto("g", null, 50, withMember.Members)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Bedroom")));
        var cut = _ctx.Render<Groups>();
        Assert.Contains("No groups yet", cut.Markup, StringComparison.Ordinal);

        cut.Find("#new-group").Input("Upstairs");
        cut.Find("form.create-group").Submit();
        cut.WaitForAssertion(() => Assert.Contains("Upstairs", cut.Find("article.group h2").TextContent, StringComparison.Ordinal));
        Assert.True(cut.FindAll("article.group button").Single(b => b.TextContent == "Activate").HasAttribute("disabled")); // no members yet

        cut.Find("form.add-member select").Change("a");
        cut.Find("form.add-member").Submit();
        cut.WaitForAssertion(() => Assert.Equal("Bedroom", cut.Find("li.member .member-name").TextContent));
        Assert.Contains(api.Requests, r => r.Path == "/api/groups/g/members" && r.Body!.Contains("\"receiverId\":\"a\"", StringComparison.Ordinal));

        cut.FindAll("article.group button").Single(b => b.TextContent == "Activate").Click();
        cut.WaitForAssertion(() => Assert.Contains("Active", cut.Find("article.group").TextContent, StringComparison.Ordinal));
        Assert.Equal("g", _state.Output.GroupId);
    }

    [Fact]
    public void Settings_page_validates_like_the_service_and_saves()
    {
        var api = UseApi((request, body) => (request.Method.Method, PathOf(request)) switch
        {
            ("GET", "/api/settings") => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")),
            ("PUT", "/api/settings") => StubHandler.Json(new SettingsDto("Küche", 88200, true, "Buffered")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value")));

        cut.Find("#latency").Change("100");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("between 22050 and 176400", cut.Find(".validation-message").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Put);

        cut.Find("#device-name").Change("Küche");
        cut.Find("#latency").Change("88200");
        cut.Find("#stream").Change("Buffered");
        cut.Find("#poll").Change(true);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            var put = Assert.Single(api.Requests, r => r.Method == HttpMethod.Put);
            Assert.Contains("\"deviceName\":\"K\\u00FCche\"", put.Body, StringComparison.Ordinal);
            Assert.Contains("\"latencyFrames\":88200", put.Body, StringComparison.Ordinal);
            Assert.Contains("\"pollReceiverVolume\":true", put.Body, StringComparison.Ordinal);
            Assert.Contains("\"preferredStream\":\"Buffered\"", put.Body, StringComparison.Ordinal);
        });
        Assert.Contains(_state.Notices, n => n.Kind == NoticeKind.Success);
    }

    [Fact]
    public async Task Diagnostics_page_shows_statistics_and_the_live_log()
    {
        UseApi((request, _) => PathOf(request) == "/api/diagnostics"
            ? StubHandler.Json(new DiagnosticsDto(["10:00:00.000 info Old: line"], ["Ethernet: 192.0.2.10"], 6002, 6001, 1234, 5, 2, ["P4: buttons"]))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        var cut = _ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("6002", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Ethernet: 192.0.2.10", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("P4: buttons", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Old: line", cut.Find(".log-lines").TextContent, StringComparison.Ordinal); // REST history until live lines arrive
        });
        Assert.True(_ctx.Services.GetRequiredService<BridgeEventClient>().LogSubscribed);

        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "info", "Bridge", "hello"));
        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch.AddSeconds(1), "warn", "Bridge", "careful"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".log-line").Count));
        Assert.Single(cut.FindAll(".log-line.level-warn"));

        cut.Find("#log-level").Change("3");
        cut.WaitForAssertion(() => Assert.Contains("careful", Assert.Single(cut.FindAll(".log-line")).TextContent, StringComparison.Ordinal));

        await _ctx.DisposeComponentsAsync();
        await WebClients.WaitUntilAsync(() => !_ctx.Services.GetRequiredService<BridgeEventClient>().LogSubscribed, "the unsubscribe");
    }

    [Fact]
    public void Now_playing_shows_the_track_and_sends_transport_commands()
    {
        var api = UseApi((request, _) => PathOf(request) == "/api/playback"
            ? StubHandler.Json(Dto.Playback(playing: false, position: 1000, duration: 200_000, title: "Song"))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true)));
        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: true, position: 61_000, duration: 200_000, title: "Song")));
        var cut = _ctx.Render<NowPlaying>();

        Assert.Equal("Song", cut.Find(".title").TextContent);
        Assert.Contains("1:01", cut.Find(".times").TextContent, StringComparison.Ordinal);
        Assert.Contains("3:20", cut.Find(".times").TextContent, StringComparison.Ordinal);
        cut.Find("button[aria-label=Pause]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Path == "/api/playback" && r.Body!.Contains("pause", StringComparison.Ordinal));
            Assert.NotNull(cut.Find("button[aria-label=Play]"));
        });
        Assert.Contains("Nothing selected", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receivers_page_against_the_service_connects_a_fake_receiver_and_sets_its_volume()
    {
        await using var fake = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake Receiver 1" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(fake.Describe());
        _ctx.Services.AddSingleton(WebClients.Api(factory));
        var events = WebClients.Events(factory, _state);
        _ctx.Services.AddSingleton(events);
        _ctx.Services.AddSingleton<BridgeCommands>();
        events.Start();
        try
        {
            var cut = _ctx.Render<Receivers>();
            cut.WaitForAssertion(() => Assert.Equal("Fake Receiver 1", cut.Find("article.receiver h2").TextContent), RecordedFake.Patience);

            cut.Find("article.receiver button.primary").Click();
            await fake.WaitAsync<FakeReceiverEvent.StreamSetup>();
            cut.WaitForAssertion(() => Assert.Equal("Connected", cut.Find("article.receiver .badge").TextContent), RecordedFake.Patience);

            cut.Find("article.receiver input[type=range]").Input("20");
            await fake.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -24.0) < 0.001); // level 20 -> -30 + 0.3 * 20
            cut.WaitForAssertion(() => Assert.Equal("20", cut.Find("article.receiver .level output").TextContent), RecordedFake.Patience);

            cut.Find("article.receiver button").Click(); // Disconnect
            await fake.WaitAsync<FakeReceiverEvent.Teardown>();
            cut.WaitForAssertion(() => Assert.Equal("Available", cut.Find("article.receiver .badge").TextContent), RecordedFake.Patience);
        }
        finally
        {
            await _ctx.DisposeComponentsAsync();
            await events.DisposeAsync();
        }
    }

    [Fact]
    public void Notices_show_api_errors_and_can_be_dismissed()
    {
        UseApi((_, _) => StubHandler.Problem(HttpStatusCode.BadGateway, "Receiver error", "RTSP 470 Connection Authorization Required"));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A")));
        var notices = _ctx.Render<NoticeArea>();
        var page = _ctx.Render<Receivers>();

        page.Find("button.primary").Click();
        notices.WaitForAssertion(() => Assert.Contains("RTSP 470", notices.Find(".notice-error").TextContent, StringComparison.Ordinal));
        notices.Find(".notice .dismiss").Click();
        Assert.Empty(notices.FindAll(".notice"));
    }
}
