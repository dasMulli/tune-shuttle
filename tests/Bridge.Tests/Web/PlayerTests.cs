using System.Net;
using Bridge.Contracts;
using Bridge.Web.Pages;
using Bunit;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>bUnit tests of the Player page (route <c>/</c>): track, progress and transport commands.</summary>
public sealed class PlayerTests : ComponentTestBase
{
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
}
