using System.Net;
using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Components;
using Bridge.Web.Layout;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// bUnit tests of the shell (UI-SPEC 2.4, 4.2, 4.9, 4.10, 5.2, 7): the theme picker (keyboard and touch), the status pill,
/// the connection banner, the toasts, the tabs and tab bar, the layout's language switch and the not-found page. In
/// <see cref="ProcessCultureCollection"/>: the language-switch test changes the process-wide default-thread cultures.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class ShellTests : ComponentTestBase
{
    private static readonly MouseEventArgs Pointer = new() { Detail = 1 };
    private static readonly MouseEventArgs KeyboardActivation = new() { Detail = 0 };

    // ---- theme picker -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Theme_picker_is_a_radio_group_with_a_roving_tabindex()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            module.Setup<string>("getTheme").SetResult("dark");
            var cut = ctx.Render<ThemePicker>();

            var group = cut.Find(".theme-picker");
            Assert.Equal("radiogroup", group.GetAttribute("role"));
            Assert.Equal("Theme", group.GetAttribute("aria-label"));
            cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".theme-btn.is-active").GetAttribute("aria-checked")));

            var buttons = cut.FindAll("button.theme-btn");
            Assert.Equal(3, buttons.Count);
            Assert.All(buttons, b => Assert.Equal("radio", b.GetAttribute("role")));
            Assert.All(buttons, b => Assert.Equal("button", b.GetAttribute("type")));
            Assert.Equal(["Light", "Dark", "System (follows the operating system)"], buttons.Select(b => b.GetAttribute("aria-label")));
            Assert.Equal(["false", "true", "false"], buttons.Select(b => b.GetAttribute("aria-checked")));
            Assert.Equal(["-1", "0", "-1"], buttons.Select(b => b.GetAttribute("tabindex")));
            Assert.All(buttons, b => Assert.NotNull(b.QuerySelector("svg[aria-hidden=true]")));
            Assert.DoesNotContain("theme-picker--open", group.ClassList);
            // Nothing selected yet: reading the stored choice does not write it back.
            Assert.Empty(module.Invocations["setTheme"]);
        }
    }

    [Fact]
    public async Task Theme_picker_shows_system_until_the_stored_choice_is_read()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            var pending = module.Setup<string>("getTheme");
            var cut = ctx.Render<ThemePicker>();
            Assert.Equal("system", ThemeOf(cut));

            pending.SetResult("light");
            cut.WaitForAssertion(() => Assert.Equal("light", ThemeOf(cut)));
        }
    }

    [Fact]
    public async Task Theme_picker_arrow_keys_move_focus_and_selection_and_wrap()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Equal("system", ThemeOf(cut)));

            // From system (the last), ArrowRight wraps to light.
            cut.Find(".theme-btn.is-active").KeyDown(Key.Right);
            Assert.Equal("light", ThemeOf(cut));
            Assert.Equal("light", module.Invocations["setTheme"][^1].Arguments[0]);
            Assert.Equal(["0", "-1", "-1"], cut.FindAll("button.theme-btn").Select(b => b.GetAttribute("tabindex")));
            Assert.NotEmpty(ctx.JSInterop.Invocations["Blazor._internal.domWrapper.focus"]);

            cut.Find(".theme-btn.is-active").KeyDown(Key.Down);
            Assert.Equal("dark", ThemeOf(cut));
            cut.Find(".theme-btn.is-active").KeyDown(Key.Left);
            Assert.Equal("light", ThemeOf(cut));
            cut.Find(".theme-btn.is-active").KeyDown(Key.Up);
            Assert.Equal("system", ThemeOf(cut));
            cut.Find(".theme-btn.is-active").KeyDown(Key.Home);
            Assert.Equal("light", ThemeOf(cut));
            cut.Find(".theme-btn.is-active").KeyDown(Key.End);
            Assert.Equal("system", ThemeOf(cut));
            Assert.Equal(["light", "dark", "light", "system", "light", "system"], module.Invocations["setTheme"].Select(i => i.Arguments[0]));

            // Other keys (Tab, letters) neither select nor move.
            cut.Find(".theme-btn.is-active").KeyDown(Key.Tab);
            Assert.Equal(6, module.Invocations["setTheme"].Count);
        }
    }

    [Fact]
    public async Task Theme_picker_with_a_mouse_selects_on_the_first_click()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: true);
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["hoverCapable"]));

            cut.FindAll("button.theme-btn")[1].Click(Pointer);
            Assert.Equal("dark", ThemeOf(cut));
            Assert.Equal("dark", Assert.Single(module.Invocations["setTheme"]).Arguments[0]);
            Assert.DoesNotContain("theme-picker--open", cut.Find(".theme-picker").ClassList);
            // A pointer that can hover keeps the :hover unfold of the CSS: no touch class.
            Assert.DoesNotContain("theme-picker--touch", cut.Find(".theme-picker").ClassList);
            // A pointer that can hover needs no outside-click listener (the pill unfolds on hover).
            Assert.Empty(module.Invocations["onOutsideClick"]);
        }
    }

    [Fact]
    public async Task Theme_picker_on_touch_opens_on_the_first_tap_and_selects_on_the_second()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: false);
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["onOutsideClick"]));
            // A pointer that cannot hover: the CSS collapses the pill without :hover, which a tap leaves stuck on the
            // tapped button (REVIEW-2 #1).
            cut.WaitForAssertion(() => Assert.Contains("theme-picker--touch", cut.Find(".theme-picker").ClassList));

            // First tap on the collapsed pill (it shows only the active "system" button): opens, selects nothing.
            cut.Find(".theme-btn.is-active").Click(Pointer);
            Assert.Contains("theme-picker--open", cut.Find(".theme-picker").ClassList);
            Assert.Contains("theme-picker--touch", cut.Find(".theme-picker").ClassList);
            Assert.Equal("system", ThemeOf(cut));
            Assert.Empty(module.Invocations["setTheme"]);

            // Second tap selects and collapses.
            cut.FindAll("button.theme-btn")[1].Click(Pointer);
            Assert.Equal("dark", ThemeOf(cut));
            Assert.Equal("dark", Assert.Single(module.Invocations["setTheme"]).Arguments[0]);
            Assert.DoesNotContain("theme-picker--open", cut.Find(".theme-picker").ClassList);
            Assert.Contains("theme-picker--touch", cut.Find(".theme-picker").ClassList);
        }
    }

    [Fact]
    public async Task Theme_picker_on_touch_selects_at_once_on_keyboard_activation()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: false);
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["onOutsideClick"]));

            // Enter/Space on a button raise a click with detail 0: the keyboard can see every option, so it selects.
            cut.FindAll("button.theme-btn")[0].Click(KeyboardActivation);
            Assert.Equal("light", ThemeOf(cut));
            Assert.Equal("light", Assert.Single(module.Invocations["setTheme"]).Arguments[0]);
            Assert.DoesNotContain("theme-picker--open", cut.Find(".theme-picker").ClassList);
        }
    }

    [Fact]
    public async Task Theme_picker_escape_collapses_the_open_pill()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: false);
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["onOutsideClick"]));
            cut.Find(".theme-btn.is-active").Click(Pointer);
            Assert.Contains("theme-picker--open", cut.Find(".theme-picker").ClassList);
            // The opening tap selects nothing and leaves the focus where it is (the pill is meant to stay open).
            Assert.Empty(module.Invocations["blur"]);

            cut.Find(".theme-btn.is-active").KeyDown(Key.Escape);
            Assert.DoesNotContain("theme-picker--open", cut.Find(".theme-picker").ClassList);
            Assert.Equal("system", ThemeOf(cut));
            Assert.Empty(module.Invocations["setTheme"]);
            // The focus stays on the radio (REVIEW cycle 2); theme-picker--closed collapses the pill despite :focus-within.
            Assert.Contains("theme-picker--closed", cut.Find(".theme-picker").ClassList);
            Assert.Empty(module.Invocations["blur"]);
        }
    }

    [Fact]
    public async Task Theme_picker_escape_with_the_mouse_layout_collapses_and_keeps_the_focus()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: true);
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["hoverCapable"]));

            // A keyboard user tabbed in (focus-within unfolds the pill) and presses Escape: the closed class collapses
            // it and the focus stays on the checked radio (a blur would drop it to <body>, REVIEW cycle 2).
            cut.Find(".theme-btn.is-active").KeyDown(Key.Escape);
            Assert.Contains("theme-picker--closed", cut.Find(".theme-picker").ClassList);
            Assert.Empty(module.Invocations["blur"]);
            Assert.Empty(module.Invocations["setTheme"]);

            // Tab leaves it collapsed; the focus leaving the group or an arrow key clears the class again.
            cut.Find(".theme-btn.is-active").KeyDown(Key.Tab);
            Assert.Contains("theme-picker--closed", cut.Find(".theme-picker").ClassList);
            cut.Find(".theme-picker").FocusOut();
            Assert.DoesNotContain("theme-picker--closed", cut.Find(".theme-picker").ClassList);
            cut.Find(".theme-btn.is-active").KeyDown(Key.Escape);
            cut.Find(".theme-btn.is-active").KeyDown(Key.Right);
            Assert.DoesNotContain("theme-picker--closed", cut.Find(".theme-picker").ClassList);
            Assert.Equal("light", ThemeOf(cut)); // from "system", the last option, Right wraps to the first
        }
    }

    [Fact]
    public async Task Theme_picker_moves_the_focus_out_after_a_pointer_selection_but_not_after_a_keyboard_one()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: false);
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["onOutsideClick"]));

            // Keyboard activation (detail 0) and arrow keys select and keep the focus on the radio.
            cut.FindAll("button.theme-btn")[0].Click(KeyboardActivation);
            cut.Find(".theme-btn.is-active").KeyDown(Key.Right);
            Assert.Equal("dark", ThemeOf(cut));
            Assert.Empty(module.Invocations["blur"]);

            // Touch: open, then select with a tap; Chromium focused the tapped button, so the picker blurs it.
            cut.Find(".theme-btn.is-active").Click(Pointer);
            Assert.Empty(module.Invocations["blur"]);
            cut.FindAll("button.theme-btn")[2].Click(Pointer);
            Assert.Equal("system", ThemeOf(cut));
            Assert.DoesNotContain("theme-picker--open", cut.Find(".theme-picker").ClassList);
            Assert.Single(module.Invocations["blur"]);
        }
    }

    [Fact]
    public async Task Theme_picker_with_a_mouse_moves_the_focus_out_after_a_click()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: true);
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["hoverCapable"]));

            cut.FindAll("button.theme-btn")[1].Click(Pointer);
            Assert.Equal("dark", ThemeOf(cut));
            Assert.Single(module.Invocations["blur"]);
        }
    }

    [Fact]
    public async Task Theme_picker_collapses_on_a_click_outside_but_not_on_the_opening_tap()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: false);
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["onOutsideClick"]));
            var registration = module.Invocations["onOutsideClick"][0];
            Assert.IsType<ElementReference>(registration.Arguments[0]);
            var target = Assert.IsType<DotNetObjectReference<ThemePicker>>(registration.Arguments[1]);
            Assert.Same(cut.Instance, target.Value);
            Assert.Equal(nameof(ThemePicker.CollapseFromOutside), registration.Arguments[2]);

            // The opening tap is a click inside the picker: app.js never reports it, so the pill stays open.
            cut.Find(".theme-btn.is-active").Click(Pointer);
            Assert.True(cut.Instance.IsOpen);
            cut.Render();
            Assert.Contains("theme-picker--open", cut.Find(".theme-picker").ClassList);

            // A click elsewhere on the page: app.js calls the [JSInvokable] method.
            await target.Value.CollapseFromOutside();
            cut.WaitForAssertion(() => Assert.DoesNotContain("theme-picker--open", cut.Find(".theme-picker").ClassList));
            Assert.Equal("system", ThemeOf(cut));
            Assert.Empty(module.Invocations["setTheme"]);

            // Collapsed already: another outside click changes nothing.
            await target.Value.CollapseFromOutside();
            Assert.False(cut.Instance.IsOpen);
        }
    }

    [Fact]
    public async Task Theme_picker_releases_its_outside_click_listener_when_disposed()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: false);
        await using (ctx)
        {
            var handle = module.SetupModule(invocation => invocation.Identifier == "onOutsideClick");
            handle.SetupVoid("dispose").SetVoidResult();
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["onOutsideClick"]));
            var target = (DotNetObjectReference<ThemePicker>)module.Invocations["onOutsideClick"][0].Arguments[1]!;

            await ctx.DisposeComponentsAsync();

            Assert.Single(handle.Invocations["dispose"]);
            Assert.Throws<ObjectDisposedException>(() => target.Value);
        }
    }

    [Fact]
    public async Task Theme_picker_disposed_while_registering_the_outside_click_listener_releases_it()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en", hoverCapable: false);
        await using (ctx)
        {
            // The listener registration is still in flight when the picker goes away (the Router recreates the layout
            // when it switches between a found and an unknown address). bUnit answers module calls that return an
            // object at once, so the module is wrapped to hold the onOutsideClick answer back.
            var pending = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new GatedModuleRuntime(ctx.JSInterop.JSRuntime, "onOutsideClick", pending.Task);
            ctx.Services.AddSingleton(new BrowserBridge(gate));
            var cut = ctx.Render<ThemePicker>();
            var arguments = await gate.Requested.WaitAsync(TimeSpan.FromSeconds(10));
            var target = Assert.IsType<DotNetObjectReference<ThemePicker>>(arguments[1]);

            await ctx.DisposeComponentsAsync();
            var handle = new RecordingObjectReference();
            pending.SetResult(handle);

            await WebClients.WaitUntilAsync(() => handle.Disposed, "the late listener to be released");
            Assert.Equal(["dispose"], handle.Calls);
            Assert.Throws<ObjectDisposedException>(() => target.Value);
            Assert.False(ctx.Renderer.UnhandledException.IsCompleted, "the picker threw after being disposed");
        }
    }

    [Fact]
    public async Task Theme_picker_keeps_the_navigation_keys_from_scrolling_the_page()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            var handle = module.SetupModule(invocation => invocation.Identifier == "preventKeys");
            handle.SetupVoid("dispose").SetVoidResult();
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["preventKeys"]));

            var registration = module.Invocations["preventKeys"][0];
            Assert.IsType<ElementReference>(registration.Arguments[0]);
            Assert.Equal(
                ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End"],
                Assert.IsAssignableFrom<IEnumerable<string>>(registration.Arguments[1]));
            Assert.Empty(handle.Invocations["dispose"]);

            await ctx.DisposeComponentsAsync();
            Assert.Single(handle.Invocations["dispose"]);
        }
    }

    [Fact]
    public async Task Theme_picker_follows_a_choice_made_elsewhere()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            cut.WaitForAssertion(() => Assert.Single(module.Invocations["hoverCapable"]));
            Assert.Equal("system", ThemeOf(cut));

            // The Settings Appearance card chooses through the same service.
            var browser = ctx.Services.GetRequiredService<BrowserBridge>();
            await browser.SetThemeAsync("dark");

            cut.WaitForAssertion(() => Assert.Equal("dark", ThemeOf(cut)));
            Assert.Equal(["-1", "0", "-1"], cut.FindAll("button.theme-btn").Select(b => b.GetAttribute("tabindex")));
            // Only the outside call wrote the choice: the picker does not echo it back.
            Assert.Equal("dark", Assert.Single(module.Invocations["setTheme"]).Arguments[0]);

            // The picker's own choice comes back through the same event without a second write.
            cut.FindAll("button.theme-btn")[0].Click(Pointer);
            Assert.Equal("light", ThemeOf(cut));
            Assert.Equal(["dark", "light"], module.Invocations["setTheme"].Select(i => i.Arguments[0]));

            // Disposed: a later change elsewhere is ignored.
            var renders = cut.RenderCount;
            await ctx.DisposeComponentsAsync();
            await browser.SetThemeAsync("system");
            await Task.Delay(50);
            Assert.Equal(renders, cut.RenderCount);
            Assert.False(ctx.Renderer.UnhandledException.IsCompleted, "the disposed picker threw on a theme change");
        }
    }

    [Fact]
    public async Task Theme_picker_keeps_working_without_the_browser_module()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            module.Setup<string>("getTheme").SetException(new JSException("no module"));
            module.SetupVoid("setTheme", _ => true).SetException(new JSException("no module"));
            var cut = ctx.Render<ThemePicker>();
            cut.FindAll("button.theme-btn")[0].Click(Pointer);
            Assert.Equal("light", ThemeOf(cut));
        }
    }

    [Fact]
    public async Task Theme_picker_labels_follow_the_language()
    {
        var (ctx, _, _, _, _) = UiTest.Create("de");
        await using (ctx)
        {
            var cut = ctx.Render<ThemePicker>();
            Assert.Equal("Design", cut.Find(".theme-picker").GetAttribute("aria-label"));
            Assert.Equal(["Hell", "Dunkel", "System (folgt dem Betriebssystem)"], cut.FindAll("button.theme-btn").Select(b => b.GetAttribute("aria-label")));
            Assert.Equal(["Hell", "Dunkel", "System (folgt dem Betriebssystem)"], cut.FindAll("button.theme-btn").Select(b => b.GetAttribute("title")));
        }
    }

    // ---- status pill ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", EventConnectionState.Connecting, null, false, "status status--connecting", "connecting…", "Connecting to the bridge for live updates")]
    [InlineData("en", EventConnectionState.Connected, null, false, "status status--live", "live", "Live updates are on")]
    [InlineData("en", EventConnectionState.Disconnected, "The socket closed.", true, "status status--paused", "paused", "Live updates paused. Retrying shortly.")]
    [InlineData("en", EventConnectionState.Disconnected, "Stopped.", false, "status status--error", "offline", "No connection to the bridge. The pages show the last known state.")]
    [InlineData("en", EventConnectionState.Disconnected, "net_webstatus_ConnectFailure", true, "status status--paused", "paused", "Live updates paused. Retrying shortly.")]
    [InlineData("de", EventConnectionState.Connecting, null, false, "status status--connecting", "verbinde…", "Verbinde mit der Bridge für Live-Updates")]
    [InlineData("de", EventConnectionState.Connected, null, false, "status status--live", "live", "Live-Updates sind aktiv")]
    [InlineData("de", EventConnectionState.Disconnected, "The socket closed.", true, "status status--paused", "pausiert", "Live-Updates pausiert. Es wird gleich erneut versucht.")]
    [InlineData("de", EventConnectionState.Disconnected, "Stopped.", false, "status status--error", "offline", "Keine Verbindung zur Bridge. Die Seiten zeigen den letzten bekannten Stand.")]
    [InlineData("de", EventConnectionState.Disconnected, "The bridge closed the connection.", true, "status status--paused", "pausiert", "Live-Updates pausiert: Die Bridge hat die Verbindung geschlossen. Es wird gleich erneut versucht.")]
    [InlineData("en", EventConnectionState.Disconnected, "The bridge closed the connection.", true, "status status--paused", "paused", "Live updates paused: The bridge closed the connection. Retrying shortly.")]
    public async Task Status_pill_maps_the_connection_to_class_text_and_title(string lang, EventConnectionState connection, string? error, bool retry, string css, string text, string title)
    {
        var (ctx, state, _, _, _) = UiTest.Create(lang);
        await using (ctx)
        {
            state.SetConnection(connection, error, retry ? DateTimeOffset.UnixEpoch : null);
            var pill = ctx.Render<StatusPill>().Find(".status");
            Assert.Equal(css, pill.ClassName);
            Assert.Equal(text, pill.TextContent);
            Assert.Equal(title, pill.GetAttribute("title"));
            // Not a live region: the banner slot announces the transitions (one announcement each).
            Assert.Null(pill.GetAttribute("role"));
        }
    }

    [Fact]
    public async Task Status_pill_follows_transitions_and_has_no_dangling_colon_without_an_error()
    {
        var (ctx, state, _, _, _) = UiTest.Create("en");
        await using (ctx)
        {
            // BridgeEventClient.Start says Connecting before the first render (no offline flash at startup).
            state.SetConnection(EventConnectionState.Connecting);
            var cut = ctx.Render<StatusPill>();
            Assert.Equal("status status--connecting", cut.Find(".status").ClassName);

            // Disconnected without a retry time (the loop stopped): offline, no error, no dangling colon.
            state.SetConnection(EventConnectionState.Disconnected);
            cut.WaitForAssertion(() => Assert.Equal("status status--error", cut.Find(".status").ClassName));
            Assert.Equal("No connection to the bridge. The pages show the last known state.", cut.Find(".status").GetAttribute("title"));

            state.SetConnection(EventConnectionState.Disconnected, null, DateTimeOffset.UnixEpoch);
            cut.WaitForAssertion(() => Assert.Equal("status status--paused", cut.Find(".status").ClassName));
            Assert.Equal("Live updates paused. Retrying shortly.", cut.Find(".status").GetAttribute("title"));

            state.SetConnection(EventConnectionState.Connected);
            cut.WaitForAssertion(() => Assert.Equal("status status--live", cut.Find(".status").ClassName));
        }
    }

    // ---- connection banner ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Connection_banner_shows_connecting_paused_and_offline_and_hides_while_connected()
    {
        var (ctx, state, _, _, _) = UiTest.Create("en");
        await using (ctx)
        {
            state.SetConnection(EventConnectionState.Connecting);
            var cut = ctx.Render<ConnectionBanner>();
            // The status region is the always-present slot; the coloured strip lives inside it.
            Assert.Equal("status", cut.Find("div.banner-slot").GetAttribute("role"));
            var banner = cut.Find("div.banner-slot > .banner");
            Assert.Equal("banner banner--connecting", banner.ClassName);
            Assert.Null(banner.GetAttribute("role"));
            Assert.Equal("Connecting to the bridge for live updates…", banner.TextContent.Trim());
            Assert.Empty(cut.FindAll("button"));

            state.SetConnection(EventConnectionState.Disconnected, ConnectionError.ClosedByBridge, DateTimeOffset.UnixEpoch);
            cut.WaitForAssertion(() => Assert.Equal("banner banner--paused", cut.Find(".banner").ClassName));
            Assert.Equal("Live updates paused: The bridge closed the connection. Retrying shortly.", cut.Find(".banner__text").TextContent);
            Assert.Empty(cut.FindAll("button"));

            state.SetConnection(EventConnectionState.Disconnected, null, DateTimeOffset.UnixEpoch);
            cut.WaitForAssertion(() => Assert.Equal("Live updates paused. Retrying shortly.", cut.Find(".banner__text").TextContent));

            state.SetConnection(EventConnectionState.Disconnected, "Stopped.");
            cut.WaitForAssertion(() => Assert.Equal("banner banner--offline", cut.Find(".banner").ClassName));
            Assert.Equal("No connection to the bridge. The pages show the last known state.", cut.Find(".banner__text").TextContent);
            var reload = cut.Find("button.btn.btn--sm");
            Assert.Equal("Reload", reload.TextContent);
            Assert.Equal("button", reload.GetAttribute("type"));

            var navigation = ctx.Services.GetRequiredService<BunitNavigationManager>();
            reload.Click();
            var reloaded = navigation.History.First();
            Assert.True(reloaded.Options.ForceLoad);
            Assert.Equal(navigation.Uri, new Uri(new Uri(navigation.BaseUri), reloaded.Uri).ToString());

            state.SetConnection(EventConnectionState.Connected);
            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".banner")));
            // Connected: the region stays, empty, so the next state change is announced (and measures 0px).
            var slot = cut.Find("div.banner-slot");
            Assert.Equal("status", slot.GetAttribute("role"));
            Assert.Empty(slot.Children);
            Assert.Equal(string.Empty, slot.TextContent.Trim());
        }
    }

    [Fact]
    public async Task Connection_banner_speaks_german()
    {
        var (ctx, state, _, _, _) = UiTest.Create("de");
        await using (ctx)
        {
            state.SetConnection(EventConnectionState.Disconnected, "Stopped.");
            var cut = ctx.Render<ConnectionBanner>();
            Assert.Equal("Keine Verbindung zur Bridge. Die Seiten zeigen den letzten bekannten Stand.", cut.Find(".banner__text").TextContent);
            Assert.Equal("Neu laden", cut.Find("button").TextContent);
        }
    }

    [Theory]
    [InlineData("de", "Live-Updates pausiert: Die Bridge hat die Verbindung geschlossen. Es wird gleich erneut versucht.", "Live-Updates pausiert. Es wird gleich erneut versucht.")]
    [InlineData("en", "Live updates paused: The bridge closed the connection. Retrying shortly.", "Live updates paused. Retrying shortly.")]
    public async Task Connection_banner_never_shows_the_clients_runtime_text(string lang, string closed, string other)
    {
        var (ctx, state, _, _, _) = UiTest.Create(lang);
        await using (ctx)
        {
            // The event client's own sentence comes from the catalog (UI-SPEC 3.5: only service text stays as sent).
            state.SetConnection(EventConnectionState.Disconnected, ConnectionError.ClosedByBridge, DateTimeOffset.UnixEpoch);
            var cut = ctx.Render<ConnectionBanner>();
            Assert.Equal(closed, cut.Find(".banner__text").TextContent);

            // A runtime exception message is left out in every language, English included: the WASM runtime reports a
            // resource key instead of a sentence (REVIEW-2 #2). The error-free sentence stays.
            foreach (var runtime in new[] { "net_webstatus_ConnectFailure", "Unable to connect to the remote server." })
            {
                state.SetConnection(EventConnectionState.Disconnected, runtime, DateTimeOffset.UnixEpoch);
                cut.WaitForAssertion(() => Assert.Equal(other, cut.Find(".banner__text").TextContent));
                Assert.DoesNotContain("net_webstatus", cut.Markup, StringComparison.Ordinal);
                Assert.DoesNotContain("Unable", cut.Markup, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Closed_by_bridge_sentence_is_the_one_the_event_client_writes()
    {
        // BridgeEventClient stays unchanged (UI-SPEC 8), so ConnectionError matches its literal: keep the two in step.
        var source = File.ReadAllText(Path.Combine(CatalogUsageTests.WebRoot(), "BridgeEventClient.cs"));
        Assert.Contains("error = \"" + ConnectionError.ClosedByBridge + "\";", source, StringComparison.Ordinal);
    }

    // ---- toasts -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Notices_show_api_errors_and_can_be_dismissed()
    {
        UseApi((_, _) => StubHandler.Problem(HttpStatusCode.BadGateway, "Receiver error", "RTSP 470 Connection Authorization Required"));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A")));
        var notices = _ctx.Render<NoticeArea>();

        // The failing call goes through BridgeCommands, as every page's button does.
        await _ctx.Services.GetRequiredService<BridgeCommands>().ConnectAsync("a", CancellationToken.None);

        notices.WaitForAssertion(() => Assert.Contains("RTSP 470", notices.Find(".toast--err").TextContent, StringComparison.Ordinal));
        var toast = notices.Find(".toast--err");
        Assert.Equal("toast toast--err", toast.ClassName);
        Assert.Equal("alert", toast.GetAttribute("role"));
        Assert.Equal("Dismiss", toast.QuerySelector(".toast__close")!.GetAttribute("aria-label"));
        notices.Find(".toast__close").Click();
        Assert.Empty(notices.FindAll(".toast"));
        // The region stays (a live region must exist before its content arrives).
        Assert.Equal("polite", notices.Find("div.toasts").GetAttribute("aria-live"));
    }

    [Fact]
    public async Task Toasts_have_a_kind_class_and_role_and_errors_stay()
    {
        var (ctx, state, _, _, _) = UiTest.Create("en");
        await using (ctx)
        {
            var cut = ctx.Render<NoticeArea>(p => p.Add(x => x.DismissAfter, TimeSpan.FromHours(1)).Add(x => x.ErrorDismissAfter, TimeSpan.FromHours(1)));
            state.Notify(NoticeKind.Success, "Settings saved.");
            state.Notify(NoticeKind.Info, "The Spotify login is forgotten.");
            state.Notify(NoticeKind.Error, "Connecting A failed.");

            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".toast").Count));
            var toasts = cut.FindAll(".toast");
            // Oldest first in the DOM: newest at the bottom on desktop; the phone CSS reverses the column.
            Assert.Equal(["toast toast--ok", "toast toast--info", "toast toast--err"], toasts.Select(t => t.ClassName));
            // One live region per toast: the polite container announces success and info, an error is its own alert.
            Assert.Equal(new string?[] { null, null, "alert" }, toasts.Select(t => t.GetAttribute("role")));
            Assert.Equal("Settings saved.", toasts[0].QuerySelector("span")!.TextContent);
            Assert.All(toasts, t => Assert.Equal("button", t.QuerySelector("button.toast__close")!.GetAttribute("type")));
            Assert.All(toasts, t => Assert.NotNull(t.QuerySelector(".toast__close [aria-hidden=true]")));

            cut.FindAll(".toast__close")[1].Click();
            Assert.Equal(["toast toast--ok", "toast toast--err"], cut.FindAll(".toast").Select(t => t.ClassName));
        }
    }

    [Fact]
    public async Task Toasts_auto_dismiss_success_and_info_before_errors()
    {
        var (ctx, state, _, _, _) = UiTest.Create("de");
        await using (ctx)
        {
            var cut = ctx.Render<NoticeArea>(p => p.Add(x => x.DismissAfter, TimeSpan.FromMilliseconds(50)).Add(x => x.ErrorDismissAfter, TimeSpan.FromHours(1)));
            state.Notify(NoticeKind.Error, "Fehler.");
            state.Notify(NoticeKind.Success, "Einstellungen gespeichert.");
            state.Notify(NoticeKind.Info, "Info.");
            cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".toast").Count));
            Assert.Equal("Ausblenden", cut.Find(".toast__close").GetAttribute("aria-label"));

            cut.WaitForAssertion(() => Assert.Equal("toast toast--err", Assert.Single(cut.FindAll(".toast")).ClassName), TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            Assert.Single(state.Notices);
            Assert.Equal(TimeSpan.FromSeconds(6), NoticeArea.AutoDismiss);
        }
    }

    [Fact]
    public async Task Error_toasts_auto_dismiss_after_their_longer_delay()
    {
        // UI-SPEC 4.9: an error stays 15 s (role="alert" announces it at once), so a forgotten one does not cover the
        // Output rows above the phone tab bar for good; it can still be dismissed earlier.
        var (ctx, state, _, _, _) = UiTest.Create("en");
        await using (ctx)
        {
            var cut = ctx.Render<NoticeArea>(p => p.Add(x => x.DismissAfter, TimeSpan.FromHours(1)).Add(x => x.ErrorDismissAfter, TimeSpan.FromMilliseconds(50)));
            state.Notify(NoticeKind.Info, "Info.");
            state.Notify(NoticeKind.Error, "Connecting A failed.");
            cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".toast").Count));

            cut.WaitForAssertion(() => Assert.Equal("toast toast--info", Assert.Single(cut.FindAll(".toast")).ClassName), TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(15), NoticeArea.ErrorAutoDismiss);
            Assert.True(NoticeArea.ErrorAutoDismiss > NoticeArea.AutoDismiss);
        }
    }

    // ---- tabs, tab bar, layout --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", 0)]
    [InlineData("groups", 0)]
    [InlineData("groups/new", 0)]
    [InlineData("groups/abc?x=1", 0)]
    [InlineData("receivers", 1)]
    [InlineData("receivers#a", 1)]
    [InlineData("settings", 2)]
    [InlineData("settings/diagnostics", 2)]
    [InlineData("_styleguide", -1)]
    [InlineData("groupsx", -1)]
    public async Task Tab_bar_marks_the_section_of_the_current_address(string path, int active)
    {
        var (ctx, _, _, _, _) = UiTest.Create("en");
        await using (ctx)
        {
            var navigation = ctx.Services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo(path);
            var cut = ctx.Render<TabBar>();

            var nav = cut.Find("nav.tabbar");
            Assert.Equal("Sections", nav.GetAttribute("aria-label"));
            var links = cut.FindAll("nav.tabbar a");
            Assert.Equal(["./", "receivers", "settings"], links.Select(a => a.GetAttribute("href")));
            Assert.Equal(["Player", "Receivers", "Settings"], links.Select(a => a.QuerySelector("span")!.TextContent));
            Assert.All(links, a => Assert.NotNull(a.QuerySelector("svg.ico[aria-hidden=true]")));
            for (var i = 0; i < links.Count; i++)
            {
                Assert.Equal(i == active ? "page" : null, links[i].GetAttribute("aria-current"));
                Assert.Equal(i == active, links[i].ClassList.Contains("active"));
            }
        }
    }

    [Fact]
    public async Task Tab_bar_follows_navigation()
    {
        var (ctx, _, _, _, _) = UiTest.Create("en");
        await using (ctx)
        {
            var navigation = ctx.Services.GetRequiredService<NavigationManager>();
            var cut = ctx.Render<TabBar>();
            Assert.Equal("page", cut.FindAll("nav.tabbar a")[0].GetAttribute("aria-current"));

            navigation.NavigateTo("settings/diagnostics");
            cut.WaitForAssertion(() => Assert.Equal("page", cut.FindAll("nav.tabbar a")[2].GetAttribute("aria-current")));
            Assert.Null(cut.FindAll("nav.tabbar a")[0].GetAttribute("aria-current"));
        }
    }

    [Fact]
    public async Task Layout_has_brand_tabs_tools_and_regions_and_publishes_the_bar_heights()
    {
        var (ctx, _, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            var cut = ctx.Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<h1>Body</h1>"))));

            var brand = cut.Find("header.topbar > a.brand");
            Assert.Equal("AirPlay Bridge", brand.GetAttribute("aria-label"));
            Assert.Equal("./", brand.GetAttribute("href"));
            Assert.Equal("AirPlay Bridge", brand.QuerySelector(".brand__name")!.TextContent);
            Assert.Equal("true", brand.QuerySelector(".brand__tile")!.GetAttribute("aria-hidden"));

            var tabs = cut.Find("header.topbar > nav.tabs");
            Assert.Equal("Sections", tabs.GetAttribute("aria-label"));
            Assert.Equal(["Player", "Receivers", "Settings"], cut.FindAll("nav.tabs a").Select(a => a.TextContent));
            // Relative to <base href>, like the tab bar's: the app also works under a path prefix.
            Assert.Equal(["./", "receivers", "settings"], cut.FindAll("nav.tabs a").Select(a => a.GetAttribute("href")));
            Assert.Equal("page", cut.FindAll("nav.tabs a")[0].GetAttribute("aria-current"));

            Assert.NotNull(cut.Find(".tools > select.lang-select"));
            Assert.NotNull(cut.Find(".tools > .theme-picker"));
            Assert.NotNull(cut.Find(".tools > .status"));
            Assert.Equal("Body", cut.Find("main.main > h1").TextContent);
            Assert.NotNull(cut.Find("div.toasts"));
            Assert.NotNull(cut.Find("nav.tabbar"));
            Assert.Empty(cut.FindAll(".live-dot, .sidebar, .notice"));

            // The shell's elements are siblings (direct children of #app in the page), so the sticky bar and banner
            // stick for the whole page; the banner slot sits right after the bar and holds the banner while not connected.
            var slot = cut.Find("header.topbar").NextElementSibling!;
            Assert.Equal("banner-slot", slot.ClassName);
            Assert.NotNull(slot.QuerySelector(".banner"));

            cut.WaitForAssertion(() => Assert.Equal(3, module.Invocations["measure"].Count));
            Assert.Equal(
                new[] { ("header.topbar", "--topbar-h"), ("nav.tabbar", "--tabbar-h"), ("div.banner-slot", "--banner-h") },
                module.Invocations["measure"].Select(i => ((string)i.Arguments[0]!, (string)i.Arguments[1]!)));
        }
    }

    [Fact]
    public async Task Language_select_in_the_bar_switches_the_tab_labels_to_german()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, _) = UiTest.Create("en");
        await using (ctx)
        {
            var cut = ctx.Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<h1>Body</h1>"))));
            Assert.Equal(["Player", "Receivers", "Settings"], cut.FindAll("nav.tabs a").Select(a => a.TextContent));

            cut.Find("header select.lang-select").Change("de");

            Assert.Equal("de", l.Code);
            cut.WaitForAssertion(() => Assert.Equal(["Player", "Empfänger", "Einstellungen"], cut.FindAll("nav.tabs a").Select(a => a.TextContent)));
            Assert.Equal(["Player", "Empfänger", "Einstellungen"], cut.FindAll("nav.tabbar a span").Select(a => a.TextContent));
            Assert.Equal("Bereiche", cut.Find("nav.tabs").GetAttribute("aria-label"));
            Assert.Equal("Design", cut.Find(".theme-picker").GetAttribute("aria-label"));
            Assert.Equal("Sprache", cut.Find("select.lang-select").GetAttribute("aria-label"));
            Assert.Equal("offline", cut.Find(".status").TextContent);
            Assert.Equal("Neu laden", cut.Find(".banner button").TextContent);
        }
    }

    [Fact]
    public async Task Unknown_address_shows_the_not_found_text_in_the_shell()
    {
        var (ctx, _, _, _, _) = UiTest.Create("de");
        await using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("does/not/exist");
            var cut = ctx.Render<Bridge.Web.App>();

            cut.WaitForAssertion(() => Assert.Equal("Nicht gefunden", cut.Find("main.main h1").TextContent));
            Assert.Equal("Unter dieser Adresse gibt es nichts.", cut.Find("main.main h1 + p").TextContent);
            var back = cut.Find("main.main a[href='./']");
            // Rendered like every other back link (1.5 .btn--back): a hidden ← glyph, then nav.backToPlayer.
            Assert.Equal("← Zurück zum Player", back.TextContent);
            Assert.Equal("true", back.QuerySelector("span")!.GetAttribute("aria-hidden"));
            // The page's only action is a 44px button-styled link on phones (5.4); the audit cannot reach this page (5.6).
            Assert.Equal(["btn", "btn--ghost", "btn--back"], back.ClassList);
            Assert.NotNull(cut.Find("header.topbar"));
            // No tab is active on an unknown address.
            Assert.Empty(cut.FindAll("nav.tabs a[aria-current]"));
        }
    }

    [Fact]
    public async Task Layout_stops_following_the_language_once_disposed()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, module) = UiTest.Create("en");
        await using (ctx)
        {
            // The Router recreates the layout at every switch between a found and an unknown address: a handler left
            // behind would re-render a dead layout at each later language switch.
            var cut = ctx.Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<h1>Body</h1>"))));
            cut.WaitForAssertion(() => Assert.Equal(3, module.Invocations["measure"].Count));
            var renders = cut.RenderCount;

            await ctx.DisposeComponentsAsync();
            l.SetLanguage("de");
            await Task.Delay(50);

            Assert.Equal(renders, cut.RenderCount);
            Assert.False(ctx.Renderer.UnhandledException.IsCompleted, "the disposed layout threw on a language switch");
        }
    }

    [Fact]
    public async Task Unknown_address_writes_its_title_and_focuses_its_heading()
    {
        var (ctx, _, _, _, _) = UiTest.Create("de");
        await using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("does/not/exist");
            var head = ctx.Render<HeadOutlet>();
            var cut = ctx.Render<Bridge.Web.App>();

            cut.WaitForAssertion(() => Assert.Equal("Nicht gefunden", cut.Find("main.main h1").TextContent));
            head.WaitForAssertion(() => Assert.Equal("Nicht gefunden \u00b7 AirPlay Bridge", head.Find("title").TextContent));

            // FocusOnNavigate needs route data; the not-found page focuses its own <h1> (UI-SPEC 6.1).
            var heading = cut.Find("main.main h1");
            Assert.Equal("-1", heading.GetAttribute("tabindex"));
            cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations["Blazor._internal.domWrapper.focus"]));
            var focused = Assert.IsType<ElementReference>(ctx.JSInterop.Invocations["Blazor._internal.domWrapper.focus"][0].Arguments[0]);
            Assert.Equal(focused.Id, heading.GetAttribute("blazor:elementReference"));
        }
    }

    [Fact]
    public async Task Unknown_address_follows_the_language()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, _) = UiTest.Create("en");
        await using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("does/not/exist");
            var head = ctx.Render<HeadOutlet>();
            var cut = ctx.Render<Bridge.Web.App>();
            cut.WaitForAssertion(() => Assert.Equal("Not found", cut.Find("main.main h1").TextContent));
            head.WaitForAssertion(() => Assert.Equal("Not found \u00b7 AirPlay Bridge", head.Find("title").TextContent));

            l.SetLanguage("de");

            cut.WaitForAssertion(() => Assert.Equal("Nicht gefunden", cut.Find("main.main h1").TextContent));
            Assert.Equal("Unter dieser Adresse gibt es nichts.", cut.Find("main.main h1 + p").TextContent);
            Assert.Equal("Bereiche", cut.Find("nav.tabs").GetAttribute("aria-label"));
            head.WaitForAssertion(() => Assert.Equal("Nicht gefunden \u00b7 AirPlay Bridge", head.Find("title").TextContent));
        }
    }

    private static string ThemeOf(IRenderedComponent<ThemePicker> cut)
    {
        // The DOM decides: exactly one checked button, and the component agrees with it.
        var buttons = cut.FindAll("button.theme-btn");
        var checkedIndex = Assert.Single(Enumerable.Range(0, buttons.Count), i => buttons[i].GetAttribute("aria-checked") == "true");
        Assert.Contains("is-active", buttons[checkedIndex].ClassList);
        Assert.Equal(ThemePicker.Themes[checkedIndex], cut.Instance.Theme);
        return ThemePicker.Themes[checkedIndex];
    }

    /// <summary>A JS object handle (<c>{ dispose() }</c>) whose arrival the test controls; records the calls made on it.</summary>
    private sealed class RecordingObjectReference : IJSObjectReference
    {
        private readonly List<string> _calls = [];
        private volatile bool _disposed;

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public bool Disposed => _disposed;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            lock (_calls)
            {
                _calls.Add(identifier);
            }

            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// An <see cref="IJSRuntime"/> over bUnit's whose imported module holds back the answer of one call
    /// (<paramref name="identifier"/>) until <paramref name="answer"/> completes; every other call goes to bUnit's mock.
    /// </summary>
    private sealed class GatedModuleRuntime(IJSRuntime inner, string identifier, Task<IJSObjectReference> answer) : IJSRuntime
    {
        private readonly TaskCompletionSource<object?[]> _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _identifier = identifier;
        private readonly Task<IJSObjectReference> _answer = answer;

        /// <summary>Completes with the arguments of the held-back call once it is made.</summary>
        public Task<object?[]> Requested => _requested.Task;

        public ValueTask<TValue> InvokeAsync<TValue>(string id, object?[]? args) => InvokeAsync<TValue>(id, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string id, CancellationToken cancellationToken, object?[]? args)
        {
            if (id == "import")
            {
                var module = await inner.InvokeAsync<IJSObjectReference>(id, cancellationToken, args);
                return (TValue)(object)new GatedModule(module, this);
            }

            return await inner.InvokeAsync<TValue>(id, cancellationToken, args);
        }

        private sealed class GatedModule(IJSObjectReference module, GatedModuleRuntime owner) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string id, object?[]? args) => InvokeAsync<TValue>(id, CancellationToken.None, args);

            public async ValueTask<TValue> InvokeAsync<TValue>(string id, CancellationToken cancellationToken, object?[]? args)
            {
                if (id == owner._identifier)
                {
                    owner._requested.TrySetResult(args ?? []);
                    return (TValue)(object)await owner._answer;
                }

                return await module.InvokeAsync<TValue>(id, cancellationToken, args);
            }

            public ValueTask DisposeAsync() => module.DisposeAsync();
        }
    }
}
