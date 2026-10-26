using Airplay2.Sender.Crypto;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;

namespace Airplay2.Receiver.Rtsp;

/// <summary>
/// A response written by <see cref="RtspServerConnection"/>: status line with the request's protocol token echoed (the
/// srcvers-366 speaker echoes it, SPEC-control.md 1.8), <c>Content-Length</c> (always), <c>Content-Type</c> when set,
/// <c>Server</c>, <c>CSeq</c> echoed when the request had one [sps:rtsp.c:4210-4217], then <see cref="Headers"/>. The answer
/// a <see cref="RtspRequestHandler"/> returns (SPEC-receiver.md 4.1-4.9).
/// </summary>
/// <param name="StatusCode">The status code.</param>
/// <param name="ReasonPhrase">Reason phrase; the standard one for the code when null.</param>
public sealed record RtspServerResponse(int StatusCode, string? ReasonPhrase = null)
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>Extra headers (e.g. <c>Audio-Latency</c>, <c>Connection: close</c>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = NoHeaders;

    /// <summary>Body content type, or null.</summary>
    public string? ContentType { get; init; }

    /// <summary>Body bytes.</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>
    /// Keys (server view: <see cref="HapChannelKeys.WriteKey"/> encrypts what the server sends) to switch to after this
    /// response has been written in plaintext: "prepare encryption, but don't activate yet, the response to this
    /// request is still plaintext" [sps:rtsp.c:1811-1817].
    /// </summary>
    public HapChannelKeys? EnableEncryptionAfter { get; init; }

    /// <summary>Close the connection after writing this response.</summary>
    public bool CloseAfter { get; init; }

    /// <summary>Test switch: write this CSeq instead of echoing the request's.</summary>
    public int? CSeqOverride { get; init; }

    /// <summary>Test switch: do not write a CSeq header.</summary>
    public bool OmitCSeq { get; init; }

    /// <summary>
    /// Test switch: write nothing (the handler answered itself through <see cref="RtspServerConnection.WriteRawAsync"/>,
    /// or the request is meant to go unanswered). <see cref="CloseAfter"/> still applies.
    /// </summary>
    public bool Suppress { get; init; }

    /// <summary>A response that writes nothing (see <see cref="Suppress"/>).</summary>
    public static RtspServerResponse None() => new(RtspStatus.Ok) { Suppress = true };

    /// <summary>200 OK without body.</summary>
    public static RtspServerResponse Ok() => new(RtspStatus.Ok);

    /// <summary>A status without body.</summary>
    public static RtspServerResponse Status(int statusCode) => new(statusCode);

    /// <summary>200 OK with a binary plist body.</summary>
    public static RtspServerResponse Plist(object body) =>
        new(RtspStatus.Ok) { ContentType = RtspContentTypes.BinaryPlist, Body = BinaryPlist.Encode(body) };

    /// <summary>200 OK with an arbitrary body.</summary>
    public static RtspServerResponse Bytes(string contentType, ReadOnlyMemory<byte> body) =>
        new(RtspStatus.Ok) { ContentType = contentType, Body = body };

    /// <summary>The standard reason phrase for a status code (RTSP/HTTP plus the AirPlay codes of SPEC-control.md 2.4).</summary>
    public static string DefaultReason(int statusCode) => statusCode switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        451 => "Unavailable",
        453 => "Not Enough Bandwidth",
        470 => "Connection Authorization Required",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        _ => "Status",
    };
}
