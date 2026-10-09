using Bridge.Service.Output;
using Bridge.Service.Store;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bridge.Service.Spotify;

/// <summary>
/// Hosts librespot (docs/PLAN.md "Spotify side"): Zeroconf discovery with the configured device name, the credential
/// cache in SQLite, a Session + Spirc + Player with <see cref="AirplaySink"/> as the backend, the reconnect loop from
/// Librespot.Cli, and the mapping of player events to the group player (pause/seek/skip -> flush, track change ->
/// metadata/artwork/progress, volume -> G).
/// </summary>
public sealed class SpotifyHost(IBridgeStore store, AirplaySink sink, ILogger<SpotifyHost> logger) : BackgroundService
{
    private IBridgeStore Store { get; } = store;

    private AirplaySink Sink { get; } = sink;

    private ILogger Logger { get; } = logger;

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => throw new NotImplementedException("TODO(impl: bridge-service)");
}
