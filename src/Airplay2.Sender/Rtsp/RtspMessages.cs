using System.Security.Cryptography;
using Airplay2.Sender.Plist;

namespace Airplay2.Sender.Rtsp;

/// <summary>Content types used on the control connection (SPEC-control.md 2.3).</summary>
public static class RtspContentTypes
{
    public const string BinaryPlist = "application/x-apple-binary-plist";
    public const string OctetStream = "application/octet-stream";
    public const string Parameters = "text/parameters";
    public const string DmapTagged = "application/x-dmap-tagged";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string PeerListChanged = "/peer-list-changed";
    public const string PeerListChangedX = "/peer-list-changed-x";
}

/// <summary>Header names.</summary>
public static class RtspHeaders
{
    public const string CSeq = "CSeq";
    public const string UserAgent = "User-Agent";
    public const string DacpId = "DACP-ID";
    public const string ActiveRemote = "Active-Remote";
    public const string ClientInstance = "Client-Instance";
    public const string ContentType = "Content-Type";
    public const string ContentLength = "Content-Length";
    public const string XAppleHkp = "X-Apple-HKP";
    public const string XAppleClientName = "X-Apple-Client-Name";
    public const string RtpInfo = "RTP-Info";
    public const string Range = "Range";
    public const string AudioLatency = "Audio-Latency";
    public const string Connection = "Connection";
    public const string Server = "Server";
    public const string Public = "Public";
}

/// <summary>Status codes with AirPlay-specific meanings (SPEC-control.md 2.4).</summary>
public static class RtspStatus
{
    public const int Ok = 200;
    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int Forbidden = 403;
    public const int NotFound = 404;
    public const int Unavailable = 451;
    public const int NotPrincipal = 453;
    public const int ConnectionAuthorizationRequired = 470;
    public const int InternalError = 500;
    public const int NotImplemented = 501;
}

/// <summary>
/// The identity headers every request carries (SPEC-control.md 2.2): <c>DACP-ID</c> (upper-case hex of a random 64-bit
/// value; pyatv writes it unpadded, we zero-pad to 16 digits, both are fine), <c>Active-Remote</c> (random 32-bit decimal),
/// <c>Client-Instance</c> (= DACP-ID) and <c>User-Agent</c>.
/// </summary>
public sealed record RtspClientIdentity(string DacpId, uint ActiveRemote, string UserAgent)
{
    /// <summary>The User-Agent of the reference sender path.</summary>
    public const string DefaultUserAgent = "AirPlay/550.10";

    /// <summary>Creates a random identity.</summary>
    public static RtspClientIdentity CreateRandom(string? userAgent = null)
    {
        Span<byte> b = stackalloc byte[12];
        RandomNumberGenerator.Fill(b);
        var dacp = Convert.ToHexString(b[..8]);
        var active = BitConverter.ToUInt32(b[8..]);
        return new RtspClientIdentity(dacp, active, userAgent ?? DefaultUserAgent);
    }
}

/// <summary>
/// A request on the control connection. Identity headers, CSeq, Content-Type and Content-Length are added by the codec
/// with this rule (SPEC-control.md 2.2): <c>Content-Type</c> is written whenever <c>ContentType</c> is non-null, even with
/// an empty body (<c>POST /pair-pin-start</c> sends <c>application/octet-stream</c> and no body, as pyatv does);
/// <c>Content-Length</c> is written whenever <c>ContentType</c> is non-null or the body is non-empty (so the pin-start
/// request carries <c>Content-Length: 0</c>); neither is written when <c>ContentType</c> is null and the body is empty.
/// <c>Headers</c> must not contain these two names (the codec throws).
/// </summary>
/// <param name="Method">RTSP method or HTTP verb (<c>SETUP</c>, <c>POST</c>, ...).</param>
/// <param name="Uri">Session URI <c>rtsp://&lt;local ip&gt;/&lt;session id&gt;</c> for RTSP methods, a path for HTTP-style endpoints.</param>
/// <param name="Headers">Extra headers (RTP-Info, X-Apple-HKP, ...); never Content-Type/Content-Length.</param>
/// <param name="ContentType">Body content type; null only when neither Content-Type nor Content-Length must appear.</param>
/// <param name="Body">Body bytes, empty when none.</param>
public sealed record RtspRequest(string Method, string Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, ReadOnlyMemory<byte> Body)
{
    /// <summary>The protocol token written on every request: <c>RTSP/1.0</c> (SPEC-control.md 2.1 decision).</summary>
    public const string Protocol = "RTSP/1.0";

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>A request without body or extra headers (no Content-Type, no Content-Length).</summary>
    public static RtspRequest Create(string method, string uri) => new(method, uri, NoHeaders, null, ReadOnlyMemory<byte>.Empty);

