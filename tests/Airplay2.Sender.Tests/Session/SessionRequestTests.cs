using System.Net;
using System.Text;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Xunit;

namespace Airplay2.Sender.Tests.Session;

/// <summary>Request bodies and headers of the session layer against SPEC-control.md 3.10, 4, 5 and SPEC-audio.md 1.1.</summary>
public sealed class SessionRequestTests
{
    private const string Uri = "rtsp://203.0.113.150/12345";

    [Fact]
    public void Dmap_listing_item_has_the_spec_bytes()
    {
        // SPEC-control.md 5.2: mlit { minm, asar, asal }, tag + 4-byte big-endian length + value, strings UTF-8.
        var bytes = DmapEncoder.EncodeListingItem(new TrackMetadata("Title", "Artist", "Album", null));
        var expected = Convert.FromHexString(
            "6D6C697400000028" + // mlit, 40
            "6D696E6D00000005" + Convert.ToHexString("Title"u8) +
            "6173617200000006" + Convert.ToHexString("Artist"u8) +
            "6173616C00000005" + Convert.ToHexString("Album"u8));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Dmap_numeric_tags_are_big_endian_with_their_width()
    {
        var bytes = DmapEncoder.EncodeListingItem(new TrackMetadata("Ä", null, "", TimeSpan.FromMilliseconds(200500), TrackNumber: 7, DiscNumber: 2, Genre: "Pop"));
        var expected = Convert.FromHexString(
            "6D6C697400000035" +
            "6D696E6D00000002C384" + // UTF-8 without terminator; the null artist and the empty album are left out
            "6173746D0000000400030F34" + // astm uint32 ms
            "6173746E000000020007" + // astn uint16
            "6173646E000000020002" + // asdn uint16
            "6173676E00000003" + Convert.ToHexString("Pop"u8));
        Assert.Equal(expected, bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => DmapEncoder.EncodeListingItem(new TrackMetadata(null, null, null, null, TrackNumber: 70000)));
    }

    [Fact]
    public void Dmap_without_items_is_an_empty_mlit()
    {
        Assert.Equal(Convert.FromHexString("6D6C697400000000"), DmapEncoder.EncodeListingItem(new TrackMetadata(null, null, null, null)));
    }

    [Fact]
    public void Auth_setup_is_33_octet_stream_bytes_starting_with_01()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var request = SessionRequests.AuthSetup(key);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/auth-setup", request.Uri);
        Assert.Equal(RtspContentTypes.OctetStream, request.ContentType);
        Assert.Equal(33, request.Body.Length);
        Assert.Equal(0x01, request.Body.Span[0]);
        Assert.Equal(key, request.Body.Span[1..].ToArray());

        var wire = Encoding.ASCII.GetString(RtspMessageCodec.EncodeRequest(request, 3, new RtspClientIdentity("0123456789ABCDEF", 42, "AirPlay/550.10")));
        Assert.StartsWith("POST /auth-setup RTSP/1.0\r\n", wire);
        Assert.Contains("Content-Type: application/octet-stream\r\n", wire);
        Assert.Contains("Content-Length: 33\r\n", wire);
        Assert.Throws<ArgumentException>(() => SessionRequests.AuthSetup(new byte[31]));
    }

