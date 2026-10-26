using System.Buffers.Binary;
using System.Text;
using Airplay2.Sender.Session;
using Xunit;

namespace Airplay2.Sender.Tests.Session;

/// <summary>
/// <see cref="DmapDecoder"/> and the <c>SET_PARAMETER</c> body parsers of <see cref="ReceiverParameters"/> against
/// SPEC-receiver.md 4.8 and 11.1 and SPEC-control.md 5.2-5.4.
/// </summary>
public sealed class DmapDecoderTests
{
    [Fact]
    public void Encoder_output_of_the_d12_form_decodes_to_the_same_metadata()
    {
        // SPEC.md D12 / SPEC-control.md 5.2: mlit { minm, asar, asal, astm } plus the optional numeric tags and genre.
        var metadata = new TrackMetadata("Tïtle ♪", "Ärtist", "Album", TimeSpan.FromMilliseconds(215_123), 7, 2, "Jazz");

        var item = DmapDecoder.Decode(DmapEncoder.EncodeListingItem(metadata));

        Assert.Equal(metadata, item.ToTrackMetadata());
        Assert.NotNull(item.OtherTags);
        Assert.Empty(item.OtherTags);
    }

    [Fact]
    public void Encoder_output_of_the_minimal_form_round_trips()
    {
        var metadata = new TrackMetadata("Title", "Artist", "Album", null);

        Assert.Equal(metadata, DmapDecoder.Decode(DmapEncoder.EncodeListingItem(metadata)).ToTrackMetadata());
    }

