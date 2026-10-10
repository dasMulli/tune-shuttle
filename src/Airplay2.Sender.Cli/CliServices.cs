using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Cli;

/// <summary>Process exit codes of the <c>airplay2</c> CLI.</summary>
public static class ExitCodes
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>Failure: invalid input, receiver not found, network or protocol error, a probe that failed.</summary>
    public const int Failure = 1;

    /// <summary>
    /// Audio refused: <c>play</c> or an audio-sending probe without <c>--i-know</c> (docs/DEVELOPMENT.md, "Live-device rules"). Nothing was
    /// sent and no socket was opened.
    /// </summary>
    public const int AudioRefused = 2;

    /// <summary>The requested probe is not available in this build (owned by a later package, or needs a person with a microphone).</summary>
    public const int Unavailable = 3;

    /// <summary>Interrupted (Ctrl+C); every session that was set up was torn down.</summary>
    public const int Interrupted = 130;
}

/// <summary>
/// What the commands talk to: console streams, logging, the pairing store, discovery and the sender options. The
/// defaults are the real console, mDNS and the JSON pairing file next to the executable; tests replace them (fake
/// receivers, an in-memory store, loopback sockets) and can thereby check that a refused command never reaches any of them.
/// </summary>
public sealed record CliServices
{
    /// <summary>Environment variable overriding the pairing file path.</summary>
    public const string PairingFileVariable = "AIRPLAY2_PAIRINGS";

    private readonly Func<ILoggerFactory, IReceiverDirectory>? _createDirectory;

    /// <summary>The real console, mDNS and the JSON pairing store.</summary>
    public static CliServices Default { get; } = new();

    /// <summary>Standard output (results).</summary>
    public TextWriter Out { get; init; } = Console.Out;

    /// <summary>Standard error (refusals, errors).</summary>
    public TextWriter Error { get; init; } = Console.Error;

    /// <summary>Asks the user a question (PIN entry) and returns the answer, or null at end of input.</summary>
    public Func<string, CancellationToken, Task<string?>> Prompt { get; init; } = ConsolePromptAsync;

    /// <summary>Creates the logger factory of a command: console at Information (Debug with <c>--verbose</c>), plus an optional file at Debug.</summary>
    public Func<CliLogOptions, ILoggerFactory> CreateLogging { get; init; } = CliLogging.Create;

    /// <summary>Creates the pairing store: <see cref="JsonFilePairingStore"/> at <c>%AIRPLAY2_PAIRINGS%</c> or next to the executable.</summary>
    public Func<ILoggerFactory, IPairingStore> CreatePairingStore { get; init; } = logging =>
        new JsonFilePairingStore(Environment.GetEnvironmentVariable(PairingFileVariable) is { Length: > 0 } path ? path : JsonFilePairingStore.DefaultPath, logging.CreateLogger<JsonFilePairingStore>());

    /// <summary>
    /// Creates the receiver directory: by default mDNS with <see cref="AirplaySenderOptions.Browser"/> of
    /// <see cref="SenderOptions"/> (the one browser of a command; the sender creates its own only when asked for it). Only
    /// commands that talk to receivers create one.
    /// </summary>
    public Func<ILoggerFactory, IReceiverDirectory> CreateDirectory
    {
        get => _createDirectory ?? CreateMdnsDirectory;
        init => _createDirectory = value;
    }

    /// <summary>Template for the sender options (identity is filled in from the pairing store's controller identity).</summary>
    public AirplaySenderOptions SenderOptions { get; init; } = new();

    /// <summary>How long name lookups wait for a receiver to appear in mDNS.</summary>
    public TimeSpan LookupTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Directory for probe logs; null: <c>artifacts/live</c> under the repository root (or the current directory).</summary>
    public string? LiveLogDirectory { get; init; }

    private IReceiverDirectory CreateMdnsDirectory(ILoggerFactory logging) =>
        new BrowserReceiverDirectory(new MdnsReceiverBrowser(SenderOptions.Browser, logging.CreateLogger<MdnsReceiverBrowser>()));

    private static async Task<string?> ConsolePromptAsync(string question, CancellationToken cancellationToken)
    {
        await Console.Out.WriteAsync(question.AsMemory(), cancellationToken).ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
    }
}
