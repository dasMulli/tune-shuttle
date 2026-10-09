using System.Buffers.Binary;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Rtp;
using Xunit;

namespace Airplay2.Sender.Tests.Rtp;

/// <summary>Byte-level layouts of SPEC-audio.md 1.2-1.3, 2.2-2.4 and 4.4.</summary>
public class RtpPacketTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", string.Empty, StringComparison.Ordinal));

    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    // The "silence" frame of docs/airplay2/test-vectors/alac-44100-s16-2ch-352.json.
    private static readonly byte[] SilenceAlac = Hex("200000040013080981f8c1ff80000013080981f8c1ff800000ff80afbfe02bfc");

    [Fact]
    public void Worked_example_of_spec_1_3_is_byte_exact()
    {
        // Expected packet from gen_rtp_vector.py (independent pure-Python RFC 8439, self-checked against RFC 8439 2.8.2).
        var expected = Hex(
            "8060123400015888deadbeef" +
            "d10f844c22fb7280e3ce8fcc7c8810893a9423b930128905aa65b3485809fce0" +
            "915a95c1640759e9511ed4c6399d9af2" +
            "0700000000000000");

        using var builder = new RtpAudioPacketBuilder(Key, 0xDEADBEEF);
        var scratch = new byte[RealtimeStreamConstants.MaxPacketSize];
        for (var i = 0; i < 7; i++)
        {
            builder.Build(scratch, (ushort)i, 0, false, SilenceAlac);
        }

        Assert.Equal(7UL, builder.NextCounter);
        var packet = new byte[RealtimeStreamConstants.MaxPacketSize];
        var length = builder.Build(packet, 0x1234, 0x00015888, false, SilenceAlac);

        Assert.Equal(12 + SilenceAlac.Length + 24, length);
        Assert.Equal(expected, packet[..length]);
        Assert.Equal(8UL, builder.NextCounter);

        // Header, AAD and nonce tail as the spec spells them out.
        Assert.Equal(Hex("80 60 12 34 00 01 58 88 DE AD BE EF"), packet[..12]);
        Assert.Equal(Hex("07 00 00 00 00 00 00 00"), packet[(length - 8)..length]);
    }

    [Fact]
    public void Decrypting_with_the_aead_recovers_the_frame()
    {
        using var builder = new RtpAudioPacketBuilder(Key, 0xDEADBEEF);
        var packet = new byte[RealtimeStreamConstants.MaxPacketSize];
        var length = builder.Build(packet, 0x1234, 0x00015888, false, SilenceAlac);
        var span = packet.AsSpan(0, length);

        // Receiver procedure, independent of the builder: nonce = 4 zero bytes || last 8 bytes; AAD = bytes 4..11.
        var nonce = new byte[12];
        span[^8..].CopyTo(nonce.AsSpan(4));
        var n = length - 36;
        var plaintext = new byte[n];
        Assert.True(ChaCha20Poly1305Aead.TryDecrypt(Key, nonce, span.Slice(12, n), span.Slice(12 + n, 16), span.Slice(4, 8), plaintext));
        Assert.Equal(SilenceAlac, plaintext);

        var viaHelper = new byte[n];
        Assert.True(RtpAudioPacketBuilder.TryDecrypt(Key, span, viaHelper, out var frameLength));
        Assert.Equal(n, frameLength);
        Assert.Equal(SilenceAlac, viaHelper);
    }

    [Fact]
    public void Tampered_aad_fails_authentication()
    {
        using var builder = new RtpAudioPacketBuilder(Key, 1);
        var packet = new byte[RealtimeStreamConstants.MaxPacketSize];
        var length = builder.Build(packet, 1, 1000, false, SilenceAlac);
        packet[7] ^= 1; // timestamp is AAD
        Assert.False(RtpAudioPacketBuilder.TryDecrypt(Key, packet.AsSpan(0, length), new byte[length], out _));
        packet[7] ^= 1;
        packet[3] ^= 1; // sequence is not AAD (SPEC-audio 1.3: AAD = bytes 4..11 only)
        Assert.True(RtpAudioPacketBuilder.TryDecrypt(Key, packet.AsSpan(0, length), new byte[length], out _));
    }

    [Fact]
    public void Marker_bit_sets_E0_and_counter_advances_per_packet()
    {
        using var builder = new RtpAudioPacketBuilder(Key, 5);
        var a = new byte[200];
        var b = new byte[200];
        var la = builder.Build(a, 0xFFFF, 0xFFFFFF00, true, SilenceAlac);
        var lb = builder.Build(b, 0x0000, 0x00000060, false, SilenceAlac); // 0xFFFFFF00 + 352 wraps mod 2^32

        Assert.Equal(0xE0, a[1]);
        Assert.Equal(0x60, b[1]);
        Assert.True(RtpAudioPacketBuilder.ReadMarker(a));
        Assert.False(RtpAudioPacketBuilder.ReadMarker(b));
        Assert.Equal((ushort)0xFFFF, RtpAudioPacketBuilder.ReadSequence(a));
        Assert.Equal((ushort)0, RtpAudioPacketBuilder.ReadSequence(b));
        Assert.Equal(0x00000060u, RtpAudioPacketBuilder.ReadTimestamp(b));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(a.AsSpan(la - 8)));
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(lb - 8)));
        Assert.Equal(2UL, builder.NextCounter);
    }

    [Fact]
    public void Two_receivers_with_different_keys_produce_different_ciphertext_for_the_same_frame()
    {
        using var one = new RtpAudioPacketBuilder(Key, 1);
        using var two = new RtpAudioPacketBuilder(Key.Select(b => (byte)(b ^ 0xFF)).ToArray(), 1);
        var a = new byte[200];
        var b = new byte[200];
        var la = one.Build(a, 10, 3520, false, SilenceAlac);
        var lb = two.Build(b, 10, 3520, false, SilenceAlac);
        Assert.Equal(la, lb);
        Assert.Equal(a[..12], b[..12]);
        Assert.NotEqual(a[12..la], b[12..lb]);
    }

    [Fact]
    public void Build_rejects_bad_input()
    {
        Assert.Throws<ArgumentException>(() => new RtpAudioPacketBuilder(new byte[16], 0));
        using var builder = new RtpAudioPacketBuilder(Key, 0);
        Assert.Throws<ArgumentException>(() => builder.Build(new byte[100], 0, 0, false, ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentException>(() => builder.Build(new byte[12 + 32 + 23], 0, 0, false, SilenceAlac));
        var shared = new byte[200];
        Assert.Throws<ArgumentException>(() => builder.Build(shared, 0, 0, false, shared.AsSpan(0, 32)));
        Assert.Equal(0UL, builder.NextCounter);
        builder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => builder.Build(new byte[200], 0, 0, false, SilenceAlac));
    }

    [Fact]
    public void Max_size_escape_frame_fits_the_mtu_budget()
    {
        using var builder = new RtpAudioPacketBuilder(Key, 0);
        var frame = new byte[RealtimeStreamConstants.MaxAlacFrame];
        new Random(1).NextBytes(frame);
        var packet = new byte[RealtimeStreamConstants.MaxPacketSize];
        Assert.Equal(1448, builder.Build(packet, 0, 0, false, frame));
    }

    [Fact]
    public void Sync_packet_layout_matches_spec_2_3()
    {
        var buffer = new byte[SyncPacket.Size];
        new SyncPacket(true, 0x11223344, 0xAABBCCDD_01020304UL, 0x11223344 + 77175).Write(buffer);
        Assert.Equal(Hex("90 D4 00 07 11 22 33 44 AA BB CC DD 01 02 03 04 11 23 60 BB"), buffer);

        new SyncPacket(false, 1, 2, 3).Write(buffer);
        Assert.Equal(Hex("80 D4 00 07 00 00 00 01 00 00 00 00 00 00 00 02 00 00 00 03"), buffer);

        Assert.True(SyncPacket.TryParse(buffer, out var parsed));
        Assert.Equal(new SyncPacket(false, 1, 2, 3), parsed);
        Assert.Throws<ArgumentException>(() => new SyncPacket(false, 1, 2, 3).Write(new byte[19]));
    }

    [Fact]
    public void Ptp_sync_packet_layout_matches_spec_4_4()
    {
        var buffer = new byte[PtpSyncPacket.Size];
        var packet = new PtpSyncPacket(true, 0x01020304, 0x1122334455667788UL, 0x01020304 + 77175, 0xFFFF_0A0B0C0D0E0FUL);
        packet.Write(buffer);
        Assert.Equal(28, PtpSyncPacket.Size);
        Assert.Equal(
            Hex("90 D7 00 06 01 02 03 04 11 22 33 44 55 66 77 88 01 03 30 7B FF FF 0A 0B 0C 0D 0E 0F"),
            buffer);

        (packet with { First = false }).Write(buffer);
        Assert.Equal(0x80, buffer[0]);
        Assert.True(PtpSyncPacket.TryParse(buffer, out var parsed));
        Assert.Equal(packet with { First = false }, parsed);
        Assert.False(SyncPacket.TryParse(buffer, out _));
    }

    [Fact]
    public void Timing_reply_fields_match_spec_2_2()
    {
        // A shairport-sync style request (rtp.c:595-664) with a non-zero transmit time, as owntone's copy expects.
        var request = Hex("80 D2 00 07 00 00 00 00 00000000 00000000 00000000 00000000 E1 23 45 67 89 AB CD EF");
        Assert.True(TimingPacket.TryParse(request, out var parsed));
        Assert.True(parsed.IsRequest);
        Assert.Equal((ushort)7, parsed.Sequence);
        Assert.Equal(0xE123456789ABCDEFUL, parsed.Transmit);

        var reply = TimingPacket.Reply(parsed, 0x0102030405060708UL, 0x0102030405060709UL);
        Assert.Equal(RtpPacketTypes.TimingReply, reply.Type);
        Assert.Equal(parsed.Transmit, reply.Origin);
        var buffer = new byte[TimingPacket.Size];
        reply.Write(buffer);
        Assert.Equal(
            Hex("80 D3 00 07 00 00 00 00 E1 23 45 67 89 AB CD EF 01 02 03 04 05 06 07 08 01 02 03 04 05 06 07 09"),
            buffer);

        Assert.True(TimingPacket.TryParse(buffer, out var back));
        Assert.False(back.IsRequest);
        Assert.False(TimingPacket.TryParse(request.AsSpan(0, 31), out _));
        Assert.False(TimingPacket.TryParse(Hex("80 D4 00 07" + new string('0', 56)), out _));
    }

    [Theory]
    [InlineData("80 D5 00 01 12 34 00 05", 0x1234, 5)]
    [InlineData("80 D5 00 04 FF FE 00 03 E1 23 45 67 89 AB CD EF", 0xFFFE, 3)]
    [InlineData("80 55 00 01 00 0A 00 01", 10, 1)]
    public void Retransmit_request_parses_both_lengths(string hex, int first, int count)
    {
        Assert.True(RetransmitRequest.TryParse(Hex(hex), out var request));
        Assert.Equal(new RetransmitRequest((ushort)first, (ushort)count), request);
    }

    [Theory]
    [InlineData("80 D5 00 01 12 34 00")]
    [InlineData("80 D5 00 01 12 34 00 05 00 00 00 00")]
    [InlineData("80 D4 00 01 12 34 00 05")]
    public void Retransmit_request_rejects_other_shapes(string hex) =>
        Assert.False(RetransmitRequest.TryParse(Hex(hex), out _));

    [Fact]
    public void Retransmit_request_write_round_trips_both_forms()
    {
        var buffer = new byte[16];
        var request = new RetransmitRequest(0xABCD, 2);
        Assert.Equal(8, request.Write(buffer));
        Assert.Equal(Hex("80 D5 00 01 AB CD 00 02"), buffer[..8]);
        Assert.Equal(16, request.Write(buffer, 0x0102030405060708UL));
        Assert.Equal(Hex("80 D5 00 04 AB CD 00 02 01 02 03 04 05 06 07 08"), buffer);
        Assert.True(RetransmitRequest.TryParse(buffer, out var parsed));
        Assert.Equal(request, parsed);
    }

    [Fact]
    public void Wrapped_retransmit_reply_is_80_D6_seq_original()
    {
        using var builder = new RtpAudioPacketBuilder(Key, 0);
        var buffer = new byte[RetransmitReply.HeaderSize + 200];
        var length = builder.Build(buffer.AsSpan(RetransmitReply.HeaderSize), 0xBEEF, 352, false, SilenceAlac);
        RetransmitReply.WriteHeader(buffer, buffer.AsSpan(RetransmitReply.HeaderSize, length));
        Assert.Equal(Hex("80 D6 BE EF 80 60 BE EF"), buffer[..8]);
        Assert.True(RetransmitReply.TryUnwrap(buffer.AsSpan(0, RetransmitReply.HeaderSize + length), out var original));
        Assert.Equal(length, original.Length);
        Assert.True(RtpAudioPacketBuilder.TryDecrypt(Key, original, new byte[length], out _));
    }
}