    /// <summary>A request with a Content-Type but no body: written with <c>Content-Type</c> and <c>Content-Length: 0</c> (<c>POST /pair-pin-start</c>).</summary>
    public static RtspRequest WithEmptyBody(string method, string uri, string contentType, IReadOnlyDictionary<string, string>? headers = null) =>
        new(method, uri, headers ?? NoHeaders, contentType, ReadOnlyMemory<byte>.Empty);

    /// <summary>A request with a binary plist body.</summary>
    public static RtspRequest WithPlist(string method, string uri, object body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(method, uri, headers ?? NoHeaders, RtspContentTypes.BinaryPlist, BinaryPlist.Encode(body));

    /// <summary>A request with an arbitrary body.</summary>
    public static RtspRequest WithBody(string method, string uri, string contentType, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null) =>
        new(method, uri, headers ?? NoHeaders, contentType, body);
}

/// <summary>A response from the receiver (or a request from it on the event channel, see <see cref="RtspIncomingRequest"/>).</summary>
public sealed record RtspResponse(string Protocol, int StatusCode, string ReasonPhrase, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body)
{
    /// <summary>True for any 2xx.</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>Header lookup, case-insensitive.</summary>
    public string? Header(string name)
    {
        foreach (var (k, v) in Headers)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>Decodes the body as a binary plist dictionary; throws when it is not one.</summary>
    public PlistDictionary PlistBody() => BinaryPlist.DecodeDictionary(Body.Span);

    /// <summary>The <c>CSeq</c> header as an integer, or null.</summary>
    public int? CSeq => int.TryParse(Header(RtspHeaders.CSeq), out var v) ? v : null;
}

/// <summary>A request received from the receiver (event channel <c>POST /command</c>).</summary>
public sealed record RtspIncomingRequest(string Method, string Uri, string Protocol, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body)
{
    /// <summary>Header lookup, case-insensitive.</summary>
    public string? Header(string name)
    {
        foreach (var (k, v) in Headers)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}

/// <summary>Thrown when the receiver answers with a non-2xx status.</summary>
public sealed class RtspStatusException(RtspRequest request, RtspResponse response)
    : Exception($"{request.Method} {request.Uri} -> {response.StatusCode} {response.ReasonPhrase}")
{
    /// <summary>The request that failed.</summary>
    public RtspRequest Request { get; } = request;

    /// <summary>The receiver's answer.</summary>
    public RtspResponse Response { get; } = response;

    /// <summary>The status code.</summary>
    public int StatusCode => Response.StatusCode;
}

/// <summary>
/// Serialises requests and parses responses/requests in the HTTP/1.1-shaped RTSP framing (SPEC-control.md 2.1):
/// request line, headers, blank line, <c>Content-Length</c> body. Header names are matched case-insensitively; either
/// <c>RTSP/1.0</c> or <c>HTTP/1.1</c> is accepted in a status line.
/// </summary>
public static class RtspMessageCodec
{
    /// <summary>
    /// Writes the request: request line, <c>CSeq</c>, <c>User-Agent</c>, <c>DACP-ID</c>, <c>Active-Remote</c>,
    /// <c>Client-Instance</c>, then <c>Content-Type</c>/<c>Content-Length</c> per the rule on <see cref="RtspRequest"/>
    /// (Content-Type when set; Content-Length when Content-Type is set or the body is non-empty), the extra headers, then the body.
    /// </summary>
    public static byte[] EncodeRequest(RtspRequest request, int cseq, RtspClientIdentity identity) =>
        throw new NotImplementedException("TODO(impl: rtsp-transport)");

    /// <summary>Writes a response (used to answer event-channel requests and by the fake receiver).</summary>
    public static byte[] EncodeResponse(string protocol, int status, string reason, IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> body) =>
        throw new NotImplementedException("TODO(impl: rtsp-transport)");

    /// <summary>Parses one complete response from the start of <paramref name="buffer"/>; false when more bytes are needed.</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> buffer, out RtspResponse? response, out int consumed) =>
        throw new NotImplementedException("TODO(impl: rtsp-transport)");

    /// <summary>Parses one complete request from the start of <paramref name="buffer"/>; false when more bytes are needed.</summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> buffer, out RtspIncomingRequest? request, out int consumed) =>
        throw new NotImplementedException("TODO(impl: rtsp-transport)");
}
