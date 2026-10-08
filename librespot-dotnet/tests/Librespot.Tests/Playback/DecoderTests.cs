// Tests for playback/src/decoder/{mod,symphonia_decoder,passthrough_decoder}.rs and symphonia_util.rs
// (upstream has no unit tests for them). Assets: tests/Librespot.Tests/Assets/tone-2s.{ogg,mp3}, 2 s of a
// 440 Hz (left) / 660 Hz (right) sine at 44.1 kHz stereo, generated with ffmpeg (libvorbis q0, libmp3lame 64k,
// ID3v2.3 tags on the MP3, Vorbis comments on the Ogg).
using System.Text;
using Librespot.Metadata.Audio;
using Librespot.Playback;
using Librespot.Playback.Decoder;
using NVorbis;
using Xunit;

namespace Librespot.Tests.Playback;

public class DecoderTests
{
    /// <summary>Test assets are copied next to the test assembly (Librespot.Tests.csproj).</summary>
    internal static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static Stream OpenAsset(string name) => new MemoryStream(File.ReadAllBytes(AssetPath(name)));

    [Fact]
    public void AudioPacket_accessors_match_upstream_errors()
    {
        AudioPacket samples = new AudioPacket.Samples([0.5, -0.5]);
        AudioPacket raw = new AudioPacket.Raw([1, 2, 3]);

        Assert.Equal(new[] { 0.5, -0.5 }, samples.SamplesOrThrow());
        Assert.Equal(new byte[] { 1, 2, 3 }, raw.RawOrThrow());
        Assert.Equal("Decoder Raw Error: Can't return Raw on Samples", Assert.Throws<AudioPacketException>(() => raw.SamplesOrThrow()).Detail);
        Assert.Equal("Decoder Samples Error: Can't return Samples on Raw", Assert.Throws<AudioPacketException>(() => samples.RawOrThrow()).Detail);
        Assert.False(samples.IsEmpty);
        Assert.True(new AudioPacket.Raw([]).IsEmpty);

        uint position = new AudioPacketPosition(1234, false);
        Assert.Equal(1234u, position);
    }

    [Fact]
    public void Vorbis_decodes_whole_file_as_stereo_f64()
    {
        using var decoder = AudioDecoders.Create(OpenAsset("tone-2s.ogg"), "audio/ogg", passthrough: false);
        Assert.IsType<VorbisDecoder>(decoder);

        var total = 0;
        uint lastPosition = 0;
        var first = true;
        var peak = 0.0;
        while (decoder.NextPacket() is var (position, packet))
        {
            var samples = packet.SamplesOrThrow();
            Assert.Equal(0, samples.Length % 2);
            Assert.True(samples.Length <= AudioConstants.SamplesPerSecond / 10);
            Assert.False(position.Skipped);
            if (first)
            {
                Assert.Equal(0u, position.PositionMs);
                first = false;
            }
            else
            {
                Assert.True(position.PositionMs > lastPosition);
            }

            Assert.Equal((uint)(total / 2 * 1000L / 44100), position.PositionMs);
            lastPosition = position.PositionMs;
            total += samples.Length;
            peak = Math.Max(peak, samples.Max(Math.Abs));
        }

        // 2 s of stereo audio at 44.1 kHz.
        Assert.Equal(2 * 44100 * 2, total);
        Assert.InRange(peak, 0.05, 1.5);
    }

    [Fact]
    public void Vorbis_seek_returns_actual_position_and_saturates()
    {
        using var decoder = AudioDecoders.Create(OpenAsset("tone-2s.ogg"), new DecoderHint(MimeType: "audio/ogg"));

        Assert.Equal(1000u, decoder.Seek(1000));
        var (position, packet) = decoder.NextPacket()!.Value;
        Assert.Equal(1000u, position.PositionMs);
        Assert.NotEmpty(packet.SamplesOrThrow());

        Assert.Equal(0u, decoder.Seek(0));
        Assert.Equal(0u, decoder.NextPacket()!.Value.Position.PositionMs);

        // Into the (end-trimmed) last page, which NVorbis cannot seek to directly.
        Assert.Equal(1950u, decoder.Seek(1950));
        Assert.Equal(1950u, decoder.NextPacket()!.Value.Position.PositionMs);

        // Past the end: saturated to the duration, then end of stream.
        Assert.Equal(2000u, decoder.Seek(60_000));
        Assert.Null(decoder.NextPacket());
    }

    [Fact]
    public void Vorbis_tags_give_local_file_metadata_and_default_normalisation_data()
    {
        using var decoder = AudioDecoders.Create(OpenAsset("tone-2s.ogg"), new DecoderHint(MimeType: "audio/ogg"));
        var metadata = decoder.GetLocalFileMetadata();
        Assert.NotNull(metadata);
        Assert.Equal("Test Tone", metadata.Name);
        Assert.Equal("Librespot Tests", metadata.Artists);
        Assert.Equal("Assets", metadata.Album);
        Assert.Equal(3u, metadata.Number);
        Assert.Equal(1u, metadata.DiscNumber);
        Assert.Equal(2000u, metadata.DurationMs);

        // Tags exist but carry no float ReplayGain values (symphonia stores them as strings too): defaults.
        Assert.Equal(NormalisationData.Default, decoder.GetNormalisationData());
    }

