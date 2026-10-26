using System.Text.Json;
using Airplay2.Receiver.Codecs;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Receiver.Tests.Codecs;

/// <summary>
/// <see cref="FfmpegAacDecoder"/> and its factory (SPEC-receiver.md 7.2, 11.1). The decode tests need the FFmpeg 7.1 shared
/// libraries: <c>AIRPLAY_FFMPEG_PATH</c>, else <c>artifacts/ffmpeg</c> of the repository (<c>tools/get-ffmpeg.ps1</c> puts
/// them there). Without them they pass with the output "skipped: libavcodec not found" (DEVELOPMENT.md "Live tests" gate
/// pattern: xunit 2.9 has no dynamic skip).
/// </summary>
public class FfmpegAacDecoderTests(ITestOutputHelper output)
{
    private const int BlockFrames = 1024;

    [Fact]
    public void Factory_reports_a_reason_when_the_library_path_points_nowhere()
    {
        var nowhere = Path.Combine(Path.GetTempPath(), "tune-shuttle-no-ffmpeg-" + Guid.NewGuid().ToString("N"));
        var factory = new FfmpegAacDecoderFactory(nowhere);

        Assert.False(factory.TryCreate(out var decoder, out var reason));
        Assert.Null(decoder);
        Assert.Contains("libavcodec not found", reason, StringComparison.Ordinal);
        Assert.Contains(nowhere, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_reports_the_missing_library_file_in_an_empty_directory()
    {
        var empty = Directory.CreateTempSubdirectory("tune-shuttle-empty-ffmpeg-").FullName;
        try
        {
            var factory = new FfmpegAacDecoderFactory(empty);

            Assert.False(factory.TryCreate(out _, out var reason));
            Assert.Contains("libavcodec not found", reason, StringComparison.Ordinal);
            Assert.Contains(FfmpegLibraries.FileName("avutil"), reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void Library_names_follow_the_pinned_ffmpeg_major()
    {
        Assert.Equal(61, FfmpegLibraries.AvcodecMajor); // FFmpeg 7.1 (FFmpeg.AutoGen 7.1.1, Directory.Packages.props)
        var expected = OperatingSystem.IsWindows() ? "avcodec-61.dll" : OperatingSystem.IsMacOS() ? "libavcodec.61.dylib" : "libavcodec.so.61";
        Assert.Equal(expected, FfmpegLibraries.FileName("avcodec"));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 16384)]
    [InlineData(-0.5f, -16384)]
    [InlineData(1f, 32767)]
    [InlineData(-1f, -32768)]
    [InlineData(1.7f, 32767)]
    [InlineData(-3f, -32768)]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 32767)]
    public void Float_samples_convert_to_s16_with_clipping(float sample, short expected)
    {
        Assert.Equal(expected, FfmpegAacDecoder.ClipToS16(sample));
    }

    [Fact]
    public void Decoding_the_vector_yields_the_440_hz_tone_at_44100_stereo()
    {
        if (!TryCreateDecoder(out var decoder))
        {
            return;
        }

        using (decoder)
        {
            var vector = AacVector.Load();
            var pcm = DecodeAll(decoder, vector.Blocks, out var failed);

            Assert.Equal(0, failed);
            var frames = pcm.Count / 2;
            output.WriteLine($"{vector.Blocks.Count} blocks -> {frames} frames (expected {vector.ExpectedFrames}), RMS {Rms(pcm):F1} (expected {vector.ExpectedRms})");
            Assert.InRange(frames, vector.ExpectedFrames - BlockFrames, vector.ExpectedFrames + BlockFrames);
            Assert.InRange(Rms(pcm), vector.ExpectedRms * 0.9, vector.ExpectedRms * 1.1);
            AssertStereoTone(pcm, vector.FrequencyHz);
        }
    }

    [Fact]
    public void Reset_mid_stream_flushes_and_decoding_continues()
    {
        if (!TryCreateDecoder(out var decoder))
        {
            return;
        }

        using (decoder)
        {
            var vector = AacVector.Load();
            var half = vector.Blocks.Count / 2;
            var first = DecodeAll(decoder, vector.Blocks.Take(half), out var failedFirst);
            decoder.Reset();
            var second = DecodeAll(decoder, vector.Blocks.Skip(half), out var failedSecond);

            Assert.Equal(0, failedFirst + failedSecond);
            Assert.InRange((first.Count + second.Count) / 2, vector.ExpectedFrames - BlockFrames, vector.ExpectedFrames + BlockFrames);

            // A steady stretch after the flush (past the first re-primed block) still carries the tone at its level.
            var steady = second.GetRange(4 * BlockFrames * 2, 20 * BlockFrames * 2);
            var toneRms = vector.ExpectedRms * Math.Sqrt(vector.ExpectedFrames / (vector.ToneSeconds * 44100));
            Assert.InRange(Rms(steady), toneRms * 0.9, toneRms * 1.1);
            AssertStereoTone(steady, vector.FrequencyHz);
        }
    }

    [Fact]
    public void A_rejected_block_or_a_short_output_buffer_fails_and_the_decoder_keeps_working()
    {
        if (!TryCreateDecoder(out var decoder))
        {
            return;
        }

        using (decoder)
        {
            var vector = AacVector.Load();
            var pcm = new short[2 * 2048];

            Assert.False(decoder.TryDecode(ReadOnlySpan<byte>.Empty, pcm, out var frames));
            Assert.Equal(0, frames);
            Assert.False(decoder.TryDecode(AdtsHeader.Frame(vector.Blocks[1]), new short[100], out frames));
            Assert.Equal(0, frames);

            decoder.Reset();
            for (var i = 1; i < 10; i++)
            {
                Assert.True(decoder.TryDecode(AdtsHeader.Frame(vector.Blocks[i]), pcm, out frames));
                Assert.Equal(BlockFrames, frames);
            }
        }
    }

    [Fact]
    public void Dispose_is_idempotent_and_use_after_dispose_throws()
    {
        if (!TryCreateDecoder(out var decoder))
        {
            return;
        }

        decoder.Dispose();
        decoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => decoder.TryDecode(new byte[10], new short[2048], out _));
        Assert.Throws<ObjectDisposedException>(decoder.Reset);
    }

    private bool TryCreateDecoder(out IAacDecoder decoder)
    {
        var factory = new FfmpegAacDecoderFactory(LibraryDirectory());
        if (factory.TryCreate(out var created, out var reason))
        {
            decoder = created;
            return true;
        }

        output.WriteLine($"skipped: libavcodec not found ({reason}); run tools/get-ffmpeg.ps1 or set AIRPLAY_FFMPEG_PATH");
        decoder = null!;
        return false;
    }

    private static string? LibraryDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("AIRPLAY_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")))
            {
                var candidate = Path.Combine(dir.FullName, "artifacts", "ffmpeg");
                return Directory.Exists(candidate) ? candidate : null;
            }
        }

        return null;
    }

    private static List<short> DecodeAll(IAacDecoder decoder, IEnumerable<byte[]> blocks, out int failed)
    {
        var all = new List<short>();
        var pcm = new short[2 * 2048];
        failed = 0;
        foreach (var block in blocks)
        {
            if (decoder.TryDecode(AdtsHeader.Frame(block), pcm, out var frames))
            {
                all.AddRange(pcm.AsSpan(0, frames * 2));
            }
            else
            {
                failed++;
            }
        }

        return all;
    }

    private static double Rms(IEnumerable<short> samples)
    {
        double sum = 0;
        var count = 0;
        foreach (var s in samples)
        {
            sum += (double)s * s;
            count++;
        }

        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    /// <summary>Both channels carry the same tone, and its frequency has the largest Goertzel power between 100 Hz and 4 kHz.</summary>
    private static void AssertStereoTone(List<short> interleaved, int frequency)
    {
        var frames = interleaved.Count / 2;
        var left = new double[frames];
        var right = new double[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = interleaved[2 * i];
            right[i] = interleaved[(2 * i) + 1];
        }

        var leftRms = Rms(interleaved.Where((_, i) => i % 2 == 0));
        var rightRms = Rms(interleaved.Where((_, i) => i % 2 == 1));
        Assert.InRange(leftRms / Math.Max(1, rightRms), 0.95, 1.05);
        foreach (var channel in new[] { left, right })
        {
            var target = Goertzel(channel, frequency);
            var best = 0.0;
            var bestFrequency = 0;
            for (var f = 100; f <= 4000; f += 20)
            {
                var power = Goertzel(channel, f);
                if (power > best)
                {
                    best = power;
                    bestFrequency = f;
                }
            }

            Assert.Equal(frequency, bestFrequency);
            Assert.True(target > 100 * Goertzel(channel, 1000), $"the {frequency} Hz tone does not dominate 1 kHz");
        }
    }

    private static double Goertzel(double[] samples, double frequency)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / 44100);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            var s0 = x + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        return (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
    }

    private sealed record AacVector(List<byte[]> Blocks, int ExpectedFrames, double ExpectedRms, int FrequencyHz, double ToneSeconds)
    {
        public static AacVector Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "aac-44100-stereo-1024.json");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            Assert.Equal(44100, root.GetProperty("sample_rate").GetInt32());
            Assert.Equal(2, root.GetProperty("channels").GetInt32());
            Assert.Equal(BlockFrames, root.GetProperty("frames_per_block").GetInt32());
            var blocks = root.GetProperty("blocks_base64").EnumerateArray().Select(e => Convert.FromBase64String(e.GetString()!)).ToList();
            return new AacVector(
                blocks,
                root.GetProperty("expected_frames").GetInt32(),
                root.GetProperty("expected_rms_s16").GetDouble(),
                root.GetProperty("tone_frequency_hz").GetInt32(),
                root.GetProperty("tone_duration_s").GetDouble());
        }
    }
}
