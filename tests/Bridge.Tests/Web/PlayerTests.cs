using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Components;
using Bridge.Web.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// bUnit tests of the Player page (route <c>/</c>, UI-SPEC 4.3): the now-playing hero, the master volume, the Output
/// list and the Status card.
/// </summary>
public sealed class PlayerTests : ComponentTestBase
{
    private const string Minus = "−";

    // ---- hero ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Now_playing_shows_the_track_and_sends_transport_commands()
    {
        var api = UseApi((request, _) => PathOf(request) == "/api/playback"
            ? StubHandler.Json(Dto.Playback(playing: false, position: 1000, duration: 200_000, title: "Song"))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true)));
        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: true, position: 61_000, duration: 200_000, title: "Song")));
        var cut = _ctx.Render<Player>();

        Assert.Equal("Song", cut.Find(".hero__title").TextContent);
        Assert.Equal("Artist", cut.Find(".hero__artist").TextContent);
        Assert.Equal("Album · 3:20", cut.Find(".hero__info .meta").TextContent);
        Assert.Equal("Spotify · martin", cut.Find(".hero .chip--accent").TextContent);
        var times = cut.FindAll(".hero__progress > .meta").Select(m => m.TextContent).ToArray();
        Assert.Equal(["1:01", Minus + "2:19"], times);
        var bar = cut.Find(".hero__progress [role=progressbar]");
        Assert.Equal("1:01 of 3:20", bar.GetAttribute("aria-valuetext"));
        Assert.Equal("200", bar.GetAttribute("aria-valuemax"));
        Assert.Equal("61", bar.GetAttribute("aria-valuenow"));
        Assert.Empty(cut.FindAll(".track__knob"));
        Assert.DoesNotContain("track__knob", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("meter"));

        cut.Find("button[aria-label=Pause]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Path == "/api/playback" && r.Body!.Contains("pause", StringComparison.Ordinal));
            Assert.NotNull(cut.Find("button[aria-label=Play]"));
        });
    }

    [Fact]
    public void Nothing_playing_names_the_bridge_while_the_status_is_unknown_and_disables_the_transport()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cut = _ctx.Render<Player>();

        Assert.Equal("Nothing playing", cut.Find(".hero__title").TextContent);
        Assert.Equal("Select \"the bridge\" as the device in a Spotify app.", cut.Find(".hero__info .meta").TextContent);
        Assert.Empty(cut.FindAll(".hero .chip"));
        Assert.Empty(cut.FindAll(".hero__progress"));
        Assert.All(cut.FindAll(".transport button"), b => Assert.True(b.HasAttribute("disabled")));

        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status("Living Room Bridge")));
        cut.WaitForAssertion(() => Assert.Equal("Select \"Living Room Bridge\" as the device in a Spotify app.", cut.Find(".hero__info .meta").TextContent));

        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true)));
        cut.WaitForAssertion(() => Assert.Equal("Start playback in a Spotify app.", cut.Find(".hero__info .meta").TextContent));
    }

    [Fact]
    public void Transport_sends_play_next_and_previous_and_is_disabled_without_spotify()
    {
        var api = UseApi((request, _) => PathOf(request) == "/api/playback"
            ? StubHandler.Json(Dto.Playback(playing: false, position: 0, duration: 100_000, title: "Song"))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: false)));
        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: false, position: 0, duration: 100_000, title: "Song")));
        var cut = _ctx.Render<NowPlayingHero>();

        string[] Labels() => [.. cut.FindAll(".transport button").Select(b => b.GetAttribute("aria-label")!)];
        Assert.Equal(["Previous track", "Play", "Next track"], Labels());
        Assert.All(cut.FindAll(".transport button"), b => Assert.True(b.HasAttribute("disabled")));

        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true)));
        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".transport button"), b => Assert.False(b.HasAttribute("disabled"))));

        cut.Find("button[aria-label=Play]").Click();
        cut.WaitForState(() => api.Requests.Count == 1);
        cut.Find("button[aria-label='Next track']").Click();
        cut.WaitForState(() => api.Requests.Count == 2);
        cut.Find("button[aria-label='Previous track']").Click();
        cut.WaitForState(() => api.Requests.Count == 3);

        string[] commands = [.. api.Requests.Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("command").GetString()!)];
        Assert.Equal(["play", "next", "prev"], commands);
        Assert.All(api.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
    }

    [Fact]
    public void Transport_buttons_keep_the_focus_while_a_command_runs_and_play_pause_is_one_element()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, _) => PathOf(request) == "/api/playback"
            ? answer.Task
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true)));
        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: false, position: 0, duration: 100_000, title: "Song")));
        var cut = _ctx.Render<NowPlayingHero>();
        var play = cut.Instance.PlayButton.Id;

        // While the request runs the pressed button stays in the DOM and is not disabled (Chromium would blur it):
        // every transport button is aria-disabled and aria-busy instead, and a second press is ignored.
        cut.Find("button[aria-label=Play]").Click();
        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".transport button"), b =>
        {
            Assert.False(b.HasAttribute("disabled"));
            Assert.Equal("true", b.GetAttribute("aria-disabled"));
            Assert.Equal("true", b.GetAttribute("aria-busy"));
        }));
        cut.Find("button[aria-label=Play]").Click();
        cut.Find("button[aria-label='Next track']").Click();
        Assert.Single(api.Requests);

        // playbackChanged flips the state: the same <button> element changes its label and icon (no swap that would
        // destroy the focused element).
        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: true, position: 0, duration: 100_000, title: "Song")));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".transport button.play[aria-label=Pause]")));
        Assert.Single(cut.FindAll(".transport button.play"));
        Assert.Equal(play, cut.Instance.PlayButton.Id);

        answer.SetResult(StubHandler.Json(Dto.Playback(playing: true, position: 0, duration: 100_000, title: "Song")));
        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".transport button"), b =>
        {
            Assert.False(b.HasAttribute("aria-disabled"));
            Assert.False(b.HasAttribute("aria-busy"));
        }));
        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: false, position: 0, duration: 100_000, title: "Song")));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".transport button.play[aria-label=Play]")));
        Assert.Equal(play, cut.Instance.PlayButton.Id);
    }

    [Fact]
    public void Track_without_an_album_shows_the_duration_alone_and_without_a_duration_the_album_alone()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(position: 0, duration: 200_000, title: "Episode 12") with { Album = null }));
        var cut = _ctx.Render<NowPlayingHero>();
        Assert.Equal("3:20", cut.Find(".hero__info .meta").TextContent);

        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(position: 0, duration: 0, title: "Song")));
        cut.WaitForAssertion(() => Assert.Equal("Album", cut.Find(".hero__info .meta").TextContent));

        _state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(position: 0, duration: 0, title: "Song") with { Album = null }));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".hero__info .meta")));
    }

    [Fact]
    public async Task Artwork_is_an_image_named_by_the_album_in_english_and_german()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var playback = Dto.Playback(title: "Song", duration: 200_000) with { ArtworkUrl = "https://i.scdn.co/image/abc" };
        _state.Apply(new BridgeEvent.PlaybackChanged(playback));
        var cut = _ctx.Render<NowPlayingHero>();

        var img = cut.Find("img.art");
        Assert.Equal("https://i.scdn.co/image/abc", img.GetAttribute("src"));
        Assert.Equal("Cover of Album", img.GetAttribute("alt"));
        Assert.Empty(cut.FindAll(".art--placeholder"));

        var (ctx, state, _, _, _) = UiTest.Create("de");
        await using var context = ctx;
        state.Apply(new BridgeEvent.PlaybackChanged(playback));
        Assert.Equal("Cover von Album", ctx.Render<NowPlayingHero>().Find("img.art").GetAttribute("alt"));
    }

    [Fact]
    public void Spotify_chip_without_a_user_name_says_spotify_only()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true) with { SpotifyUser = null }));
        var cut = _ctx.Render<NowPlayingHero>();

        Assert.Equal("Spotify", cut.Find(".hero .chip--accent").TextContent);
    }

    [Fact]
    public void Hero_redraws_the_extrapolated_position_once_a_second_while_playing()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch.AddDays(1));
        var state = new BridgeState(time);
        _ctx.Services.AddSingleton(state); // the last registration wins over the fixture's state
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: true, position: 61_000, duration: 200_000, title: "Song")));
        var cut = _ctx.Render<NowPlayingHero>();
        Assert.Equal("1:01", cut.Find(".hero__progress > .meta").TextContent);

        time.Now += TimeSpan.FromSeconds(5); // no state change: only the 1 s timer re-renders
        cut.WaitForAssertion(
            () =>
            {
                Assert.Equal("1:06", cut.Find(".hero__progress > .meta").TextContent);
                Assert.Equal("66", cut.Find(".hero__progress [role=progressbar]").GetAttribute("aria-valuenow"));
            },
            TimeSpan.FromSeconds(5));
    }

    // ---- master volume -------------------------------------------------------------------------------------------

    [Fact]
    public void Group_master_sends_the_group_route_and_previews_the_member_fills()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, _) => PathOf(request) == "/api/groups/g/volume" ? answer.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        UseGroupOutput();
        var cut = _ctx.Render<Player>();

        Assert.Equal("Master volume · Downstairs", cut.Find(".master .label").TextContent);
        Assert.Equal("Master volume · Downstairs", cut.Find(".master input[type=range]").GetAttribute("aria-label"));
        Assert.Equal("50 of 100", cut.Find(".master input[type=range]").GetAttribute("aria-valuetext"));
        Assert.Equal("follows the Spotify slider", cut.Find(".master__head .meta").TextContent);
        Assert.Equal(["width: 30%", "width: 80%"], Fills(cut));
        Assert.Contains("only-wide", cut.Find(".master .members").ClassList);
        Assert.Empty(cut.FindAll(".master p.meta"));

        cut.Find(".master input[type=range]").Input("100");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["width: 60%", "width: 100%"], Fills(cut));
            Assert.Equal(["60", "100"], cut.FindAll(".member__level").Select(e => e.TextContent));
            var hint = cut.Find(".master p.meta");
            Assert.Contains("clamp", hint.TextContent, StringComparison.Ordinal);
            Assert.Contains("only-wide", hint.ClassList);
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/groups/g/volume" && r.Body!.Contains("100", StringComparison.Ordinal));
        });

        answer.SetResult(StubHandler.Json(Dto.Group("g", "Downstairs", 100, true, ("a", "Kitchen", 60), ("b", "Living room", 100))));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["width: 60%", "width: 100%"], Fills(cut));
            Assert.Empty(cut.FindAll(".master p.meta"));
            Assert.Equal("100", cut.Find(".master output").TextContent);
        });
        Assert.DoesNotContain(api.Requests, r => r.Path.StartsWith("/api/receivers", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_receiver_master_sends_the_receiver_route_and_shows_no_member_rows()
    {
        var api = UseApi((request, body) => PathOf(request) == "/api/receivers/r1/volume"
            ? StubHandler.Json(Dto.Receiver("r1", "Kitchen", level: LevelIn(body)))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        UseReceiverOutput(level: 40);
        var cut = _ctx.Render<Player>();

        Assert.Equal("Master volume · Kitchen", cut.Find(".master .label").TextContent);
        Assert.Empty(cut.FindAll(".master .members"));
        Assert.Empty(cut.FindAll(".chip--members"));

        cut.Find(".master input[type=range]").Input("70");
        cut.WaitForAssertion(() => Assert.Contains(api.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/receivers/r1/volume" && LevelIn(r.Body) == 70));
        cut.WaitForAssertion(() => Assert.Equal("70", cut.Find(".master output").TextContent));
        Assert.DoesNotContain(api.Requests, r => r.Path.StartsWith("/api/groups", StringComparison.Ordinal));
    }

    [Fact]
    public void Steppers_move_the_master_by_five_and_clamp_at_the_ends()
    {
        var api = UseApi((request, body) => PathOf(request) == "/api/receivers/r1/volume"
            ? StubHandler.Json(Dto.Receiver("r1", "Kitchen", level: LevelIn(body)))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        UseReceiverOutput(level: 98);
        var cut = _ctx.Render<Player>();
        int[] Sent() => [.. api.Requests.Select(r => LevelIn(r.Body))];

        cut.Find("button[aria-label=Louder]").Click();
        cut.WaitForAssertion(() => Assert.Equal([100], Sent()));
        cut.WaitForAssertion(() => Assert.Equal("100", cut.Find(".master output").TextContent));

        cut.Find("button[aria-label=Louder]").Click(); // at 100: nothing to send
        Assert.Equal([100], Sent());

        cut.Find("button[aria-label=Quieter]").Click();
        cut.WaitForAssertion(() => Assert.Equal([100, 95], Sent()));
        cut.WaitForAssertion(() => Assert.Equal("95", cut.Find(".master output").TextContent));
    }

    [Fact]
    public void Steppers_clamp_at_zero()
    {
        var api = UseApi((request, body) => PathOf(request) == "/api/receivers/r1/volume"
            ? StubHandler.Json(Dto.Receiver("r1", "Kitchen", level: LevelIn(body)))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        UseReceiverOutput(level: 3);
        var cut = _ctx.Render<Player>();

        cut.Find("button[aria-label=Quieter]").Click();
        cut.WaitForAssertion(() => Assert.Equal([0], api.Requests.Select(r => LevelIn(r.Body))));
        cut.WaitForAssertion(() => Assert.Equal("0", cut.Find(".master output").TextContent));
        cut.Find("button[aria-label=Quieter]").Click();
        Assert.Single(api.Requests);
    }

    [Fact]
    public void Master_is_disabled_without_an_output()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("r1", "Kitchen")));
        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        var cut = _ctx.Render<Player>();

        Assert.Equal("Master volume · no output", cut.Find(".master .label").TextContent);
        Assert.True(cut.Find(".master input[type=range]").HasAttribute("disabled"));
        Assert.True(cut.Find("button[aria-label=Quieter]").HasAttribute("disabled"));
        Assert.True(cut.Find("button[aria-label=Louder]").HasAttribute("disabled"));
        Assert.Equal("–", cut.Find(".master output").TextContent);
        Assert.Empty(cut.FindAll(".chip--members"));
    }

    [Fact]
    public async Task Members_chip_lists_every_member_with_its_level_in_english_and_german()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        UseGroupOutput();
        var cut = _ctx.Render<Player>();
        var chip = cut.Find(".chip.chip--members");
        Assert.Contains("only-phone", chip.ClassList);
        Assert.Equal("Downstairs · Kitchen 30 · Living room 80", chip.TextContent);

        var (ctx, state, _, _, _) = UiTest.Create("de");
        await using var context = ctx;
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Küche")));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Wohnzimmer")));
        state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "Unten", 50, true, ("a", "Küche", 30), ("b", "Wohnzimmer", 80))));
        state.Apply(new BridgeEvent.OutputChanged(new OutputDto("g", null, 50, [new("a", "Küche", 30, ReceiverConnectionState.Connected), new("b", "Wohnzimmer", 80, ReceiverConnectionState.Connected)])));
        var de = ctx.Render<Player>();
        Assert.Equal("Unten · Küche 30 · Wohnzimmer 80", de.Find(".chip--members").TextContent);
        Assert.Equal("Master-Lautstärke · Unten", de.Find(".master .label").TextContent);
        Assert.Equal("folgt dem Spotify-Regler", de.Find(".master__head .meta").TextContent);
    }

    [Fact]
    public void Steppers_against_a_group_send_the_group_route_and_preview_the_members()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, _) => PathOf(request) == "/api/groups/g/volume" ? answer.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        UseGroupOutput();
        var cut = _ctx.Render<Player>();

        cut.Find("button[aria-label=Louder]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal([55], api.Requests.Where(r => r.Method == HttpMethod.Put && r.Path == "/api/groups/g/volume").Select(r => LevelIn(r.Body)));
            Assert.Equal(["width: 33%", "width: 88%"], Fills(cut)); // GroupVolumePreview at G 55 (30 and 80 at G 50)
            Assert.Equal("Downstairs · Kitchen 33 · Living room 88", cut.Find(".chip--members").TextContent);
        });

        answer.SetResult(StubHandler.Json(Dto.Group("g", "Downstairs", 55, true, ("a", "Kitchen", 33), ("b", "Living room", 88))));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("55", cut.Find(".master output").TextContent);
            Assert.Equal(["width: 33%", "width: 88%"], Fills(cut));
        });
        Assert.DoesNotContain(api.Requests, r => r.Path.StartsWith("/api/receivers", StringComparison.Ordinal));
    }

    [Fact]
    public void A_draft_left_by_an_output_switch_mid_drag_does_not_come_back_with_the_group()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        UseApi((request, _) => PathOf(request) == "/api/groups/g/volume" ? answer.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        UseGroupOutput();
        var groupOutput = _state.Output;
        var cut = _ctx.Render<Player>();

        cut.Find(".master input[type=range]").Input("100");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["width: 60%", "width: 100%"], Fills(cut));
            Assert.NotEmpty(cut.FindAll(".master p.meta"));
        });

        // The output moves to a receiver while the group's send is pending, then back to the group.
        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "a", 30, [new("a", "Kitchen", 30, ReceiverConnectionState.Connected)])));
        cut.WaitForAssertion(() => Assert.Equal("Master volume · Kitchen", cut.Find(".master .label").TextContent));
        _state.Apply(new BridgeEvent.OutputChanged(groupOutput));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Master volume · Downstairs", cut.Find(".master .label").TextContent);
            Assert.Equal(["width: 30%", "width: 80%"], Fills(cut));
            Assert.Equal(["30", "80"], cut.FindAll(".member__level").Select(e => e.TextContent));
            Assert.Empty(cut.FindAll(".master p.meta"));
            Assert.Equal("Downstairs · Kitchen 30 · Living room 80", cut.Find(".chip--members").TextContent);
        });

        answer.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // ---- output list ---------------------------------------------------------------------------------------------

    [Fact]
    public void Output_radios_activate_a_group_and_connect_a_receiver()
    {
        var api = UseApi((request, _) => PathOf(request) switch
        {
            "/api/groups/g/activate" => StubHandler.Json(new OutputDto("g", null, 50, [new("a", "Kitchen", 30, ReceiverConnectionState.Connected), new("b", "Living room", 80, ReceiverConnectionState.Connected)])),
            "/api/receivers/b/connect" => StubHandler.Json(Dto.Receiver("b", "Living room", state: ReceiverConnectionState.Connected)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Living room")));
        _state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "Downstairs", 50, false, ("a", "Kitchen", 30), ("b", "Living room", 80))));
        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        var cut = _ctx.Render<Player>();

        Assert.Empty(cut.FindAll(".output .row.is-active"));
        Assert.Equal(["Downstairs", "Kitchen", "Living room"], cut.FindAll(".output .row strong").Select(s => s.TextContent));

        cut.Find("[data-group-id=g] .row__label").PointerDown();

        cut.Find("[data-group-id=g] input[type=radio]").Change(true);
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/groups/g/activate");
            var row = cut.Find("[data-group-id=g]");
            Assert.Contains("is-active", row.ClassList);
            Assert.True(row.QuerySelector("input[type=radio]")!.HasAttribute("checked"));
            Assert.NotNull(row.QuerySelector(".radio.is-on"));
        });

        cut.Find("[data-receiver-id=b] .row__label").PointerDown();

        cut.Find("[data-receiver-id=b] input[type=radio]").Change(true);
        cut.WaitForAssertion(() => Assert.Contains(api.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/receivers/b/connect"));

        // The output follows with outputChanged.
        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "b", 50, [new("b", "Living room", 50, ReceiverConnectionState.Connected)])));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("is-active", cut.Find("[data-receiver-id=b]").ClassList);
            Assert.DoesNotContain("is-active", cut.Find("[data-group-id=g]").ClassList);
            Assert.Single(cut.FindAll(".output .row.is-active"));
        });
    }

    [Fact]
    public void Receiver_rows_that_are_not_the_output_are_quiet()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        UseReceiverOutput(level: 40);
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("r2", "Office")));
        var cut = _ctx.Render<Player>();

        var output = cut.Find("[data-receiver-id=r1]");
        Assert.Contains("is-active", output.ClassList);
        Assert.DoesNotContain("is-quiet", output.ClassList);
        Assert.Contains("is-quiet", cut.Find("[data-receiver-id=r2]").ClassList);
        Assert.Equal("Volume of Office", cut.Find("[data-receiver-id=r2] input[type=range]").GetAttribute("aria-label"));
        Assert.Equal("50 of 100", cut.Find("[data-receiver-id=r2] input[type=range]").GetAttribute("aria-valuetext"));
    }

    [Fact]
    public void Offline_receivers_cannot_be_selected_and_pin_receivers_offer_pairing_instead_of_a_slider()
    {
        var api = UseApi((request, _) => PathOf(request) == "/api/receivers/pin/pair"
            ? StubHandler.Json(new PairingStartedDto("token", "Enter the PIN."))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("off", "Attic", online: false, state: ReceiverConnectionState.Offline)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("pin", "TV", pairing: PairingState.PinRequired)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("hk", "HomeKit speaker", pairing: PairingState.Disabled)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("ok", "Kitchen")));
        var cut = _ctx.Render<Player>();

        Assert.True(cut.Find("[data-receiver-id=off] input[type=radio]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-receiver-id=hk] input[type=radio]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-receiver-id=ok] input[type=radio]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-receiver-id=pin] input[type=radio]").HasAttribute("disabled"));

        var pinRow = cut.Find("[data-receiver-id=pin]");
        Assert.Null(pinRow.QuerySelector(".level--compact"));
        Assert.Null(pinRow.QuerySelector("input[type=range]"));
        Assert.Equal("PIN needed", pinRow.QuerySelector(".pill")!.TextContent);
        Assert.NotNull(cut.Find("[data-receiver-id=ok] .level--compact"));

        cut.Find("[data-receiver-id=pin] button").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/receivers/pin/pair");
            Assert.Equal("token", _state.Pairing?.Token);
        });
        Assert.Equal("Pair", cut.Find("[data-receiver-id=pin] button").TextContent);
    }

    [Fact]
    public void Group_rows_show_members_and_an_edit_link_and_cannot_be_selected_without_members()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen")));
        _state.Apply(new BridgeEvent.GroupChanged(Dto.Group("e", "Empty", 50, false)));
        _state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "Downstairs", 50, false, ("a", "Kitchen", 30))));
        var cut = _ctx.Render<Player>();

        var group = cut.Find("[data-group-id=g]");
        Assert.Equal("Group · Kitchen", group.QuerySelector(".meta.only-wide")!.TextContent);
        Assert.Equal("Kitchen", group.QuerySelector(".meta.only-phone")!.TextContent);
        var edit = group.QuerySelector("a.btn")!;
        Assert.Equal("Edit", edit.TextContent);
        Assert.Equal("Edit group Downstairs", edit.GetAttribute("aria-label"));
        Assert.Equal("groups/g", edit.GetAttribute("href"));
        // Wide screens only: the phone row is radio, name, meta and pill; the head's "Edit groups" link leads to the editor.
        Assert.Contains("only-wide", edit.ClassList);
        Assert.Null(group.QuerySelector("input[type=range]"));

        var empty = cut.Find("[data-group-id=e]");
        Assert.True(empty.QuerySelector("input[type=radio]")!.HasAttribute("disabled"));
        Assert.Equal("Group · no members", empty.QuerySelector(".meta.only-wide")!.TextContent);
        Assert.Equal("no members", empty.QuerySelector(".meta.only-phone")!.TextContent);
    }

    [Fact]
    public void Stop_output_clears_the_output_and_the_no_output_line_appears()
    {
        var api = UseApi((request, _) => PathOf(request) == "/api/output" ? StubHandler.Json(Dto.NoOutput) : new HttpResponseMessage(HttpStatusCode.NotFound));
        UseReceiverOutput(level: 40);
        var cut = _ctx.Render<Player>();
        Assert.Empty(cut.FindAll(".output__none"));
        Assert.DoesNotContain("Nothing selected", cut.Markup, StringComparison.Ordinal);

        var stop = cut.FindAll(".output .card__actions button").Single();
        Assert.Equal("Stop output", stop.TextContent);
        Assert.DoesNotContain("only-wide", stop.ClassList);
        Assert.False(stop.HasAttribute("aria-disabled"));
        var stopElement = cut.FindComponent<OutputList>().Instance.StopButton.Id;
        stop.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Put && r.Path == "/api/output");
            var none = cut.Find(".output__none");
            Assert.Equal("status", none.GetAttribute("role"));
            Assert.Contains("Nothing selected", none.TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll(".output .row.is-active"));
        });

        // With nothing selected the button stays on wide screens only, aria-disabled: the same element (no @if pair the
        // diff would swap), never disabled, so a keyboard user who pressed it keeps the focus.
        var disabled = cut.FindAll(".output .card__actions button").Single();
        Assert.Equal("true", disabled.GetAttribute("aria-disabled"));
        Assert.False(disabled.HasAttribute("disabled"));
        Assert.Contains("only-wide", disabled.ClassList);
        Assert.Equal(stopElement, cut.FindComponent<OutputList>().Instance.StopButton.Id);
        disabled.Click(); // ignored: nothing to stop
        Assert.Single(api.Requests, r => r.Path == "/api/output");
        var json = JsonDocument.Parse(api.Requests.Single(r => r.Path == "/api/output").Body!).RootElement;
        Assert.Equal(JsonValueKind.Null, json.GetProperty("groupId").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("receiverId").ValueKind);
    }

    [Fact]
    public void No_output_line_shows_only_when_loaded_with_receivers_and_nothing_selected()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cut = _ctx.Render<Player>();
        Assert.Empty(cut.FindAll(".output__none")); // not loaded yet

        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        cut.WaitForAssertion(() => Assert.Contains("No AirPlay receivers found yet.", cut.Find(".output .empty").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".output__none")); // no receivers: the empty state instead

        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("r1", "Kitchen")));
        cut.WaitForAssertion(() => Assert.Equal("Nothing selected: audio is discarded. Choose a receiver or a group.", cut.Find(".output__none").TextContent));
        Assert.Empty(cut.FindAll(".output .empty"));

        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "r1", 40, [new("r1", "Kitchen", 40, ReceiverConnectionState.Connected)])));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".output__none")));
    }

    [Fact]
    public void Output_list_is_a_fieldset_named_by_its_legend()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("r1", "Kitchen")));
        var cut = _ctx.Render<Player>();

        var fieldset = cut.Find("fieldset.output__list");
        Assert.False(fieldset.HasAttribute("role"));
        Assert.False(fieldset.HasAttribute("aria-label"));
        var legend = fieldset.QuerySelector("legend")!;
        Assert.Equal("Choose the output", legend.TextContent);
        Assert.Contains("visually-hidden", legend.ClassList);
        Assert.Empty(cut.FindAll("[role=radiogroup]"));

        var radio = cut.Find("[data-receiver-id=r1] input[type=radio]");
        Assert.Equal("output", radio.GetAttribute("name"));
        Assert.Contains("visually-hidden", radio.ClassList);
        Assert.Equal("Kitchen", cut.Find("#" + radio.GetAttribute("aria-labelledby")!.Replace(":", "\\:", StringComparison.Ordinal)).TextContent);
        Assert.Equal("Edit groups", cut.Find(".output .card__actions a.only-phone").TextContent);
        Assert.Equal("New group", cut.Find(".output .card__actions a.only-wide").TextContent);
    }

    [Fact]
    public void Output_card_says_it_is_connecting_before_the_first_snapshot()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cut = _ctx.Render<OutputList>();

        Assert.Equal("Connecting to the bridge…", cut.Find(".output > p.meta").TextContent);
        Assert.Empty(cut.FindAll("fieldset"));
        Assert.Empty(cut.FindAll(".output .empty"));
    }

    [Fact]
    public void A_busy_row_says_connecting_and_keeps_its_radio_enabled()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = UseApi((request, _) => PathOf(request) == "/api/receivers/b/connect" ? answer.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Living room")));
        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        var cut = _ctx.Render<OutputList>();
        Assert.False(cut.Find("[data-receiver-id=b]").HasAttribute("aria-busy"));

        cut.Find("[data-receiver-id=b] .row__label").PointerDown();

        cut.Find("[data-receiver-id=b] input[type=radio]").Change(true);
        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("[data-receiver-id=b]");
            Assert.Equal("true", row.GetAttribute("aria-busy"));
            var pill = row.QuerySelector(".pill")!;
            Assert.Equal("connecting…", pill.TextContent);
            Assert.Contains("pill--accent", pill.ClassList);
            Assert.False(row.QuerySelector("input[type=radio]")!.HasAttribute("disabled")); // keeps the keyboard focus
        });

        cut.Find("[data-receiver-id=b] .row__label").PointerDown();

        cut.Find("[data-receiver-id=b] input[type=radio]").Change(true); // a repeat while busy is ignored
        Assert.Single(api.Requests);

        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "b", 50, [new("b", "Living room", 50, ReceiverConnectionState.Connected)])));
        answer.SetResult(StubHandler.Json(Dto.Receiver("b", "Living room", state: ReceiverConnectionState.Connected)));
        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("[data-receiver-id=b]");
            Assert.False(row.HasAttribute("aria-busy"));
            Assert.Equal("connected", row.QuerySelector(".pill")!.TextContent);
            Assert.Contains("is-active", row.ClassList);
            Assert.True(row.QuerySelector("input[type=radio]")!.HasAttribute("checked"));
        });
    }

    [Fact]
    public void Arrow_key_picks_wait_for_the_last_row_and_send_one_request()
    {
        var api = UseApi((request, _) => PathOf(request) switch
        {
            "/api/receivers/c/connect" => StubHandler.Json(Dto.Receiver("c", "Office", state: ReceiverConnectionState.Connected)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Living room")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("c", "Office")));
        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        var cut = _ctx.Render<OutputList>();

        // Arrow keys stepping through the radio set: a change per step and no pointer press before any of them. A
        // replaced pick ends its handler on the renderer's own time (a render that renews the handler ids), so each
        // step finds and changes its radio on the renderer's dispatcher, with no render in between.
        void Step(string id) => cut.InvokeAsync(() => cut.Find($"[data-receiver-id={id}] input[type=radio]").Change(true)).GetAwaiter().GetResult();

        Step("a");
        Step("b");
        Step("c");
        Assert.Empty(api.Requests); // nothing is sent while the keys still move

        // Only the row the user stopped on is connected, once.
        cut.WaitForAssertion(() => Assert.Single(api.Requests), TimeSpan.FromSeconds(5));
        Assert.Equal("/api/receivers/c/connect", api.Requests.Single().Path);
        Thread.Sleep(OutputList.KeyboardSelectDelay * 2);
        Assert.Single(api.Requests);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".row[aria-busy]")));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    [InlineData("pin")]
    public void Native_check_returns_to_the_output_when_a_select_leaves_the_output_unchanged(string outcome)
    {
        var api = UseApi((request, _) =>
        {
            if (PathOf(request) != "/api/receivers/b/connect")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            switch (outcome)
            {
                case "failed":
                    return StubHandler.Problem(HttpStatusCode.InternalServerError, "Connect failed", "The receiver refused.");
                case "error":
                    return StubHandler.Json(Dto.Receiver("b", "Living room", state: ReceiverConnectionState.Error, error: "refused"));
                default:
                    _state.Apply(new BridgeEvent.PairingRequired("b", "token"));
                    return StubHandler.Json(Dto.Receiver("b", "Living room", pairing: PairingState.PinRequired));
            }
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen", state: ReceiverConnectionState.Connected)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Living room")));
        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "a", 50, [new("a", "Kitchen", 50, ReceiverConnectionState.Connected)])));
        var cut = _ctx.Render<OutputList>();
        bool Checked(string id) => cut.Find($"[data-receiver-id={id}] input[type=radio]").HasAttribute("checked");
        var checksOfB = new ConcurrentQueue<bool>();
        cut.OnMarkupUpdated += (_, _) => checksOfB.Enqueue(Checked("b"));

        // The browser has checked b's radio when the change event fires; the output stays a.
        cut.Find("[data-receiver-id=b] .row__label").PointerDown();
        cut.Find("[data-receiver-id=b] input[type=radio]").Change(true);
        cut.WaitForAssertion(() =>
        {
            Assert.Single(api.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/receivers/b/connect");
            Assert.False(cut.Find("[data-receiver-id=b]").HasAttribute("aria-busy"));
            // One render matched the browser (b checked) so the next one, back on a, is a diff Blazor applies.
            Assert.Contains(true, checksOfB);
            Assert.False(checksOfB.Last());
            Assert.True(Checked("a"));
            Assert.False(Checked("b"));
            Assert.Single(cut.FindAll(".row.is-active"));
            Assert.Contains("is-active", cut.Find("[data-receiver-id=a]").ClassList);
            Assert.Single(cut.FindAll(".radio.is-on"));
            Assert.NotNull(cut.Find("[data-receiver-id=a] .radio.is-on"));
        });
        if (outcome == "pin")
        {
            Assert.Equal("token", _state.Pairing?.Token);
        }

        // The row can be tried again at once.
        cut.Find("[data-receiver-id=b] .row__label").PointerDown();
        cut.Find("[data-receiver-id=b] input[type=radio]").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(2, api.Requests.Count(r => r.Path == "/api/receivers/b/connect")));
        cut.WaitForAssertion(() => Assert.False(Checked("b")));
    }

    [Fact]
    public void A_group_whose_members_are_all_offline_cannot_be_selected()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen", online: false, state: ReceiverConnectionState.Offline)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Living room", online: false, state: ReceiverConnectionState.Offline)));
        _state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "Downstairs", 50, false, ("a", "Kitchen", 30), ("b", "Living room", 80))));
        var cut = _ctx.Render<OutputList>();

        Assert.True(cut.Find("[data-group-id=g] input[type=radio]").HasAttribute("disabled"));
        Assert.Equal("offline", cut.Find("[data-group-id=g] .pill").TextContent);

        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Living room")));
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-group-id=g] input[type=radio]").HasAttribute("disabled")));
    }

    [Fact]
    public void Output_rows_keep_their_other_controls_outside_the_label()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("ok", "Kitchen")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("pin", "TV", pairing: PairingState.PinRequired)));
        _state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "Downstairs", 50, false, ("ok", "Kitchen", 30))));
        var cut = _ctx.Render<OutputList>();

        // A label holds no labelable control but its radio (HTML): the slider (with its <output>), the Pair button and
        // the Edit link are siblings of the row's label, so they never check the radio and need no click workaround.
        foreach (var (selector, inner) in new[] { ("[data-receiver-id=ok]", ".level--compact"), ("[data-receiver-id=pin]", "button"), ("[data-group-id=g]", "a.btn") })
        {
            var row = cut.Find(selector);
            Assert.Equal("DIV", row.TagName);
            Assert.Contains("row--choice", row.ClassList);
            var label = Assert.Single(row.Children, c => c.TagName == "LABEL");
            Assert.Contains("row__label", label.ClassList);
            Assert.Equal(["radio"], label.QuerySelectorAll("input, button, output, select, textarea, a").Select(e => e.GetAttribute("type")));
            Assert.Contains(row.Children, c => c.Matches(inner));
            Assert.Null(row.QuerySelector(inner)!.Closest("label"));
            Assert.DoesNotContain(row.QuerySelectorAll("[style]"), e => !e.Matches("input[type=range]"));
        }
    }

    // ---- status card ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_card_shows_four_facts_in_english_and_german()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        FeedStatusFacts(_state);
        var cut = _ctx.Render<StatusCard>();

        Assert.Equal(4, cut.FindAll(".fact").Count);
        Assert.Equal(["Spotify", "Output", "Receivers", "Live updates"], cut.FindAll(".fact .label").Select(l => l.TextContent));
        Assert.Equal(["connected · martin", "connected", "1 of 2 online", "offline"], cut.FindAll(".fact__value").Select(v => v.TextContent));
        Assert.Contains("text-ok", cut.Find("[data-fact=spotify] .fact__value").ClassList);
        Assert.Contains("text-err", cut.Find("[data-fact=live] .fact__value").ClassList);
        Assert.Equal("settings/diagnostics", cut.Find(".card__head a").GetAttribute("href"));
        Assert.Equal("Diagnostics", cut.Find(".card__head a").TextContent);

        var (ctx, state, _, _, _) = UiTest.Create("de");
        await using var context = ctx;
        FeedStatusFacts(state);
        var de = ctx.Render<StatusCard>();
        Assert.Equal(["Spotify", "Ausgabe", "Empfänger", "Live-Updates"], de.FindAll(".fact .label").Select(l => l.TextContent));
        Assert.Equal(["verbunden · martin", "verbunden", "1 von 2 online", "offline"], de.FindAll(".fact__value").Select(v => v.TextContent));
    }

    [Fact]
    public void Status_card_shows_the_muted_values_without_spotify_and_output()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen")));
        _state.SetConnection(EventConnectionState.Connected);
        var cut = _ctx.Render<StatusCard>();

        Assert.Equal(["not connected", "none", "1 of 1 online", "live"], cut.FindAll(".fact__value").Select(v => v.TextContent));
        Assert.Contains("text-muted", cut.Find("[data-fact=spotify] .fact__value").ClassList);
        Assert.Contains("text-muted", cut.Find("[data-fact=output] .fact__value").ClassList);
        Assert.Contains("text-ok", cut.Find("[data-fact=live] .fact__value").ClassList);

        _state.SetConnection(EventConnectionState.Disconnected, "gone", DateTimeOffset.UtcNow.AddSeconds(5));
        cut.WaitForAssertion(() => Assert.Equal("paused", cut.Find("[data-fact=live] .fact__value").TextContent));
        Assert.Contains("text-warn", cut.Find("[data-fact=live] .fact__value").ClassList);
    }

    [Fact]
    public void Status_card_output_fact_follows_a_selected_output_that_is_not_connected_yet()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen", state: ReceiverConnectionState.Connecting)));
        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "a", 40, [new("a", "Kitchen", 40, ReceiverConnectionState.Connecting)])));
        var cut = _ctx.Render<StatusCard>();
        var fact = () => cut.Find("[data-fact=output] .fact__value");

        Assert.Equal("connecting…", fact().TextContent);
        Assert.Contains("text-accent", fact().ClassList);

        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen", state: ReceiverConnectionState.Error, error: "refused")));
        cut.WaitForAssertion(() => Assert.Equal("error", fact().TextContent));
        Assert.Contains("text-err", fact().ClassList);

        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen", state: ReceiverConnectionState.Streaming)));
        cut.WaitForAssertion(() => Assert.Equal("connected", fact().TextContent));
        Assert.Contains("text-ok", fact().ClassList);

        _state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        cut.WaitForAssertion(() => Assert.Equal("none", fact().TextContent));
        Assert.Contains("text-muted", fact().ClassList);
    }

    [Fact]
    public void Player_page_has_a_hidden_focusable_heading_and_the_three_cards_in_order()
    {
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cut = _ctx.Render<Player>();

        var h1 = cut.Find("h1");
        Assert.Equal("Player", h1.TextContent);
        Assert.Contains("visually-hidden", h1.ClassList);
        Assert.Equal("-1", h1.GetAttribute("tabindex"));
        Assert.Equal("Now playing", cut.Find("section.hero").GetAttribute("aria-label"));
        Assert.Equal(["Output", "Status"], cut.FindAll(".player__side h2").Select(h => h.TextContent));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string[] Fills(IRenderedComponent<Player> cut) =>
        [.. cut.FindAll(".master .member .track__fill").Select(f => f.GetAttribute("style")!)];

    private static int LevelIn(string? body) => JsonDocument.Parse(body!).RootElement.GetProperty("level").GetInt32();

    private void UseGroupOutput()
    {
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen", state: ReceiverConnectionState.Connected)));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Living room", state: ReceiverConnectionState.Connected)));
        _state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "Downstairs", 50, true, ("a", "Kitchen", 30), ("b", "Living room", 80))));
        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto("g", null, 50, [new("a", "Kitchen", 30, ReceiverConnectionState.Connected), new("b", "Living room", 80, ReceiverConnectionState.Connected)])));
    }

    private void UseReceiverOutput(int level)
    {
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("r1", "Kitchen", level: level, state: ReceiverConnectionState.Connected)));
        _state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "r1", level, [new("r1", "Kitchen", level, ReceiverConnectionState.Connected)])));
    }

    private static void FeedStatusFacts(BridgeState state)
    {
        state.Apply(new BridgeEvent.StatusChanged(Dto.Status(spotify: true)));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "Kitchen", state: ReceiverConnectionState.Connected)));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Attic", online: false, state: ReceiverConnectionState.Offline)));
        state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "a", 40, [new("a", "Kitchen", 40, ReceiverConnectionState.Connected)])));
    }
}
