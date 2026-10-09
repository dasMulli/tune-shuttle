using System.Buffers.Binary;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;

namespace Airplay2.Sender.Streams;

/// <summary>
/// An audio format of the buffered stream (SPEC-audio.md 4.1, 4.2): what the stream SETUP announces
/// (<c>ct</c>, <c>audioFormat</c>, <c>sr</c>, <c>spf</c>) and the SSRC that names the codec in every packet. The receiver
/// selects its decoder from the SSRC per packet (<c>[sps] player.h:141-152</c>, <c>ap2_buffered_audio_processor.c:287-297</c>);
/// <c>audioFormat</c> is the authoritative selector in the SETUP (SPEC.md C9).
/// </summary>
/// <param name="Name">Display name (<c>[sps] player.c get_ssrc_name</c> spelling).</param>
/// <param name="Ssrc">The SSRC format code written at packet offset 8.</param>
/// <param name="AudioFormat">The stream SETUP <c>audioFormat</c> bit (bit table <c>[ot] airplay.c:2576-2611</c>).</param>
/// <param name="CompressionType">The stream SETUP <c>ct</c> (2 = ALAC in every source).</param>
/// <param name="SampleRate">The stream SETUP <c>sr</c>; also the rate of the RTP timestamps.</param>
/// <param name="FramesPerPacket">The stream SETUP <c>spf</c>; the RTP timestamp step per packet (352 for ALAC, <c>[sps] player.c:509-525</c>).</param>
/// <param name="BitDepth">Sample bit depth inside the ALAC frames.</param>
public sealed record BufferedAudioFormat(string Name, uint Ssrc, uint AudioFormat, int CompressionType, int SampleRate, int FramesPerPacket, int BitDepth)
{
    /// <summary>
    /// ALAC 44.1 kHz / 16-bit / stereo with SSRC <c>0x0000FACE</c>, the code shairport-sync made up for it ("this is made
    /// up", <c>[sps] player.h:146</c>), <c>audioFormat</c> bit 18 (0x40000). The default: it is the encoder this project has
    /// and the format of the realtime stream; whether Apple-SDK receivers accept the code is probe P16 (SPEC.md, A-Q7).
    /// </summary>
    public static BufferedAudioFormat Alac44100S16 { get; } = new("ALAC/44100/S16/2", BufferedAudioPacket.SsrcAlac44100S16, StreamSetupRequest.AudioFormatAlac44100S16Stereo, StreamSetupRequest.CompressionTypeAlac, 44100, 352, 16);

    /// <summary>
    /// ALAC 48 kHz / 24-bit / stereo with SSRC <c>0x15000000</c> (<c>[sps] player.h:141-152</c>), <c>audioFormat</c> bit 21
    /// (0x200000): what Apple senders use for lossless buffered audio (SPEC-audio.md 4.2). The caller supplies 24-bit ALAC
    /// frames (<see cref="AlacEscapeFrame.WriteStereo24"/> writes uncompressed ones); the group player's 16-bit encoder
    /// cannot feed it. Used by probe P16.
    /// </summary>
    public static BufferedAudioFormat Alac48000S24 { get; } = new("ALAC/48000/S24/2", BufferedAudioPacket.SsrcAlac48000S24, 0x200000, StreamSetupRequest.CompressionTypeAlac, 48000, 352, 24);

    /// <summary>
    /// Writes this format into a stream SETUP request: type 103, <c>ct</c>, <c>audioFormat</c>, <c>sr</c>, <c>spf</c>
    /// (SPEC-audio.md 4.1); the key, our control port and the connection id stay as given.
    /// </summary>
    public StreamSetupRequest Apply(StreamSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request with
        {
            Type = StreamType.Buffered,
            CompressionType = CompressionType,
            AudioFormat = AudioFormat,
            SampleRate = SampleRate,
            SamplesPerFrame = FramesPerPacket,
        };
    }
}

