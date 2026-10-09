using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Airplay2.Sender.Plist;

namespace Airplay2.Sender.Rtsp;

/// <summary>
/// Serialises requests and parses responses/requests in the HTTP/1.1-shaped RTSP framing (SPEC-control.md 2.1):
/// request line, headers, blank line, <c>Content-Length</c> body. Header names are matched case-insensitively; either
/// <c>RTSP/1.0</c> or <c>HTTP/1.1</c> is accepted in a status line.
/// </summary>
/// <remarks>
/// Parsing rules (pyatv splits at the first <c>\r\n\r\n</c> and reads <c>Content-Length</c>, default 0
/// [pyatv:pyatv/support/http.py:110-140]): leading empty lines in front of a message are skipped (RFC 7230 3.5); a
/// header line without a colon is ignored; repeated header names are joined with <c>", "</c> except
/// <c>Content-Length</c>, whose repetitions must agree. When <c>Content-Length</c> is missing and the bytes after the
/// head start with the <c>bplist</c> magic, the body is the bplist, its length taken from the 32-byte bplist trailer
/// (SPEC-control.md 4.3: "find the bplist by its bplist magic if the headers are odd" [owntone:airplay_events.c:237-285]
/// treats everything after the head as the body; we bound it by the trailer so a following message stays intact);
/// otherwise a message without <c>Content-Length</c> has an empty body. Malformed messages throw
/// <see cref="InvalidDataException"/>, after which the connection must be closed (the stream position is lost).
/// </remarks>
public static class RtspMessageCodec
{
    /// <summary>Largest message head (start line + headers) accepted before the blank line must have arrived.</summary>
    public const int MaxHeadBytes = 64 * 1024;

    /// <summary>Largest body accepted (artwork is at most 200 KiB, SPEC.md D12; replies are far smaller).</summary>
    public const int MaxBodyBytes = 16 * 1024 * 1024;

    private const int BplistTrailerSize = 32;
    private const int BplistMinSize = 8 + 1 + 1 + BplistTrailerSize; // magic, one object, one offset, trailer

    private static readonly string[] ReservedRequestHeaders =
    [
        RtspHeaders.CSeq, RtspHeaders.UserAgent, RtspHeaders.DacpId, RtspHeaders.ActiveRemote, RtspHeaders.ClientInstance,
        RtspHeaders.ContentType, RtspHeaders.ContentLength,
    ];

    private static readonly string[] ReservedBareRequestHeaders = [RtspHeaders.CSeq, RtspHeaders.ContentType, RtspHeaders.ContentLength];

    /// <summary>
    /// Writes the request: request line, <c>CSeq</c>, <c>User-Agent</c>, <c>DACP-ID</c>, <c>Active-Remote</c>,
    /// <c>Client-Instance</c>, then <c>Content-Type</c>/<c>Content-Length</c> per the rule on <see cref="RtspRequest"/>
    /// (Content-Type when set; Content-Length when Content-Type is set or the body is non-empty), the extra headers, then the body.
    /// </summary>
    /// <exception cref="ArgumentException">A reserved header is in <see cref="RtspRequest.Headers"/>, or a token contains whitespace/CR/LF.</exception>
    public static byte[] EncodeRequest(RtspRequest request, int cseq, RtspClientIdentity identity) =>
        EncodeRequest(request, cseq, identity, clientName: null);

    /// <summary>
    /// As <see cref="EncodeRequest(RtspRequest, int, RtspClientIdentity)"/>, plus <c>X-Apple-Client-Name</c> after the
    /// identity headers when <paramref name="clientName"/> is set and the request does not carry its own.
    /// </summary>
    internal static byte[] EncodeRequest(RtspRequest request, int cseq, RtspClientIdentity identity, string? clientName)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegative(cseq);
        ValidateValue(identity.DacpId, nameof(identity.DacpId));
        ValidateValue(identity.UserAgent, nameof(identity.UserAgent));

        var sb = StartRequest(request.Method, request.Uri);
        AppendHeader(sb, RtspHeaders.CSeq, cseq.ToString(CultureInfo.InvariantCulture));
        AppendHeader(sb, RtspHeaders.UserAgent, identity.UserAgent);
        AppendHeader(sb, RtspHeaders.DacpId, identity.DacpId);
        AppendHeader(sb, RtspHeaders.ActiveRemote, identity.ActiveRemote.ToString(CultureInfo.InvariantCulture));
        AppendHeader(sb, RtspHeaders.ClientInstance, identity.DacpId);
        if (clientName is not null && RtspHeaderLookup.Find(request.Headers, RtspHeaders.XAppleClientName) is null)
        {
            ValidateValue(clientName, nameof(clientName));
            AppendHeader(sb, RtspHeaders.XAppleClientName, clientName);
        }