    [Fact]
    public void Ntp_session_setup_body_has_pyatv_keys_and_no_streams()
    {
        var body = SessionRequests.SessionSetupBody(SessionTestSupport.NtpSession(timingPort: 6002, group: "AAAAAAAA-0000-0000-0000-000000000001"), IPAddress.Loopback);
        Assert.False(body.ContainsKey("streams"));
        Assert.Equal("NTP", body.GetString("timingProtocol"));
        Assert.Equal(6002, body.GetInt64("timingPort"));
        Assert.Equal("11111111-2222-3333-4444-555555555555", body.GetString("sessionUUID"));
        Assert.Equal("AA:BB:CC:DD:EE:01", body.GetString("deviceID"));
        Assert.Equal("AA:BB:CC:DD:EE:02", body.GetString("macAddress"));
        Assert.Equal("session tests", body.GetString("name"));
        Assert.True(body.GetBool("isMultiSelectAirPlay"));
        Assert.False(body.GetBool("groupContainsGroupLeader"));
        Assert.Equal("AAAAAAAA-0000-0000-0000-000000000001", body.GetString("groupUUID"));
        Assert.False(body.ContainsKey("timingPeerInfo"));
        Assert.Equal(
            ["deviceID", "sessionUUID", "timingPort", "timingProtocol", "isMultiSelectAirPlay", "groupContainsGroupLeader", "groupUUID", "macAddress", "model", "name", "osBuildVersion", "osName", "osVersion", "senderSupportsRelay", "sourceVersion", "statsCollectionEnabled"],
            body.Keys.ToArray());

        // Round trip through the bplist writer and reader.
        var decoded = BinaryPlist.DecodeDictionary(BinaryPlist.Encode(body));
        Assert.Equal(6002, decoded.GetInt64("timingPort"));

        Assert.Throws<ArgumentOutOfRangeException>(() => SessionRequests.SessionSetupBody(SessionTestSupport.NtpSession(timingPort: 0), IPAddress.Loopback));
    }

    [Fact]
    public void Ptp_session_setup_writes_the_clock_id_as_signed_int64()
    {
        const ulong clockId = 0xFFFF_1234_5678_9ABCUL;
        var request = SessionTestSupport.NtpSession() with { TimingProtocol = TimingProtocol.Ptp, TimingPort = 0, PtpClockId = clockId, TimingPeerAddresses = [IPAddress.Parse("203.0.113.150"), IPAddress.Parse("fe80::1%12")] };
        var body = SessionRequests.SessionSetupBody(request, IPAddress.Loopback);
        Assert.Equal("PTP", body.GetString("timingProtocol"));
        Assert.False(body.ContainsKey("timingPort"));
        var info = body.GetDictionary("timingPeerInfo")!;
        Assert.Equal(unchecked((long)clockId), info["ClockID"]);
        Assert.Equal(0L, info["DeviceType"]);
        Assert.False(info.GetBool("SupportsClockPortMatchingOverride"));
        Assert.Equal(new object?[] { "203.0.113.150", "fe80::1" }, info.GetArray("Addresses")!.ToArray());
        Assert.Single(body.GetArray("timingPeerList")!);

        // On the wire a signed 8-byte integer, as owntone writes it; decoded back to the same bits.
        var decoded = BinaryPlist.DecodeDictionary(BinaryPlist.Encode(body)).GetDictionary("timingPeerInfo")!;
        Assert.Equal(clockId, unchecked((ulong)decoded.GetInt64("ClockID")!.Value));

        var noAddresses = SessionRequests.SessionSetupBody(request with { TimingPeerAddresses = null }, IPAddress.Parse("10.0.0.5"));
        Assert.Equal(new object?[] { "10.0.0.5" }, noAddresses.GetDictionary("timingPeerInfo")!.GetArray("Addresses")!.ToArray());
        Assert.Throws<ArgumentException>(() => SessionRequests.SessionSetupBody(request with { PtpClockId = null }, IPAddress.Loopback));
    }

    [Fact]
    public void Stream_setup_body_is_spec_audio_1_1()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var body = SessionRequests.StreamSetupBody(new StreamSetupRequest(StreamType.Realtime, key, 6001), rtspSessionId: 4242);
        var stream = (PlistDictionary)Assert.Single(body.GetArray("streams")!)!;
        Assert.Equal(96, stream.GetInt64("type"));
        Assert.Equal(2, stream.GetInt64("ct"));
        Assert.Equal(0x40000, stream.GetInt64("audioFormat"));
        Assert.Equal(352, stream.GetInt64("spf"));
        Assert.Equal(44100, stream.GetInt64("sr"));
        Assert.Equal(key, stream.GetData("shk"));
        Assert.Equal(6001, stream.GetInt64("controlPort"));
        Assert.Equal(11025, stream.GetInt64("latencyMin"));
        Assert.Equal(88200, stream.GetInt64("latencyMax"));
        Assert.Equal("default", stream.GetString("audioMode"));
        Assert.True(stream.GetBool("isMedia"));
        Assert.False(stream.GetBool("supportsDynamicStreamID"));
        Assert.Equal(4242, stream.GetInt64("streamConnectionID"));

