using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Session;

namespace Airplay2.Sender.Streams;

/// <summary>
/// Connects a PTP-timed receiver session to the grandmaster (SPEC-audio.md 4.4, SPEC-control.md 4.6, SPEC.md D5): after
/// the session SETUP answered with the receiver's <c>timingPeerInfo.Addresses</c>, one of them becomes a unicast peer of
/// the <see cref="IPtpMaster"/> and <c>SETPEERS</c> tells the receiver the peer list <c>[receiver, our addresses...]</c>.
/// The D5 order puts this after RECORD and before the stream SETUP; shairport-sync points nqptp at our clock on the
/// stream SETUP (<c>[sps] rtsp.c:2756-2758</c>), so Syncs that arrive earlier are harmless.
/// </summary>
public static class PtpSessionWiring
{
    /// <summary>
    /// Picks the address to send PTP to from the receiver's <c>timingPeerInfo.Addresses</c>: the address our control
    /// connection uses when it is listed; otherwise the first listed address of the same family as
    /// <paramref name="localAddress"/> that we can route to (an IPv6 link-local address only when we are link-local too,
    /// with our interface scope attached); otherwise <paramref name="receiverAddress"/>. owntone likewise adds exactly one
    /// address of its own family as the peer (behaviour described in SPEC-audio.md 4.4, <c>[ot] airplay.c:3123-3178</c>).
    /// </summary>
    /// <param name="timingPeerAddresses">The receiver's <c>timingPeerInfo.Addresses</c> (<see cref="SessionSetupResult.TimingPeerAddresses"/>).</param>
    /// <param name="receiverAddress">The address the control connection talks to.</param>
    /// <param name="localAddress">Our address on the control connection.</param>
    public static IPAddress SelectPeerAddress(IReadOnlyList<IPAddress> timingPeerAddresses, IPAddress receiverAddress, IPAddress localAddress)
    {
        ArgumentNullException.ThrowIfNull(timingPeerAddresses);
        ArgumentNullException.ThrowIfNull(receiverAddress);
        ArgumentNullException.ThrowIfNull(localAddress);
        receiverAddress = Normalize(receiverAddress);
        localAddress = Normalize(localAddress);
        foreach (var candidate in timingPeerAddresses)
        {
            if (SameAddress(Normalize(candidate), receiverAddress))
            {
                return receiverAddress;
            }
        }

        foreach (var raw in timingPeerAddresses)
        {
            var candidate = Normalize(raw);
            if (candidate.AddressFamily != localAddress.AddressFamily)
            {
                continue;
            }

            if (candidate.AddressFamily == AddressFamily.InterNetworkV6 && candidate.IsIPv6LinkLocal)
            {
                if (!localAddress.IsIPv6LinkLocal)
                {
                    continue;
                }

                if (candidate.ScopeId == 0 && localAddress.ScopeId != 0)
                {
                    candidate = new IPAddress(candidate.GetAddressBytes(), localAddress.ScopeId);
                }
            }

            return candidate;
        }

        return receiverAddress;
    }

    /// <summary>
    /// Adds the receiver as a peer of <paramref name="master"/> and sends <c>SETPEERS</c> with
    /// <c>[peer, our addresses...]</c> (owntone's order, SPEC-control.md 4.6). On a failed SETPEERS the peer is removed
    /// again and the exception propagates. Returns the peer address, for <see cref="IPtpMaster.RemovePeer"/> on TEARDOWN.
    /// </summary>
    /// <param name="session">The receiver session, after the PTP session SETUP (and RECORD, D5).</param>
    /// <param name="master">The grandmaster.</param>
    /// <param name="setup">The session SETUP result.</param>
    /// <param name="localAddresses">Our addresses for SETPEERS; the control connection's local address when null.</param>
    /// <param name="cancellationToken">Cancels the SETPEERS request.</param>
    public static Task<IPAddress> AttachAsync(IReceiverSession session, IPtpMaster master, SessionSetupResult setup, IReadOnlyList<IPAddress>? localAddresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(master);
        return AttachAsync(session, setup, master.AddPeer, master.RemovePeer, localAddresses, cancellationToken);
    }

    /// <summary>
    /// As <see cref="AttachAsync(IReceiverSession, IPtpMaster, SessionSetupResult, IReadOnlyList{IPAddress}, CancellationToken)"/>,
    /// with the peer bookkeeping supplied by the caller, e.g. <see cref="RealtimeStreamTransport.AddTimingPeer"/> and
    /// <see cref="RealtimeStreamTransport.RemoveTimingPeer"/>, which reference-count peers shared by several sessions.
    /// </summary>
    /// <param name="session">The receiver session, after the PTP session SETUP (and RECORD, D5).</param>
    /// <param name="setup">The session SETUP result.</param>
    /// <param name="addPeer">Makes the address a grandmaster peer.</param>
    /// <param name="removePeer">Undoes <paramref name="addPeer"/> when SETPEERS fails.</param>
    /// <param name="localAddresses">Our addresses for SETPEERS; the control connection's local address when null.</param>
    /// <param name="cancellationToken">Cancels the SETPEERS request.</param>
    public static async Task<IPAddress> AttachAsync(IReceiverSession session, SessionSetupResult setup, Action<IPAddress> addPeer, Action<IPAddress> removePeer, IReadOnlyList<IPAddress>? localAddresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(addPeer);
        ArgumentNullException.ThrowIfNull(removePeer);
        var connection = session.Connection;
        var peer = SelectPeerAddress(setup.TimingPeerAddresses, connection.RemoteEndPoint.Address, connection.LocalAddress);
        var peers = new List<IPAddress> { peer };
        foreach (var local in localAddresses ?? [connection.LocalAddress])
        {
            var address = Normalize(local);
            if (!peers.Contains(address))
            {
                peers.Add(address);
            }
        }

        addPeer(peer);
        try
        {
            await session.SetPeersAsync(peers, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            removePeer(peer);
            throw;
        }

        return peer;
    }

    /// <summary>Maps an IPv4-mapped IPv6 address back to IPv4 (the <see cref="IPAddress"/> form of <c>UdpSockets.Normalize(IPEndPoint)</c>); shared with <see cref="PtpMaster"/>.</summary>
    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool SameAddress(IPAddress a, IPAddress b) =>
        a.AddressFamily == b.AddressFamily && a.GetAddressBytes().AsSpan().SequenceEqual(b.GetAddressBytes());
}
