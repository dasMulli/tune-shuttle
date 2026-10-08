// Port of playback/src/audio_backend/mod.rs.
//
// Backends in this port: "naudio" (WASAPI via NAudio; replaces rodio/cpal and is the default), "pipe" (stdout or
// file; pipe.rs) and "subprocess" (subprocess.rs). alsa/pulseaudio/jack/gstreamer/portaudio/sdl are Linux/native
// backends and are intentionally not ported (see docs/PORT-PLAN.md package `playback-backends`).
using Librespot.Core;
using Librespot.Playback.Decoder;

namespace Librespot.Playback.AudioBackend;

/// <summary>Upstream <c>SinkError</c>.</summary>
public sealed class SinkException : LibrespotException
{
    public enum Reason
    {
        NotConnected,
        ConnectionRefused,
        OnWrite,
        InvalidParams,
        StateChange,
    }

    public Reason Cause { get; }

    public SinkException(Reason cause, string detail, Exception? inner = null)
        : base(ErrorKind.Unavailable, Describe(cause, detail), inner)
    {
        Cause = cause;
    }

    private static string Describe(Reason cause, string detail) => cause switch
    {
        Reason.NotConnected => $"Audio Sink Error Not Connected: {detail}",
        Reason.ConnectionRefused => $"Audio Sink Error Connection Refused: {detail}",
        Reason.OnWrite => $"Audio Sink Error On Write: {detail}",
        Reason.InvalidParams => $"Audio Sink Error Invalid Parameters: {detail}",
        Reason.StateChange => $"Audio Sink Error Changing State: {detail}",
        _ => detail,
    };
}

/// <summary>Upstream <c>trait Sink</c>. Start/Stop default to no-ops.</summary>
public interface ISink : IDisposable
{
    void Start()
    {
    }

    void Stop()
    {
    }

    /// <summary>Writes one packet, converting f64 samples to the sink's format with <paramref name="converter"/>.</summary>
    void Write(AudioPacket packet, Converter converter);
}

/// <summary>
/// Upstream <c>trait SinkAsBytes</c> + the <c>sink_as_bytes!</c> macro: a sink that accepts raw PCM bytes.
/// <see cref="SinkAsBytesExtensions.WriteAsBytes"/> performs the format conversion the macro expands to.
/// </summary>
public interface ISinkAsBytes : ISink
{
    AudioFormat Format { get; }

    void WriteBytes(ReadOnlySpan<byte> data);
}

public static class SinkAsBytesExtensions
{
    /// <summary>The body of upstream <c>sink_as_bytes!()</c>: convert samples to <see cref="ISinkAsBytes.Format"/> and write them.</summary>
    public static void WriteAsBytes(this ISinkAsBytes sink, AudioPacket packet, Converter converter)
    {
        switch (packet)
        {
            case AudioPacket.Samples s:
                switch (sink.Format)
                {
                    case AudioFormat.F64:
                        sink.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes<double>(s.Data));
                        break;
                    case AudioFormat.F32:
                        sink.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(converter.F64ToF32(s.Data)));
                        break;
                    case AudioFormat.S32:
                        sink.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes<int>(converter.F64ToS32(s.Data)));
                        break;
                    case AudioFormat.S24:
                        sink.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes<int>(converter.F64ToS24(s.Data)));
                        break;
                    case AudioFormat.S24_3:
                        sink.WriteBytes(converter.F64ToS24_3(s.Data));
                        break;
                    case AudioFormat.S16:
                        sink.WriteBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes<short>(converter.F64ToS16(s.Data)));
                        break;
                }

                break;
            case AudioPacket.Raw r:
                sink.WriteBytes(r.Data);
                break;
        }
    }
}

/// <summary>Upstream <c>SinkBuilder = fn(Option&lt;String&gt;, AudioFormat) -> Box&lt;dyn Sink&gt;</c>.</summary>
public delegate ISink SinkBuilder(string? device, AudioFormat format);

/// <summary>Registry of audio backends (upstream <c>BACKENDS</c> + <c>find</c>).</summary>
public static class AudioBackends
{
    /// <summary>Name/builder pairs; the first one is the default.</summary>
    public static IReadOnlyList<(string Name, SinkBuilder Builder)> Backends { get; } =
    [
        ("naudio", (device, format) => throw new NotImplementedException("TODO(port: playback-backends) NAudioSink")),
        ("pipe", (device, format) => throw new NotImplementedException("TODO(port: playback-backends) StdoutSink")),
        ("subprocess", (device, format) => throw new NotImplementedException("TODO(port: playback-backends) SubprocessSink")),
    ];

    /// <summary>Finds a backend by name, or the default when <paramref name="name"/> is null; null when unknown.</summary>
    public static SinkBuilder? Find(string? name)
    {
        if (name is null)
        {
            return Backends.Count > 0 ? Backends[0].Builder : null;
        }

        foreach (var (n, b) in Backends)
        {
            if (n == name)
            {
                return b;
            }
        }

        return null;
    }
}
