using System.Text.Json;
using Airplay2.Sender.Alac;
using Xunit;

namespace Airplay2.Sender.Tests.Alac;

/// <summary>Bit-exact and round-trip tests for the ALAC port (SPEC-audio.md 5.4).</summary>
public class AlacTests
{
    private const int Frames = 352;

    private sealed record Vector(string Name, byte[] Pcm, byte[] Alac);

    private static (string Cookie, List<Vector> Packets) LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "alac-44100-s16-2ch-352.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var cookie = doc.RootElement.GetProperty("magic_cookie_hex").GetString()!;
        var packets = doc.RootElement.GetProperty("packets").EnumerateArray()
            .Select(p => new Vector(p.GetProperty("name").GetString()!, Convert.FromHexString(p.GetProperty("pcm_le_hex").GetString()!), Convert.FromHexString(p.GetProperty("alac_hex").GetString()!)))
            .ToList();
        return (cookie, packets);
    }

    private static short[] ToSamples(byte[] pcmLe)
    {
        var s = new short[pcmLe.Length / 2];
        for (var i = 0; i < s.Length; i++)
        {
            s[i] = (short)(pcmLe[2 * i] | (pcmLe[2 * i + 1] << 8));
        }

        return s;
    }

    [Fact]
    public void Magic_cookie_matches_reference()
    {
        var (cookie, _) = LoadVectors();
        var enc = new AlacEncoder(Frames, 44100);
        Assert.Equal(cookie, Convert.ToHexString(enc.MagicCookie.ToBytes()).ToLowerInvariant());
        Assert.Equal(cookie, Convert.ToHexString(AlacMagicCookie.AirPlay.ToBytes()).ToLowerInvariant());
        var parsed = AlacMagicCookie.Parse(Convert.FromHexString(cookie));
        Assert.Equal(352u, parsed.FrameLength);
        Assert.Equal(16, parsed.BitDepth);
        Assert.Equal(40, parsed.Pb);
        Assert.Equal(10, parsed.Mb);
        Assert.Equal(14, parsed.Kb);
        Assert.Equal(2, parsed.NumChannels);
        Assert.Equal(255, parsed.MaxRun);
        Assert.Equal(44100u, parsed.SampleRate);
    }

    [Fact]
    public void Encoder_is_bit_exact_with_the_reference_vectors_in_order()
    {
        var (_, packets) = LoadVectors();
        Assert.Equal(6, packets.Count);
        var enc = new AlacEncoder(Frames, 44100);
        foreach (var v in packets)
        {
            var output = enc.Encode(ToSamples(v.Pcm));
            Assert.True(v.Alac.AsSpan().SequenceEqual(output), $"{v.Name}: expected {v.Alac.Length} bytes {Convert.ToHexString(v.Alac)[..Math.Min(64, v.Alac.Length * 2)]}..., got {output.Length} bytes {Convert.ToHexString(output)[..Math.Min(64, output.Length * 2)]}...");
        }
    }

    [Fact]
    public void Vector_sizes_cover_silence_music_and_escape()
    {
        var (_, packets) = LoadVectors();
        Assert.Equal(32, packets.Single(p => p.Name == "silence").Alac.Length);
        Assert.Equal(1412, packets.Single(p => p.Name.StartsWith("white_noise", StringComparison.Ordinal)).Alac.Length);
    }

    [Fact]
    public void Decoder_decodes_the_reference_vectors()
    {
        var (cookie, packets) = LoadVectors();
        var dec = new AlacDecoder(Convert.FromHexString(cookie));
        foreach (var v in packets)
        {
            var output = new short[Frames * 2];
            var n = dec.Decode(v.Alac, output, 2);
            Assert.Equal(Frames, n);
            Assert.Equal(ToSamples(v.Pcm), output);
        }
    }

    public static IEnumerable<object[]> Signals()
    {
        yield return ["sweep", Generate(Frames, i => (Math.Sin(2 * Math.PI * (200 + i * 20) * i / 44100.0) * 20000, Math.Cos(2 * Math.PI * (300 + i * 10) * i / 44100.0) * 15000))];
        yield return ["impulses", Generate(Frames, i => (i % 50 == 0 ? 32767 : 0, i % 73 == 0 ? -32768 : 0))];
        yield return ["square_full_scale", Generate(Frames, i => ((i / 20) % 2 == 0 ? 32767 : -32768, (i / 30) % 2 == 0 ? -32768 : 32767))];
        yield return ["random", GenerateRandom(Frames, 42)];
        yield return ["mono_in_stereo", Generate(Frames, i => (Math.Sin(i / 7.0) * 12000, Math.Sin(i / 7.0) * 12000))];
        yield return ["partial_100", GenerateRandom(100, 7)];
        yield return ["partial_1", GenerateRandom(1, 9)];
        yield return ["partial_351", Generate(351, i => (Math.Sin(i / 3.0) * 1000, Math.Sin(i / 5.0) * 1000))];
    }

    private static short[] Generate(int frames, Func<int, (double L, double R)> f)
    {
        var s = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var (l, r) = f(i);
            s[2 * i] = (short)Math.Clamp((int)Math.Round(l), short.MinValue, short.MaxValue);
            s[2 * i + 1] = (short)Math.Clamp((int)Math.Round(r), short.MinValue, short.MaxValue);
        }

        return s;
    }

    private static short[] GenerateRandom(int frames, int seed)
    {
        var rng = new Random(seed);
        var s = new short[frames * 2];
        for (var i = 0; i < s.Length; i++)
        {
            s[i] = (short)rng.Next(short.MinValue, short.MaxValue + 1);
        }

        return s;
    }

    [Theory]
    [MemberData(nameof(Signals))]
    public void Round_trip_through_the_ported_decoder(string name, short[] pcm)
    {
        var enc = new AlacEncoder(Frames, 44100);
        var dec = new AlacDecoder(enc.MagicCookie);
        var frame = enc.Encode(pcm);
        Assert.True(frame.Length <= 1412 + 5, $"{name}: {frame.Length} bytes");
        var output = new short[Frames * 2];
        var n = dec.Decode(frame, output, 2);
        Assert.Equal(pcm.Length / 2, n);
        Assert.Equal(pcm, output[..pcm.Length]);
    }

    [Fact]
    public void Long_stream_round_trips_with_state_carry_over_and_stays_under_the_escape_size()
    {
        var enc = new AlacEncoder(Frames, 44100);
        var dec = new AlacDecoder(enc.MagicCookie);
        var rng = new Random(1);
        var phase = 0.0;
        for (var packet = 0; packet < 200; packet++)
        {
            var pcm = new short[Frames * 2];
            var amp = packet % 10 == 0 ? 32000 : 8000;
            for (var i = 0; i < Frames; i++)
            {
                phase += 2 * Math.PI * (220 + packet) / 44100.0;
                pcm[2 * i] = (short)(Math.Sin(phase) * amp + rng.Next(-200, 200));
                pcm[2 * i + 1] = (short)(Math.Sin(phase * 1.5) * amp / 2 + rng.Next(-200, 200));
            }

            var frame = enc.Encode(pcm);
            Assert.InRange(frame.Length, 1, 1412);
            var output = new short[Frames * 2];
            Assert.Equal(Frames, dec.Decode(frame, output, 2));
            Assert.Equal(pcm, output);
        }
    }

    [Fact]
    public void Rejects_bad_input_sizes()
    {
        var enc = new AlacEncoder(Frames, 44100);
        Assert.Throws<ArgumentException>(() => enc.Encode(new short[3]));
        Assert.Throws<ArgumentException>(() => enc.Encode(new short[(Frames + 1) * 2]));
        Assert.Throws<ArgumentException>(() => enc.Encode(Array.Empty<short>()));
    }

    [Fact]
    public void Decoder_returns_minus_one_for_garbage()
    {
        var dec = new AlacDecoder(AlacMagicCookie.AirPlay);
        var output = new short[Frames * 2];
        Assert.Equal(-1, dec.Decode(new byte[] { 0x20, 0xFF, 0xFF }, output, 2)); // CPE with non-zero unused header bits
    }
}
