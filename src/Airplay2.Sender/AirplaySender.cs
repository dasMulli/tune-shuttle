using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender;

/// <summary>Top-level options.</summary>
public sealed record AirplaySenderOptions
{
    /// <summary>Who we say we are in the session SETUP.</summary>
    public SenderIdentity Identity { get; init; } = new("airplay2-streamer", "AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF");

    /// <summary>Browser options.</summary>
    public ReceiverBrowserOptions Browser { get; init; } = new();

    /// <summary>Default group player options.</summary>
    public GroupPlayerOptions Player { get; init; } = new();

    /// <summary>Preferred stream type; buffered falls back to realtime when a receiver lacks bit 40.</summary>
    public StreamType PreferredStream { get; init; } = StreamType.Realtime;

    /// <summary>Per-receiver quirks by receiver id.</summary>
    public IReadOnlyDictionary<string, ReceiverQuirks> Quirks { get; init; } = new Dictionary<string, ReceiverQuirks>();
}

/// <summary>
/// The library facade used by the CLI and the bridge: discovery, sessions and group players share one master clock,
/// one timing responder and one control port per process (SPEC-audio.md 3.1).
/// </summary>
public sealed class AirplaySender : IAsyncDisposable
{
    /// <summary>Creates the sender.</summary>
    public AirplaySender(AirplaySenderOptions options, IPairingStore pairingStore, ILoggerFactory loggerFactory)
    {
        Options = options;
        PairingStore = pairingStore;
        LoggerFactory = loggerFactory;
        Clock = new MasterClock();
        Browser = new MdnsReceiverBrowser(options.Browser, loggerFactory.CreateLogger<MdnsReceiverBrowser>());
    }

    /// <summary>The options.</summary>
    public AirplaySenderOptions Options { get; }

    /// <summary>Where regular pairings live.</summary>
    public IPairingStore PairingStore { get; }

    /// <summary>The process-wide master clock.</summary>
    public IMasterClock Clock { get; }

    /// <summary>The receiver browser (call <see cref="IReceiverBrowser.StartAsync"/>).</summary>
    public IReceiverBrowser Browser { get; }

    private ILoggerFactory LoggerFactory { get; }

    /// <summary>Opens a control connection to a receiver, runs GET /info and the pairing it requires; no SETUP yet.</summary>
    public Task<IReceiverSession> ConnectAsync(DiscoveredReceiver receiver, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: sender-api-cli)");

    /// <summary>Starts a PIN pairing for the web app flow: returns after M2 so the UI can ask for the PIN.</summary>
    public Task<PinPairingInProgress> BeginPinPairingAsync(DiscoveredReceiver receiver, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: sender-api-cli)");

    /// <summary>Completes a PIN pairing and persists the credentials.</summary>
    public Task<PairingCredentials> CompletePinPairingAsync(PinPairingInProgress pairing, string pin, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: sender-api-cli)");

    /// <summary>Creates a group player with the preferred stream transport; receivers are added with <see cref="IGroupPlayer.AddReceiverAsync"/>.</summary>
    public Task<IGroupPlayer> CreateGroupPlayerAsync(GroupPlayerOptions? options, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: sender-api-cli)");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Browser.DisposeAsync();
}
