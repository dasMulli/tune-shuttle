using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// bUnit tests of the layout audit page <c>/_audit</c> (UI-SPEC 5.6), which tools/web-audit.ps1 reads: it renders the page
/// of <c>?route=</c> and calls app.js <c>audit()</c> only after the data arrived (State.Loaded + one more render + the
/// settle time), then writes the JSON into <c>&lt;pre id="audit-result"&gt;</c>.
/// </summary>
public sealed class AuditTests
{
    private const string Result = "{\"overflow\":0,\"small\":[],\"unnamed\":[]}";

    private static readonly BridgeSnapshot Snapshot = new(
        Dto.Status(),
        [Dto.Receiver("02:FA:CE:00:00:01", "Fake Receiver 1"), Dto.Receiver("02:FA:CE:00:00:02", "Fake Receiver 2")],
        [],
        Dto.NoOutput,
        Dto.Playback());

    private static readonly BridgeSnapshot SnapshotWithGroup = new(
        Dto.Status(),
        [Dto.Receiver("02:FA:CE:00:00:01", "Fake Receiver 1", groupId: "g-living"), Dto.Receiver("02:FA:CE:00:00:02", "Fake Receiver 2")],
        [Dto.Group("g-living", "Living room", 60, active: false, ("02:FA:CE:00:00:01", "Fake Receiver 1", 50))],
        Dto.NoOutput,
        Dto.Playback());

    [Fact]
    public async Task Audit_runs_only_after_the_routed_page_has_its_data()
    {
        var (ctx, state, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            module.Setup<string>("audit").SetResult(Result);
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/_audit?route=%2Freceivers");
            var cut = ctx.Render<Audit>(p => p.Add(a => a.Settle, TimeSpan.FromMilliseconds(100)));

            // The routed page renders inside the audit page; nothing is measured while it has no data.
            Assert.Equal("Receivers", cut.Find(".stack > h1").TextContent);
            Assert.Equal(string.Empty, cut.Find("pre#audit-result").TextContent);
            Assert.True(cut.Find("pre#audit-result").HasAttribute("hidden"));
            await Task.Delay(100);
            Assert.Empty(module.Invocations["audit"]);

            await cut.InvokeAsync(() => state.Load(Snapshot));

            cut.WaitForAssertion(() => Assert.Equal(Result, cut.Find("pre#audit-result").TextContent));
            Assert.Single(module.Invocations["audit"]);
            Assert.Equal(2, cut.FindAll("article.receiver").Count); // measured with the receivers on the page
        }
    }

    [Theory]
    [InlineData("/_audit", "section.hero")]
    [InlineData("/_audit?route=/settings/diagnostics", ".stack > div > a[href='settings']")]
    [InlineData("/_audit?route=%2Fsettings%2Fdiagnostics", ".stack > div > a[href='settings']")]
    public async Task Audit_defaults_to_the_player_and_resolves_nested_routes(string uri, string selector)
    {
        var (ctx, state, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            module.Setup<string>("audit").SetResult(Result);
            state.Load(Snapshot); // already loaded: an in-app navigation to the audit page
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
            var cut = ctx.Render<Audit>(p => p.Add(a => a.Settle, TimeSpan.FromMilliseconds(100)));

            Assert.NotNull(cut.Find(selector));
            cut.WaitForAssertion(() => Assert.Equal(Result, cut.Find("pre#audit-result").TextContent));
            Assert.Single(module.Invocations["audit"]);
        }
    }

    [Theory]
    [InlineData("/_audit?route=/groups/new", "Create a group")] // the literal template wins over /groups/{Id}
    [InlineData("/_audit?route=/groups/g-living", "Living room")] // {Id} binds the editor's [Parameter] Id
    [InlineData("/_audit?route=%2Fgroups%2Fg-living", "Living room")]
    public async Task Audit_binds_route_parameters_and_prefers_literal_segments(string uri, string heading)
    {
        var (ctx, state, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            module.Setup<string>("audit").SetResult(Result);
            state.Load(SnapshotWithGroup);
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
            var cut = ctx.Render<Audit>(p => p.Add(a => a.Settle, TimeSpan.FromMilliseconds(100)));

            Assert.Equal(heading, cut.Find(".stack > h1").TextContent);
            Assert.Equal(heading == "Create a group", cut.FindAll("input#new-group").Count == 1);
            cut.WaitForAssertion(() => Assert.Equal(Result, cut.Find("pre#audit-result").TextContent));
        }
    }

    [Fact]
    public async Task Audit_reports_a_failing_audit_call_as_an_error()
    {
        var (ctx, state, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            module.Setup<string>("audit").SetException(new JSException("boom"));
            state.Load(Snapshot);
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/_audit?route=/receivers");
            var cut = ctx.Render<Audit>(p => p.Add(a => a.Settle, TimeSpan.FromMilliseconds(100)));

            cut.WaitForAssertion(() => Assert.Contains("audit() failed", cut.Find("pre#audit-result").TextContent, StringComparison.Ordinal));
            Assert.Contains("boom", cut.Find("pre#audit-result").TextContent, StringComparison.Ordinal);
            Assert.Contains("\"error\"", cut.Find("pre#audit-result").TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Audit_reports_a_route_without_a_page_at_once_and_measures_nothing()
    {
        var (ctx, state, _, _, module) = UiTest.Create("en");
        await using (ctx)
        {
            state.Load(Snapshot);
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/_audit?route=/nowhere");
            var cut = ctx.Render<Audit>(p => p.Add(a => a.Settle, TimeSpan.FromMilliseconds(1)));

            Assert.Contains("\"error\"", cut.Find("pre#audit-result").TextContent, StringComparison.Ordinal);
            Assert.Contains("/nowhere", cut.Find("pre#audit-result").TextContent, StringComparison.Ordinal);
            await Task.Delay(100);
            Assert.Empty(module.Invocations["audit"]);
        }
    }

    [Fact]
    public async Task Audit_page_title_comes_from_the_catalog()
    {
        var (ctx, _, _, _, _) = UiTest.Create("de");
        await using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/_audit?route=/settings");
            var cut = ctx.Render<Audit>();
            Assert.Equal("Layout-Prüfung", cut.Find("h1.visually-hidden").TextContent);
        }
    }
}
