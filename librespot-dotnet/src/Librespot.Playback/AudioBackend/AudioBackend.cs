// Port of playback/src/audio_backend/mod.rs.
//
// Backends in this port: "naudio" (WASAPI via NAudio; replaces rodio/cpal from rodio.rs and is the default),
// "pipe" (stdout or file; pipe.rs) and "subprocess" (subprocess.rs). Not ported, because they are Linux/native-only
// (see docs/PORTING-NOTES.md, "NuGet mapping"): alsa.rs ("alsa"), portaudio.rs ("portaudio"),
// pulseaudio.rs ("pulseaudio"), jackaudio.rs ("jackaudio"), gstreamer.rs ("gstreamer"), sdl.rs ("sdl"), the
// "rodiojack" variant of rodio.rs, and mixer/alsamixer.rs. Like upstream builds without those cargo features,
// `AudioBackends.Find` returns null for their names. "rodio" is accepted by `Find` as an alias of "naudio".
using System.Runtime.InteropServices;
using Librespot.Core;
using Librespot.Playback.Decoder;

namespace Librespot.Playback.AudioBackend;

/// <summary>Upstream <c>SinkError</c>.</summary>
public sealed class SinkException : LibrespotException
{
    /// <summary>The upstream <c>SinkError</c> variant.</summary>
    public enum Reason
    {
        /// <summary>The output is not open.</summary>
        NotConnected,

        /// <summary>The output device, file or process could not be opened.</summary>
        ConnectionRefused,

        /// <summary>Writing (or flushing/stopping) the output failed.</summary>
        OnWrite,

        /// <summary>The sink was configured with invalid parameters.</summary>
        InvalidParams,

        /// <summary>Changing the sink's state failed.</summary>
        StateChange,
    }

    /// <summary>Which upstream variant this error is.</summary>
    public Reason Cause { get; }

    /// <summary>Creates the error; the message renders like upstream's <c>#[error]</c> text.</summary>
    /// <param name="cause">The variant.</param>
    /// <param name="detail">The variant's payload (a backend-specific message).</param>
    /// <param name="inner">The underlying exception, if any.</param>
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
    /// <summary>Opens or resumes the output before samples are written.</summary>
    void Start()
    {
    }

    /// <summary>Drains and closes or pauses the output, e.g. when playback is paused.</summary>
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
    /// <summary>The sample format the sink writes.</summary>
    AudioFormat Format { get; }

    /// <summary>Writes already converted PCM bytes (upstream <c>SinkAsBytes::write_bytes</c>).</summary>
    void WriteBytes(ReadOnlySpan<byte> data);
}

/// <summary>Shared <see cref="ISink.Write"/> implementation for byte-oriented sinks.</summary>
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
                        sink.WriteBytes(MemoryMarshal.AsBytes<double>(s.Data));
                        break;
                    case AudioFormat.F32:
                        sink.WriteBytes(MemoryMarshal.AsBytes<float>(converter.F64ToF32(s.Data)));
                        break;
                    case AudioFormat.S32:
                        sink.WriteBytes(MemoryMarshal.AsBytes<int>(converter.F64ToS32(s.Data)));
                        break;
                    case AudioFormat.S24:
                        sink.WriteBytes(MemoryMarshal.AsBytes<int>(converter.F64ToS24(s.Data)));
                        break;
                    case AudioFormat.S24_3:
                        sink.WriteBytes(converter.F64ToS24_3(s.Data));
                        break;
                    case AudioFormat.S16:
                        sink.WriteBytes(MemoryMarshal.AsBytes<short>(converter.F64ToS16(s.Data)));
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
        ("naudio", OpenNAudio), // NAudioSink.Name (a Windows-only type); default goes first
        (StdoutSink.Name, StdoutSink.Open),
        (SubprocessSink.Name, SubprocessSink.Open),
    ];

    /// <summary>Upstream <c>mk_rodio</c>: WASAPI only exists on Windows, so other platforms get a sink error.</summary>
    private static ISink OpenNAudio(string? device, AudioFormat format) =>
        OperatingSystem.IsWindows()
            ? NAudioSink.Open(device, format)
            : throw new SinkException(SinkException.Reason.InvalidParams, "<NAudioSink> WASAPI output is only available on Windows");

    /// <summary>
    /// Upstream name of the backend that "naudio" replaces (<c>RodioSink::NAME</c>). <see cref="Find"/> accepts it as an
    /// alias so command lines written for upstream (<c>--backend rodio</c>) keep working; it is not listed separately.
    /// </summary>
    public const string RodioAlias = "rodio";

    /// <summary>Finds a backend by name, or the default when <paramref name="name"/> is null; null when unknown.</summary>
    public static SinkBuilder? Find(string? name)
    {
        if (name is null)
        {
            return Backends.Count > 0 ? Backends[0].Builder : null;
        }

        if (name == RodioAlias)
        {
            name = "naudio"; // NAudioSink.Name
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
