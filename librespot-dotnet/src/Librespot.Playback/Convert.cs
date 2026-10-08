// Port of playback/src/convert.rs.
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback;

/// <summary>
/// Converts normalized f64 samples (-1.0..1.0) to the sink's sample format, optionally dithering.
/// Upstream <c>Converter</c>.
/// </summary>
/// <remarks>
/// Rust's <c>f64 as iN</c> casts saturate (and map NaN to 0); the conversions here do the same explicitly so the
/// output is bit-identical to upstream regardless of the runtime's floating-point conversion rules.
/// </remarks>
public sealed class Converter
{
    /// <summary>
    /// Base bit positions for PCM format scaling. These represent the position of the most significant bit in each
    /// format's full-scale representation. For signed integers in two's complement, full scale is 2^(bits-1).
    /// 16-bit: 2^15 = 32768. Upstream <c>SHIFT_S16</c>.
    /// </summary>
    public const byte ShiftS16 = 15;

    /// <summary>24-bit: 2^23 = 8388608. Upstream <c>SHIFT_S24</c>.</summary>
    public const byte ShiftS24 = 23;

    /// <summary>32-bit: 2^31 = 2147483648. Upstream <c>SHIFT_S32</c>.</summary>
    public const byte ShiftS32 = 31;

    /// <summary>
    /// Additional bit shift needed to scale from 16-bit to 24-bit (23 - 15 = 8). Upstream <c>SHIFT_16_TO_24</c>.
    /// </summary>
    public const byte Shift16To24 = ShiftS24 - ShiftS16;

    /// <summary>
    /// Additional bit shift needed to scale from 16-bit to 32-bit (31 - 15 = 16). Upstream <c>SHIFT_16_TO_32</c>.
    /// </summary>
    public const byte Shift16To32 = ShiftS32 - ShiftS16;

    /// <summary>Pre-calculated scale factor for 24-bit clamping bounds. Upstream <c>SCALE_S24</c>.</summary>
    public const double ScaleS24 = 1UL << ShiftS24;

    private static readonly ILogger Log = LibrespotLog.CreateLogger<Converter>();

    private readonly IDitherer? _ditherer;

    /// <summary>Creates a converter; when <paramref name="ditherConfig"/> is set its ditherer adds noise before rounding.</summary>
    public Converter(DithererBuilder? ditherConfig)
    {
        if (ditherConfig is not null)
        {
            var ditherer = ditherConfig();
            Log.LogInformation("Converting with ditherer: {Ditherer}", ditherer.Name);
            _ditherer = ditherer;
        }
    }

    /// <summary>
    /// Scale audio samples with optimal dithering strategy for Spotify's 16-bit source material.
    /// </summary>
    /// <remarks>
    /// Since Spotify audio is always 16-bit depth, this function:
    /// 1. When dithering: applies noise at 16-bit level, preserves fractional precision, then scales to target
    ///    format and rounds once at the end.
    /// 2. When not dithering: scales directly from normalized float to target format.
    /// The <paramref name="shift"/> parameter specifies how many extra bits to shift beyond the base 16-bit
    /// scaling (0 for 16-bit, 8 for 24-bit, 16 for 32-bit).
    /// </remarks>
    public double Scale(double sample, byte shift)
    {
        if (_ditherer is { } d)
        {
            // With dithering: Apply noise at 16-bit level to address original quantization,
            // then scale up to target format while preserving sub-LSB information
            var dithered16Bit = sample * (1UL << ShiftS16) + d.Noise();
            var scaled = dithered16Bit * (1UL << shift);
            return RoundRust(scaled);
        }

        // No dithering: Scale directly from normalized float to target format
        // using a single bit shift operation (base 16-bit shift + additional shift)
        var totalShift = ShiftS16 + shift;
        return RoundRust(sample * (1UL << totalShift));
    }

    /// <summary>
    /// Clamping scale specifically for 24-bit output to prevent MSB overflow. Only used for S24 formats where
    /// samples are packed in 32-bit words. Ensures the most significant byte is zero to prevent overflow during
    /// dithering. Upstream <c>clamping_scale_s24</c>.
    /// </summary>
    public double ClampingScaleS24(double sample)
    {
        var intValue = Scale(sample, Shift16To24);

        // In two's complement, there are more negative than positive values.
        const double min = -ScaleS24;
        const double max = ScaleS24 - 1.0;

        // Rust f64::clamp propagates NaN; Math.Clamp does too.
        return Math.Clamp(intValue, min, max);
    }

    /// <summary>Upstream <c>f64_to_f32</c>.</summary>
    public float[] F64ToF32(ReadOnlySpan<double> samples)
    {
        var output = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            output[i] = (float)samples[i];
        }

        return output;
    }

    /// <summary>Upstream <c>f64_to_s32</c>.</summary>
    public int[] F64ToS32(ReadOnlySpan<double> samples)
    {
        var output = new int[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            output[i] = SaturateToInt32(Scale(samples[i], Shift16To32));
        }

        return output;
    }

    /// <summary>S24 is 24-bit PCM packed in an upper 32-bit word. Upstream <c>f64_to_s24</c>.</summary>
    public int[] F64ToS24(ReadOnlySpan<double> samples)
    {
        var output = new int[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            output[i] = SaturateToInt32(ClampingScaleS24(samples[i]));
        }

        return output;
    }

    /// <summary>
    /// S24_3 is 24-bit PCM in a 3-byte array: packed little-endian 24-bit samples, 3 bytes each (upstream
    /// <c>f64_to_s24_3</c> returning <c>Vec&lt;i24&gt;</c>).
    /// </summary>
    /// <remarks>
    /// Upstream's <c>i24::from_s24</c> trims the padding byte in native byte order; every sink writes native-endian
    /// samples, and .NET targets are little-endian, so the bytes here are the three low bytes in little-endian order.
    /// </remarks>
    public byte[] F64ToS24_3(ReadOnlySpan<double> samples)
    {
        var output = new byte[samples.Length * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = SaturateToInt32(ClampingScaleS24(samples[i]));
            WriteI24(output.AsSpan(i * 3, 3), sample);
        }

        return output;
    }

    /// <summary>Upstream <c>f64_to_s16</c>.</summary>
    public short[] F64ToS16(ReadOnlySpan<double> samples)
    {
        var output = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            output[i] = SaturateToInt16(Scale(samples[i], 0));
        }

        return output;
    }

    /// <summary>Upstream <c>i24::from_s24</c>: the three least significant bytes of <paramref name="sample"/>.</summary>
    private static void WriteI24(Span<byte> destination, int sample)
    {
        if (BitConverter.IsLittleEndian)
        {
            destination[0] = (byte)sample;
            destination[1] = (byte)(sample >> 8);
            destination[2] = (byte)(sample >> 16);
        }
        else
        {
            destination[0] = (byte)(sample >> 16);
            destination[1] = (byte)(sample >> 8);
            destination[2] = (byte)sample;
        }
    }

    /// <summary>Rust <c>f64::round</c>: round half away from zero.</summary>
    private static double RoundRust(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>Rust <c>f64 as i32</c>: saturating, NaN becomes 0.</summary>
    private static int SaturateToInt32(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        if (value <= int.MinValue)
        {
            return int.MinValue;
        }

        return (int)value;
    }

    /// <summary>Rust <c>f64 as i16</c>: saturating, NaN becomes 0.</summary>
    private static short SaturateToInt16(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        if (value >= short.MaxValue)
        {
            return short.MaxValue;
        }

        if (value <= short.MinValue)
        {
            return short.MinValue;
        }

        return (short)value;
    }
}
