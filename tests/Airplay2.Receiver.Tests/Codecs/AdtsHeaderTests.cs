using Airplay2.Receiver.Codecs;
using Xunit;

namespace Airplay2.Receiver.Tests.Codecs;

/// <summary>The 7-byte ADTS header of SPEC-receiver.md 7.2 (44.1 kHz stereo AAC-LC, no CRC) for known block lengths.</summary>
public class AdtsHeaderTests
{
    [Theory]
    [InlineData(0, "FFF15080 00FFFC")] // frame length 7
    [InlineData(371, "FFF15080 2F5FFC")] // 378 = 0x17A
    [InlineData(2041, "FFF15081 001FFC")] // 2048: bit 11 of the length in byte 3
    [InlineData(8184, "FFF15083 FFFFFC")] // 8191: the largest 13-bit length
    public void Write_produces_the_known_bytes(int payloadLength, string expectedHex)
    {
        var header = new byte[AdtsHeader.Size];

        AdtsHeader.Write(header, payloadLength);

        Assert.Equal(expectedHex.Replace(" ", string.Empty, StringComparison.Ordinal), Convert.ToHexString(header));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(371)]
    [InlineData(1500)]
    [InlineData(AdtsHeader.MaxPayloadLength)]
    public void Fields_decode_back_to_the_profile_rate_channels_and_frame_length(int payloadLength)
    {
        var h = new byte[AdtsHeader.Size];
        AdtsHeader.Write(h, payloadLength);

        Assert.Equal(0xFFF, (h[0] << 4) | (h[1] >> 4)); // sync word
        Assert.Equal(0, (h[1] >> 3) & 1); // ID: MPEG-4
        Assert.Equal(0, (h[1] >> 1) & 3); // layer
        Assert.Equal(1, h[1] & 1); // protection absent: no CRC
        Assert.Equal(AdtsHeader.ProfileAacLc, h[2] >> 6);
        Assert.Equal(AdtsHeader.SamplingIndex44100, (h[2] >> 2) & 0xF);
        Assert.Equal(AdtsHeader.ChannelsStereo, ((h[2] & 1) << 2) | (h[3] >> 6));
        Assert.Equal(payloadLength + AdtsHeader.Size, ((h[3] & 3) << 11) | (h[4] << 3) | (h[5] >> 5));
        Assert.Equal(0x7FF, ((h[5] & 0x1F) << 6) | (h[6] >> 2)); // buffer fullness: VBR
        Assert.Equal(0, h[6] & 3); // one raw data block
    }

    [Fact]
    public void Frame_prepends_the_header_to_the_payload()
    {
        byte[] payload = [0x21, 0x10, 0x05, 0x00];

        var frame = AdtsHeader.Frame(payload);

        Assert.Equal(AdtsHeader.Size + payload.Length, frame.Length);
        var header = new byte[AdtsHeader.Size];
        AdtsHeader.Write(header, payload.Length);
        Assert.Equal(header, frame[..AdtsHeader.Size]);
        Assert.Equal(payload, frame[AdtsHeader.Size..]);
    }

    [Fact]
    public void Lengths_outside_the_13_bit_field_and_short_destinations_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdtsHeader.Write(new byte[AdtsHeader.Size], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdtsHeader.Write(new byte[AdtsHeader.Size], AdtsHeader.MaxPayloadLength + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdtsHeader.Frame(new byte[AdtsHeader.MaxPayloadLength + 1]));
        Assert.Throws<ArgumentException>(() => AdtsHeader.Write(new byte[AdtsHeader.Size - 1], 10));
    }
}
