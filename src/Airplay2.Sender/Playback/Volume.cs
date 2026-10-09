namespace Airplay2.Sender.Playback;

/// <summary>
/// The wire representation of volume (SPEC-control.md 5.1, SPEC-audio.md 6): a 0..100 level maps to
/// <c>-30 + 0.3 * level</c> dB, level 0 is sent as <c>-144.0</c> (mute).
/// </summary>
public static class VolumeMapping
{
    /// <summary>Quietest audible wire value.</summary>
    public const double MinDb = -30.0;

    /// <summary>Full-scale wire value.</summary>
    public const double MaxDb = 0.0;

    /// <summary>The mute sentinel.</summary>
    public const double MuteDb = -144.0;

    /// <summary>Level 0..100 to dB.</summary>
    public static double LevelToDb(int level)
    {
        level = Math.Clamp(level, 0, 100);
        return level == 0 ? MuteDb : MinDb + (MaxDb - MinDb) * level / 100.0;
    }

    /// <summary>dB to level 0..100; anything at or below -30 (including -144) is 0.</summary>
    public static int DbToLevel(double db)
    {
        if (db <= MinDb)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round((db - MinDb) / (MaxDb - MinDb) * 100.0), 0, 100);
    }

    /// <summary>Formats the <c>volume: </c> parameter body with a dot decimal separator and six decimals, CRLF-terminated.</summary>
    public static string FormatParameter(double db) =>
        "volume: " + db.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + "\r\n";

    /// <summary>
    /// Parses the <c>GET_PARAMETER</c> reply (SPEC-control.md 4.11): leading CRLF is skipped and the <c>volume:</c> line
    /// may appear anywhere (shairport-sync answers <c>\r\nvolume: %.6f\r\n</c>, airplay2-receiver <c>volume: &lt;v&gt;\r\n</c>); null when absent.
    /// </summary>
    public static double? ParseParameter(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("volume:", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(t["volume:".Length..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
        }

        return null;
    }
}

/// <summary>
/// PLAN.md's volume model: per-receiver level <c>r_i</c> (0..100) and one group master level <c>G</c>. Moving G scales
/// every r_i by G'/G (relative differences preserved; receivers at 100 clamp). A change reported by a receiver updates
/// only its r_i. Pure state; the group player applies the resulting levels to the sessions.
/// </summary>
public sealed class GroupVolumeModel
{
    private readonly Dictionary<string, int> _levels = new(StringComparer.Ordinal);

    /// <summary>Creates a model with the given master level.</summary>
    public GroupVolumeModel(int masterLevel = 50)
    {
        MasterLevel = Math.Clamp(masterLevel, 0, 100);
    }

    /// <summary>G.</summary>
    public int MasterLevel { get; private set; }

    /// <summary>The current r_i by receiver id.</summary>
    public IReadOnlyDictionary<string, int> Levels => _levels;

    /// <summary>Adds or replaces a receiver's level.</summary>
    public void SetReceiverLevel(string receiverId, int level) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <summary>Removes a receiver.</summary>
    public void RemoveReceiver(string receiverId) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <summary>Moves G and returns the receivers whose r_i changed with their new values.</summary>
    public IReadOnlyList<(string ReceiverId, int Level)> SetMasterLevel(int level) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");

    /// <summary>A receiver reported its own level (event channel or GET_PARAMETER poll): updates r_i only.</summary>
    public void ReceiverReported(string receiverId, int level) => throw new NotImplementedException("TODO(impl: audio-pipeline-group-sync)");
}