/// <summary>
/// The buffered stream's packet on the TCP data connection (SPEC-audio.md 4.2):
/// <c>len16 || 0x80 || M|seq23 || ts32 || ssrc32 || ciphertext || tag(16) || nonce[4..11](8)</c>, all big-endian. The
/// 2-byte length <b>includes itself</b> (<c>[sps] ap2_buffered_audio_processor.c:242-260</c> reads <c>data_len - 2</c>
/// bytes, <c>[ap2r] audio.py:948-954</c> the same). Byte 1 bit 7 is the RTP marker, "always set", and with the next
/// 23 bits forms the sequence number, which the receiver masks with <c>0x7FFFFF</c>
/// (<c>[sps] ap2_buffered_audio_processor.c:273-278</c>). The payload is encrypted exactly as on the realtime stream
/// (SPEC-audio.md 1.3: AAD = bytes 4..11 of the RTP packet, i.e. timestamp and SSRC; <c>[ap2r] audio.py:545-548</c>
/// "c.update(rtp.aad) necessary at least for RTP type 103"), so <see cref="RtpAudioPacketBuilder"/> builds it and only
/// bytes 1..3, which the AEAD does not cover, are rewritten.
/// </summary>
public static class BufferedAudioPacket
{
    /// <summary>Size of the length prefix.</summary>
    public const int LengthPrefixSize = 2;

    /// <summary>RTP header size (as on the realtime stream).</summary>
    public const int HeaderSize = RealtimeStreamConstants.HeaderSize;

    /// <summary>Tag plus nonce tail after the ciphertext.</summary>
    public const int TrailerSize = RealtimeStreamConstants.TrailerSize;

    /// <summary>Bytes a packet adds to its ALAC frame, length prefix included: 2 + 12 + 24.</summary>
    public const int Overhead = LengthPrefixSize + HeaderSize + TrailerSize;

    /// <summary>The largest frame that fits the 16-bit length: 65 535 - 38 bytes.</summary>
    public const int MaxFrameSize = ushort.MaxValue - Overhead;

    /// <summary>Sequence numbers are 23 bits wide (<c>[sps] rtsp.c:1349, 1376-1378</c> masks <c>FLUSHBUFFERED</c> values the same way).</summary>
    public const uint SequenceMask = 0x7FFFFF;

    /// <summary>The marker bit in byte 1 ("always set", <c>[sps] ap2_buffered_audio_processor.c:273-276</c>).</summary>
    public const byte MarkerBit = 0x80;

    /// <summary><c>ALAC_44100_S16_2 = 0x0000FACE</c> ("this is made up", <c>[sps] player.h:146</c>).</summary>
    public const uint SsrcAlac44100S16 = 0x0000FACE;

    /// <summary><c>ALAC_48000_S24_2 = 0x15000000</c> (<c>[sps] player.h:141-152</c>).</summary>
    public const uint SsrcAlac48000S24 = 0x15000000;

    /// <summary><c>AAC_44100_F24_2 = 0x16000000</c> (<c>[sps] player.h:141-152</c>); not produced here (no AAC encoder).</summary>
    public const uint SsrcAacLc44100 = 0x16000000;

    /// <summary><c>AAC_48000_F24_2 = 0x17000000</c> (<c>[sps] player.h:141-152</c>); not produced here.</summary>
    public const uint SsrcAacLc48000 = 0x17000000;

    /// <summary>The length of a packet (prefix included) carrying an ALAC frame of <paramref name="frameLength"/> bytes.</summary>
    public static int PacketLength(int frameLength) => Overhead + frameLength;

    /// <summary>
    /// Builds one packet into <paramref name="destination"/> and returns its length (prefix included). Consumes one nonce
    /// counter value of <paramref name="builder"/> (SPEC.md D9: never reset under one key), whose SSRC must be the format code.
    /// </summary>
    /// <param name="builder">The receiver's builder (its key and nonce counter; its SSRC is the format code).</param>
    /// <param name="destination">At least <see cref="PacketLength"/> bytes, not overlapping the frame.</param>
    /// <param name="sequence">The 23-bit wire sequence number (masked).</param>
    /// <param name="timestamp">The RTP timestamp of the frame's first sample.</param>
    /// <param name="frame">The plaintext ALAC frame, 1..<see cref="MaxFrameSize"/> bytes.</param>
    /// <exception cref="ArgumentException">The frame is empty or too large, or the destination is too small.</exception>
    public static int Build(RtpAudioPacketBuilder builder, Span<byte> destination, uint sequence, uint timestamp, ReadOnlySpan<byte> frame)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (frame.IsEmpty || frame.Length > MaxFrameSize)
        {
            throw new ArgumentException($"A buffered packet carries 1..{MaxFrameSize} bytes of ALAC.", nameof(frame));
        }

