using System.Net;
using System.Threading.Channels;
using Librespot.Discovery.Mdns;

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
    /// <summary>A receiver appeared: its <c>_airplay._tcp</c> instance resolved (SRV, TXT and at least one A/AAAA address); the <c>_raop._tcp</c> record is merged in when seen (an <see cref="Updated"/> follows if it arrives later).</summary>
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

    /// <summary>
    /// Also send every query from an ephemeral port per interface (a legacy unicast query, RFC 6762 section 6.7), whose
    /// answers come back by unicast. Fallback for networks where an mDNS reflector or proxy answers on behalf of the
    /// receivers (SPEC-control.md 1.1) and for hosts where port 5353 cannot be bound. Such answers carry TTLs of at most
    /// 10 s; the browser keeps them for at least two <see cref="QueryInterval"/>s.
    /// </summary>
    public bool UseLegacyUnicastQueries { get; init; } = true;

    /// <summary>
    /// The multicast groups, port and hop limit to browse on: <see cref="MdnsEndpoint.Default"/> (224.0.0.251 /
    /// ff02::fb, port 5353) for every real use. Tests pass a private group and port so the same socket code runs without
    /// anything else on the network listening or reflecting.
    /// </summary>
    public MdnsEndpoint Endpoint { get; init; } = MdnsEndpoint.Default;
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

    /// <summary>Resolves one receiver by id (the <c>deviceid</c>, with or without colons) or, failing that, by its name (case-insensitive), waiting up to <paramref name="timeout"/> for it to appear; null on timeout. Starts the browser if needed.</summary>
    Task<DiscoveredReceiver?> FindAsync(string id, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Service type names.</summary>
public static class AirplayServiceTypes
{
    /// <summary>The AirPlay 2 service (HAP pairing, bplist SETUP).</summary>
    public const string Airplay = "_airplay._tcp.local";

    /// <summary>The RAOP (AirPlay 1) service; its TXT carries the audio keys merged into the receiver.</summary>
    public const string Raop = "_raop._tcp.local";
}
