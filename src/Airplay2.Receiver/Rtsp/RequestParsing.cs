using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Airplay2.Receiver.Rtsp;

/// <summary>
/// The receiver's parsers for what a sender puts in headers and <c>SET_PARAMETER</c> bodies (SPEC-receiver.md 4.5, 4.8;
/// the sender side is SPEC-control.md 4.8, 5): <c>RTP-Info</c>, <c>text/parameters</c> (volume, progress) and the DMAP
/// <c>mlit</c> item, plus the airplay2-receiver float format of <c>GET_PARAMETER</c>.
/// </summary>
public static class RequestParsing
{
    /// <summary>DMAP tags whose value is UTF-8 text (SPEC-control.md 5.2 table); everything else is an integer by width or raw bytes.</summary>
    private static readonly HashSet<string> StringTags = new(StringComparer.Ordinal) { "minm", "asar", "asal", "ascp", "asgn", "asaa", "ascm", "asdt" };

    /// <summary>One line of a <c>text/parameters</c> body.</summary>
    public abstract record TextParameter
    {
        /// <summary><c>volume: &lt;dB&gt;</c>.</summary>
        public sealed record VolumeValue(double Db) : TextParameter;

        /// <summary><c>progress: start/current/end</c>.</summary>
        public sealed record ProgressValue(uint Start, uint Current, uint End) : TextParameter;

        /// <summary>Anything else (or a malformed value), verbatim.</summary>
        public sealed record Other(string Line) : TextParameter;
    }

    /// <summary>
    /// Parses <c>RTP-Info: seq=&lt;n&gt;;rtptime=&lt;t&gt;</c> or <c>rtptime=&lt;t&gt;</c> (SPEC-control.md 2.2); airplay2-receiver
    /// reads both values [ap2r:ap2-receiver.py:850-868], shairport only <c>rtptime</c> [sps:rtsp.c:2329-2363].
    /// </summary>
    public static (ushort? Sequence, uint? RtpTime) ParseRtpInfo(string? header)
    {
        ushort? seq = null;
        uint? rtpTime = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            return (seq, rtpTime);
        }

        foreach (var part in header.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Equals("seq", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            {
                seq = unchecked((ushort)s);
            }
            else if (name.Equals("rtptime", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            {
                rtpTime = unchecked((uint)t);
            }
        }

        return (seq, rtpTime);
    }

    /// <summary>
    /// Parses a <c>text/parameters</c> body: <c>volume: -15.000000</c> (shairport takes <c>atof</c> after the prefix
    /// [sps:rtsp.c:3230-3231], airplay2-receiver splits on <c>:</c> [ap2r:ap2-receiver.py:677-685]) and
    /// <c>progress: a/b/c</c> [sps:rtsp.c:3263-3271]; empty lines are skipped.
    /// </summary>
    public static IReadOnlyList<TextParameter> ParseTextParameters(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = new List<TextParameter>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var name = colon > 0 ? line[..colon].Trim() : line;
            var value = colon > 0 ? line[(colon + 1)..].Trim() : string.Empty;
            if (name.Equals("volume", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            {
                result.Add(new TextParameter.VolumeValue(db));
            }
            else if (name.Equals("progress", StringComparison.OrdinalIgnoreCase) && ParseProgress(value) is { } progress)
            {
                result.Add(progress);
            }
            else
            {
                result.Add(new TextParameter.Other(line));
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a DMAP <c>mlit</c> body (SPEC-control.md 5.2): the 8-byte outer header is skipped unconditionally, as
    /// shairport does, then <c>tag(4) || length(4, BE) || value</c> items are read until the body ends
    /// [sps:rtsp.c:3282-3314]. Text tags become strings, 1/2/4/8-byte values of other tags unsigned integers, anything
    /// else a byte array. A truncated item ends the walk.
    /// </summary>
    public static IReadOnlyDictionary<string, object> ParseDmapListingItem(ReadOnlySpan<byte> body)
    {
        var items = new Dictionary<string, object>(StringComparer.Ordinal);
        if (body.Length < 8)
        {
            return items;
        }

        var rest = body[8..];
        while (rest.Length >= 8)
        {
            var tag = Encoding.ASCII.GetString(rest[..4]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(rest[4..8]);
            if (length > (uint)(rest.Length - 8))
            {
                break;
            }

            var value = rest.Slice(8, (int)length);
            items[tag] = StringTags.Contains(tag)
                ? Encoding.UTF8.GetString(value)
                : value.Length switch
                {
                    1 => (ulong)value[0],
                    2 => (ulong)BinaryPrimitives.ReadUInt16BigEndian(value),
                    4 => (ulong)BinaryPrimitives.ReadUInt32BigEndian(value),
                    8 => BinaryPrimitives.ReadUInt64BigEndian(value),
                    _ => value.ToArray(),
                };
            rest = rest[(8 + (int)length)..];
        }

        return items;
    }

    /// <summary>Formats a double like Python's <c>str(float)</c> (<c>-20.0</c>, <c>-15.5</c>), the shape airplay2-receiver writes.</summary>
    public static string PythonFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static TextParameter.ProgressValue? ParseProgress(string value)
    {
        var parts = value.Split('/');
        if (parts.Length == 3 &&
            uint.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) &&
            uint.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var current) &&
            uint.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
        {
            return new TextParameter.ProgressValue(start, current, end);
        }

        return null;
    }
}
