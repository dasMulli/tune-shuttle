namespace Airplay2.Receiver.Streams;

/// <summary>
/// One block of a buffered (type 103) stream (SPEC-receiver.md 5.2, SPEC-audio.md 4.2): after the 2-byte length prefix a
/// 23-bit sequence, the RTP timestamp, the SSRC that names the format and the encrypted payload.
/// </summary>
/// <param name="Sequence">The 23-bit block sequence (<c>flushUntilSeq</c>/<c>flushFromSeq</c> compare against it, 4.7).</param>
/// <param name="Timestamp">The RTP timestamp of the block's first frame.</param>
/// <param name="Ssrc">The format code: <see cref="SsrcAac"/>, <see cref="SsrcAlac"/>, or another value decoded per the stream SETUP's <c>audioFormat</c> (RQ3).</param>
/// <param name="Payload">The encrypted payload (ChaCha20-Poly1305 with <c>shk</c>).</param>
public readonly record struct BufferedBlock(uint Sequence, uint Timestamp, uint Ssrc, ReadOnlyMemory<byte> Payload)
{
    /// <summary>SSRC of AAC-LC/44.1 blocks, 1024 frames each (SPEC-receiver.md 5.2).</summary>
    public const uint SsrcAac = 0x16000000;

    /// <summary>SSRC of ALAC/44.1/16 blocks, 352 frames each (SPEC-receiver.md 5.2).</summary>
    public const uint SsrcAlac = 0x0000FACE;

    /// <summary>The sequence field's width in bits (SPEC-receiver.md 4.7: 23-bit modular compare).</summary>
    public const int SequenceBits = 23;

    /// <summary>The mask of a 23-bit sequence.</summary>
    public const uint SequenceMask = (1u << SequenceBits) - 1;

    /// <summary>Frames per AAC block (SPEC-receiver.md R3).</summary>
    public const int AacFramesPerBlock = 1024;

    /// <summary>Frames per ALAC block (SPEC-receiver.md R3).</summary>
    public const int AlacFramesPerBlock = 352;

    /// <summary>
    /// True when <paramref name="a"/> is before <paramref name="b"/> in 23-bit modular order (SPEC-receiver.md 4.7:
    /// <c>FLUSHBUFFERED</c> drops frames with sequence below <c>flushUntilSeq</c>).
    /// </summary>
    public static bool IsBefore(uint a, uint b) => ((b - a) & SequenceMask) is > 0 and < (1u << (SequenceBits - 1));

    /// <summary>Parses one block without its 2-byte length prefix (SPEC-audio.md 4.2); false for a block shorter than its header.</summary>
    public static bool TryRead(ReadOnlyMemory<byte> block, out BufferedBlock parsed) => throw new NotImplementedException("SPEC-receiver.md 5.2, wave 2");
}
