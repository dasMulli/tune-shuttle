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
/// The group volume model (docs/ARCHITECTURE.md, "Group volume model"; SPEC-audio.md 6.3): per-receiver level <c>r_i</c> (0..100) and one group master level <c>G</c>.
/// Moving G scales every r_i by G'/G (relative differences preserved; receivers that reach 100 clamp). A change reported
/// by a receiver updates only its r_i. Pure state; the group player applies the resulting levels to the sessions.
/// </summary>
/// <remarks>
/// <para>
/// Each receiver keeps an unclamped weight <c>w_i = r_i / G</c>, taken when its level is set directly. A master move
/// computes <c>r_i = clamp(round(w_i * G'), 0, 100)</c> from the weights, never from the clamped levels, so a receiver
/// clamped at 100 on the way up returns to its old relation with the others on the way down, and <c>G = 0</c> (every
/// r_i 0) does not erase the relation either.
/// </para>
/// <para>
/// While <c>G</c> is 0 a direct level cannot be expressed as a weight; the receiver then gets the weight <c>level / 100</c>
/// (as if G were 100) and keeps its level until the master moves. Not thread-safe for writers; the group player
/// serialises the mutations. <see cref="Levels"/> is an immutable snapshot replaced on every change, so readers on other
/// threads (the bridge's API, the web app) may read and iterate it without a lock.
/// </para>
/// </remarks>
public sealed class GroupVolumeModel
{
    private readonly Dictionary<string, int> _levels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _weights = new(StringComparer.Ordinal);
    private volatile IReadOnlyDictionary<string, int> _snapshot = new Dictionary<string, int>(StringComparer.Ordinal).AsReadOnly();

    /// <summary>Creates a model with the given master level.</summary>
    public GroupVolumeModel(int masterLevel = 50)
    {
        MasterLevel = Math.Clamp(masterLevel, 0, 100);
    }

    /// <summary>G.</summary>
    public int MasterLevel { get; private set; }

    /// <summary>The current r_i by receiver id: an immutable snapshot taken after the last change (safe to iterate on any thread).</summary>
    public IReadOnlyDictionary<string, int> Levels => _snapshot;

    /// <summary>Adds or replaces a receiver's level (clamped to 0..100); G and the other receivers are unchanged.</summary>
    public void SetReceiverLevel(string receiverId, int level)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        level = Math.Clamp(level, 0, 100);
        _levels[receiverId] = level;
        _weights[receiverId] = MasterLevel > 0 ? (double)level / MasterLevel : level / 100.0;
        Publish();
    }

    /// <summary>Removes a receiver.</summary>
    public void RemoveReceiver(string receiverId)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        if (_levels.Remove(receiverId))
        {
            _weights.Remove(receiverId);
            Publish();
        }
    }

    /// <summary>Moves G (clamped to 0..100) and returns the receivers whose r_i changed with their new values.</summary>
    public IReadOnlyList<(string ReceiverId, int Level)> SetMasterLevel(int level)
    {
        level = Math.Clamp(level, 0, 100);
        MasterLevel = level;
        var changed = new List<(string, int)>();
        foreach (var (id, weight) in _weights)
        {
            var next = Math.Clamp((int)Math.Round(weight * level, MidpointRounding.AwayFromZero), 0, 100);
            if (_levels[id] != next)
            {
                _levels[id] = next;
                changed.Add((id, next));
            }
        }

        if (changed.Count != 0)
        {
            Publish();
        }

        return changed;
    }

    /// <summary>
    /// A receiver reported its own level (event channel or GET_PARAMETER poll, SPEC-audio.md 6.2/6.3): updates r_i only;
    /// G and the other receivers stay. Unknown receivers are ignored.
    /// </summary>
    public void ReceiverReported(string receiverId, int level)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        if (_levels.ContainsKey(receiverId))
        {
            SetReceiverLevel(receiverId, level);
        }
    }

    private void Publish() => _snapshot = new Dictionary<string, int>(_levels, StringComparer.Ordinal).AsReadOnly();
}
