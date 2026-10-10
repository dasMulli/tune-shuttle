using System.Net;
using Bridge.Contracts;
using Bridge.Web.Components;
using Bridge.Web.Pages;
using Bunit;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>bUnit tests of the shell components (notices, later the theme picker, status pill, tab bar and banner).</summary>
public sealed class ShellTests : ComponentTestBase
{
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