        return FinishRequest(sb, request.ContentType, request.Headers, ReservedRequestHeaders, request.Body.Span, nameof(request));
    }

    /// <summary>
    /// Writes a request without the sender identity block, as a receiver sends one on the event channel
    /// (<c>POST /command</c>, SPEC-control.md 4.3; used by the fake receiver): request line, <c>CSeq</c> when
    /// <paramref name="cseq"/> is set (Apple TV writes <c>CSeq: 0</c>, shairport-sync none), then
    /// <c>Content-Type</c>/<c>Content-Length</c> per the rule on <see cref="RtspRequest"/>, then <paramref name="headers"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="headers"/> contains <c>CSeq</c>, <c>Content-Type</c> or <c>Content-Length</c>, or a token/value contains whitespace or a line break.</exception>
    internal static byte[] EncodeBareRequest(
        string method,
        string uri,
        int? cseq,
        string? contentType,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var sb = StartRequest(method, uri);
        if (cseq is { } c)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(c, nameof(cseq));
            AppendHeader(sb, RtspHeaders.CSeq, c.ToString(CultureInfo.InvariantCulture));
        }

        return FinishRequest(sb, contentType, headers, ReservedBareRequestHeaders, body, nameof(headers));
    }

    /// <summary>
    /// Writes a response (used to answer event-channel requests and by the fake receiver): status line,
    /// <c>Content-Length</c> (always, also <c>0</c>, as receivers do [sps:rtsp.c:845-857]), then
    /// <paramref name="headers"/> in their order, blank line, body. <paramref name="headers"/> must not contain
    /// <c>Content-Length</c>. A null or empty <paramref name="reason"/> writes the status line without a reason phrase
    /// (<c>RTSP/1.0 200</c>, no trailing space), the form the parser reads as an empty reason.
    /// </summary>
    public static byte[] EncodeResponse(string protocol, int status, string? reason, IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ValidateToken(protocol, nameof(protocol));
        ArgumentOutOfRangeException.ThrowIfLessThan(status, 100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(status, 999);
        reason ??= string.Empty;
        ValidateValue(reason, nameof(reason));

        var sb = new StringBuilder(160);
        sb.Append(protocol).Append(' ').Append(status.ToString(CultureInfo.InvariantCulture));
        if (reason.Length > 0)
        {
            sb.Append(' ').Append(reason);
        }

        sb.Append("\r\n");
        AppendHeader(sb, RtspHeaders.ContentLength, body.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, RtspHeaders.ContentLength, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Content-Length is written by the codec.", nameof(headers));
            }

            ValidateHeaderName(name);
            ValidateValue(value, name);
            AppendHeader(sb, name, value);
        }

        sb.Append("\r\n");
        return Concat(sb, body.Span);
    }

    /// <summary>Parses one complete response from the start of <paramref name="buffer"/>; false when more bytes are needed.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a response (bad status line, bad <c>Content-Length</c>, oversized head or body).</exception>
    public static bool TryParseResponse(ReadOnlySpan<byte> buffer, out RtspResponse? response, out int consumed)
    {
        response = null;
        if (!TryParseMessage(buffer, out var startLine, out var headers, out var body, out consumed))
        {
            return false;
        }

        // <protocol>/<version> <code> [<reason>]  [pyatv:pyatv/support/http.py:170-187]
        var firstSpace = startLine.IndexOf(' ');
        if (firstSpace <= 0)
        {
            throw new InvalidDataException($"Bad status line '{Truncate(startLine)}'.");
        }

        var protocol = startLine[..firstSpace];
        if (!IsProtocolToken(protocol))
        {
            throw new InvalidDataException($"Bad status line '{Truncate(startLine)}': unknown protocol.");
        }

        var rest = startLine[(firstSpace + 1)..].TrimStart(' ');
        var codeEnd = rest.IndexOf(' ');
        var codeText = codeEnd < 0 ? rest : rest[..codeEnd];
        if (codeText.Length != 3 || !int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            throw new InvalidDataException($"Bad status line '{Truncate(startLine)}': status code.");
        }

        var reason = codeEnd < 0 ? string.Empty : rest[(codeEnd + 1)..].Trim();
        response = new RtspResponse(protocol, code, reason, headers, body);
        return true;
    }

    /// <summary>Parses one complete request from the start of <paramref name="buffer"/>; false when more bytes are needed.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a request (bad request line, bad <c>Content-Length</c>, oversized head or body).</exception>
    public static bool TryParseRequest(ReadOnlySpan<byte> buffer, out RtspIncomingRequest? request, out int consumed)
    {
        request = null;
        if (!TryParseMessage(buffer, out var startLine, out var headers, out var body, out consumed))
        {
            return false;
        }

        // <method> <uri> <protocol>/<version>
        var parts = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !IsProtocolToken(parts[2]))
        {
            throw new InvalidDataException($"Bad request line '{Truncate(startLine)}'.");
        }

        request = new RtspIncomingRequest(parts[0], parts[1], parts[2], headers, body);
        return true;
    }

    /// <summary>
    /// Classifies the message at the start of <paramref name="buffer"/> (after any leading empty lines): a response
    /// starts with <c>RTSP/</c> or <c>HTTP/</c>, anything else is a request. <see cref="RtspMessageKind.None"/> when too
    /// few bytes have arrived to tell.
    /// </summary>
    internal static RtspMessageKind Classify(ReadOnlySpan<byte> buffer)
    {
        var skip = SkipEmptyLines(buffer);
        var rest = buffer[skip..];
        if (rest.Length < 5)
        {
            // "RTSP/" and "HTTP/" share no prefix with a method name except for "HTTP"/"RTSP" themselves, which no
            // receiver sends as a method; wait for five bytes.
            return RtspMessageKind.None;
        }

        return IsProtocolPrefix(rest) ? RtspMessageKind.Response : RtspMessageKind.Request;
    }

    /// <summary>Number of CR/LF bytes in front of the first message byte.</summary>
    internal static int SkipEmptyLines(ReadOnlySpan<byte> buffer)
    {
        var skip = 0;
        while (skip < buffer.Length && (buffer[skip] == (byte)'\r' || buffer[skip] == (byte)'\n'))
        {
            skip++;
        }

        return skip;
    }

    /// <summary>
    /// Length of the bplist at the start of <paramref name="data"/>, from the trailer: the first length <c>n</c> whose
    /// last 32 bytes form a consistent trailer (6 zero bytes, offset/ref sizes 1..8, top object inside the table,
    /// <c>offsetTableOffset + numObjects * offsetIntSize + 32 == n</c>). -1 when no complete bplist is present yet.
    /// </summary>
    internal static int FindBplistLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < BplistMinSize || !BinaryPlist.IsBinaryPlist(data))
        {
            return -1;
        }

        var limit = Math.Min(data.Length, MaxBodyBytes);
        for (var n = BplistMinSize; n <= limit; n++)
        {
            var trailer = data.Slice(n - BplistTrailerSize, BplistTrailerSize);
            if (trailer[..6].IndexOfAnyExcept((byte)0) >= 0)
            {
                continue;
            }

            int offsetIntSize = trailer[6];
            int objectRefSize = trailer[7];
            if (offsetIntSize is < 1 or > 8 || objectRefSize is < 1 or > 8)
            {
                continue;
            }

            var numObjects = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
            var topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
            var tableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
            if (numObjects == 0 || numObjects > (ulong)n || topObject >= numObjects || tableOffset < 8 || tableOffset > (ulong)n)
            {
                continue;
            }

            if (tableOffset + (numObjects * (ulong)offsetIntSize) + BplistTrailerSize == (ulong)n)
            {
                return n;
            }
        }

        return -1;
    }

    private static bool TryParseMessage(
        ReadOnlySpan<byte> buffer,
        out string startLine,
        out Dictionary<string, string> headers,
        out ReadOnlyMemory<byte> body,
        out int consumed)
    {
        startLine = string.Empty;
        headers = null!;
        body = ReadOnlyMemory<byte>.Empty;
        consumed = 0;

        var skip = SkipEmptyLines(buffer);
        var message = buffer[skip..];
        var headEnd = message.IndexOf("\r\n\r\n"u8);
        if (headEnd < 0)
        {
            if (message.Length > MaxHeadBytes)
            {
                throw new InvalidDataException($"No end of message head within {MaxHeadBytes} bytes.");
            }

            return false;
        }

        if (headEnd > MaxHeadBytes)
        {
            throw new InvalidDataException($"Message head of {headEnd} bytes exceeds {MaxHeadBytes}.");
        }

        var head = Encoding.UTF8.GetString(message[..headEnd]);
        var lines = head.Split("\r\n");
        startLine = lines[0];
        headers = new Dictionary<string, string>(lines.Length, StringComparer.OrdinalIgnoreCase);
        int? contentLength = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue; // not a header line; pyatv would fail here, we tolerate it
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (string.Equals(name, RtspHeaders.ContentLength, StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > MaxBodyBytes)
                {
                    throw new InvalidDataException($"Bad Content-Length '{Truncate(value)}'.");
                }

                if (contentLength is { } previous && previous != length)
                {
                    throw new InvalidDataException("Conflicting Content-Length headers.");
                }

                contentLength = length;
                headers[name] = value;
                continue;
            }

            headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }

        var bodyStart = headEnd + 4;
        var available = message[bodyStart..];
        int bodyLength;
        if (contentLength is { } declared)
        {
            if (available.Length < declared)
            {
                return false;
            }

            bodyLength = declared;
        }
        else if (StartsLikeBplist(available))
        {
            bodyLength = FindBplistLength(available);
            if (bodyLength < 0)
            {
                if (available.Length > MaxBodyBytes)
                {
                    throw new InvalidDataException("Unterminated bplist body without Content-Length.");
                }

                return false;
            }
        }
        else
        {
            bodyLength = 0;
        }

        body = bodyLength == 0 ? ReadOnlyMemory<byte>.Empty : available[..bodyLength].ToArray();
        consumed = skip + bodyStart + bodyLength;
        return true;
    }

    /// <summary>True when the bytes are the bplist magic or a proper prefix of it (more bytes may complete it).</summary>
    private static bool StartsLikeBplist(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return false;
        }

        var magic = "bplist00"u8;
        var n = Math.Min(magic.Length, data.Length);
        return data[..n].SequenceEqual(magic[..n]);
    }

    private static bool IsProtocolToken(string token) =>
        token.Length > 5 &&
        (token.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase) || token.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase));

    private static bool IsProtocolPrefix(ReadOnlySpan<byte> data) =>
        Ascii.EqualsIgnoreCase(data[..5], "RTSP/"u8) || Ascii.EqualsIgnoreCase(data[..5], "HTTP/"u8);

    private static StringBuilder StartRequest(string method, string uri)
    {
        ValidateToken(method, nameof(RtspRequest.Method));
        ValidateToken(uri, nameof(RtspRequest.Uri));
        var sb = new StringBuilder(256);
        sb.Append(method).Append(' ').Append(uri).Append(' ').Append(RtspRequest.Protocol).Append("\r\n");
        return sb;
    }

    /// <summary>Content-Type/Content-Length (the rule on <see cref="RtspRequest"/>), the caller's headers, blank line, body.</summary>
    private static byte[] FinishRequest(
        StringBuilder sb,
        string? contentType,
        IReadOnlyDictionary<string, string> headers,
        string[] reservedHeaders,
        ReadOnlySpan<byte> body,
        string paramName)
    {
        if (contentType is not null)
        {
            ValidateValue(contentType, nameof(RtspRequest.ContentType));
            AppendHeader(sb, RtspHeaders.ContentType, contentType);
        }

        if (contentType is not null || !body.IsEmpty)
        {
            AppendHeader(sb, RtspHeaders.ContentLength, body.Length.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (name, value) in headers)
        {
            foreach (var reserved in reservedHeaders)
            {
                if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Header '{name}' is written by the codec and must not be in the request headers.", paramName);
                }
            }

            ValidateHeaderName(name);
            ValidateValue(value, name);
            AppendHeader(sb, name, value);
        }

        sb.Append("\r\n");
        return Concat(sb, body);
    }

    private static void AppendHeader(StringBuilder sb, string name, string value) =>
        sb.Append(name).Append(": ").Append(value).Append("\r\n");

    private static byte[] Concat(StringBuilder head, ReadOnlySpan<byte> body)
    {
        var headText = head.ToString();
        var headLength = Encoding.UTF8.GetByteCount(headText);
        var output = new byte[headLength + body.Length];
        Encoding.UTF8.GetBytes(headText, output);
        body.CopyTo(output.AsSpan(headLength));
        return output;
    }

    private static void ValidateToken(string value, string what)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, what);
        if (value.AsSpan().IndexOfAny(" \t\r\n") >= 0)
        {
            throw new ArgumentException($"{what} must not contain whitespace or line breaks: '{Truncate(value)}'.", what);
        }
    }

    private static void ValidateHeaderName(string name)
    {
        ValidateToken(name, "header name");
        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Header name '{name}' must not contain ':'.", nameof(name));
        }
    }

    private static void ValidateValue(string value, string what)
    {
        ArgumentNullException.ThrowIfNull(value, what);
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException($"{what} must not contain line breaks.", what);
        }
    }

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "...";
}

/// <summary>What kind of message starts a buffer.</summary>
internal enum RtspMessageKind
{
    /// <summary>Not enough bytes to tell.</summary>
    None,

    /// <summary>A status line (<c>RTSP/</c> or <c>HTTP/</c>).</summary>
    Response,

    /// <summary>A request line.</summary>
    Request,
}
