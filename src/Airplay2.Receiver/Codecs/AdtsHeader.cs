namespace Airplay2.Receiver.Codecs;

/// <summary>
/// The 7-byte ADTS header prepended to each raw AAC block before decoding (SPEC-receiver.md 5.2, 7.2;
/// <c>[sps] ap2_buffered_audio_processor.c:41-90</c>): sync word, MPEG-4 AAC-LC profile, the 44.1 kHz sampling index,
/// two channels, the frame length including the header, no CRC.
/// </summary>
/// <remarks>
/// Layout (ISO/IEC 14496-3 1.A.2.2, <c>[sps] ap2_buffered_audio_processor.c:82-89</c>): <c>FF F1</c> (sync word, ID 0 =
/// MPEG-4, layer 0, protection absent), <c>50</c> (profile 1, sampling index 4, channel bits 2..), then the channel
/// configuration's low bits and the 13-bit frame length, the buffer fullness <c>0x7FF</c> (variable bit rate) and zero
/// raw data blocks beyond the first. shairport-sync writes the ID bit as 1 (<c>FF F9</c>, MPEG-2); libavcodec accepts
/// both, we use the MPEG-4 value its own ADTS muxer writes.
/// </remarks>
public static class AdtsHeader
{
    /// <summary>The header length without CRC.</summary>
    public const int Size = 7;

    /// <summary>The MPEG-4 sampling frequency index of 44 100 Hz.</summary>
    public const int SamplingIndex44100 = 4;

    /// <summary>The <c>profile</c> field of AAC-LC (object type 2 minus one).</summary>
    public const int ProfileAacLc = 1;

    /// <summary>The channel configuration of stereo.</summary>
    public const int ChannelsStereo = 2;

    /// <summary>The largest frame length (header included) the 13-bit field can carry.</summary>
    public const int MaxFrameLength = 0x1FFF;

    /// <summary>The largest raw block <see cref="Write"/> accepts (<see cref="MaxFrameLength"/> minus the header).</summary>
    public const int MaxPayloadLength = MaxFrameLength - Size;

    /// <summary>
    /// Writes the header for a raw AAC block of <paramref name="payloadLength"/> bytes into <paramref name="destination"/>
    /// (at least <see cref="Size"/> bytes); the frame length field counts the header itself (SPEC-receiver.md 7.2).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadLength"/> is negative or above <see cref="MaxPayloadLength"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Size"/>.</exception>
    public static void Write(Span<byte> destination, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength, MaxPayloadLength);
        if (destination.Length < Size)
        {
            throw new ArgumentException($"The ADTS header needs {Size} bytes.", nameof(destination));
        }

        var frameLength = payloadLength + Size;
        destination[0] = 0xFF;
        destination[1] = 0xF1;
        destination[2] = (byte)((ProfileAacLc << 6) | (SamplingIndex44100 << 2) | (ChannelsStereo >> 2));
        destination[3] = (byte)(((ChannelsStereo & 0x3) << 6) | (frameLength >> 11));
        destination[4] = (byte)((frameLength >> 3) & 0xFF);
        destination[5] = (byte)(((frameLength & 0x7) << 5) | 0x1F);
        destination[6] = 0xFC;
    }

    /// <summary>The header plus <paramref name="payload"/> as one ADTS frame (the shape <see cref="IAacDecoder.TryDecode"/> takes; SPEC-receiver.md 5.2, 7.2).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MaxPayloadLength"/>.</exception>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, MaxPayloadLength, nameof(payload));
        var frame = new byte[Size + payload.Length];
        Write(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(Size));
        return frame;
    }
}
