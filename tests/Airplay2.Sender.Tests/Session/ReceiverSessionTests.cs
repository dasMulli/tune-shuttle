using System.Net;
using System.Net.Sockets;
using System.Text;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Airplay2.Sender.Tests.Session.SessionTestSupport;
using Harness = Airplay2.Sender.Tests.Fake.FakeReceiverHarness;

namespace Airplay2.Sender.Tests.Session;

/// <summary><see cref="ReceiverSession"/> against the in-process fake receiver (WORK-PLAN.md session-streams "Verify").</summary>
public sealed class ReceiverSessionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static FakeReceiverOptions Quiet(FakeReceiverOptions? options = null) => (options ?? new FakeReceiverOptions()) with { SendTimingRequests = false };

    [Fact]
    public async Task D5_startup_sends_the_exact_request_sequence_and_bodies()
    {
        await using var fake = await StartAsync(Quiet());
        await using var session = await ConnectAsync(fake);
        var info = await session.GetInfoAsync(withQualifier: false, None);
        Assert.Equal("Fake Receiver", info.GetString("name"));
        var pairing = await session.PairAsync(new InMemoryPairingStore(), pinPrompt: null, None);
        Assert.Equal(PairingKind.Transient, pairing.Kind);
        Assert.True(session.Connection.IsEncrypted);

        var setup = await session.SetupSessionAsync(NtpSession(timingPort: 6002), None);
        Assert.True(setup.EventPort > 0);
        Assert.Equal(0, setup.TimingPort);
        var channel = await session.ConnectEventChannelAsync(None);
        Assert.Equal(0, await session.RecordAsync(None));
        var stream = await session.SetupStreamAsync(Stream(session, controlPort: 6001), None);
        Assert.Equal(fake.DataPort, stream.DataPort);
        Assert.Equal(fake.ControlPort, stream.ControlPort);
        Assert.NotNull(stream.StreamId);
        await session.SetVolumeAsync(-15.0, None);
        await session.SetMetadataAsync(new TrackMetadata("Title", "Artist", "Album", TimeSpan.FromSeconds(180)), 77175, None);
        await session.SetArtworkAsync(SessionProbes.SampleJpeg(), RtspContentTypes.Jpeg, 77175, None);
        await session.SetProgressAsync(new ProgressInfo(61815, 77175, 77175 + 180 * 44100, RtpTime: 77175), None);
        var result = await session.TeardownWithResultAsync(None);
        Assert.Single(result.StreamResponses);
        Assert.True(result.ConnectionClose);

        var requests = await RequestsAsync(fake);
        Assert.Equal(
            ["GET /info", "POST /pair-pin-start", "POST /pair-setup", "POST /pair-setup", "SETUP", "RECORD", "SETUP", "SET_PARAMETER", "SET_PARAMETER", "SET_PARAMETER", "SET_PARAMETER", "TEARDOWN", "TEARDOWN"],
            requests.Select(Line).ToArray());
        Assert.All(requests.Take(4), r => Assert.False(r.Encrypted));
        Assert.All(requests.Skip(4), r => Assert.True(r.Encrypted));
        Assert.All(requests.Skip(4), r => Assert.Equal(session.Connection.SessionUri, r.Uri));

        var sessionBody = BinaryPlist.DecodeDictionary(requests[4].Body);
        Assert.False(sessionBody.ContainsKey("streams"));
        Assert.Equal("NTP", sessionBody.GetString("timingProtocol"));
        Assert.Equal(6002, sessionBody.GetInt64("timingPort"));
        Assert.Empty(requests[5].Body);
        var streamBody = (PlistDictionary)BinaryPlist.DecodeDictionary(requests[6].Body).GetArray("streams")![0]!;
        Assert.Equal(pairing.SharedSecret.AsSpan(0, 32).ToArray(), streamBody.GetData("shk"));
        Assert.Equal(6001, streamBody.GetInt64("controlPort"));
        Assert.Equal(session.Connection.SessionId, (uint)streamBody.GetInt64("streamConnectionID")!.Value);
        Assert.Equal("volume: -15.000000\r\n", Encoding.ASCII.GetString(requests[7].Body));
        Assert.Equal(RtspContentTypes.DmapTagged, Header(requests[8], "Content-Type"));
        Assert.Equal("rtptime=77175", Header(requests[8], "RTP-Info"));
        Assert.Equal("image/jpeg", Header(requests[9], "Content-Type"));
        Assert.Equal("progress: 61815/77175/8015175\r\n", Encoding.ASCII.GetString(requests[10].Body));
        Assert.Equal("rtptime=77175", Header(requests[10], "RTP-Info"));
        Assert.True(BinaryPlist.DecodeDictionary(requests[11].Body).ContainsKey("streams"));
        Assert.Empty(BinaryPlist.DecodeDictionary(requests[12].Body));

        // The event channel was closed by the TEARDOWN.
        Assert.True(channel.Closed.IsCompleted);
        Assert.Null(session.EventChannel);
    }

    [Fact]
    public async Task Pyatv_order_stream_setup_before_record_is_accepted_too()
    {
        await using var fake = await StartAsync(Quiet());
        await using var session = await ConnectAndPairAsync(fake);
        await Harness.DrainAsync(fake, TimeSpan.FromMilliseconds(200));
        await session.SetupSessionAsync(NtpSession(), None);
        await session.ConnectEventChannelAsync(None);
        await session.SetVolumeAsync(-20.0, None);
        Assert.Equal(-20.0, await session.GetVolumeAsync(None));
        await session.SetupStreamAsync(Stream(session), None);
        await session.RecordAsync(None);
        await session.SetVolumeAsync(-12.0, None);
        await session.TeardownAsync(None);

        var requests = await RequestsAsync(fake);
        Assert.Equal(["SETUP", "SET_PARAMETER", "GET_PARAMETER", "SETUP", "RECORD", "SET_PARAMETER", "TEARDOWN", "TEARDOWN"], requests.Select(Line).ToArray());
        Assert.Equal(-12.0, fake.VolumeDb);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Auth_setup_quirk_sends_33_bytes_after_pairing_and_before_session_setup(bool quirk, bool receiverAnswers)
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { AnswerAuthSetup = receiverAnswers }));
        await using var session = await ConnectAndPairAsync(fake, quirks: new ReceiverQuirks { SendAuthSetup = quirk });
        await session.SetupSessionAsync(NtpSession(), None);
        await session.TeardownAsync(None);

        var requests = await RequestsAsync(fake);
        var lines = requests.Select(Line).ToList();
        if (!quirk)
        {
            Assert.DoesNotContain("POST /auth-setup", lines);
            return;
        }

        // A 404 from a receiver without the endpoint is ignored (SPEC-control.md 3.10: best effort).
        Assert.Equal(["GET /info", "POST /pair-pin-start", "POST /pair-setup", "POST /pair-setup", "POST /auth-setup", "SETUP", "TEARDOWN"], lines);
        var auth = requests[4];
        Assert.True(auth.Encrypted);
        Assert.Equal(33, auth.Body.Length);
        Assert.Equal(0x01, auth.Body[0]);
        Assert.Equal("application/octet-stream", Header(auth, "Content-Type"));
    }

    [Fact]
    public async Task Auth_setup_is_sent_once_per_session_even_with_two_session_setups()
    {
        await using var fake = await StartAsync(Quiet());
        await using var session = await ConnectAndPairAsync(fake, quirks: new ReceiverQuirks { SendAuthSetup = true });
        await session.AuthSetupAsync(None);
        await session.SetupSessionAsync(NtpSession(), None);
        await session.SetupSessionAsync(NtpSession(), None);
        var lines = (await RequestsAsync(fake)).Select(Line).ToList();
        Assert.Single(lines, l => l == "POST /auth-setup");
    }

    [Fact]
    public async Task Event_channel_answers_update_info_on_connect()
    {
        await using var fake = await StartAsync(Quiet());
        await using var session = await ConnectAndPairAsync(fake);
        await session.SetupSessionAsync(NtpSession(), None);
        var channel = await session.ConnectEventChannelAsync(None);
        Assert.True(channel.IsEncrypted);
        var update = Assert.IsType<ReceiverEventMessage.UpdateInfo>(await NextMessageAsync(channel));
        Assert.Equal(-20.0, update.Value.GetDouble("initialVolume"));
        Assert.NotNull(update.Value.GetData("txtAirPlay"));
        var answered = await Harness.NextAsync<FakeReceiverEvent.EventCommandAnswered>(fake);
        Assert.Equal(("updateInfo", 200, false), (answered.Type, answered.StatusCode, answered.LargeFrame));
        Assert.Equal(1, channel.RequestsAnswered);
        Assert.True(channel.LargestFrameReceived <= 1024);
    }

    [Fact]
    public async Task Event_channel_update_info_after_record_like_apple_tv()
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { UpdateInfoOnConnect = false }));
        await using var session = await ConnectAndPairAsync(fake);
        await session.SetupSessionAsync(NtpSession(), None);
        var channel = await session.ConnectEventChannelAsync(None);
        await Task.Delay(300);
        Assert.False(channel.Messages.TryRead(out _));
        Assert.Null(channel.FirstRequestAfter);
        await session.RecordAsync(None);
        Assert.IsType<ReceiverEventMessage.UpdateInfo>(await NextMessageAsync(channel));
        Assert.NotNull(channel.FirstRequestAfter);
    }

    [Fact]
    public async Task Event_channel_accepts_frames_above_1024_bytes()
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { LargeEventFrames = true }));
        await using var session = await ConnectAndPairAsync(fake);
        await session.SetupSessionAsync(NtpSession(), None);
        var channel = await session.ConnectEventChannelAsync(None);
        Assert.IsType<ReceiverEventMessage.UpdateInfo>(await NextMessageAsync(channel));
        var answered = await Harness.NextAsync<FakeReceiverEvent.EventCommandAnswered>(fake);
        Assert.True(answered.LargeFrame);
        Assert.Equal(200, answered.StatusCode);
        Assert.True(channel.LargestFrameReceived > 1024, $"largest frame {channel.LargestFrameReceived}");
    }

    [Fact]
    public async Task Event_channel_parses_remote_commands_and_echoes_cseq()
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { EventRequestsCarryCSeq = true }));
        await using var session = await ConnectAndPairAsync(fake);
        await session.SetupSessionAsync(NtpSession(), None);
        var channel = await session.ConnectEventChannelAsync(None);
        Assert.IsType<ReceiverEventMessage.UpdateInfo>(await NextMessageAsync(channel));
        await fake.SendRemoteCommandAsync("paus", None);
        var command = Assert.IsType<ReceiverEventMessage.MediaRemoteCommand>(await NextMessageAsync(channel));
        Assert.Equal("paus", command.Command);
        await fake.ChangeVolumeAsync(-7.5, None);
        var update = Assert.IsType<ReceiverEventMessage.UpdateInfo>(await NextMessageAsync(channel));
        Assert.Equal(-7.5, update.Value.GetDouble("initialVolume"));
    }

    [Fact]
    public async Task Late_event_answers_make_the_receiver_close_the_channel()
    {
        // Probe P13 on the fake: the fake gives up after 1 s, we answer after 3 s.
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { EventAnswerTimeout = TimeSpan.FromSeconds(1) }));
        var options = new ReceiverSessionOptions { EventChannel = new EventChannelOptions { AnswerDelay = TimeSpan.FromSeconds(3) } };
        await using var session = await ConnectAndPairAsync(fake, options: options);
        await session.SetupSessionAsync(NtpSession(), None);
        var channel = await session.ConnectEventChannelAsync(None);
        var closed = await channel.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(closed);
        var messages = new List<ReceiverEventMessage>();
        await foreach (var message in channel.Messages.ReadAllAsync())
        {
            messages.Add(message);
        }

        Assert.IsType<ReceiverEventMessage.Closed>(messages[^1]);
        var answered = await Harness.NextAsync<FakeReceiverEvent.EventCommandAnswered>(fake);
        Assert.Equal(0, answered.StatusCode);
    }

    [Fact]
    public async Task Event_channel_connect_retries_a_refused_port()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listening = ListenLaterAsync(port, TimeSpan.FromMilliseconds(1200));
        var options = new EventChannelOptions { ConnectAttempts = 5, ConnectRetryDelay = TimeSpan.FromMilliseconds(400) };
        await using (var channel = await EventChannel.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), keys: null, options, NullLogger.Instance, None))
        {
            Assert.False(channel.IsEncrypted);
        }

        using var accepted = await listening;

        var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var closedPort = ((IPEndPoint)refused.LocalEndpoint).Port;
        refused.Stop();
        await Assert.ThrowsAsync<SocketException>(() => EventChannel.ConnectAsync(new IPEndPoint(IPAddress.Loopback, closedPort), null, options with { ConnectAttempts = 1 }, NullLogger.Instance, None));

        static async Task<TcpClient> ListenLaterAsync(int port, TimeSpan delay)
        {
            await Task.Delay(delay);
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            try
            {
                return await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    [Theory]
    [InlineData(GetParameterShape.Shairport)]
    [InlineData(GetParameterShape.Ap2r)]
    public async Task Get_volume_reads_both_reply_shapes(GetParameterShape shape)
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { GetParameterShape = shape }));
        await using var session = await ConnectAndPairAsync(fake);
        await session.SetupSessionAsync(NtpSession(), None);
        Assert.Equal(-20.0, await session.GetVolumeAsync(None));
        await session.SetVolumeAsync(-12.5, None);
        Assert.Equal(-12.5, await session.GetVolumeAsync(None));
        await session.SetVolumeAsync(-144.0, None);
        Assert.Equal(-144.0, await session.TryGetVolumeAsync(None));
        var volumes = (await Harness.DrainAsync(fake, TimeSpan.FromMilliseconds(200))).OfType<FakeReceiverEvent.Volume>().Select(v => v.Db).ToArray();
        Assert.Equal([-12.5, -144.0], volumes);
    }

    [Fact]
    public async Task Feedback_returns_the_streams_body_and_counts_failures()
    {
        await using var fake = await StartAsync(Quiet());
        await using var session = await ConnectAndPairAsync(fake);
        await session.SetupSessionAsync(NtpSession(), None);
        Assert.Null(await session.FeedbackAsync(None));
        await session.SetupStreamAsync(Stream(session), None);
        var body = await session.FeedbackAsync(None);
        var stream = (PlistDictionary)Assert.Single(body!.GetArray("streams")!)!;
        Assert.Equal(96, stream.GetInt64("type"));
        Assert.NotNull(await session.FeedbackAsync(new PlistDictionary { ["streams"] = new List<object?>() }, None));
        Assert.Equal(0, session.ConsecutiveFeedbackFailures);

        await fake.KickAsync(None);
        await session.Connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => session.FeedbackAsync(None));
        await Assert.ThrowsAsync<IOException>(() => session.FeedbackAsync(None));
        Assert.Equal(2, session.ConsecutiveFeedbackFailures);

        // TEARDOWN on a closed connection is not an error.
        var result = await session.TeardownWithResultAsync(None);
        Assert.Null(result.SessionResponse);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Teardown_sends_per_stream_then_session_form(bool receiverReturnsStreamId)
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { ReturnStreamId = receiverReturnsStreamId }));
        await using var session = await ConnectAndPairAsync(fake);
        await session.SetupSessionAsync(NtpSession(), None);
        var channel = await session.ConnectEventChannelAsync(None);
        await session.RecordAsync(None);
        var stream = await session.SetupStreamAsync(Stream(session), None);
        Assert.Equal(receiverReturnsStreamId, stream.StreamId is not null);
        Assert.Equal(receiverReturnsStreamId ? 1 : 0, session.StreamIds.Count);

        var result = await session.TeardownWithResultAsync(None);
        Assert.Equal(receiverReturnsStreamId ? 1 : 0, result.StreamResponses.Count);
        Assert.Equal(200, result.SessionResponse!.StatusCode);
        Assert.True(result.ConnectionClose);
        Assert.Empty(session.StreamIds);
        await channel.Closed.WaitAsync(TimeSpan.FromSeconds(5));

        var teardowns = (await Harness.DrainAsync(fake, TimeSpan.FromMilliseconds(300))).OfType<FakeReceiverEvent.Teardown>().Select(t => t.Session).ToArray();
        Assert.Equal(receiverReturnsStreamId ? [false, true] : [true], teardowns);
        var reason = await session.Connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public async Task Pin_flow_stores_credentials_and_the_next_connection_only_verifies()
    {
        // A receiver that shows a PIN (flags bit 9) and refuses transient pairing.
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { StatusFlags = 0x604, AllowTransientPairing = false, Pin = "4711" }));
        var store = new InMemoryPairingStore();
        var prompts = 0;
        Task<string> Prompt(CancellationToken _)
        {
            prompts++;
            return Task.FromResult("4711");
        }

        await using (var first = await ConnectAsync(fake))
        {
            var paired = await first.PairAsync(store, Prompt, None);
            Assert.Equal(PairingKind.Verified, paired.Kind);
            Assert.True(first.Connection.IsEncrypted);
            await first.SetupSessionAsync(NtpSession(), None);
            await first.TeardownAsync(None);
        }

        Assert.Equal(1, prompts);
        Assert.True(store.Pairings.ContainsKey(fake.DeviceId));
        Assert.Equal(fake.DeviceId, store.Pairings[fake.DeviceId].AccessoryId);
        var firstRequests = (await RequestsAsync(fake)).Select(Line).ToList();
        Assert.Equal(["POST /pair-pin-start", "POST /pair-setup", "POST /pair-setup", "POST /pair-setup", "POST /pair-verify", "POST /pair-verify", "SETUP", "TEARDOWN"], firstRequests);

        await using (var second = await ConnectAsync(fake))
        {
            var verified = await second.PairAsync(store, Prompt, None);
            Assert.Equal(PairingKind.Verified, verified.Kind);
            await second.SetupSessionAsync(NtpSession(), None);
            await second.TeardownAsync(None);
        }

        Assert.Equal(1, prompts);
        var secondRequests = (await RequestsAsync(fake)).Select(Line).ToList();
        Assert.Equal(["POST /pair-verify", "POST /pair-verify", "SETUP", "TEARDOWN"], secondRequests);
    }

    [Fact]
    public async Task Pin_required_without_a_prompt_reports_transient_refused()
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { StatusFlags = 0x604, AllowTransientPairing = false }));
        var store = new InMemoryPairingStore();
        await using var session = await ConnectAsync(fake);
        var error = await Assert.ThrowsAsync<PairingException>(() => session.PairAsync(store, pinPrompt: null, None));
        Assert.Equal(PairingFailure.TransientRefused, error.Failure);
        Assert.Empty(store.Pairings);
        Assert.Null(session.Pairing);
    }

    [Fact]
    public async Task Transient_470_falls_back_to_the_pin_flow()
    {
        // TXT says transient (flags 0x404), but the receiver answers 470 (SPEC-control.md 3.9).
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { AllowTransientPairing = false, Pin = "1234" }));
        var store = new InMemoryPairingStore();
        var prompted = false;
        await using var session = await ConnectAsync(fake);
        var paired = await session.PairAsync(store, _ =>
        {
            prompted = true;
            return Task.FromResult("1234");
        }, None);
        Assert.True(prompted);
        Assert.Equal(PairingKind.Verified, paired.Kind);
        var lines = (await RequestsAsync(fake)).Select(Line).ToList();
        Assert.Equal(["POST /pair-pin-start", "POST /pair-setup", "POST /pair-pin-start", "POST /pair-setup", "POST /pair-setup", "POST /pair-setup", "POST /pair-verify", "POST /pair-verify"], lines);
    }

    [Fact]
    public async Task Rejected_stored_credentials_are_deleted_and_transient_pairing_follows()
    {
        await using var fake = await StartAsync(Quiet());
        var store = new InMemoryPairingStore();
        var controller = await store.GetControllerIdentityAsync(None);

        // Pair regularly once (the fake also allows the PIN flow when transient is possible), then forget us on the receiver.
        await using (var connection = await RtspConnection.ConnectAsync(fake.ControlEndpoint, new RtspConnectionOptions(), NullLogger.Instance, None))
        {
            var client = new HapPairingClient(NullLogger<HapPairingClient>.Instance);
            var started = await client.StartPinPairingAsync(connection, controller, TlvMethod.PairSetup, None);
            var credentials = await client.FinishPinPairingAsync(started, fake.Options.Pin, None);
            await store.SaveAsync(fake.DeviceId, credentials, None);
        }

        Assert.True(fake.RemovePairing(controller.Id));
        await using var session = await ConnectAsync(fake);
        var paired = await session.PairAsync(store, pinPrompt: null, None);
        Assert.Equal(PairingKind.Transient, paired.Kind);
        Assert.Equal(1, store.Deletes);
        Assert.Empty(store.Pairings);
        await session.SetupSessionAsync(NtpSession(), None);
    }

    [Fact]
    public async Task Pairing_twice_on_one_connection_is_refused()
    {
        await using var fake = await StartAsync(Quiet());
        await using var session = await ConnectAndPairAsync(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PairAsync(new InMemoryPairingStore(), null, None));
    }

    [Fact]
    public async Task Ntp_refused_with_501_surfaces_as_status_exception()
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { RefuseNtp = true }));
        await using var session = await ConnectAndPairAsync(fake);
        var error = await Assert.ThrowsAsync<RtspStatusException>(() => session.SetupSessionAsync(NtpSession(), None));
        Assert.Equal(501, error.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectEventChannelAsync(None));

        var ptp = await session.SetupSessionAsync(NtpSession() with { TimingProtocol = TimingProtocol.Ptp, TimingPort = 0, PtpClockId = 0xFFFF_0000_0000_0042UL }, None);
        Assert.Equal([IPAddress.Loopback], ptp.TimingPeerAddresses);
        var recorded = await Harness.NextAsync<FakeReceiverEvent.SessionSetup>(fake);
        Assert.Equal(unchecked((long)0xFFFF_0000_0000_0042UL), recorded.Body.GetDictionary("timingPeerInfo")!.GetInt64("ClockID"));
        await session.TeardownAsync(None);
    }

    [Fact]
    public async Task Flush_setpeers_and_buffered_requests_reach_the_receiver()
    {
        await using var fake = await StartAsync(Quiet());
        await using var session = await ConnectAndPairAsync(fake);
        await session.SetupSessionAsync(NtpSession(), None);
        await session.SetupStreamAsync(Stream(session), None);
        await Harness.DrainAsync(fake, TimeSpan.FromMilliseconds(200));

        await session.FlushAsync(1234, 567890, None);
        var flush = await Harness.NextAsync<FakeReceiverEvent.Flush>(fake);
        Assert.Equal((1234, 567890u), (flush.Sequence, flush.Timestamp));

        await session.SetPeersAsync([IPAddress.Loopback, IPAddress.Parse("10.1.2.3")], None);
        var peers = await Harness.NextAsync<FakeReceiverEvent.Peers>(fake);
        Assert.Equal(["127.0.0.1", "10.1.2.3"], peers.Addresses);

        await session.SetRateAnchorTimeAsync(1, 1000, 5, 1UL << 63, 0xFFFF_0000_0000_0001UL, None);
        var anchor = await Harness.NextAsync<FakeReceiverEvent.Request>(fake, r => r.Method == "SETRATEANCHORTIME");
        Assert.Equal(1000, BinaryPlist.DecodeDictionary(anchor.Body).GetInt64("rtpTime"));

        await session.FlushBufferedAsync(20, 30, null, null, None);
        var flushBuffered = await Harness.NextAsync<FakeReceiverEvent.Request>(fake, r => r.Method == "FLUSHBUFFERED");
        Assert.Equal(30, BinaryPlist.DecodeDictionary(flushBuffered.Body).GetInt64("flushUntilTS"));

        await session.SetMetadataAsync(new TrackMetadata("T", "A", "B", null), 99, None);
        var metadata = await Harness.NextAsync<FakeReceiverEvent.Metadata>(fake);
        Assert.Equal(("T", "A", "B", 99u), ((string)metadata.Items["minm"], (string)metadata.Items["asar"], (string)metadata.Items["asal"], metadata.RtpTime!.Value));

        await session.SetArtworkAsync(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, RtspContentTypes.Png, 99, None);
        var artwork = await Harness.NextAsync<FakeReceiverEvent.Artwork>(fake);
        Assert.Equal(("image/png", 99u), (artwork.ContentType, artwork.RtpTime!.Value));

        await session.SetProgressAsync(new ProgressInfo(1, 2, 3), None);
        var progress = await Harness.NextAsync<FakeReceiverEvent.Progress>(fake);
        Assert.Equal((1u, 2u, 3u), (progress.Start, progress.Current, progress.End));
        await session.TeardownAsync(None);
    }

    [Fact]
    public async Task Stage1_info_inside_the_encrypted_channel_has_txt_airplay()
    {
        await using var fake = await StartAsync(Quiet());
        await using var session = await ConnectAndPairAsync(fake);
        var info = await session.GetInfoAsync(withQualifier: true, None);
        Assert.NotNull(info.GetData("txtAirPlay"));
        Assert.Equal(-20.0, info.GetDouble("initialVolume"));
    }

    [Fact]
    public async Task Known_quirks_are_looked_up_by_model()
    {
        await using var fake = await StartAsync(Quiet(new FakeReceiverOptions { Name = "other" }));
        var receiver = Describe(fake);
        Assert.Equal("Test Speaker ABCD", receiver.Airplay.Model);
        Assert.Same(KnownReceiverQuirks.For("Test Speaker ABCD"), ReceiverQuirks.ForReceiver(receiver));
        Assert.Equal(TimingProtocol.Ntp, ReceiverQuirks.ForReceiver(receiver).TimingProtocol);
        Assert.Equal(TimingProtocol.Ptp, KnownReceiverQuirks.For("ShairportSync").TimingProtocol);
        Assert.Same(ReceiverQuirks.Default, KnownReceiverQuirks.For("Unknown speaker"));
        Assert.Same(ReceiverQuirks.Default, KnownReceiverQuirks.For(null));
    }

    private static async Task<ReceiverEventMessage> NextMessageAsync(EventChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await channel.Messages.ReadAsync(cts.Token);
    }

    private static string? Header(FakeReceiverEvent.Request request, string name) =>
        request.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
