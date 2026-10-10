using System.Net;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Airplay2.Sender.Tests.Session;

/// <summary>Answer handling of <see cref="ReceiverSession"/> for receiver behaviours the fake does not produce (live findings).</summary>
public sealed class ReceiverSessionUnitTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Session_setup_answered_200_without_plist_is_a_refusal()
    {
        // shairport-sync 5.5.2 answers an NTP session SETUP with "200 OK" and Content-Length 0 (live, 2026-10-09).
        var (session, connection) = Create();
        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(200));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.SetupSessionAsync(SessionTestSupport.NtpSession(), None));
        Assert.Contains("refused", error.Message);
        Assert.Null(session.SessionSetup);

        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(200, new PlistDictionary { ["timingPort"] = 0L }));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.SetupSessionAsync(SessionTestSupport.NtpSession(), None));
    }

    [Fact]
    public async Task SpeakerA_session_setup_answer_with_timing_port()
    {
        // Receiver A's answer (srcvers 366.0): {eventPort: 7002, timingPort: 7001} (live, 2026-10-09).
        var (session, connection) = Create();
        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(200, new PlistDictionary { ["eventPort"] = 7002L, ["timingPort"] = 7001L }));
        var result = await session.SetupSessionAsync(SessionTestSupport.NtpSession(), None);
        Assert.Equal((7002, 7001), (result.EventPort, result.TimingPort));
        Assert.Empty(result.TimingPeerAddresses);
    }

    [Fact]
    public async Task Metadata_items_are_best_effort()
    {
        // Receiver A answers DMAP with 500 in a session without audio (probe P9); that must not fail the caller.
        var (session, connection) = Create();
        connection.SendAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(500));
        await session.SetMetadataAsync(new TrackMetadata("T", "A", "B", null), 1, None);
        await session.SetArtworkAsync(new byte[] { 0xFF, 0xD8 }, RtspContentTypes.Jpeg, 1, None);
        await session.SetProgressAsync(new ProgressInfo(1, 2, 3), None);
        await connection.Received(3).SendAsync(Arg.Is<RtspRequest>(r => r.Method == "SET_PARAMETER"), Arg.Any<CancellationToken>());
        Assert.True(session.DmapRefused);

        // Transport failures still surface (a fresh session: the first one no longer sends DMAP).
        (session, connection) = Create();
        connection.SendAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns<Task<RtspResponse>>(_ => throw new IOException("closed"));
        await Assert.ThrowsAsync<IOException>(() => session.SetMetadataAsync(new TrackMetadata("T", null, null, null), 1, None));
    }

    [Fact]
    public async Task Dmap_text_stops_after_the_first_refusal_while_artwork_and_progress_continue()
    {
        // SPEC.md 4.2: receiver A answers DMAP 500, sometimes after ~10 s, and the control connection carries one request
        // at a time, so the session stops sending DMAP after the first non-2xx answer.
        var (session, connection) = Create();
        connection.SendAsync(Arg.Is<RtspRequest>(r => r.ContentType == RtspContentTypes.DmapTagged), Arg.Any<CancellationToken>()).Returns(Answer(500));
        connection.SendAsync(Arg.Is<RtspRequest>(r => r.ContentType != RtspContentTypes.DmapTagged), Arg.Any<CancellationToken>()).Returns(Answer(200));
        Assert.False(session.DmapRefused);

        await session.SetMetadataAsync(new TrackMetadata("T1", "A", "B", null), 1, None);
        Assert.True(session.DmapRefused);
        await session.SetMetadataAsync(new TrackMetadata("T2", "A", "B", null), 2, None);
        await session.SetArtworkAsync(new byte[] { 0xFF, 0xD8 }, RtspContentTypes.Jpeg, 2, None);
        await session.SetProgressAsync(new ProgressInfo(2, 3, 4), None);

        await connection.Received(1).SendAsync(Arg.Is<RtspRequest>(r => r.ContentType == RtspContentTypes.DmapTagged), Arg.Any<CancellationToken>());
        await connection.Received(2).SendAsync(Arg.Is<RtspRequest>(r => r.ContentType != RtspContentTypes.DmapTagged), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dmap_text_keeps_flowing_while_the_receiver_accepts_it()
    {
        var (session, connection) = Create();
        connection.SendAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(200));
        await session.SetMetadataAsync(new TrackMetadata("T1", "A", "B", null), 1, None);
        await session.SetMetadataAsync(new TrackMetadata("T2", "A", "B", null), 2, None);
        await connection.Received(2).SendAsync(Arg.Is<RtspRequest>(r => r.ContentType == RtspContentTypes.DmapTagged), Arg.Any<CancellationToken>());
        Assert.False(session.DmapRefused);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("0", 0)]
    [InlineData("11025", 11025)]
    [InlineData(" 2205 ", 2205)]
    [InlineData("garbage", 0)]
    public async Task Record_returns_audio_latency(string? header, int expected)
    {
        // Receiver A's RECORD answer has no Audio-Latency header at all (live, 2026-10-09).
        var (session, connection) = Create();
        var headers = new Dictionary<string, string>();
        if (header is not null)
        {
            headers[RtspHeaders.AudioLatency] = header;
        }

        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(new RtspResponse("RTSP/1.0", 200, "OK", headers, ReadOnlyMemory<byte>.Empty));
        Assert.Equal(expected, await session.RecordAsync(None));
    }

    [Fact]
    public async Task Stream_setup_needs_both_ports()
    {
        var (session, connection) = Create();
        var stream = new PlistDictionary { ["type"] = 96L, ["dataPort"] = 7003L, ["controlPort"] = 0L };
        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(200, new PlistDictionary { ["streams"] = new List<object?> { stream } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, new byte[32], 6001), None));

        stream["controlPort"] = 7004L;
        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(200, new PlistDictionary { ["streams"] = new List<object?> { stream } }));
        var result = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, new byte[32], 6001), None);
        Assert.Equal((7003, 7004, (uint?)null), (result.DataPort, result.ControlPort, result.StreamId));
        Assert.Empty(session.StreamIds);
    }

    [Fact]
    public async Task Stream_setup_on_an_unencrypted_connection_is_refused()
    {
        // shk travels inside the stream SETUP body (SPEC-control.md 4.4): never send it in plaintext.
        var (session, connection) = Create();
        connection.IsEncrypted.Returns(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, new byte[32], 6001), None));
        await connection.DidNotReceiveWithAnyArgs().SendExpectOkAsync(default!, default);
        await connection.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    [Fact]
    public async Task Failed_teardown_keeps_the_stream_ids_for_a_retry()
    {
        // D7: a per-stream TEARDOWN that times out must be retried with the per-stream form, not only {}.
        var (session, connection) = Create();
        var stream = new PlistDictionary { ["type"] = 96L, ["dataPort"] = 7003L, ["controlPort"] = 7004L, ["streamID"] = 5L };
        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(200, new PlistDictionary { ["streams"] = new List<object?> { stream } }));
        await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, new byte[32], 6001), None);
        Assert.Equal([5u], session.StreamIds);

        connection.SendAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns<Task<RtspResponse>>(_ => throw new TimeoutException("no answer"));
        await Assert.ThrowsAsync<TimeoutException>(() => session.TeardownWithResultAsync(None));
        Assert.Equal([5u], session.StreamIds);

        connection.SendAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(200));
        var result = await session.TeardownWithResultAsync(None);
        Assert.Single(result.StreamResponses);
        Assert.Equal(200, result.SessionResponse!.StatusCode);
        Assert.Empty(session.StreamIds);
        await connection.Received(2).SendAsync(Arg.Is<RtspRequest>(r => IsStreamTeardown(r)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_volume_without_a_volume_line_is_reported()
    {
        var (session, connection) = Create();
        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(new RtspResponse("RTSP/1.0", 200, "OK", new Dictionary<string, string>(), "\r\n"u8.ToArray()));
        Assert.Null(await session.TryGetVolumeAsync(None));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.GetVolumeAsync(None));

        // Receiver A's shape: no leading blank line (live, 2026-10-09).
        connection.SendExpectOkAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(new RtspResponse("RTSP/1.0", 200, "OK", new Dictionary<string, string>(), "volume: -24.000000\r\n"u8.ToArray()));
        Assert.Equal(-24.0, await session.GetVolumeAsync(None));
    }

    [Fact]
    public async Task Auth_setup_ignores_an_error_answer()
    {
        var (session, connection) = Create();
        connection.SendAsync(Arg.Any<RtspRequest>(), Arg.Any<CancellationToken>()).Returns(Answer(403));
        await session.AuthSetupAsync(None);
        await connection.Received(1).SendAsync(Arg.Is<RtspRequest>(r => r.Uri == "/auth-setup" && r.Body.Length == 33 && r.Body.ToArray()[0] == 1), Arg.Any<CancellationToken>());
    }

    private static bool IsStreamTeardown(RtspRequest request) =>
        request.Method == "TEARDOWN" && BinaryPlist.DecodeDictionary(request.Body.Span).ContainsKey("streams");

    private static (ReceiverSession Session, IRtspConnection Connection) Create()
    {
        var connection = Substitute.For<IRtspConnection>();
        connection.SessionUri.Returns("rtsp://203.0.113.150/1");
        connection.SessionId.Returns(1u);
        connection.IsEncrypted.Returns(true);
        connection.LocalAddress.Returns(IPAddress.Parse("203.0.113.150"));
        connection.RemoteEndPoint.Returns(new IPEndPoint(IPAddress.Parse("192.0.2.26"), 7000));
        connection.Closed.Returns(new TaskCompletionSource<string>().Task);
        var receiver = new DiscoveredReceiver("id", "Test Speaker ABCD", [IPAddress.Parse("192.0.2.26")], 7000, AirplayTxtRecord.Parse(["features=0x445F8A00,0x1C340", "flags=0x404", "model=Test Speaker ABCD"]), null, DateTimeOffset.UtcNow);
        var session = new ReceiverSession(receiver, connection, Substitute.For<IPairingClient>(), ReceiverQuirks.Default, NullLogger<ReceiverSession>.Instance);
        return (session, connection);
    }

    private static Task<RtspResponse> Answer(int status, PlistDictionary? body = null)
    {
        var headers = new Dictionary<string, string>();
        var bytes = body is null ? ReadOnlyMemory<byte>.Empty : BinaryPlist.Encode(body);
        if (body is not null)
        {
            headers[RtspHeaders.ContentType] = RtspContentTypes.BinaryPlist;
        }

        return Task.FromResult(new RtspResponse("RTSP/1.0", status, status == 200 ? "OK" : "Error", headers, bytes));
    }
}
