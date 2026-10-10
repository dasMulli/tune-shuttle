using System.Net;
using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Components;
using Bridge.Web.Pages;
using Bunit;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>bUnit tests of the Groups pages and the PIN pairing dialog.</summary>
public sealed class GroupsPairingTests : ComponentTestBase
{
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
}
