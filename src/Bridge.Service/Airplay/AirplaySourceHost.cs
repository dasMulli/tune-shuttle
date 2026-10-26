using Airplay2.Receiver;
using Airplay2.Receiver.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Bridge.Service.Spotify;
using Bridge.Service.Store;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Airplay;

/// <summary>
/// Everything <see cref="AirplaySource"/> needs of the bridge (SPEC-receiver.md 8.2, 8.3): the ownership gates of the
/// <see cref="SourceSwitch"/>, the output (the active <see cref="IGroupPlayer"/> and <see cref="OutputManager"/>), the
/// store, the shared clock, the DTOs of <see cref="DtoFactory"/> and the event publication. <see cref="BridgeAirplaySourceHost"/>
/// is the product implementation; tests use a stub so the source runs against a stub player without a bridge.
/// </summary>
internal interface IAirplaySourceHost
{
    /// <summary>The output owner (SPEC-receiver.md 8.2 <c>SourceSwitch.Owner</c>).</summary>
    AudioSource Owner { get; }

    /// <summary>The per-write gate (SPEC-receiver.md 8.2 <c>SourceSwitch.MayWrite(Airplay)</c>).</summary>
    bool MayWrite { get; }

    /// <summary>The command gate (SPEC-receiver.md 8.2 <c>SourceSwitch.Admit(Airplay, command)</c>): false drops the command (counted).</summary>
    bool Admit(string command);

    /// <summary>The start gate (SPEC-receiver.md 8.2 <c>SourceSwitch.AdmitStart(Airplay)</c>): a dropped start is remembered and replayed on ownership.</summary>
    bool AdmitStart();

    /// <summary>The active player, read per call (SPEC-receiver.md 8.3: the source keeps running when the output changes).</summary>
    IGroupPlayer? Player { get; }

    /// <summary>The output manager's commands: flush, now-playing, source level, current level (SPEC-receiver.md 8.3, 8.6).</summary>
    IPlaybackOutput Output { get; }

    /// <summary>Claims the output for the sender (SPEC-receiver.md R8, 8.2 <c>ClaimAsync(Airplay)</c>): completes after the eviction.</summary>
    Task ClaimAsync(SenderInfo sender, CancellationToken cancellationToken);

    /// <summary>Releases the output when AirPlay owns it (SPEC-receiver.md 8.2 <c>Release(Airplay)</c>).</summary>
    void Release();

    /// <summary>Loads the receiver settings (SPEC-receiver.md 8.4): the identity is created once.</summary>
    Task<AirplaySettings> LoadSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Persists the receiver settings (SPEC-receiver.md 8.4); the identity is never rewritten.</summary>
    Task SaveSettingsAsync(AirplaySettings settings, CancellationToken cancellationToken);

    /// <summary>The sender latency <c>L</c> in frames, for <c>Lpipe</c> (SPEC-receiver.md 5.4).</summary>
    Task<int> LatencyFramesAsync(CancellationToken cancellationToken);

    /// <summary>The process clock the group player's pacer runs on (SPEC-receiver.md 5.3: <c>AirplaySender.Clock</c>), or null when the sender could not be created.</summary>
    Task<IMasterClock?> ClockAsync(CancellationToken cancellationToken);

    /// <summary>The registered-controller store (SPEC-receiver.md 4.2, 8.4).</summary>
    IControllerStore Controllers { get; }

    /// <summary>The level of the current Spotify view (the DTO's <c>SpotifyVolume</c> while the sender set no level).</summary>
    int SpotifyVolume { get; }

    /// <summary>
    /// Builds the AirPlay <see cref="PlaybackDto"/> (SPEC-receiver.md 8.3, 9.1: <c>DtoFactory.AirplayPlayback</c>, which also keeps
    /// the artwork for <c>GET /api/playback/artwork</c>).
    /// </summary>
    PlaybackDto BuildPlayback(TrackMetadata? metadata, bool playing, TimeSpan elapsed, ReadOnlyMemory<byte>? artwork, string? artworkContentType, uint? artworkKey, int level);

    /// <summary>Forgets the kept artwork (SPEC-receiver.md 9.1: the session ended, the route answers 404).</summary>
    void ClearArtwork();

