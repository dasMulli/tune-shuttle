using System.Text;
using Airplay2.Receiver.Session;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Xunit;
using static Airplay2.Receiver.Tests.Session.ControlPlaneTestSupport;

namespace Airplay2.Receiver.Tests.Session;

/// <summary>
/// The request table of SPEC-receiver.md 4.7-4.8 and R14 on the product control plane: <c>/fp-setup</c>, SET_PARAMETER and
/// GET_PARAMETER (R9), SETRATEANCHORTIME, FLUSH, FLUSHBUFFERED, and the 200/404/501 cases.
/// </summary>
public class ControlSessionTests
{
    [Theory]
    [InlineData(0, 0x0F, 0xBA)]
    [InlineData(1, 0xCF, 0x8A)]
    [InlineData(2, 0xC1, 0xCE)]
    [InlineData(3, 0x90, 0x59)]
    public async Task Fp_setup_1_returns_the_canned_reply_of_the_mode_byte(byte mode, byte first, byte last)
    {
        var (plane, _) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane); // R14: answered before pairing

        // FPLY, version 3, type 1, sequence 1, ..., mode at offset 14 [sps:rtsp.c:1903-1906].
        byte[] request = [0x46, 0x50, 0x4C, 0x59, 0x03, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x04, 0x02, 0x00, mode, 0xBB];
        var response = await sender.SendAsync(RtspRequest.WithBody("POST", "/fp-setup", RtspContentTypes.OctetStream, request));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(RtspContentTypes.OctetStream, response.ContentType);
        var body = response.Body.ToArray();
        Assert.Equal(FairPlaySetup.Setup1ReplyLength, body.Length);
        // The first 14 bytes are the reply header with the mode at offset 13; then the reply's own bytes [sps:rtsp.c:1856-1891].
        Assert.Equal(new byte[] { 0x46, 0x50, 0x4C, 0x59, 0x03, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x82, 0x02, mode }, body[..14]);
        Assert.Equal(first, body[14]);
        Assert.Equal(last, body[^1]);
    }

    [Fact]
    public void Fp_setup_replies_differ_by_mode_and_unknown_modes_get_none()
    {
        byte[] Request(byte mode) => [0x46, 0x50, 0x4C, 0x59, 0x03, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x04, 0x02, 0x00, mode, 0xBB];

        var replies = Enumerable.Range(0, 4).Select(m => FairPlaySetup.Reply(Request((byte)m), out _)!).ToList();

        Assert.Equal(4, replies.Select(Convert.ToHexString).Distinct().Count());
        Assert.Null(FairPlaySetup.Reply(Request(4), out _));
        Assert.Null(FairPlaySetup.Reply([0x46, 0x50], out var unsupported));
        Assert.True(unsupported);
    }

    [Fact]
    public async Task Fp_setup_2_returns_the_header_and_the_last_20_request_bytes()
    {
        var (plane, _) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);

        var request = new byte[164];
        byte[] head = [0x46, 0x50, 0x4C, 0x59, 0x03, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x98];
        head.CopyTo(request, 0);
        for (var i = 0; i < 20; i++)
        {
            request[^(20 - i)] = (byte)(0xA0 + i);
        }

        var response = await sender.SendAsync(RtspRequest.WithBody("POST", "/fp-setup", RtspContentTypes.OctetStream, request));

        var body = response.Body.ToArray();
        Assert.Equal(32, body.Length);
        Assert.Equal(new byte[] { 0x46, 0x50, 0x4C, 0x59, 0x03, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00, 0x14 }, body[..12]);
        Assert.Equal(request[^20..], body[12..]);
    }

    [Fact]
    public async Task Set_parameter_volume_sets_the_level_and_get_parameter_answers_r9()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        // R9: no output and no sender level yet: 50 (-15 dB).
        Assert.Equal("volume: -15.000000\r\n", Encoding.ASCII.GetString((await sender.SendAsync(SessionRequests.GetVolume(sender.Uri))).Body.Span));

        Assert.Equal(200, (await sender.SendAsync(SessionRequests.SetVolume(sender.Uri, -9.0))).StatusCode);
        var volume = await host.WaitForEventAsync<ReceiverEvent.SenderVolume>();
        Assert.Equal(70, volume.Level);
        Assert.Equal(-9.0, volume.Db);
        Assert.Contains("level 70", host.Recorder.Calls);

        // Without an output the last sender level is answered; with one, the output's G.
        var answer = await sender.SendAsync(SessionRequests.GetVolume(sender.Uri));
        Assert.Equal(RtspContentTypes.Parameters, answer.ContentType);
        Assert.Equal("volume: -9.000000\r\n", Encoding.ASCII.GetString(answer.Body.Span));
        host.OutputLevel = 40;
        Assert.Equal("volume: -18.000000\r\n", Encoding.ASCII.GetString((await sender.SendAsync(SessionRequests.GetVolume(sender.Uri))).Body.Span));

        // Mute.
        await sender.SendAsync(SessionRequests.SetVolume(sender.Uri, -144.0));
        await WaitUntilAsync(() => host.EventsOf<ReceiverEvent.SenderVolume>().Any(v => v.Level == 0), "the mute");
        Assert.Contains("level 0", host.Recorder.Calls);
    }

    [Fact]
    public async Task Set_parameter_progress_and_artwork_raise_events_and_oversized_artwork_is_dropped()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        Assert.Equal(200, (await sender.SendAsync(SessionRequests.Progress(sender.Uri, new ProgressInfo(1000, 45100, 442000, 1000)))).StatusCode);
        var progress = await host.WaitForEventAsync<ReceiverEvent.SenderProgress>();
        Assert.Equal(new ProgressInfo(1000, 45100, 442000, 1000), progress.Progress);

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.Artwork(sender.Uri, jpeg, RtspContentTypes.Jpeg, 1000))).StatusCode);
        var artwork = await host.WaitForEventAsync<ReceiverEvent.SenderArtwork>();
        Assert.Equal(jpeg, artwork.Bytes.ToArray());
        Assert.Equal("image/jpeg", artwork.ContentType);
        Assert.Equal(1000u, artwork.RtpTime);

        var huge = new byte[(1024 * 1024) + 1];
        huge[0] = 0x89;
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.Artwork(sender.Uri, huge, RtspContentTypes.Png, 2000))).StatusCode);
        Assert.Single(host.EventsOf<ReceiverEvent.SenderArtwork>());

        // Unknown content types are answered 200 and ignored.
        Assert.Equal(200, (await sender.SendAsync(RtspRequest.WithBody("SET_PARAMETER", sender.Uri, "application/x-unknown", new byte[] { 1 }))).StatusCode);
    }

    [Fact]
    public async Task Set_parameter_dmap_raises_the_metadata_with_the_rtptime_key()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        var metadata = new TrackMetadata("Synthetic Title", "Synthetic Artist", "Synthetic Album", TimeSpan.FromSeconds(210));
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.Metadata(sender.Uri, metadata, 4242))).StatusCode);

        var item = await host.WaitForEventAsync<ReceiverEvent.SenderMetadata>();
        Assert.Equal(4242u, item.RtpTime);
        Assert.Equal("Synthetic Title", item.Item.Title);
        Assert.Equal("Synthetic Artist", item.Item.Artist);
        Assert.Equal("Synthetic Album", item.Item.Album);

        // A malformed body is answered 200 and dropped.
        Assert.Equal(200, (await sender.SendAsync(RtspRequest.WithBody("SET_PARAMETER", sender.Uri, RtspContentTypes.DmapTagged, new byte[] { 1, 2, 3 }))).StatusCode);
        Assert.Single(host.EventsOf<ReceiverEvent.SenderMetadata>());
    }

    [Fact]
    public async Task Setrateanchortime_flush_and_flushbuffered_are_forwarded_to_the_stream()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();
        Assert.Equal(200, (await sender.StreamSetupAsync(new StreamSetupRequest(StreamType.Buffered, sender.Pairing!.DefaultStreamKey, 0))).StatusCode);
        var stream = Assert.Single(host.Streams);

        // networkTime 12 s + 0.5 (frac 2^63) = 12.5e9 ns; timeline id written as a signed int64 (SPEC-control.md 4.7).
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.SetRateAnchorTime(sender.Uri, 1, 88200, 12, 1UL << 63, 0xF000_0000_0000_0001UL))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.SetRateAnchorTime(sender.Uri, 0, 0, 0, 0, 0))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.FlushBuffered(sender.Uri, 120, 0, null, null))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.FlushBuffered(sender.Uri, 300, 0, 250, 0))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.Flush(sender.Uri, 77, 9000))).StatusCode);
        // shairport-sync registers the truncated method name [sps:rtsp.c:3703].
        Assert.Equal(200, (await sender.SendAsync(RtspRequest.WithPlist("SETRATEANCHORTI", sender.Uri, new PlistDictionary { ["rate"] = 1L }))).StatusCode);

        Assert.Equal(
            ["anchor 88200 12500000000 F000000000000001", "resume", "pause", "flushbuffered 120", "splice 250 300", "flush 77", "resume"],
            stream.Calls);
        Assert.Equal(2, host.EventsOf<ReceiverEvent.SenderResumed>().Count());
        Assert.Single(host.EventsOf<ReceiverEvent.SenderPaused>());
        Assert.Equal([false, true, false], host.EventsOf<ReceiverEvent.SenderFlush>().Select(f => f.Deferred));
    }

    [Fact]
    public async Task Playout_methods_without_a_stream_are_answered_200()
    {
        var (plane, host) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        Assert.Equal(200, (await sender.SendAsync(SessionRequests.SetRateAnchorTime(sender.Uri, 1, 0, 0, 0, 0))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.FlushBuffered(sender.Uri, 1, 0, null, null))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.Flush(sender.Uri, 1, 0))).StatusCode);
        Assert.Equal(400, (await sender.SendAsync(RtspRequest.WithPlist("FLUSHBUFFERED", sender.Uri, new PlistDictionary()))).StatusCode);
        Assert.Empty(host.EventsOf<ReceiverEvent.SenderFlush>());
    }

    [Fact]
    public async Task Other_requests_get_200_404_or_501()
    {
        var (plane, _) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectPairedAsync(plane);
        await sender.SetUpSessionAsync();

        Assert.Equal(200, (await sender.SendAsync(RtspRequest.WithPlist("POST", "/command", new PlistDictionary { ["type"] = "test" }))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(RtspRequest.WithPlist("POST", "/audioMode", new PlistDictionary { ["audioMode"] = "default" }))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(RtspRequest.Create("SETMAGICCOOKIE", sender.Uri))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(RtspRequest.WithPlist("PUT", "/setProperty", new PlistDictionary()))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.SetPeers(sender.Uri, []))).StatusCode);
        Assert.Equal(404, (await sender.SendAsync(RtspRequest.Create("POST", "/no-such-endpoint"))).StatusCode);
        Assert.Equal(404, (await sender.SendAsync(RtspRequest.Create("GET", "/no-such-endpoint"))).StatusCode);
        Assert.Equal(501, (await sender.SendAsync(RtspRequest.Create("ANNOUNCE", sender.Uri))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(RtspRequest.WithBody("GET_PARAMETER", sender.Uri, RtspContentTypes.Parameters, "something\r\n"u8.ToArray()))).StatusCode);
    }

    [Fact]
    public async Task Stage_1_info_with_the_qualifier_carries_txtAirPlay()
    {
        var (plane, _) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);

        var plain = (await sender.SendAsync(SessionRequests.Info(withQualifier: false))).PlistBody();
        var stage1 = (await sender.SendAsync(SessionRequests.Info(withQualifier: true))).PlistBody();

        Assert.Null(plain.GetData("txtAirPlay"));
        Assert.NotNull(stage1.GetData("txtAirPlay"));
        Assert.StartsWith("127.0.0.1:", plain.GetString("senderAddress"), StringComparison.Ordinal);
    }
}
