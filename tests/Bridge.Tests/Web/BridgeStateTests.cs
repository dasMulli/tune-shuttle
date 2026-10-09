using Bridge.Contracts;
using Bridge.Web;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>The web app's state container fed with WebSocket events and REST answers (WORK-PLAN.md web-app "Verify").</summary>
public sealed class BridgeStateTests
{
    private static BridgeSnapshot Snapshot(IReadOnlyList<ReceiverDto> receivers, IReadOnlyList<GroupDto>? groups = null, OutputDto? output = null) =>
        new(Dto.Status(), receivers, groups ?? [], output ?? Dto.NoOutput, Dto.Playback());

    [Fact]
    public void Events_fill_the_state_and_raise_changed()
    {
        var state = new BridgeState();
        var changes = 0;
        state.Changed += () => changes++;

        state.Apply(new BridgeEvent.StatusChanged(Dto.Status("Wohnzimmer")));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Bravo")));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "alpha")));
        state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g1", "Upstairs", 50, false, ("a", "alpha", 40))));
        state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(title: "Song")));

        Assert.True(state.Loaded);
        Assert.Equal(6, changes);
        Assert.Equal("Wohnzimmer", state.Status!.DeviceName);
        Assert.Equal(["alpha", "Bravo"], state.Receivers.Select(r => r.Name)); // by name, case-insensitive
        Assert.Equal("Upstairs", Assert.Single(state.Groups).Name);
        Assert.Equal("Song", state.Playback!.Title);
        Assert.Equal("alpha", state.NameOf("a"));
        Assert.Equal("zzz", state.NameOf("zzz"));

        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "Bravo", state: ReceiverConnectionState.Connected)));
        Assert.Equal(ReceiverConnectionState.Connected, state.Receiver("b")!.State);
        state.Apply(new BridgeEvent.ReceiverRemoved("b"));
        Assert.Null(state.Receiver("b"));
        state.Apply(new BridgeEvent.GroupRemoved("g1"));
        Assert.Empty(state.Groups);

        var version = state.Version;
        state.Apply(new BridgeEvent.Pong(DateTimeOffset.UtcNow));
        Assert.Equal(version, state.Version); // keep-alive only
    }

    [Fact]
    public void Output_change_keeps_the_active_flag_of_groups_consistent()
    {
        var state = new BridgeState();
        state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g1", "One", 50, true, ("a", "A", 50))));
        state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g2", "Two", 50, false, ("b", "B", 50))));

        state.ApplyOutput(new OutputDto("g2", null, 50, [new GroupMemberDto("b", "B", 50, ReceiverConnectionState.Connected)]));

        Assert.False(state.Group("g1")!.Active);
        Assert.True(state.Group("g2")!.Active);
        Assert.True(state.IsInOutput("b"));
        Assert.False(state.IsInOutput("a"));
    }

    [Fact]
    public void Volume_changed_moves_master_member_and_receiver_levels()
    {
        var state = new BridgeState();
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A", level: 30)));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "B", level: 70)));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("solo", "Solo", level: 10)));
        state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "G", 50, true, ("a", "A", 40), ("b", "B", 60))));
        state.Apply(new BridgeEvent.OutputChanged(new OutputDto("g", null, 50, [new("a", "A", 40, ReceiverConnectionState.Connected), new("b", "B", 60, ReceiverConnectionState.Connected)])));

        // Inside the active group the slider shows the member level, not the receiver's own level.
        Assert.Equal(40, state.LevelOf(state.Receiver("a")!));
        Assert.Equal(10, state.LevelOf(state.Receiver("solo")!));

        // G of the group (no receiver id).
        state.Apply(new BridgeEvent.VolumeChanged("g", null, 80, "spotify"));
        Assert.Equal(80, state.Group("g")!.MasterLevel);
        Assert.Equal(80, state.Output.MasterLevel);

        // r_i of a member (the receiver's own buttons, SPEC-audio.md 6.2).
        state.Apply(new BridgeEvent.VolumeChanged("g", "b", 65, "receiver"));
        Assert.Equal(65, state.Group("g")!.Members.Single(m => m.ReceiverId == "b").Level);
        Assert.Equal(65, state.Output.Members.Single(m => m.ReceiverId == "b").Level);
        Assert.Equal(65, state.LevelOf(state.Receiver("b")!));
        Assert.Equal(70, state.Receiver("b")!.Level); // the receiver's own level is untouched

        // A receiver outside a group.
        state.Apply(new BridgeEvent.VolumeChanged(null, "solo", 25, "user"));
        Assert.Equal(25, state.Receiver("solo")!.Level);
        Assert.Equal(80, state.Output.MasterLevel);
    }

    [Fact]
    public void Volume_changed_of_the_single_receiver_output_moves_the_output_level()
    {
        var state = new BridgeState();
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A", level: 30)));
        state.Apply(new BridgeEvent.OutputChanged(new OutputDto(null, "a", 30, [new("a", "A", 30, ReceiverConnectionState.Connected)])));

        state.Apply(new BridgeEvent.VolumeChanged(null, "a", 55, "spotify"));

        Assert.Equal(55, state.Output.MasterLevel);
        Assert.Equal(55, Assert.Single(state.Output.Members).Level);
        Assert.Equal(55, state.LevelOf(state.Receiver("a")!));
    }

    [Fact]
    public void A_reconnect_snapshot_drops_receivers_and_groups_that_disappeared()
    {
        var state = new BridgeState();
        state.SetConnection(EventConnectionState.Connected);
        state.BeginResync();
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A")));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("gone", "Gone")));
        state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g-old", "Old", 50, false)));
        state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        Assert.Equal(2, state.Receivers.Count);

        // Connection lost; the service changed meanwhile; the new connection's snapshot does not repeat "gone" and "g-old".
        state.SetConnection(EventConnectionState.Disconnected, "lost");
        state.SetConnection(EventConnectionState.Connected);
        state.BeginResync();
        state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A")));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("new", "New")));
        state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g-new", "New group", 50, false)));
        Assert.NotNull(state.Receiver("gone")); // removed only when the snapshot is complete
        state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));

        Assert.Equal(["a", "new"], state.Receivers.Select(r => r.Id));
        Assert.Equal(["g-new"], state.Groups.Select(g => g.Id));

        // A later outputChanged (a live event, not a snapshot) removes nothing.
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("late", "Late")));
        state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        Assert.NotNull(state.Receiver("late"));
    }

    [Fact]
    public void Loaded_waits_for_the_end_of_the_live_snapshot()
    {
        var state = new BridgeState();
        state.SetConnection(EventConnectionState.Connected);
        state.BeginResync();

        // Status and log lines come first; the receivers are not in yet, so the pages must not say "none found".
        state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        state.Apply(new BridgeEvent.LogLine(DateTimeOffset.UnixEpoch, "info", "Test", "line"));
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A")));
        Assert.False(state.Loaded);
        state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));
        Assert.True(state.Loaded);
    }

    [Fact]
    public void Member_level_answer_moves_the_member_of_the_active_group()
    {
        var state = new BridgeState();
        state.Apply(new BridgeEvent.GroupChanged(Dto.Group("g", "G", 50, true, ("a", "A", 30), ("b", "B", 60))));
        state.ApplyOutput(new OutputDto("g", null, 50, [new GroupMemberDto("a", "A", 30, ReceiverConnectionState.Connected), new GroupMemberDto("b", "B", 60, ReceiverConnectionState.Connected)]));

        state.ApplyMemberLevel("g", "a", 45);

        Assert.Equal([45, 60], state.Group("g")!.Members.Select(m => m.Level));
        Assert.Equal([45, 60], state.Output.Members.Select(m => m.Level));
        Assert.Equal(50, state.Output.MasterLevel);
    }

    [Fact]
    public void Rest_snapshot_is_ignored_once_the_live_snapshot_arrived()
    {
        var state = new BridgeState();
        Assert.True(state.Load(Snapshot([Dto.Receiver("rest", "From REST")])));
        Assert.Equal("From REST", Assert.Single(state.Receivers).Name);

        state.SetConnection(EventConnectionState.Connected);
        state.BeginResync();
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("ws", "From WebSocket")));
        state.Apply(new BridgeEvent.OutputChanged(Dto.NoOutput));

        // A slow REST answer arriving after the WebSocket snapshot must not overwrite it.
        Assert.False(state.Load(Snapshot([Dto.Receiver("rest", "From REST")])));
        Assert.Equal("From WebSocket", Assert.Single(state.Receivers).Name);

        // While disconnected, REST is the only source again.
        state.SetConnection(EventConnectionState.Disconnected, "lost");
        Assert.True(state.Load(Snapshot([Dto.Receiver("rest", "From REST")])));
    }

    [Fact]
    public void Pairing_required_opens_the_prompt_and_close_only_closes_its_own_token()
    {
        var state = new BridgeState();
        state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("pin", "PIN Speaker", pairing: PairingState.PinRequired)));

        state.Apply(new BridgeEvent.PairingRequired("pin", "token-1"));
        var prompt = state.Pairing!;
        Assert.Equal("pin", prompt.ReceiverId);
        Assert.Equal("token-1", prompt.Token);
        Assert.Equal(PairingPromptOrigin.Service, prompt.Origin);
        Assert.Contains("PIN Speaker", prompt.Message, StringComparison.Ordinal);

        // A new pairing (the service replaced the old one) replaces the prompt; closing the stale token does nothing.
        state.OpenPairing(new PairingPrompt("pin", "token-2", "Enter the PIN", PairingPromptOrigin.User));
        state.ClosePairing("token-1");
        Assert.Equal("token-2", state.Pairing!.Token);
        state.ClosePairing("token-2");
        Assert.Null(state.Pairing);
    }

    [Fact]
    public void Log_is_capped_and_a_replay_after_resubscribe_adds_no_duplicates()
    {
        var state = new BridgeState();
        var at = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < BridgeState.MaxLogLines + 20; i++)
        {
            state.Apply(new BridgeEvent.LogLine(at.AddMilliseconds(i), "info", "Test", "line " + i));
        }

        Assert.Equal(BridgeState.MaxLogLines, state.Log.Count);
        Assert.Equal("line 20", state.Log[0].Message);

        // The service replays its last 100 lines on subscribe.
        for (var i = BridgeState.MaxLogLines - 80; i < BridgeState.MaxLogLines + 20; i++)
        {
            state.Apply(new BridgeEvent.LogLine(at.AddMilliseconds(i), "info", "Test", "line " + i));
        }

        Assert.Equal(BridgeState.MaxLogLines, state.Log.Count);
        state.Apply(new BridgeEvent.LogLine(at.AddDays(1), "warn", "Test", "new"));
        Assert.Equal("new", state.Log[^1].Message);
        state.ClearLog();
        Assert.Empty(state.Log);
    }

    [Fact]
    public void Playback_position_is_extrapolated_while_playing_and_capped_at_the_duration()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var state = new BridgeState(time);
        state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: true, position: 10_000, duration: 15_000, title: "Song")));

        time.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(12_000, state.PlaybackPositionMs());
        time.Now += TimeSpan.FromSeconds(10);
        Assert.Equal(15_000, state.PlaybackPositionMs());

        state.Apply(new BridgeEvent.PlaybackChanged(Dto.Playback(playing: false, position: 3_000, duration: 15_000, title: "Song")));
        time.Now += TimeSpan.FromSeconds(5);
        Assert.Equal(3_000, state.PlaybackPositionMs());
    }

    [Fact]
    public void Notices_are_capped_and_dismissable()
    {
        var state = new BridgeState();
        for (var i = 0; i < BridgeState.MaxNotices + 2; i++)
        {
            state.Notify(NoticeKind.Error, "error " + i);
        }

        Assert.Equal(BridgeState.MaxNotices, state.Notices.Count);
        Assert.Equal("error 2", state.Notices[0].Text);
        state.Dismiss(state.Notices[0].Id);
        Assert.Equal("error 3", state.Notices[0].Text);
    }

    [Fact]
    public void Connection_state_changes_are_reported_once()
    {
        var state = new BridgeState();
        var changes = 0;
        state.Changed += () => changes++;
        state.SetConnection(EventConnectionState.Connecting);
        state.SetConnection(EventConnectionState.Connecting);
        state.SetConnection(EventConnectionState.Disconnected, "refused", DateTimeOffset.UnixEpoch);
        Assert.Equal(2, changes);
        Assert.Equal("refused", state.ConnectionError);
        Assert.Equal(DateTimeOffset.UnixEpoch, state.NextAttemptAt);
    }
}
