using System.Buffers.Binary;
using Airplay2.Receiver.Streams;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Streams;
using Xunit;

namespace Airplay2.Receiver.Tests.Streams;

/// <summary>
/// Buffered block framing (SPEC-receiver.md 5.2, 11.1; SPEC-audio.md 4.2): the 23-bit sequence and marker, timestamp,
/// SSRC and payload of a block as our sender's <see cref="BufferedAudioPacket"/> writes it, the 23-bit modular order of
/// 4.7 and the ChaCha20-Poly1305 decryption with AAD bytes 4..11.
/// </summary>
public class BufferedBlockTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void A_sender_packet_parses_to_its_sequence_marker_timestamp_and_ssrc()
    {
        var frame = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var packet = Build(BufferedBlock.SsrcAac, 0x7ABCDE, 0xDEADBEEF, frame);

        Assert.True(BufferedBlock.TryRead(packet.AsMemory(BufferedBlock.LengthPrefixSize), out var block));

        Assert.Equal(0x7ABCDEu, block.Sequence);
        Assert.True(block.Marker);
        Assert.Equal(0xDEADBEEFu, block.Timestamp);
        Assert.Equal(BufferedBlock.SsrcAac, block.Ssrc);
        Assert.Equal(packet.Length - BufferedBlock.LengthPrefixSize - BufferedBlock.HeaderSize, block.Payload.Length);
        Assert.Equal(frame.Length, block.CiphertextLength);
        Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16BigEndian(packet)); // the prefix counts itself
    }

    [Fact]
    public void The_sequence_is_masked_to_23_bits_and_the_marker_read_separately()
    {
        var raw = new byte[BufferedBlock.HeaderSize + 30];
        raw[0] = 0x80;
        raw[1] = 0x7F; // marker clear, top sequence bits set
        raw[2] = 0xFF;
        raw[3] = 0xFF;

        Assert.True(BufferedBlock.TryRead(raw, out var block));
        Assert.Equal(BufferedBlock.SequenceMask, block.Sequence);
        Assert.False(block.Marker);

        raw[1] = 0x80;
        raw[2] = 0;
        raw[3] = 0;
        Assert.True(BufferedBlock.TryRead(raw, out block));
        Assert.Equal(0u, block.Sequence);
        Assert.True(block.Marker);
    }

    [Fact]
    public void A_block_shorter_than_its_header_is_refused()
    {
        Assert.False(BufferedBlock.TryRead(new byte[BufferedBlock.HeaderSize - 1], out _));
        Assert.True(BufferedBlock.TryRead(new byte[BufferedBlock.HeaderSize], out var empty));
        Assert.Equal(0, empty.CiphertextLength);
        Assert.False(empty.TryDecrypt(Key, new byte[64], out _));
    }

    [Fact]
    public void Decryption_recovers_the_frame_with_the_stream_key()
    {
        var frame = Enumerable.Range(0, 700).Select(i => (byte)(i * 7)).ToArray();
        var packet = Build(BufferedBlock.SsrcAlac, 42, 352 * 42, frame);
        Assert.True(BufferedBlock.TryRead(packet.AsMemory(BufferedBlock.LengthPrefixSize), out var block));

        var plaintext = new byte[1024];
        Assert.True(block.TryDecrypt(Key, plaintext, out var length));
        Assert.Equal(frame, plaintext[..length]);
    }

    [Fact]
    public void Decryption_fails_for_a_wrong_key_a_changed_aad_or_a_short_destination()
    {
        var frame = new byte[100];
        var packet = Build(BufferedBlock.SsrcAlac, 1, 352, frame);
        Assert.True(BufferedBlock.TryRead(packet.AsMemory(BufferedBlock.LengthPrefixSize), out var block));

        var wrongKey = (byte[])Key.Clone();
        wrongKey[0] ^= 1;
        Assert.False(block.TryDecrypt(wrongKey, new byte[200], out _));
        Assert.False(block.TryDecrypt(Key.AsSpan(0, 16), new byte[200], out _));
        Assert.False(block.TryDecrypt(Key, new byte[50], out _));

        // AAD = bytes 4..11: a changed timestamp or SSRC fails authentication; the sequence (bytes 1..3) is not covered.
        Assert.False((block with { Timestamp = block.Timestamp + 1 }).TryDecrypt(Key, new byte[200], out _));
        Assert.False((block with { Ssrc = BufferedBlock.SsrcAac }).TryDecrypt(Key, new byte[200], out _));
        Assert.True((block with { Sequence = 999 }).TryDecrypt(Key, new byte[200], out _));
    }

    [Theory]
    [InlineData(0u, 1u, true)]
    [InlineData(1u, 0u, false)]
    [InlineData(5u, 5u, false)]
    [InlineData(0x7FFFFFu, 0u, true)] // wraps
    [InlineData(0u, 0x7FFFFFu, false)]
    [InlineData(0x7FFFF0u, 0x10u, true)]
    [InlineData(0u, 0x3FFFFFu, true)]
    [InlineData(0u, 0x400000u, false)] // half the space away: not before
    public void IsBefore_compares_in_23_bit_modular_order(uint a, uint b, bool expected)
    {
        Assert.Equal(expected, BufferedBlock.IsBefore(a, b));
    }

    private static byte[] Build(uint ssrc, uint sequence, uint timestamp, byte[] frame)
    {
        using var builder = new RtpAudioPacketBuilder(Key, ssrc);
        var packet = new byte[BufferedAudioPacket.PacketLength(frame.Length)];
        BufferedAudioPacket.Build(builder, packet, sequence, timestamp, frame);
        return packet;
    }
}
