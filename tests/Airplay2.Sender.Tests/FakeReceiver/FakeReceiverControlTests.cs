using System.Text;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tests.Rtsp;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging;
using Xunit;
using static Airplay2.Sender.Tests.Fake.FakeReceiverHarness;

namespace Airplay2.Sender.Tests.Fake;

/// <summary>
/// Self-tests of the fake receiver's control plane, driven with hand-built requests through the library's
/// <see cref="RtspConnection"/> and <see cref="HapPairingClient"/> (pairing, SETUP,
/// option switches, dropped connection).
/// </summary>
public class FakeReceiverControlTests
{
    [Fact]
    public async Task Get_info_serves_the_recorded_SpeakerA_plist_with_name_identity_and_volume()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { Name = "Kitchen", InitialVolume = -17.5 });
        await using var client = await ConnectAsync(receiver);

        var bare = (await client.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None)).PlistBody();
        Assert.Equal("Kitchen", bare.GetString("name"));
        Assert.Equal(receiver.DeviceId, bare.GetString("deviceID"));
        Assert.Equal(receiver.DeviceId, bare.GetString("pi"));
        Assert.Equal("Lautsprecher SpeakerA GmbH", bare.GetString("manufacturer"));
        Assert.Equal(0x1C340445F8A00L, bare.GetInt64("features"));
        Assert.Equal(0x404L, bare.GetInt64("statusFlags"));
        Assert.Equal(-17.5, bare.GetDouble("initialVolume"));
        Assert.False(bare.ContainsKey("txtAirPlay"));

        var qualifier = new PlistDictionary { ["qualifier"] = new List<object?> { "txtAirPlay" } };
        var stage1 = (await client.SendExpectOkAsync(RtspRequest.WithPlist("GET", "/info", qualifier), CancellationToken.None)).PlistBody();
        var txt = DecodePascalStrings(stage1.GetData("txtAirPlay")!);
        Assert.Equal(receiver.Describe().AirplayTxt, txt);

        var noQualifier = new PlistDictionary { ["qualifier"] = new List<object?> { "somethingElse" } };
        Assert.Equal(400, (await client.SendAsync(RtspRequest.WithPlist("GET", "/info", noQualifier), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Get_info_omits_initialVolume_like_the_SpeakerA_when_not_configured()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { InitialVolume = null });
        await using var client = await ConnectAsync(receiver);
        var info = (await client.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None)).PlistBody();
        Assert.False(info.ContainsKey("initialVolume"));
    }

    [Fact]
    public async Task Describe_gives_txt_records_the_library_parses()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { DeviceId = "02:11:22:33:44:55" });
        var (airplayTxt, raopTxt, endpoint) = receiver.Describe();
        var airplay = AirplayTxtRecord.Parse(airplayTxt);
        var raop = RaopTxtRecord.Parse(raopTxt);

        Assert.Equal(receiver.ControlEndpoint, endpoint);
        Assert.Equal("02:11:22:33:44:55", airplay.DeviceId);
        Assert.Equal("02:11:22:33:44:55", airplay.PairingId);
        Assert.Equal(receiver.PublicKey, airplay.PublicKey);
        Assert.True(airplay.Has(AirplayFeatures.TransientPairing));
        Assert.Equal(airplay.Features, raop.Features);
        Assert.Equal(receiver.PublicKey, raop.PublicKey);
        Assert.Contains(RaopTxtRecord.CodecAlac, raop.Codecs);
    }

    [Fact]
    public async Task Transient_pairing_switches_to_encryption_after_M4()
    {
        await using var receiver = await StartAsync();
        await using var client = await ConnectAsync(receiver);
        var session = await PairingClient().PairTransientAsync(client, CancellationToken.None);
        client.EnableEncryption(session.ControlKeys);

        var paired = await NextAsync<FakeReceiverEvent.Paired>(receiver);
        Assert.Equal("Transient", paired.Kind);
        Assert.Equal(session.SharedSecret, paired.SharedSecret);

        var response = await client.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        Assert.Equal("Fake Receiver", response.PlistBody().GetString("name"));
        var request = await NextAsync<FakeReceiverEvent.Request>(receiver, r => r.Uri == "/info");
        Assert.True(request.Encrypted);
    }

    [Fact]
    public async Task Transient_refused_with_470_then_PIN_pairing_and_verify_on_a_new_connection()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { AllowTransientPairing = false, Pin = "4321" });
        var client = PairingClient();
        PairingCredentials credentials;
        await using (var first = await ConnectAsync(receiver))
        {
            var refused = await Assert.ThrowsAsync<PairingException>(() => client.PairTransientAsync(first, CancellationToken.None));
            Assert.Equal(PairingFailure.TransientRefused, refused.Failure);

            var controller = ControllerIdentity.Generate();
            var started = await client.StartPinPairingAsync(first, controller, TlvMethod.PairSetup, CancellationToken.None);
            credentials = await client.FinishPinPairingAsync(started, "4321", CancellationToken.None);
            Assert.Equal(receiver.DeviceId, credentials.AccessoryId);
            Assert.Equal(receiver.PublicKey, credentials.AccessoryPublicKey);
            Assert.Equal(1, receiver.PairedControllerCount);
        }

        await using var second = await ConnectAsync(receiver);
        var session = await client.VerifyAsync(second, credentials, CancellationToken.None);
        second.EnableEncryption(session.ControlKeys);
        await second.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        Assert.Equal("Verified", (await NextAsync<FakeReceiverEvent.Paired>(receiver)).Kind);

        Assert.True(receiver.RemovePairing(credentials.Controller.Id));
        await using var third = await ConnectAsync(receiver);
        var rejected = await Assert.ThrowsAsync<PairingException>(() => client.VerifyAsync(third, credentials, CancellationToken.None));
        Assert.Equal(PairingFailure.CredentialsRejected, rejected.Failure);
    }

    [Fact]
    public async Task Wrong_pin_is_an_authentication_failure()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { Pin = "1111" });
        await using var connection = await ConnectAsync(receiver);
        var client = PairingClient();
        var started = await client.StartPinPairingAsync(connection, ControllerIdentity.Generate(), TlvMethod.PairSetup, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<PairingException>(() => client.FinishPinPairingAsync(started, "2222", CancellationToken.None));
        Assert.Equal(PairingFailure.Authentication, failure.Failure);
    }

    [Fact]
    public async Task Pair_verify_without_encryption_switch_emulates_shairport()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { EncryptAfterPairVerify = false });
        await using var connection = await ConnectAsync(receiver);
        var client = PairingClient();
        var started = await client.StartPinPairingAsync(connection, ControllerIdentity.Generate(), TlvMethod.PairSetup, CancellationToken.None);
        var credentials = await client.FinishPinPairingAsync(started, "1234", CancellationToken.None);
        await client.VerifyAsync(connection, credentials, CancellationToken.None);

        // Plaintext keeps working, as on shairport-sync 5.5.2 (SPEC-control.md 3.7 note).
        var info = await connection.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        Assert.Equal(200, info.StatusCode);
    }

    [Fact]
    public async Task RequireHkpHeader_refuses_pairing_posts_without_the_header()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { RequireHkpHeader = true });
        await using var bare = await ConnectAsync(receiver);
        var m1 = new Tlv8Document().Add(TlvType.Method, (byte)TlvMethod.PairSetup).AddState(1).Add(TlvType.Flags, (byte)TlvFlags.Transient).Encode();
        var response = await bare.SendAsync(RtspRequest.WithBody("POST", HapPairingEndpoints.PairSetup, RtspContentTypes.OctetStream, m1), CancellationToken.None);
        Assert.Equal(RtspStatus.ConnectionAuthorizationRequired, response.StatusCode);

        await using var proper = await ConnectAsync(receiver);
        await PairingClient().PairTransientAsync(proper, CancellationToken.None); // sends X-Apple-HKP
    }

    [Fact]
    public async Task Session_methods_before_pairing_get_470()
    {
        await using var receiver = await StartAsync();
        await using var client = await ConnectAsync(receiver);
        var response = await client.SendAsync(RtspRequest.WithPlist("SETUP", client.SessionUri, NtpSessionBody()), CancellationToken.None);
        Assert.Equal(RtspStatus.ConnectionAuthorizationRequired, response.StatusCode);
    }

    [Fact]
    public async Task Ntp_session_setup_answers_eventPort_or_501_with_RefuseNtp()
    {
        await using (var receiver = await StartAsync())
        await using (var sender = await SenderSide.ConnectAsync(receiver))
        {
            var answer = await sender.SetupSessionAsync();
            Assert.True(answer.GetInt64("eventPort") > 0);
            Assert.Equal(0L, answer.GetInt64("timingPort"));
            Assert.False(answer.ContainsKey("timingPeerInfo"));
            var recorded = await NextAsync<FakeReceiverEvent.SessionSetup>(receiver);
            Assert.Equal("NTP", recorded.Body.GetString("timingProtocol"));
        }

        await using (var refusing = await StartAsync(new FakeReceiverOptions { RefuseNtp = true }))
        await using (var sender = await SenderSide.ConnectAsync(refusing))
        {
            var response = await sender.SendAsync(RtspRequest.WithPlist("SETUP", sender.Uri, NtpSessionBody()));
            Assert.Equal(RtspStatus.NotImplemented, response.StatusCode);

            // PTP is still accepted and answered with the receiver's timingPeerInfo.
            var ptp = await sender.SetupSessionAsync(NtpSessionBody(protocol: "PTP"));
            Assert.True(ptp.GetInt64("eventPort") > 0);
            Assert.Equal("127.0.0.1", Assert.Single(ptp.GetDictionary("timingPeerInfo")!.GetArray("Addresses")!));
        }
    }

    [Fact]
    public async Task Group_uuid_from_the_session_setup_becomes_the_advertised_gid()
    {
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        var body = NtpSessionBody();
        body["groupUUID"] = "6F4B1D2C-0000-4000-8000-000000000001";
        await sender.SetupSessionAsync(body);
        Assert.Equal("6F4B1D2C-0000-4000-8000-000000000001", AirplayTxtRecord.Parse(receiver.Describe().AirplayTxt).GroupId);
    }

    [Fact]
    public async Task Record_stream_setup_and_both_teardown_forms()
    {
        var log = new CapturingLogger(LogLevel.Warning);
        await using var receiver = await StartAsync(new FakeReceiverOptions { Logger = log });
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync();

        var record = await sender.SendAsync(RtspRequest.Create("RECORD", sender.Uri));
        Assert.Equal(200, record.StatusCode);
        Assert.Equal("0", record.Header(RtspHeaders.AudioLatency));

        var stream = await sender.SetupStreamAsync(controlPort: 6001);
        Assert.Equal(96L, stream.GetInt64("type"));
        Assert.Equal(receiver.DataPort, (int)stream.GetInt64("dataPort")!.Value);
        Assert.Equal(receiver.ControlPort, (int)stream.GetInt64("controlPort")!.Value);
        var streamId = stream.GetInt64("streamID");
        Assert.NotNull(streamId);
        var setup = await NextAsync<FakeReceiverEvent.StreamSetup>(receiver);
        Assert.Equal(sender.Pairing.DefaultStreamKey, ((PlistDictionary)setup.Body.GetArray("streams")![0]!).GetData("shk"));

        var feedback = await sender.SendAsync(RtspRequest.Create("POST", "/feedback"));
        var feedbackStream = (PlistDictionary)feedback.PlistBody().GetArray("streams")![0]!;
        Assert.Equal(96L, feedbackStream.GetInt64("type"));
        Assert.Equal(44100.0, feedbackStream.GetDouble("sr"));

        var perStream = new PlistDictionary { ["streams"] = new List<object?> { new PlistDictionary { ["streamID"] = streamId, ["type"] = 96L } } };
        var first = await sender.SendAsync(RtspRequest.WithPlist("TEARDOWN", sender.Uri, perStream));
        Assert.Equal(200, first.StatusCode);
        Assert.Null(first.Header(RtspHeaders.Connection));
        Assert.False((await NextAsync<FakeReceiverEvent.Teardown>(receiver)).Session);
        Assert.True((await sender.SendAsync(RtspRequest.Create("POST", "/feedback"))).Body.IsEmpty);

        var second = await sender.SendAsync(RtspRequest.WithPlist("TEARDOWN", sender.Uri, new PlistDictionary()));
        Assert.Equal(200, second.StatusCode);
        Assert.Equal("close", second.Header(RtspHeaders.Connection));
        Assert.True((await NextAsync<FakeReceiverEvent.Teardown>(receiver)).Session);
        await sender.Connection.Closed.WaitAsync(Patience);
        await NextAsync<FakeReceiverEvent.ConnectionClosed>(receiver);
        await receiver.DisposeAsync();
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Stream_setup_without_streamID_like_shairport()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { ReturnStreamId = false });
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync();
        var stream = await sender.SetupStreamAsync(controlPort: 6001);
        Assert.False(stream.ContainsKey("streamID"));
    }

    [Fact]
    public async Task Stream_setup_before_session_setup_is_451()
    {
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        var response = await sender.SendAsync(RtspRequest.WithPlist("SETUP", sender.Uri, StreamBody(new byte[32], 6001)));
        Assert.Equal(RtspStatus.Unavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(GetParameterShape.Shairport, "\r\nvolume: -20.000000\r\n", "\r\nvolume: -15.500000\r\n")]
    [InlineData(GetParameterShape.Ap2r, "volume: -20.0\r\n", "volume: -15.5\r\n")]
    public async Task Get_parameter_volume_in_both_shapes(GetParameterShape shape, string before, string after)
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { GetParameterShape = shape, InitialVolume = -20.0 });
        await using var sender = await SenderSide.ConnectAsync(receiver);
        var get = RtspRequest.WithBody("GET_PARAMETER", sender.Uri, RtspContentTypes.Parameters, "volume\r\n"u8.ToArray());

        var first = await sender.SendAsync(get);
        Assert.Equal(before, Encoding.ASCII.GetString(first.Body.Span));
        Assert.Equal(-20.0, VolumeMapping.ParseParameter(Encoding.ASCII.GetString(first.Body.Span)));

        await sender.SendAsync(RtspRequest.WithBody("SET_PARAMETER", sender.Uri, RtspContentTypes.Parameters, Encoding.ASCII.GetBytes(VolumeMapping.FormatParameter(-15.5))));
        Assert.Equal(-15.5, (await NextAsync<FakeReceiverEvent.Volume>(receiver)).Db);
        Assert.Equal(-15.5, receiver.VolumeDb);

        var second = await sender.SendAsync(get);
        Assert.Equal(after, Encoding.ASCII.GetString(second.Body.Span));
    }

    [Fact]
    public async Task Set_parameter_metadata_artwork_progress_and_flush_are_recorded()
    {
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        var rtpInfo = new Dictionary<string, string> { [RtspHeaders.RtpInfo] = "rtptime=4242" };

        var dmap = Dmap("mlit", [.. Dmap("minm", "Title"u8.ToArray()), .. Dmap("asar", "Artist"u8.ToArray()), .. Dmap("astm", [0, 0, 0x04, 0xD2])]);
        await sender.SendAsync(RtspRequest.WithBody("SET_PARAMETER", sender.Uri, RtspContentTypes.DmapTagged, dmap, rtpInfo));
        var metadata = await NextAsync<FakeReceiverEvent.Metadata>(receiver);
        Assert.Equal("Title", metadata.Items["minm"]);
        Assert.Equal("Artist", metadata.Items["asar"]);
        Assert.Equal(1234UL, metadata.Items["astm"]);
        Assert.Equal(4242u, metadata.RtpTime);

        var jpeg = new byte[3000];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        await sender.SendAsync(RtspRequest.WithBody("SET_PARAMETER", sender.Uri, RtspContentTypes.Jpeg, jpeg, rtpInfo));
        var artwork = await NextAsync<FakeReceiverEvent.Artwork>(receiver);
        Assert.Equal(jpeg, artwork.Image);
        Assert.Equal(4242u, artwork.RtpTime);

        await sender.SendAsync(RtspRequest.WithBody("SET_PARAMETER", sender.Uri, RtspContentTypes.Parameters, "progress: 100/200/300\r\n"u8.ToArray()));
        Assert.Equal(new FakeReceiverEvent.Progress(100, 200, 300), await NextAsync<FakeReceiverEvent.Progress>(receiver));

        await sender.SendAsync(RtspRequest.Create("FLUSH", sender.Uri) with { Headers = new Dictionary<string, string> { [RtspHeaders.RtpInfo] = "seq=7;rtptime=99" } });
        Assert.Equal(new FakeReceiverEvent.Flush(7, 99), await NextAsync<FakeReceiverEvent.Flush>(receiver));
    }

    [Fact]
    public async Task Setpeers_auth_setup_options_and_unknown_methods()
    {
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);

        var auth = await sender.SendAsync(RtspRequest.WithBody("POST", "/auth-setup", RtspContentTypes.OctetStream, new byte[33]));
        Assert.Equal(200, auth.StatusCode);
        Assert.Equal(1076, auth.Body.Length);

        var peers = new List<object?> { "127.0.0.1", "198.51.100.2" };
        await sender.SendAsync(RtspRequest.WithBody("SETPEERS", sender.Uri, RtspContentTypes.PeerListChanged, BinaryPlist.Encode(peers)));
        Assert.Equal(new[] { "127.0.0.1", "198.51.100.2" }, (await NextAsync<FakeReceiverEvent.Peers>(receiver)).Addresses);

        var options = await sender.SendAsync(RtspRequest.Create("OPTIONS", "*"));
        Assert.Contains("SETUP", options.Header(RtspHeaders.Public));

        Assert.Equal(RtspStatus.NotImplemented, (await sender.SendAsync(RtspRequest.Create("FROBNICATE", sender.Uri))).StatusCode);
        Assert.Equal(RtspStatus.NotFound, (await sender.SendAsync(RtspRequest.Create("POST", "/nowhere"))).StatusCode);

        await using var strict = await StartAsync(new FakeReceiverOptions { AnswerAuthSetup = false });
        await using var other = await SenderSide.ConnectAsync(strict);
        Assert.Equal(RtspStatus.NotFound, (await other.SendAsync(RtspRequest.WithBody("POST", "/auth-setup", RtspContentTypes.OctetStream, new byte[33]))).StatusCode);
    }

    [Fact]
    public async Task Event_channel_sends_updateInfo_on_connect_and_remote_commands_on_demand()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { Name = "Events" });
        await using var sender = await SenderSide.ConnectAsync(receiver);
        var session = await sender.SetupSessionAsync();
        await using var events = await sender.ConnectEventsAsync(session);

        var update = await events.NextAsync("updateInfo");
        var value = update.GetDictionary("value")!;
        Assert.Equal("Events", value.GetString("name"));
        Assert.NotNull(value.GetData("txtAirPlay"));
        Assert.Equal(200, (await NextAsync<FakeReceiverEvent.EventCommandAnswered>(receiver)).StatusCode);
        Assert.InRange(events.Server.LargestReceivedFrame, 1, 1024);

        await receiver.SendRemoteCommandAsync("paus", CancellationToken.None);
        Assert.Equal("paus", (await events.NextAsync("sendMediaRemoteCommand")).GetString("value"));

        await receiver.ChangeVolumeAsync(-12.0, CancellationToken.None);
        Assert.Equal(-12.0, (await events.NextAsync("updateInfo")).GetDictionary("value")!.GetDouble("initialVolume"));
    }

    [Fact]
    public async Task Event_channel_large_frames_exceed_1024_bytes()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { LargeEventFrames = true });
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await using var events = await sender.ConnectEventsAsync(await sender.SetupSessionAsync());
        await events.NextAsync("updateInfo");
        Assert.True(events.Server.LargestReceivedFrame > 1024, $"largest frame {events.Server.LargestReceivedFrame}");
        Assert.True((await NextAsync<FakeReceiverEvent.EventCommandAnswered>(receiver)).LargeFrame);
    }

    [Fact]
    public async Task UpdateInfo_after_record_like_an_Apple_TV()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { UpdateInfoOnConnect = false });
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await using var events = await sender.ConnectEventsAsync(await sender.SetupSessionAsync());
        await Task.Delay(300);
        Assert.False(events.Requests.TryRead(out _));

        await sender.SendAsync(RtspRequest.Create("RECORD", sender.Uri));
        await events.NextAsync("updateInfo");
    }

    [Fact]
    public async Task Unanswered_event_request_closes_the_event_channel()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { EventAnswerTimeout = TimeSpan.FromMilliseconds(300) });
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await using var events = await sender.ConnectEventsAsync(await sender.SetupSessionAsync(), answer: false);
        await events.NextAsync("updateInfo");

        Assert.Equal(0, (await NextAsync<FakeReceiverEvent.EventCommandAnswered>(receiver)).StatusCode);
        await NextAsync<FakeReceiverEvent.EventChannelClosed>(receiver);
        await events.Server.Closed.WaitAsync(Patience);
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.SendRemoteCommandAsync("play", CancellationToken.None));
    }

    [Fact]
    public async Task Survives_a_dropped_connection_and_serves_the_next_sender()
    {
        var log = new CapturingLogger(LogLevel.Warning);
        await using var receiver = await StartAsync(new FakeReceiverOptions { Logger = log });
        var sender = await SenderSide.ConnectAsync(receiver);
        var events = await sender.ConnectEventsAsync(await sender.SetupSessionAsync());
        await events.NextAsync("updateInfo");
        await sender.SetupStreamAsync(controlPort: 6001);

        await sender.DisposeAsync(); // dropped without TEARDOWN
        await NextAsync<FakeReceiverEvent.ConnectionClosed>(receiver);
        await NextAsync<FakeReceiverEvent.EventChannelClosed>(receiver);
        await events.Server.Closed.WaitAsync(Patience);
        await events.DisposeAsync();

        await using var next = await SenderSide.ConnectAsync(receiver);
        var session = await next.SetupSessionAsync();
        await using var nextEvents = await next.ConnectEventsAsync(session);
        await nextEvents.NextAsync("updateInfo");
        await next.SetupStreamAsync(controlPort: 6002);
        var deadline = DateTime.UtcNow + Patience;
        while (receiver.OpenConnections != 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, receiver.OpenConnections);
        await receiver.DisposeAsync();
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Kick_closes_the_control_connection()
    {
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await receiver.KickAsync(CancellationToken.None);
        await sender.Connection.Closed.WaitAsync(Patience);
        await NextAsync<FakeReceiverEvent.ConnectionClosed>(receiver);
    }

    private static List<string> DecodePascalStrings(byte[] data)
    {
        var list = new List<string>();
        for (var i = 0; i < data.Length;)
        {
            var length = data[i];
            list.Add(Encoding.UTF8.GetString(data, i + 1, length));
            i += 1 + length;
        }

        return list;
    }

    private static byte[] Dmap(string tag, byte[] value)
    {
        var item = new byte[8 + value.Length];
        Encoding.ASCII.GetBytes(tag, item);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(item.AsSpan(4), (uint)value.Length);
        value.CopyTo(item, 8);
        return item;
    }
}
