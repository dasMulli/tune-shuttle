using System.Collections;
using System.Globalization;
using System.Text;
using Airplay2.Sender.Plist;

namespace Airplay2.Sender.Rtsp;

/// <summary>
/// Renders messages for the decrypted wire log (docs/DEVELOPMENT.md, "Code conventions": <c>Debug</c> for wire-level detail; every live test logs
/// the complete decrypted exchange). Heads are shown verbatim, bodies as text for <c>text/*</c>, as a decoded plist for
/// bplist bodies, otherwise as hex (a preview at Debug, everything at Trace).
/// </summary>
internal static class RtspWireLog
{
    private const int HexPreviewBytes = 64;
    private const int MaxRendered = 8 * 1024;

    /// <summary>Renders an encoded message (head + body) as it went out.</summary>
    public static string DescribeEncoded(ReadOnlySpan<byte> message, bool full)
    {
        var headEnd = message.IndexOf("\r\n\r\n"u8);
        if (headEnd < 0)
        {
            return Encoding.UTF8.GetString(message);
        }

        var head = Encoding.UTF8.GetString(message[..headEnd]);
        string? contentType = null;
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith(RtspHeaders.ContentType + ":", StringComparison.OrdinalIgnoreCase))
            {
                contentType = line[(RtspHeaders.ContentType.Length + 1)..].Trim();
            }
        }

        return head + DescribeBody(contentType, message[(headEnd + 4)..], full);
    }

    /// <summary>Renders a parsed response.</summary>
    public static string Describe(RtspResponse response, bool full)
    {
        var sb = new StringBuilder();
        sb.Append(response.Protocol).Append(' ').Append(response.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(response.ReasonPhrase);
        AppendHeaders(sb, response.Headers);
        return sb + DescribeBody(response.ContentType, response.Body.Span, full);
    }

    /// <summary>Renders a parsed request.</summary>
    public static string Describe(RtspIncomingRequest request, bool full)
    {
        var sb = new StringBuilder();
        sb.Append(request.Method).Append(' ').Append(request.Uri).Append(' ').Append(request.Protocol);
        AppendHeaders(sb, request.Headers);
        return sb + DescribeBody(request.ContentType, request.Body.Span, full);
    }

    /// <summary>Renders a body: empty, text, plist, or hex.</summary>
    public static string DescribeBody(string? contentType, ReadOnlySpan<byte> body, bool full)
    {
        if (body.IsEmpty)
        {
            return string.Empty;
        }

        var prefix = $"\n[body {body.Length} bytes]\n";
        var plistAt = body.IndexOf("bplist00"u8);
        if (plistAt is >= 0 and < 16)
        {
            try
            {
                var sb = new StringBuilder();
                AppendPlist(sb, BinaryPlist.Decode(body[plistAt..]), full);
                return prefix + Limit(sb.ToString(), full);
            }
            catch (FormatException)
            {
                // fall through to hex
            }
        }

        if (contentType is not null && contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return prefix + Limit(Encoding.UTF8.GetString(body).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n\n", StringComparison.Ordinal), full);
        }

        var shown = full ? body : body[..Math.Min(body.Length, HexPreviewBytes)];
        return prefix + Convert.ToHexString(shown).ToLowerInvariant() + (shown.Length < body.Length ? "..." : string.Empty);
    }

    private static void AppendHeaders(StringBuilder sb, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (k, v) in headers)
        {
            sb.Append("\r\n").Append(k).Append(": ").Append(v);
        }
    }

    private static string Limit(string s, bool full) => full || s.Length <= MaxRendered ? s : s[..MaxRendered] + "...";

    private static void AppendPlist(StringBuilder sb, object? value, bool full)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                sb.Append('"').Append(s).Append('"');
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case byte[] data:
                var shown = full ? data.AsSpan() : data.AsSpan(0, Math.Min(data.Length, HexPreviewBytes));
                sb.Append("<data ").Append(data.Length.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append(Convert.ToHexString(shown).ToLowerInvariant()).Append(shown.Length < data.Length ? "...>" : ">");
                break;
            case PlistDictionary dict:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in dict)
                {
                    sb.Append(first ? string.Empty : ", ").Append(k).Append(": ");
                    AppendPlist(sb, v, full);
                    first = false;
                }

                sb.Append('}');
                break;
            case IList list:
                sb.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    sb.Append(i == 0 ? string.Empty : ", ");
                    AppendPlist(sb, list[i], full);
                }

                sb.Append(']');
                break;
            case IFormattable f:
                sb.Append(f.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(value);
                break;
        }
    }
}