    [Fact]
    public void Captured_style_body_with_unknown_tags_is_decoded_and_unknown_tags_are_kept()
    {
        // What iOS sends (SPEC-control.md 5.2 table): unknown tags of every width around the known ones.
        var body = Mlit(
            Item("caps", 0x01),
            Item("minm", "Song"u8.ToArray()),
            Item("mper", 0, 0, 0, 0, 0, 0, 0x12, 0x34),
            Item("asar", "Band"u8.ToArray()),
            Item("asal", "Record"u8.ToArray()),
            Item("astm", 0x00, 0x03, 0x0D, 0x40), // 200 000 ms
            Item("astn", 0x00, 0x05),
            Item("asdk", 0x00),
            Item("ascp", "Writer"u8.ToArray()),
            Item("mlit", Item("zzzz", 9)), // a nested container is the raw bytes of an unknown tag
            Item("asgn", "Rock"u8.ToArray()));

        var item = DmapDecoder.Decode(body);

        Assert.Equal("Song", item.Title);
        Assert.Equal("Band", item.Artist);
        Assert.Equal("Record", item.Album);
        Assert.Equal(TimeSpan.FromSeconds(200), item.Duration);
        Assert.Equal(5, item.TrackNumber);
        Assert.Null(item.DiscNumber);
        Assert.Equal("Rock", item.Genre);
        Assert.NotNull(item.OtherTags);
        Assert.Equal(["ascp", "asdk", "caps", "mlit", "mper"], item.OtherTags.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([0x01], item.OtherTags["caps"]);
        Assert.Equal("Writer", Encoding.UTF8.GetString(item.OtherTags["ascp"]));
        Assert.Equal(0x1234UL, DmapDecoder.ReadUnsigned(item.OtherTags["mper"]));
        Assert.Equal(1UL, DmapDecoder.ReadUnsigned(item.OtherTags["caps"]));
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xC8 }, 200)]
    [InlineData(new byte[] { 0xC8 }, 200)]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0xC8 }, 200)]
    public void Integers_are_read_by_their_width(byte[] value, long milliseconds)
    {
        var item = DmapDecoder.Decode(Mlit(Item("astm", value)));

        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), item.Duration);
    }

    [Fact]
    public void Integer_of_an_odd_width_is_kept_as_an_unknown_tag()
    {
        var item = DmapDecoder.Decode(Mlit(Item("astn", 0, 0, 1)));

        Assert.Null(item.TrackNumber);
        Assert.NotNull(item.OtherTags);
        Assert.Equal([0, 0, 1], item.OtherTags["astn"]);
        Assert.Null(DmapDecoder.ReadUnsigned([0, 0, 1]));
    }

    [Fact]
    public void Empty_container_decodes_to_an_empty_item()
    {
        var item = DmapDecoder.Decode(Mlit());

        Assert.Equal(new TrackMetadata(null, null, null, null), item.ToTrackMetadata());
    }

    [Fact]
    public void Truncated_body_is_rejected()
    {
        var body = DmapEncoder.EncodeListingItem(new TrackMetadata("Title", "Artist", "Album", TimeSpan.FromSeconds(1)));

        for (var cut = 1; cut < body.Length; cut++)
        {
            var truncated = body[..^cut];
            Assert.Throws<FormatException>(() => DmapDecoder.Decode(truncated));
            Assert.False(DmapDecoder.TryDecode(truncated, out var none));
            Assert.Null(none);
        }
    }

    [Fact]
    public void Item_length_past_the_end_is_rejected_even_when_the_outer_length_is_consistent()
    {
        var inner = Item("minm", "abc"u8.ToArray());
        inner[7] = 200; // the item claims 200 bytes, the body has 3
        var body = Mlit(inner);

        Assert.Throws<FormatException>(() => DmapDecoder.Decode(body));
    }

    [Fact]
    public void Trailing_partial_header_is_rejected()
    {
        var body = Mlit(Item("minm", "abc"u8.ToArray()), [0x61, 0x73, 0x61]);

        Assert.Throws<FormatException>(() => DmapDecoder.Decode(body));
    }

    [Fact]
    public void Outer_tag_other_than_mlit_is_rejected()
    {
        var body = Mlit(Item("minm", "abc"u8.ToArray()));
        "mcon"u8.CopyTo(body);

        Assert.Throws<FormatException>(() => DmapDecoder.Decode(body));
        Assert.False(DmapDecoder.TryDecode(body, out _));
    }

    [Fact]
    public void Short_or_empty_body_is_rejected()
    {
        Assert.Throws<FormatException>(() => DmapDecoder.Decode([]));
        Assert.Throws<FormatException>(() => DmapDecoder.Decode("mlit"u8));
    }

    [Fact]
    public void Bytes_after_the_declared_outer_length_are_ignored()
    {
        // shairport-sync does not check the outer length (SPEC-control.md 5.2); a shorter one than the body bounds the walk.
        byte[] body = [.. Mlit(Item("minm", "abc"u8.ToArray())), 0xFF, 0xFF];

        Assert.True(DmapDecoder.TryDecode(body, out var item));
        Assert.Equal("abc", item.Title);
    }

    [Fact]
    public void Text_parameters_volume_is_parsed_in_both_receiver_shapes()
    {
        Assert.Equal(-24.0, ReceiverParameters.ParseVolume("volume: -24.000000\r\n"));
        Assert.Equal(-144.0, ReceiverParameters.ParseVolume("\r\nvolume: -144.0\r\n"));
        Assert.Equal(-0.3, ReceiverParameters.ParseVolume("volume:-0.3"));
        Assert.Null(ReceiverParameters.ParseVolume("progress: 1/2/3\r\n"));
        Assert.Null(ReceiverParameters.ParseVolume("volume: loud\r\n"));
        Assert.Null(ReceiverParameters.ParseVolume("volume: NaN\r\n"));
        Assert.Null(ReceiverParameters.ParseVolume(string.Empty));
    }

    [Fact]
    public void Text_parameters_progress_is_parsed_with_the_rtp_time_key()
    {
        var progress = ReceiverParameters.ParseProgress("progress: 61815/77175/8015175\r\n", rtpTime: 77175);

        Assert.Equal(new ProgressInfo(61815, 77175, 8015175, RtpTime: 77175), progress);
        Assert.Equal(new ProgressInfo(1, 2, 3), ReceiverParameters.ParseProgress("progress: 1 / 2 / 3"));
        Assert.Null(ReceiverParameters.ParseProgress("progress: 1/2\r\n"));
        Assert.Null(ReceiverParameters.ParseProgress("progress: a/b/c\r\n"));
        Assert.Null(ReceiverParameters.ParseProgress("progress: 1/2/99999999999\r\n"));
        Assert.Null(ReceiverParameters.ParseProgress("volume: -3.0\r\n"));
    }

    [Fact]
    public void Progress_elapsed_and_duration_are_rtp_differences_at_44100()
    {
        var progress = new ProgressInfo(44100, 44100 + (3 * 44100) + 22050, 44100 + (180 * 44100));

        Assert.Equal(TimeSpan.FromSeconds(3.5), ReceiverParameters.Elapsed(progress));
        Assert.Equal(TimeSpan.FromSeconds(180), ReceiverParameters.Duration(progress));
    }

    [Fact]
    public void Progress_elapsed_handles_the_rtp_wrap_and_current_before_start()
    {
        var wrapped = new ProgressInfo(uint.MaxValue - 44099, 44100, 88200);
        Assert.Equal(TimeSpan.FromSeconds(2), ReceiverParameters.Elapsed(wrapped));
        Assert.Equal(TimeSpan.FromSeconds(3), ReceiverParameters.Duration(wrapped));

        // A current before start (a stale or reordered value) is 0, not the 27 hours of an unsigned wrap.
        var early = new ProgressInfo(100_000, 90_000, 200_000);
        Assert.Equal(TimeSpan.Zero, ReceiverParameters.Elapsed(early));
    }

    [Theory]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData(" image/jpeg; charset=binary", "image/jpeg")]
    [InlineData("image/gif", null)]
    [InlineData("text/parameters", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Artwork_content_type_is_jpeg_or_png(string? contentType, string? expected)
    {
        Assert.Equal(expected, ReceiverParameters.ArtworkContentType(contentType));
        Assert.Equal(expected is not null, ReceiverParameters.IsArtworkContentType(contentType));
    }

    [Fact]
    public void Artwork_size_limit_is_one_mebibyte()
    {
        Assert.Equal(1024 * 1024, ReceiverParameters.MaxArtworkBytes);
        Assert.True(ReceiverParameters.IsArtworkWithinLimit(1024 * 1024));
        Assert.False(ReceiverParameters.IsArtworkWithinLimit((1024 * 1024) + 1));
        Assert.False(ReceiverParameters.IsArtworkWithinLimit(0));
    }

    private static byte[] Item(string tag, params byte[] value)
    {
        var output = new byte[8 + value.Length];
        Encoding.ASCII.GetBytes(tag, output.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4, 4), (uint)value.Length);
        value.CopyTo(output, 8);
        return output;
    }

    private static byte[] Mlit(params byte[][] items) => Item("mlit", [.. items.SelectMany(i => i)]);
}
