using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Cli;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Playback;
using Airplay2.Sender.Tests.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TestReceiver = Airplay2.FakeReceiver.FakeReceiver;

namespace Airplay2.Sender.Tests.Sender;

/// <summary>
/// The <see cref="AirplaySender"/> facade against in-process fake receivers:
/// a 2 s generated WAV to two fakes through the facade with decoded output equal to the input, the session factory
/// (GET /info, pairing, quirks, address fallback), the PIN flow for the web app, and the transport choice.
/// </summary>
public sealed class AirplaySenderTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_generated_wav_plays_through_the_facade_to_two_fakes_and_both_decode_the_input()
    {
        // 2 s of distinct PCM written as a WAV and read back by the CLI's reader (what `play file.wav` does).
        var original = TestPcm.Sweep(2 * 44100);
        using var wav = new MemoryStream();
        WavReader.Write(wav, original);
        wav.Position = 0;
        var input = ReadAll(WavReader.Open(wav, "sweep"));
        Assert.Equal(original, input);

        await using var a = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var b = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake B" });
        var store = new InMemoryPairingStore();
        await using var sender = NewSender(store);
        var receivers = new[] { SessionTestSupport.Describe(a), SessionTestSupport.Describe(b) };
        var player = await sender.CreateGroupPlayerAsync(receivers, new GroupPlayerOptions { VolumePollInterval = null }, None);
        List<FakeReceiverEvent> eventsA;
        try
        {
            await player.AddReceiverAsync(receivers[0], 15, None);
            await player.AddReceiverAsync(receivers[1], 15, None);
            await player.StartAsync(None);
            var writing = WriteOnThread(player, input);

            var packets = (input.Length / 2 + 351) / 352;
            eventsA = await CollectAudioAsync(a, packets);
            var eventsB = await CollectAudioAsync(b, packets);
            await writing;
            var audioA = eventsA.OfType<FakeReceiverEvent.AudioPacket>().ToList();
            var audioB = eventsB.OfType<FakeReceiverEvent.AudioPacket>().ToList();

            Assert.True(audioA[0].Marker);
            Assert.Equal(input, audioA.SelectMany(p => p.Pcm).Take(input.Length).ToArray());
            Assert.Equal(input, audioB.SelectMany(p => p.Pcm).Take(input.Length).ToArray());
            Assert.Equal(audioA.Select(p => (p.Sequence, p.Timestamp)), audioB.Select(p => (p.Sequence, p.Timestamp)));
        }
        finally
        {
            await player.DisposeAsync();
        }

        // The members were torn down (D7), and the session SETUP carried the sender identity of the facade.
        var setup = eventsA.OfType<FakeReceiverEvent.SessionSetup>().Single();
        Assert.Equal(sender.Options.Identity.DeviceId, setup.Body.GetString("deviceID"));
        Assert.Equal("NTP", setup.Body.GetString("timingProtocol"));
        Assert.Equal(sender.Timing.Port, setup.Body.GetInt64("timingPort"));
        Assert.Contains(eventsA, e => e is FakeReceiverEvent.Paired);
        await FirstAsync<FakeReceiverEvent.Teardown>(a);
    }

    [Fact]
    public async Task ConnectAsync_runs_get_info_and_transient_pairing_and_returns_an_encrypted_session()
    {
        await using var fake = await SessionTestSupport.StartAsync();
        await using var sender = NewSender(new InMemoryPairingStore());
        await using var session = await sender.ConnectAsync(SessionTestSupport.Describe(fake), null, None);

        Assert.Equal(PairingKind.Transient, session.Pairing!.Kind);
        Assert.True(session.Connection.IsEncrypted);
        var requests = await SessionTestSupport.RequestsAsync(fake);
        Assert.Equal(["GET /info", "POST /pair-pin-start", "POST /pair-setup", "POST /pair-setup"], requests.Select(SessionTestSupport.Line).ToArray());
        var info = await session.GetInfoAsync(withQualifier: false, None);
        Assert.NotEmpty(info);
    }

    [Fact]
    public async Task ConnectAsync_falls_back_to_the_next_address_when_the_first_refuses()
    {
        await using var fake = await SessionTestSupport.StartAsync();
        await using var sender = NewSender(new InMemoryPairingStore());
        var described = SessionTestSupport.Describe(fake);
        var receiver = described with { Addresses = [IPAddress.Parse("127.0.0.2"), IPAddress.Loopback] };
        await using var session = await sender.ConnectAsync(receiver, null, None);
        Assert.Equal(IPAddress.Loopback, session.Connection.RemoteEndPoint.Address);
    }

    [Fact]
    public async Task ConnectAsync_retries_a_transport_failure_and_gives_up_after_the_configured_attempts()
    {
        await using var fake = await SessionTestSupport.StartAsync();
        var port = fake.ControlEndpoint.Port;
        await fake.DisposeAsync(); // nothing listens there now
        var receiver = SessionTestSupport.Describe(fake) with { Port = port, Addresses = [IPAddress.Loopback] };
        await using var sender = NewSender(new InMemoryPairingStore(), new AirplaySenderOptions { ConnectAttempts = 2, ConnectRetryDelay = TimeSpan.FromMilliseconds(50), LocalAddress = IPAddress.Loopback });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(() => sender.ConnectAsync(receiver, null, None));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(50), "the second attempt comes after the retry delay");
    }

    [Fact]
    public async Task Pin_pairing_for_the_web_app_stores_verified_credentials_that_the_next_connect_uses()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { StatusFlags = 0x404 | 0x200, AllowTransientPairing = false, Pin = "4711" });
        var store = new InMemoryPairingStore();
        await using var sender = NewSender(store);
        var receiver = SessionTestSupport.Describe(fake);
        Assert.Equal(PairingRequirement.Pin, receiver.Capabilities.Pairing);

        // Without a prompt the session factory reports that a PIN is needed (the bridge emits PairingRequired).
        var refused = await Assert.ThrowsAsync<PairingException>(() => sender.ConnectAsync(receiver, null, None));
        Assert.Equal(PairingFailure.TransientRefused, refused.Failure);

        var started = await sender.BeginPinPairingAsync(receiver, None);
        Assert.Equal(1, sender.PendingPinPairings);
        var credentials = await sender.CompletePinPairingAsync(started, "4711", None);
        Assert.Equal(0, sender.PendingPinPairings);
        Assert.Same(credentials, store.Pairings[receiver.Id]);

        await using var session = await sender.ConnectAsync(receiver, null, None);
        Assert.Equal(PairingKind.Verified, session.Pairing!.Kind);
    }

    [Fact]
    public async Task A_wrong_pin_closes_the_pairing_and_the_state_cannot_be_reused()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { StatusFlags = 0x404 | 0x200, AllowTransientPairing = false, Pin = "4711" });
        var store = new InMemoryPairingStore();
        await using var sender = NewSender(store);
        var receiver = SessionTestSupport.Describe(fake);
        var started = await sender.BeginPinPairingAsync(receiver, None);
        var wrong = await Assert.ThrowsAsync<PairingException>(() => sender.CompletePinPairingAsync(started, "0000", None));
        Assert.Equal(PairingFailure.Authentication, wrong.Failure);
        Assert.Empty(store.Pairings);
        Assert.Equal(0, sender.PendingPinPairings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.CompletePinPairingAsync(started, "4711", None));
    }

    [Fact]
    public async Task An_abandoned_pin_pairing_expires_and_closes_its_connection()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { StatusFlags = 0x404 | 0x200, AllowTransientPairing = false });
        await using var sender = NewSender(new InMemoryPairingStore(), new AirplaySenderOptions { PinPairingTimeout = TimeSpan.FromMilliseconds(200), LocalAddress = IPAddress.Loopback });
        var started = await sender.BeginPinPairingAsync(SessionTestSupport.Describe(fake), None);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (sender.PendingPinPairings > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, sender.PendingPinPairings);
        Assert.False(string.IsNullOrEmpty(await started.Connection.Closed.WaitAsync(TimeSpan.FromSeconds(5))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.CompletePinPairingAsync(started, "1234", None));
    }

    [Fact]
    public async Task Quirks_overlay_keeps_the_known_timing_protocol_unless_configured()
    {
        var speakerA = Receiver("Test Speaker ABCD", "02:1A:2B:3C:4D:5E", srcvers: "366.0"); // known quirks are keyed on srcvers
        var shairport = Receiver("ShairportSync", "11:22:33:44:55:66");
        var other = Receiver("Something", "AA:00:00:00:00:01");
        await using var sender = NewSender(new InMemoryPairingStore(), new AirplaySenderOptions
        {
            Quirks = new Dictionary<string, ReceiverQuirks>
            {
                ["021a2b3c4d5e"] = new() { OffsetFrames = 100 },
                ["AA:00:00:00:00:01"] = new() { TimingProtocol = TimingProtocol.Ptp },
            },
        });

        var q = sender.QuirksFor(speakerA);
        Assert.Equal(100, q.OffsetFrames);
        Assert.Equal(TimingProtocol.Ntp, q.TimingProtocol);
        Assert.Equal(TimingProtocol.Ptp, sender.TimingProtocolFor(shairport));
        Assert.Equal(TimingProtocol.Ptp, sender.TimingProtocolFor(other));

        Assert.Equal((StreamType.Realtime, TimingProtocol.Ntp), sender.ChooseTransport([speakerA]));
        Assert.Equal((StreamType.Realtime, TimingProtocol.Ptp), sender.ChooseTransport([speakerA, shairport]));
        Assert.Equal((StreamType.Realtime, TimingProtocol.Ntp), sender.ChooseTransport([]));
    }

    [Fact]
    public async Task Buffered_is_chosen_only_when_preferred_and_every_receiver_has_bit_40()
    {
        var withBit40 = Receiver("A", "AA:00:00:00:00:01", "features=0x445F8A00,0x1C340");
        var without = Receiver("B", "AA:00:00:00:00:02", "features=0x445F8A00,0x0");
        await using var sender = NewSender(new InMemoryPairingStore(), new AirplaySenderOptions { PreferredStream = StreamType.Buffered });
        Assert.Equal((StreamType.Buffered, TimingProtocol.Ptp), sender.ChooseTransport([withBit40]));
        Assert.Equal((StreamType.Realtime, TimingProtocol.Ntp), sender.ChooseTransport([withBit40, without]));
    }

    [Fact]
    public async Task A_buffered_player_wraps_its_sessions_and_shares_the_players_L_with_the_transport()
    {
        // SPEC-audio.md 4.3: the anchors and the player's "frame at the speaker" use one L; the transport's session
        // decorator (BufferedStreamTransport.CreateGroupPlayer) is what sends SETRATEANCHORTIME and FLUSHBUFFERED.
        var withBit40 = Receiver("A", "AA:00:00:00:00:01", "features=0x445F8A00,0x1C340");
        await using var sender = NewSender(new InMemoryPairingStore(), new AirplaySenderOptions
        {
            PreferredStream = StreamType.Buffered,
            LocalAddress = IPAddress.Loopback,
            Ptp = new PtpMasterOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 },
        });
        await using var player = await sender.CreateGroupPlayerAsync([withBit40], new GroupPlayerOptions { LatencyFrames = 66150 }, None);
        var group = Assert.IsType<GroupPlayer>(player);
        var transport = Assert.IsType<BufferedStreamTransport>(group.Transport);
        Assert.Equal(66150, transport.LatencyFrames);
        Assert.Equal(66150, group.Options.LatencyFrames);
    }

    [Fact]
    public void DeriveIdentity_is_stable_per_controller_and_a_locally_administered_unicast_mac()
    {
        var controller = ControllerIdentity.Generate();
        var first = AirplaySender.DeriveIdentity("x", controller);
        var second = AirplaySender.DeriveIdentity("x", controller with { });
        var other = AirplaySender.DeriveIdentity("x", ControllerIdentity.Generate());
        Assert.Equal(first, second);
        Assert.NotEqual(first.DeviceId, other.DeviceId);
        Assert.Matches("^([0-9A-F]{2}:){5}[0-9A-F]{2}$", first.DeviceId);
        var b0 = Convert.ToByte(first.DeviceId[..2], 16);
        Assert.Equal(0x02, b0 & 0x03);
    }

    [Fact]
    public async Task Players_left_alive_are_torn_down_when_the_sender_is_disposed()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var sender = NewSender(new InMemoryPairingStore());
        var player = await sender.CreateGroupPlayerAsync(null, None);
        await player.AddReceiverAsync(SessionTestSupport.Describe(fake), 10, None);
        await sender.DisposeAsync();
        var teardown = await FirstAsync<FakeReceiverEvent.Teardown>(fake);
        Assert.NotNull(teardown);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sender.CreateGroupPlayerAsync(null, None));
    }

    [Fact]
    public async Task A_pin_pairing_whose_pair_verify_does_not_succeed_keeps_no_credentials()
    {
        // SPEC-control.md 3.9: any pair-verify failure drops the stored pairing, not only a rejection (here: interrupted).
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { StatusFlags = 0x404 | 0x200, AllowTransientPairing = false, Pin = "4711" });
        using var cts = new CancellationTokenSource();
        var store = new CancelOnSaveStore(cts);
        await using var sender = NewSender(store);
        var started = await sender.BeginPinPairingAsync(SessionTestSupport.Describe(fake), None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.CompletePinPairingAsync(started, "4711", cts.Token));
        Assert.Empty(store.Inner.Pairings);
        Assert.Equal(1, store.Inner.Deletes);
    }

    internal static AirplaySender NewSender(IPairingStore store, AirplaySenderOptions? options = null) =>
        new(options ?? new AirplaySenderOptions { LocalAddress = IPAddress.Loopback }, store, NullLoggerFactory.Instance);

    /// <summary>Reads the fake's events until <paramref name="packets"/> audio packets arrived; returns every event read.</summary>
    internal static async Task<List<FakeReceiverEvent>> CollectAudioAsync(TestReceiver fake, int packets, TimeSpan? timeout = null)
    {
        var list = new List<FakeReceiverEvent>();
        var audio = 0;
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        while (audio < packets)
        {
            var e = await fake.Events.ReadAsync(cts.Token);
            Assert.False(e is FakeReceiverEvent.BadAudioPacket, $"bad audio packet: {e}");
            list.Add(e);
            if (e is FakeReceiverEvent.AudioPacket)
            {
                audio++;
            }
        }

        return list;
    }

    /// <summary>The next event of type <typeparamref name="T"/> the fake publishes.</summary>
    internal static async Task<T> FirstAsync<T>(TestReceiver fake, TimeSpan? timeout = null)
        where T : FakeReceiverEvent
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            if (await fake.Events.ReadAsync(cts.Token) is T found)
            {
                return found;
            }
        }
    }

    private static Task WriteOnThread(IGroupPlayer player, short[] pcm)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                player.Write(pcm);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        { IsBackground = true };
        thread.Start();
        return done.Task;
    }

    private static short[] ReadAll(IPcmSource source)
    {
        using (source)
        {
            var all = new List<short>();
            var buffer = new short[1001 * 2];
            int frames;
            while ((frames = source.Read(buffer)) > 0)
            {
                all.AddRange(buffer.AsSpan(0, frames * 2));
            }

            return [.. all];
        }
    }

    private static DiscoveredReceiver Receiver(string model, string id, string features = "features=0x445F8A00,0x1C340", string? srcvers = null) =>
        new(id, model, [IPAddress.Loopback], 7000, AirplayTxtRecord.Parse(["model=" + model, "deviceid=" + id, features, "flags=0x404", .. srcvers is null ? Array.Empty<string>() : ["srcvers=" + srcvers]]), null, DateTimeOffset.UtcNow);

    /// <summary>Saves, then cancels <paramref name="cts"/>: the pair-verify after the save sees a cancelled token.</summary>
    private sealed class CancelOnSaveStore(CancellationTokenSource cts) : IPairingStore
    {
        public InMemoryPairingStore Inner { get; } = new();

        public Task<ControllerIdentity> GetControllerIdentityAsync(CancellationToken cancellationToken) => Inner.GetControllerIdentityAsync(cancellationToken);

        public Task<PairingCredentials?> GetAsync(string receiverId, CancellationToken cancellationToken) => Inner.GetAsync(receiverId, cancellationToken);

        public async Task SaveAsync(string receiverId, PairingCredentials credentials, CancellationToken cancellationToken)
        {
            await Inner.SaveAsync(receiverId, credentials, cancellationToken);
            await cts.CancelAsync();
        }

        public Task DeleteAsync(string receiverId, CancellationToken cancellationToken) => Inner.DeleteAsync(receiverId, cancellationToken);
    }
}
