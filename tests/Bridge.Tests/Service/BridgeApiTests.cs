using System.Net;
using Airplay2.FakeReceiver;
using Bridge.Contracts;
using Bridge.Service;
using Bridge.Service.Output;
using Bridge.Service.Store;
using Xunit;

namespace Bridge.Tests.Service;

/// <summary>
/// Every route of the bridge API (docs/ARCHITECTURE.md, "Bridge API") against the bridge in-process with a temp database and in-process fake
/// receivers: connect a fake, set volumes, create a group, select the output, the
/// PIN flow, and the WebSocket events.
/// </summary>
public sealed class BridgeApiTests
{
    private static string ReceiverUrl(string id) => Http.Receiver(ApiRoutes.Receivers + "/{id}", id);

    [Fact]
    public async Task Status_settings_diagnostics_and_openapi_respond()
    {
        await using var factory = new BridgeFactory();
        var client = factory.CreateClient();

        var status = await client.GetJsonAsync<StatusDto>(ApiRoutes.Status);
        Assert.False(status.SpotifyConnected);
        Assert.Equal("TuneShuttle", status.DeviceName);
        Assert.False(status.OutputConnected);

        var settings = await client.GetJsonAsync<SettingsDto>(ApiRoutes.Settings);
        Assert.Equal(new SettingsDto("TuneShuttle", 77175, false, "Realtime", 3), settings);

        using (var invalid = await client.SendJsonAsync(HttpMethod.Put, ApiRoutes.Settings, settings with { LatencyFrames = 10 }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        }

        using (var invalid = await client.SendJsonAsync(HttpMethod.Put, ApiRoutes.Settings, settings with { PreferredStream = "carrier pigeon" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        using (var invalid = await client.SendJsonAsync(HttpMethod.Put, ApiRoutes.Settings, settings with { ForgetOfflineAfterDays = -1 }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        using (var invalid = await client.SendJsonAsync(HttpMethod.Put, ApiRoutes.Settings, settings with { ForgetOfflineAfterDays = 3651 }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        // A client that predates ForgetOfflineAfterDays (null) keeps the value; a new one sets it (0 = never).
        var updated = await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, new SettingsDto("Wohnzimmer", 88200, true, "buffered"));
        Assert.Equal(new SettingsDto("Wohnzimmer", 88200, true, "Buffered", 3), updated);
        Assert.Equal(updated, await client.GetJsonAsync<SettingsDto>(ApiRoutes.Settings));
        updated = await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, updated with { ForgetOfflineAfterDays = 0 });
        Assert.Equal(0, updated.ForgetOfflineAfterDays);
        updated = await client.SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, updated with { ForgetOfflineAfterDays = 7.5 });
        Assert.Equal(new SettingsDto("Wohnzimmer", 88200, true, "Buffered", 7.5), updated);
        Assert.Equal(updated, await client.GetJsonAsync<SettingsDto>(ApiRoutes.Settings));
        Assert.Equal(TimeSpan.FromDays(7.5), await ReceiverRetention.LoadAsync(factory.Get<IBridgeStore>(), new BridgeOptions(), CancellationToken.None));
        Assert.Equal("Wohnzimmer", (await client.GetJsonAsync<StatusDto>(ApiRoutes.Status)).DeviceName);
        Assert.Equal("Wohnzimmer", await factory.Get<IBridgeStore>().GetSettingAsync(SettingKeys.DeviceName, CancellationToken.None));

        var diagnostics = await client.GetJsonAsync<DiagnosticsDto>(ApiRoutes.Diagnostics);
        Assert.NotEmpty(diagnostics.OpenQuestionsPending);
        Assert.Contains(diagnostics.RecentLog, l => l.Contains("Settings", StringComparison.Ordinal));

        using var openApi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        Assert.Contains("/api/receivers/{id}/connect", await openApi.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Playback_and_spotify_routes_without_a_spotify_session()
    {
        await using var factory = new BridgeFactory();
        var client = factory.CreateClient();

        var playback = await client.GetJsonAsync<PlaybackDto>(ApiRoutes.Playback);
        Assert.False(playback.Playing);

        using (var conflict = await client.SendJsonAsync(HttpMethod.Post, ApiRoutes.Playback, new PlaybackCommandRequest("play")))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        using (var invalid = await client.SendJsonAsync(HttpMethod.Post, ApiRoutes.Playback, new PlaybackCommandRequest("dance")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        var store = factory.Get<IBridgeStore>();
        await store.SaveSpotifyCredentialsAsync(new SpotifyCredentialRow("someone", 1, [1, 2, 3]), CancellationToken.None);
        using (var forgotten = await client.DeleteAsync(ApiRoutes.Spotify))
        {
            Assert.Equal(HttpStatusCode.NoContent, forgotten.StatusCode);
        }

        Assert.Null(await store.GetSpotifyCredentialsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Receivers_come_from_discovery_and_unknown_ids_are_404()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        var client = factory.CreateClient();

        var receivers = await client.GetJsonAsync<List<ReceiverDto>>(ApiRoutes.Receivers);
        var dto = Assert.Single(receivers);
        Assert.Equal(a.Id, dto.Id);
        Assert.Equal("Fake A", dto.Name);
        Assert.True(dto.Online);
        Assert.Equal(ReceiverConnectionState.Available, dto.State);
        Assert.Equal(PairingState.Transient, dto.Pairing);
        Assert.True(dto.Capabilities.AirPlay2);
        Assert.Equal(50, dto.Level);

        // The inventory stores what it discovers (the receivers table).
        await WaitUntilAsync(async () => await factory.Get<IBridgeStore>().GetReceiverAsync(a.Id, CancellationToken.None) is not null);

        using var missing = await client.GetAsync(ReceiverUrl("00:00:00:00:00:00"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingConnect = await client.PostAsync(Http.Receiver(ApiRoutes.ReceiverConnect, "00:00:00:00:00:00"), null);
        Assert.Equal(HttpStatusCode.NotFound, missingConnect.StatusCode);
        using var missingGroup = await client.GetAsync(Http.Group(ApiRoutes.Group, "nope"));
        Assert.Equal(HttpStatusCode.NotFound, missingGroup.StatusCode);

        // Lost from mDNS: still listed (from the store), offline.
        factory.Browser.Remove(a.Id);
        var offline = await client.WaitForReceiverAsync(a.Id, r => !r.Online);
        Assert.Equal(ReceiverConnectionState.Offline, offline.State);
        Assert.Equal("127.0.0.1", offline.Address);
    }

    [Fact]
    public async Task Connect_volume_offset_and_disconnect_drive_the_fake_receiver()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        var client = factory.CreateClient();
        await using var events = await EventClient.ConnectAsync(factory);

        var connected = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverConnect, a.Id));
        Assert.Equal(ReceiverConnectionState.Connected, connected.State);
        await a.WaitAsync<FakeReceiverEvent.SessionSetup>();
        await a.WaitAsync<FakeReceiverEvent.StreamSetup>();
        await a.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -15.0) < 0.001); // level 50 -> -30 + 0.3 * 50
        await events.WaitAsync<BridgeEvent.ReceiverChanged>(e => e.Receiver.Id == a.Id && e.Receiver.State == ReceiverConnectionState.Connected);

        var output = await client.GetJsonAsync<OutputDto>(ApiRoutes.Output);
        Assert.Equal(a.Id, output.ReceiverId);
        Assert.Null(output.GroupId);
        Assert.Equal(50, output.MasterLevel);
        Assert.Equal(ReceiverConnectionState.Connected, Assert.Single(output.Members).State);
        Assert.True((await client.GetJsonAsync<StatusDto>(ApiRoutes.Status)).OutputConnected);

        var louder = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Put, Http.Receiver(ApiRoutes.ReceiverVolume, a.Id), new VolumeRequest(20));
        Assert.Equal(20, louder.Level);
        await a.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -24.0) < 0.001);
        await events.WaitAsync<BridgeEvent.VolumeChanged>(e => e.ReceiverId == a.Id && e.Level == 20 && e.Source == "user");
        Assert.Equal(20, (await factory.Get<IBridgeStore>().GetReceiverAsync(a.Id, CancellationToken.None))!.Level);
        using (var invalid = await client.SendJsonAsync(HttpMethod.Put, Http.Receiver(ApiRoutes.ReceiverVolume, a.Id), new VolumeRequest(101)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        // The offset is part of the session (SPEC-audio.md 3.3): the connected receiver re-joins with it.
        var setupsBefore = a.Count<FakeReceiverEvent.SessionSetup>();
        var offset = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Put, Http.Receiver(ApiRoutes.ReceiverOffset, a.Id), new OffsetRequest(441));
        Assert.Equal(441, offset.OffsetFrames);
        await a.WaitAsync<FakeReceiverEvent.SessionSetup>(skip: setupsBefore);
        await client.WaitForReceiverAsync(a.Id, r => r.State == ReceiverConnectionState.Connected);
        using (var invalid = await client.SendJsonAsync(HttpMethod.Put, Http.Receiver(ApiRoutes.ReceiverOffset, a.Id), new OffsetRequest(1_000_000)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        var teardowns = a.Count<FakeReceiverEvent.Teardown>();
        var disconnected = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverDisconnect, a.Id));
        Assert.Equal(ReceiverConnectionState.Available, disconnected.State);
        await a.WaitAsync<FakeReceiverEvent.Teardown>(skip: teardowns);
        output = await client.GetJsonAsync<OutputDto>(ApiRoutes.Output);
        Assert.Null(output.ReceiverId);
        Assert.Empty(output.Members);
        await events.WaitAsync<BridgeEvent.OutputChanged>(e => e.Output.ReceiverId is null);
    }

    [Fact]
    public async Task Groups_scale_member_levels_activate_and_tear_down()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var b = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake B" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        factory.Browser.Add(b.Describe());
        var client = factory.CreateClient();

        using (var created = await client.SendJsonAsync(HttpMethod.Post, ApiRoutes.Groups, new GroupRequest("Erdgeschoss")))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var invalid = await client.SendJsonAsync(HttpMethod.Post, ApiRoutes.Groups, new GroupRequest(" ")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        var group = Assert.Single(await client.GetJsonAsync<List<GroupDto>>(ApiRoutes.Groups));
        Assert.Equal("Erdgeschoss", group.Name);
        Assert.Equal(50, group.MasterLevel);
        Assert.False(group.Active);

        group = await client.SendJsonAsync<GroupDto>(HttpMethod.Put, Http.Group(ApiRoutes.Group, group.Id), new GroupRequest("Haus"));
        Assert.Equal("Haus", group.Name);
        group = await client.SendJsonAsync<GroupDto>(HttpMethod.Post, Http.Group(ApiRoutes.GroupMembers, group.Id), new GroupMemberRequest(a.Id, 40));
        group = await client.SendJsonAsync<GroupDto>(HttpMethod.Post, Http.Group(ApiRoutes.GroupMembers, group.Id), new GroupMemberRequest(b.Id, 80));
        Assert.Equal([(a.Id, 40), (b.Id, 80)], group.Members.Select(m => (m.ReceiverId, m.Level)).ToArray());

        // G 50 -> 25 halves every r_i (docs/ARCHITECTURE.md, "Group volume model"); inactive groups are scaled in the store.
        group = await client.SendJsonAsync<GroupDto>(HttpMethod.Put, Http.Group(ApiRoutes.GroupVolume, group.Id), new VolumeRequest(25));
        Assert.Equal(25, group.MasterLevel);
        Assert.Equal([20, 40], group.Members.Select(m => m.Level).ToArray());

        var output = await client.SendJsonAsync<OutputDto>(HttpMethod.Post, Http.Group(ApiRoutes.GroupActivate, group.Id));
        Assert.Equal(group.Id, output.GroupId);
        Assert.Equal(25, output.MasterLevel);
        Assert.All(output.Members, m => Assert.Equal(ReceiverConnectionState.Connected, m.State));
        var setupA = await a.WaitAsync<FakeReceiverEvent.SessionSetup>();
        var setupB = await b.WaitAsync<FakeReceiverEvent.SessionSetup>();
        Assert.Equal(group.Id, setupA.Body.GetString("groupUUID"));
        Assert.Equal(group.Id, setupB.Body.GetString("groupUUID"));
        await a.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -24.0) < 0.001); // r = 20
        await b.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -18.0) < 0.001); // r = 40
        Assert.True((await client.GetJsonAsync<GroupDto>(Http.Group(ApiRoutes.Group, group.Id))).Active);

        // G to 0 mutes; back to 50 restores the relative levels (20:40 at G 25 -> 40:80 at G 50).
        group = await client.SendJsonAsync<GroupDto>(HttpMethod.Put, Http.Group(ApiRoutes.GroupVolume, group.Id), new VolumeRequest(0));
        await a.WaitAsync<FakeReceiverEvent.Volume>(v => v.Db <= -144.0);
        await b.WaitAsync<FakeReceiverEvent.Volume>(v => v.Db <= -144.0);
        group = await client.SendJsonAsync<GroupDto>(HttpMethod.Put, Http.Group(ApiRoutes.GroupVolume, group.Id), new VolumeRequest(50));
        Assert.Equal([40, 80], group.Members.Select(m => m.Level).ToArray());
        await a.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -18.0) < 0.001);
        await b.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -6.0) < 0.001);

        // r_i of one member inside the active group: only that receiver changes.
        var memberLevel = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Put, Http.Receiver(ApiRoutes.ReceiverVolume, a.Id), new VolumeRequest(10));
        Assert.Equal(10, Assert.Single((await client.GetJsonAsync<GroupDto>(Http.Group(ApiRoutes.Group, group.Id))).Members, m => m.ReceiverId == a.Id).Level);
        Assert.Equal(group.Id, memberLevel.GroupId);
        await a.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -27.0) < 0.001);

        // Removing a member tears it down; the other keeps playing.
        var teardownsB = b.Count<FakeReceiverEvent.Teardown>();
        group = await client.SendJsonAsync<GroupDto>(HttpMethod.Delete, Http.Group(ApiRoutes.GroupMember, group.Id, b.Id));
        Assert.Equal(a.Id, Assert.Single(group.Members).ReceiverId);
        await b.WaitAsync<FakeReceiverEvent.Teardown>(skip: teardownsB);
        Assert.Equal(0, a.Count<FakeReceiverEvent.Teardown>());

        // Adding it back to the active group joins it at once.
        await client.SendJsonAsync<GroupDto>(HttpMethod.Post, Http.Group(ApiRoutes.GroupMembers, group.Id), new GroupMemberRequest(b.Id, null));
        await b.WaitAsync<FakeReceiverEvent.SessionSetup>(skip: 1);
        await client.WaitForReceiverAsync(b.Id, r => r.State == ReceiverConnectionState.Connected);

        // Deleting the active group clears the output and tears every member down.
        using (var deleted = await client.DeleteAsync(Http.Group(ApiRoutes.Group, group.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await a.WaitAsync<FakeReceiverEvent.Teardown>();
        output = await client.GetJsonAsync<OutputDto>(ApiRoutes.Output);
        Assert.Null(output.GroupId);
        Assert.Empty(await client.GetJsonAsync<List<GroupDto>>(ApiRoutes.Groups));
        using var gone = await client.DeleteAsync(Http.Group(ApiRoutes.Group, group.Id));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Put_output_selects_a_receiver_and_the_selection_survives_a_restart()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        var dataDirectory = Path.Combine(Path.GetTempPath(), "bridge-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = new BridgeFactory(dataDirectory))
            {
                first.Browser.Add(a.Describe());
                var client = first.CreateClient();
                using (var both = await client.SendJsonAsync(HttpMethod.Put, ApiRoutes.Output, new OutputRequest("g", a.Id)))
                {
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, both.StatusCode);
                }

                var output = await client.SendJsonAsync<OutputDto>(HttpMethod.Put, ApiRoutes.Output, new OutputRequest(null, a.Id));
                Assert.Equal(a.Id, output.ReceiverId);
                await a.WaitAsync<FakeReceiverEvent.StreamSetup>();
            }

            // Disposing the host tears the receiver down (D7) ...
            await a.WaitAsync<FakeReceiverEvent.Teardown>();

            // ... and a new host restores the selected output from the settings and connects it again.
            await using var second = new BridgeFactory(dataDirectory);
            second.Browser.Add(a.Describe());
            var again = second.CreateClient();
            var restored = await again.WaitForReceiverAsync(a.Id, r => r.State == ReceiverConnectionState.Connected);
            Assert.Equal(a.Id, (await again.GetJsonAsync<OutputDto>(ApiRoutes.Output)).ReceiverId);
            Assert.True(restored.Online);
            await a.WaitAsync<FakeReceiverEvent.StreamSetup>(skip: 1);

            var none = await again.SendJsonAsync<OutputDto>(HttpMethod.Put, ApiRoutes.Output, new OutputRequest(null, null));
            Assert.Null(none.ReceiverId);
            await a.WaitAsync<FakeReceiverEvent.Teardown>(skip: 1);
        }
        finally
        {
            BridgeFactory.DeleteDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task The_sink_outlives_librespot_and_host_stop_tears_down_even_with_a_closed_sink()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using (var factory = new BridgeFactory())
        {
            factory.Browser.Add(a.Describe());
            var client = factory.CreateClient();
            var sink = factory.Get<AirplaySink>();
            await client.SendJsonAsync<OutputDto>(HttpMethod.Put, ApiRoutes.Output, new OutputRequest(null, a.Id));
            await a.WaitAsync<FakeReceiverEvent.StreamSetup>();

            // librespot's Player disposes the sink it was given when its thread ends (Player.Drop: every shutdown, or a
            // crash while the service keeps running). The sink and later outputs must survive that.
            sink.LibrespotSink.Dispose();
            var again = await client.SendJsonAsync<OutputDto>(HttpMethod.Put, ApiRoutes.Output, new OutputRequest(null, a.Id));
            Assert.Equal(a.Id, again.ReceiverId);
            await a.WaitAsync<FakeReceiverEvent.StreamSetup>(skip: 1);
            await a.WaitAsync<FakeReceiverEvent.Teardown>(); // the re-selection replaced the first session

            // Even a sink closed before the output manager stops must not cost the receivers their TEARDOWN (D7).
            await sink.DisposeAsync();
        }

        await a.WaitAsync<FakeReceiverEvent.Teardown>(skip: 1);
    }

    [Fact]
    public async Task Pin_receiver_connect_announces_pairing_and_the_pin_flow_stores_the_keys()
    {
        await using var pin = await RecordedFake.StartPinAsync("PIN Fake", "4711");
        await using var factory = new BridgeFactory();
        factory.Browser.Add(pin.Describe());
        var client = factory.CreateClient();
        await using var events = await EventClient.ConnectAsync(factory);

        var listed = await client.GetJsonAsync<ReceiverDto>(ReceiverUrl(pin.Id));
        Assert.Equal(PairingState.PinRequired, listed.Pairing);
        Assert.True(listed.Capabilities.Pin);

        // Connecting without keys: the bridge starts the PIN pairing and tells the web app (pairingRequired).
        var attempt = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverConnect, pin.Id));
        Assert.Equal(ReceiverConnectionState.Error, attempt.State);
        Assert.Equal(PairingState.PinRequired, attempt.Pairing);
        var required = await events.WaitAsync<BridgeEvent.PairingRequired>(e => e.ReceiverId == pin.Id);

        // A wrong PIN is a validation error and consumes the pairing.
        using (var wrong = await client.SendJsonAsync(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverPairPin, pin.Id), new PairingPinRequest(required.PairingToken, "0000")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        }

        using (var reused = await client.SendJsonAsync(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverPairPin, pin.Id), new PairingPinRequest(required.PairingToken, "4711")))
        {
            Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        }

        // POST /pair starts a new one; the right PIN stores the keys and the bridge connects the receiver by itself.
        var started = await client.SendJsonAsync<PairingStartedDto>(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverPair, pin.Id));
        Assert.False(string.IsNullOrEmpty(started.PairingToken));
        var paired = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverPairPin, pin.Id), new PairingPinRequest(started.PairingToken, "4711"));
        Assert.Equal(PairingState.Paired, paired.Pairing);
        Assert.NotNull(await factory.Get<IBridgeStore>().GetAsync(pin.Id, CancellationToken.None));
        await client.WaitForReceiverAsync(pin.Id, r => r.State == ReceiverConnectionState.Connected);
        await pin.WaitAsync<FakeReceiverEvent.StreamSetup>();

        // Forgetting the keys: back to PIN required.
        using (var forget = await client.DeleteAsync(Http.Receiver(ApiRoutes.ReceiverUnpair, pin.Id)))
        {
            Assert.Equal(HttpStatusCode.NoContent, forget.StatusCode);
        }

        Assert.Null(await factory.Get<IBridgeStore>().GetAsync(pin.Id, CancellationToken.None));
        Assert.Equal(PairingState.PinRequired, (await client.GetJsonAsync<ReceiverDto>(ReceiverUrl(pin.Id))).Pairing);
    }

    [Fact]
    public async Task WebSocket_sends_the_snapshot_first_answers_ping_and_streams_the_log_on_request()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        var client = factory.CreateClient();
        await client.WaitForReceiverAsync(a.Id, r => r.Online);
        await using var events = await EventClient.ConnectAsync(factory);

        await events.WaitAsync<BridgeEvent.OutputChanged>();
        var snapshot = events.Events;
        Assert.IsType<BridgeEvent.StatusChanged>(snapshot[0]);
        Assert.Contains(snapshot, e => e is BridgeEvent.ReceiverChanged r && r.Receiver.Id == a.Id);

        await events.SendAsync(new BridgeClientMessage.Ping());
        await events.WaitAsync<BridgeEvent.Pong>();

        Assert.DoesNotContain(events.Events, e => e is BridgeEvent.LogLine);
        await events.SendAsync(new BridgeClientMessage.Subscribe(true));
        using (var _ = await client.SendJsonAsync(HttpMethod.Post, ApiRoutes.Groups, new GroupRequest("Logged")))
        {
        }

        await events.WaitAsync<BridgeEvent.LogLine>(l => l.Message.Contains("Logged", StringComparison.Ordinal));
        await events.WaitAsync<BridgeEvent.GroupChanged>(g => g.Group.Name == "Logged");

        using var plain = await client.GetAsync(ApiRoutes.Events);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
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
}
