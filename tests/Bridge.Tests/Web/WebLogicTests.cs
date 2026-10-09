using Airplay2.Sender.Playback;
using Bridge.Web;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>The web app's pure helpers: the group volume preview and the slider's coalescing sender.</summary>
public sealed class WebLogicTests
{
    [Theory]
    [InlineData(50, 80)]
    [InlineData(50, 20)]
    [InlineData(37, 100)]
    [InlineData(80, 33)]
    [InlineData(100, 1)]
    [InlineData(1, 100)]
    [InlineData(0, 60)]
    public void Preview_matches_the_service_group_volume_model(int master, int newMaster)
    {
        // The service scales with Airplay2.Sender's GroupVolumeModel; the preview must show the same r_i.
        int[] levels = [0, 1, 13, 25, 40, 50, 64, 77, 99, 100];
        var model = new GroupVolumeModel(master);
        for (var i = 0; i < levels.Length; i++)
        {
            model.SetReceiverLevel("r" + i, levels[i]);
        }

        var group = Dto.Group("g", "G", master, false, [.. levels.Select((l, i) => ("r" + i, "R" + i, l))]);
        model.SetMasterLevel(newMaster);
        var preview = GroupVolumePreview.Preview(group, newMaster);

        foreach (var member in preview)
        {
            Assert.Equal(model.Levels[member.ReceiverId], member.Level);
        }
    }

    [Fact]
    public void Preview_keeps_the_member_levels_when_the_master_goes_to_zero()
    {
        // The service stores the r_i unchanged at G' = 0 (OutputManager.SetGroupLevelAsync), so its groupChanged shows them.
        var group = Dto.Group("g", "G", 60, false, ("a", "A", 30), ("b", "B", 90));
        Assert.Same(group.Members, GroupVolumePreview.Preview(group, 0));
        Assert.False(GroupVolumePreview.Clamps(group, 0));
    }

    [Fact]
    public void Preview_reports_clamping()
    {
        var group = Dto.Group("g", "G", 50, false, ("a", "A", 30), ("b", "B", 80));
        Assert.False(GroupVolumePreview.Clamps(group, 60));
        Assert.True(GroupVolumePreview.Clamps(group, 70)); // 80/50*70 = 112 -> clamps at 100
        Assert.Equal([42, 100], GroupVolumePreview.Preview(group, 70).Select(m => m.Level));
        Assert.Same(group.Members, GroupVolumePreview.Preview(group, 50));
    }

    [Fact]
    public async Task Coalescing_sender_sends_the_first_and_the_latest_value_only()
    {
        var sent = new List<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new CoalescingSender<int>(async (v, ct) =>
        {
            sent.Add(v);
            if (sent.Count == 1)
            {
                await release.Task;
            }
        });

        var first = sender.SubmitAsync(10, CancellationToken.None);
        await WebClients.WaitUntilAsync(() => sent.Count == 1, "the first send");
        var second = sender.SubmitAsync(20, CancellationToken.None);
        var third = sender.SubmitAsync(30, CancellationToken.None);
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.True(sender.Busy);

        release.SetResult();
        await first;
        Assert.Equal([10, 30], sent);
        Assert.False(sender.Busy);
        Assert.Equal(2, sender.Sent);

        await sender.SubmitAsync(40, CancellationToken.None);
        Assert.Equal([10, 30, 40], sent);
    }

    [Fact]
    public async Task Coalescing_sender_surfaces_a_failure_and_recovers()
    {
        var calls = 0;
        var sender = new CoalescingSender<int>((v, ct) => ++calls == 1 ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SubmitAsync(1, CancellationToken.None));
        Assert.False(sender.Busy);
        await sender.SubmitAsync(2, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Events_uri_follows_the_page_scheme_and_base_path()
    {
        Assert.Equal(new Uri("ws://bridge.local:5000/ws/events"), BridgeEventClient.EventsUri(new Uri("http://bridge.local:5000/")));
        Assert.Equal(new Uri("wss://example.org/bridge/ws/events"), BridgeEventClient.EventsUri(new Uri("https://example.org/bridge/")));
    }

    [Fact]
    public void Retry_delay_doubles_up_to_the_cap()
    {
        var client = new BridgeEventClient(
            new BridgeState(),
            new Uri("ws://localhost/ws/events"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BridgeEventClient>.Instance,
            new BridgeEventClientOptions { InitialRetryDelay = TimeSpan.FromSeconds(1), MaxRetryDelay = TimeSpan.FromSeconds(10) });
        Assert.InRange(client.RetryDelay(0).TotalSeconds, 0.8, 1.2);
        Assert.InRange(client.RetryDelay(2).TotalSeconds, 3.2, 4.8);
        Assert.InRange(client.RetryDelay(10).TotalSeconds, 8, 12);
        Assert.InRange(client.RetryDelay(1000).TotalSeconds, 8, 12);
    }

    [Fact]
    public void Route_escapes_receiver_ids()
    {
        Assert.Equal("/api/receivers/02%3AFA%3ACE/connect", BridgeApiClient.Route(Bridge.Contracts.ApiRoutes.ReceiverConnect, "02:FA:CE"));
        Assert.Equal("/api/groups/g%201/members/a%3Ab", BridgeApiClient.Route(Bridge.Contracts.ApiRoutes.GroupMember, "g 1", "a:b"));
    }
}