        var length = PacketLength(frame.Length);
        if (destination.Length < length)
        {
            throw new ArgumentException($"Destination holds {destination.Length} bytes, the packet needs {length}.", nameof(destination));
        }

        var rtp = destination[LengthPrefixSize..length];
        builder.Build(rtp, unchecked((ushort)sequence), timestamp, marker: true, frame);
        WriteSequence(rtp, sequence);
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)length);
        return length;
    }

    /// <summary>Writes bytes 1..3 of an RTP header as <c>0x80 | seq[22..16], seq[15..8], seq[7..0]</c> (SPEC-audio.md 4.2).</summary>
    public static void WriteSequence(Span<byte> rtpHeader, uint sequence)
    {
        if (rtpHeader.Length < HeaderSize)
        {
            throw new ArgumentException("An RTP header is 12 bytes.", nameof(rtpHeader));
        }

        sequence &= SequenceMask;
        rtpHeader[1] = (byte)(MarkerBit | (sequence >> 16));
        rtpHeader[2] = (byte)(sequence >> 8);
        rtpHeader[3] = (byte)sequence;
    }

    /// <summary>Reads the 23-bit sequence number of an RTP packet (<c>ntohl(packet[0..3]) &amp; 0x7FFFFF</c>).</summary>
    public static uint ReadSequence(ReadOnlySpan<byte> rtpPacket) => BinaryPrimitives.ReadUInt32BigEndian(rtpPacket) & SequenceMask;

    /// <summary>Reads the SSRC (format code) at offset 8 of an RTP packet.</summary>
    public static uint ReadSsrc(ReadOnlySpan<byte> rtpPacket) => BinaryPrimitives.ReadUInt32BigEndian(rtpPacket[8..]);

    /// <summary>
    /// Receiver side (tests, the fake buffered reader): splits one length-prefixed packet off the front of
    /// <paramref name="stream"/>. Returns false while fewer bytes than the prefix announces are available.
    /// </summary>
    /// <param name="stream">Bytes read from the TCP connection.</param>
    /// <param name="rtpPacket">The RTP packet without the prefix.</param>
    /// <param name="consumed">Bytes to drop from the front of <paramref name="stream"/>.</param>
    /// <exception cref="InvalidDataException">The prefix announces fewer bytes than a header and trailer need.</exception>
    public static bool TrySplit(ReadOnlySpan<byte> stream, out ReadOnlySpan<byte> rtpPacket, out int consumed)
    {
        rtpPacket = default;
        consumed = 0;
        if (stream.Length < LengthPrefixSize)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(stream);
        if (length < Overhead + 1)
        {
            throw new InvalidDataException($"A buffered packet of {length} bytes (prefix included) is too short.");
        }

        if (stream.Length < length)
        {
            return false;
        }

        rtpPacket = stream[LengthPrefixSize..length];
        consumed = length;
        return true;
    }

    /// <summary>
    /// <c>a - b</c> in the 23-bit sequence space as a signed distance (<c>[sps] a_minus_b_mod23</c>, used for the flush
    /// end points, <c>ap2_buffered_audio_processor.c:343-401</c>).
    /// </summary>
    public static int Distance(uint a, uint b)
    {
        var d = (int)((a - b) & SequenceMask);
        return d >= 0x400000 ? d - 0x800000 : d;
    }
}

/// <summary>
/// Uncompressed ("escape") ALAC frames for the 24-bit formats, the one ALAC frame kind the 16-bit encoder port does not
/// write: <c>ID_CPE(3)=1, tag(4)=0, unused(12)=0, partial(1), bytesShifted(2)=0, escape(1)=1, [sampleCount(32) if partial],
/// then per frame L(24) R(24), ID_END(3)=7, zero padding to a byte</c> (<c>[alac] ALACEncoder.cpp:731-790</c>
/// <c>EncodeStereoEscape</c> and <c>Encode</c> at 1036-1040, 1141-1144). Lets probe P16 send ALAC 48 kHz/24-bit
/// (<see cref="BufferedAudioFormat.Alac48000S24"/>) without a 24-bit compressor.
/// </summary>
public static class AlacEscapeFrame
{
    /// <summary>The size of a stereo 24-bit escape frame of <paramref name="frames"/> sample frames.</summary>
    public static int Size24(int frames, bool partial) => (int)(((3 + 4 + 12 + 4 + (partial ? 32 : 0) + (frames * 48L) + 3) + 7) / 8);

