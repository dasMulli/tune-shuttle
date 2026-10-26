using Airplay2.Receiver.Rtsp;

namespace Airplay2.FakeReceiver;

/// <summary>
/// Parsers for what the sender puts in headers and SET_PARAMETER bodies (SPEC-control.md 4.8, 5): the fake receiver's
/// view of <see cref="RequestParsing"/> (SPEC-receiver.md R2), kept so tests written against the fake stay unchanged.
/// </summary>
public static class FakeParsing
{
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

    /// <summary>Parses <c>RTP-Info</c> (<see cref="RequestParsing.ParseRtpInfo"/>).</summary>
    public static (ushort? Sequence, uint? RtpTime) ParseRtpInfo(string? header) => RequestParsing.ParseRtpInfo(header);

    /// <summary>Parses a <c>text/parameters</c> body (<see cref="RequestParsing.ParseTextParameters"/>).</summary>
    public static IReadOnlyList<TextParameter> ParseTextParameters(string body) =>
        [.. RequestParsing.ParseTextParameters(body).Select(Convert)];

    /// <summary>Parses a DMAP <c>mlit</c> body (<see cref="RequestParsing.ParseDmapListingItem"/>).</summary>
    public static IReadOnlyDictionary<string, object> ParseDmapListingItem(ReadOnlySpan<byte> body) => RequestParsing.ParseDmapListingItem(body);

    /// <summary>Formats a double like Python's <c>str(float)</c> (<see cref="RequestParsing.PythonFloat"/>).</summary>
    public static string PythonFloat(double value) => RequestParsing.PythonFloat(value);

    private static TextParameter Convert(RequestParsing.TextParameter parameter) => parameter switch
    {
        RequestParsing.TextParameter.VolumeValue v => new TextParameter.VolumeValue(v.Db),
        RequestParsing.TextParameter.ProgressValue p => new TextParameter.ProgressValue(p.Start, p.Current, p.End),
        RequestParsing.TextParameter.Other o => new TextParameter.Other(o.Line),
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "Unknown text parameter."),
    };
}
