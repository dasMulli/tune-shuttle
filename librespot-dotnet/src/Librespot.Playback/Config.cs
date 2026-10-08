// Port of playback/src/config.rs.
namespace Librespot.Playback;

public enum Bitrate
{
    Bitrate96,
    Bitrate160,
    Bitrate320,
}

public static class BitrateExtensions
{
    public const Bitrate Default = Bitrate.Bitrate160;

    /// <summary>Upstream <c>FromStr</c>: "96" | "160" | "320".</summary>
    public static Bitrate? Parse(string s) => s switch
    {
        "96" => Bitrate.Bitrate96,
        "160" => Bitrate.Bitrate160,
        "320" => Bitrate.Bitrate320,
        _ => null,
    };
}

/// <summary>Sample format handed to the sink.</summary>
public enum AudioFormat
{
    F64,
    F32,
    S32,
    S24,
    /// <summary>Packed 24-bit (3 bytes per sample).</summary>
    S24_3,
    S16,
}

public static class AudioFormatExtensions
{
    public const AudioFormat Default = AudioFormat.S16;

    /// <summary>Upstream <c>FromStr</c> (case-insensitive).</summary>
    public static AudioFormat? Parse(string s) => s.ToUpperInvariant() switch
    {
        "F64" => AudioFormat.F64,
        "F32" => AudioFormat.F32,
        "S32" => AudioFormat.S32,
        "S24" => AudioFormat.S24,
        "S24_3" => AudioFormat.S24_3,
        "S16" => AudioFormat.S16,
        _ => null,
    };

    /// <summary>Bytes per sample for the format (S32 and S24 are both stored in 4 bytes).</summary>
    public static int Size(this AudioFormat format) => format switch
    {
        AudioFormat.F64 => sizeof(double),
        AudioFormat.F32 => sizeof(float),
        AudioFormat.S24_3 => 3,
        AudioFormat.S16 => sizeof(short),
        _ => sizeof(int),
    };
}

public enum NormalisationType
{
    Album,
    Track,
    Auto,
}

public static class NormalisationTypeExtensions
{
    public const NormalisationType Default = NormalisationType.Auto;

    public static NormalisationType? Parse(string s) => s.ToLowerInvariant() switch
    {
        "album" => NormalisationType.Album,
        "track" => NormalisationType.Track,
        "auto" => NormalisationType.Auto,
        _ => null,
    };
}

public enum NormalisationMethod
{
    Basic,
    Dynamic,
}

public static class NormalisationMethodExtensions
{
    public const NormalisationMethod Default = NormalisationMethod.Dynamic;

    public static NormalisationMethod? Parse(string s) => s.ToLowerInvariant() switch
    {
        "basic" => NormalisationMethod.Basic,
        "dynamic" => NormalisationMethod.Dynamic,
        _ => null,
    };
}

/// <summary>Player configuration (upstream <c>PlayerConfig</c>); construct with <c>new PlayerConfig()</c> for the defaults.</summary>
public sealed record PlayerConfig
{
    public Bitrate Bitrate { get; init; } = BitrateExtensions.Default;
    public bool Gapless { get; init; } = true;
    public bool Passthrough { get; init; }

    public bool Normalisation { get; init; }
    public NormalisationType NormalisationType { get; init; } = NormalisationTypeExtensions.Default;
    public NormalisationMethod NormalisationMethod { get; init; } = NormalisationMethodExtensions.Default;
    public double NormalisationPregainDb { get; init; }
    public double NormalisationThresholdDbfs { get; init; } = -2.0;
    public double NormalisationAttackCf { get; init; } = Player.DurationToCoefficient(TimeSpan.FromMilliseconds(5));
    public double NormalisationReleaseCf { get; init; } = Player.DurationToCoefficient(TimeSpan.FromMilliseconds(100));
    public double NormalisationKneeDb { get; init; } = 5.0;

    public IReadOnlyList<string> LocalFileDirectories { get; init; } = [];

    /// <summary>
    /// Factory for the ditherer, so it can be instantiated lazily on the player thread
    /// (upstream passes a fn pointer for the same reason). Null disables dithering.
    /// </summary>
    public DithererBuilder? Ditherer { get; init; } = Dither.MakeDitherer<TriangularDitherer>;

    /// <summary>
    /// Setting this enables periodically sending <c>PlayerEvent.PositionChanged</c> during playback.
    /// </summary>
    public TimeSpan? PositionUpdateInterval { get; init; }
}

/// <summary>Volume control curve (upstream <c>VolumeCtrl</c>); fields are the dB range.</summary>
public abstract record VolumeCtrl
{
    private VolumeCtrl()
    {
    }

    public sealed record Cubic(double DbRange) : VolumeCtrl;

    public sealed record Fixed : VolumeCtrl;

    public sealed record Linear : VolumeCtrl;

    public sealed record Log(double DbRange) : VolumeCtrl;

    public const ushort MaxVolume = ushort.MaxValue;

    /// <summary>Taken from https://www.dr-lex.be/info-stuff/volumecontrols.html</summary>
    public const double DefaultDbRange = 60.0;

    public static VolumeCtrl Default => new Log(DefaultDbRange);

    /// <summary>Upstream <c>FromStr</c>: uses <see cref="DefaultDbRange"/>.</summary>
    public static VolumeCtrl? Parse(string s) => ParseWithRange(s, DefaultDbRange);

    /// <summary>Upstream <c>from_str_with_range</c> (case-insensitive).</summary>
    public static VolumeCtrl? ParseWithRange(string s, double dbRange) => s.ToLowerInvariant() switch
    {
        "cubic" => new Cubic(dbRange),
        "fixed" => new Fixed(),
        "linear" => new Linear(),
        "log" => new Log(dbRange),
        _ => null,
    };
}
