namespace Airplay2.Receiver.Codecs;

/// <summary>
/// The 7-byte ADTS header prepended to each raw AAC block before decoding (SPEC-receiver.md 5.2, 7.2;
/// <c>[sps] ap2_buffered_audio_processor.c:41-90</c>): sync word, MPEG-4 AAC-LC profile, the 44.1 kHz sampling index,
/// two channels, the frame length including the header, no CRC.
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the writer is the wave 1 <c>aac-decoder</c> package (SPEC-receiver.md 13).
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

    /// <summary>Writes the header for a raw AAC block of <paramref name="payloadLength"/> bytes into <paramref name="destination"/> (at least <see cref="Size"/> bytes).</summary>
    public static void Write(Span<byte> destination, int payloadLength) => throw new NotImplementedException("SPEC-receiver.md 7.2, wave 1");

    /// <summary>The header plus <paramref name="payload"/> as one ADTS frame (the shape <see cref="IAacDecoder.TryDecode"/> takes).</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload) => throw new NotImplementedException("SPEC-receiver.md 7.2, wave 1");
}
