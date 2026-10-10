using System.Text;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Xunit;

namespace Airplay2.Sender.Tests.Rtsp;

public class RtspMessageCodecTests
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static byte[] SpeakerAResponse()
    {
        var head = Encoding.ASCII.GetBytes(SpeakerACapture.ProbeSection('A', "response head"));
        return [.. head, .. SpeakerACapture.InfoBody];
    }

    [Fact]
    public void GET_info_is_byte_exact_to_the_probe_request()
    {
        var expected = SpeakerACapture.ProbeSection('A', "request");
        var encoded = RtspMessageCodec.EncodeRequest(RtspRequest.Create("GET", "/info"), 0, SpeakerACapture.ProbeIdentity);
        Assert.Equal(expected, Text(encoded));
        Assert.StartsWith("GET /info RTSP/1.0\r\nCSeq: 0\r\nUser-Agent: AirPlay/550.10\r\nDACP-ID: F27C859F90C330F8\r\n", expected);
    }

    [Fact]
    public void GET_info_with_qualifier_carries_the_probe_headers_and_a_70_byte_bplist()
    {
        var probeHead = SpeakerACapture.ProbeSection('C', "request");
        var body = BinaryPlist.Encode(new PlistDictionary { ["qualifier"] = new List<object?> { "txtAirPlay" } });
        var request = RtspRequest.WithBody(
            "GET",
            "/info",
            RtspContentTypes.BinaryPlist,
            body,
            new Dictionary<string, string> { ["X-Apple-ProtocolVersion"] = "1" });
        var encoded = RtspMessageCodec.EncodeRequest(request, 0, SpeakerACapture.ProbeIdentity);

        Assert.True(RtspMessageCodec.TryParseRequest(encoded, out var parsed, out var consumed));
        Assert.Equal(encoded.Length, consumed);
        Assert.Equal(70, body.Length); // what plistlib produced in the probe (Content-Length: 70)

        // Same header set as the probe (our order puts Content-Type/Length before the extra headers, as pyatv does).
        var probeLines = probeHead.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
        var ourLines = Text(encoded)[..Text(encoded).IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(probeLines, ourLines);
        Assert.Equal(body, parsed!.Body.ToArray());
        Assert.Equal(["txtAirPlay"], parsed.PlistBody().GetArray("qualifier")!.Cast<string>());
    }

    [Fact]
    public void Pin_start_has_content_type_and_zero_length_and_no_body()
    {
        var request = RtspRequest.WithEmptyBody(
            "POST",
            "/pair-pin-start",
            RtspContentTypes.OctetStream,
            new Dictionary<string, string> { [RtspHeaders.XAppleHkp] = "4" });
        var identity = new RtspClientIdentity("00000000000000AB", 7, "AirPlay/550.10");
        var text = Text(RtspMessageCodec.EncodeRequest(request, 3, identity));
        Assert.Equal(
            "POST /pair-pin-start RTSP/1.0\r\nCSeq: 3\r\nUser-Agent: AirPlay/550.10\r\nDACP-ID: 00000000000000AB\r\n" +
            "Active-Remote: 7\r\nClient-Instance: 00000000000000AB\r\nContent-Type: application/octet-stream\r\n" +
            "Content-Length: 0\r\nX-Apple-HKP: 4\r\n\r\n",
            text);
    }

    [Fact]
    public void No_content_type_and_no_body_writes_neither_header()
    {
        var text = Text(RtspMessageCodec.EncodeRequest(RtspRequest.Create("POST", "/feedback"), 1, SpeakerACapture.ProbeIdentity));
        Assert.DoesNotContain("Content-Type", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", text);
    }

    [Fact]
    public void Body_without_content_type_writes_only_the_length()
    {
        var request = new RtspRequest("SET_PARAMETER", "rtsp://10.0.0.2/1", NoHeaders, null, new byte[] { 1, 2, 3 });
        var encoded = RtspMessageCodec.EncodeRequest(request, 1, SpeakerACapture.ProbeIdentity);
        var text = Text(encoded);
        Assert.DoesNotContain("Content-Type", text, StringComparison.Ordinal);
        Assert.Contains("\r\nContent-Length: 3\r\n", text, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1, 2, 3 }, encoded[^3..]);
    }

    [Fact]
    public void Client_name_is_written_unless_the_request_has_its_own()
    {
        var text = Text(RtspMessageCodec.EncodeRequest(RtspRequest.Create("GET", "/info"), 0, SpeakerACapture.ProbeIdentity, "Bridge"));
        Assert.Contains("\r\nX-Apple-Client-Name: Bridge\r\n", text, StringComparison.Ordinal);

        var own = new RtspRequest("GET", "/info", new Dictionary<string, string> { ["x-apple-client-name"] = "Mine" }, null, ReadOnlyMemory<byte>.Empty);
        text = Text(RtspMessageCodec.EncodeRequest(own, 0, SpeakerACapture.ProbeIdentity, "Bridge"));
        Assert.DoesNotContain("Bridge", text, StringComparison.Ordinal);
        Assert.Contains("x-apple-client-name: Mine", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("content-length")]
    [InlineData("CSeq")]
    [InlineData("User-Agent")]
    [InlineData("DACP-ID")]
    [InlineData("active-remote")]
    [InlineData("Client-Instance")]
    public void Reserved_headers_are_rejected(string name)
    {
        var request = new RtspRequest("GET", "/info", new Dictionary<string, string> { [name] = "x" }, null, ReadOnlyMemory<byte>.Empty);
        Assert.Throws<ArgumentException>(() => RtspMessageCodec.EncodeRequest(request, 0, SpeakerACapture.ProbeIdentity));
    }

    [Fact]
    public void Line_breaks_in_tokens_and_values_are_rejected()
    {
        var injected = new RtspRequest("GET", "/info", new Dictionary<string, string> { ["X-Test"] = "a\r\nEvil: 1" }, null, ReadOnlyMemory<byte>.Empty);
        Assert.Throws<ArgumentException>(() => RtspMessageCodec.EncodeRequest(injected, 0, SpeakerACapture.ProbeIdentity));
        Assert.Throws<ArgumentException>(() => RtspMessageCodec.EncodeRequest(RtspRequest.Create("GET", "/in fo"), 0, SpeakerACapture.ProbeIdentity));
        Assert.Throws<ArgumentException>(() => RtspMessageCodec.EncodeRequest(RtspRequest.Create("GET\r\n", "/info"), 0, SpeakerACapture.ProbeIdentity));
    }

    [Fact]
    public void Random_identity_is_16_upper_case_hex_digits_and_client_instance_matches()
    {
        for (var i = 0; i < 200; i++)
        {
            var identity = RtspClientIdentity.CreateRandom();
            Assert.Matches("^[0-9A-F]{16}$", identity.DacpId);
            Assert.Equal(RtspClientIdentity.DefaultUserAgent, identity.UserAgent);
            var text = Text(RtspMessageCodec.EncodeRequest(RtspRequest.Create("GET", "/info"), 0, identity));
            Assert.Contains($"\r\nClient-Instance: {identity.DacpId}\r\n", text, StringComparison.Ordinal);
            Assert.Contains($"\r\nActive-Remote: {identity.ActiveRemote}\r\n", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parses_the_captured_speaker_A_response()
    {
        var raw = SpeakerAResponse();
        Assert.True(RtspMessageCodec.TryParseResponse(raw, out var response, out var consumed));
        Assert.Equal(raw.Length, consumed);
        Assert.Equal("RTSP/1.0", response!.Protocol);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("OK", response.ReasonPhrase);
        Assert.True(response.IsSuccess);
        Assert.Equal(0, response.CSeq);
        Assert.Equal("AirTunes/366.0", response.Header("server"));
        Assert.Equal(RtspContentTypes.BinaryPlist, response.ContentType);
        Assert.Equal("525", response.Header("CONTENT-LENGTH"));
        Assert.Equal(SpeakerACapture.InfoBody, response.Body.ToArray());
        Assert.Equal("Test Speaker ABCD", response.PlistBody().GetString("name"));
    }

    [Fact]
    public void Parses_the_HTTP_1_1_variant()
    {
        var head = SpeakerACapture.ProbeSection('B', "response head");
        Assert.StartsWith("HTTP/1.1 200 OK", head);
        byte[] raw = [.. Encoding.ASCII.GetBytes(head), .. SpeakerACapture.InfoBody];
        Assert.True(RtspMessageCodec.TryParseResponse(raw, out var response, out _));
        Assert.Equal("HTTP/1.1", response!.Protocol);
        Assert.Equal(525, response.Body.Length);
    }

    [Fact]
    public void Every_prefix_is_incomplete()
    {
        var raw = SpeakerAResponse();
        for (var n = 0; n < raw.Length; n++)
        {
            Assert.False(RtspMessageCodec.TryParseResponse(raw.AsSpan(0, n), out var response, out var consumed));
            Assert.Null(response);
            Assert.Equal(0, consumed);
        }
    }

    [Fact]
    public void Back_to_back_messages_are_parsed_one_at_a_time()
    {
        var second = "RTSP/1.0 470 Connection Authorization Required\r\ncseq: 1\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        byte[] raw = [.. SpeakerAResponse(), .. second];
        Assert.True(RtspMessageCodec.TryParseResponse(raw, out var first, out var consumed));
        Assert.Equal(200, first!.StatusCode);
        Assert.True(RtspMessageCodec.TryParseResponse(raw.AsSpan(consumed), out var next, out var consumed2));
        Assert.Equal(470, next!.StatusCode);
        Assert.Equal("Connection Authorization Required", next.ReasonPhrase);
        Assert.Equal(1, next.CSeq);
        Assert.Equal(raw.Length, consumed + consumed2);
    }

    [Fact]
    public void Header_names_are_case_insensitive_and_whitespace_is_trimmed()
    {
        var raw = "RTSP/1.0 200 OK\r\ncontent-length:3\r\nCSEQ:   12  \r\nX-Dup: a\r\nx-dup: b\r\nnot a header line\r\n\r\nabc"u8.ToArray();
        Assert.True(RtspMessageCodec.TryParseResponse(raw, out var response, out var consumed));
        Assert.Equal(raw.Length, consumed);
        Assert.Equal(12, response!.CSeq);
        Assert.Equal("abc", Encoding.ASCII.GetString(response.Body.Span));
        Assert.Equal("a, b", response.Header("X-DUP"));
    }

    [Fact]
    public void Reason_phrase_may_be_missing()
    {
        Assert.True(RtspMessageCodec.TryParseResponse("RTSP/1.0 501\r\n\r\n"u8, out var response, out _));
        Assert.Equal(501, response!.StatusCode);
        Assert.Equal(string.Empty, response.ReasonPhrase);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public void Leading_empty_lines_are_skipped()
    {
        var raw = "\r\n\r\nRTSP/1.0 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        Assert.True(RtspMessageCodec.TryParseResponse(raw, out var response, out var consumed));
        Assert.Equal(200, response!.StatusCode);
        Assert.Equal(raw.Length, consumed);
    }

    [Fact]
    public void Missing_content_length_with_a_bplist_body_is_sniffed_by_the_trailer()
    {
        var head = "RTSP/1.0 200 OK\r\nContent-Type: application/x-apple-binary-plist\r\nCSeq: 4\r\n\r\n"u8.ToArray();
        var next = "RTSP/1.0 200 OK\r\nCSeq: 5\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        var body = SpeakerACapture.InfoBody;
        byte[] raw = [.. head, .. body, .. next];

        // incomplete bplist: wait for more
        Assert.False(RtspMessageCodec.TryParseResponse(raw.AsSpan(0, head.Length + 100), out _, out _));
        Assert.False(RtspMessageCodec.TryParseResponse(raw.AsSpan(0, head.Length + 3), out _, out _));

        Assert.True(RtspMessageCodec.TryParseResponse(raw, out var response, out var consumed));
        Assert.Equal(head.Length + body.Length, consumed);
        Assert.Equal(body, response!.Body.ToArray());
        Assert.True(RtspMessageCodec.TryParseResponse(raw.AsSpan(consumed), out var second, out _));
        Assert.Equal(5, second!.CSeq);
    }

    [Fact]
    public void Bplist_trailer_length_matches_every_vector()
    {
        Assert.Equal(525, RtspMessageCodec.FindBplistLength(SpeakerACapture.InfoBody));
        Assert.Equal(909, RtspMessageCodec.FindBplistLength(SpeakerACapture.Stage1InfoBody));
        var small = BinaryPlist.Encode(new PlistDictionary { ["streams"] = new List<object?>() });
        Assert.Equal(small.Length, RtspMessageCodec.FindBplistLength([.. small, .. "RTSP/1.0"u8.ToArray()]));
        Assert.Equal(-1, RtspMessageCodec.FindBplistLength(small.AsSpan(0, small.Length - 1)));
    }

    [Fact]
    public void Missing_content_length_without_bplist_means_no_body()
    {
        var raw = "RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n"u8;
        Assert.True(RtspMessageCodec.TryParseResponse(raw, out var response, out var consumed));
        Assert.True(response!.Body.IsEmpty);
        Assert.Equal(raw.Length, consumed);
    }

    [Fact]
    public void Plist_body_is_found_behind_junk()
    {
        byte[] body = [.. "\r\n\0junk"u8.ToArray(), .. SpeakerACapture.InfoBody];
        var response = new RtspResponse("RTSP/1.0", 200, "OK", new Dictionary<string, string>(), body);
        Assert.Equal("Test Speaker ABCD", response.PlistBody().GetString("name"));
        Assert.Throws<FormatException>(() => new RtspResponse("RTSP/1.0", 200, "OK", new Dictionary<string, string>(), "abc"u8.ToArray()).PlistBody());
    }

    [Theory]
    [InlineData("FOO 200 OK\r\n\r\n")]
    [InlineData("RTSP/1.0 2000 OK\r\n\r\n")]
    [InlineData("RTSP/1.0 abc OK\r\n\r\n")]
    [InlineData("RTSP/1.0\r\n\r\n")]
    [InlineData("RTSP/1.0 200 OK\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("RTSP/1.0 200 OK\r\nContent-Length: 12x\r\n\r\n")]
    [InlineData("RTSP/1.0 200 OK\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\nab")]
    [InlineData("RTSP/1.0 200 OK\r\nContent-Length: 999999999\r\n\r\n")]
    public void Malformed_responses_throw(string raw)
    {
        Assert.Throws<InvalidDataException>(() => RtspMessageCodec.TryParseResponse(Encoding.ASCII.GetBytes(raw), out _, out _));
    }

    [Fact]
    public void An_endless_head_throws_instead_of_buffering_forever()
    {
        var raw = Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nX: " + new string('a', RtspMessageCodec.MaxHeadBytes + 10));
        Assert.Throws<InvalidDataException>(() => RtspMessageCodec.TryParseResponse(raw, out _, out _));
    }

    [Fact]
    public void Parses_an_event_channel_request_without_CSeq()
    {
        var body = BinaryPlist.Encode(new PlistDictionary { ["type"] = "updateInfo", ["value"] = new PlistDictionary { ["name"] = "x" } });
        byte[] raw =
        [
            .. Encoding.ASCII.GetBytes($"POST /command RTSP/1.0\r\nContent-Length: {body.Length}\r\nContent-Type: application/x-apple-binary-plist\r\n\r\n"),
            .. body,
        ];
        Assert.True(RtspMessageCodec.TryParseRequest(raw, out var request, out var consumed));
        Assert.Equal(raw.Length, consumed);
        Assert.Equal("POST", request!.Method);
        Assert.Equal("/command", request.Uri);
        Assert.Equal("RTSP/1.0", request.Protocol);
        Assert.Null(request.CSeq);
        Assert.Equal("updateInfo", request.PlistBody().GetString("type"));

        Assert.True(RtspMessageCodec.TryParseRequest("GET /info HTTP/1.1\r\nCSeq: 0\r\n\r\n"u8, out var http, out _));
        Assert.Equal("HTTP/1.1", http!.Protocol);
        Assert.Equal(0, http.CSeq);
        Assert.Throws<InvalidDataException>(() => RtspMessageCodec.TryParseRequest("GET /info\r\n\r\n"u8, out _, out _));
        Assert.Throws<InvalidDataException>(() => RtspMessageCodec.TryParseRequest("GET /info FOO/1\r\n\r\n"u8, out _, out _));
    }

    [Fact]
    public void Encodes_the_captured_response_byte_exact()
    {
        var headers = new OrderedDictionary<string, string>
        {
            [RtspHeaders.ContentType] = RtspContentTypes.BinaryPlist,
            [RtspHeaders.Server] = "AirTunes/366.0",
            [RtspHeaders.CSeq] = "0",
        };
        var encoded = RtspMessageCodec.EncodeResponse("RTSP/1.0", 200, "OK", headers, SpeakerACapture.InfoBody);
        Assert.Equal(SpeakerAResponse(), encoded);
        Assert.Throws<ArgumentException>(() => RtspMessageCodec.EncodeResponse(
            "RTSP/1.0", 200, "OK", new Dictionary<string, string> { ["content-length"] = "1" }, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Empty_response_always_carries_content_length_zero()
    {
        var text = Text(RtspMessageCodec.EncodeResponse("RTSP/1.0", 200, "OK", new Dictionary<string, string> { ["Audio-Latency"] = "0" }, ReadOnlyMemory<byte>.Empty));
        Assert.Equal("RTSP/1.0 200 OK\r\nContent-Length: 0\r\nAudio-Latency: 0\r\n\r\n", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_reason_writes_no_trailing_space(string? reason)
    {
        var encoded = RtspMessageCodec.EncodeResponse("RTSP/1.0", 200, reason, NoHeaders, ReadOnlyMemory<byte>.Empty);
        Assert.Equal("RTSP/1.0 200\r\nContent-Length: 0\r\n\r\n", Text(encoded));
        Assert.True(RtspMessageCodec.TryParseResponse(encoded, out var parsed, out _));
        Assert.Equal(string.Empty, parsed!.ReasonPhrase);
    }

    [Fact]
    public void Bare_request_follows_the_content_rule_and_validates_headers()
    {
        var plist = BinaryPlist.Encode(new PlistDictionary { ["type"] = "updateInfo" });
        var withCSeq = Text(RtspMessageCodec.EncodeBareRequest("POST", "/command", 0, RtspContentTypes.BinaryPlist, NoHeaders, plist));
        Assert.StartsWith($"POST /command RTSP/1.0\r\nCSeq: 0\r\nContent-Type: {RtspContentTypes.BinaryPlist}\r\nContent-Length: {plist.Length}\r\n\r\nbplist00", withCSeq);

        var bare = Text(RtspMessageCodec.EncodeBareRequest("POST", "/command", null, null, new Dictionary<string, string> { ["X-Test"] = "1" }, default));
        Assert.Equal("POST /command RTSP/1.0\r\nX-Test: 1\r\n\r\n", bare);

        Assert.Throws<ArgumentException>(() => RtspMessageCodec.EncodeBareRequest("POST", "/command", null, null, new Dictionary<string, string> { ["X-Test"] = "a\r\nInjected: 1" }, default));
        Assert.Throws<ArgumentException>(() => RtspMessageCodec.EncodeBareRequest("POST", "/command", null, null, new Dictionary<string, string> { ["cseq"] = "5" }, default));
        Assert.Throws<ArgumentException>(() => RtspMessageCodec.EncodeBareRequest("POST", "/com mand", null, null, NoHeaders, default));
    }

    [Fact]
    public void Classify_tells_responses_from_requests()
    {
        Assert.Equal(RtspMessageKind.None, RtspMessageCodec.Classify("\r\nRTS"u8));
        Assert.Equal(RtspMessageKind.Response, RtspMessageCodec.Classify("\r\nRTSP/1.0 200"u8));
        Assert.Equal(RtspMessageKind.Response, RtspMessageCodec.Classify("HTTP/1.1 200"u8));
        Assert.Equal(RtspMessageKind.Request, RtspMessageCodec.Classify("POST /command"u8));
    }
}
