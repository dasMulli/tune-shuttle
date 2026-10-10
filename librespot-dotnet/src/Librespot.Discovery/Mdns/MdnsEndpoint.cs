// New code (no upstream .rs file): libmdns hard-codes the mDNS group and port (address_family.rs, `MDNS_PORT`). The
// responder and the AirPlay receiver browser take them as a parameter instead, so tests can run the real socket code on
// a private group that nothing else on the LAN listens to or reflects (see Airplay2.Sender.Tests'
// ReceiverBrowserLoopbackTests). Production code always uses Default.
using System.Net;
using System.Net.Sockets;

namespace Librespot.Discovery.Mdns;

/// <summary>
/// The multicast groups, UDP port and multicast hop limit an mDNS socket binds to, joins and sends to.
/// <see cref="Default"/> is the standard mDNS endpoint (RFC 6762: 224.0.0.251 / ff02::fb, port 5353, TTL 255).
/// </summary>
/// <param name="GroupV4">The IPv4 multicast group.</param>
/// <param name="GroupV6">The IPv6 multicast group.</param>
/// <param name="Port">The UDP port the listeners bind to and the queries/answers are sent to.</param>
/// <param name="HopLimit">IP TTL / IPv6 hop limit of the multicast packets sent (RFC 6762 section 11 asks for 255).</param>
public sealed record MdnsEndpoint(IPAddress GroupV4, IPAddress GroupV6, int Port, int HopLimit = 255)
{
    /// <summary>The standard mDNS endpoint: 224.0.0.251 and ff02::fb on port 5353, hop limit 255.</summary>
    public static MdnsEndpoint Default { get; } = new(IPAddress.Parse("224.0.0.251"), IPAddress.Parse("ff02::fb"), 5353);

    /// <summary>The IPv4 multicast group.</summary>
    public IPAddress GroupV4 { get; init; } = Check(GroupV4, AddressFamily.InterNetwork, nameof(GroupV4));

    /// <summary>The IPv6 multicast group.</summary>
    public IPAddress GroupV6 { get; init; } = Check(GroupV6, AddressFamily.InterNetworkV6, nameof(GroupV6));

    /// <summary>The UDP port.</summary>
    public int Port { get; init; } = Port is > 0 and <= 65535 ? Port : throw new ArgumentOutOfRangeException(nameof(Port), Port, "The port must be 1..65535.");

    /// <summary>The multicast hop limit.</summary>
    public int HopLimit { get; init; } = HopLimit is > 0 and <= 255 ? HopLimit : throw new ArgumentOutOfRangeException(nameof(HopLimit), HopLimit, "The hop limit must be 1..255.");

    /// <summary>The group of <paramref name="family"/> (InterNetwork or InterNetworkV6).</summary>
    public IPAddress GroupOf(AddressFamily family) => family == AddressFamily.InterNetworkV6 ? GroupV6 : GroupV4;

    /// <summary>The group of <paramref name="family"/> on <see cref="Port"/>.</summary>
    public IPEndPoint GroupEndPoint(AddressFamily family) => new(GroupOf(family), Port);

    /// <inheritdoc />
    public override string ToString() => $"{GroupV4} / {GroupV6} port {Port} (hop limit {HopLimit})";

    private static IPAddress Check(IPAddress group, AddressFamily family, string name)
    {
        ArgumentNullException.ThrowIfNull(group, name);
        var multicast = family == AddressFamily.InterNetwork
            ? group.AddressFamily == family && (group.GetAddressBytes()[0] & 0xF0) == 0xE0
            : group.AddressFamily == family && group.IsIPv6Multicast;
        return multicast ? group : throw new ArgumentException($"{group} is not an {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} multicast address.", name);
    }
}
