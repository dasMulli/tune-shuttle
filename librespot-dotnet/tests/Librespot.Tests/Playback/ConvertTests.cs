// Tests for playback/src/convert.rs (upstream has no unit tests for it).
using Librespot.Playback;
using Xunit;

namespace Librespot.Tests.Playback;

public class ConvertTests
{
    private sealed class ConstantDitherer(double noise) : IDitherer
    {
        public string Name => "constant";

        public double Noise() => noise;
    }

    [Fact]
    public void F64ToS16_scales_rounds_and_saturates()
    {
        var converter = new Converter(null);
        var result = converter.F64ToS16(
        [
            0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0,
            0.5 / 32768, -0.5 / 32768, // exactly half an LSB: rounds away from zero like f64::round
            0.25 / 32768, double.NaN,
        ]);
        Assert.Equal(new short[] { 0, 32767, -32768, 16384, -16384, 32767, -32768, 1, -1, 0, 0 }, result);
    }

    [Fact]
    public void F64ToS32_scales_and_saturates()
    {
        var converter = new Converter(null);
        var result = converter.F64ToS32([0.0, 1.0, -1.0, 0.5, -0.5, 1.0 / 32768]);
        Assert.Equal(new[] { 0, int.MaxValue, int.MinValue, 1 << 30, -(1 << 30), 1 << 16 }, result);
    }

    [Fact]
    public void F64ToS24_clamps_to_24_bits()
    {
        var converter = new Converter(null);
        var result = converter.F64ToS24([0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0, 1.0 / 8388608]);
        Assert.Equal(new[] { 0, 8388607, -8388608, 4194304, -4194304, 8388607, -8388608, 1 }, result);
    }

    [Fact]
    public void F64ToS24_3_packs_three_little_endian_bytes()
    {
        var converter = new Converter(null);
        var result = converter.F64ToS24_3([1.0, -1.0, 0.5, -1.0 / 8388608, 0.0]);
        Assert.Equal(
            new byte[]
            {
                0xff, 0xff, 0x7f, // 8388607
                0x00, 0x00, 0x80, // -8388608
                0x00, 0x00, 0x40, // 4194304
                0xff, 0xff, 0xff, // -1
                0x00, 0x00, 0x00,
            },
            result);
    }

    [Fact]
    public void F64ToF32_casts()
    {
        var converter = new Converter(null);
        Assert.Equal(new[] { 0.0f, 1.0f, -0.5f, 1.5f, (float)0.1 }, converter.F64ToF32([0.0, 1.0, -0.5, 1.5, 0.1]));
    }

    [Fact]
    public void Dither_is_applied_at_16_bit_level_before_scaling()
    {
        var converter = new Converter(() => new ConstantDitherer(0.25));

        // S16: 0 * 32768 + 0.25 = 0.25 -> 0; 0.5 LSB + 0.25 = 0.75 -> 1.
        Assert.Equal(new short[] { 0, 1 }, converter.F64ToS16([0.0, 0.5 / 32768]));

        // S24: (0 * 32768 + 0.25) * 256 = 64 (sub-LSB precision of the 16-bit noise is kept).
        Assert.Equal(new[] { 64, 8388607 }, converter.F64ToS24([0.0, 1.0]));

        // S32: (0 * 32768 + 0.25) * 65536 = 16384.
        Assert.Equal(new[] { 16384 }, converter.F64ToS32([0.0]));

        // Without dithering the same sample stays 0.
        Assert.Equal(new[] { 0 }, new Converter(null).F64ToS24([0.0]));
    }

    [Fact]
    public void Dither_noise_rounds_half_away_from_zero()
    {
        var converter = new Converter(() => new ConstantDitherer(-0.5));
        Assert.Equal(new short[] { -1 }, converter.F64ToS16([0.0]));
    }

    [Fact]
    public void Scale_and_clamping_scale_match_upstream_constants()
    {
        Assert.Equal(8, Converter.Shift16To24);
        Assert.Equal(16, Converter.Shift16To32);
        Assert.Equal(8388608.0, Converter.ScaleS24);

        var converter = new Converter(null);
        Assert.Equal(32768.0, converter.Scale(1.0, 0));
        Assert.Equal(8388607.0, converter.ClampingScaleS24(1.0));
        Assert.Equal(-8388608.0, converter.ClampingScaleS24(-1.5));
    }
}
