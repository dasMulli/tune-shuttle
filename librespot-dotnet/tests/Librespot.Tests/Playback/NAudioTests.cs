using System.Runtime.Versioning;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Decoder;
using Xunit;

namespace Librespot.Tests.Playback;

/// <summary>Tests for <see cref="NAudioSink"/> (port of playback/src/audio_backend/rodio.rs; upstream has no unit tests).</summary>
public sealed class NAudioTests
{
    [Fact(Skip = "audio device")]
    [SupportedOSPlatform("windows")]
    public void Default_device_plays_100ms_of_silence()
    {
        using var sink = NAudioSink.Open(null, AudioFormat.S16);

        sink.Start();
        var samples = new double[(int)(AudioConstants.SamplesPerSecond / 10)];
        sink.Write(new AudioPacket.Samples(samples), new Converter(null));
        sink.Stop();
    }

    [Fact]
    [SupportedOSPlatform("windows")] // guarded below; the analyzer does not carry the guard into the lambda
    public void Unknown_device_name_is_connection_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var e = Assert.Throws<SinkException>(() => NAudioSink.Open("librespot no such device 4711", AudioFormat.S16));

        // Without any audio endpoint service the device enumeration itself fails (InvalidParams).
        Assert.Contains(e.Cause, new[] { SinkException.Reason.ConnectionRefused, SinkException.Reason.InvalidParams });
        if (e.Cause == SinkException.Reason.ConnectionRefused)
        {
            Assert.Equal(
                "Audio Sink Error Connection Refused: <NAudioSink> device \"librespot no such device 4711\" is Not Available",
                e.Detail);
        }
    }

    [Fact(Skip = "audio device")] // opens the default device and starts a playing stream
    public void Raw_packets_are_rejected_like_rodio_does()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ISink sink;
        try
        {
            sink = NAudioSink.Open(null, AudioFormat.F32);
        }
        catch (SinkException)
        {
            return; // no audio device on this machine
        }

        using (sink)
        {
            var e = Assert.Throws<SinkException>(() => sink.Write(new AudioPacket.Raw([1, 2]), new Converter(null)));
            Assert.Equal(SinkException.Reason.OnWrite, e.Cause);
            Assert.Equal("Audio Sink Error On Write: <NAudioSink> Decoder Raw Error: Can't return Raw on Samples", e.Detail);
        }
    }
}
