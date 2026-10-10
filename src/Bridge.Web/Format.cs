using System.Globalization;

namespace Bridge.Web;

/// <summary>
/// Display formatting shared by the pages (UI-SPEC 3.4). Everything a user reads goes through the culture-aware helpers,
/// which use <see cref="CultureInfo.CurrentCulture"/>; <c>I18n.SetLanguage</c> sets the default-thread culture every flow
/// reads it from (the app never sets the AsyncLocal one) to the selected language, so the
/// numbers and the catalog texts around them agree. <see cref="PlaybackTime"/>, <see cref="Remaining"/> and
/// <see cref="FramesAsMs"/> stay invariant (m:ss is universal; FramesAsMs feeds hints that wrap it themselves).
/// </summary>
public static class Format
{
    /// <summary>The minus sign of <see cref="Remaining"/> (U+2212, not the hyphen).</summary>
    public const string Minus = "−";

    /// <summary>A whole number with the culture's grouping (<c>N0</c>): 1.234 (de) / 1,234 (en).</summary>
    public static string Integer(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A number with <paramref name="digits"/> decimals (<c>N{digits}</c>): 1,75 (de) / 1.75 (en).</summary>
    public static string Decimal(double value, int digits) => value.ToString(NumberFormat("N", digits), CultureInfo.CurrentCulture);

    /// <summary>
    /// A ratio as a percentage with <paramref name="digits"/> decimals and the culture's spacing (<c>P{digits}</c>):
    /// 0,4 % (de) / 0.4% (en) for 0.004.
    /// </summary>
    public static string Percent(double ratio, int digits) => ratio.ToString(NumberFormat("P", digits), CultureInfo.CurrentCulture);

    /// <summary>
    /// Frames at 44.1 kHz as whole milliseconds with the culture's grouping: 1.750 (de) / 1,750 (en) for 77175. The unit
    /// is part of the catalog string that embeds the number.
    /// </summary>
    public static string Milliseconds(int frames) => (frames / 44.1).ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A moment as local time in the culture's long time pattern (<c>T</c>): 14:05:09 (de) / 2:05:09 PM (en).</summary>
    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    /// <summary>A moment as local date and time in the culture's general pattern (<c>G</c>): 10.10.2026 14:05:09 (de).</summary>
    public static string DateTime(DateTimeOffset value) => value.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);

    /// <summary>Frames at 44.1 kHz as whole milliseconds, invariant (offsets, latency).</summary>
    public static string FramesAsMs(int frames) => (frames / 44.1).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>A playback time as m:ss (h:mm:ss from an hour on); negative times show as 0:00. Invariant.</summary>
    public static string PlaybackTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>The time left as <c>−m:ss</c> with the U+2212 minus sign (<see cref="PlaybackTime"/> of <paramref name="ms"/>).</summary>
    public static string Remaining(long ms) => Minus + PlaybackTime(ms);

    private static string NumberFormat(string specifier, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        return specifier + digits.ToString(CultureInfo.InvariantCulture);
    }
}
