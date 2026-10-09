using System.Globalization;

namespace Bridge.Web;

/// <summary>Display formatting shared by the pages.</summary>
public static class Format
{
    /// <summary>Frames at 44.1 kHz as whole milliseconds (offsets, latency).</summary>
    public static string FramesAsMs(int frames) => (frames / 44.1).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>A playback time as m:ss (h:mm:ss from an hour on); negative times show as 0:00.</summary>
    public static string PlaybackTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
