using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Sender.Tests.Rtsp;

public class RtspConnectionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static RtspServerResponse Echo(RtspIncomingRequest request) =>
        RtspServerResponse.Bytes(RtspContentTypes.OctetStream, request.Body.ToArray());

    [Fact]
    public async Task GET_info_over_loopback_returns_the_vector_and_counts_CSeq()
    {
        var seen = new ConcurrentQueue<RtspIncomingRequest>();
        await using var pair = await LoopbackPair.StartAsync((_, request, _) =>
        {
            seen.Enqueue(request);
            return ValueTask.FromResult(RtspServerResponse.Bytes(RtspContentTypes.BinaryPlist, SpeakerACapture.InfoBody));
        });

        var first = await pair.Client.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        var second = await pair.Client.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);

        Assert.Equal(SpeakerACapture.InfoBody, first.Body.ToArray());
        Assert.Equal("RTSP/1.0", first.Protocol);
        Assert.Equal(0, first.CSeq);
        Assert.Equal(1, second.CSeq);
        Assert.Equal("AirTunes/366.0", first.Header("Server"));

        var requests = seen.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("RTSP/1.0", requests[0].Protocol);
        Assert.Equal(pair.Client.Identity.DacpId, requests[0].Header("DACP-ID"));
        Assert.Equal(pair.Client.Identity.DacpId, requests[0].Header("Client-Instance"));
        Assert.Equal(pair.Client.Identity.ActiveRemote.ToString(CultureInfo.InvariantCulture), requests[0].Header("Active-Remote"));
        Assert.Equal("AirPlay/550.10", requests[0].Header("User-Agent"));
        Assert.Null(requests[0].Header("Content-Length"));
        Assert.Equal("1", requests[1].Header("CSeq"));

        Assert.Equal(IPAddress.Loopback, pair.Client.LocalAddress);
        Assert.Equal($"rtsp://127.0.0.1/{pair.Client.SessionId}", pair.Client.SessionUri);
        Assert.False(pair.Client.IsEncrypted);
        Assert.False(pair.Client.Closed.IsCompleted);
    }

    [Fact]
    public async Task Pin_start_goes_out_with_octet_stream_and_zero_length()
    {
        var seen = new TaskCompletionSource<RtspIncomingRequest>();
        await using var pair = await LoopbackPair.StartAsync((_, request, _) =>
        {
            seen.TrySetResult(request);
            return ValueTask.FromResult(RtspServerResponse.Ok());
        });

        var request = RtspRequest.WithEmptyBody("POST", "/pair-pin-start", RtspContentTypes.OctetStream, new Dictionary<string, string> { [RtspHeaders.XAppleHkp] = "4" });
        await pair.Client.SendExpectOkAsync(request, CancellationToken.None);
        var received = await seen.Task.WaitAsync(Wait);
        Assert.Equal("application/octet-stream", received.ContentType);
        Assert.Equal("0", received.Header("Content-Length"));
        Assert.Equal("4", received.Header("X-Apple-HKP"));
        Assert.True(received.Body.IsEmpty);
    }

    [Fact]
    public async Task Non_2xx_is_returned_by_SendAsync_and_thrown_by_SendExpectOkAsync()
    {
        await using var pair = await LoopbackPair.StartAsync((_, _, _) => ValueTask.FromResult(RtspServerResponse.Status(RtspStatus.ConnectionAuthorizationRequired)));
        var response = await pair.Client.SendAsync(RtspRequest.Create("POST", "/pair-setup"), CancellationToken.None);
        Assert.Equal(470, response.StatusCode);
        Assert.Equal("Connection Authorization Required", response.ReasonPhrase);
        var ex = await Assert.ThrowsAsync<RtspStatusException>(() => pair.Client.SendExpectOkAsync(RtspRequest.Create("POST", "/pair-setup"), CancellationToken.None));
        Assert.Equal(470, ex.StatusCode);
        Assert.Equal("/pair-setup", ex.Request.Uri);
    }

    [Fact]
    public async Task Encrypted_channel_after_plaintext_pairing_response_with_large_bodies_both_ways()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var keys = HapChannelKeys.ForControl(secret);
        var serverSawEncrypted = new ConcurrentQueue<(string Uri, bool Encrypted)>();
        await using var pair = await LoopbackPair.StartAsync((connection, request, _) =>
        {
            serverSawEncrypted.Enqueue((request.Uri, connection.IsEncrypted));
            return ValueTask.FromResult(request.Uri switch
            {
                // M4: answered in plaintext, encryption switches on after it (sps:rtsp.c:1811-1817)
                "/pair-verify" => RtspServerResponse.Bytes(RtspContentTypes.OctetStream, new byte[] { 6, 1, 4 }) with { EnableEncryptionAfter = keys.Swapped() },
                "/echo" => Echo(request),
                "/big" => BigResponse(request, connection),
                _ => RtspServerResponse.Status(404),
            });
        });

        var m4 = await pair.Client.SendExpectOkAsync(RtspRequest.WithBody("POST", "/pair-verify", RtspContentTypes.OctetStream, new byte[] { 6, 1, 3 }), CancellationToken.None);
        Assert.Equal(new byte[] { 6, 1, 4 }, m4.Body.ToArray());
        Assert.False(pair.Client.IsEncrypted);
        pair.Client.EnableEncryption(keys);
        Assert.True(pair.Client.IsEncrypted);
        Assert.Throws<InvalidOperationException>(() => pair.Client.EnableEncryption(keys));

        // > 1024 bytes in both directions: the client splits at 1024, the server (default cap) too.
        var payload = RandomNumberGenerator.GetBytes(3000);
        var echoed = await pair.Client.SendExpectOkAsync(RtspRequest.WithBody("POST", "/echo", RtspContentTypes.OctetStream, payload), CancellationToken.None);
        Assert.Equal(payload, echoed.Body.ToArray());
        Assert.True(pair.Server.ReceivedFrames >= 3);
        Assert.Equal(HapFrameCipher.MaxSendFramePlaintext, pair.Server.LargestReceivedFrame);
        Assert.Equal(HapFrameCipher.MaxSendFramePlaintext, pair.Client.LargestReceivedFrame);

        // One 4000-byte frame from the receiver side must be accepted (receive rule, SPEC-control.md 2.5).
        var framesBefore = pair.Client.ReceivedFrames;
        var sentBefore = pair.Server.SentFrames;
        var big = await pair.Client.SendExpectOkAsync(RtspRequest.Create("GET", "/big"), CancellationToken.None);
        Assert.Equal(1, pair.Server.SentFrames - sentBefore);
        Assert.Equal(1, pair.Client.ReceivedFrames - framesBefore);
        Assert.Equal(4000, pair.Client.LargestReceivedFrame);
        Assert.Equal(BigBodyByte, big.Body.Span[0]);

        var order = serverSawEncrypted.ToArray();
        Assert.Equal(("/pair-verify", false), order[0]);
        Assert.Equal(("/echo", true), order[1]);
        Assert.False(pair.Client.Closed.IsCompleted);

        // the decrypted exchange is in the Debug log
        var log = pair.ClientLog.Dump();
        Assert.Contains("POST /echo RTSP/1.0", log, StringComparison.Ordinal);
        Assert.Contains("(encrypted", log, StringComparison.Ordinal);
    }

    private const byte BigBodyByte = 0x5A;

    /// <summary>A response whose encoded size is exactly 4000 bytes, sent as one frame.</summary>
    private static RtspServerResponse BigResponse(RtspIncomingRequest request, RtspServerConnection connection)
    {
        // The server writes Content-Length, Content-Type, Server, CSeq: size the body so the whole message is 4000 bytes.
        var headers = new OrderedDictionary<string, string>
        {
            [RtspHeaders.ContentType] = RtspContentTypes.OctetStream,
            [RtspHeaders.Server] = "AirTunes/366.0",
            [RtspHeaders.CSeq] = request.Header(RtspHeaders.CSeq)!,
        };
        var emptyLength = RtspMessageCodec.EncodeResponse(request.Protocol, 200, "OK", headers, ReadOnlyMemory<byte>.Empty).Length;
        var bodyLength = 4000 - emptyLength - 3; // "0" becomes a four-digit length
        var body = Enumerable.Repeat(BigBodyByte, bodyLength).ToArray();
        Assert.Equal(4000, RtspMessageCodec.EncodeResponse(request.Protocol, 200, "OK", headers, body).Length);
        connection.MaxSendFramePlaintext = HapFrameCipher.MaxReceiveFramePlaintext;
        return RtspServerResponse.Bytes(RtspContentTypes.OctetStream, body);
    }

    [Fact]
    public async Task Ciphertext_right_after_the_pairing_response_is_held_until_EnableEncryption()
    {
        // The receiver switches right after M4 and (unusually) sends at once; the first frame is 13 bytes long, so the
        // wire starts with 0x0D 0x00, which must not be stripped as a keep-alive CRLF while the client is still in plaintext.
        var keys = HapChannelKeys.ForControl(RandomNumberGenerator.GetBytes(32));
        var unsolicited = "RTSP/1.0 200 OK\r\nCSeq: 99\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        await using var pair = await LoopbackPair.StartAsync(async (connection, request, ct) =>
        {
            if (request.Uri != "/pair-verify")
            {
                return Echo(request);
            }

            var m4 = RtspMessageCodec.EncodeResponse(request.Protocol, 200, "OK", new Dictionary<string, string> { ["CSeq"] = request.Header("CSeq")! }, new byte[] { 6, 1, 4 });
            await connection.WriteRawAsync(m4, null, ct);
            connection.EnableEncryption(keys.Swapped());
            await connection.WriteRawAsync(unsolicited, [13], ct);
            return RtspServerResponse.None();
        });

        var m4 = await pair.Client.SendExpectOkAsync(RtspRequest.Create("POST", "/pair-verify"), CancellationToken.None);
        Assert.Equal(new byte[] { 6, 1, 4 }, m4.Body.ToArray());
        await Task.Delay(300); // let the frames arrive while the client is still plaintext
        pair.Client.EnableEncryption(keys);
        Assert.Equal(2, pair.Client.ReceivedFrames);
        Assert.Contains(pair.ClientLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("followed the pairing response", StringComparison.Ordinal));
        Assert.Contains(pair.ClientLog.Entries, e => e.Message.Contains("discarding response 200 (CSeq 99)", StringComparison.Ordinal));

        var echoed = await pair.Client.SendExpectOkAsync(RtspRequest.WithBody("POST", "/echo", RtspContentTypes.OctetStream, new byte[] { 7 }), CancellationToken.None);
        Assert.Equal(new byte[] { 7 }, echoed.Body.ToArray());
        Assert.False(pair.Client.Closed.IsCompleted);
    }

    [Fact]
    public async Task Response_split_into_frames_that_do_not_align_with_the_message()
    {
        var keys = HapChannelKeys.ForControl(RandomNumberGenerator.GetBytes(32));
        await using var pair = await LoopbackPair.StartAsync(async (connection, request, ct) =>
        {
            if (request.Uri == "/pair-verify")
            {
                return RtspServerResponse.Ok() with { EnableEncryptionAfter = keys.Swapped() };
            }

            var body = Encoding.ASCII.GetBytes(new string('x', 1500));
            var bytes = RtspMessageCodec.EncodeResponse(request.Protocol, 200, "OK", new Dictionary<string, string> { ["CSeq"] = request.Header("CSeq")! }, body);
            await connection.WriteRawAsync(bytes, [7, 1, 30, 1024, 2], ct);
            return RtspServerResponse.None();
        });

        await pair.Client.SendExpectOkAsync(RtspRequest.Create("POST", "/pair-verify"), CancellationToken.None);
        pair.Client.EnableEncryption(keys);
        var response = await pair.Client.SendExpectOkAsync(RtspRequest.Create("GET", "/split"), CancellationToken.None);
        Assert.Equal(1500, response.Body.Length);
        Assert.Equal(6, pair.Client.ReceivedFrames); // 5 explicit frames + the rest
    }

    [Fact]
    public async Task Plaintext_response_written_in_small_pieces()
    {
        await using var pair = await LoopbackPair.StartAsync(async (connection, request, ct) =>
        {
            var head = Encoding.ASCII.GetBytes(SpeakerACapture.ProbeSection('A', "response head").Replace("CSeq: 0", "CSeq: " + request.Header("CSeq"), StringComparison.Ordinal));
            byte[] bytes = [.. head, .. SpeakerACapture.InfoBody];
            await connection.WriteRawAsync(bytes, [head.Length, 100, 300], ct);
            return RtspServerResponse.None();
        });

        var response = await pair.Client.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        Assert.Equal(SpeakerACapture.InfoBody, response.Body.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mismatched_or_missing_CSeq_falls_back_to_the_outstanding_request(bool omit)
    {
        await using var pair = await LoopbackPair.StartAsync((_, request, _) =>
            ValueTask.FromResult(Echo(request) with { CSeqOverride = omit ? null : 99, OmitCSeq = omit }));
        var response = await pair.Client.SendExpectOkAsync(RtspRequest.WithBody("POST", "/x", RtspContentTypes.OctetStream, new byte[] { 42 }), CancellationToken.None);
        Assert.Equal(new byte[] { 42 }, response.Body.ToArray());
        Assert.Equal(omit ? null : 99, response.CSeq);
        if (!omit)
        {
            Assert.Contains(pair.ClientLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("matches no request", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_late_response_after_a_timeout_is_not_handed_to_the_next_request()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = await LoopbackPair.StartAsync(
            async (_, request, ct) =>
            {
                if (request.Uri == "/slow")
                {
                    await release.Task.WaitAsync(ct);
                }

                return RtspServerResponse.Bytes("text/plain", Encoding.ASCII.GetBytes(request.Uri));
            },
            new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(1) });

        // /slow times out at 1 s and is answered only after that; the server handles requests in order, so its late
        // answer (CSeq 0) precedes the answer to /fast and must be discarded, not handed to /fast by the FIFO fallback.
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => pair.Client.SendAsync(RtspRequest.Create("GET", "/slow"), CancellationToken.None));
        Assert.True(watch.ElapsedMilliseconds >= 900, $"timed out after {watch.ElapsedMilliseconds} ms");
        release.SetResult();

        var fast = await pair.Client.SendAsync(RtspRequest.Create("GET", "/fast"), CancellationToken.None).WaitAsync(Wait);
        Assert.Equal("/fast", Encoding.ASCII.GetString(fast.Body.Span));
        Assert.Equal(1, fast.CSeq);
        Assert.False(pair.Client.Closed.IsCompleted);
        Assert.Contains(pair.ClientLog.Entries, e => e.Message.Contains("abandoned CSeq 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unanswered_request_times_out_and_the_connection_stays_usable()
    {
        await using var pair = await LoopbackPair.StartAsync(
            (_, request, _) => ValueTask.FromResult(request.Uri == "/never" ? RtspServerResponse.None() : RtspServerResponse.Ok()),
            new RtspConnectionOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) });
        await Assert.ThrowsAsync<TimeoutException>(() => pair.Client.SendAsync(RtspRequest.Create("GET", "/never"), CancellationToken.None));
        var ok = await pair.Client.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(1, ok.CSeq);
    }

    [Fact]
    public async Task Caller_cancellation_abandons_the_request_without_closing()
    {
        await using var pair = await LoopbackPair.StartAsync((_, request, _) =>
            ValueTask.FromResult(request.Uri == "/never" ? RtspServerResponse.None() : RtspServerResponse.Ok()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pair.Client.SendAsync(RtspRequest.Create("GET", "/never"), cts.Token));
        Assert.Equal(200, (await pair.Client.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None)).StatusCode);
        Assert.False(pair.Client.Closed.IsCompleted);
    }

    [Fact]
    public async Task Receiver_closing_fails_the_pending_request_and_completes_Closed()
    {
        await using var pair = await LoopbackPair.StartAsync((_, _, _) => ValueTask.FromResult(RtspServerResponse.None() with { CloseAfter = true }));
        var ex = await Assert.ThrowsAsync<IOException>(() => pair.Client.SendAsync(RtspRequest.Create("TEARDOWN", "rtsp://x/1"), CancellationToken.None));
        Assert.Contains("closed", ex.Message, StringComparison.Ordinal);
        var reason = await pair.Client.Closed.WaitAsync(Wait);
        Assert.Equal("receiver closed the connection", reason);
        await Assert.ThrowsAsync<IOException>(() => pair.Client.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None));
    }

    [Fact]
    public async Task Idle_close_by_the_receiver_completes_Closed()
    {
        await using var pair = await LoopbackPair.StartAsync((_, _, _) => ValueTask.FromResult(RtspServerResponse.Ok()));
        await pair.Server.CloseAsync();
        Assert.Equal("receiver closed the connection", await pair.Client.Closed.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_corrupt_frame_closes_the_connection()
    {
        var keys = HapChannelKeys.ForControl(RandomNumberGenerator.GetBytes(32));
        await using var pair = await LoopbackPair.StartAsync((_, _, _) => ValueTask.FromResult(RtspServerResponse.Ok() with { EnableEncryptionAfter = keys.Swapped() }));
        await pair.Client.SendExpectOkAsync(RtspRequest.Create("POST", "/pair-verify"), CancellationToken.None);
        pair.Client.EnableEncryption(keys);
        await pair.Server.WriteWireAsync(new byte[] { 5, 0, 1, 2, 3, 4, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, CancellationToken.None);
        var reason = await pair.Client.Closed.WaitAsync(Wait);
        Assert.StartsWith("decryption failed", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_garbage_status_line_closes_the_connection_with_a_protocol_error()
    {
        await using var pair = await LoopbackPair.StartAsync((_, _, _) => ValueTask.FromResult(RtspServerResponse.Ok()));
        await pair.Server.WriteWireAsync("RTSP/1.0 abc\r\n\r\n"u8.ToArray(), CancellationToken.None);

        // Idle plaintext is held (it could be ciphertext after a pairing response); the next plaintext request parses it.
        await Assert.ThrowsAsync<IOException>(() => pair.Client.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None));
        var reason = await pair.Client.Closed.WaitAsync(Wait);
        Assert.StartsWith("protocol error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_request_from_the_receiver_is_ignored()
    {
        await using var pair = await LoopbackPair.StartAsync((_, _, _) => ValueTask.FromResult(RtspServerResponse.Ok()));
        await pair.Server.WriteWireAsync("POST /command RTSP/1.0\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), CancellationToken.None);
        var ok = await pair.Client.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        Assert.Equal(200, ok.StatusCode);
        Assert.Contains(pair.ClientLog.Entries, e => e.Message.Contains("unexpected request POST /command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_one_request_is_in_flight()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptAsync();
            var reader = new RtspStreamReader();
            var buffer = new byte[4096];
            var bytesAfterFirst = new List<int>();
            for (var answered = 0; answered < 2;)
            {
                var n = await socket.ReceiveAsync(buffer);
                reader.Append(buffer.AsSpan(0, n));
                while (reader.TryReadRequest(out var request))
                {
                    await Task.Delay(300);
                    bytesAfterFirst.Add(socket.Available + reader.BufferedPlaintext);
                    var response = RtspMessageCodec.EncodeResponse("RTSP/1.0", 200, "OK", new Dictionary<string, string> { ["CSeq"] = request!.Header("CSeq")! }, ReadOnlyMemory<byte>.Empty);
                    await socket.SendAsync(response);
                    answered++;
                }
            }

            return bytesAfterFirst;
        });

        await using var client = await RtspConnection.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, new RtspConnectionOptions(), NullLogger.Instance, CancellationToken.None);
        var a = client.SendAsync(RtspRequest.Create("GET", "/a"), CancellationToken.None);
        var b = client.SendAsync(RtspRequest.Create("GET", "/b"), CancellationToken.None);
        var responses = await Task.WhenAll(a, b).WaitAsync(Wait);
        Assert.Equal([0, 1], responses.Select(r => r.CSeq!.Value).Order());
        var pendingBytes = await server.WaitAsync(Wait);
        Assert.Equal(0, pendingBytes[0]); // the second request was not written while the first was unanswered
    }

    [Fact]
    public async Task Connect_retries_until_the_receiver_listens()
    {
        var port = FreePort();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connect = RtspConnection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), new RtspConnectionOptions { ConnectTimeout = TimeSpan.FromSeconds(15) }, NullLogger.Instance, CancellationToken.None);
        await Task.Delay(500);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(1);
        await using var client = await connect.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(port, client.RemoteEndPoint.Port);
    }

    [Fact]
    public async Task Connect_gives_up_after_the_connect_timeout()
    {
        var port = FreePort();
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => RtspConnection.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, port),
            new RtspConnectionOptions { ConnectTimeout = TimeSpan.FromMilliseconds(1500) },
            NullLogger.Instance,
            CancellationToken.None));
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.3, 6);
    }

    [Fact]
    public async Task Connect_honours_caller_cancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RtspConnection.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, FreePort()),
            new RtspConnectionOptions(),
            NullLogger.Instance,
            cts.Token));
    }

    [Fact]
    public async Task Dispose_completes_Closed_and_rejects_new_requests()
    {
        var pair = await LoopbackPair.StartAsync((_, _, _) => ValueTask.FromResult(RtspServerResponse.Ok()));
        await pair.Client.DisposeAsync();
        Assert.Equal("disposed", await pair.Client.Closed.WaitAsync(Wait));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pair.Client.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None));
        Assert.Equal("peer closed the connection", await pair.Server.Closed.WaitAsync(Wait));
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task Server_side_requests_reach_a_raw_client_as_on_the_event_channel()
    {
        // The fake's event-channel role: the server sends POST /command and reads the answer.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var accept = RtspServerConnection.AcceptAsync(listener, (_, _, _) => ValueTask.FromResult(RtspServerResponse.Status(501)), null, null, CancellationToken.None);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        await using var server = await accept;
        var keys = HapChannelKeys.ForEvents(RandomNumberGenerator.GetBytes(32));
        server.EnableEncryption(keys.Swapped());
        server.MaxSendFramePlaintext = 4000; // an updateInfo larger than 1024 in one frame

        var info = new PlistDictionary { ["type"] = "updateInfo", ["value"] = BinaryPlist.DecodeDictionary(SpeakerACapture.Stage1InfoBody) };
        var send = server.SendRequestAsync("POST", "/command", RtspContentTypes.BinaryPlist, BinaryPlist.Encode(info), null, CancellationToken.None);

        var reader = new RtspStreamReader();
        reader.EnableDecryption(new HapFrameCipher(keys));
        var buffer = new byte[8192];
        RtspIncomingRequest? request = null;
        while (request is null)
        {
            var n = await client.ReceiveAsync(buffer);
            Assert.True(n > 0);
            reader.Append(buffer.AsSpan(0, n));
            reader.TryReadRequest(out request);
        }

        Assert.Equal(1, reader.FramesDecrypted);
        Assert.True(reader.LargestFramePlaintext > 1024);
        Assert.Null(request.CSeq);
        Assert.Equal("updateInfo", request.PlistBody().GetString("type"));

        var answer = RtspMessageCodec.EncodeResponse("RTSP/1.0", 200, "OK", new Dictionary<string, string> { ["Audio-Latency"] = "0" }, ReadOnlyMemory<byte>.Empty);
        await client.SendAsync(new HapFrameCipher(keys).Encrypt(answer));
        var response = await send.WaitAsync(Wait);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("0", response.Header("Audio-Latency"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Server_side_request_timeout_does_not_desynchronise_later_requests(bool withCSeq)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var options = new RtspServerConnectionOptions { RequestTimeout = TimeSpan.FromMilliseconds(1500), SendCSeqOnRequests = withCSeq };
        var accept = RtspServerConnection.AcceptAsync(listener, (_, _, _) => ValueTask.FromResult(RtspServerResponse.Status(501)), options, null, CancellationToken.None);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        await using var server = await accept;
        var reader = new RtspStreamReader();
        var buffer = new byte[4096];

        async Task<RtspIncomingRequest> ReadRequestAsync()
        {
            RtspIncomingRequest? request = null;
            while (!reader.TryReadRequest(out request))
            {
                var n = await client.ReceiveAsync(buffer);
                Assert.True(n > 0);
                reader.Append(buffer.AsSpan(0, n));
            }

            return request!;
        }

        static byte[] Answer(RtspIncomingRequest request, string latency)
        {
            var headers = new Dictionary<string, string> { ["Audio-Latency"] = latency };
            if (request.Header("CSeq") is { } cseq)
            {
                headers["CSeq"] = cseq;
            }

            return RtspMessageCodec.EncodeResponse("RTSP/1.0", 200, "OK", headers, ReadOnlyMemory<byte>.Empty);
        }

        // The first request is not answered in time.
        var first = server.SendRequestAsync("POST", "/command", null, default, null, CancellationToken.None);
        var firstSeen = await ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        var second = server.SendRequestAsync("POST", "/command", null, default, null, CancellationToken.None);
        var secondSeen = await ReadRequestAsync();
        if (withCSeq)
        {
            // With CSeqs the late answer to the first request is recognised and dropped.
            Assert.Equal(0, firstSeen.CSeq);
            Assert.Equal(1, secondSeen.CSeq);
            await client.SendAsync(Answer(firstSeen, "1"));
        }
        else
        {
            Assert.Null(secondSeen.CSeq);
        }

        await client.SendAsync(Answer(secondSeen, "2"));
        var response = await second.WaitAsync(Wait);
        Assert.Equal("2", response.Header("Audio-Latency"));
    }

    [Fact]
    public async Task Server_CloseAsync_from_a_handler_does_not_deadlock()
    {
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = await LoopbackPair.StartAsync(async (connection, _, _) =>
        {
            await connection.CloseAsync();
            closing.SetResult();
            return RtspServerResponse.Ok();
        });

        await Assert.ThrowsAsync<IOException>(() => pair.Client.SendAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None));
        await closing.Task.WaitAsync(Wait);
        Assert.Equal("closed by the server", await pair.Server.Closed.WaitAsync(Wait));
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
