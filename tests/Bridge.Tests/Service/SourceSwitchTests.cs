using System.Collections.Concurrent;
using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Receiver;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Session;
using Bridge.Contracts;
using Bridge.Service.Output;
using Bridge.Service.Spotify;
using Bridge.Service.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests.Service;

/// <summary>
/// <see cref="SourceSwitch"/> (SPEC-receiver.md R8, 8.2, 11.1 "Bridge"): claims and evictions with stubbed source
/// delegates (a claim grants only after the evicted source's pause or session end completed), releases, the remembered
/// start, the owner's playback view and <c>ActiveSource</c> in <c>statusChanged</c>; and the output manager's side of 8.6
/// (<c>SpotifyVolumeUpdates</c> suppressed while AirPlay owns the output, a non-owner's level dropped).
/// </summary>
public sealed class SourceSwitchTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly SenderInfo PhoneA = new("Phone A", "iPhone14,3", "02:00:5E:00:00:0A", IPAddress.Parse("192.0.2.10"));
    private static readonly SenderInfo PhoneB = new("Phone B", "iPhone15,2", "02:00:5E:00:00:0B", IPAddress.Parse("192.0.2.11"));

    [Fact]
    public async Task Airplay_claim_waits_for_spotify_to_pause_then_takes_ownership()
    {
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evictions = 0;
        var harness = new Harness(new SourceSwitchHooks
        {
            EvictSpotifyAsync = _ =>
            {
                Interlocked.Increment(ref evictions);
                return pause.Task;
            },
        });
        Assert.True(harness.Switch.TryClaimUnowned(AudioSource.Spotify));

        var claim = harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(claim.IsCompleted); // SPEC-receiver.md 8.2: the grant comes after the eviction
        Assert.Equal(AudioSource.Spotify, harness.Switch.Owner);
        Assert.False(harness.Switch.MayWrite(AudioSource.Airplay));

        pause.SetResult();
        await claim.WaitAsync(Patience);
        Assert.Equal(1, evictions);
        Assert.Equal(AudioSource.Airplay, harness.Switch.Owner);
        Assert.Equal(PhoneA, harness.Switch.Sender);
        Assert.True(harness.Switch.MayWrite(AudioSource.Airplay));
        Assert.False(harness.Switch.MayWrite(AudioSource.Spotify));
        await harness.Switch.StatusPublication.WaitAsync(Patience);
        Assert.Equal("airplay", harness.Statuses.Last().ActiveSource);
    }

    [Fact]
    public async Task A_spotify_pause_that_never_completes_is_bounded_and_the_claim_still_grants()
    {
        var harness = new Harness(new SourceSwitchHooks { EvictSpotifyAsync = _ => Task.Delay(Timeout.Infinite) }, spotifyTimeout: TimeSpan.FromMilliseconds(200));
        harness.Switch.TryClaimUnowned(AudioSource.Spotify);

        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(AudioSource.Airplay, harness.Switch.Owner);
    }

    [Fact]
    public async Task Spotify_claim_ends_the_airplay_session_first()
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reasons = new ConcurrentQueue<string>();
        var harness = new Harness(new SourceSwitchHooks
        {
            EvictAirplayAsync = (reason, _) =>
            {
                reasons.Enqueue(reason);
                return ended.Task;
            },
        });
        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);

        var claim = harness.Switch.ClaimAsync(AudioSource.Spotify, null, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(claim.IsCompleted);
        Assert.Equal(AudioSource.Airplay, harness.Switch.Owner);

        ended.SetResult();
        await claim.WaitAsync(Patience);
        Assert.Equal(["replaced by Spotify"], reasons);
        Assert.Equal(AudioSource.Spotify, harness.Switch.Owner);
        Assert.Null(harness.Switch.Sender);
        await harness.Switch.StatusPublication.WaitAsync(Patience);
        Assert.Equal("spotify", harness.Statuses.Last().ActiveSource);
    }

    [Fact]
    public async Task A_second_airplay_claim_replaces_the_first_sender_and_the_old_session_end_does_not_release_it()
    {
        var evictions = 0;
        var harness = new Harness(new SourceSwitchHooks
        {
            EvictAirplayAsync = (_, _) =>
            {
                Interlocked.Increment(ref evictions);
                return Task.CompletedTask;
            },
        });
        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);

        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneB, CancellationToken.None);

        // R8: the receiver ends the older session itself; the switch only records the new sender and publishes it.
        Assert.Equal(0, evictions);
        Assert.Equal(AudioSource.Airplay, harness.Switch.Owner);
        Assert.Equal(PhoneB, harness.Switch.Sender);
        await harness.Switch.StatusPublication.WaitAsync(Patience);
        Assert.Equal(2, harness.Statuses.Count);

        harness.Switch.Release(AudioSource.Airplay, PhoneA); // the replaced session's SenderGone
        Assert.Equal(AudioSource.Airplay, harness.Switch.Owner);
        harness.Switch.Release(AudioSource.Airplay, PhoneB);
        Assert.Equal(AudioSource.None, harness.Switch.Owner);
        Assert.Null(harness.Switch.Sender);
        await harness.Switch.StatusPublication.WaitAsync(Patience);
        Assert.Null(harness.Statuses.Last().ActiveSource);
    }

    [Fact]
    public async Task Release_by_a_non_owner_changes_nothing_and_release_by_the_owner_leaves_none()
    {
        var harness = new Harness(new SourceSwitchHooks());
        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);

        harness.Switch.Release(AudioSource.Spotify);
        Assert.Equal(AudioSource.Airplay, harness.Switch.Owner);

        harness.Switch.Release(AudioSource.Airplay);
        Assert.Equal(AudioSource.None, harness.Switch.Owner);

        // Unowned: every command passes, Spotify's first write claims.
        Assert.True(harness.Switch.MayCommand(AudioSource.Airplay));
        Assert.True(harness.Switch.MayCommand(AudioSource.Spotify));
        Assert.False(harness.Switch.MayWrite(AudioSource.Spotify));
        Assert.True(harness.Switch.TryClaimUnowned(AudioSource.Spotify));
        Assert.Equal(AudioSource.Spotify, harness.Switch.Owner);
        Assert.False(harness.Switch.TryClaimUnowned(AudioSource.Airplay));
    }

    [Fact]
    public async Task A_non_owners_commands_are_dropped_and_a_dropped_start_is_replayed_on_ownership()
    {
        var replays = 0;
        var harness = new Harness(new SourceSwitchHooks
        {
            StartSpotifyIfRunningAsync = _ =>
            {
                Interlocked.Increment(ref replays);
                return Task.CompletedTask;
            },
        });
        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);

        Assert.False(harness.Switch.Admit(AudioSource.Spotify, "Flush"));
        Assert.False(harness.Switch.Admit(AudioSource.Spotify, "Now playing"));
        Assert.False(harness.Switch.AdmitStart(AudioSource.Spotify));
        Assert.True(harness.Switch.Admit(AudioSource.Airplay, "Flush"));
        Assert.True(harness.Switch.AdmitStart(AudioSource.Airplay));
        Assert.Equal(3, harness.Switch.DroppedCommands);

        await harness.Switch.ClaimAsync(AudioSource.Spotify, null, CancellationToken.None);
        Assert.Equal(1, replays);

        // Remembered once: a later claim does not replay again.
        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);
        await harness.Switch.ClaimAsync(AudioSource.Spotify, null, CancellationToken.None);
        Assert.Equal(1, replays);
    }

    [Fact]
    public async Task The_playback_view_follows_the_owner()
    {
        var harness = new Harness(new SourceSwitchHooks());
        var spotify = new PlaybackDto(true, "spotify:track:x", "Spotify song", "Artist", "Album", 1000, 200_000, null, 40);
        var airplay = new PlaybackDto(true, null, "Phone song", "Phone artist", "Phone album", 5000, 180_000, "/api/playback/artwork?v=1", 40);

        Assert.True(harness.Switch.PublishPlayback(AudioSource.Spotify, spotify)); // unowned: Spotify's view is shown
        Assert.Null(harness.Runtime.Playback.Source);
        Assert.False(harness.Switch.PublishPlayback(AudioSource.Airplay, airplay)); // not the owner: stored only
        Assert.Equal("Spotify song", harness.Runtime.Playback.Title);
        Assert.Equal("Phone song", harness.Runtime.AirplayPlayback.Title);

        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);
        Assert.Equal("Phone song", harness.Runtime.Playback.Title);
        Assert.Equal("airplay", harness.Runtime.Playback.Source);
        Assert.Equal("Phone song", harness.Playbacks.Last().Title);

        // The Paused event the eviction caused arrives late: stored, not shown.
        Assert.False(harness.Switch.PublishPlayback(AudioSource.Spotify, spotify with { Playing = false }));
        Assert.Equal("Phone song", harness.Runtime.Playback.Title);
        Assert.False(harness.Runtime.SpotifyPlayback.Playing);

        harness.Switch.Release(AudioSource.Airplay);
        Assert.Null(harness.Runtime.Playback.Title); // None: an empty view
        Assert.False(harness.Runtime.Playback.Playing);
        Assert.Equal(40, harness.Runtime.Playback.SpotifyVolume);
        Assert.Null(harness.Runtime.Playback.Source);

        await harness.Switch.ClaimAsync(AudioSource.Spotify, null, CancellationToken.None);
        Assert.Equal("Spotify song", harness.Runtime.Playback.Title);
        Assert.Equal("spotify", harness.Runtime.Playback.Source);
    }

    [Fact]
    public async Task Status_changed_events_carry_the_active_source_in_order()
    {
        var harness = new Harness(new SourceSwitchHooks());

        harness.Switch.TryClaimUnowned(AudioSource.Spotify);
        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);
        harness.Switch.Release(AudioSource.Airplay);
        await harness.Switch.StatusPublication.WaitAsync(Patience);

        // One status per change, in order, each naming the owner of its change; the rest comes from the status builder.
        Assert.Equal(["spotify", "airplay", null], harness.Statuses.Select(s => s.ActiveSource));
        Assert.All(harness.Statuses, s => Assert.Equal("test", s.Version));
    }

    [Fact]
    public async Task A_failing_eviction_still_grants_and_the_claimant_wins()
    {
        var harness = new Harness(new SourceSwitchHooks { EvictAirplayAsync = (_, _) => throw new InvalidOperationException("receiver gone") });
        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);

        await harness.Switch.ClaimAsync(AudioSource.Spotify, null, CancellationToken.None);

        Assert.Equal(AudioSource.Spotify, harness.Switch.Owner);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Switch.ClaimAsync(AudioSource.None, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_start_the_replaced_sender_dropped_is_not_replayed_for_a_later_claim()
    {
        var replays = 0;
        var harness = new Harness(new SourceSwitchHooks
        {
            ResumeAirplayIfPlayingAsync = _ =>
            {
                Interlocked.Increment(ref replays);
                return Task.CompletedTask;
            },
        });
        harness.Switch.TryClaimUnowned(AudioSource.Spotify);
        Assert.False(harness.Switch.AdmitStart(AudioSource.Airplay)); // sender A's start, dropped and remembered
        harness.Switch.Release(AudioSource.Spotify);
        Assert.True(harness.Switch.TryClaimUnowned(AudioSource.Airplay));

        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneB, CancellationToken.None); // B replaces A (R8)
        await harness.Switch.ClaimAsync(AudioSource.Spotify, null, CancellationToken.None);
        await harness.Switch.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);

        Assert.Equal(0, replays); // SPEC-receiver.md 8.2: A's start belonged to A's session, not to a later claim
    }

    [Fact]
    public async Task Spotify_volume_updates_are_suppressed_while_airplay_owns_the_output()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        _ = factory.CreateClient();
        var store = factory.Get<IBridgeStore>();
        await WaitUntilAsync(async () => await store.GetReceiverAsync(a.Id, CancellationToken.None) is not null);
        var output = factory.Get<OutputManager>();
        var sources = factory.Get<SourceSwitch>();
        Assert.Same(sources, output.Sources);
        await output.ConnectReceiverAsync(a.Id, CancellationToken.None);
        Drain(output);

        // Nobody owns the output: a level set in the web app reaches the Spotify slider, as before the receiver.
        await output.SetReceiverLevelAsync(a.Id, 60, OutputManager.SourceUser, CancellationToken.None);
        Assert.Equal(60, await ReadAsync(output));

        // AirPlay owns it: the web app's level goes to the receivers, not to Spotify (R9, 8.6); Spotify's own level is dropped.
        await sources.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);
        await output.SetReceiverLevelAsync(a.Id, 30, OutputManager.SourceUser, CancellationToken.None);
        await output.SetSourceLevelAsync(80, OutputManager.SourceSpotify, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(output.SpotifyVolumeUpdates.TryRead(out _));
        Assert.Equal(30, (await store.GetReceiverAsync(a.Id, CancellationToken.None))!.Level);
        await output.SetSourceLevelAsync(45, OutputManager.SourceAirplay, CancellationToken.None);
        Assert.Equal(45, (await store.GetReceiverAsync(a.Id, CancellationToken.None))!.Level);
        Assert.False(output.SpotifyVolumeUpdates.TryRead(out _));

        // AirPlay leaves: the Spotify slider catches up with the level set meanwhile.
        sources.Release(AudioSource.Airplay);
        Assert.Equal(45, await ReadAsync(output));
        Assert.True(output.QueuedFrames >= 0);
    }

    [Fact]
    public async Task A_spotify_tagged_command_is_dropped_by_the_output_manager_while_airplay_owns_and_airplays_pass()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        _ = factory.CreateClient();
        var store = factory.Get<IBridgeStore>();
        await WaitUntilAsync(async () => await store.GetReceiverAsync(a.Id, CancellationToken.None) is not null);
        var output = factory.Get<OutputManager>();
        var sources = factory.Get<SourceSwitch>();
        await output.ConnectReceiverAsync(a.Id, CancellationToken.None);
        var player = Assert.IsType<GroupPlayer>(output.ActivePlayer);
        await sources.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);
        var nowPlaying = new NowPlaying(new TrackMetadata("Synthetic Song", "Test Artist", null, null), null, null, TimeSpan.Zero);
        var frames = new short[2 * 352];

        // SPEC-receiver.md 8.2: every Spotify-tagged command is dropped (and counted) while AirPlay owns the output.
        var dropped = sources.DroppedCommands;
        await output.StartPlayerAsync(AudioSource.Spotify, CancellationToken.None);
        Assert.False(player.IsStarted);
        await output.FlushAsync(AudioSource.Spotify, CancellationToken.None);
        await output.SetNowPlayingAsync(AudioSource.Spotify, nowPlaying, CancellationToken.None);
        await output.PausePlayerAsync(AudioSource.Spotify, CancellationToken.None);
        Assert.False(output.Write(AudioSource.Spotify, frames));
        Assert.Equal(dropped + 4, sources.DroppedCommands);

        // The owner's commands pass.
        await output.StartPlayerAsync(AudioSource.Airplay, CancellationToken.None);
        Assert.True(player.IsStarted);
        Assert.True(output.Write(AudioSource.Airplay, frames));
        await output.SetNowPlayingAsync(AudioSource.Airplay, nowPlaying, CancellationToken.None);
        await output.FlushAsync(AudioSource.Airplay, CancellationToken.None);
        await output.PausePlayerAsync(AudioSource.Airplay, CancellationToken.None);
        Assert.False(player.IsStarted);
        Assert.Equal(dropped + 4, sources.DroppedCommands);

        // N2: a receiver button while AirPlay owns the output does not reach Spotify; once released it does.
        await a.WaitAsync<FakeReceiverEvent.EventChannelConnected>();
        await a.Fake.SendRemoteCommandAsync("nitm", CancellationToken.None);
        await Task.Delay(300);
        Assert.False(output.RemoteCommands.TryRead(out _));
        sources.Release(AudioSource.Airplay);
        await a.Fake.SendRemoteCommandAsync("play", CancellationToken.None);
        using var cts = new CancellationTokenSource(Patience);
        Assert.Equal("play", await output.RemoteCommands.ReadAsync(cts.Token));
    }

    [Fact]
    public async Task Spotify_regaining_the_output_after_airplay_restores_its_now_playing_on_the_receivers()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        _ = factory.CreateClient();
        var store = factory.Get<IBridgeStore>();
        await WaitUntilAsync(async () => await store.GetReceiverAsync(a.Id, CancellationToken.None) is not null);
        var output = factory.Get<OutputManager>();
        var sources = factory.Get<SourceSwitch>();
        await output.ConnectReceiverAsync(a.Id, CancellationToken.None);
        var spotify = new NowPlaying(new TrackMetadata("Spotify Song", "Test Artist", null, null), null, null, TimeSpan.Zero);
        var phone = new NowPlaying(new TrackMetadata("Phone Song", "Test Artist", null, null), null, null, TimeSpan.Zero);
        static bool Titled(FakeReceiverEvent.Metadata m, string title) => m.Items.TryGetValue("minm", out var t) && Equals(t, title);

        await output.SetNowPlayingAsync(AudioSource.Spotify, spotify, CancellationToken.None);
        await a.WaitAsync<FakeReceiverEvent.Metadata>(m => Titled(m, "Spotify Song"));
        await sources.ClaimAsync(AudioSource.Airplay, PhoneA, CancellationToken.None);
        await output.SetNowPlayingAsync(AudioSource.Airplay, phone, CancellationToken.None);
        await a.WaitAsync<FakeReceiverEvent.Metadata>(m => Titled(m, "Phone Song"));

        // SPEC-receiver.md 8.2: Spotify's track goes back to the receivers when Spotify owns the output again.
        await sources.ClaimAsync(AudioSource.Spotify, null, CancellationToken.None);
        await a.WaitAsync<FakeReceiverEvent.Metadata>(m => Titled(m, "Spotify Song"), skip: 1);
    }

    private static void Drain(OutputManager output)
    {
        while (output.SpotifyVolumeUpdates.TryRead(out _))
        {
        }
    }

    private static async Task<int> ReadAsync(OutputManager output)
    {
        using var cts = new CancellationTokenSource(Patience);
        return await output.SpotifyVolumeUpdates.ReadAsync(cts.Token);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + RecordedFake.Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }

    /// <summary>A switch with stub hooks whose published events are recorded.</summary>
    private sealed class Harness
    {
        private readonly ConcurrentQueue<BridgeEvent> _events = new();

        public Harness(SourceSwitchHooks hooks, TimeSpan? spotifyTimeout = null)
        {
            hooks = hooks with
            {
                StatusAsync = hooks.StatusAsync ?? (_ => Task.FromResult(new StatusDto("test", false, null, "TuneShuttle", false, 0, DateTimeOffset.UnixEpoch))),
            };
            Switch = new SourceSwitch(Runtime, null, NullLogger<SourceSwitch>.Instance, hooks) { SpotifyEvictionTimeout = spotifyTimeout ?? TimeSpan.FromSeconds(2) };
            Switch.Published += _events.Enqueue;
        }

        public RuntimeState Runtime { get; } = new();

        public SourceSwitch Switch { get; }

        public IReadOnlyList<StatusDto> Statuses => _events.OfType<BridgeEvent.StatusChanged>().Select(e => e.Status).ToList();

        public IReadOnlyList<PlaybackDto> Playbacks => _events.OfType<BridgeEvent.PlaybackChanged>().Select(e => e.Playback).ToList();
    }
}
