// Port of playback/src/convert.rs (contract only).
// TODO(port: playback-pipeline): scale_with_dither / scale, clamping bounds, f64_to_* conversions, i24 packing.
namespace Librespot.Playback;

/// <summary>Converts f64 samples (-1.0..1.0) to the sink's sample format, optionally dithering.</summary>
public sealed class Converter
{
    private readonly IDitherer? _ditherer;

    public Converter(DithererBuilder? ditherConfig)
    {
        _ditherer = ditherConfig?.Invoke();
    }

    public float[] F64ToF32(ReadOnlySpan<double> samples) => throw new NotImplementedException("TODO(port: playback-pipeline)");
    public int[] F64ToS32(ReadOnlySpan<double> samples) => throw new NotImplementedException("TODO(port: playback-pipeline)");
    public int[] F64ToS24(ReadOnlySpan<double> samples) => throw new NotImplementedException("TODO(port: playback-pipeline)");
    /// <summary>Packed little-endian 24-bit samples, 3 bytes each (upstream <c>i24</c>).</summary>
    public byte[] F64ToS24_3(ReadOnlySpan<double> samples) => throw new NotImplementedException("TODO(port: playback-pipeline)");
    public short[] F64ToS16(ReadOnlySpan<double> samples) => throw new NotImplementedException("TODO(port: playback-pipeline)");
}
