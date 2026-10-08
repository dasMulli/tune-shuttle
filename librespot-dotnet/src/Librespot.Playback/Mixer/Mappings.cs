// Port of playback/src/mixer/mappings.rs.
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback.Mixer;

/// <summary>Upstream <c>trait MappedCtrl</c>, implemented for <see cref="VolumeCtrl"/> as extension methods.</summary>
public static class MappedCtrl
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger("Librespot.Playback.Mixer.Mappings");

    /// <summary>Maps a 0..65535 volume to the 0.0..1.0 attenuation factor following the control's curve.</summary>
    public static double ToMapped(this VolumeCtrl ctrl, ushort volume)
    {
        // More than just an optimization, this ensures that zero volume is
        // really mute (both the log and cubic equations would otherwise not
        // reach zero).
        if (volume == 0)
        {
            return 0.0;
        }

        if (volume == VolumeCtrl.MaxVolume)
        {
            // And limit in case of rounding errors (as is the case for log).
            return 1.0;
        }

        var normalizedVolume = volume / (double)VolumeCtrl.MaxVolume;
        double mappedVolume;
        if (ctrl.RangeOk())
        {
            mappedVolume = ctrl switch
            {
                VolumeCtrl.Cubic c => CubicMapping.LinearToMapped(normalizedVolume, c.DbRange),
                VolumeCtrl.Log l => LogMapping.LinearToMapped(normalizedVolume, l.DbRange),
                _ => normalizedVolume,
            };
        }
        else
        {
            // Ensure not to return -inf or NaN due to division by zero.
            Log.LogError("{Ctrl} does not work with 0 dB range, using linear mapping instead", ctrl);
            mappedVolume = normalizedVolume;
        }

        Log.LogDebug("Input volume {Volume} mapped to: {Mapped:F2}%", volume, mappedVolume * 100.0);

        return mappedVolume;
    }

    /// <summary>Inverse of <see cref="ToMapped"/>.</summary>
    public static ushort AsUnmapped(this VolumeCtrl ctrl, double mappedVolume)
    {
        // More than just an optimization, this ensures that zero mapped volume
        // is unmapped to non-negative real numbers (otherwise the log and cubic
        // equations would respectively return -inf and -1/9.)
        if (Math.Abs(mappedVolume - 0.0) <= double.Epsilon)
        {
            return 0;
        }

        if (Math.Abs(mappedVolume - 1.0) <= double.Epsilon)
        {
            return VolumeCtrl.MaxVolume;
        }

        double unmappedVolume;
        if (ctrl.RangeOk())
        {
            unmappedVolume = ctrl switch
            {
                VolumeCtrl.Cubic c => CubicMapping.MappedToLinear(mappedVolume, c.DbRange),
                VolumeCtrl.Log l => LogMapping.MappedToLinear(mappedVolume, l.DbRange),
                _ => mappedVolume,
            };
        }
        else
        {
            // Ensure not to return -inf or NaN due to division by zero.
            Log.LogError("{Ctrl} does not work with 0 dB range, using linear mapping instead", ctrl);
            unmappedVolume = mappedVolume;
        }

        // Rust `as u16` saturates; emulate that.
        var scaled = unmappedVolume * VolumeCtrl.MaxVolume;
        return double.IsNaN(scaled) ? (ushort)0 : (ushort)Math.Clamp(scaled, 0, VolumeCtrl.MaxVolume);
    }

    public static double DbRange(this VolumeCtrl ctrl) => ctrl switch
    {
        VolumeCtrl.Fixed => 0.0,
        VolumeCtrl.Linear => VolumeCtrl.DefaultDbRange, // arbitrary, could be anything > 0
        VolumeCtrl.Log l => l.DbRange,
        VolumeCtrl.Cubic c => c.DbRange,
        _ => 0.0,
    };

    /// <summary>Upstream <c>set_db_range</c>; records are immutable so this returns the updated control (unchanged for Fixed/Linear).</summary>
    public static VolumeCtrl WithDbRange(this VolumeCtrl ctrl, double newDbRange)
    {
        VolumeCtrl result = ctrl switch
        {
            VolumeCtrl.Cubic c => c with { DbRange = newDbRange },
            VolumeCtrl.Log l => l with { DbRange = newDbRange },
            _ => ctrl,
        };

        if (ReferenceEquals(result, ctrl))
        {
            Log.LogError("Invalid to set dB range for volume control type {Ctrl}", ctrl);
        }

        Log.LogDebug("Volume control is now {Ctrl}", result);
        return result;
    }

    public static bool RangeOk(this VolumeCtrl ctrl) => ctrl.DbRange() > 0.0 || ctrl is VolumeCtrl.Fixed or VolumeCtrl.Linear;
}

/// <summary>Upstream <c>trait VolumeMapping</c>.</summary>
public interface IVolumeMapping
{
    static abstract double LinearToMapped(double unmappedVolume, double dbRange);
    static abstract double MappedToLinear(double mappedVolume, double dbRange);
}

/// <summary>
/// Volume conversion taken from: https://www.dr-lex.be/info-stuff/volumecontrols.html#ideal2
/// As the human auditory system has a logarithmic sensitivity curve, this mapping results in a near linear
/// loudness experience with the listener.
/// </summary>
public sealed class LogMapping : IVolumeMapping
{
    public static double LinearToMapped(double normalizedVolume, double dbRange)
    {
        var (dbRatio, idealFactor) = Coefficients(dbRange);
        return Math.Exp(idealFactor * normalizedVolume) / dbRatio;
    }

    public static double MappedToLinear(double mappedVolume, double dbRange)
    {
        var (dbRatio, idealFactor) = Coefficients(dbRange);
        return Math.Log(dbRatio * mappedVolume) / idealFactor;
    }

    private static (double DbRatio, double IdealFactor) Coefficients(double dbRange)
    {
        var dbRatio = Player.DbToRatio(dbRange);
        var idealFactor = Math.Log(dbRatio);
        return (dbRatio, idealFactor);
    }
}

/// <summary>
/// Ported from alsa-utils alsamixer/volume_mapping.c (inspired by https://www.robotplanet.dk/audio/audio_gui_design/).
/// Use this mapping to mimic Alsa's native mixer or for a more granular control in the upper volume range.
/// </summary>
public sealed class CubicMapping : IVolumeMapping
{
    public static double LinearToMapped(double normalizedVolume, double dbRange)
    {
        var minNorm = MinNorm(dbRange);
        return Math.Pow(normalizedVolume * (1.0 - minNorm) + minNorm, 3);
    }

    public static double MappedToLinear(double mappedVolume, double dbRange)
    {
        var minNorm = MinNorm(dbRange);
        return (Math.Pow(mappedVolume, 1.0 / 3.0) - minNorm) / (1.0 - minNorm);
    }

    private static double MinNorm(double dbRange)
    {
        // Note that this 60.0 is unrelated to DEFAULT_DB_RANGE.
        // Instead, it's the cubic voltage to dB ratio.
        return Math.Pow(10.0, -dbRange / 60.0);
    }
}
