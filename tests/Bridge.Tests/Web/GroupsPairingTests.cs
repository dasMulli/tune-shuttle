using System.Net;
using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Components;
using Bridge.Web.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>bUnit tests of the Groups pages (UI-SPEC 4.5) and the PIN pairing dialog (UI-SPEC 4.11).</summary>
public sealed class GroupsPairingTests : ComponentTestBase
{
    private const string Focus = "Blazor._internal.domWrapper.focus";

    // ---- pairing dialog ------------------------------------------------------------------------------------------

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

        // The service announces that a connect needs a PIN: the dialog says so in its own words (Service origin).
        _state.Apply(new BridgeEvent.PairingRequired("pin", "token-1"));
        cut.WaitForAssertion(() => Assert.Equal("Pair with PIN Speaker", cut.Find("[role=dialog] h2").TextContent));
        Assert.Equal("PIN Speaker needs a PIN pairing. Enter the PIN it shows now.", cut.Find("#pairing-prompt").TextContent);
        Assert.Equal("true", cut.Find(".scrim > .dialog").GetAttribute("aria-modal"));
        Assert.Equal("pairing-title", cut.Find(".dialog").GetAttribute("aria-labelledby"));
        // The dialog role sits on the wrapper div (ARIA in HTML allows no dialog role on <form>); the form is inside it.
        Assert.Equal("dialog", cut.Find(".scrim > div.dialog").GetAttribute("role"));
        Assert.False(cut.Find(".dialog > form.dialog__form").HasAttribute("role"));

        // The PIN field: a labelled numeric one-time-code input.
        var pin = cut.Find("input#pairing-pin.input.pin");
        Assert.Equal("PIN", cut.Find("label[for=pairing-pin]").TextContent);
        Assert.Equal("numeric", pin.GetAttribute("inputmode"));
        Assert.Equal("one-time-code", pin.GetAttribute("autocomplete"));
        Assert.Equal("8", pin.GetAttribute("maxlength"));
        Assert.True(cut.Find("button[data-action=pair]").HasAttribute("disabled")); // nothing typed yet

