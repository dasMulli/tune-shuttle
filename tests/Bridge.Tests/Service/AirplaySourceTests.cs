using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Airplay2.Receiver;
using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Bridge.Contracts;
using Bridge.Service;
using Bridge.Service.Airplay;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Bridge.Service.Spotify;
using Bridge.Service.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using ReceiverEvent = Airplay2.Receiver.ReceiverEvent;

namespace Bridge.Tests.Service;

/// <summary>
/// <c>AirplaySource</c> (SPEC-receiver.md 8.3, 11.1 "Bridge") against a stub player, output manager and switch: writes gated by
/// ownership, pause/flush/now-playing forwarded with the buffer-derived elapsed time, the level set with source <c>airplay</c>,
/// <c>ReleaseAsync</c> pausing first, the replay of what was dropped before the claim, the playback view; the receiver's
/// lifecycle on loopback with settings changes (3.3) and a stable identity (R11); self-exclusion in <c>ReceiverInventory</c>
/// (R12); and one <c>PtpPort</c> shared by a PTP output and the receiver's slave (6.1, ports 0).
/// </summary>
public sealed class AirplaySourceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly SenderInfo Phone = new("Test Phone", "iPhone0,0", "02:00:00:00:00:10", IPAddress.Parse("192.0.2.20"));

    [Fact]
    public async Task Writes_reach_the_player_only_while_airplay_owns_it_and_started_it()
    {
        var (host, player) = NewHost(AudioSource.Spotify);
        await using var source = NewSource(host);

        await source.WriteAsync(Frames(352), Ct); // Spotify owns the output
        host.Owner = AudioSource.Airplay;
        await source.WriteAsync(Frames(352), Ct); // owned, but nobody started the player: a write would block on its ring
        Assert.Equal(0, player.FramesWritten);
        Assert.Equal(704, source.DroppedFrames);

        await source.ResumeAsync(Ct);
        await source.WriteAsync(Frames(352), Ct);
        Assert.Equal(1, player.Starts);
        Assert.Equal(352, player.FramesWritten);

        player.Queued = 1234;
        Assert.Equal(1234, source.QueuedFrames);
        host.Owner = AudioSource.Spotify;
        Assert.Equal(0, source.QueuedFrames);
        await source.WriteAsync(Frames(352), Ct);
        Assert.Equal(352, player.FramesWritten);
    }

    [Fact]
    public async Task A_new_player_is_started_before_the_first_write_while_playing()
    {
        var (host, first) = NewHost(AudioSource.Airplay);
        await using var source = NewSource(host);
        await source.ResumeAsync(Ct);
        await source.WriteAsync(Frames(352), Ct);

        var second = new StubPlayer(host.Log);
        host.Player = second; // the user selected another output (SPEC-receiver.md 8.3: the player is read per write)
        await source.WriteAsync(Frames(352), Ct);

        Assert.Equal(352, first.FramesWritten);
        Assert.Equal(1, second.Starts);
        Assert.Equal(352, second.FramesWritten);
    }

    [Fact]
    public async Task Pause_and_release_fall_back_to_the_active_player_when_the_started_one_was_disposed()
    {
        // SPEC-receiver.md 8.3: the user switched the output mid-stream; the started player is gone.
        var (host, first) = NewHost(AudioSource.Airplay);
        await using var source = NewSource(host);
        await source.ResumeAsync(Ct);
        var second = new StubPlayer(host.Log);
        host.Player = second;
        first.Disposed = true;

        await source.PauseAsync(Ct);
        Assert.Equal(0, first.Pauses);
        Assert.Equal(1, second.Pauses);

        await source.ResumeAsync(Ct); // starts the second player
        var third = new StubPlayer(host.Log);
        host.Player = third;
        second.Disposed = true;
        await source.ReleaseAsync(Ct);
        Assert.Equal(1, third.Pauses);
    }

    [Fact]
    public async Task Commands_are_forwarded_with_the_buffer_elapsed_time_and_the_airplay_level_source()
    {
        var (host, player) = NewHost(AudioSource.Airplay);
        await using var source = NewSource(host);
        var metadata = new TrackMetadata("Title", "Artist", "Album", TimeSpan.FromMinutes(3));

        await source.ResumeAsync(Ct);
        await source.FlushAsync(Ct);
        await source.SetMasterLevelAsync(37, Ct);
        await source.SetNowPlayingAsync(metadata, new byte[] { 1, 2, 3 }, "image/png", TimeSpan.FromSeconds(12.5), Ct);
        await source.PauseAsync(Ct);

        Assert.Equal(1, host.Output.Flushes);
        Assert.Equal([(37, OutputManager.SourceAirplay)], host.Output.Levels);
        var nowPlaying = Assert.Single(host.Output.NowPlayings);
        Assert.Equal(metadata, nowPlaying.Metadata);
        Assert.Equal(TimeSpan.FromSeconds(12.5), nowPlaying.Elapsed); // the receiver's buffer-derived value, unchanged
        Assert.Equal("image/png", nowPlaying.ArtworkContentType);
        Assert.Equal(1, player.Starts);
        Assert.Equal(1, player.Pauses);
        Assert.Equal(0, player.Flushes); // flush goes through the output manager, not to the player directly
    }

    [Fact]
    public async Task Commands_of_a_non_owner_are_dropped_and_replayed_once_airplay_owns_the_output()
    {
        var (host, player) = NewHost(AudioSource.Spotify);
        var time = new ManualTime();
        await using var source = NewSource(host, time);

        await source.ResumeAsync(Ct);
        await source.FlushAsync(Ct);
        await source.SetMasterLevelAsync(20, Ct);
        await source.SetNowPlayingAsync(new TrackMetadata("T", "A", "B", TimeSpan.FromMinutes(4)), null, null, TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(0, player.Starts);
        Assert.Equal(0, host.Output.Flushes);
        Assert.Empty(host.Output.Levels);
        Assert.Empty(host.Output.NowPlayings);
        Assert.True(host.Dropped >= 4);

        time.Advance(TimeSpan.FromSeconds(2));
        await source.HandleEventAsync(new ReceiverEvent.SenderStreamStarted(Phone, StreamType.Buffered, "AAC-LC/44100/2"), null, Ct);

        Assert.Equal(AudioSource.Airplay, host.Owner);
        Assert.Equal([Phone], host.Claims);
        Assert.Equal(1, player.Starts);
        Assert.Equal([(20, OutputManager.SourceAirplay)], host.Output.Levels);
        Assert.Equal(TimeSpan.FromSeconds(32), Assert.Single(host.Output.NowPlayings).Elapsed); // advanced while it was dropped
        Assert.Equal(0, host.Output.Flushes); // a flush is not replayed
        Assert.Equal(StreamType.Buffered, source.StreamType);
        Assert.Equal("AAC-LC/44100/2", source.Format);

        await source.ResumeIfPlayingAsync(Ct); // the switch's replay hook after ours: nothing twice
        Assert.Equal(1, player.Starts);
        Assert.Single(host.Output.Levels);
    }

    [Fact]
    public async Task Release_pauses_the_player_before_the_output_is_unowned()
    {
        var (host, player) = NewHost(AudioSource.Airplay);
        await using var source = NewSource(host);
        await source.ResumeAsync(Ct);
        await source.WriteAsync(Frames(352), Ct);

        await source.ReleaseAsync(Ct);

        Assert.Equal(["start", "pause", "release"], host.Log.Where(l => l is "start" or "pause" or "release"));
        Assert.Equal(AudioSource.None, host.Owner);
        await source.WriteAsync(Frames(352), Ct);
        Assert.Equal(352, player.FramesWritten);
    }

    [Fact]
    public async Task End_session_without_a_receiver_still_pauses_and_releases()
    {
        var (host, player) = NewHost(AudioSource.Airplay);
        await using var source = NewSource(host);
        await source.ResumeAsync(Ct);

        await source.EndSessionAsync("replaced by Spotify", Ct);

        Assert.Equal(1, player.Pauses);
        Assert.Equal(AudioSource.None, host.Owner);
    }

    [Fact]
    public async Task Playback_view_follows_now_playing_and_the_sender_with_the_airplay_source()
    {
        var (host, _) = NewHost(AudioSource.Airplay);
        var time = new ManualTime();
        await using var source = NewSource(host, time);
        var artwork = new byte[] { 0xFF, 0xD8, 0xFF };

        await source.HandleEventAsync(new ReceiverEvent.SenderResumed(), null, Ct);
        await source.SetNowPlayingAsync(new TrackMetadata("Song", "Band", "Record", TimeSpan.FromSeconds(200)), artwork, "image/jpeg", TimeSpan.FromSeconds(10), Ct);
        await source.HandleEventAsync(new ReceiverEvent.SenderArtwork(artwork, "image/jpeg", 4242), null, Ct);
        time.Advance(TimeSpan.FromSeconds(5));

        var playing = source.Playback;
        Assert.True(playing.Playing);
        Assert.Equal(("Song", "Band", "Record"), (playing.Title, playing.Artist, playing.Album));
        Assert.Equal(15_000, playing.PositionMs); // elapsed plus the time since, while playing
        Assert.Equal(200_000, playing.DurationMs);
        Assert.Equal("/api/playback/artwork?v=4242", playing.ArtworkUrl);
        Assert.Equal(OutputManager.SourceAirplay, playing.Source);
        Assert.Equal(4242u, source.Artwork!.Key);
        Assert.Equal(artwork, host.ArtworkBytes);
        Assert.All(host.Published, p => Assert.Equal(OutputManager.SourceAirplay, p.Source));

        await source.HandleEventAsync(new ReceiverEvent.SenderPaused(), null, Ct);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(source.Playback.Playing);
        Assert.Equal(15_000, source.Playback.PositionMs);

        time.Advance(TimeSpan.FromSeconds(1000));
        await source.HandleEventAsync(new ReceiverEvent.SenderResumed(), null, Ct);
        time.Advance(TimeSpan.FromSeconds(1000));
        Assert.Equal(200_000, source.Playback.PositionMs); // never past the duration

        await source.HandleEventAsync(new ReceiverEvent.SenderGone("TEARDOWN"), null, Ct);
        Assert.Null(source.Artwork);
        Assert.Null(source.Playback.Title);
        Assert.True(host.ArtworkCleared);
        Assert.Equal(AudioSource.None, host.Owner);
    }

    [Fact]
    public async Task An_unchanged_view_is_published_once()
    {
        var (host, _) = NewHost(AudioSource.Airplay);
        await using var source = NewSource(host, new ManualTime());
        var metadata = new TrackMetadata("Song", "Band", "Record", TimeSpan.FromSeconds(200));

        // A paused sender repeating its progress keep-alive: the coalesced calls carry the same track and position.
        await source.SetNowPlayingAsync(metadata, null, null, TimeSpan.FromSeconds(10), Ct);
        await source.SetNowPlayingAsync(metadata, null, null, TimeSpan.FromSeconds(10), Ct);
        await source.HandleEventAsync(new ReceiverEvent.SenderPaused(), null, Ct);
        Assert.Single(host.Published);

        await source.SetNowPlayingAsync(metadata, null, null, TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(2, host.Published.Count);
        Assert.Equal(3, host.Output.NowPlayings.Count); // every call still reaches the receivers
    }

    [Fact]
    public async Task The_view_is_kept_but_not_published_while_another_source_owns_the_output()
    {
        var (host, _) = NewHost(AudioSource.Spotify);
        await using var source = NewSource(host);

        await source.SetNowPlayingAsync(new TrackMetadata("Song", null, null, null), null, null, TimeSpan.Zero, Ct);

        Assert.Empty(host.Published);
        Assert.Equal("Song", host.Stored!.Title);
    }

    [Fact]
    public async Task Receiver_options_come_from_the_settings_and_bridge_options()
    {
        var identity = ReceiverIdentity.Create();
        var settings = new AirplaySettings(true, "Kitchen", "pw", identity);
        var bridge = new BridgeOptions { LocalAddress = "192.0.2.5", Airplay = new AirplayOptions { RtspPort = 7001, FfmpegPath = "ffmpeg-dir", AnchorLeadMs = 250, SessionTimeoutSeconds = 12 } };
        await using var port = new PtpPort(new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 });
        var clock = new MasterClock();
        var controllers = new InMemoryControllerStore();

        var options = AirplaySource.BuildReceiverOptions(settings, bridge, port, clock, controllers, 44100, _ => ValueTask.FromResult<int?>(40));

        Assert.Equal("Kitchen", options.Name);
        Assert.Equal("pw", options.Password);
        Assert.Same(identity, options.Identity);
        Assert.Equal(IPAddress.Parse("192.0.2.5"), options.BindAddress);
        Assert.Equal([IPAddress.Parse("192.0.2.5")], options.AdvertisedAddresses);
        Assert.Equal(7001, options.RtspPort);
        Assert.Equal("ffmpeg-dir", options.FfmpegPath);
        Assert.Same(port, options.PtpPort);
        Assert.Same(clock, options.Clock);
        Assert.Same(controllers, options.ControllerStore);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.Playout.AnchorLead);
        Assert.Equal(TimeSpan.FromSeconds(12), options.SessionTimeout);
        Assert.Equal(44100 + 11025 + 8820, options.Playout.Lpipe); // SPEC-receiver.md 5.4
        Assert.Equal(40, await options.OutputLevel!(Ct));

        var open = AirplaySource.BuildReceiverOptions(settings with { Password = "" }, new BridgeOptions(), null, null, null, 77175, null);
        Assert.Null(open.Password);
        Assert.Null(open.BindAddress);
        Assert.Null(open.AdvertisedAddresses);
    }

    [Fact]
    public async Task Receiver_starts_applies_name_and_password_without_restart_and_stops_on_disable()
    {
        await using var store = await MemoryStoreAsync();
        await using var port = new PtpPort(new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 });
        var (host, _) = NewHost(AudioSource.None, store);
        await using var source = NewSource(host, port: port);
        source.ConfigureReceiver = Loopback;

        await source.StartAsync(Ct);
        await WaitUntilAsync(() => source.Status == ReceiverStatus.Advertising);
        var receiver = source.Receiver!;
        Assert.True(source.RtspPort > 0);
        Assert.Equal("TuneShuttle", source.Name); // the device name by default (R11)
        Assert.NotNull(source.DecoderStatus);

        var settings = await AirplaySettings.LoadAsync(store, new AirplayOptions(), "TuneShuttle", Ct);
        await source.ApplySettingsAsync(settings with { Name = "Kitchen", Password = "secret", Identity = ReceiverIdentity.Create() }, Ct);
        Assert.Same(receiver, source.Receiver); // 3.3: no restart
        Assert.Equal("Kitchen", receiver.Name);
        var stored = await AirplaySettings.LoadAsync(store, new AirplayOptions(), "TuneShuttle", Ct);
        Assert.Equal(("Kitchen", "secret"), (stored.Name, stored.Password));
        Assert.Equal(settings.Identity.DeviceId, stored.Identity.DeviceId); // R11: never rewritten

        await source.ApplySettingsAsync(stored with { Enabled = false }, Ct);
        Assert.Equal(ReceiverStatus.Off, source.Status);
        Assert.Null(source.Receiver);

        await source.ApplySettingsAsync(stored with { Enabled = true }, Ct);
        Assert.Equal(ReceiverStatus.Advertising, source.Status);
        Assert.NotSame(receiver, source.Receiver);
        Assert.True(host.StatusPublications >= 3);

        await source.StopAsync(Ct);
        Assert.Equal(ReceiverStatus.Off, source.Status);
        Assert.False(port.IsBound); // no session, so the slave never bound the shared port
    }

    [Fact]
    public async Task Identity_is_created_once_and_stable_across_restarts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tuneshuttle-airplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "bridge.db");
            string first;
            await using (var store = new SqliteBridgeStore(path, NullLogger<SqliteBridgeStore>.Instance))
            {
                await store.InitializeAsync(Ct);
                first = await RunOnceAsync(store);
            }

            await using (var store = new SqliteBridgeStore(path, NullLogger<SqliteBridgeStore>.Instance))
            {
                await store.InitializeAsync(Ct);
                Assert.Equal(first, await RunOnceAsync(store));
            }
        }
        finally
        {
            BridgeFactory.DeleteDirectory(directory);
        }

        static async Task<string> RunOnceAsync(IBridgeStore store)
        {
            var (host, _) = NewHost(AudioSource.None, store);
            await using var source = NewSource(host);
            source.ConfigureReceiver = Loopback;
            await source.StartAsync(Ct);
            await WaitUntilAsync(() => source.Status == ReceiverStatus.Advertising);
            var id = source.Receiver!.Options.Identity.DeviceId;
            await source.StopAsync(Ct);
            return id;
        }
    }

    [Fact]
    public async Task A_failed_start_is_reported_as_error()
    {
        using var taken = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var busy = ((IPEndPoint)taken.LocalEndpoint).Port;
        var (host, _) = NewHost(AudioSource.None);
        await using var source = NewSource(host);
        source.ConfigureReceiver = o => Loopback(o) with { RtspPort = busy };

        await source.StartAsync(Ct);
        await WaitUntilAsync(() => source.Status == ReceiverStatus.Error);
        Assert.Null(source.Receiver);
    }

    [Fact]
    public async Task Receiver_inventory_ignores_our_own_advertisement()
    {
        await using var factory = new BridgeFactory();
        _ = factory.CreateClient();
        var store = factory.Get<IBridgeStore>();
        string? json = null;
        await WaitUntilAsync(async () => (json = await store.GetSettingAsync(BridgeSettingKeys.AirplayIdentity, Ct)) is not null);
        Assert.True(AirplaySettings.TryParseIdentity(json, out var self));

        var byId = Describe(self.DeviceId, "Ourselves", ["deviceid=" + self.DeviceId, "features=0x405FCA00,0x18340"]);
        var byKey = Describe("02:00:00:00:00:B1", "Ourselves again", ["deviceid=02:00:00:00:00:B1", "pk=" + self.PublicKeyHex, "features=0x405FCA00,0x18340"]);
        var other = Describe("02:00:00:00:00:A1", "Other speaker", ["deviceid=02:00:00:00:00:A1", "pk=" + new string('a', 64), "features=0x405FCA00,0x18340"]);
        Assert.True(ReceiverInventory.IsOwnAdvertisement(byId, self));
        Assert.True(ReceiverInventory.IsOwnAdvertisement(byKey, self));
        Assert.False(ReceiverInventory.IsOwnAdvertisement(other, self));
        Assert.False(ReceiverInventory.IsOwnAdvertisement(byId, null));

        factory.Browser.Add(byId);
        factory.Browser.Add(byKey);
        factory.Browser.Add(other);
        await WaitUntilAsync(async () => await store.GetReceiverAsync(other.Id, Ct) is not null);

        Assert.Null(await store.GetReceiverAsync(byId.Id, Ct));
        Assert.Null(await store.GetReceiverAsync(byKey.Id, Ct));
        var inventory = factory.Get<ReceiverInventory>();
        Assert.Null(await inventory.ResolveAsync(byId.Id, Ct));
        Assert.NotNull(await inventory.ResolveAsync(other.Id, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => inventory.RequireRowAsync(byKey.Id, Ct));

        // Nor does it reach the web app (R12): GET /receivers, the receiver lookup and the status count skip it.
        var dtos = factory.Get<DtoFactory>();
        var listed = await dtos.ReceiversAsync(Ct);
        Assert.Contains(listed, r => r.Id == other.Id);
        Assert.DoesNotContain(listed, r => r.Id == byId.Id || r.Id == byKey.Id);
        Assert.Null(await dtos.ReceiverAsync(byId.Id, Ct));
        Assert.Equal(factory.Browser.Snapshot().Count - 2, (await dtos.StatusAsync(Ct)).Receivers);
    }

    [Fact]
    public async Task One_ptp_port_serves_a_ptp_output_and_the_receiver_in_one_process()
    {
        await using var factory = new BridgeFactory();
        _ = factory.CreateClient();
        var port = factory.Get<IPtpPort>();
        var source = factory.Get<AirplaySource>();
        Assert.Same(port, source.PtpPort);
        var sender = await factory.Get<AirplaySenderProvider>().GetAsync(Ct);
        Assert.Same(port, sender.Options.PtpPort); // the output's PTP transports use the same port (SPEC-receiver.md 6.1)

        // The grandmaster of a PTP output and the receiver's slave on the one port (ports 0, both peers on 127.0.0.1).
        await using var master = new PtpMaster(sender.Clock, port, NullLogger<PtpMaster>.Instance);
        await master.StartAsync(Ct);
        Assert.True(port.IsBound);
        await using var slave = new PtpSlave(new PtpSlaveOptions
        {
            Port = port,
            SenderAddresses = [IPAddress.Loopback],
            PeerEventPort = port.EventPort,
            PeerGeneralPort = port.GeneralPort,
        });
        await slave.StartAsync(Ct);

        Assert.NotEqual(0, port.EventPort);
        Assert.Equal(port.EventPort, master.EventPort);
        Assert.Equal(port.EventPort, slave.EventPort);
        Assert.Same(port, slave.Port);
    }

    // ---- support ------------------------------------------------------------------------------------------------

    private static AirplayReceiverOptions Loopback(AirplayReceiverOptions options) =>
        options with { BindAddress = IPAddress.Loopback, RtspPort = 0, Advertise = false, StatisticsInterval = TimeSpan.Zero };

    private static (StubHost Host, StubPlayer Player) NewHost(AudioSource owner, IBridgeStore? store = null)
    {
        var host = new StubHost(store) { Owner = owner };
        var player = new StubPlayer(host.Log);
        host.Player = player;
        return (host, player);
    }

    private static AirplaySource NewSource(StubHost host, TimeProvider? time = null, IPtpPort? port = null) =>
        new(Options.Create(new BridgeOptions { Airplay = new AirplayOptions { RtspPort = 0, PtpEventPort = 0, PtpGeneralPort = 0 } }),
            port ?? new PtpPort(new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 }),
            host,
            NullLoggerFactory.Instance,
            time);

    private static async Task<SqliteBridgeStore> MemoryStoreAsync()
    {
        var store = new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance);
        await store.InitializeAsync(Ct);
        return store;
    }

    private static ReadOnlyMemory<short> Frames(int frames) => new short[frames * 2];

    private static DiscoveredReceiver Describe(string id, string name, string[] txt) =>
        new(id, name, [IPAddress.Parse("192.0.2.30")], 7000, AirplayTxtRecord.Parse(txt), null, DateTimeOffset.UtcNow);

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(20);
        }
    }

    /// <summary>A controllable monotonic time source.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class StubHost(IBridgeStore? store) : IAirplaySourceHost
    {
        public ConcurrentQueue<string> Log { get; } = new();

        public AudioSource Owner { get; set; }

        public bool MayWrite => Owner == AudioSource.Airplay;

        public int Dropped;

        public IGroupPlayer? Player { get; set; }

        public RecordingOutput Output { get; } = new();

        IPlaybackOutput IAirplaySourceHost.Output => Output;

        public List<SenderInfo> Claims { get; } = [];

        public List<PlaybackDto> Published { get; } = [];

        public PlaybackDto? Stored { get; private set; }

        public byte[]? ArtworkBytes { get; private set; }

        public bool ArtworkCleared { get; private set; }

        public int StatusPublications;

        public IControllerStore Controllers { get; } = new InMemoryControllerStore();

        public int SpotifyVolume => 40;

        public bool Admit(string command)
        {
            if (Owner is AudioSource.Airplay or AudioSource.None)
            {
                return true;
            }

            Interlocked.Increment(ref Dropped);
            return false;
        }

        public bool AdmitStart() => Admit("start");

        public Task ClaimAsync(SenderInfo sender, CancellationToken cancellationToken)
        {
            Claims.Add(sender);
            Owner = AudioSource.Airplay;
            return Task.CompletedTask;
        }

        public void Release()
        {
            Log.Enqueue("release");
            if (Owner == AudioSource.Airplay)
            {
                Owner = AudioSource.None;
            }
        }

        public Task<AirplaySettings> LoadSettingsAsync(CancellationToken cancellationToken) =>
            store is null
                ? Task.FromResult(new AirplaySettings(true, "TuneShuttle", null, ReceiverIdentity.Create()))
                : AirplaySettings.LoadAsync(store, new AirplayOptions(), "TuneShuttle", cancellationToken);

        public Task SaveSettingsAsync(AirplaySettings settings, CancellationToken cancellationToken) =>
            store is null ? Task.CompletedTask : settings.SaveAsync(store, cancellationToken);

        public Task<int> LatencyFramesAsync(CancellationToken cancellationToken) => Task.FromResult(77175);

        public Task<IMasterClock?> ClockAsync(CancellationToken cancellationToken) => Task.FromResult<IMasterClock?>(null);

        public PlaybackDto BuildPlayback(TrackMetadata? metadata, bool playing, TimeSpan elapsed, ReadOnlyMemory<byte>? artwork, string? artworkContentType, uint? artworkKey, int level)
        {
            if (artwork is { } bytes)
            {
                Assert.Null(ArtworkBytes); // handed over once per key
                ArtworkBytes = bytes.ToArray();
            }

            return Mapping.ToAirplayPlaybackDto(metadata, playing, elapsed, artworkKey, level);
        }

        public void ClearArtwork() => ArtworkCleared = true;

        public void PublishPlayback(PlaybackDto playback)
        {
            Stored = playback;
            if (Owner == AudioSource.Airplay)
            {
                Published.Add(playback);
            }
        }

        public Task PublishStatusAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StatusPublications);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutput : IPlaybackOutput
    {
        private readonly Channel<string> _remote = Channel.CreateUnbounded<string>();
        private readonly Channel<int> _volume = Channel.CreateUnbounded<int>();

        public int Flushes { get; private set; }

        public List<(int Level, string Source)> Levels { get; } = [];

        public List<NowPlaying> NowPlayings { get; } = [];

        public long FramesWritten => 0;

        public ChannelReader<string> RemoteCommands => _remote.Reader;

        public ChannelReader<int> SpotifyVolumeUpdates => _volume.Reader;

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }

        public Task SetNowPlayingAsync(NowPlaying nowPlaying, CancellationToken cancellationToken)
        {
            NowPlayings.Add(nowPlaying);
            return Task.CompletedTask;
        }

        public Task SetSourceLevelAsync(int level, string source, CancellationToken cancellationToken)
        {
            Levels.Add((level, source));
            return Task.CompletedTask;
        }

        public Task<int?> CurrentLevelAsync(CancellationToken cancellationToken) => Task.FromResult<int?>(50);
    }

    private sealed class StubPlayer(ConcurrentQueue<string> log) : IGroupPlayer
    {
        private readonly Channel<GroupPlayerEvent> _events = Channel.CreateUnbounded<GroupPlayerEvent>();

        public int Starts { get; private set; }

        public int Pauses { get; private set; }

        public int Flushes { get; private set; }

        public long FramesWritten { get; private set; }

        public int Queued { get; set; }

        public ChannelReader<GroupPlayerEvent> Events => _events.Reader;

        public GroupVolumeModel Volume { get; } = new();

        public IReadOnlyDictionary<string, GroupMemberState> Members { get; } = new Dictionary<string, GroupMemberState>();

        public int QueuedFrames => Queued;

        public Task AddReceiverAsync(DiscoveredReceiver receiver, int level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveReceiverAsync(string receiverId, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Write(ReadOnlySpan<short> interleavedPcm) => FramesWritten += interleavedPcm.Length / 2;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            log.Enqueue("start");
            return Task.CompletedTask;
        }

        public bool Disposed { get; set; }

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Pauses++;
            log.Enqueue("pause");
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }

        public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetReceiverLevelAsync(string receiverId, int level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
