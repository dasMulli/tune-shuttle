using System.Net;
using AngleSharp.Dom;
using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Pages;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// bUnit tests of the Settings page (<c>/settings</c>, UI-SPEC 4.6) and the Diagnostics page (<c>/settings/diagnostics</c>,
/// 4.7). In <see cref="ProcessCultureCollection"/>: the language tests switch the catalog through <c>SetLanguage</c>.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class SettingsDiagnosticsTests : ComponentTestBase
{
    private static readonly DiagnosticsDto Stats = new(
        ["10:00:00.000 info Old: line", "10:00:01.000 warn Old: careful", "10:00:02.000 fail Old: broken"],
        ["Ethernet: 192.0.2.10"],
        6002,
        6001,
        1234,
        5,
        2,
        ["P4: buttons"]);

    [Fact]
    public void Settings_page_validates_like_the_service_and_saves()
    {
        using var scope = new CultureScope("en");
        var api = UseApi((request, body) => (request.Method.Method, PathOf(request)) switch
        {
            ("GET", "/api/settings") => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")),
            ("PUT", "/api/settings") => StubHandler.Json(new SettingsDto("Küche", 88200, true, "Buffered")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value")));
        Assert.Equal("Settings", cut.Find("h1").TextContent);
        Assert.Contains("input", cut.Find("#device-name").ClassList);
        Assert.Contains("input", cut.Find("select#stream").ClassList);
        Assert.Contains("= 1,750 ms at 44.1 kHz (22,050–176,400).", cut.Find("#latency-hint").TextContent, StringComparison.Ordinal);

        cut.Find("#latency").Change("100");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal("The latency must be between 22050 and 176400 frames.", cut.Find(".validation-message").TextContent));
        Assert.Equal("true", cut.Find("#latency").GetAttribute("aria-invalid"));
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
        cut.WaitForAssertion(() => Assert.Equal("Save", cut.Find("button[type=submit]").TextContent));
        Assert.Empty(cut.FindAll(".validation-message"));
    }

    [Fact]
    public void Poll_row_is_a_wrapping_label_like_the_theme_radios()
    {
        using var scope = new CultureScope("en");
        UseApi((_, _) => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")));
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => cut.Find("#poll"));

        // The label wraps the checkbox, so on phones the whole 44px row is the target (UI-SPEC 5.4/5.6).
        var label = cut.Find("label[for=poll]");
        Assert.Contains("field--check", label.ClassList);
        Assert.Single(cut.FindAll("div.field > label.field--check[for=poll] > input#poll[type=checkbox]"));
        Assert.Equal("Poll receiver volume", label.TextContent.Trim());
        Assert.Equal("field", label.ParentElement!.ClassName);
        Assert.Equal("poll-hint", cut.Find("#poll").GetAttribute("aria-describedby"));
        Assert.Single(cut.FindAll("div.field > label[for=poll] + p.hint#poll-hint"));

        // The same structure as the Appearance rows (.field > label.field--check > input + span), so one rule styles both.
        var radioLabel = cut.Find("label[for=settings-theme-light]");
        Assert.Contains("field", radioLabel.ParentElement!.ClassList);
        Assert.Equal(["INPUT", "SPAN"], label.Children.Select(c => c.TagName));
        Assert.Equal(["INPUT", "SPAN"], radioLabel.Children.Select(c => c.TagName));
    }

    [Fact]
    public void Save_keeps_the_form_and_shows_saving_while_the_put_runs()
    {
        using var scope = new CultureScope("en");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        UseApi((request, _) => request.Method == HttpMethod.Put
            ? pending.Task
            : Task.FromResult(StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime"))));
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value")));
        var input = cut.FindComponent<InputText>().Instance;
        var editContext = cut.FindComponent<EditForm>().Instance.EditContext;

        cut.Find("#device-name").Change("  Küche ");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal("Saving…", cut.Find("button[type=submit]").TextContent));
        Assert.True(cut.Find("button[type=submit]").HasAttribute("disabled"));
        Assert.True(cut.Find("form .actions .btn--ghost").HasAttribute("disabled"));

        pending.SetResult(StubHandler.Json(new SettingsDto("Küche", 77175, false, "Realtime")));
        cut.WaitForAssertion(() => Assert.Equal("Save", cut.Find("button[type=submit]").TextContent));
        Assert.False(cut.Find("button[type=submit]").HasAttribute("disabled"));
        Assert.Equal("Küche", cut.Find("#device-name").GetAttribute("value"));

        // The same EditContext and inputs stay (a new context would re-create them and drop the keyboard focus).
        Assert.Same(input, cut.FindComponent<InputText>().Instance);
        Assert.Same(editContext, cut.FindComponent<EditForm>().Instance.EditContext);
        Assert.False(editContext!.IsModified());
    }

    [Fact]
    public void Reset_reloads_from_the_service_and_discards_edits()
    {
        using var scope = new CultureScope("en");
        var api = UseApi((_, _) => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")));
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value")));

        cut.Find("#device-name").Change("Edited");
        cut.Find("#poll").Change(true);
        Assert.Equal("Edited", cut.Find("#device-name").GetAttribute("value"));

        var reset = cut.Find("form .actions .btn--ghost");
        Assert.Equal("Reset", reset.TextContent);
        reset.Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, api.Requests.Count(r => r.Method == HttpMethod.Get));
            Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value"));
        });
        Assert.False(cut.Find("#poll").HasAttribute("checked"));
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public void Failed_reset_says_so_above_the_form_and_retries()
    {
        using var scope = new CultureScope("en");
        var calls = 0;
        UseApi((_, _) => ++calls == 2
            ? StubHandler.Problem(HttpStatusCode.InternalServerError, "Boom", "Disk full.")
            : StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")));
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value")));
        Assert.Empty(cut.FindAll("[role=alert]"));

        cut.Find("form .actions .btn--ghost").Click();
        cut.WaitForAssertion(() => Assert.StartsWith("The settings could not be loaded. Boom", cut.Find("form [role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Single(cut.FindAll("form")); // the values stay on screen
        Assert.Equal("Retry", cut.Find(".settings-retry").TextContent);

        cut.Find(".settings-retry").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=alert]")));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Settings_validation_messages_come_from_the_catalog_in_german()
    {
        using var scope = new CultureScope("de");
        var (ctx, _, api, _, _) = UiTest.Create("de", respond: (request, _) => Task.FromResult(
            request.Method == HttpMethod.Get
                ? StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime"))
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var context = ctx;
        var cut = ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value")));
        Assert.Equal("Spotify-Gerätename", cut.Find("label[for=device-name]").TextContent);
        Assert.Contains("= 1.750 ms bei 44,1 kHz (22.050–176.400).", cut.Find("#latency-hint").TextContent, StringComparison.Ordinal);

        cut.Find("#device-name").Change("   ");
        cut.Find("#latency").Change("100");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(
            ["Der Gerätename ist leer.", "Die Latenz muss zwischen 22050 und 176400 Frames liegen."],
            cut.FindAll(".validation-message").Select(m => m.TextContent)));
        Assert.All(cut.FindAll(".validation-message"), m => Assert.Equal("alert", m.GetAttribute("role")));
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Settings_validation_messages_follow_a_language_switch()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, _) = UiTest.Create("en", respond: (_, _) => Task.FromResult(
            StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime"))));
        await using var context = ctx;
        var cut = ctx.Render<Settings>();
        cut.WaitForAssertion(() => cut.Find("#latency"));
        cut.Find("#latency").Change("100");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal("The latency must be between 22050 and 176400 frames.", cut.Find(".validation-message").TextContent));

        await cut.InvokeAsync(() => l.SetLanguage("de"));
        cut.WaitForAssertion(() => Assert.Equal("Die Latenz muss zwischen 22050 und 176400 Frames liegen.", cut.Find(".validation-message").TextContent));
        Assert.Equal("Einstellungen", cut.Find("h1").TextContent);
    }

    [Fact]
    public void Settings_load_failure_offers_a_retry()
    {
        using var scope = new CultureScope("en");
        var calls = 0;
        UseApi((_, _) => ++calls == 1
            ? StubHandler.Problem(HttpStatusCode.InternalServerError, "Boom", "Disk full.")
            : StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")));
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.StartsWith("The settings could not be loaded. Boom", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("form"));

        cut.Find(".card .btn").Click();
        cut.WaitForAssertion(() => Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value")));
    }

    [Fact]
    public void Appearance_radios_show_the_stored_theme_and_call_setTheme()
    {
        using var scope = new CultureScope("en");
        UseApi((_, _) => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")));
        var cut = _ctx.Render<Settings>();

        var fieldset = cut.Find("fieldset");
        Assert.Equal("Theme", fieldset.QuerySelector("legend")!.TextContent);
        Assert.Null(fieldset.GetAttribute("role"));
        Assert.Null(fieldset.GetAttribute("aria-label"));
        var radios = fieldset.QuerySelectorAll("input[type=radio]");
        Assert.Equal(["light", "dark", "system"], radios.Select(r => r.GetAttribute("value")));
        Assert.All(radios, r => Assert.Equal("settings-theme", r.GetAttribute("name")));
        Assert.Equal(
            ["Light", "Dark", "System (follows the operating system)"],
            radios.Select(r => cut.Find($"label[for={r.Id}]").TextContent.Trim()));
        Assert.All(cut.FindAll("fieldset label"), label => Assert.Contains("field--check", label.ClassList));

        // getTheme answers "system" after the first render.
        cut.WaitForAssertion(() => Assert.True(cut.Find("#settings-theme-system").HasAttribute("checked")));
        Assert.False(cut.Find("#settings-theme-dark").HasAttribute("checked"));

        cut.Find("#settings-theme-dark").Change(true);
        Assert.Equal("dark", Assert.Single(_module.Invocations["setTheme"]).Arguments[0]);
        cut.WaitForAssertion(() => Assert.True(cut.Find("#settings-theme-dark").HasAttribute("checked")));
        Assert.False(cut.Find("#settings-theme-system").HasAttribute("checked"));

        cut.Find("#settings-theme-light").Change(true);
        Assert.Equal(["dark", "light"], _module.Invocations["setTheme"].Select(i => i.Arguments[0]));
    }

    [Fact]
    public async Task Appearance_radios_follow_a_choice_made_in_the_theme_picker()
    {
        using var scope = new CultureScope("en");
        UseApi((_, _) => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")));
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.True(cut.Find("#settings-theme-system").HasAttribute("checked")));

        // The top-bar picker chooses through the same service.
        var browser = _ctx.Services.GetRequiredService<BrowserBridge>();
        await browser.SetThemeAsync("dark");
        cut.WaitForAssertion(() => Assert.True(cut.Find("#settings-theme-dark").HasAttribute("checked")));
        Assert.False(cut.Find("#settings-theme-system").HasAttribute("checked"));
        Assert.Equal("dark", Assert.Single(_module.Invocations["setTheme"]).Arguments[0]);

        // Disposed: a later choice is ignored without an error.
        var renders = cut.RenderCount;
        await _ctx.DisposeComponentsAsync();
        await browser.SetThemeAsync("light");
        await Task.Delay(50);
        Assert.Equal(renders, cut.RenderCount);
        Assert.False(_ctx.Renderer.UnhandledException.IsCompleted, "the disposed page threw on a theme change");
    }

    [Fact]
    public void Appearance_radios_survive_a_failing_browser_module()
    {
        using var scope = new CultureScope("en");
        _module.Setup<string>("getTheme").SetException(new JSException("storage blocked"));
        _module.SetupVoid("setTheme", _ => true).SetException(new JSException("storage blocked"));
        UseApi((_, _) => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")));
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() => Assert.Equal("AirPlay Bridge", cut.Find("#device-name").GetAttribute("value")));

        // getTheme failed: the three radios render, none checked.
        var radios = cut.FindAll("fieldset input[type=radio]");
        Assert.Equal(3, radios.Count);
        Assert.All(radios, r => Assert.False(r.HasAttribute("checked")));

        // setTheme failed: the choice is still shown.
        cut.Find("#settings-theme-dark").Change(true);
        cut.WaitForAssertion(() => Assert.True(cut.Find("#settings-theme-dark").HasAttribute("checked")));
    }

    [Fact]
    public async Task Language_field_is_named_by_its_label_and_switches_the_app()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, module) = UiTest.Create("en", respond: (_, _) => Task.FromResult(
            StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime"))));
        await using var context = ctx;
        var cut = ctx.Render<Settings>();

        var label = cut.Find("label[for=settings-language]");
        Assert.Equal("Language", label.TextContent);
        var select = cut.Find("select#settings-language");
        Assert.Contains("input", select.ClassList);
        Assert.Null(select.GetAttribute("aria-label"));
        Assert.Equal("field", label.ParentElement!.ClassName);

        select.Change("de");
        Assert.Equal("de", l.Code);
        Assert.Equal("de", Assert.Single(module.Invocations["setLang"]).Arguments[0]);
        cut.WaitForAssertion(() => Assert.Equal("Sprache", cut.Find("label[for=settings-language]").TextContent));
        Assert.Equal("Darstellung", cut.Find("#settings-appearance").TextContent);
    }

    [Fact]
    public void Spotify_card_names_the_user_and_confirms_before_forgetting()
    {
        using var scope = new CultureScope("en");
        var api = UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("GET", "/api/settings") => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")),
            ("DELETE", _) or ("POST", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true)));
        var cut = _ctx.Render<Settings>();
        var card = SpotifyCard(cut);
        Assert.Contains("Connected as martin.", card.TextContent, StringComparison.Ordinal);

        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { SpotifyConnected = true }));
        cut.WaitForAssertion(() => Assert.Contains("Connected.", SpotifyCard(cut).TextContent, StringComparison.Ordinal));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        cut.WaitForAssertion(() => Assert.Contains("Not connected. Select the bridge in a Spotify app to log in.", SpotifyCard(cut).TextContent, StringComparison.Ordinal));

        Assert.Empty(SpotifyCard(cut).QuerySelectorAll(".btn--danger"));
        cut.Find("#settings-spotify").ParentElement!.ParentElement!.QuerySelector(".btn--ghost")!.Click();
        cut.WaitForAssertion(() => Assert.Contains("Forget the cached Spotify login?", SpotifyCard(cut).TextContent, StringComparison.Ordinal));
        Assert.Equal("Keep", SpotifyCard(cut).QuerySelector(".actions .btn:not(.btn--danger)")!.TextContent);
        Assert.DoesNotContain(api.Requests, r => r.Path.Contains("spotify", StringComparison.OrdinalIgnoreCase));

        SpotifyCard(cut).QuerySelector(".btn--danger")!.Click();
        cut.WaitForAssertion(() => Assert.Contains(api.Requests, r => r.Path.Contains("spotify", StringComparison.OrdinalIgnoreCase)));
        cut.WaitForAssertion(() => Assert.Empty(SpotifyCard(cut).QuerySelectorAll(".btn--danger")));
    }

    [Fact]
    public void Failed_forget_raises_a_notice_and_keeps_the_confirm_open()
    {
        using var scope = new CultureScope("en");
        var api = UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("GET", "/api/settings") => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")),
            ("DELETE", _) or ("POST", _) => StubHandler.Problem(HttpStatusCode.InternalServerError, "Boom", "Disk full."),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true)));
        var cut = _ctx.Render<Settings>();

        SpotifyCard(cut).QuerySelector(".btn--ghost")!.Click();
        cut.WaitForAssertion(() => Assert.NotNull(SpotifyCard(cut).QuerySelector(".btn--danger")));
        SpotifyCard(cut).QuerySelector(".btn--danger")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_state.Notices, n => n.Kind == NoticeKind.Error));
        Assert.StartsWith("Forgetting the Spotify login failed. Boom", Assert.Single(_state.Notices, n => n.Kind == NoticeKind.Error).Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_state.Notices, n => n.Kind == NoticeKind.Info);
        Assert.Contains(api.Requests, r => r.Path.Contains("spotify", StringComparison.OrdinalIgnoreCase));

        // The confirm stays, enabled again, so trying once more is one tap; Keep still closes it.
        cut.WaitForAssertion(() => Assert.False(SpotifyCard(cut).QuerySelector(".btn--danger")!.HasAttribute("disabled")));
        Assert.Contains("Forget the cached Spotify login?", SpotifyCard(cut).TextContent, StringComparison.Ordinal);
        SpotifyCard(cut).QuerySelector(".actions .btn:not(.btn--danger)")!.Click();
        cut.WaitForAssertion(() => Assert.Empty(SpotifyCard(cut).QuerySelectorAll(".btn--danger")));
    }

    [Fact]
    public void Settings_links_to_diagnostics_and_titles_the_page()
    {
        using var scope = new CultureScope("en");
        UseApi((_, _) => StubHandler.Json(new SettingsDto("AirPlay Bridge", 77175, false, "Realtime")));
        var cut = _ctx.Render<Settings>();
        var link = cut.Find("a.row[href='settings/diagnostics']");
        Assert.Equal("Diagnostics", link.QuerySelector("strong")!.TextContent);
        Assert.Equal("Status, network, statistics and the live log", link.QuerySelector(".meta")!.TextContent);
        Assert.Equal("true", link.QuerySelector(".row__chevron")!.GetAttribute("aria-hidden"));
        Assert.Equal(["Bridge", "Appearance", "Spotify"], cut.FindAll("h2").Select(h => h.TextContent));
    }

    [Fact]
    public async Task Diagnostics_page_shows_statistics_and_the_live_log()
    {
        using var scope = new CultureScope("en");
        UseApi((request, _) => PathOf(request) == "/api/diagnostics"
            ? StubHandler.Json(Stats with { RecentLog = ["10:00:00.000 info Old: line"] })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        var cut = _ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("6002", Fact(cut, "Timing port (UDP)"));
            Assert.Equal("Ethernet: 192.0.2.10", Fact(cut, "Local addresses"));
            Assert.Equal("P4: buttons", cut.Find(".probe").TextContent);
            Assert.Contains("Old: line", cut.Find(".log").TextContent, StringComparison.Ordinal); // REST history until live lines arrive
        });
        Assert.True(_ctx.Services.GetRequiredService<BridgeEventClient>().LogSubscribed);
        Assert.Equal("Diagnostics", cut.Find("h1").TextContent);
        Assert.Equal("settings", cut.Find("a.btn--ghost").GetAttribute("href"));
        Assert.Contains("Back to settings", cut.Find("a.btn--ghost").TextContent, StringComparison.Ordinal);

        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "info", "Bridge", "hello"));
        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch.AddSeconds(1), "warn", "Bridge", "careful"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".log__line").Count));
        Assert.Single(cut.FindAll(".log__line.log__line--warn"));
        Assert.DoesNotContain("Old: line", cut.Find(".log").TextContent, StringComparison.Ordinal);

        cut.Find("#log-level").Change("3");
        cut.WaitForAssertion(() => Assert.Contains("careful", Assert.Single(cut.FindAll(".log__line")).TextContent, StringComparison.Ordinal));

        await _ctx.DisposeComponentsAsync();
        await WebClients.WaitUntilAsync(() => !_ctx.Services.GetRequiredService<BridgeEventClient>().LogSubscribed, "the unsubscribe");
    }

    [Fact]
    public void Log_level_filter_pause_and_clear()
    {
        using var scope = new CultureScope("en");
        UseApi((request, _) => PathOf(request) == "/api/diagnostics"
            ? StubHandler.Json(Stats)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var cut = _ctx.Render<Diagnostics>();

        // The REST history carries its level: classes and the filter apply to it too.
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".log__line").Count));
        Assert.Equal(["log__line", "log__line log__line--warn", "log__line log__line--fail"], cut.FindAll(".log__line").Select(l => l.ClassName));
        Assert.Equal(["All", "Information", "Warning", "Error"], cut.FindAll("#log-level option").Select(o => o.TextContent));
        Assert.Equal("Level", cut.Find("label[for=log-level]").TextContent);
        Assert.Equal("log", cut.Find(".log").GetAttribute("role"));
        cut.Find("#log-level").Change("4");
        cut.WaitForAssertion(() => Assert.Contains("broken", Assert.Single(cut.FindAll(".log__line")).TextContent, StringComparison.Ordinal));
        cut.Find("#log-level").Change("0");

        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "trce", "Rtp", "queued"));
        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "dbug", "Rtp", "slept"));
        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "information", "Bridge", "connected"));
        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "fail", "Bridge", "failed"));
        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "crit", "Bridge", "lost"));
        cut.WaitForAssertion(() => Assert.Equal(
            ["log__line log__line--trce", "log__line log__line--dbug", "log__line", "log__line log__line--fail", "log__line log__line--crit"],
            cut.FindAll(".log__line").Select(l => l.ClassName)));

        cut.Find("#log-level").Change("2");
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".log__line").Count));
        cut.Find("#log-level").Change("4");
        cut.WaitForAssertion(() => Assert.Equal(["failed", "lost"], cut.FindAll(".log__line").Select(l => l.TextContent.Split(": ")[1])));
        cut.Find("#log-level").Change("0");

        // Pause freezes what is shown; resume catches up.
        Assert.Equal("Pause", cut.Find(".log-pause").TextContent.Trim());
        cut.Find(".log-pause").Click();
        Assert.Equal("Resume", cut.Find(".log-pause").TextContent.Trim());
        _state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "info", "Bridge", "later"));
        cut.Render();
        Assert.Equal(5, cut.FindAll(".log__line").Count);
        cut.Find(".log-pause").Click();
        cut.WaitForAssertion(() => Assert.Equal(6, cut.FindAll(".log__line").Count));

        // Clear empties the live lines and does not bring the REST history back.
        cut.Find(".log-clear").Click();
        cut.WaitForAssertion(() => Assert.Equal("No log lines yet.", cut.Find("#diagnostics-log").ParentElement!.ParentElement!.QuerySelector("p.meta")!.TextContent));
        Assert.Empty(cut.FindAll(".log"));
    }

    [Fact]
    public void Pause_freezes_the_history_against_the_periodic_refresh()
    {
        using var scope = new CultureScope("en");
        var calls = 0;
        UseApi((request, _) => PathOf(request) != "/api/diagnostics"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Interlocked.Increment(ref calls) == 1
                ? StubHandler.Json(Stats)
                : StubHandler.Json(Stats with { RecentLog = ["10:00:03.000 info New: replaced"] }));
        var cut = _ctx.Render<Diagnostics>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".log__line").Count));

        cut.Find(".log-pause").Click();
        // The next refresh (every 5 s) brings a different history; the paused view keeps the old one.
        cut.WaitForAssertion(() => Assert.True(Volatile.Read(ref calls) >= 2), Diagnostics.RefreshInterval * 3);
        cut.Render();
        Assert.Equal(3, cut.FindAll(".log__line").Count);
        Assert.DoesNotContain("New: replaced", cut.Find(".log").TextContent, StringComparison.Ordinal);

        // The level filter still applies to the snapshot.
        cut.Find("#log-level").Change("4");
        cut.WaitForAssertion(() => Assert.Contains("broken", Assert.Single(cut.FindAll(".log__line")).TextContent, StringComparison.Ordinal));
        cut.Find("#log-level").Change("0");

        cut.Find(".log-pause").Click();
        cut.WaitForAssertion(() => Assert.Contains("New: replaced", Assert.Single(cut.FindAll(".log__line")).TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostics_numbers_follow_the_culture()
    {
        using var scope = new CultureScope("de");
        var (ctx, state, _, _, _) = UiTest.Create("de", respond: (request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/api/diagnostics"
                ? StubHandler.Json(Stats with { ControlPort = 0, LocalAddresses = [.. Enumerable.Range(1, 7).Select(i => $"eth{i}: 192.0.2.{i}")] })
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var context = ctx;
        var started = new DateTimeOffset(2026, 10, 10, 14, 5, 9, TimeSpan.Zero);
        state.Apply(new BridgeEvent.StatusChanged(new StatusDto("0.1.0", true, "martin", "Wohnzimmer", false, 1234, started)));
        var cut = ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() => Assert.Equal("1.234", Fact(cut, "Gesendete Pakete")));
        Assert.Equal("Diagnose", cut.Find("h1").TextContent);
        Assert.Equal("5", Fact(cut, "Neusendungen"));
        Assert.Equal("2", Fact(cut, "Verspätete Pacer-Aufwachen"));
        Assert.Equal("0,4 % · 2 verspätet", Spaces(Fact(cut, "Neusendungsquote")));
        Assert.Equal("6002", Fact(cut, "Timing-Port (UDP)"));
        Assert.Equal("noch nicht gebunden", Fact(cut, "Control-Port (UDP)"));
        Assert.Equal("1.234", Fact(cut, "Empfänger im Netz"));
        Assert.Equal(Format.DateTime(started), Fact(cut, "Läuft seit"));
        Assert.Equal("verbunden als martin", Fact(cut, "Spotify"));
        Assert.Equal("Wohnzimmer", Fact(cut, "Gerätename"));
        Assert.Equal("Keine", Fact(cut, "Ausgabe"));

        // The first five addresses, the rest folded behind "2 weitere".
        var addresses = cut.Find("dd.addresses");
        Assert.Equal(5, addresses.Children.Count(c => c.ClassList.Contains("mono")));
        Assert.Equal("2 weitere", addresses.QuerySelector("details > summary")!.TextContent);
        Assert.Equal(2, addresses.QuerySelectorAll("details .mono").Length);
        Assert.StartsWith("Stand ", cut.FindAll("p.meta").Select(p => p.TextContent).Single(t => t.StartsWith("Stand", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retransmit_ratio_is_a_dash_without_packets_and_lists_report_none()
    {
        using var scope = new CultureScope("en");
        var (ctx, state, _, _, _) = UiTest.Create("en", respond: (request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/api/diagnostics"
                ? StubHandler.Json(new DiagnosticsDto([], [], 0, 0, 0, 0, 0, []))
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var context = ctx;
        state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        var cut = ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() => Assert.Equal("–", Fact(cut, "Retransmit ratio")));
        Assert.Equal("0", Fact(cut, "Packets sent"));
        Assert.Equal("not bound yet", Fact(cut, "Timing port (UDP)"));
        Assert.Equal("None reported.", Fact(cut, "Local addresses"));
        Assert.Equal("None reported.", cut.Find("#diagnostics-probes").ParentElement!.ParentElement!.QuerySelector("p.meta")!.TextContent);
        Assert.Equal("No log lines yet.", cut.Find("#diagnostics-log").ParentElement!.ParentElement!.QuerySelector(":scope > p.meta")!.TextContent);
        Assert.Equal("not connected", Fact(cut, "Spotify"));
    }

    [Fact]
    public async Task Live_updates_word_comes_from_the_status_keys()
    {
        using var scope = new CultureScope("de");
        var (ctx, state, _, _, _) = UiTest.Create("de");
        await using var context = ctx;
        state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        var cut = ctx.Render<Diagnostics>();

        Assert.Equal("offline (0 Verbindungen, 0 Ereignisse)", Fact(cut, "Live-Updates"));
        state.SetConnection(EventConnectionState.Connecting);
        cut.WaitForAssertion(() => Assert.Equal("verbinde… (0 Verbindungen, 0 Ereignisse)", Fact(cut, "Live-Updates")));
        state.SetConnection(EventConnectionState.Connected);
        cut.WaitForAssertion(() => Assert.Equal("live (0 Verbindungen, 0 Ereignisse)", Fact(cut, "Live-Updates")));
        state.SetConnection(EventConnectionState.Disconnected, "gone", DateTimeOffset.UnixEpoch);
        cut.WaitForAssertion(() => Assert.Equal("pausiert (0 Verbindungen, 0 Ereignisse)", Fact(cut, "Live-Updates")));

        // The output fact names the active group, else the output receiver.
        state.ApplyReceiver(Dto.Receiver("r1", "Küche"));
        state.ApplyOutput(new OutputDto(null, "r1", 40, []));
        cut.WaitForAssertion(() => Assert.Equal("Küche", Fact(cut, "Ausgabe")));
        state.ApplyGroup(Dto.Group("g1", "Unten", 50, true, ("r1", "Küche", 40)));
        state.ApplyOutput(new OutputDto("g1", null, 50, []));
        cut.WaitForAssertion(() => Assert.Equal("Unten", Fact(cut, "Ausgabe")));
    }

    [Fact]
    public void Diagnostics_failure_shows_the_catalog_text_and_retries()
    {
        using var scope = new CultureScope("en");
        var calls = 0;
        UseApi((request, _) => PathOf(request) != "/api/diagnostics"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : ++calls == 1 ? StubHandler.Problem(HttpStatusCode.InternalServerError, "Boom", "Disk full.") : StubHandler.Json(Stats));
        var cut = _ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() => Assert.StartsWith("Diagnostics unavailable. Boom", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        cut.Find("#diagnostics-network").ParentElement!.ParentElement!.QuerySelector(".btn")!.Click();
        cut.WaitForAssertion(() => Assert.Equal("1,234", Fact(cut, "Packets sent")));
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    private static IElement SpotifyCard(IRenderedComponent<Settings> cut) => cut.Find("#settings-spotify").ParentElement!.ParentElement!;

    /// <summary>The text of the <c>dd</c> that follows the <c>dt</c> reading <paramref name="label"/>.</summary>
    private static string Fact<T>(IRenderedComponent<T> cut, string label)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        var term = cut.FindAll("dt").SingleOrDefault(dt => dt.TextContent == label)
            ?? throw new Xunit.Sdk.XunitException($"No fact \"{label}\" in: {string.Join(", ", cut.FindAll("dt").Select(dt => dt.TextContent))}");
        return term.NextElementSibling!.TextContent.Trim();
    }

    private static string Spaces(string text) => text.Replace(' ', ' ').Replace(' ', ' ');
}
