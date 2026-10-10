using System.Net;
using System.Threading.Channels;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;
using Bridge.Contracts;
using Bridge.Service;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Bridge.Service.Store;
using Bridge.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests.Service;

/// <summary>
/// Forgetting receivers: <c>DELETE /receivers/{id}</c> and <c>DELETE /receivers?offline=true</c> against the bridge
/// in-process (store cascade, runtime state, events), the last-seen time on the DTO, the pruning rules of
/// <see cref="ReceiverJanitor"/> with a fake clock, and the schedule of <see cref="ReceiverPruner"/>.
/// </summary>
public sealed class ForgetReceiversTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static string ReceiverUrl(string id) => Http.Receiver(ApiRoutes.Receiver, id);

    [Fact]
    public async Task Forgetting_refuses_an_online_receiver_and_removes_an_offline_one_with_its_state()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var factory = new BridgeFactory();
        var client = factory.CreateClient();
        var store = factory.Get<IBridgeStore>();
        var announced = a.Describe();
        factory.Browser.Add(announced);
        await WaitUntilAsync(async () => await store.GetReceiverAsync(a.Id, Ct) is not null);

        // User state that must go with it: a pairing, the only seat in a group, the stored output selection.
        var group = await client.SendJsonAsync<GroupDto>(HttpMethod.Post, ApiRoutes.Groups, new GroupRequest("Solo"));
        await client.SendJsonAsync<GroupDto>(HttpMethod.Post, Http.Group(ApiRoutes.GroupMembers, group.Id), new GroupMemberRequest(a.Id, 40));
        await store.SaveAsync(a.Id, new PairingCredentials(ControllerIdentity.Generate(), "accessory", Ed25519KeyPair.Generate().PublicKey), Ct);
        await store.SetSettingAsync(SettingKeys.SelectedOutputReceiver, a.Id, Ct);
        await using var events = await EventClient.ConnectAsync(factory);

        var online = await client.GetJsonAsync<ReceiverDto>(ReceiverUrl(a.Id));
        Assert.True(online.Online);
        Assert.NotNull(online.LastSeen);

        using (var refused = await client.DeleteAsync(ReceiverUrl(a.Id)))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        Assert.NotNull(await store.GetReceiverAsync(a.Id, Ct));

        // Gone from mDNS: offline, with the arrival time of its last record. The stored last-seen is backdated first, so
        // only the Lost event can have moved it there.
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        await store.UpsertReceiverAsync((await store.GetReceiverAsync(a.Id, Ct))! with { LastSeen = Mapping.FormatTimestamp(stale) }, Ct);
        var heard = Mapping.ParseTimestamp(Mapping.FormatTimestamp(announced.LastSeen));
        factory.Browser.Remove(a.Id);
        await WaitUntilAsync(async () => Mapping.ParseTimestamp((await store.GetReceiverAsync(a.Id, Ct))!.LastSeen) == heard);
        var offline = await client.WaitForReceiverAsync(a.Id, r => !r.Online);
        Assert.Equal(heard, offline.LastSeen);

        using (var forgotten = await client.DeleteAsync(ReceiverUrl(a.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, forgotten.StatusCode);
        }

        Assert.Null(await store.GetReceiverAsync(a.Id, Ct));
        Assert.Null(await store.GetAsync(a.Id, Ct));
        Assert.Null(await store.GetSettingAsync(SettingKeys.SelectedOutputReceiver, Ct));
        Assert.DoesNotContain(await client.GetJsonAsync<List<ReceiverDto>>(ApiRoutes.Receivers), r => r.Id == a.Id);

        // The group stays, empty; both changes are announced.
        var emptied = await client.GetJsonAsync<GroupDto>(Http.Group(ApiRoutes.Group, group.Id));
        Assert.Empty(emptied.Members);
        await events.WaitAsync<BridgeEvent.ReceiverRemoved>(e => e.Id == a.Id);
        await events.WaitAsync<BridgeEvent.GroupChanged>(e => e.Group.Id == group.Id && e.Group.Members.Count == 0);

        using (var again = await client.DeleteAsync(ReceiverUrl(a.Id)))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        using (var unknown = await client.DeleteAsync(ReceiverUrl("00:00:00:00:00:00")))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        // It comes back as new when it shows up again.
        factory.Browser.Add(a.Describe());
        var back = await client.WaitForReceiverAsync(a.Id, r => r.Online);
        Assert.Equal(PairingState.Transient, back.Pairing);
        Assert.Null(back.GroupId);
    }

    [Fact]
    public async Task Bulk_forget_removes_every_offline_receiver_and_keeps_the_online_ones()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var b = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake B" });
        await using var c = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake C" });
        await using var factory = new BridgeFactory();
        var client = factory.CreateClient();
        var store = factory.Get<IBridgeStore>();
        foreach (var fake in new[] { a, b, c })
        {
            factory.Browser.Add(fake.Describe());
        }

        await WaitUntilAsync(async () => (await store.GetReceiversAsync(Ct)).Count == 3);
        factory.Browser.Remove(a.Id);
        factory.Browser.Remove(b.Id);
        await client.WaitForReceiverAsync(b.Id, r => !r.Online);
        await client.WaitForReceiverAsync(a.Id, r => !r.Online);
        await using var events = await EventClient.ConnectAsync(factory);

        using (var invalid = await client.DeleteAsync(ApiRoutes.Receivers))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        var result = await client.SendJsonAsync<ForgottenReceiversDto>(HttpMethod.Delete, ApiRoutes.Receivers + "?offline=true");
        Assert.Equal(new[] { a.Id, b.Id }.Order(StringComparer.Ordinal), result.Removed.Order(StringComparer.Ordinal));
        Assert.Equal([c.Id], (await client.GetJsonAsync<List<ReceiverDto>>(ApiRoutes.Receivers)).Select(r => r.Id));
        await events.WaitAsync<BridgeEvent.ReceiverRemoved>(e => e.Id == a.Id);
        await events.WaitAsync<BridgeEvent.ReceiverRemoved>(e => e.Id == b.Id);

        // Nothing offline any more: an empty answer.
        Assert.Empty((await client.SendJsonAsync<ForgottenReceiversDto>(HttpMethod.Delete, ApiRoutes.Receivers + "?offline=true")).Removed);
    }

    [Theory]
    [InlineData("eligible", null)]
    [InlineData("recent", "seen recently")]
    [InlineData("online", "online")]
    [InlineData("paired", "paired")]
    [InlineData("pairingState", "paired")]
    [InlineData("grouped", "in a group")]
    [InlineData("selected", "the selected output")]
    [InlineData("level", "has its own level, offset or quirks")]
    [InlineData("offset", "has its own level, offset or quirks")]
    [InlineData("quirks", "has its own level, offset or quirks")]
    [InlineData("neverSeen", "never seen")]
    [InlineData("off", "pruning is off")]
    public void Keep_reasons_cover_each_kind_of_user_state(string kind, string? expected)
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var row = Row("R", now - TimeSpan.FromDays(kind == "recent" ? 2 : 10)) with
        {
            PairingState = kind == "pairingState" ? PairingStates.Paired : PairingStates.Transient,
            Level = kind == "level" ? 70 : SqliteBridgeStore.DefaultLevel,
            OffsetFrames = kind == "offset" ? 441 : 0,
            Quirks = kind == "quirks" ? "{\"timing\":\"ptp\"}" : null,
        };
        if (kind == "neverSeen")
        {
            row = row with { LastSeen = null };
        }

        var reason = ReceiverJanitor.KeepReason(
            row,
            offline: kind != "online",
            hasStoredPairing: kind == "paired",
            inGroup: kind == "grouped",
            selected: kind == "selected",
            now,
            kind == "off" ? TimeSpan.Zero : TimeSpan.FromDays(3));
        Assert.Equal(expected, reason);
    }

    [Fact]
    public async Task Pruning_forgets_only_offline_receivers_without_user_state_seen_longer_ago_than_the_setting()
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTime(now);
        await using var online = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Online" });
        await using var factory = new BridgeFactory(time: clock);
        var client = factory.CreateClient();
        var store = factory.Get<IBridgeStore>();
        var janitor = factory.Get<ReceiverJanitor>();
        var old = now - TimeSpan.FromDays(10);

        await store.UpsertReceiverAsync(Row("ELIGIBLE", old), Ct);
        await store.UpsertReceiverAsync(Row("RECENT", now - TimeSpan.FromDays(2)), Ct);
        await store.UpsertReceiverAsync(Row("PAIRED", old) with { PairingState = PairingStates.Paired }, Ct);
        await store.SaveAsync("PAIRED", new PairingCredentials(ControllerIdentity.Generate(), "accessory", Ed25519KeyPair.Generate().PublicKey), Ct);
        await store.UpsertReceiverAsync(Row("GROUPED", old), Ct);
        var group = await store.CreateGroupAsync("G", Ct);
        await store.SetGroupMemberAsync(group.Id, "GROUPED", 50, Ct);
        await store.UpsertReceiverAsync(Row("SELECTED", old), Ct);
        await store.SetSettingAsync(SettingKeys.SelectedOutputReceiver, "SELECTED", Ct);
        await store.UpsertReceiverAsync(Row("LEVEL", old) with { Level = 70 }, Ct);
        await store.UpsertReceiverAsync(Row("OFFSET", old) with { OffsetFrames = -441 }, Ct);

        // Online with an old stored last-seen (records unchanged for days): the run moves it to now and keeps it.
        factory.Browser.Add(online.Describe());
        await WaitUntilAsync(async () => await store.GetReceiverAsync(online.Id, Ct) is not null);
        await store.UpsertReceiverAsync((await store.GetReceiverAsync(online.Id, Ct))! with { LastSeen = Mapping.FormatTimestamp(old) }, Ct);
        await using var events = await EventClient.ConnectAsync(factory);

        Assert.Equal(["ELIGIBLE"], await janitor.PruneAsync(Ct));
        Assert.Null(await store.GetReceiverAsync("ELIGIBLE", Ct));
        await events.WaitAsync<BridgeEvent.ReceiverRemoved>(e => e.Id == "ELIGIBLE");
        Assert.Equal(
            new[] { "GROUPED", "LEVEL", "OFFSET", "PAIRED", "RECENT", "SELECTED", online.Id }.Order(StringComparer.Ordinal),
            (await store.GetReceiversAsync(Ct)).Select(r => r.Id).Order(StringComparer.Ordinal));
        // Moved to the time of its latest record (the fake was described just now; the fake clock is not the wall clock).
        var touched = Mapping.ParseTimestamp((await store.GetReceiverAsync(online.Id, Ct))!.LastSeen);
        Assert.True(touched > old + TimeSpan.FromDays(9), $"last seen still {touched}");

        // Time passes: RECENT crosses the three days; the others still carry state.
        clock.Now = now + TimeSpan.FromDays(2);
        Assert.Equal(["RECENT"], await janitor.PruneAsync(Ct));

        // 0 never forgets; a longer period spares what the default would take.
        await store.UpsertReceiverAsync(Row("LATER", now - TimeSpan.FromDays(5)), Ct);
        await ReceiverRetention.SaveDaysAsync(store, 0, Ct);
        Assert.Empty(await janitor.PruneAsync(Ct));
        await ReceiverRetention.SaveDaysAsync(store, 30, Ct);
        Assert.Empty(await janitor.PruneAsync(Ct));
        Assert.NotNull(await store.GetReceiverAsync("LATER", Ct));
        await ReceiverRetention.SaveDaysAsync(store, 3, Ct);
        Assert.Equal(["LATER"], await janitor.PruneAsync(Ct));
    }

    [Fact]
    public async Task Retention_comes_from_the_configuration_until_the_settings_page_stores_one()
    {
        var options = new BridgeOptions();
        Assert.Equal(TimeSpan.FromDays(3), options.ForgetOfflineAfter);
        Assert.Equal(3, ReceiverRetention.ToDays(options.ForgetOfflineAfter));
        Assert.Equal(0.5, ReceiverRetention.ToDays(TimeSpan.FromHours(12)));

        // What is stored is what the API reports (a thousandth of a day): 0.0004 days is never, not 35 seconds.
        await using var factory = new BridgeFactory();
        var store = factory.Get<IBridgeStore>();
        Assert.Equal(TimeSpan.FromDays(3), await ReceiverRetention.LoadAsync(store, options, Ct));
        await ReceiverRetention.SaveDaysAsync(store, 0.0004, Ct);
        Assert.Equal(TimeSpan.Zero, await ReceiverRetention.LoadAsync(store, options, Ct));
        await ReceiverRetention.SaveDaysAsync(store, 1.0004, Ct);
        Assert.Equal(TimeSpan.FromDays(1), await ReceiverRetention.LoadAsync(store, options, Ct));
        await ReceiverRetention.SaveDaysAsync(store, 0.25, Ct);
        Assert.Equal(TimeSpan.FromHours(6), await ReceiverRetention.LoadAsync(store, options, Ct));
    }

    [Fact]
    public async Task Pruner_runs_after_the_startup_delay_then_hourly_and_a_failed_run_does_not_end_it()
    {
        var time = new SteppedTime(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var runs = Channel.CreateUnbounded<int>();
        var count = 0;
        using var pruner = new ReceiverPruner(
            _ =>
            {
                var run = Interlocked.Increment(ref count);
                runs.Writer.TryWrite(run);
                return run switch
                {
                    1 => Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("the store is gone")),
                    // A cancellation that is not the host stopping (a timeout inside the player): skip this run only.
                    2 => Task.FromCanceled<IReadOnlyList<string>>(new CancellationToken(canceled: true)),
                    _ => Task.FromResult<IReadOnlyList<string>>(["GONE"]),
                };
            },
            time,
            NullLogger<ReceiverPruner>.Instance);

        await pruner.StartAsync(Ct);
        Assert.Equal(ReceiverPruner.StartupDelay, await time.NextTimerAsync());
        Assert.False(runs.Reader.TryRead(out _), "ran before the start-up delay");

        time.Advance(ReceiverPruner.StartupDelay);
        Assert.Equal(1, await NextRunAsync());
        for (var expected = 2; expected <= 3; expected++)
        {
            Assert.Equal(ReceiverPruner.Interval, await time.NextTimerAsync());
            time.Advance(ReceiverPruner.Interval - TimeSpan.FromSeconds(1));
            Assert.False(runs.Reader.TryRead(out _), "ran before the hour was up");
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(expected, await NextRunAsync());
        }

        Assert.Equal(ReceiverPruner.Interval, await time.NextTimerAsync());
        Assert.False(pruner.ExecuteTask!.IsCompleted);
        await pruner.StopAsync(Ct);
        Assert.True(pruner.ExecuteTask.IsCompletedSuccessfully);

        async Task<int> NextRunAsync()
        {
            using var timeout = new CancellationTokenSource(RecordedFake.Patience);
            return await runs.Reader.ReadAsync(timeout.Token);
        }
    }

    private static ReceiverRow Row(string id, DateTimeOffset lastSeen) =>
        StoreTests.Receiver(id, "Speaker " + id) with { LastSeen = Mapping.FormatTimestamp(lastSeen), PublicKey = null };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + RecordedFake.Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }
}
