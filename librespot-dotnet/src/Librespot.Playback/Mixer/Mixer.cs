// Port of playback/src/mixer/mod.rs. alsamixer.rs is Linux-only and not ported; "softvol" is the only mixer.
using Librespot.Core;

namespace Librespot.Playback.Mixer;

/// <summary>Upstream <c>trait VolumeGetter</c>: the attenuation the player applies to samples (1.0 = none).</summary>
public interface IVolumeGetter
{
    double AttenuationFactor();
}

/// <summary>Upstream <c>NoOpVolume</c>: no attenuation (hardware mixers).</summary>
public sealed class NoOpVolume : IVolumeGetter
{
    public double AttenuationFactor() => 1.0;
}

/// <summary>Upstream <c>trait Mixer</c>. Implementations provide a static <c>Open(MixerConfig)</c> factory via <see cref="MixerFn"/>.</summary>
public interface IMixer
{
    /// <summary>Current volume, 0..65535.</summary>
    ushort Volume { get; }

    void SetVolume(ushort volume);

    /// <summary>Upstream <c>get_soft_volume</c>: defaults to <see cref="NoOpVolume"/>.</summary>
    IVolumeGetter GetSoftVolume() => new NoOpVolume();
}

/// <summary>Upstream <c>MixerConfig</c>.</summary>
public sealed record MixerConfig
{
    public string Device { get; init; } = "default";
    public string Control { get; init; } = "PCM";
    public uint Index { get; init; }
    public VolumeCtrl VolumeCtrl { get; init; } = VolumeCtrl.Default;
}

/// <summary>Upstream <c>MixerFn = fn(MixerConfig) -> Result&lt;Arc&lt;dyn Mixer&gt;, Error&gt;</c>.</summary>
public delegate IMixer MixerFn(MixerConfig config);

/// <summary>Registry of mixers (upstream <c>MIXERS</c> + <c>find</c>).</summary>
public static class Mixers
{
    public static IReadOnlyList<(string Name, MixerFn Open)> All { get; } =
    [
        (SoftMixer.Name, SoftMixer.Open), // default goes first
    ];

    public static MixerFn? Find(string? name)
    {
        if (name is null)
        {
            return All.Count > 0 ? All[0].Open : null;
        }

        foreach (var (n, open) in All)
        {
            if (n == name)
            {
                return open;
            }
        }

        return null;
    }
}