    [Fact]
    public void Mp3_decodes_and_reads_id3v2_tags()
    {
        using var decoder = AudioDecoders.Create(OpenAsset("tone-2s.mp3"), AudioFileFormat.Mp3320, passthrough: false);
        Assert.IsType<Mp3Decoder>(decoder);

        var metadata = decoder.GetLocalFileMetadata();
        Assert.NotNull(metadata);
        Assert.Equal("Test Tone & Co", metadata.Name);
        Assert.Equal("Librespot Tests", metadata.Artists);
        Assert.Equal("Various", metadata.AlbumArtists);
        Assert.Equal("Assets", metadata.Album);
        Assert.Equal(3u, metadata.Number);
        Assert.Equal(1u, metadata.DiscNumber);
        Assert.Equal(NormalisationData.Default, decoder.GetNormalisationData());

        var total = 0;
        while (decoder.NextPacket() is var (_, packet))
        {
            total += packet.SamplesOrThrow().Length;
        }

        // ~2 s of stereo audio plus encoder delay/padding (no gapless trimming in NLayer).
        Assert.InRange(total, 2 * 44100 * 2, 2 * 44100 * 2 + 4 * 1152 * 2);
    }

    [Fact]
    public void Mp3_seek_returns_actual_position()
    {
        var input = OpenAsset("tone-2s.mp3");
        using var decoder = new Mp3Decoder(input);

        // NLayer lands on the start of the MPEG frame (1152 samples) containing the target.
        var actual = decoder.Seek(500);
        Assert.InRange(actual, 500u - 27u, 500u);

        // A seek within the track must not make NLayer scan to the end of the stream.
        Assert.True(input.Position < input.Length, $"read {input.Position} of {input.Length} bytes");
        Assert.Equal(actual, decoder.NextPacket()!.Value.Position.PositionMs);
    }

    [Fact]
    public void Mp3_seek_past_the_end_saturates_to_the_end()
    {
        using var decoder = new Mp3Decoder(OpenAsset("tone-2s.mp3"));
        var durationMs = decoder.GetLocalFileMetadata()!.DurationMs!.Value;

        var actual = decoder.Seek(60_000);
        Assert.InRange(actual, durationMs - 27u, durationMs);

        // Seeking back after the saturated seek still works.
        Assert.Equal(0u, decoder.Seek(0));
        Assert.Equal(0u, decoder.NextPacket()!.Value.Position.PositionMs);
    }

    [Fact]
    public void Unsupported_content_fails_the_probe()
    {
        var e = Assert.Throws<DecoderException>(() =>
            AudioDecoders.Create(new MemoryStream(Encoding.ASCII.GetBytes("fLaC and some more bytes")), new DecoderHint()));
        Assert.Equal(DecoderException.Reason.AudioDecoder, e.Cause);
        Assert.Contains("no suitable format reader found", e.Message);
    }

    [Fact]
    public void Passthrough_rejects_non_vorbis_formats()
    {
        var e = Assert.Throws<DecoderException>(() => new PassthroughDecoder(new MemoryStream(), AudioFileFormat.Mp3320));
        Assert.Equal(DecoderException.Reason.PassthroughDecoder, e.Cause);
        Assert.Contains("Passthrough decoder is not implemented for format", e.Message);
        Assert.Throws<DecoderException>(() => AudioDecoders.Create(new MemoryStream(), "audio/mpeg", passthrough: true));
    }

