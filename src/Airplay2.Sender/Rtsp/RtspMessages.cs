using System.Globalization;
using System.Security.Cryptography;
using Airplay2.Sender.Plist;

namespace Airplay2.Sender.Rtsp;

/// <summary>Content types used on the control connection (SPEC-control.md 2.3).</summary>
public static class RtspContentTypes
{
    /// <summary>Binary property list (<c>bplist00</c>): SETUP, TEARDOWN, /info stage 1, /feedback, /command.</summary>
    public const string BinaryPlist = "application/x-apple-binary-plist";
    /// <summary>TLV8 or raw bytes: pairing and /auth-setup.</summary>
    public const string OctetStream = "application/octet-stream";
    /// <summary><c>name: value</c> lines (CRLF-terminated): volume, progress, GET_PARAMETER.</summary>
    public const string Parameters = "text/parameters";
    /// <summary>DMAP metadata (SET_PARAMETER).</summary>
    public const string DmapTagged = "application/x-dmap-tagged";
    /// <summary>JPEG artwork (SET_PARAMETER).</summary>
    public const string Jpeg = "image/jpeg";
    /// <summary>PNG artwork (SET_PARAMETER).</summary>
    public const string Png = "image/png";
    /// <summary>SETPEERS sub-path label used as content type.</summary>
    public const string PeerListChanged = "/peer-list-changed";
    /// <summary>SETPEERSX sub-path label used as content type.</summary>
    public const string PeerListChangedX = "/peer-list-changed-x";
}

/// <summary>Header names used on the control connection (SPEC-control.md 2.2).</summary>
public static class RtspHeaders
{
    /// <summary>Request sequence number, echoed by the receiver.</summary>
    public const string CSeq = "CSeq";
    /// <summary>Sender user agent (<c>AirPlay/550.10</c>).</summary>
    public const string UserAgent = "User-Agent";
    /// <summary>Upper-case hex of a random 64-bit value, zero-padded to 16 digits.</summary>
    public const string DacpId = "DACP-ID";
    /// <summary>Random 32-bit decimal.</summary>
    public const string ActiveRemote = "Active-Remote";
    /// <summary>Same value as DACP-ID.</summary>
    public const string ClientInstance = "Client-Instance";
    /// <summary>Body content type.</summary>
    public const string ContentType = "Content-Type";
    /// <summary>Body length in bytes.</summary>
    public const string ContentLength = "Content-Length";
    /// <summary>HomeKit pairing kind: 3 regular, 4 transient.</summary>
    public const string XAppleHkp = "X-Apple-HKP";
    /// <summary>Optional sender display name.</summary>
    public const string XAppleClientName = "X-Apple-Client-Name";
    /// <summary><c>seq=..;rtptime=..</c> or <c>rtptime=..</c> on FLUSH and SET_PARAMETER.</summary>
    public const string RtpInfo = "RTP-Info";
    /// <summary><c>npt=0-</c> (harmless, ignored by receivers).</summary>
    public const string Range = "Range";
    /// <summary>Latency the receiver reports on RECORD.</summary>
    public const string AudioLatency = "Audio-Latency";
    /// <summary><c>close</c> on a session-ending TEARDOWN.</summary>
    public const string Connection = "Connection";
    /// <summary>Receiver software (<c>AirTunes/366.0</c>).</summary>
    public const string Server = "Server";
    /// <summary>Methods listed in an OPTIONS answer.</summary>
    public const string Public = "Public";
}