    /// <summary>
    /// Writes a stereo 24-bit escape frame. Samples are interleaved left/right, each a signed 24-bit value in an
    /// <see cref="int"/>. A frame shorter than <paramref name="frameSize"/> is written as a partial frame (with its count).
    /// </summary>
    /// <param name="interleaved">2 x frames samples in -2^23..2^23-1.</param>
    /// <param name="frameSize">The stream's frames per packet (352).</param>
    /// <param name="destination">At least <see cref="Size24"/> bytes.</param>
    /// <returns>The frame length in bytes.</returns>
    public static int WriteStereo24(ReadOnlySpan<int> interleaved, int frameSize, Span<byte> destination)
    {
        if ((interleaved.Length & 1) != 0 || interleaved.IsEmpty || interleaved.Length / 2 > frameSize)
        {
            throw new ArgumentException($"1..{frameSize} interleaved stereo frames are required.", nameof(interleaved));
        }

        var frames = interleaved.Length / 2;
        var partial = frames != frameSize;
        var size = Size24(frames, partial);
        if (destination.Length < size)
        {
            throw new ArgumentException($"Destination holds {destination.Length} bytes, the frame needs {size}.", nameof(destination));
        }

        destination[..size].Clear();
        var bits = new BitWriter(destination);
        bits.Write(1, 3); // ID_CPE
        bits.Write(0, 4); // element instance tag
        bits.Write(0, 12); // unused
        bits.Write(partial ? 1u : 0u, 1);
        bits.Write(0, 2); // bytes shifted
        bits.Write(1, 1); // escape: not compressed
        if (partial)
        {
            bits.Write((uint)frames, 32);
        }

        foreach (var sample in interleaved)
        {
            bits.Write((uint)sample & 0xFFFFFF, 24);
        }

        bits.Write(7, 3); // ID_END
        return size;
    }

    /// <summary>A big-endian bit writer over a cleared span.</summary>
    private ref struct BitWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private long _position;

        public void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                {
                    _buffer[(int)(_position >> 3)] |= (byte)(0x80 >> (int)(_position & 7));
                }

                _position++;
            }
        }
    }
}

/// <summary>Conversions between PTP nanoseconds and the <c>networkTimeSecs</c>/<c>networkTimeFrac</c> pair of SETRATEANCHORTIME.</summary>
public static class NetworkTime
{
    private const ulong NanosPerSecond = 1_000_000_000UL;

    /// <summary>
    /// Splits PTP nanoseconds into whole seconds and a 64-bit binary fraction whose MSB is 1/2 s ("Units are (1/2^64)",
    /// <c>[ap2r] session_properties.py:97</c>; <c>[sps] rtsp.c:1437-1445</c> converts back via <c>(frac &gt;&gt; 32) * 1e9 &gt;&gt; 32</c>).
    /// </summary>
    public static (ulong Seconds, ulong Fraction) FromNanoseconds(ulong nanoseconds)
    {
        var seconds = nanoseconds / NanosPerSecond;
        var rest = nanoseconds % NanosPerSecond;
        var fraction = (ulong)(((UInt128)rest << 64) / NanosPerSecond);
        return (seconds, fraction);
    }

    /// <summary>The inverse of <see cref="FromNanoseconds"/> as shairport-sync computes it (32-bit precision of the fraction).</summary>
    public static ulong ToNanoseconds(ulong seconds, ulong fraction) => (seconds * NanosPerSecond) + (((fraction >> 32) * NanosPerSecond) >> 32);
}

/// <summary>Constants of the buffered stream.</summary>
public static class BufferedStreamConstants
{
    /// <summary>
    /// <c>audioBufferSize</c> assumed when the stream SETUP answer has none. shairport-sync and airplay2-receiver answer
    /// 8 MiB (<c>[sps] rtsp.c:4175, 2962-2963</c>; <c>[ap2r] stream.py:421-423</c>); an unknown receiver gets a quarter of that.
    /// </summary>
    public const long DefaultAudioBufferSize = 2L << 20;

    /// <summary>ChaCha20-Poly1305 key length of <c>shk</c>.</summary>
    public const int StreamKeySize = ChaCha20Poly1305Aead.KeySize;
}
