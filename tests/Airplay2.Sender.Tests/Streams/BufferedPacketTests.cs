using System.Buffers.Binary;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Xunit;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>Packet layout of the buffered stream against SPEC-audio.md 4.2, the anchor time encoding of 4.3, and the format table of 4.1.</summary>
public sealed class BufferedPacketTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Packet_has_a_length_prefix_including_itself_a_marker_with_a_23_bit_sequence_the_timestamp_and_the_format_ssrc()
    {
        var frame = new AlacEncoder().Encode(PlaybackSine());
        using var builder = new RtpAudioPacketBuilder(Key, BufferedAudioPacket.SsrcAlac44100S16);
        var buffer = new byte[BufferedAudioPacket.PacketLength(frame.Length)];
        var length = BufferedAudioPacket.Build(builder, buffer, 0x5A_BCDE, 0x0001_5888, frame);

        Assert.Equal(2 + 12 + frame.Length + 16 + 8, length);
        Assert.Equal(length, BinaryPrimitives.ReadUInt16BigEndian(buffer)); // includes the two length bytes [sps] ap2_buffered_audio_processor.c:242-260
        Assert.Equal(0x80, buffer[2]); // V=2
        Assert.Equal(new byte[] { 0x80 | 0x5A, 0xBC, 0xDE }, buffer[3..6]); // marker always set, then the 23-bit sequence
        Assert.Equal(new byte[] { 0x00, 0x01, 0x58, 0x88 }, buffer[6..10]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFA, 0xCE }, buffer[10..14]); // ALAC_44100_S16_2 [sps] player.h:146
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, buffer[^8..]); // first nonce counter value, little-endian

        var rtp = buffer.AsSpan(2, length - 2);
        Assert.Equal(0x5ABCDEu, BufferedAudioPacket.ReadSequence(rtp));
        Assert.Equal(BufferedAudioPacket.SsrcAlac44100S16, BufferedAudioPacket.ReadSsrc(rtp));

        // ChaCha20-Poly1305 with AAD = RTP bytes 4..11 (timestamp + SSRC) and nonce = 4 zero bytes || the 8-byte tail.
        var n = frame.Length;
        var plain = new byte[n];
        var nonce = new byte[12];
        rtp[^8..].CopyTo(nonce.AsSpan(4));
        Assert.True(ChaCha20Poly1305Aead.TryDecrypt(Key, nonce, rtp.Slice(12, n), rtp.Slice(12 + n, 16), rtp.Slice(4, 8), plain));
        Assert.Equal(frame, plain);

        // Bytes 1..3 are outside the AAD: the realtime decryptor recovers the frame too.
        var again = new byte[rtp.Length];
        Assert.True(RtpAudioPacketBuilder.TryDecrypt(Key, rtp, again, out var m));
        Assert.Equal(frame, again[..m]);
    }

    [Fact]
    public void The_nonce_counter_advances_per_packet_and_the_aad_covers_timestamp_and_ssrc()
    {
        var frame = new byte[] { 1, 2, 3, 4, 5 };
        using var builder = new RtpAudioPacketBuilder(Key, BufferedAudioPacket.SsrcAlac44100S16);
        var a = new byte[64];
        var b = new byte[64];
        var la = BufferedAudioPacket.Build(builder, a, 1, 352, frame);
        var lb = BufferedAudioPacket.Build(builder, b, 2, 704, frame);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(a.AsSpan(la - 8)));
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(lb - 8)));

        // Changing the SSRC (inside the AAD) breaks authentication; changing the sequence (outside) does not.
        var tampered = a[2..la];
        tampered[11] ^= 1;
        Assert.False(RtpAudioPacketBuilder.TryDecrypt(Key, tampered, new byte[64], out _));
        tampered = a[2..la];
        tampered[3] ^= 0x7F;
        Assert.True(RtpAudioPacketBuilder.TryDecrypt(Key, tampered, new byte[64], out _));
    }

    [Theory]
    [InlineData(0u, 0x80, 0x00, 0x00)]
    [InlineData(0x7FFFFFu, 0xFF, 0xFF, 0xFF)]
    [InlineData(0x800000u, 0x80, 0x00, 0x00)] // masked to 23 bits
    [InlineData(0x012345u, 0x81, 0x23, 0x45)]
    public void Sequence_is_masked_to_23_bits_with_the_marker_bit_set(uint sequence, byte b1, byte b2, byte b3)
    {
        var header = new byte[12];
        BufferedAudioPacket.WriteSequence(header, sequence);
        Assert.Equal(new[] { b1, b2, b3 }, header[1..4]);
        Assert.Equal(sequence & 0x7FFFFF, BufferedAudioPacket.ReadSequence(header));
    }

    [Theory]
    [InlineData(5u, 3u, 2)]
    [InlineData(3u, 5u, -2)]
    [InlineData(0u, 0x7FFFFFu, 1)] // wraps like [sps] a_minus_b_mod23
    [InlineData(0x7FFFFFu, 0u, -1)]
    public void Sequence_distance_is_modulo_2_to_the_23(uint a, uint b, int expected) => Assert.Equal(expected, BufferedAudioPacket.Distance(a, b));

    [Fact]
    public void TrySplit_reads_the_length_prefix_and_waits_for_the_whole_packet()
    {
        using var builder = new RtpAudioPacketBuilder(Key, BufferedAudioPacket.SsrcAlac44100S16);
        var stream = new byte[200];
        var l1 = BufferedAudioPacket.Build(builder, stream, 7, 0, new byte[] { 9, 9, 9 });
        var l2 = BufferedAudioPacket.Build(builder, stream.AsSpan(l1), 8, 352, new byte[] { 8, 8 });

        Assert.False(BufferedAudioPacket.TrySplit(stream.AsSpan(0, 1), out _, out _));
        Assert.False(BufferedAudioPacket.TrySplit(stream.AsSpan(0, l1 - 1), out _, out _));
        Assert.True(BufferedAudioPacket.TrySplit(stream.AsSpan(0, l1 + l2), out var first, out var consumed));
        Assert.Equal(l1, consumed);
        Assert.Equal(l1 - 2, first.Length);
        Assert.Equal(7u, BufferedAudioPacket.ReadSequence(first));
        Assert.True(BufferedAudioPacket.TrySplit(stream.AsSpan(l1, l2), out var second, out _));
        Assert.Equal(8u, BufferedAudioPacket.ReadSequence(second));
        Assert.Throws<InvalidDataException>(() => BufferedAudioPacket.TrySplit(new byte[] { 0, 10, 0, 0 }, out _, out _));
    }

    [Fact]
    public void Build_rejects_empty_and_oversized_frames()
    {
        using var builder = new RtpAudioPacketBuilder(Key, 0);
        var big = new byte[ushort.MaxValue + 100];
        Assert.Throws<ArgumentException>(() => BufferedAudioPacket.Build(builder, big, 0, 0, ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentException>(() => BufferedAudioPacket.Build(builder, big, 0, 0, new byte[BufferedAudioPacket.MaxFrameSize + 1]));
        Assert.Throws<ArgumentException>(() => BufferedAudioPacket.Build(builder, new byte[40], 0, 0, new byte[10]));
        Assert.Equal(ushort.MaxValue, BufferedAudioPacket.Build(builder, big, 0, 0, new byte[BufferedAudioPacket.MaxFrameSize]));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(500_000_000UL)]
    [InlineData(999_999_999UL)]
    [InlineData(1_760_000_123_456_789_012UL)]
    public void Network_time_fraction_has_its_msb_at_half_a_second_and_round_trips_as_shairport_reads_it(ulong nanos)
    {
        var (seconds, fraction) = NetworkTime.FromNanoseconds(nanos);
        Assert.Equal(nanos / 1_000_000_000UL, seconds);
        var back = NetworkTime.ToNanoseconds(seconds, fraction);
        Assert.InRange((long)(nanos - back), 0, 1); // [sps] rtsp.c:1437-1445 keeps 32 bits of the fraction
        if (nanos % 1_000_000_000UL == 500_000_000UL)
        {
            Assert.Equal(0x8000_0000_0000_0000UL, fraction); // "Units are (1/2^64)" [ap2r] session_properties.py:97
        }
    }

    [Fact]
    public void Formats_carry_the_ssrc_codes_and_setup_fields_of_spec_audio_4_1()
    {
        var alac16 = BufferedAudioFormat.Alac44100S16;
        Assert.Equal(0x0000FACEu, alac16.Ssrc);
        Assert.Equal(0x40000u, alac16.AudioFormat);
        Assert.Equal((2, 44100, 352), (alac16.CompressionType, alac16.SampleRate, alac16.FramesPerPacket));
        var alac24 = BufferedAudioFormat.Alac48000S24;
        Assert.Equal(0x15000000u, alac24.Ssrc);
        Assert.Equal(0x200000u, alac24.AudioFormat);
        Assert.Equal((48000, 24), (alac24.SampleRate, alac24.BitDepth));

        var realtime = new StreamSetupRequest(StreamType.Realtime, Key, 6001);
        var buffered = alac24.Apply(realtime);
        Assert.Equal(StreamType.Buffered, buffered.Type);
        Assert.Equal((2, 0x200000u, 48000, 352), (buffered.CompressionType, buffered.AudioFormat, buffered.SampleRate, buffered.SamplesPerFrame));
        Assert.Same(Key, buffered.StreamKey);
        Assert.Equal(6001, buffered.ControlPort);

        // The SETUP body the session sends for it: type 103 with audioFormat and no latencyMin/Max.
        var body = SessionRequests.StreamSetupBody(alac16.Apply(realtime), 42);
        var stream = (Airplay2.Sender.Plist.PlistDictionary)body.GetArray("streams")![0]!;
        Assert.Equal(103L, stream.GetInt64("type"));
        Assert.Equal(0x40000L, stream.GetInt64("audioFormat"));
        Assert.Null(stream.GetInt64("latencyMin"));
    }

    [Fact]
    public void Escape_frame_24_bit_has_the_alac_uncompressed_layout()
    {
        var samples = new int[352 * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = ((i * 7919) % 0xFFFFFF) - 0x800000;
        }

        var frame = new byte[AlacEscapeFrame.Size24(352, partial: false)];
        var length = AlacEscapeFrame.WriteStereo24(samples, 352, frame);
        Assert.Equal((23 + (352 * 48) + 3 + 7) / 8, length);

        var bits = new BitReader(frame);
        Assert.Equal(1u, bits.Read(3)); // ID_CPE
        Assert.Equal(0u, bits.Read(4)); // tag
        Assert.Equal(0u, bits.Read(12));
        Assert.Equal(0u, bits.Read(1)); // not partial
        Assert.Equal(0u, bits.Read(2)); // bytes shifted
        Assert.Equal(1u, bits.Read(1)); // escape [alac] ALACEncoder.cpp:743
        foreach (var sample in samples)
        {
            var raw = bits.Read(24);
            Assert.Equal(sample, ((int)(raw << 8)) >> 8);
        }

        Assert.Equal(7u, bits.Read(3)); // ID_END

        var partial = new byte[AlacEscapeFrame.Size24(10, partial: true)];
        AlacEscapeFrame.WriteStereo24(samples.AsSpan(0, 20), 352, partial);
        bits = new BitReader(partial);
        bits.Read(19);
        Assert.Equal(1u, bits.Read(1)); // partial
        bits.Read(3);
        Assert.Equal(10u, bits.Read(32)); // sample count
    }

    private static short[] PlaybackSine()
    {
        var pcm = new short[352 * 2];
        for (var i = 0; i < 352; i++)
        {
            pcm[2 * i] = (short)(8000 * Math.Sin(i * 0.05));
            pcm[(2 * i) + 1] = (short)(6000 * Math.Cos(i * 0.03));
        }

        return pcm;
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public uint Read(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++)
            {
                var bit = (_data[_position >> 3] >> (7 - (_position & 7))) & 1;
                value = (value << 1) | (uint)bit;
                _position++;
            }

            return value;
        }
    }
}