        var explicitId = SessionRequests.StreamSetupBody(new StreamSetupRequest(StreamType.Realtime, key, 6001, StreamConnectionId: 99), 4242);
        Assert.Equal(99, ((PlistDictionary)explicitId.GetArray("streams")![0]!).GetInt64("streamConnectionID"));

        var buffered = (PlistDictionary)SessionRequests.StreamSetupBody(new StreamSetupRequest(StreamType.Buffered, key, 6001), 1).GetArray("streams")![0]!;
        Assert.Equal(103, buffered.GetInt64("type"));
        Assert.False(buffered.ContainsKey("latencyMin"));

        // The buffered stream picks its own format (SPEC.md C9): ct and audioFormat come from the request.
        var aac = (PlistDictionary)SessionRequests.StreamSetupBody(new StreamSetupRequest(StreamType.Buffered, key, 6001, CompressionType: 4, AudioFormat: 0x400000), 1).GetArray("streams")![0]!;
        Assert.Equal((4L, 0x400000L), (aac.GetInt64("ct"), aac.GetInt64("audioFormat")));

        Assert.Throws<ArgumentException>(() => SessionRequests.StreamSetupBody(new StreamSetupRequest(StreamType.Realtime, new byte[16], 6001), 1));
    }

    [Theory]
    [InlineData(-15.0, "volume: -15.000000\r\n")]
    [InlineData(-0.3, "volume: -0.300000\r\n")]
    [InlineData(0.0, "volume: 0.000000\r\n")]
    [InlineData(-144.0, "volume: -144.000000\r\n")]
    public void Volume_parameter_body(double db, string expected)
    {
        var request = SessionRequests.SetVolume(Uri, db);
        Assert.Equal("SET_PARAMETER", request.Method);
        Assert.Equal(RtspContentTypes.Parameters, request.ContentType);
        Assert.Equal(expected, Encoding.ASCII.GetString(request.Body.Span));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(-31.0)]
    [InlineData(double.NaN)]
    public void Volume_outside_the_wire_range_is_refused(double db) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionRequests.SetVolume(Uri, db));

    [Fact]
    public void Get_parameter_body_is_exactly_volume_crlf()
    {
        var request = SessionRequests.GetVolume(Uri);
        Assert.Equal("GET_PARAMETER", request.Method);
        Assert.Equal(RtspContentTypes.Parameters, request.ContentType);
        Assert.Equal("volume\r\n"u8.ToArray(), request.Body.ToArray());
    }

    [Fact]
    public void Metadata_artwork_and_progress_carry_rtp_info()
    {
        var dmap = SessionRequests.Metadata(Uri, new TrackMetadata("T", "A", "B", null), 77175);
        Assert.Equal(RtspContentTypes.DmapTagged, dmap.ContentType);
        Assert.Equal("rtptime=77175", dmap.Headers[RtspHeaders.RtpInfo]);

        var art = SessionRequests.Artwork(Uri, new byte[] { 0xFF, 0xD8, 0xFF }, "IMAGE/JPEG", 77175);
        Assert.Equal("image/jpeg", art.ContentType);
        Assert.Equal("rtptime=77175", art.Headers[RtspHeaders.RtpInfo]);
        Assert.Throws<ArgumentException>(() => SessionRequests.Artwork(Uri, new byte[] { 1 }, "image/gif", 0));
        Assert.Throws<ArgumentException>(() => SessionRequests.Artwork(Uri, ReadOnlyMemory<byte>.Empty, "image/png", 0));

        var progress = SessionRequests.Progress(Uri, new ProgressInfo(61815, 77175, 8015175, RtpTime: 77175));
        Assert.Equal("progress: 61815/77175/8015175\r\n", Encoding.ASCII.GetString(progress.Body.Span));
        Assert.Equal("rtptime=77175", progress.Headers[RtspHeaders.RtpInfo]);
        Assert.Equal("rtptime=5", SessionRequests.Progress(Uri, new ProgressInfo(5, 6, 7)).Headers[RtspHeaders.RtpInfo]);
    }

    [Fact]
    public void Flush_has_rtp_info_and_no_body()
    {
        var request = SessionRequests.Flush(Uri, 65535, 4294967295);
        Assert.Equal("FLUSH", request.Method);
        Assert.Null(request.ContentType);
        Assert.True(request.Body.IsEmpty);
        Assert.Equal("seq=65535;rtptime=4294967295", request.Headers[RtspHeaders.RtpInfo]);
    }

    [Fact]
    public void Set_rate_anchor_time_and_flush_buffered_bodies()
    {
        const ulong timeline = 0xFFFF_0000_0000_0001UL;
        var anchor = SessionRequests.SetRateAnchorTime(Uri, 1, 123456, 1_000, ulong.MaxValue, timeline);
        Assert.Equal("SETRATEANCHORTIME", anchor.Method);
        var body = BinaryPlist.DecodeDictionary(anchor.Body.Span);
        Assert.Equal(1, body.GetInt64("rate"));
        Assert.Equal(123456, body.GetInt64("rtpTime"));
        Assert.Equal(1000, body.GetInt64("networkTimeSecs"));
        Assert.Equal(ulong.MaxValue, body["networkTimeFrac"]);
        Assert.Equal(unchecked((long)timeline), body.GetInt64("networkTimeTimelineID"));

        var immediate = BinaryPlist.DecodeDictionary(SessionRequests.FlushBuffered(Uri, 10, 20, null, null).Body.Span);
        Assert.Equal(["flushUntilSeq", "flushUntilTS"], immediate.Keys.ToArray());
        var deferred = BinaryPlist.DecodeDictionary(SessionRequests.FlushBuffered(Uri, 10, 20, 5, 6).Body.Span);
        Assert.Equal(["flushFromSeq", "flushFromTS", "flushUntilSeq", "flushUntilTS"], deferred.Keys.ToArray());
        Assert.Throws<ArgumentException>(() => SessionRequests.FlushBuffered(Uri, 1, 2, 3, null));
    }

    [Fact]
    public void Setpeers_is_a_bplist_array_of_strings_with_the_sub_path_content_type()
    {
        var request = SessionRequests.SetPeers(Uri, [IPAddress.Parse("192.0.2.26"), IPAddress.Parse("203.0.113.150")]);
        Assert.Equal("/peer-list-changed", request.ContentType);
        var list = Assert.IsType<List<object?>>(BinaryPlist.Decode(request.Body.Span));
        Assert.Equal(new object?[] { "192.0.2.26", "203.0.113.150" }, list.ToArray());
    }

    [Fact]
    public void Teardown_always_carries_a_bplist()
    {
        // SPEC.md D7: per-stream {streams:[{streamID,type}]}, session {}; never body-less.
        var perStream = SessionRequests.TeardownStream(Uri, 7, StreamType.Realtime);
        var stream = (PlistDictionary)BinaryPlist.DecodeDictionary(perStream.Body.Span).GetArray("streams")![0]!;
        Assert.Equal(7, stream.GetInt64("streamID"));
        Assert.Equal(96, stream.GetInt64("type"));

        var session = SessionRequests.TeardownSession(Uri);
        Assert.Equal(RtspContentTypes.BinaryPlist, session.ContentType);
        Assert.Empty(BinaryPlist.DecodeDictionary(session.Body.Span));
    }

    [Fact]
    public void Feedback_is_body_less_unless_a_probe_body_is_given()
    {
        var plain = SessionRequests.Feedback();
        Assert.Equal("POST", plain.Method);
        Assert.Equal("/feedback", plain.Uri);
        Assert.Null(plain.ContentType);
        Assert.True(plain.Body.IsEmpty);
        Assert.Equal(RtspContentTypes.BinaryPlist, SessionRequests.Feedback(new PlistDictionary()).ContentType);
    }

    [Fact]
    public void Get_info_qualifier_body()
    {
        Assert.True(SessionRequests.Info(false).Body.IsEmpty);
        var stage1 = BinaryPlist.DecodeDictionary(SessionRequests.Info(true).Body.Span);
        Assert.Equal(new object?[] { "txtAirPlay" }, stage1.GetArray("qualifier")!.ToArray());
    }
}
