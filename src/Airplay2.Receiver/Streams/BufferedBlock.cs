using System.Buffers.Binary;
using Airplay2.Sender.Crypto;

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

    /// <summary>The RTP header in front of the payload: <c>0x80</c>, marker + 23-bit sequence, timestamp, SSRC (SPEC-audio.md 4.2).</summary>
    public const int HeaderSize = 12;

    /// <summary>The ChaCha20-Poly1305 tag and the 8-byte nonce tail after the ciphertext (SPEC-audio.md 1.3, 4.2).</summary>
    public const int TrailerSize = ChaCha20Poly1305Aead.TagSize + 8;

    /// <summary>The 2-byte big-endian length in front of each block on the TCP connection; it counts itself (SPEC-audio.md 4.2).</summary>
    public const int LengthPrefixSize = 2;

    /// <summary>
    /// The RTP marker bit (byte 1 bit 7), "always set" by senders (SPEC-audio.md 4.2, <c>[sps]
    /// ap2_buffered_audio_processor.c:273-278</c>); kept for diagnostics, the receiver does not act on it.
    /// </summary>
    public bool Marker { get; init; }

    /// <summary>The length of the ciphertext inside <see cref="Payload"/> (without tag and nonce), or 0 when the payload is too short for the trailer.</summary>
    public int CiphertextLength => Math.Max(0, Payload.Length - TrailerSize);

    /// <summary>Parses one block without its 2-byte length prefix (SPEC-audio.md 4.2); false for a block shorter than its header.</summary>
    /// <param name="block">The bytes after the length prefix; <see cref="Payload"/> refers to them (not copied).</param>
    /// <param name="parsed">The block: the sequence masked to 23 bits, the marker, timestamp, SSRC and the encrypted payload.</param>
    public static bool TryRead(ReadOnlyMemory<byte> block, out BufferedBlock parsed)
    {
        var span = block.Span;
        if (span.Length < HeaderSize)
        {
            parsed = default;
            return false;
        }

        var first = BinaryPrimitives.ReadUInt32BigEndian(span);
        parsed = new BufferedBlock(
            first & SequenceMask,
            BinaryPrimitives.ReadUInt32BigEndian(span[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[8..]),
            block[HeaderSize..])
        {
            Marker = (span[1] & 0x80) != 0,
        };
        return true;
    }

    /// <summary>
    /// Decrypts <see cref="Payload"/> with the stream's <c>shk</c> (SPEC-receiver.md 5.2, SPEC-audio.md 1.3 and 4.2):
    /// ChaCha20-Poly1305 with the AAD = header bytes 4..11 (timestamp and SSRC), the nonce = four zero bytes and the
    /// payload's last 8 bytes, the 16-byte tag in front of them (<c>[sps] ap2_buffered_audio_processor.c:523-549</c>).
    /// False when the key is not 32 bytes, the payload is shorter than the trailer, <paramref name="plaintext"/> is
    /// shorter than <see cref="CiphertextLength"/>, or authentication fails.
    /// </summary>
    /// <param name="key">The 32-byte <c>shk</c>.</param>
    /// <param name="plaintext">Receives the decrypted audio block (ALAC frame or raw AAC).</param>
    /// <param name="length">The plaintext length.</param>
    public bool TryDecrypt(ReadOnlySpan<byte> key, Span<byte> plaintext, out int length)
    {
        length = 0;
        var payload = Payload.Span;
        if (key.Length != ChaCha20Poly1305Aead.KeySize || payload.Length < TrailerSize)
        {
            return false;
        }

        var n = payload.Length - TrailerSize;
        if (plaintext.Length < n)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[ChaCha20Poly1305Aead.NonceSize];
        nonce[..4].Clear();
        payload[^8..].CopyTo(nonce[4..]);
        Span<byte> aad = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(aad, Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(aad[4..], Ssrc);
        if (!ChaCha20Poly1305Aead.TryDecrypt(key, nonce, payload[..n], payload.Slice(n, ChaCha20Poly1305Aead.TagSize), aad, plaintext[..n]))
        {
            return false;
        }

        length = n;
        return true;
    }
}
