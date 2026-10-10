using Airplay2.Sender.Discovery;

namespace Airplay2.Sender.Cli;

/// <summary>Where the CLI finds receivers: mDNS in production, a fixed list in tests.</summary>
public interface IReceiverDirectory : IAsyncDisposable
{
    /// <summary>Browses for <paramref name="duration"/> and returns everything seen.</summary>
    Task<IReadOnlyList<DiscoveredReceiver>> BrowseAsync(TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Finds a receiver by device id or name (case-insensitive), waiting up to <paramref name="timeout"/>; null when not seen.</summary>
    Task<DiscoveredReceiver?> FindAsync(string nameOrId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary><see cref="IReceiverDirectory"/> over an <see cref="IReceiverBrowser"/> (it owns and disposes the browser).</summary>
public sealed class BrowserReceiverDirectory(IReceiverBrowser browser) : IReceiverDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredReceiver>> BrowseAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        await browser.StartAsync(cancellationToken).ConfigureAwait(false);
        await browser.RefreshAsync(duration, cancellationToken).ConfigureAwait(false);
        return browser.Snapshot();
    }

    /// <inheritdoc />
    public Task<DiscoveredReceiver?> FindAsync(string nameOrId, TimeSpan timeout, CancellationToken cancellationToken) =>
        browser.FindAsync(nameOrId, timeout, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => browser.DisposeAsync();
}

/// <summary>A fixed set of receivers (tests, or a host that already knows them); matching like <see cref="IReceiverBrowser.FindAsync"/>.</summary>
public sealed class StaticReceiverDirectory(IEnumerable<DiscoveredReceiver> receivers) : IReceiverDirectory
{
    private readonly List<DiscoveredReceiver> _receivers = [.. receivers];

    /// <summary>How many lookups and browses were made (tests check that a refused command made none).</summary>
    public int Lookups { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredReceiver>> BrowseAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        Lookups++;
        return Task.FromResult<IReadOnlyList<DiscoveredReceiver>>(_receivers);
    }

    /// <inheritdoc />
    public Task<DiscoveredReceiver?> FindAsync(string nameOrId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Lookups++;
        var wanted = Normalize(nameOrId);
        var found = _receivers.FirstOrDefault(r => Normalize(r.Id) == wanted)
            ?? _receivers.FirstOrDefault(r => string.Equals(r.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Normalize(string id) => id.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
}