/// <summary>Status codes with AirPlay-specific meanings (SPEC-control.md 2.4).</summary>
public static class RtspStatus
{
    /// <summary>OK.</summary>
    public const int Ok = 200;
    /// <summary>Bad request.</summary>
    public const int BadRequest = 400;
    /// <summary>Password required (AirPlay 1 digest).</summary>
    public const int Unauthorized = 401;
    /// <summary>Not authenticated.</summary>
    public const int Forbidden = 403;
    /// <summary>Unknown endpoint.</summary>
    public const int NotFound = 404;
    /// <summary>shairport-sync: unavailable / invalid arguments.</summary>
    public const int Unavailable = 451;
    /// <summary>shairport-sync: could not become the principal connection.</summary>
    public const int NotPrincipal = 453;
    /// <summary>Pairing rejected; on transient M1 it means "use the PIN flow".</summary>
    public const int ConnectionAuthorizationRequired = 470;
    /// <summary>Internal error.</summary>
    public const int InternalError = 500;
    /// <summary>Not implemented (also shairport-sync's answer to an NTP session SETUP).</summary>
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
/// <c>Headers</c> must not contain these two names, nor the names the codec writes itself (<c>CSeq</c>, <c>User-Agent</c>,
/// <c>DACP-ID</c>, <c>Active-Remote</c>, <c>Client-Instance</c>): the codec throws <see cref="ArgumentException"/>. Use a
/// different <see cref="RtspClientIdentity"/> to change the identity values.
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
/// <param name="Protocol">The protocol token of the status line as received (<c>RTSP/1.0</c> or <c>HTTP/1.1</c>).</param>
/// <param name="StatusCode">The status code.</param>
/// <param name="ReasonPhrase">The reason phrase, possibly empty.</param>
/// <param name="Headers">The headers; lookups through <see cref="Header"/> are case-insensitive.</param>
/// <param name="Body">The body (<c>Content-Length</c> bytes, or a sniffed bplist when the length header was missing).</param>
public sealed record RtspResponse(string Protocol, int StatusCode, string ReasonPhrase, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body)
{
    /// <summary>True for any 2xx.</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>Header lookup, case-insensitive.</summary>
    public string? Header(string name) => RtspHeaderLookup.Find(Headers, name);

    /// <summary>The <c>Content-Type</c> header, or null.</summary>
    public string? ContentType => Header(RtspHeaders.ContentType);

    /// <summary>
    /// Decodes the body as a binary plist dictionary; throws <see cref="FormatException"/> when it is not one. The
    /// plist is located by its <c>bplist00</c> magic, so bytes in front of it (odd receivers) are skipped
    /// (SPEC-control.md 4.3, owntone's event parser does the same).
    /// </summary>
    public PlistDictionary PlistBody() => RtspHeaderLookup.DecodePlist(Body.Span);

    /// <summary>The <c>CSeq</c> header as an integer, or null.</summary>
    public int? CSeq => RtspHeaderLookup.CSeq(Headers);
}

/// <summary>A request received from the receiver (event channel <c>POST /command</c>), or by the fake receiver from the sender.</summary>
/// <param name="Method">The method token (<c>POST</c>, <c>SETUP</c>, ...).</param>
/// <param name="Uri">The request URI as sent.</param>
/// <param name="Protocol">The protocol token of the request line (<c>RTSP/1.0</c> or <c>HTTP/1.1</c>); answers echo it.</param>
/// <param name="Headers">The headers; lookups through <see cref="Header"/> are case-insensitive.</param>
/// <param name="Body">The body.</param>
public sealed record RtspIncomingRequest(string Method, string Uri, string Protocol, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body)
{
    /// <summary>Header lookup, case-insensitive.</summary>
    public string? Header(string name) => RtspHeaderLookup.Find(Headers, name);

    /// <summary>The <c>Content-Type</c> header, or null.</summary>
    public string? ContentType => Header(RtspHeaders.ContentType);

    /// <summary>The <c>CSeq</c> header as an integer, or null (shairport-sync's event requests carry none).</summary>
    public int? CSeq => RtspHeaderLookup.CSeq(Headers);

    /// <summary>Decodes the body as a binary plist dictionary, located by its <c>bplist00</c> magic; throws <see cref="FormatException"/> when there is none.</summary>
    public PlistDictionary PlistBody() => RtspHeaderLookup.DecodePlist(Body.Span);
}

/// <summary>Shared helpers of the message records.</summary>
internal static class RtspHeaderLookup
{
    /// <summary>Case-insensitive header lookup over any dictionary (a case-insensitive one takes the fast path).</summary>
    public static string? Find(IReadOnlyDictionary<string, string> headers, string name)
    {
        if (headers.TryGetValue(name, out var direct))
        {
            return direct;
        }

        foreach (var (k, v) in headers)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>The <c>CSeq</c> header as an integer, or null when it is missing or not a number.</summary>
    public static int? CSeq(IReadOnlyDictionary<string, string> headers) =>
        int.TryParse(Find(headers, RtspHeaders.CSeq), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Decodes the first bplist found in <paramref name="body"/>.</summary>
    public static PlistDictionary DecodePlist(ReadOnlySpan<byte> body)
    {
        var start = body.IndexOf("bplist00"u8);
        if (start < 0)
        {
            throw new FormatException("The body does not contain a binary plist.");
        }

        return BinaryPlist.DecodeDictionary(body[start..]);
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