        cut.Find("input.pin").Input("0000");
        cut.Find("form.dialog__form").Submit();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("The receiver rejected the PIN. Start again; it then shows a new PIN.", cut.Find("[role=alert]").TextContent);
            Assert.Equal("Start again", cut.Find("button.btn--primary").TextContent);
            Assert.Equal("true", cut.Find("input.pin").GetAttribute("aria-invalid"));
        });
        Assert.Contains(api.Requests, r => r.Path == "/api/receivers/pin/pair/pin" && r.Body!.Contains("token-1", StringComparison.Ordinal));

        // Only "Start again" helps now: it takes the focus.
        cut.WaitForAssertion(() => Assert.Equal(ReferenceOf(cut, "button[data-action=restart]"), LastFocusedId()));

        // Start again: the receiver shows a new PIN; the field is empty and the error gone.
        cut.Find("button.btn--primary").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("token-2", _state.Pairing?.Token);
            Assert.Equal("Pair", cut.Find("button.btn--primary").TextContent);
            Assert.Empty(cut.FindAll("[role=alert]"));
            Assert.Equal(string.Empty, cut.Find("input.pin").GetAttribute("value") ?? string.Empty);
        });

        // The "Start again" button is gone: the focus is back in the (empty) field, the element focused on open (its
        // markup no longer carries the reference id once it is re-rendered in place).
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, _ctx.JSInterop.Invocations[Focus].Count); // open, Start again, the field
            Assert.Equal(FocusedId(0), LastFocusedId());
        });

        cut.Find("input.pin").Input("4711");
        cut.Find("form.dialog__form").Submit();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=dialog]")));
        Assert.Equal(PairingState.Paired, _state.Receiver("pin")!.Pairing);
        Assert.Contains(api.Requests, r => r.Path == "/api/receivers/pin/pair/pin" && r.Body!.Contains("token-2", StringComparison.Ordinal) && r.Body.Contains("4711", StringComparison.Ordinal));
    }

    [Fact]
    public void Pairing_dialog_shows_the_message_of_a_user_started_pairing_muted()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("pin", "PIN Speaker", pairing: PairingState.PinRequired)));
        _state.OpenPairing(new PairingPrompt("pin", "t", "Enter the PIN that PIN Speaker shows.", PairingPromptOrigin.User));
        var cut = _ctx.Render<PairingDialog>();

        var prompt = cut.Find("#pairing-prompt");
        Assert.Equal("Enter the PIN that PIN Speaker shows.", prompt.TextContent);
        Assert.Contains("meta", prompt.ClassList);
    }

    [Fact]
    public void Pairing_dialog_remembers_the_focus_and_focuses_the_pin_field_when_it_opens()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var remembered = RememberActiveHandle();
        var keys = PreventKeysHandle();
        var cut = _ctx.Render<PairingDialog>();
        Assert.Empty(_module.Invocations["rememberActive"]);

        _state.Apply(new BridgeEvent.PairingRequired("pin", "token-1"));

        cut.WaitForAssertion(() => Assert.Single(_ctx.JSInterop.Invocations[Focus]));
        Assert.Single(_module.Invocations["rememberActive"]);
        Assert.Equal(ReferenceOf(cut, "input#pairing-pin"), FocusedId(0));
        Assert.Empty(remembered.Invocations["restore"]);

        // The two ends of the trap keep "Tab" from its default (app.js preventKeys): the PIN field and the cancel button.
        var registrations = _module.Invocations["preventKeys"];
        Assert.Equal(2, registrations.Count);
        Assert.Equal(
            [ReferenceOf(cut, "input#pairing-pin"), ReferenceOf(cut, "button[data-action=cancel]")],
            registrations.Select(r => Assert.IsType<ElementReference>(r.Arguments[0]).Id));
        Assert.All(registrations, r => Assert.Equal(["Tab"], Assert.IsAssignableFrom<IEnumerable<string>>(r.Arguments[1])));
        Assert.Empty(keys.Invocations["dispose"]);
    }

    [Fact]
    public void Pairing_dialog_traps_tab_between_the_pin_field_and_the_cancel_button()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        RememberActiveHandle();
        PreventKeysHandle();
        _state.OpenPairing(new PairingPrompt("pin", "t", "Enter the PIN.", PairingPromptOrigin.User));
        var cut = _ctx.Render<PairingDialog>();
        cut.WaitForAssertion(() => Assert.Single(_ctx.JSInterop.Invocations[Focus])); // the PIN field on open
        var pin = ReferenceOf(cut, "input#pairing-pin");
        var cancel = ReferenceOf(cut, "button[data-action=cancel]");
        var pair = ReferenceOf(cut, "button[data-action=pair]");

        // Tab on the last element (cancel) wraps to the first (the PIN field).
        cut.Find("button[data-action=cancel]").KeyDown(new KeyboardEventArgs { Key = "Tab" });
        cut.WaitForAssertion(() => Assert.Equal(2, _ctx.JSInterop.Invocations[Focus].Count));
        Assert.Equal(pin, FocusedId(1));

        // Shift+Tab on the first element (the PIN field) wraps to the last (cancel).
        cut.Find("input#pairing-pin").KeyDown(new KeyboardEventArgs { Key = "Tab", ShiftKey = true });
        cut.WaitForAssertion(() => Assert.Equal(3, _ctx.JSInterop.Invocations[Focus].Count));
        Assert.Equal(cancel, FocusedId(2));

        // Inner directions: with an empty field the Pair button is disabled, so Tab on the field goes on to cancel;
        // once a PIN is typed it goes to Pair, and Shift+Tab on cancel goes back to Pair.
        cut.Find("input#pairing-pin").KeyDown(new KeyboardEventArgs { Key = "Tab" });
        cut.WaitForAssertion(() => Assert.Equal(4, _ctx.JSInterop.Invocations[Focus].Count));
        Assert.Equal(cancel, FocusedId(3));

        cut.Find("input#pairing-pin").Input("1234");
        cut.Find("input#pairing-pin").KeyDown(new KeyboardEventArgs { Key = "Tab" });
        cut.WaitForAssertion(() => Assert.Equal(5, _ctx.JSInterop.Invocations[Focus].Count));
        Assert.Equal(pair, FocusedId(4));
        cut.Find("button[data-action=cancel]").KeyDown(new KeyboardEventArgs { Key = "Tab", ShiftKey = true });
        cut.WaitForAssertion(() => Assert.Equal(6, _ctx.JSInterop.Invocations[Focus].Count));
        Assert.Equal(pair, FocusedId(5));

        // Other keys move nothing.
        cut.Find("input#pairing-pin").KeyDown(new KeyboardEventArgs { Key = "5" });
        cut.Find("button[data-action=cancel]").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(6, _ctx.JSInterop.Invocations[Focus].Count);
        Assert.NotNull(_state.Pairing);
    }

    [Fact]
    public void Pairing_dialog_escape_cancels_and_returns_the_focus()
    {
        var api = UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var remembered = RememberActiveHandle();
        var keys = PreventKeysHandle();
        _state.OpenPairing(new PairingPrompt("pin", "t", "Enter the PIN.", PairingPromptOrigin.User));
        var cut = _ctx.Render<PairingDialog>();
        cut.WaitForAssertion(() => Assert.Single(_ctx.JSInterop.Invocations[Focus]));

        cut.Find("input#pairing-pin").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForAssertion(() =>
        {
            Assert.Null(_state.Pairing);
            Assert.Empty(cut.FindAll("[role=dialog]"));
            Assert.Single(remembered.Invocations["restore"]);
            Assert.Equal(2, keys.Invocations["dispose"].Count);
        });
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void Pairing_dialog_cancel_closes_without_calling_the_service()
    {
        var api = UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var remembered = RememberActiveHandle();
        _state.OpenPairing(new PairingPrompt("pin", "t", "Enter the PIN.", PairingPromptOrigin.User));
        var cut = _ctx.Render<PairingDialog>();
        cut.WaitForAssertion(() => Assert.Single(_module.Invocations["rememberActive"]));

        cut.Find("button[data-action=cancel]").Click();

        Assert.Null(_state.Pairing);
        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Empty(api.Requests);
        cut.WaitForAssertion(() => Assert.Single(remembered.Invocations["restore"]));
    }

    [Fact]
    public void Pairing_dialog_shows_a_failure_with_the_service_detail()
    {
        UseApi((request, _) => PathOf(request) == "/api/receivers/pin/pair/pin"
            ? StubHandler.Problem(HttpStatusCode.BadGateway, "Receiver unreachable", "No route to host.")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.OpenPairing(new PairingPrompt("pin", "t", "Enter the PIN.", PairingPromptOrigin.User));
        var cut = _ctx.Render<PairingDialog>();

        cut.Find("input.pin").Input("1234");
        cut.Find("form.dialog__form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("The pairing failed: Receiver unreachable: No route to host.", cut.Find("[role=alert]").TextContent));
        Assert.Equal("Pair", cut.Find("button.btn--primary").TextContent); // a failure keeps the token: try again
        Assert.NotNull(_state.Pairing);
    }

    [Fact]
    public void Pairing_dialog_asks_for_the_pin_when_enter_submits_an_empty_field()
    {
        var api = UseApi((request, _) => PathOf(request) == "/api/receivers/pin/pair/pin"
            ? StubHandler.Json(Dto.Receiver("pin", "PIN Speaker", pairing: PairingState.Paired))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.OpenPairing(new PairingPrompt("pin", "t", "Enter the PIN.", PairingPromptOrigin.User));
        var cut = _ctx.Render<PairingDialog>();

        // Enter in the field submits the form even though the Pair button is disabled.
        cut.Find("form.dialog__form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("Enter the PIN the receiver shows.", cut.Find("[role=alert]").TextContent));
        Assert.Equal("Pair", cut.Find("button.btn--primary").TextContent); // nothing consumed: type and pair
        Assert.DoesNotContain(api.Requests, r => r.Path == "/api/receivers/pin/pair/pin");
        Assert.NotNull(_state.Pairing);
    }

    [Theory]
    [InlineData(PinOutcome.Failed, "No route to host.", "The pairing failed: No route to host.")]
    [InlineData(PinOutcome.Failed, null, "The pairing failed.")]
    [InlineData(PinOutcome.Failed, "", "The pairing failed.")]
    [InlineData(PinOutcome.Rejected, "Wrong PIN", "The receiver rejected the PIN. Start again; it then shows a new PIN.")]
    [InlineData(PinOutcome.Expired, "gone", "This pairing expired. Start again; the receiver then shows a new PIN.")]
    [InlineData(PinOutcome.Invalid, null, "Enter the PIN the receiver shows.")]
    [InlineData(PinOutcome.Paired, null, null)]
    public void Pairing_dialog_picks_the_error_text_by_outcome_and_detail(PinOutcome outcome, string? message, string? expected)
    {
        Assert.Equal(expected, PairingDialog.ErrorText(new PinResult(outcome, message), _l));
        Assert.Null(PairingDialog.ErrorText(null, _l));
    }

    [Fact]
    public async Task Pairing_dialog_speaks_german()
    {
        var (ctx, state, _, l, _) = UiTest.Create("de");
        await using (ctx)
        {
            state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("pin", "Küche", pairing: PairingState.PinRequired)));
            state.Apply(new BridgeEvent.PairingRequired("pin", "token-1"));
            var cut = ctx.Render<PairingDialog>();

            Assert.Equal("Mit Küche koppeln", cut.Find("[role=dialog] h2").TextContent);
            Assert.Equal("Küche braucht eine PIN-Kopplung. Gib die PIN ein, die der Empfänger jetzt anzeigt.", cut.Find("#pairing-prompt").TextContent);
            Assert.Equal("Koppeln", cut.Find("button[data-action=pair]").TextContent);
            Assert.Equal("Abbrechen", cut.Find("button[data-action=cancel]").TextContent);
            Assert.Equal("Die Kopplung ist fehlgeschlagen: kaputt", PairingDialog.ErrorText(new PinResult(PinOutcome.Failed, "kaputt"), l));
            Assert.Equal("Die Kopplung ist fehlgeschlagen.", PairingDialog.ErrorText(new PinResult(PinOutcome.Failed, null), l));
        }
    }

    // ---- group list ----------------------------------------------------------------------------------------------

    [Fact]
    public void Groups_list_shows_the_empty_state_and_links_to_a_new_group()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Load(Snapshot());
        var cut = _ctx.Render<Groups>();

        Assert.Equal("Groups", cut.Find("h1").TextContent);
        Assert.Equal("No groups yet. Create one, add receivers, then activate it.", cut.Find(".empty").TextContent.Trim());
        Assert.Equal("groups/new", cut.Find("a.btn.btn--primary").GetAttribute("href"));
        Assert.Equal("New group", cut.Find("a.btn.btn--primary").TextContent);
        var back = cut.Find("a.btn.btn--ghost");
        Assert.Equal("./", back.GetAttribute("href"));
        Assert.Contains("Back to the player", back.TextContent, StringComparison.Ordinal);
        Assert.Contains("btn--back", back.ClassList);
    }

    [Fact]
    public void Groups_list_shows_each_group_with_members_pill_and_edit_link()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Load(Snapshot(
            [Dto.Receiver("a", "Kitchen"), Dto.Receiver("b", "Living room")],
            [Dto.Group("g", "Downstairs", 50, true, ("a", "Kitchen", 30), ("b", "Living room", 80)), Dto.Group("e", "Empty", 50, false)]));
        var cut = _ctx.Render<Groups>();

        var row = cut.Find(".row[data-group-id=g]");
        Assert.Equal("Downstairs", row.QuerySelector("strong")!.TextContent);
        Assert.Equal("Kitchen, Living room", row.QuerySelector(".meta")!.TextContent);
        Assert.Equal("active", row.QuerySelector(".pill")!.TextContent);
        var edit = row.QuerySelector("a.btn.btn--sm")!;
        Assert.Equal("groups/g", edit.GetAttribute("href"));
        Assert.Equal("Edit", edit.TextContent);
        Assert.Equal("Edit group Downstairs", edit.GetAttribute("aria-label"));

        var empty = cut.Find(".row[data-group-id=e]");
        Assert.Equal("No members yet.", empty.QuerySelector(".meta")!.TextContent);
        Assert.Equal("empty", empty.QuerySelector(".pill")!.TextContent);
    }

    // ---- group editor --------------------------------------------------------------------------------------------

    [Fact]
    public void Creating_a_group_navigates_to_its_editor()
    {
        var api = UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("POST", "/api/groups") => StubHandler.Json(Dto.Group("g 1", "Upstairs", 50, false), HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Load(Snapshot());
        var cut = _ctx.Render<GroupEditor>();

        Assert.Equal("Create a group", cut.Find("h1").TextContent);
        Assert.Equal("Name", cut.Find("label[for=new-group]").TextContent);
        Assert.Equal("Living room + kitchen", cut.Find("#new-group").GetAttribute("placeholder"));
        Assert.Equal("64", cut.Find("#new-group").GetAttribute("maxlength"));
        Assert.True(cut.Find("button[type=submit]").HasAttribute("disabled"));
        Assert.Equal("groups", cut.Find("a.btn.btn--ghost").GetAttribute("href"));

        cut.Find("#new-group").Input("  Upstairs ");
        cut.Find("button[type=submit]").Click();

        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.Equal("http://localhost/groups/g%201", navigation.Uri));
        Assert.Contains(api.Requests, r => r.Path == "/api/groups" && r.Body!.Contains("\"name\":\"Upstairs\"", StringComparison.Ordinal));
        Assert.Equal("Upstairs", _state.Group("g 1")!.Name);
    }

    [Fact]
    public void Group_editor_renames_inline()
    {
        var api = UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("PUT", "/api/groups/g") => StubHandler.Json(Dto.Group("g", "Upstairs", 50, false)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Load(Snapshot([], [Dto.Group("g", "Downstairs", 50, false)]));
        var cut = RenderEditor("g");
        Assert.Equal("Downstairs", cut.Find("h1").TextContent);
        Assert.Empty(cut.FindAll("[data-card=name] input"));

        cut.Find("button[data-action=rename]").Click();
        var input = cut.Find("[data-card=name] input.input");
        Assert.Equal("Name", input.GetAttribute("aria-label"));
        Assert.Equal("Downstairs", input.GetAttribute("value"));
        Assert.Empty(cut.FindAll("button[data-action=rename]"));
        cut.WaitForAssertion(() => Assert.Equal(ReferenceOf(cut, "[data-card=name] input"), LastFocusedId()));

        // Cancel keeps the old name; Rename again, type, Save.
        cut.FindAll("[data-card=name] button").Single(b => b.TextContent == "Cancel").Click();
        Assert.Empty(cut.FindAll("[data-card=name] input"));
        cut.Find("button[data-action=rename]").Click();
        cut.Find("[data-card=name] input").Input("Upstairs");
        cut.Find("[data-card=name] form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Upstairs", cut.Find("h1").TextContent);
            Assert.Empty(cut.FindAll("[data-card=name] input"));
        });
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/groups/g" && r.Body!.Contains("Upstairs", StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Equal(ReferenceOf(cut, "button[data-action=rename]"), LastFocusedId()));
    }

    [Fact]
    public void Group_editor_escape_cancels_the_rename_and_returns_the_focus_to_rename()
    {
        var api = UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Load(Snapshot([], [Dto.Group("g", "Downstairs", 50, false)]));
        var cut = RenderEditor("g");

        cut.Find("button[data-action=rename]").Click();
        cut.Find("[data-card=name] input").Input("Up");
        cut.Find("[data-card=name] input").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll("[data-card=name] input"));
        Assert.Equal("Downstairs", cut.Find("[data-card=name] p").TextContent);
        cut.WaitForAssertion(() => Assert.Equal(ReferenceOf(cut, "button[data-action=rename]"), LastFocusedId()));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void Group_editor_drops_the_inline_state_when_it_shows_another_group()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Load(Snapshot([], [Dto.Group("g", "Downstairs", 50, false), Dto.Group("h", "Upstairs", 50, false)]));
        var cut = RenderEditor("g");
        cut.Find("button[data-action=rename]").Click();
        cut.Find("button[data-action=delete]").Click();
        Assert.Single(cut.FindAll("[data-card=name] input"));
        Assert.Single(cut.FindAll("button[data-action=delete-yes]"));

        // The same component instance, another id (a link from one editor to the next): no open rename or confirm.
        cut.Render(p => p.Add(x => x.Id, "h"));

        Assert.Equal("Upstairs", cut.Find("h1").TextContent);
        Assert.Empty(cut.FindAll("[data-card=name] input"));
        Assert.Single(cut.FindAll("button[data-action=rename]"));
        Assert.Empty(cut.FindAll("button[data-action=delete-yes]"));
        Assert.Single(cut.FindAll("button[data-action=delete]"));
    }

    [Fact]
    public void Group_editor_adds_and_removes_members()
    {
        var withMember = Dto.Group("g", "Upstairs", 50, false, ("a", "Bedroom", 50));
        var api = UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("POST", "/api/groups/g/members") => StubHandler.Json(withMember),
            ("DELETE", "/api/groups/g/members/a") => StubHandler.Json(Dto.Group("g", "Upstairs", 50, false)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Load(Snapshot([Dto.Receiver("a", "Bedroom"), Dto.Receiver("b", "Attic", online: false)], [Dto.Group("g", "Upstairs", 50, false)]));
        var cut = RenderEditor("g");
        Assert.Equal("No members yet.", cut.Find("[data-card=members] p.meta").TextContent);

        // The candidates: offline ones carry the suffix.
        Assert.Equal("Add receiver", cut.Find("label[for=add-member]").TextContent);
        Assert.Equal(["Choose…", "Attic (offline)", "Bedroom"], cut.FindAll("#add-member option").Select(o => o.TextContent));
        // Nothing chosen: aria-disabled, not disabled (a successful add empties the choice, and a disabled button would
        // drop the focus); a submit is ignored.
        Assert.Equal("true", cut.Find("[data-form=add-member] button").GetAttribute("aria-disabled"));
        Assert.False(cut.Find("[data-form=add-member] button").HasAttribute("disabled"));
        cut.Find("[data-form=add-member]").Submit();
        Assert.DoesNotContain(api.Requests, r => r.Path == "/api/groups/g/members");

        cut.Find("#add-member").Change("a");
        Assert.False(cut.Find("[data-form=add-member] button").HasAttribute("aria-disabled"));
        cut.Find("[data-form=add-member]").Submit();
        cut.WaitForAssertion(() => Assert.Equal("Bedroom", cut.Find(".row[data-receiver-id=a] strong").TextContent));
        Assert.Equal("true", cut.Find("[data-form=add-member] button").GetAttribute("aria-disabled"));
        Assert.Contains(api.Requests, r => r.Path == "/api/groups/g/members" && r.Body!.Contains("\"receiverId\":\"a\"", StringComparison.Ordinal));
        Assert.Equal(["Choose…", "Attic (offline)"], cut.FindAll("#add-member option").Select(o => o.TextContent));

        // The member row: pill, compact slider named after the member (never quiet), remove button.
        var row = cut.Find(".row[data-receiver-id=a]");
        Assert.DoesNotContain("is-quiet", row.ClassList);
        Assert.Equal("online", row.QuerySelector(".pill")!.TextContent);
        Assert.Equal("Level of Bedroom", row.QuerySelector(".level--compact input[type=range]")!.GetAttribute("aria-label"));
        Assert.Equal("50 of 100", row.QuerySelector(".level--compact input[type=range]")!.GetAttribute("aria-valuetext"));
        var remove = cut.Find(".row[data-receiver-id=a] button.btn--icon");
        Assert.Equal("Remove Bedroom", remove.GetAttribute("aria-label"));
        // The DOM order is the visual order at every width (name, slider, remove; the phone row wraps after the name
        // and puts no `order` on its children), so Tab and a screen reader follow what is seen (UI-SPEC 5.3).
        Assert.Equal(["row__main", "level", "btn"], row.Children.Select(c => c.ClassList.First()));

        remove.Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".row[data-receiver-id=a]")));
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Delete && r.Path == "/api/groups/g/members/a");
    }

    [Theory]
    [InlineData(ReceiverConnectionState.Available, "pill pill--ok", "online")]
    [InlineData(ReceiverConnectionState.Connecting, "pill pill--accent", "connecting…")]
    [InlineData(ReceiverConnectionState.Connected, "pill pill--ok", "connected")]
    [InlineData(ReceiverConnectionState.Streaming, "pill pill--ok", "playing")]
    [InlineData(ReceiverConnectionState.Error, "pill pill--err", "error")]
    [InlineData(ReceiverConnectionState.Offline, "pill", "offline")]
    public void Group_editor_shows_the_members_own_state_while_its_receiver_is_unknown(ReceiverConnectionState state, string css, string text)
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Load(Snapshot([], [new GroupDto("g", "Downstairs", 50, false, [new GroupMemberDto("x", "Gone speaker", 40, state)])]));
        var cut = RenderEditor("g");

        var pill = cut.Find(".row[data-receiver-id=x] .pill");
        Assert.Equal("Gone speaker", cut.Find(".row[data-receiver-id=x] strong").TextContent);
        Assert.Equal(css, pill.GetAttribute("class"));
        Assert.Equal(text, pill.TextContent);
        Assert.False(string.IsNullOrEmpty(pill.GetAttribute("title")));
    }

    [Fact]
    public void Group_master_slider_previews_the_scaled_member_levels_before_the_service_answers()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var volumeCalls = 0;
        var api = UseApi((request, _) => PathOf(request) != "/api/groups/g/volume"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            : Interlocked.Increment(ref volumeCalls) == 1
                ? answer.Task
                : Task.FromResult(StubHandler.Json(Dto.Group("g", "Downstairs", 70, false, ("a", "Kitchen", 42), ("b", "Living room", 100)))));
        _state.Load(Snapshot(
            [Dto.Receiver("a", "Kitchen"), Dto.Receiver("b", "Living room")],
            [Dto.Group("g", "Downstairs", 50, false, ("a", "Kitchen", 30), ("b", "Living room", 80))]));
        var cut = RenderEditor("g");

        string[] MemberLevels() => [.. cut.FindAll(".row[data-receiver-id] .level output").Select(o => o.TextContent)];
        bool MembersDisabled() => cut.FindAll(".row[data-receiver-id] input[type=range]").All(i => i.HasAttribute("disabled"));
        var master = cut.Find("[data-card=master] .level--hero input[type=range]");
        Assert.Equal("Master (G)", master.GetAttribute("aria-label"));
        Assert.Equal("50 of 100", master.GetAttribute("aria-valuetext"));
        Assert.Equal("Master (G)", cut.Find("[data-card=master] h2").TextContent);
        Assert.Equal(["30", "80"], MemberLevels());
        Assert.False(MembersDisabled());

        // Drag G from 50 to 60: r_i * 60/50 shows at once (36, 96); the request is still in flight.
        master.Input("60");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["36", "96"], MemberLevels());
            Assert.True(MembersDisabled());
            Assert.Empty(cut.FindAll("[data-hint=clamp]"));
        });
        cut.Find("[data-card=master] input[type=range]").Input("70");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["42", "100"], MemberLevels()); // 80 * 70/50 = 112 clamps at 100
            Assert.Equal("Some members reach 100 and clamp; the balance between them changes.", cut.Find("[data-hint=clamp]").TextContent);
        });

        // The service answers the first request; the newest value (70) follows; then the preview gives way to its data.
        answer.SetResult(StubHandler.Json(Dto.Group("g", "Downstairs", 60, false, ("a", "Kitchen", 36), ("b", "Living room", 96))));
        cut.WaitForAssertion(() => Assert.Equal(2, api.Requests.Count(r => r.Path == "/api/groups/g/volume")));
        var bodies = api.Requests.Where(r => r.Path == "/api/groups/g/volume").Select(r => r.Body).ToList();
        Assert.Contains("60", bodies[0], StringComparison.Ordinal);
        Assert.Contains("70", bodies[1], StringComparison.Ordinal);
        cut.WaitForAssertion(() =>
        {
            Assert.False(MembersDisabled());
            Assert.Empty(cut.FindAll("[data-hint=clamp]"));
        });
    }

    [Fact]
    public void Group_editor_activates_and_stops_the_output()
    {
        var group = Dto.Group("g", "Upstairs", 50, false, ("a", "Bedroom", 50));
        var activated = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, body) => (request.Method.Method, PathOf(request)) switch
        {
            ("POST", "/api/groups/g/activate") => activated.Task,
            ("PUT", "/api/output") => Task.FromResult(StubHandler.Json(Dto.NoOutput)),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
        });
        _state.Load(Snapshot([Dto.Receiver("a", "Bedroom")], [group]));
        var cut = RenderEditor("g");
        var element = cut.Instance.OutputButton.Id;

        var activate = cut.Find("button[data-action=activate]");
        Assert.Equal("Activate", activate.TextContent);
        Assert.Equal("Play to this group", activate.GetAttribute("title"));
        Assert.False(activate.HasAttribute("disabled"));
        Assert.Equal("ready", cut.Find("[data-card=output] .pill").TextContent);

        // While the request runs the pressed button keeps the focus: aria-disabled and aria-busy, never disabled.
        activate.Click();
        cut.WaitForAssertion(() =>
        {
            var busy = cut.Find("button[data-action=activate]");
            Assert.False(busy.HasAttribute("disabled"));
            Assert.Equal("true", busy.GetAttribute("aria-disabled"));
            Assert.Equal("true", busy.GetAttribute("aria-busy"));
            Assert.Equal("true", cut.Find("button[data-action=rename]").GetAttribute("aria-disabled"));
            Assert.False(cut.Find("button[data-action=rename]").HasAttribute("disabled"));
        });
        activated.SetResult(StubHandler.Json(new OutputDto("g", null, 50, group.Members)));
        cut.WaitForAssertion(() => Assert.Equal("Stop output", cut.Find("button[data-action=stop]").TextContent));
        // Activate and Stop are one element (no @if pair the diff would swap and so drop the focus).
        Assert.Equal(element, cut.Instance.OutputButton.Id);
        cut.WaitForAssertion(() => Assert.False(cut.Find("button[data-action=stop]").HasAttribute("aria-disabled")));
        Assert.Equal("g", _state.Output.GroupId);
        Assert.Equal("active", cut.Find("[data-card=output] .pill").TextContent);

        cut.Find("button[data-action=stop]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[data-action=activate]")));
        Assert.Equal(element, cut.Instance.OutputButton.Id);
        Assert.Null(_state.Output.GroupId);
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/output");
    }

    [Fact]
    public void Group_editor_cannot_activate_a_group_without_members()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Load(Snapshot([Dto.Receiver("a", "Bedroom")], [Dto.Group("g", "Upstairs", 50, false)]));
        var cut = RenderEditor("g");

        var activate = cut.Find("button[data-action=activate]");
        Assert.True(activate.HasAttribute("disabled"));
        Assert.Equal("Add receivers first", activate.GetAttribute("title"));
        Assert.Equal("Add receivers first", cut.Find("[data-card=output] .hint").TextContent); // visible, not only the title
        Assert.True(cut.Find("[data-card=master] input[type=range]").HasAttribute("disabled"));
    }

    [Fact]
    public void Group_editor_deletes_after_an_inline_confirm_and_returns_to_the_list()
    {
        var api = UseApi((request, _) => (request.Method.Method, PathOf(request)) switch
        {
            ("DELETE", "/api/groups/g") => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Load(Snapshot([], [Dto.Group("g", "Upstairs", 50, false)]));
        var cut = RenderEditor("g");
        Assert.Contains("card--danger", cut.Find("[data-card=delete]").ClassList);
        Assert.Equal("Deletes the group; the receivers stay.", cut.Find("[data-card=delete] .hint").TextContent);

        // One h2 per card (hidden here): it names the region, not the hint or the question.
        var title = cut.Find("[data-card=delete] h2.visually-hidden");
        Assert.Equal("Delete group", title.TextContent);
        Assert.Equal(title.Id, cut.Find("[data-card=delete]").GetAttribute("aria-labelledby"));

        // Keep backs out without a request and hands the focus back to Delete.
        cut.Find("button[data-action=delete]").Click();
        Assert.Equal("Delete Upstairs?", cut.Find("[data-card=delete] strong").TextContent);
        Assert.Equal("Delete group", cut.Find("#" + cut.Find("[data-card=delete]").GetAttribute("aria-labelledby")).TextContent);
        cut.WaitForAssertion(() => Assert.Equal(ReferenceOf(cut, "button[data-action=keep]"), FocusedId(0)));
        cut.Find("button[data-action=keep]").Click();
        cut.WaitForAssertion(() => Assert.Equal(ReferenceOf(cut, "button[data-action=delete]"), FocusedId(1)));
        Assert.Empty(api.Requests);

        cut.Find("button[data-action=delete]").Click();
        Assert.Equal("Delete", cut.Find("button[data-action=delete-yes]").TextContent);
        cut.Find("button[data-action=delete-yes]").Click();

        var navigation = _ctx.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.Equal("http://localhost/groups", navigation.Uri));
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Delete && r.Path == "/api/groups/g");
        Assert.Null(_state.Group("g"));
        Assert.Empty(cut.FindAll(".empty")); // no not-found flash before the list
    }

    [Fact]
    public void Group_editor_shows_not_found_for_an_unknown_id()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cut = RenderEditor("nope");
        Assert.Equal("Connecting to the bridge…", cut.Find("p.meta[role=status]").TextContent); // not loaded yet

        _state.Load(Snapshot());
        cut.WaitForAssertion(() => Assert.Equal("This group does not exist (any more).", cut.Find(".empty").TextContent.Trim()));
        Assert.Equal("Not found", cut.Find("h1").TextContent);
        Assert.Equal("groups", cut.Find("a.btn.btn--ghost").GetAttribute("href"));
        Assert.Contains("Back to the groups", cut.Find("a.btn.btn--ghost").TextContent, StringComparison.Ordinal);
        Assert.Contains("btn--back", cut.Find("a[href='groups']").ClassList);
    }

    [Fact]
    public async Task Group_pages_speak_german()
    {
        var (ctx, state, _, _, _) = UiTest.Create("de");
        await using (ctx)
        {
            state.Load(Snapshot(
                [Dto.Receiver("a", "Küche"), Dto.Receiver("b", "Bad", online: false)],
                [Dto.Group("g", "Unten", 50, false, ("a", "Küche", 40))]));

            var list = ctx.Render<Groups>();
            Assert.Equal("Gruppen", list.Find("h1").TextContent);
            Assert.Equal("Neue Gruppe", list.Find("a.btn.btn--primary").TextContent);
            Assert.Contains("Zurück zum Player", list.Find("a.btn.btn--ghost").TextContent, StringComparison.Ordinal);
            Assert.Equal("Bearbeiten", list.Find(".row a.btn--sm").TextContent);
            Assert.Equal("Gruppe Unten bearbeiten", list.Find(".row a.btn--sm").GetAttribute("aria-label"));

            var editor = ctx.Render<GroupEditor>(p => p.Add(x => x.Id, "g"));
            Assert.Equal("Aktivieren", editor.Find("button[data-action=activate]").TextContent);
            Assert.Equal("Zu dieser Gruppe spielen", editor.Find("button[data-action=activate]").GetAttribute("title"));
            Assert.Equal("Mitglieder", editor.Find("[data-card=members] h2").TextContent);
            Assert.Equal("Umbenennen", editor.Find("button[data-action=rename]").TextContent);
            Assert.Equal("Pegel von Küche", editor.Find(".row[data-receiver-id=a] input[type=range]").GetAttribute("aria-label"));
            Assert.Equal("40 von 100", editor.Find(".row[data-receiver-id=a] input[type=range]").GetAttribute("aria-valuetext"));
            Assert.Equal("Küche entfernen", editor.Find(".row[data-receiver-id=a] button.btn--icon").GetAttribute("aria-label"));
            Assert.Equal(["Auswählen…", "Bad (offline)"], editor.FindAll("#add-member option").Select(o => o.TextContent));
            Assert.Equal("Gruppe löschen", editor.Find("button[data-action=delete]").TextContent);
            Assert.Contains("Zurück zu den Gruppen", editor.Find("a.btn.btn--ghost").TextContent, StringComparison.Ordinal);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private IRenderedComponent<GroupEditor> RenderEditor(string id) => _ctx.Render<GroupEditor>(p => p.Add(x => x.Id, id));

    private static BridgeSnapshot Snapshot(ReceiverDto[]? receivers = null, GroupDto[]? groups = null) =>
        new(
            Dto.Status(),
            receivers ?? [],
            groups ?? [],
            groups?.FirstOrDefault(g => g.Active) is { } active ? new OutputDto(active.Id, null, active.MasterLevel, active.Members) : Dto.NoOutput,
            Dto.Playback());

    /// <summary>A fresh mock of the <c>rememberActive</c> handle whose <c>restore</c> calls the test reads.</summary>
    private BunitJSModuleInterop RememberActiveHandle()
    {
        var handle = _module.SetupModule(invocation => invocation.Identifier == "rememberActive");
        handle.SetupVoid("restore").SetVoidResult();
        return handle;
    }

    /// <summary>A mock of the <c>preventKeys</c> handle (not part of the shared module mock).</summary>
    private BunitJSModuleInterop PreventKeysHandle()
    {
        var handle = _module.SetupModule(invocation => invocation.Identifier == "preventKeys");
        handle.SetupVoid("dispose").SetVoidResult();
        return handle;
    }

    /// <summary>The element reference id bUnit rendered on the element matching <paramref name="selector"/>.</summary>
    private static string? ReferenceOf<T>(IRenderedComponent<T> cut, string selector)
        where T : IComponent => cut.Find(selector).GetAttribute("blazor:elementReference");

    /// <summary>The element reference id of the <paramref name="index"/>th <c>FocusAsync</c> call.</summary>
    private string FocusedId(int index) => Assert.IsType<ElementReference>(_ctx.JSInterop.Invocations[Focus][index].Arguments[0]).Id;

    /// <summary>The element reference id of the latest <c>FocusAsync</c> call.</summary>
    private string LastFocusedId() => FocusedId(_ctx.JSInterop.Invocations[Focus].Count - 1);
}