    [Fact]
    public void Passthrough_remuxes_to_a_valid_ogg_vorbis_stream()
    {
        using var decoder = new PassthroughDecoder(OpenAsset("tone-2s.ogg"), AudioFileFormat.OggVorbis160);
        Assert.Equal(0u, decoder.Seek(0));

        var output = new MemoryStream();
        uint lastPosition = 0;
        while (decoder.NextPacket() is var (position, packet))
        {
            Assert.False(position.Skipped);
            Assert.True(position.PositionMs >= lastPosition);
            lastPosition = position.PositionMs;
            output.Write(packet.RawOrThrow());
        }

        Assert.InRange(lastPosition, 1900u, 2000u);

        // The re-muxed stream starts with the three Vorbis header packets and decodes to the full length.
        output.Position = 0;
        using var reader = new VorbisReader(output, closeOnDispose: false);
        Assert.Equal(44100, reader.SampleRate);
        Assert.Equal(2, reader.Channels);
        var buffer = new float[8192];
        long total = 0;
        int n;
        while ((n = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
        }

        Assert.InRange(total, 2 * 44100 * 2 - 2048 * 2, 2 * 44100 * 2);
    }

    [Fact]
    public void Passthrough_seek_starts_a_new_stream_with_rebased_granules()
    {
        using var decoder = new PassthroughDecoder(OpenAsset("tone-2s.ogg"), AudioFileFormat.OggVorbis320);
        var output = new MemoryStream();
        output.Write(decoder.NextPacket()!.Value.Packet.RawOrThrow()); // starts the first logical stream

        // The ogg crate's seek lands near the target page and reports the granule position of the page it landed on
        // (pages are about 100 ms long in this asset).
        var seeked = decoder.Seek(1000);
        Assert.InRange(seeked, 900u, 1100u);

        while (decoder.NextPacket() is var (_, packet))
        {
            output.Write(packet.RawOrThrow());
        }

        // The output contains the first stream (ended with an EOS page by the seek), then a new stream with fresh
        // headers and a new serial.
        output.Position = 0;
        var reader = new OggPacketReader(output);
        var serials = new HashSet<uint>();
        while (reader.ReadPacket() is { } pck)
        {
            serials.Add(pck.StreamSerial);
        }

        Assert.Equal(2, serials.Count);
    }

    [Fact]
    public void Probe_reads_id3v2_and_detects_formats()
    {
        using var mp3 = OpenAsset("tone-2s.mp3");
        var probe = DecoderUtil.Probe(mp3, new DecoderHint());
        Assert.Equal(ProbedFormat.Mpa, probe.Format);
        Assert.NotNull(probe.Metadata);
        Assert.Equal(0, mp3.Position);
        Assert.Contains(probe.Metadata, t => t.StdKey == StandardTagKey.TrackTitle && t.Value.ToString() == "Test Tone & Co");

        using var ogg = OpenAsset("tone-2s.ogg");
        probe = DecoderUtil.Probe(ogg, new DecoderHint());
        Assert.Equal(ProbedFormat.Ogg, probe.Format);
        Assert.Null(probe.Metadata);

        // GetLatestMetadata prefers the container's metadata and falls back to the probe's.
        IReadOnlyList<Tag> container = [];
        Assert.Same(container, DecoderUtil.GetLatestMetadata(container, new ProbeResult(ProbedFormat.Mpa, [])));
        IReadOnlyList<Tag> probed = [];
        Assert.Same(probed, DecoderUtil.GetLatestMetadata(null, new ProbeResult(ProbedFormat.Mpa, probed)));
        Assert.Null(DecoderUtil.GetLatestMetadata(null, new ProbeResult(ProbedFormat.Mpa, null)));
    }

    [Theory]
    [InlineData("3", 3u, null)]
    [InlineData("+7", 7u, null)]
    [InlineData("3/12", null, "invalid digit found in string")]
    [InlineData("", null, "cannot parse integer from empty string")]
    [InlineData(" 1", null, "invalid digit found in string")]
    [InlineData("4294967296", null, "number too large to fit in target type")]
    public void ParseU32_matches_rust_from_str(string input, uint? expected, string? expectedError)
    {
        Assert.Equal(expected, DecoderUtil.ParseU32(input, out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void Local_file_metadata_parses_number_tags_like_upstream()
    {
        var metadata = AudioDecoders.LocalFileMetadataFromTags(
        [
            new Tag(StandardTagKey.TrackNumber, "TRCK", new TagValue.String("5")),
            new Tag(StandardTagKey.DiscNumber, "TPOS", new TagValue.String("1/2")),
            new Tag(StandardTagKey.Language, "TLAN", new TagValue.String("eng")),
            new Tag(null, "TXXX:foo", new TagValue.String("bar")),
        ], null)!;
        Assert.Equal(5u, metadata.Number);
        Assert.Null(metadata.DiscNumber);
        Assert.Equal("eng", metadata.Language);

        metadata = AudioDecoders.LocalFileMetadataFromTags(
            [new Tag(StandardTagKey.TrackNumber, "x", new TagValue.UnsignedInt(9))], null)!;
        Assert.Equal(9u, metadata.Number);

        Assert.Null(AudioDecoders.LocalFileMetadataFromTags(null, null));
        Assert.Null(AudioDecoders.NormalisationDataFromTags([]));

        var normalisation = AudioDecoders.NormalisationDataFromTags(
        [
            new Tag(StandardTagKey.ReplayGainTrackGain, "x", new TagValue.Float(-3.5)),
            new Tag(StandardTagKey.ReplayGainAlbumPeak, "x", new TagValue.Float(0.9)),
            new Tag(StandardTagKey.ReplayGainAlbumGain, "x", new TagValue.String("-1 dB")),
        ]);
        Assert.Equal(NormalisationData.Default with { TrackGainDb = -3.5, AlbumPeak = 0.9 }, normalisation);
    }
}
