namespace Airplay2.Receiver.Codecs;

/// <summary>
/// An AAC-LC decoder for one buffered stream (SPEC-receiver.md R4, 7.2): fed ADTS-framed blocks, produces S16 interleaved
/// stereo at 44.1 kHz. Not thread-safe; owned by the buffered reader. <see cref="FfmpegAacDecoder"/> is the implementation;
/// a managed port is a later option behind the same interface.
/// </summary>
public interface IAacDecoder : IDisposable
{
    /// <summary>
    /// Decodes one ADTS-framed block (SPEC-receiver.md 5.2, 7.2). <paramref name="pcmOut"/> receives interleaved S16 stereo
    /// (at least 2 × 1024 samples for one AAC-LC block); <paramref name="frames"/> is the number of stereo frames written.
    /// False when the block could not be decoded (the caller drops it and counts it).
    /// </summary>
    bool TryDecode(ReadOnlySpan<byte> adtsBlock, Span<short> pcmOut, out int frames);

    /// <summary>Flushes the decoder state after a flush or discontinuity (SPEC-receiver.md 7.2: <c>avcodec_flush_buffers</c>).</summary>
    void Reset();
}
