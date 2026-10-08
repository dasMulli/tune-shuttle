// Port of playback/src/audio_backend/pipe.rs.
using Librespot.Core.Logging;
using Librespot.Playback.Decoder;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback.AudioBackend;

/// <summary>
/// Upstream <c>StdoutSink</c> (backend name <c>"pipe"</c>): writes raw PCM in the configured
/// <see cref="AudioFormat"/> to stdout, or to a file when a device (file path) is given.
/// The output is opened by <see cref="Start"/> and flushed and released by <see cref="Stop"/>.
/// </summary>
public sealed class StdoutSink : ISinkAsBytes
{
    /// <summary>Backend name used by the registry (upstream <c>StdoutSink::NAME</c>).</summary>
    public const string Name = "pipe";

    private static readonly ILogger Log = LibrespotLog.CreateLogger<StdoutSink>();

    private readonly string? _file;
    private Stream? _output;

    /// <summary>Upstream <c>Open::open</c>. A device of <c>"?"</c> prints the usage and exits the process, like upstream.</summary>
    /// <param name="file">Output file path, or null to write to stdout.</param>
    /// <param name="format">Sample format written to the output.</param>
    public StdoutSink(string? file, AudioFormat format)
    {
        if (file == "?")
        {
            Console.WriteLine(
                "\nUsage:\n\nOutput to stdout:\n\n\t--backend pipe\n\nOutput to file:\n\n\t--backend pipe --device {filename}\n");
            Environment.Exit(0);
        }

        Log.LogInformation("Using StdoutSink (pipe) with format: {Format}", format);

        _file = file;
        Format = format;
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <summary>The <see cref="SinkBuilder"/> for this backend (upstream <c>mk_sink::&lt;StdoutSink&gt;</c>).</summary>
    public static ISink Open(string? file, AudioFormat format) => new StdoutSink(file, format);

    /// <summary>Opens the output (truncating/creating the file) unless it is already open.</summary>
    public void Start()
    {
        if (_output is not null)
        {
            return;
        }

        if (_file is null)
        {
            _output = Console.OpenStandardOutput();
            return;
        }

        try
        {
            _output = new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw OpenFailure(_file, e);
        }
    }

    /// <summary>Flushes and releases the output; throws <see cref="SinkException.Reason.NotConnected"/> when it is not open.</summary>
    public void Stop()
    {
        var output = _output ?? throw NoOutput();
        _output = null;

        try
        {
            output.Flush();
        }
        catch (IOException e)
        {
            throw FlushFailure(e);
        }
        finally
        {
            // Upstream drops the writer here, which closes the file. Stdout itself stays usable.
            if (_file is not null)
            {
                output.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Write(AudioPacket packet, Converter converter) => this.WriteAsBytes(packet, converter);

    /// <inheritdoc />
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        var output = _output ?? throw NoOutput();

        try
        {
            output.Write(data);
        }
        catch (IOException e)
        {
            throw OnWrite(e);
        }
    }

    /// <summary>Closes an open output file (upstream <c>Drop</c> of the boxed writer); stdout is left open.</summary>
    public void Dispose()
    {
        if (_output is not null && _file is not null)
        {
            _output.Dispose();
        }

        _output = null;
    }

    // Upstream `StdoutError` and its `From<StdoutError> for SinkError` mapping.
    private static SinkException OnWrite(Exception e) =>
        new(SinkException.Reason.OnWrite, $"<StdoutSink> {e.Message}", e);

    private static SinkException OpenFailure(string file, Exception e) =>
        new(SinkException.Reason.ConnectionRefused, $"<StdoutSink> File Path {file} Can Not be Opened and/or Created, {e.Message}", e);

    private static SinkException FlushFailure(Exception e) =>
        new(SinkException.Reason.OnWrite, $"<StdoutSink> Failed to Flush the Output Stream, {e.Message}", e);

    private static SinkException NoOutput() =>
        new(SinkException.Reason.NotConnected, "<StdoutSink> The Output Stream is None");
}
