using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Discovery;

/// <summary>A receiver seen on the LAN: both service records merged by device id, plus the resolved addresses.</summary>
/// <param name="Id">Stable identifier: the <c>deviceid</c> TXT value (MAC-style), falling back to the instance name.</param>
/// <param name="Name">User-visible name (the <c>_airplay._tcp</c> instance name).</param>
/// <param name="Addresses">A/AAAA addresses from the records, never the UDP source address (mDNS may be reflected, SPEC-control.md 1.1).</param>
/// <param name="Port">The <c>_airplay._tcp</c> SRV port (7000 on both LAN receivers).</param>
/// <param name="Airplay">The parsed <c>_airplay._tcp</c> TXT record.</param>
/// <param name="Raop">The parsed <c>_raop._tcp</c> TXT record, when seen.</param>
/// <param name="LastSeen">When the last record for this receiver arrived.</param>
public sealed record DiscoveredReceiver(
    string Id,
    string Name,
    IReadOnlyList<IPAddress> Addresses,
    int Port,
    AirplayTxtRecord Airplay,
    RaopTxtRecord? Raop,
    DateTimeOffset LastSeen)
{
    /// <summary>What the TXT records say about pairing, streams and metadata.</summary>
    public ReceiverCapabilities Capabilities => ReceiverCapabilities.From(Airplay, Raop);

    /// <summary>The first address of the requested family, or null.</summary>
    public IPAddress? AddressOf(System.Net.Sockets.AddressFamily family) => Addresses.FirstOrDefault(a => a.AddressFamily == family);
}

/// <summary>Browser notifications.</summary>
public abstract record ReceiverEvent(DiscoveredReceiver Receiver)
{
    /// <summary>A receiver appeared (both records resolved, or one of them plus addresses).</summary>
    public sealed record Found(DiscoveredReceiver Receiver) : ReceiverEvent(Receiver);

    /// <summary>A TXT record or address of a known receiver changed (for example <c>gid</c> after a group SETUP, or <c>pk</c> after a reset).</summary>
    public sealed record Updated(DiscoveredReceiver Receiver, DiscoveredReceiver Previous) : ReceiverEvent(Receiver);

    /// <summary>A receiver's records expired (TTL) or a goodbye packet arrived.</summary>
    public sealed record Lost(DiscoveredReceiver Receiver) : ReceiverEvent(Receiver);
}

/// <summary>Options of the mDNS browser.</summary>
public sealed record ReceiverBrowserOptions
{
    /// <summary>Interfaces/addresses to bind the multicast sockets to; empty means every up, non-loopback interface.</summary>
    public IReadOnlyList<IPAddress> LocalAddresses { get; init; } = [];

    /// <summary>Whether to send IPv6 queries and keep AAAA records.</summary>
    public bool EnableIPv6 { get; init; } = true;

    /// <summary>Interval of repeated PTR queries while browsing.</summary>
    public TimeSpan QueryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Grace period after a record's TTL before <see cref="ReceiverEvent.Lost"/> is raised.</summary>
    public TimeSpan ExpiryGrace { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Continuous DNS-SD browse for <c>_airplay._tcp.local</c> and <c>_raop._tcp.local</c> (SPEC-control.md 1.1).
/// Implementations keep a cache keyed by instance name, merge the two records of one device, and raise events.
/// </summary>
public interface IReceiverBrowser : IAsyncDisposable
{
    /// <summary>Events in arrival order; a bounded channel that drops the oldest when nobody reads.</summary>
    ChannelReader<ReceiverEvent> Events { get; }

    /// <summary>The current cache.</summary>
    IReadOnlyList<DiscoveredReceiver> Snapshot();

    /// <summary>Binds the sockets and sends the first queries. Idempotent.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Sends PTR queries now (for a "refresh" button) and waits up to <paramref name="settle"/> for answers.</summary>
    Task RefreshAsync(TimeSpan settle, CancellationToken cancellationToken);

    /// <summary>Resolves one receiver by id, waiting up to <paramref name="timeout"/> for it to appear.</summary>
    Task<DiscoveredReceiver?> FindAsync(string id, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Service type names.</summary>
public static class AirplayServiceTypes
{
    public const string Airplay = "_airplay._tcp.local";
    public const string Raop = "_raop._tcp.local";
}

/// <summary>
/// mDNS implementation of <see cref="IReceiverBrowser"/> on top of <c>Librespot.Discovery.Mdns.DnsMessage</c>
/// (PTR/SRV/TXT/A/AAAA parsing already exists there). One socket per interface and address family, multicast group
/// 224.0.0.251 / ff02::fb, port 5353, with unicast-response queries as a fallback for reflectors.
/// </summary>
public sealed class MdnsReceiverBrowser : IReceiverBrowser
{
    private readonly Channel<ReceiverEvent> _events = Channel.CreateBounded<ReceiverEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Creates a browser.</summary>
    public MdnsReceiverBrowser(ReceiverBrowserOptions options, ILogger<MdnsReceiverBrowser> logger)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The options.</summary>
    public ReceiverBrowserOptions Options { get; }

    private ILogger Logger { get; }

    /// <inheritdoc />
    public ChannelReader<ReceiverEvent> Events => _events.Reader;

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredReceiver> Snapshot() => []; // TODO(impl: mdns-browser)

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: mdns-browser)");

    /// <inheritdoc />
    public Task RefreshAsync(TimeSpan settle, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: mdns-browser)");

    /// <inheritdoc />
    public Task<DiscoveredReceiver?> FindAsync(string id, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: mdns-browser)");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
