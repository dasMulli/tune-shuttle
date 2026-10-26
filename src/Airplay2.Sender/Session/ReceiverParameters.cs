using System.Globalization;
using Airplay2.Sender.Playback;

namespace Airplay2.Sender.Session;

/// <summary>
/// Pure helpers the receiver's control plane calls for <c>SET_PARAMETER</c> and <c>GET_PARAMETER</c> (SPEC-receiver.md
/// 4.8, R9): the <c>text/parameters</c> bodies (<c>volume: &lt;dB&gt;</c>, SPEC-control.md 5.1; <c>progress: a/b/c</c>,
/// 5.4), the artwork content-type and size checks (5.3), the elapsed time of a progress triple and the volume answer
/// with the no-output default. The dB ↔ level mapping itself stays <see cref="VolumeMapping"/> (R9: no second mapping).
/// </summary>
public static class ReceiverParameters
{
    /// <summary>The RTP clock of every stream the receiver accepts (SPEC-receiver.md R3; SPEC-control.md 5.4: 44100 per second).</summary>
    public const int SampleRate = 44100;

    /// <summary>The largest artwork body that is kept (SPEC-receiver.md 4.8: 1 MiB; larger ones are answered 200 and dropped).</summary>
    public const int MaxArtworkBytes = 1024 * 1024;

    /// <summary>The level answered while no output is selected and the sender has set none in this session (SPEC-receiver.md R9: 50, -15 dB).</summary>
    public const int DefaultLevel = 50;

    private const string JpegType = "image/jpeg";
    private const string PngType = "image/png";

    /// <summary>
    /// The dB value of the <c>volume:</c> line of a <c>text/parameters</c> body (SPEC-control.md 5.1): the line may appear
    /// anywhere, the separator is <c>:</c> with optional spaces (airplay2-receiver splits on <c>:</c>, shairport-sync takes
    /// <c>atof</c> after the prefix), the decimal separator is always <c>.</c>; null when there is no finite value.
    /// </summary>
    public static double? ParseVolume(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FindValue(body, "volume") is { } text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) &&
            double.IsFinite(db)
            ? db
            : null;
    }

    /// <summary>
    /// The level 0..100 a <c>SET_PARAMETER volume</c> body asks for (SPEC-receiver.md R9: <see cref="ParseVolume"/> then
    /// <see cref="VolumeMapping.DbToLevel"/>, <c>-144</c> and anything at or below <c>-30</c> are 0); null when the body
    /// has no volume line.
    /// </summary>
    public static int? SenderLevel(string body) => ParseVolume(body) is { } db ? VolumeMapping.DbToLevel(db) : null;

    /// <summary>
    /// The <c>progress: start/current/end</c> line of a <c>text/parameters</c> body (SPEC-control.md 5.4: three 32-bit RTP
    /// timestamps), with the request's <c>RTP-Info rtptime</c> as the item key (SPEC-receiver.md 4.8); null when the
    /// body has no well-formed progress line.
    /// </summary>
    public static ProgressInfo? ParseProgress(string body, uint? rtpTime = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (FindValue(body, "progress") is not { } text)
        {
            return null;
        }

        var parts = text.Split('/', StringSplitOptions.TrimEntries);
        return parts.Length == 3 &&
            uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start) &&
            uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var current) &&
            uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            ? new ProgressInfo(start, current, end, rtpTime)
            : null;
    }

    /// <summary>
    /// The position in the track a progress triple describes (SPEC-control.md 5.4): <c>(current - start) / 44100</c>,
    /// computed modulo 2^32 so a timeline that wrapped between start and current still gives the right value; a
    /// current that lies before start (more than half the RTP range "ahead") is 0.
    /// </summary>
    public static TimeSpan Elapsed(ProgressInfo progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return Span(progress.Start, progress.Current);
    }

    /// <summary>The track length a progress triple describes (SPEC-control.md 5.4): <c>(end - start) / 44100</c>, modulo 2^32 like <see cref="Elapsed"/>.</summary>
    public static TimeSpan Duration(ProgressInfo progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return Span(progress.Start, progress.End);
    }

    /// <summary>
    /// The normalised artwork type of a <c>SET_PARAMETER</c> <c>Content-Type</c> (SPEC-receiver.md 4.8, SPEC-control.md
    /// 5.3): <c>image/jpeg</c> or <c>image/png</c>, case-insensitive, parameters after <c>;</c> ignored; null for any other type.
    /// </summary>
    public static string? ArtworkContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var media = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
        return media.Equals(JpegType, StringComparison.OrdinalIgnoreCase) ? JpegType
            : media.Equals(PngType, StringComparison.OrdinalIgnoreCase) ? PngType
            : null;
    }

    /// <summary>Whether a <c>Content-Type</c> is an artwork type (SPEC-receiver.md 4.8; <see cref="ArtworkContentType"/>).</summary>
    public static bool IsArtworkContentType(string? contentType) => ArtworkContentType(contentType) is not null;

    /// <summary>Whether an artwork body of this length is kept (SPEC-receiver.md 4.8: 1 to <see cref="MaxArtworkBytes"/> bytes; larger or empty ones are dropped).</summary>
    public static bool IsArtworkWithinLimit(int length) => length is > 0 and <= MaxArtworkBytes;

    /// <summary>
    /// The level the receiver reports for <c>GET_PARAMETER volume</c> and <c>/info</c> <c>initialVolume</c>
    /// (SPEC-receiver.md R9): the output's G when an output is selected, else the last level the sender set in this
    /// session, else <see cref="DefaultLevel"/>; clamped to 0..100.
    /// </summary>
    public static int AnsweredLevel(int? outputLevel, int? lastSenderLevel) =>
        Math.Clamp(outputLevel ?? lastSenderLevel ?? DefaultLevel, 0, 100);

    /// <summary>The dB value of <see cref="AnsweredLevel"/> through <see cref="VolumeMapping.LevelToDb"/> (SPEC-receiver.md R9, 3.2 <c>initialVolume</c>).</summary>
    public static double AnsweredDb(int? outputLevel, int? lastSenderLevel) =>
        VolumeMapping.LevelToDb(AnsweredLevel(outputLevel, lastSenderLevel));

    /// <summary>
    /// The <c>GET_PARAMETER volume</c> answer body (SPEC-receiver.md 4.8, R9): <see cref="VolumeMapping.FormatParameter"/>
    /// of <see cref="AnsweredDb"/>, the airplay2-receiver shape <c>volume: -24.000000\r\n</c> without a leading blank line.
    /// </summary>
    public static string FormatVolumeAnswer(int? outputLevel, int? lastSenderLevel) =>
        VolumeMapping.FormatParameter(AnsweredDb(outputLevel, lastSenderLevel));

    private static TimeSpan Span(uint from, uint to)
    {
        var frames = unchecked(to - from);
        if (frames > int.MaxValue)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / SampleRate);
    }

    private static string? FindValue(string body, string name)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }
}
