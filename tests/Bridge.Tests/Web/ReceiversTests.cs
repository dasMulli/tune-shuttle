using System.Net;
using Airplay2.FakeReceiver;
using Bridge.Contracts;
using Bridge.Tests.Service;
using Bridge.Web;
using Bridge.Web.Localization;
using Bridge.Web.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>bUnit tests of the Receivers page (route <c>/receivers</c>, UI-SPEC 4.4), with stubbed API and against the service with a fake receiver.</summary>
public sealed class ReceiversTests : ComponentTestBase
{
    private const string One = "02:FA:CE:00:00:01";
    private const string Two = "02:FA:CE:00:00:02";

    [Fact]
    public void Receivers_page_lists_receivers_and_connect_applies_the_answer()
    {
        var api = UseApi((request, _) => PathOf(request) switch
        {
            "/api/receivers/02:FA:CE:00:00:01/connect" => StubHandler.Json(Dto.Receiver(One, "Fake Receiver 1", state: ReceiverConnectionState.Connected)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status("Wohnzimmer")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver(One, "Fake Receiver 1")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver(Two, "Fake Receiver 2", pairing: PairingState.PinRequired, online: false)));

        var cut = _ctx.Render<Receivers>();

        Assert.Contains("Wohnzimmer", cut.Find("p.meta").TextContent, StringComparison.Ordinal);
        var cards = cut.FindAll("article.card.receiver");
        Assert.Equal(2, cards.Count);
        Assert.Equal("Fake Receiver 1", cards[0].QuerySelector(".card__head h2")!.TextContent);
        Assert.Equal("online", cards[0].QuerySelector(".card__head .pill")!.TextContent);
        Assert.Equal("offline", cards[1].QuerySelector(".pill")!.TextContent);
        Assert.Contains("PIN needed", cards[1].QuerySelector(".tags")!.TextContent, StringComparison.Ordinal);

        // Offline: no connect, and the title says why; online: the title says what Connect does.
        var offlineConnect = cards[1].QuerySelector("button.btn--primary")!;
        Assert.True(offlineConnect.HasAttribute("disabled"));
        Assert.Equal("The receiver is offline", offlineConnect.GetAttribute("title"));
        var connect = cut.Find($"article[data-receiver-id='{One}'] button.btn--primary");
        Assert.False(connect.HasAttribute("disabled"));
        Assert.Equal("Play to this receiver alone", connect.GetAttribute("title"));
        Assert.Equal("Connect", connect.TextContent);

        connect.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/receivers/02:FA:CE:00:00:01/connect");
            Assert.Equal("Disconnect", cut.Find($"article[data-receiver-id='{One}'] .actions button.btn").TextContent);
            Assert.Equal("connected", cut.Find($"article[data-receiver-id='{One}'] .pill").TextContent);
        });
    }

    [Fact]
    public void Connect_shows_the_busy_label_while_the_request_runs()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, _) => PathOf(request) == "/api/receivers/a/connect" ? answer.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A")));
        var cut = _ctx.Render<Receivers>();

        cut.Find("button.btn--primary").Click();

        // The pressed button stays in the DOM and is not disabled while the request runs (Chromium would blur a focused
        // button that becomes disabled): aria-disabled and aria-busy instead, and a second press is ignored.
        cut.WaitForAssertion(() =>
        {
            var busy = cut.Find("button.btn--primary");
            Assert.Equal("Connecting…", busy.TextContent);
            Assert.False(busy.HasAttribute("disabled"));
            Assert.Equal("true", busy.GetAttribute("aria-disabled"));
            Assert.Equal("true", busy.GetAttribute("aria-busy"));
        });
        cut.Find("button.btn--primary").Click();
        Assert.Single(api.Requests);

        // Connect and Disconnect are one element whose role switches (no @if pair the diff would swap).
        answer.SetResult(StubHandler.Json(Dto.Receiver("a", "A", state: ReceiverConnectionState.Connected)));
        cut.WaitForAssertion(() => Assert.Equal("Disconnect", cut.Find("[data-action=disconnect]").TextContent));
        Assert.Single(cut.FindAll(".actions button[data-action=disconnect], .actions button[data-action=connect]"));
        Assert.False(cut.Find("[data-action=disconnect]").HasAttribute("aria-disabled"));
    }

    [Fact]
    public void Receiver_slider_and_offset_send_their_values()
    {
        using var culture = new CultureScope("en"); // the Format helpers read the current culture, like the app after SetLanguage
        var api = UseApi((request, body) => PathOf(request) switch
        {
            "/api/receivers/a/volume" => StubHandler.Json(Dto.Receiver("a", "A", level: 73)),
            "/api/receivers/a/offset" => StubHandler.Json(Dto.Receiver("a", "A", offset: -441)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A", level: 50)));
        var cut = _ctx.Render<Receivers>();

        // The hero slider: named "Volume of A", the value text from the catalog, the big number beside it.
        var slider = cut.Find("article.receiver .level.level--hero input[type=range]");
        Assert.Equal("Volume of A", slider.GetAttribute("aria-label"));
        Assert.Equal("50 of 100", slider.GetAttribute("aria-valuetext"));
        Assert.Equal("50", cut.Find(".level output.big").TextContent);

        slider.Input("73");
        cut.WaitForAssertion(() =>
        {
            var request = Assert.Single(api.Requests, r => r.Path == "/api/receivers/a/volume");
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Contains("\"level\":73", request.Body, StringComparison.Ordinal);
            Assert.Equal("73", cut.Find(".level output").TextContent);
        });

        // The offset form lives in the collapsed details; an offset beyond ±2 s is refused before it reaches the service.
        // The form is novalidate, so a browser submits it like this test does and the catalog text is what users see.
        Assert.True(cut.Find("article.receiver details.details form.offset").HasAttribute("novalidate"));
        cut.Find("form.offset input").Change("100000");
        cut.Find("form.offset").Submit();
        var error = cut.Find("article.receiver details [role=alert]");
        Assert.Equal("The offset must be within ±88,200 frames (2 s).", error.TextContent);
        Assert.Equal("true", cut.Find("form.offset input").GetAttribute("aria-invalid"));
        Assert.DoesNotContain(api.Requests, r => r.Path == "/api/receivers/a/offset");

        // Not a whole number: refused too.
        cut.Find("form.offset input").Change("1.5");
        Assert.Equal("Enter a whole number of frames.", cut.Find("article.receiver details [role=alert]").TextContent);
        cut.Find("form.offset").Submit();
        Assert.DoesNotContain(api.Requests, r => r.Path == "/api/receivers/a/offset");

        cut.Find("form.offset input").Change("-441");
        Assert.Empty(cut.FindAll("article.receiver details [role=alert]"));
        Assert.Null(cut.Find("form.offset input").GetAttribute("aria-invalid"));
        Assert.Equal("-441 frames = -10 ms (latency trim, ±2 s)", cut.Find("details .hint").TextContent);
        cut.Find("form.offset").Submit();
        cut.WaitForAssertion(() => Assert.Contains(api.Requests, r => r.Path == "/api/receivers/a/offset" && r.Body!.Contains("-441", StringComparison.Ordinal)));
        Assert.Equal(-441, _state.Receiver("a")!.OffsetFrames);
    }

    [Fact]
    public void Details_are_collapsed_and_list_the_receiver_facts()
    {
        using var culture = new CultureScope("en"); // the Format helpers read the current culture, like the app after SetLanguage
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver(One, "Fake Receiver 1", offset: 882)));
        var cut = _ctx.Render<Receivers>();

        var details = cut.Find("article.receiver details.details");
        Assert.False(details.HasAttribute("open"));
        Assert.Equal("Details", details.QuerySelector("summary")!.TextContent);

        var input = details.QuerySelector("form.offset input.input[type=number]")!;
        Assert.Equal("882", input.GetAttribute("value"));
        var label = details.QuerySelector($"form.offset label[for='{input.Id}']")!;
        Assert.Equal("Offset", label.TextContent);
        Assert.Contains(details.QuerySelector(".hint")!.Id!, input.GetAttribute("aria-describedby"), StringComparison.Ordinal);
        Assert.Equal("882 frames = 20 ms (latency trim, ±2 s)", details.QuerySelector(".hint")!.TextContent);
        var apply = details.QuerySelector("form.offset button.btn.btn--sm[type=submit]")!;
        Assert.Equal("Apply", apply.TextContent);
        // Nothing to apply until the draft differs: aria-disabled, not disabled (a successful apply makes the draft equal
        // again, and a disabled button would drop the focus).
        Assert.Equal("true", apply.GetAttribute("aria-disabled"));
        Assert.False(apply.HasAttribute("disabled"));

        var facts = details.QuerySelectorAll(".facts dt").Zip(details.QuerySelectorAll(".facts dd"), (dt, dd) => (dt.TextContent, dd.TextContent)).ToList();
        Assert.Equal(
            [("Id", One), ("Manufacturer", "Maker"), ("Port", "7000"), ("Features", "0x1C340405D4A00"), ("Flags", "0x4")],
            facts);

        Assert.Equal("Model · 192.0.2.1:7000", cut.Find("article.receiver .card__head + p.meta").TextContent);
    }

    [Fact]
    public void Tags_show_capabilities_pairing_group_and_output()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var caps = new ReceiverCapabilitiesDto(true, false, true, true, true, true, true, true, true, "0x1", "0x0");
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver(One, "Fake Receiver 1", pairing: PairingState.Paired, groupId: "g1", state: ReceiverConnectionState.Connected) with { Capabilities = caps }));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver(Two, "Fake Receiver 2", pairing: PairingState.PinRequired) with { Model = null, Capabilities = caps with { AirPlay2 = false, Ptp = false, Buffered = false } }));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("c", "HomeKit TV", pairing: PairingState.Disabled)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("d", "Plain", pairing: PairingState.Transient)));
        _state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g1", "Downstairs", 60, active: true, (One, "Fake Receiver 1", 80))));
        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto("g1", null, 60, [new GroupMemberDto(One, "Fake Receiver 1", 80, ReceiverConnectionState.Connected)])));
        var cut = _ctx.Render<Receivers>();

        string[] Chips(string id) => [.. cut.FindAll($"article[data-receiver-id='{id}'] .tags .chip").Select(c => c.TextContent)];

        Assert.Equal(["AirPlay 2", "PTP", "Buffered", "paired", "Group: Downstairs", "Output"], Chips(One));
        Assert.Equal(["PIN needed"], Chips(Two));
        Assert.Equal(["AirPlay 2", "HomeKit only"], Chips("c"));
        Assert.Equal(["AirPlay 2", "no PIN needed"], Chips("d"));

        Assert.Contains("chip--accent", cut.Find($"article[data-receiver-id='{One}'] [data-pairing=paired]").ClassList);
        Assert.Contains("chip--accent", cut.Find($"article[data-receiver-id='{One}'] [data-tag=output]").ClassList);
        Assert.Contains("chip--warn", cut.Find($"article[data-receiver-id='{Two}'] [data-pairing=pin]").ClassList);
        Assert.Equal("Unknown model · 192.0.2.1:7000", cut.Find($"article[data-receiver-id='{Two}'] .card__head + p.meta").TextContent);

        // Actions follow the pairing: forget when paired, pair with PIN when needed, HomeKit-only cannot connect.
        Assert.Equal(["Disconnect", "Forget pairing"], cut.FindAll($"article[data-receiver-id='{One}'] .actions button").Select(b => b.TextContent));
        Assert.Contains("btn--ghost", cut.Find($"article[data-receiver-id='{One}'] [data-action=forget]").ClassList);
        Assert.Equal(["Connect", "Pair with PIN"], cut.FindAll($"article[data-receiver-id='{Two}'] .actions button").Select(b => b.TextContent));
        var homekitConnect = cut.Find("article[data-receiver-id='c'] button.btn--primary");
        Assert.True(homekitConnect.HasAttribute("disabled"));
        Assert.Equal("Only members of the HomeKit home may connect.", homekitConnect.GetAttribute("title"));
        Assert.Equal(["Connect"], cut.FindAll("article[data-receiver-id='d'] .actions button").Select(b => b.TextContent));

        Assert.Contains("Output: group Downstairs.", cut.Find("p.meta").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Pair_and_forget_call_the_service_and_keep_the_card_busy_meanwhile()
    {
        var pairAnswer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forgetAnswer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("POST", "/api/receivers/p/pair") => pairAnswer.Task,
            ("DELETE", "/api/receivers/f/pairing") => forgetAnswer.Task,
            ("GET", "/api/receivers/f") => Task.FromResult(StubHandler.Json(Dto.Receiver("f", "F", pairing: PairingState.Transient))),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("p", "P", pairing: PairingState.PinRequired)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("f", "F", pairing: PairingState.Paired)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o", "O", pairing: PairingState.PinRequired, online: false)));
        var cut = _ctx.Render<Receivers>();

        // An offline receiver cannot be paired.
        Assert.True(cut.Find("article[data-receiver-id='o'] [data-action=pair]").HasAttribute("disabled"));

        var pair = cut.Find("article[data-receiver-id='p'] [data-action=pair]");
        Assert.False(pair.HasAttribute("disabled"));
        pair.Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/receivers/p/pair");
            Assert.Equal("true", cut.Find("article[data-receiver-id='p'] [data-action=pair]").GetAttribute("aria-disabled"));
            Assert.False(cut.Find("article[data-receiver-id='p'] [data-action=pair]").HasAttribute("disabled")); // keeps the focus
            Assert.Equal("true", cut.Find("article[data-receiver-id='p'] [data-action=connect]").GetAttribute("aria-disabled"));
        });
        pairAnswer.SetResult(StubHandler.Json(new PairingStartedDto("token-1", "Enter the PIN")));
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("article[data-receiver-id='p'] [data-action=pair]").HasAttribute("disabled"));
            Assert.False(cut.Find("article[data-receiver-id='p'] [data-action=pair]").HasAttribute("aria-disabled"));
            Assert.Equal("p", _state.Pairing?.ReceiverId);
        });

        // Paired in the dialog: the Pair button the dialog would give the focus back to is gone, so the row's Connect
        // button takes it once the dialog has closed (never while it is still open).
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("p", "P", pairing: PairingState.Paired)));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("article[data-receiver-id='p'] [data-action=forget]")));
        Assert.Empty(_ctx.JSInterop.Invocations[Focus]);
        _state.ClosePairing();
        cut.WaitForAssertion(() => Assert.Equal(cut.Instance.ConnectButtonOf("p").Id, LastFocusedId()));

        cut.Find("article[data-receiver-id='f'] [data-action=forget]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Delete && r.Path == "/api/receivers/f/pairing");
            Assert.Equal("true", cut.Find("article[data-receiver-id='f'] [data-action=forget]").GetAttribute("aria-disabled"));
            Assert.False(cut.Find("article[data-receiver-id='f'] [data-action=forget]").HasAttribute("disabled"));
        });
        forgetAnswer.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("article[data-receiver-id='f'] [data-action=forget]"));
            Assert.Equal("no PIN needed", cut.Find("article[data-receiver-id='f'] [data-pairing=transient]").TextContent);
        });
        // The pressed Forget button went away: the row's Connect button takes the focus.
        cut.WaitForAssertion(() => Assert.Equal(cut.Instance.ConnectButtonOf("f").Id, LastFocusedId()));
    }

    [Fact]
    public void Last_seen_phrases_age_with_the_clock_without_a_bridge_event()
    {
        var time = new SteppedTime(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var state = new BridgeState(time);
        _ctx.Services.AddSingleton(state); // the last registration wins over the fixture's state
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o", "O", online: false, state: ReceiverConnectionState.Offline, lastSeen: time.GetUtcNow() - TimeSpan.FromMinutes(5))));
        state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        var receivers = _ctx.Render<Receivers>();
        var player = _ctx.Render<Player>();
        Assert.Equal("Last seen 5 minutes ago", receivers.Find("article[data-receiver-id='o'] [data-last-seen]").TextContent);
        Assert.Equal("Last seen 5 minutes ago", player.Find("[data-receiver-id='o'] .row__main .meta.only-phone").TextContent);
        var version = state.Version;

        // Ten minutes later, with no event in between: the once-a-minute redraw (Format.LastSeenRefresh) catches up.
        time.Advance(TimeSpan.FromMinutes(10));
        receivers.WaitForAssertion(() => Assert.Equal("Last seen 15 minutes ago", receivers.Find("article[data-receiver-id='o'] [data-last-seen]").TextContent));
        player.WaitForAssertion(() => Assert.Equal("Last seen 15 minutes ago", player.Find("[data-receiver-id='o'] .row__main .meta.only-phone").TextContent));
        Assert.Equal(version, state.Version);
    }

    [Fact]
    public void Offline_cards_show_when_they_were_last_seen_and_forget_asks_first()
    {
        var forgetAnswer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("DELETE", "/api/receivers/o") => forgetAnswer.Task,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A", lastSeen: DateTimeOffset.UtcNow)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o", "O", online: false, state: ReceiverConnectionState.Offline, lastSeen: DateTimeOffset.UtcNow - TimeSpan.FromDays(3) - TimeSpan.FromHours(1))));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("c", "C", online: false, state: ReceiverConnectionState.Connecting, lastSeen: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5))));
        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        var cut = _ctx.Render<Receivers>();

        // Last seen on offline cards only; Forget only where the service would accept it (offline, no session).
        Assert.Equal("Last seen 3 days ago", cut.Find("article[data-receiver-id='o'] [data-last-seen]").TextContent);
        Assert.Equal("Last seen 5 minutes ago", cut.Find("article[data-receiver-id='c'] [data-last-seen]").TextContent);
        Assert.Empty(cut.FindAll("article[data-receiver-id='a'] [data-last-seen]"));
        Assert.Empty(cut.FindAll("article[data-receiver-id='a'] [data-action=forget-receiver]"));
        Assert.Empty(cut.FindAll("article[data-receiver-id='c'] [data-action=forget-receiver]"));
        var forget = cut.Find("article[data-receiver-id='o'] [data-action=forget-receiver]");
        Assert.Equal("Forget", forget.TextContent);
        Assert.Contains("btn--ghost", forget.ClassList);

        // The confirm replaces the actions row; Keep brings the button back with the focus.
        forget.Click();
        Assert.Equal("Forget O? Its pairing, group memberships and settings are deleted; if it shows up on the network again, it is added afresh.", cut.Find("article[data-receiver-id='o'] [data-confirm=forget-receiver]").TextContent);
        Assert.Empty(cut.FindAll("article[data-receiver-id='o'] [data-action=connect]"));
        Assert.Contains("btn--danger", cut.Find("article[data-receiver-id='o'] [data-action=forget-receiver-yes]").ClassList);
        cut.WaitForAssertion(() => Assert.Equal(ElementId(cut.Find("article[data-receiver-id='o'] [data-action=keep-receiver]")), LastFocusedId()));
        cut.Find("article[data-receiver-id='o'] [data-action=keep-receiver]").Click();
        Assert.Empty(cut.FindAll("[data-confirm=forget-receiver]"));
        cut.WaitForAssertion(() => Assert.Equal(ElementId(cut.Find("article[data-receiver-id='o'] [data-action=forget-receiver]")), LastFocusedId()));
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Delete);

        // Forget: busy while the request runs, then the card is gone and the heading has the focus.
        cut.Find("article[data-receiver-id='o'] [data-action=forget-receiver]").Click();
        cut.Find("article[data-receiver-id='o'] [data-action=forget-receiver-yes]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Delete && r.Path == "/api/receivers/o");
            Assert.Equal("true", cut.Find("article[data-receiver-id='o'] [data-action=forget-receiver-yes]").GetAttribute("aria-busy"));
        });
        forgetAnswer.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("article[data-receiver-id='o']")));
        Assert.Null(_state.Receiver("o"));
        cut.WaitForAssertion(() => Assert.Equal(cut.Instance.Title.Id, LastFocusedId()));
        Assert.Equal("-1", cut.Find("h1").GetAttribute("tabindex"));
    }

    [Fact]
    public void A_refused_forget_keeps_the_confirm_open_and_says_why()
    {
        UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("DELETE", "/api/receivers/o") => StubHandler.Problem(HttpStatusCode.Conflict, "Conflict", "O is online"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o", "O", online: false, state: ReceiverConnectionState.Offline)));
        var cut = _ctx.Render<Receivers>();

        // Without a last-seen time the card shows none.
        Assert.Empty(cut.FindAll("[data-last-seen]"));
        cut.Find("[data-action=forget-receiver]").Click();
        cut.Find("[data-action=forget-receiver-yes]").Click();
        cut.WaitForAssertion(() => Assert.Contains(_state.Notices, n => n.Kind == NoticeKind.Error && n.Text.Contains("O is online", StringComparison.Ordinal)));
        Assert.Single(cut.FindAll("[data-confirm=forget-receiver]"));
        Assert.NotNull(_state.Receiver("o"));
    }

    [Fact]
    public void Forget_all_offline_is_hidden_without_offline_receivers_and_forgets_them_after_a_confirm()
    {
        string? query = null;
        var api = UseApi((request, _) =>
        {
            if (request.Method == HttpMethod.Delete && PathOf(request) == "/api/receivers")
            {
                query = request.RequestUri!.Query;
                return StubHandler.Json(new ForgottenReceiversDto(["o1", "o2"]));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A")));
        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        var cut = _ctx.Render<Receivers>();
        Assert.Empty(cut.FindAll("[data-bulk]"));

        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o1", "O1", online: false, state: ReceiverConnectionState.Offline)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o2", "O2", online: false, state: ReceiverConnectionState.Error, error: "gone")));
        cut.WaitForAssertion(() => Assert.Equal("Forget all offline receivers (2)", cut.Find("[data-action=forget-offline]").TextContent));

        cut.Find("[data-action=forget-offline]").Click();
        Assert.Equal("Forget 2 offline receivers? Their pairings, group memberships and settings are deleted.", cut.Find("[data-bulk] p").TextContent);
        cut.WaitForAssertion(() => Assert.Equal(ElementId(cut.Find("[data-action=keep-offline]")), LastFocusedId()));
        cut.Find("[data-action=keep-offline]").Click();
        cut.WaitForAssertion(() => Assert.Equal(ElementId(cut.Find("[data-action=forget-offline]")), LastFocusedId()));
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Delete);

        cut.Find("[data-action=forget-offline]").Click();
        cut.Find("[data-action=forget-offline-yes]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("?offline=true", query);
            Assert.Equal(["a"], cut.FindAll("article.card.receiver").Select(c => c.GetAttribute("data-receiver-id")));
            Assert.Empty(cut.FindAll("[data-bulk]"));
        });
        Assert.Contains(_state.Notices, n => n.Kind == NoticeKind.Success && n.Text == "2 receivers forgotten.");
        cut.WaitForAssertion(() => Assert.Equal(cut.Instance.Title.Id, LastFocusedId()));
    }

    [Fact]
    public void Forget_texts_render_in_german_and_russian()
    {
        using var culture = new CultureScope("ru");
        _ctx.Services.AddSingleton(new I18n("ru"));
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o", "O", online: false, state: ReceiverConnectionState.Offline, lastSeen: DateTimeOffset.UtcNow - TimeSpan.FromDays(5))));
        var cut = _ctx.Render<Receivers>();

        Assert.Equal("Был в сети 5 дней назад", cut.Find("[data-last-seen]").TextContent);
        Assert.Equal("Забыть все приёмники не в сети (1)", cut.Find("[data-action=forget-offline]").TextContent);
        cut.Find("[data-action=forget-offline]").Click();
        Assert.Equal("Забыть 1 приёмник не в сети? Его сопряжение, группы и настройки будут удалены.", cut.Find("[data-bulk] p").TextContent);

        var l = new I18n("de");
        Assert.Equal("Alle Offline-Empfänger vergessen (3)", l.Format("receivers.forgetOffline", ("count", 3)));
        Assert.Equal("1 Offline-Empfänger vergessen? Seine Kopplung, Gruppenmitgliedschaften und Einstellungen werden gelöscht.", l.Plural("receivers.forgetOfflineConfirm", 1));
        Assert.Equal("Wohnzimmer vergessen? Kopplung, Gruppenmitgliedschaften und Einstellungen des Empfängers werden gelöscht; taucht er wieder im Netz auf, wird er neu aufgenommen.", l.Format("receivers.forgetConfirm", ("name", "Wohnzimmer")));
    }

    [Fact]
    public void A_receiver_that_comes_back_online_closes_its_forget_confirm()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o", "O", online: false, state: ReceiverConnectionState.Offline)));
        var cut = _ctx.Render<Receivers>();
        cut.Find("[data-action=forget-receiver]").Click();
        cut.Find("[data-action=forget-offline]").Click();
        Assert.Single(cut.FindAll("[data-confirm=forget-receiver]"));

        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o", "O")));
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-confirm=forget-receiver]"));
            Assert.Empty(cut.FindAll("[data-bulk]"));
            Assert.Single(cut.FindAll("[data-action=connect]"));
        });

        // Offline again: the buttons, not the old confirms.
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("o", "O", online: false, state: ReceiverConnectionState.Offline)));
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("[data-action=forget-receiver]"));
            Assert.Single(cut.FindAll("[data-action=forget-offline]"));
        });
        Assert.Empty(cut.FindAll("[data-confirm=forget-receiver]"));
    }

    /// <summary>The element reference id bUnit renders for an element with <c>@ref</c>.</summary>
    private static string? ElementId(AngleSharp.Dom.IElement element) => element.GetAttribute("blazor:elementReference");

    private const string Focus = "Blazor._internal.domWrapper.focus";

    /// <summary>The element reference id of the latest <c>FocusAsync</c> call.</summary>
    private string LastFocusedId() =>
        Assert.IsType<Microsoft.AspNetCore.Components.ElementReference>(_ctx.JSInterop.Invocations[Focus].Last().Arguments[0]).Id;

    [Fact]
    public void Error_line_is_an_alert_while_the_receiver_is_in_error()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A", state: ReceiverConnectionState.Error, error: "connection refused")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "B", error: "stale")));
        var cut = _ctx.Render<Receivers>();

        var alert = cut.Find("article[data-receiver-id='a'] > p.meta.text-err[role=alert]");
        Assert.Equal("connection refused", alert.TextContent);
        Assert.Equal("error", cut.Find("article[data-receiver-id='a'] .pill").TextContent);
        Assert.Empty(cut.FindAll("article[data-receiver-id='b'] [role=alert]")); // an old error without the Error state is not shown
    }

    [Fact]
    public void Empty_states_and_the_page_title()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cut = _ctx.Render<Receivers>();

        Assert.Equal("Receivers", cut.Find("h1").TextContent);
        Assert.Equal("Connecting to the bridge…", cut.Find("p.meta[role=status]").TextContent);
        Assert.Empty(cut.FindAll(".empty"));

        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput)); // loaded, no receivers
        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find(".empty");
            Assert.Contains("No AirPlay receivers found yet.", empty.TextContent, StringComparison.Ordinal);
            Assert.Contains("(mDNS)", empty.TextContent, StringComparison.Ordinal);
        });
        Assert.Empty(cut.FindAll(".cards"));
    }

    [Fact]
    public void Page_renders_in_german()
    {
        using var culture = new CultureScope("de");
        _ctx.Services.AddSingleton(new I18n("de"));
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status("Wohnzimmer", spotify: true)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A", offset: 44100, level: 40)));
        var cut = _ctx.Render<Receivers>();

        Assert.Equal("Empfänger", cut.Find("h1").TextContent);
        Assert.Equal("Spotify-Gerät Wohnzimmer, verbunden als martin. Keine Ausgabe gewählt; Audio wird verworfen.", cut.Find("p.meta").TextContent);
        Assert.Equal("online", cut.Find(".pill").TextContent);
        Assert.Equal("Verbinden", cut.Find("button.btn--primary").TextContent);
        Assert.Equal("Nur zu diesem Empfänger spielen", cut.Find("button.btn--primary").GetAttribute("title"));
        Assert.Equal("Lautstärke von A", cut.Find("input[type=range]").GetAttribute("aria-label"));
        Assert.Equal("40 von 100", cut.Find("input[type=range]").GetAttribute("aria-valuetext"));
        Assert.Equal("44.100 Frames = 1.000 ms (Latenz-Trimmung, ±2 s)", cut.Find("details .hint").TextContent);
        Assert.Equal("keine PIN nötig", cut.Find("[data-pairing=transient]").TextContent);
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
            cut.WaitForAssertion(() => Assert.Equal("online", cut.Find("article.receiver .pill").TextContent), RecordedFake.Patience);

            await cut.InvokeAsync(() => cut.Find("article.receiver button.btn--primary").Click());
            await fake.WaitAsync<FakeReceiverEvent.StreamSetup>();
            cut.WaitForAssertion(() => Assert.Equal("connected", cut.Find("article.receiver .pill").TextContent), RecordedFake.Patience);

            cut.Find("article.receiver input[type=range]").Input("20");
            await fake.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -24.0) < 0.001); // level 20 -> -30 + 0.3 * 20
            cut.WaitForAssertion(() => Assert.Equal("20", cut.Find("article.receiver .level output").TextContent), RecordedFake.Patience);

            // The connect request may still be on its way (its events came first): the button is aria-disabled and a
            // press is ignored until it settles, as for a user.
            cut.WaitForAssertion(() => Assert.False(cut.Find("article.receiver [data-action=disconnect]").HasAttribute("aria-disabled")), RecordedFake.Patience);
            await cut.InvokeAsync(() => cut.Find("article.receiver [data-action=disconnect]").Click());
            await fake.WaitAsync<FakeReceiverEvent.Teardown>();
            cut.WaitForAssertion(() => Assert.Equal("online", cut.Find("article.receiver .pill").TextContent), RecordedFake.Patience);
        }
        finally
        {
            await _ctx.DisposeComponentsAsync();
            await events.DisposeAsync();
        }
    }
}
