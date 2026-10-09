using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Decoder;
using Xunit;

namespace Librespot.Tests.Playback;

/// <summary>Tests for <see cref="StdoutSink"/> (port of playback/src/audio_backend/pipe.rs; upstream has no unit tests).</summary>
public sealed class PipeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("librespot-pipe-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Raw_packets_are_written_verbatim_to_the_file()
    {
        var path = Path.Combine(_dir, "raw.pcm");
        using var sink = new StdoutSink(path, AudioFormat.S16);

        sink.Start();
        sink.Write(new AudioPacket.Raw([1, 2, 3, 4]), new Converter(null));
        sink.Write(new AudioPacket.Raw([5, 6]), new Converter(null));
        sink.Stop();

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void S16_samples_are_written_as_little_endian_i16()
    {
        var path = Path.Combine(_dir, "s16.pcm");
        using var sink = new StdoutSink(path, AudioFormat.S16);

        sink.Start();
        // Upstream f64_to_s16 without dither: round(sample * 2^15).
        sink.Write(new AudioPacket.Samples([0.0, 0.5, -0.5, -1.0]), new Converter(null));
        sink.Stop();

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x40, 0x00, 0xC0, 0x00, 0x80 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void S24_3_samples_are_written_as_packed_little_endian_i24()
    {
        var path = Path.Combine(_dir, "s24_3.pcm");
        using var sink = new StdoutSink(path, AudioFormat.S24_3);

        sink.Start();
        // round(sample * 2^23), clamped to [-2^23, 2^23 - 1], 3 bytes each.
        sink.Write(new AudioPacket.Samples([0.5, -1.0]), new Converter(null));
        sink.Stop();

        Assert.Equal(new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0x80 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void F64_samples_are_written_without_conversion()
    {
        var path = Path.Combine(_dir, "f64.pcm");
        using var sink = new StdoutSink(path, AudioFormat.F64);

        sink.Start();
        sink.Write(new AudioPacket.Samples([1.0, -0.25]), new Converter(null));
        sink.Stop();

        var expected = new byte[16];
        BitConverter.TryWriteBytes(expected.AsSpan(0, 8), 1.0);
        BitConverter.TryWriteBytes(expected.AsSpan(8, 8), -0.25);
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void Start_truncates_and_restarting_reopens_the_file()
    {
        var path = Path.Combine(_dir, "restart.pcm");
        File.WriteAllBytes(path, [9, 9, 9, 9, 9]);
        using var sink = new StdoutSink(path, AudioFormat.S16);

        sink.Start();
        sink.Start(); // already open: no-op
        sink.WriteBytes([1]);
        sink.Stop();
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));

        sink.Start();
        sink.WriteBytes([2, 3]);
        sink.Stop();
        Assert.Equal(new byte[] { 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Writing_or_stopping_without_an_output_is_not_connected()
    {
        using var sink = new StdoutSink(Path.Combine(_dir, "never.pcm"), AudioFormat.S16);

        var write = Assert.Throws<SinkException>(() => sink.WriteBytes([1]));
        Assert.Equal(SinkException.Reason.NotConnected, write.Cause);
        Assert.Equal("Audio Sink Error Not Connected: <StdoutSink> The Output Stream is None", write.Detail);

        var stop = Assert.Throws<SinkException>(sink.Stop);
        Assert.Equal(SinkException.Reason.NotConnected, stop.Cause);
    }

    [Fact]
    public void Unopenable_file_is_connection_refused()
    {
        var path = Path.Combine(_dir, "missing-dir", "out.pcm");
        using var sink = new StdoutSink(path, AudioFormat.S16);

        var e = Assert.Throws<SinkException>(sink.Start);
        Assert.Equal(SinkException.Reason.ConnectionRefused, e.Cause);
        Assert.StartsWith($"Audio Sink Error Connection Refused: <StdoutSink> File Path {path} Can Not be Opened and/or Created, ", e.Detail);
    }

    [Fact]
    public void Registry_finds_pipe_and_subprocess_and_defaults_to_naudio()
    {
        Assert.Equal(new[] { "naudio", "pipe", "subprocess" }, AudioBackends.Backends.Select(b => b.Name));
        Assert.Same(AudioBackends.Backends[0].Builder, AudioBackends.Find(null));
        Assert.NotNull(AudioBackends.Find("pipe"));
        Assert.NotNull(AudioBackends.Find("subprocess"));
        Assert.Null(AudioBackends.Find("alsa"));
        Assert.Same(AudioBackends.Find("naudio"), AudioBackends.Find("rodio")); // upstream name, alias of naudio

        using var sink = AudioBackends.Find("pipe")!(Path.Combine(_dir, "registry.pcm"), AudioFormat.S16);
        Assert.IsType<StdoutSink>(sink);
    }
}
