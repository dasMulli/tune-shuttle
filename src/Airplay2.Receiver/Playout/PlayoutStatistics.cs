using Airplay2.Sender.Session;

namespace Airplay2.Receiver.Playout;

/// <summary>
/// A snapshot of the <see cref="PlayoutBuffer"/> for Diagnostics (SPEC-receiver.md 5.3, 9.1 <c>AirplayDiagnosticsDto</c>).
/// </summary>
/// <param name="BufferedMs">Decoded audio waiting in the buffer, in milliseconds.</param>
/// <param name="StreamOffset">The learnt per-stream offset <c>D</c> of R6 (0 for buffered streams, about 0.45 s for realtime streams, 5.4).</param>
/// <param name="LateFrames">Frames dropped because their release time was more than <c>LateThreshold</c> in the past.</param>
/// <param name="MissingFrames">Frames of silence inserted for sequence gaps that were not filled in time.</param>
/// <param name="DroppedForDrift">Single frames dropped by the drift correction.</param>
/// <param name="RepeatedForDrift">Single frames repeated by the drift correction.</param>
/// <param name="Decoder">The decoder in use (<c>ALAC</c> or <c>AAC-LC (libavcodec)</c>), or null without a stream.</param>
/// <param name="StreamType">Realtime or buffered, or null without a stream.</param>
/// <param name="NextTimestamp">The RTP timestamp of the next frame the buffer will release (the <c>elapsed</c> source of 5.5), or null when empty.</param>
public sealed record PlayoutStatistics(
    int BufferedMs,
    TimeSpan StreamOffset,
    long LateFrames,
    long MissingFrames,
    long DroppedForDrift,
    long RepeatedForDrift,
    string? Decoder,
    StreamType? StreamType,
    uint? NextTimestamp)
{
    /// <summary>Both drift corrections together (the <c>DriftCorrections</c> of SPEC-receiver.md 9.1).</summary>
    public long DriftCorrections => DroppedForDrift + RepeatedForDrift;

    /// <summary>An empty snapshot: no stream, nothing buffered.</summary>
    public static PlayoutStatistics Empty { get; } = new(0, TimeSpan.Zero, 0, 0, 0, 0, null, null, null);
}
