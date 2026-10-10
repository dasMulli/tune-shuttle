using System.Net;
using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>bUnit tests of the Settings page (<c>/settings</c>) and the Diagnostics page (<c>/settings/diagnostics</c>).</summary>
public sealed class SettingsDiagnosticsTests : ComponentTestBase
{
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
}