    /// <summary>
    /// Stores the AirPlay playback view (SPEC-receiver.md 8.2 <c>RuntimeState.AirplayPlayback</c>) and, while AirPlay owns the
    /// output, publishes it as the owner's view (<c>SourceSwitch.PublishPlayback</c>).
    /// </summary>
    void PublishPlayback(PlaybackDto playback);

    /// <summary>Publishes <c>statusChanged</c> with the status <c>DtoFactory</c> builds (SPEC-receiver.md 8.3).</summary>
    Task PublishStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The product <see cref="IAirplaySourceHost"/> (SPEC-receiver.md 8.2, 8.3). <see cref="OutputManager"/>, <see cref="DtoFactory"/>
/// and <see cref="AirplaySenderProvider"/> are resolved on first use: they (may) depend on the source themselves.
/// </summary>
internal sealed class BridgeAirplaySourceHost(
    SourceSwitch sources,
    IBridgeStore store,
    RuntimeState runtime,
    EventHub hub,
    IOptions<BridgeOptions> options,
    IServiceProvider services,
    ILogger logger) : IAirplaySourceHost
{
    private OutputManager? _output;
    private DtoFactory? _dtos;

    private OutputManager OutputManager => _output ??= services.GetRequiredService<OutputManager>();

    private DtoFactory Dtos => _dtos ??= services.GetRequiredService<DtoFactory>();

    /// <inheritdoc />
    public AudioSource Owner => sources.Owner;

    /// <inheritdoc />
    public bool MayWrite => sources.MayWrite(AudioSource.Airplay);

    /// <inheritdoc />
    public bool Admit(string command) => sources.Admit(AudioSource.Airplay, command);

    /// <inheritdoc />
    public bool AdmitStart() => sources.AdmitStart(AudioSource.Airplay);

    /// <inheritdoc />
    public IGroupPlayer? Player => OutputManager.ActivePlayer;

    /// <inheritdoc />
    public IPlaybackOutput Output => OutputManager;

    /// <inheritdoc />
    public IControllerStore Controllers { get; } = new BridgeControllerStore(store);

    /// <inheritdoc />
    public int SpotifyVolume => runtime.SpotifyPlayback.SpotifyVolume;

    /// <inheritdoc />
    public Task ClaimAsync(SenderInfo sender, CancellationToken cancellationToken) => sources.ClaimAsync(AudioSource.Airplay, sender, cancellationToken);

    /// <inheritdoc />
    public void Release() => sources.Release(AudioSource.Airplay);

    /// <inheritdoc />
    public async Task<AirplaySettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var bridge = await BridgeSettings.LoadAsync(store, options.Value, cancellationToken).ConfigureAwait(false);
        return await AirplaySettings.LoadAsync(store, options.Value.Airplay, bridge.DeviceName, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SaveSettingsAsync(AirplaySettings settings, CancellationToken cancellationToken) => settings.SaveAsync(store, cancellationToken);

    /// <inheritdoc />
    public async Task<int> LatencyFramesAsync(CancellationToken cancellationToken) =>
        (await BridgeSettings.LoadAsync(store, options.Value, cancellationToken).ConfigureAwait(false)).LatencyFrames;

    /// <inheritdoc />
    public async Task<IMasterClock?> ClockAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sender = await services.GetRequiredService<AirplaySenderProvider>().GetAsync(cancellationToken).ConfigureAwait(false);
            return sender.Clock;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // SPEC-receiver.md 5.3: without the sender's clock the playout buffer and the pacer run on two timelines; the
            // receiver still works on its own MasterClock, only the alignment is looser.
            logger.LogWarning("The AirPlay sender could not be created ({Error}); the receiver uses its own clock", ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public PlaybackDto BuildPlayback(TrackMetadata? metadata, bool playing, TimeSpan elapsed, ReadOnlyMemory<byte>? artwork, string? artworkContentType, uint? artworkKey, int level) =>
        Dtos.AirplayPlayback(metadata, playing, elapsed, artwork, artworkContentType, artworkKey, level);

    /// <inheritdoc />
    public void ClearArtwork() => Dtos.ClearAirplayArtwork();

    /// <inheritdoc />
    public void PublishPlayback(PlaybackDto playback) => sources.PublishPlayback(AudioSource.Airplay, playback);

    /// <inheritdoc />
    public async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        var status = await Dtos.StatusAsync(cancellationToken).ConfigureAwait(false);
        hub.Publish(new BridgeEvent.StatusChanged(status));
    }
}
