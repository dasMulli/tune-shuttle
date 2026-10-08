// Tests for Decoder/Id3v2.cs (the minimal port of symphonia-metadata's ID3v2 reader) with hand-built tags.
using System.Text;
using Librespot.Playback.Decoder;
using Xunit;

namespace Librespot.Tests.Playback;

public class Id3v2Tests
{
    [Fact]
    public void Reads_v24_utf8_text_frames_with_multiple_values()
    {
        var tag = BuildTag(4, 0,
            Frame4("TIT2", [3, .. "Title"u8, 0]),
            Frame4("TPE1", [3, .. "A"u8, 0, .. "B"u8, 0]),
            Frame4("TXXX", [3, .. "REPLAYGAIN_TRACK_GAIN"u8, 0, .. "-6.5 dB"u8]));
        var stream = new MemoryStream([.. tag, 0xff, 0xfb]);

        var tags = Id3v2.Read(stream);

        Assert.Equal(tag.Length, stream.Position);
        Assert.Equal(
            new[]
            {
                new Tag(StandardTagKey.TrackTitle, "TIT2", new TagValue.String("Title")),
                new Tag(StandardTagKey.Artist, "TPE1", new TagValue.String("A")),
                new Tag(StandardTagKey.Artist, "TPE1", new TagValue.String("B")),
                new Tag(StandardTagKey.ReplayGainTrackGain, "TXXX:REPLAYGAIN_TRACK_GAIN", new TagValue.String("-6.5 dB")),
            },
            tags);
    }

    [Fact]
    public void Reads_v23_utf16_and_latin1_frames_and_stops_at_padding()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Ålbum")).Concat(new byte[] { 0, 0 }).ToArray();
        var tag = BuildTag(3, 0,
            Frame3("TALB", [1, .. utf16]),
            Frame3("TRCK", [0, .. "7/9"u8, 0]),
            Frame3("TPOS", [0, 0x01, .. "2"u8]), // control characters are dropped from ISO-8859-1 text
            new byte[32]); // padding

        var tags = Id3v2.Read(new MemoryStream(tag));

        Assert.Equal(
            new[]
            {
                new Tag(StandardTagKey.Album, "TALB", new TagValue.String("Ålbum")),
                new Tag(StandardTagKey.TrackNumber, "TRCK", new TagValue.String("7/9")),
                new Tag(StandardTagKey.DiscNumber, "TPOS", new TagValue.String("2")),
            },
            tags);
    }

    [Fact]
    public void Reads_v22_frames_through_the_legacy_id_table()
    {
        byte[] frame = [.. "TT2"u8, 0, 0, 6, 0, .. "Short"u8];
        var tags = Id3v2.Read(new MemoryStream(BuildTag(2, 0, frame)));
        Assert.Equal([new Tag(StandardTagKey.TrackTitle, "TIT2", new TagValue.String("Short"))], tags);
    }

    [Fact]
    public void Decodes_v23_tag_level_unsynchronisation()
    {
        // 0xFF 0x00 in the tag body is the unsynchronised form of 0xFF ("ÿ" in ISO-8859-1).
        byte[] body = [.. "TIT2"u8, 0, 0, 0, 3, 0, 0, 0, 0xff, 0x00, (byte)'x'];
        var tags = Id3v2.Read(new MemoryStream(BuildTag(3, 0x80, body)));
        Assert.Equal("ÿx", tags.Single().Value.ToString());
    }

    [Fact]
    public void Rejects_unsupported_versions()
    {
        Assert.Throws<NotSupportedException>(() => Id3v2.Read(new MemoryStream(BuildTag(5, 0))));
        Assert.Throws<NotSupportedException>(() => Id3v2.Read(new MemoryStream(BuildTag(2, 0x40))));
        Assert.Throws<InvalidDataException>(() => Id3v2.Read(new MemoryStream("TAG\0\0\0\0\0\0\0"u8.ToArray())));
    }

    [Fact]
    public void Utf16_without_bom_is_big_endian()
    {
        // symphonia decodes encoding 1 with encoding_rs's UTF_16BE (BOM sniffing, big endian otherwise).
        byte[] noBom = [1, .. Encoding.BigEndianUnicode.GetBytes("Ab"), 0, 0];
        byte[] utf8Bom = [1, 0xef, 0xbb, 0xbf, .. "Cd"u8];
        var tags = Id3v2.Read(new MemoryStream(BuildTag(3, 0, Frame3("TIT2", noBom), Frame3("TALB", utf8Bom))));
        Assert.Equal(["Ab", "Cd"], tags.Select(t => t.Value.ToString()));
    }

    [Fact]
    public void Compressed_or_encrypted_text_frames_fail_the_read()
    {
        // Upstream unsupported_error; frames without a parser are skipped regardless of their flags.
        byte[] compressed = [.. "TIT2"u8, .. Syncsafe(2), 0x00, 0x08, 3, (byte)'x'];
        byte[] encrypted = [.. "TIT2"u8, .. Syncsafe(2), 0x00, 0x04, 3, (byte)'x'];
        byte[] skipped = [.. "XYZW"u8, .. Syncsafe(2), 0x00, 0x08, 3, (byte)'x'];

        Assert.Contains("compressed", Assert.Throws<NotSupportedException>(() => Id3v2.Read(new MemoryStream(BuildTag(4, 0, compressed)))).Message);
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => Id3v2.Read(new MemoryStream(BuildTag(4, 0, encrypted)))).Message);
        Assert.Empty(Id3v2.Read(new MemoryStream(BuildTag(4, 0, skipped))));
    }

    [Fact]
    public void Malformed_tag_fails_the_probe()
    {
        // symphonia's probe propagates the ID3v2 reader's error instead of continuing behind the tag.
        byte[] frame = [.. "TIT2"u8, 0, 0, 0, 50, 0, 0, 3, (byte)'x'];
        var e = Assert.Throws<DecoderException>(() =>
            DecoderUtil.Probe(new MemoryStream([.. BuildTag(3, 0, frame), 0xff, 0xfb]), new DecoderHint()));
        Assert.Equal(DecoderException.Reason.AudioDecoder, e.Cause);
        Assert.Contains("malformed stream: id3v2:", e.Message);
    }

    private static byte[] Frame4(string id, byte[] body) =>
        [.. Encoding.ASCII.GetBytes(id), .. Syncsafe(body.Length), 0, 0, .. body];

    private static byte[] Frame3(string id, byte[] body) =>
        [.. Encoding.ASCII.GetBytes(id), (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, 0, 0, .. body];

    private static byte[] BuildTag(byte major, byte flags, params byte[][] frames)
    {
        var body = frames.SelectMany(f => f).ToArray();
        return [.. "ID3"u8, major, 0, flags, .. Syncsafe(body.Length), .. body];
    }

    private static byte[] Syncsafe(int value) =>
        [(byte)((value >> 21) & 0x7f), (byte)((value >> 14) & 0x7f), (byte)((value >> 7) & 0x7f), (byte)(value & 0x7f)];
}
