using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Airplay2.FakeReceiver;
using Airplay2.Receiver.Rtsp;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Airplay2.Sender.Tests.Rtsp;

/// <summary>Captures log entries (for asserting the decrypted wire log and for the live-test wire logs).</summary>
internal sealed class CapturingLogger(LogLevel minimum = LogLevel.Trace) : ILogger
{
    public ConcurrentQueue<(DateTimeOffset At, LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Entries.Enqueue((DateTimeOffset.Now, logLevel, formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message)));
        }
    }

    public string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (at, level, message) in Entries)
        {
            sb.Append(at.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(level).Append(' ').AppendLine(message);
        }

        return sb.ToString();
    }
}

/// <summary>A client <see cref="RtspConnection"/> and an <see cref="RtspServerConnection"/> on loopback.</summary>
internal sealed class LoopbackPair : IAsyncDisposable
{
    private readonly Socket _listener;

    private LoopbackPair(Socket listener, RtspConnection client, RtspServerConnection server, CapturingLogger clientLog)
    {
        _listener = listener;
        Client = client;
        Server = server;
        ClientLog = clientLog;
    }

    public RtspConnection Client { get; }

    public RtspServerConnection Server { get; }

    public CapturingLogger ClientLog { get; }

    public static async Task<LoopbackPair> StartAsync(
        RtspRequestHandler handler,
        RtspConnectionOptions? clientOptions = null,
        RtspServerConnectionOptions? serverOptions = null)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(4);
        var accept = RtspServerConnection.AcceptAsync(listener, handler, serverOptions, null, CancellationToken.None);
        var log = new CapturingLogger();
        var client = await RtspConnection.ConnectAsync(
            (IPEndPoint)listener.LocalEndPoint!,
            clientOptions ?? new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(5) },
            log,
            CancellationToken.None);
        var server = await accept;
        return new LoopbackPair(listener, client, server, log);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        _listener.Dispose();
    }
}

/// <summary>Test data: the captured exchange with receiver A (a third-party AirPlay 2 speaker, srcvers 366.0).</summary>
internal static class SpeakerACapture
{
    public static string Vector(string name) => Path.Combine(AppContext.BaseDirectory, "TestVectors", name);

    /// <summary>The raw 525-byte GET /info body.</summary>
    public static byte[] InfoBody => File.ReadAllBytes(Vector("info-response.bplist"));

    /// <summary>The raw 909-byte stage-1 GET /info body.</summary>
    public static byte[] Stage1InfoBody => File.ReadAllBytes(Vector("info-response-stage1.bplist"));

    /// <summary>The identity the probe used (DACP-ID / Active-Remote in probes/get_info_speaker.txt).</summary>
    public static RtspClientIdentity ProbeIdentity => new("F27C859F90C330F8", 3046643544, RtspClientIdentity.DefaultUserAgent);

    /// <summary>
    /// Path of a file under docs/airplay2/probes, found by walking up from the test output directory, or from this
    /// source file when the output lives outside the repository (artifacts layout, the Docker VM run).
    /// </summary>
    public static string ProbeFile(string name, [CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) ?? string.Empty })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "docs", "airplay2", "probes", name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"docs/airplay2/probes/{name} not found above {AppContext.BaseDirectory} or {sourceFile}");
    }

    /// <summary>The repository root (for artifacts/live logs).</summary>
    public static string RepositoryRoot => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProbeFile("get_info_speaker.txt"))!, "..", "..", ".."));

    /// <summary>
    /// Extracts a section of the probe capture: the lines after <c>--- request ---</c> or <c>--- response head ---</c> of
    /// the given variant, up to the first empty line (request) or the next <c>---</c> marker (response head), as CRLF text.
    /// </summary>
    public static string ProbeSection(char variant, string section)
    {
        // The capture printed the raw CRLF heads with Python's text-mode stdout on Windows, so the file has CR CR LF
        // (or CR LF / LF after a checkout with other settings): split on LF and drop every trailing CR.
        var lines = File.ReadAllText(ProbeFile("get_info_speaker.txt")).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var start = Array.FindIndex(lines, l => l.StartsWith($"Variant {variant}:", StringComparison.Ordinal));
        Assert.True(start >= 0, $"variant {variant} not in the capture");
        var marker = Array.FindIndex(lines, start, l => l == $"--- {section} ---");
        Assert.True(marker >= 0, $"section {section} not in variant {variant}");
        var result = new List<string>();
        for (var i = marker + 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0 || lines[i].StartsWith("---", StringComparison.Ordinal))
            {
                break;
            }

            result.Add(lines[i]);
        }

        return string.Join("\r\n", result) + "\r\n\r\n";
    }
}
