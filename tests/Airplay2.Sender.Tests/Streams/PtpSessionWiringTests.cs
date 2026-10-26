using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>
/// The SETPEERS wiring: a PTP session SETUP carrying the master's clock id, the receiver's
/// <c>timingPeerInfo</c> turned into a grandmaster peer, <c>SETPEERS</c> with the peer list, and PTP traffic reaching the
/// fake receiver's slave.
/// </summary>
public class PtpSessionWiringTests
{
    [Fact]
    public async Task Ptp_session_setup_setpeers_and_sync_reach_the_fake_receiver()
    {
        await using var receiver = await SessionTestSupport.StartAsync();
        await using var slave = new FakePtpSlave();
        await slave.StartAsync(CancellationToken.None);
        var clock = new MasterClock();
        await using var port = new PtpPort(new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 });
        await using var master = new PtpMaster(clock, port, NullLogger<PtpMaster>.Instance, new PtpMasterOptions
        {
            PeerEventPort = slave.EventPort,
            PeerGeneralPort = slave.GeneralPort,
        });
        await master.StartAsync(CancellationToken.None);
        await using var session = await SessionTestSupport.ConnectAndPairAsync(receiver);

        var request = SessionTestSupport.NtpSession() with { TimingProtocol = TimingProtocol.Ptp, TimingPort = 0, PtpClockId = master.ClockId };
        var setup = await session.SetupSessionAsync(request, CancellationToken.None);
        var body = (await Fake.FakeReceiverHarness.NextAsync<FakeReceiverEvent.SessionSetup>(receiver)).Body;
        Assert.Equal("PTP", body.GetString("timingProtocol"));
        Assert.Equal(unchecked((long)master.ClockId), body.GetDictionary("timingPeerInfo")!.GetInt64("ClockID"));
        Assert.Equal(IPAddress.Loopback, Assert.Single(setup.TimingPeerAddresses));

        await session.ConnectEventChannelAsync(CancellationToken.None);
        await session.RecordAsync(CancellationToken.None);
        var peer = await PtpSessionWiring.AttachAsync(session, master, setup, null, CancellationToken.None);
        Assert.Equal(IPAddress.Loopback, peer);
        var peers = await Fake.FakeReceiverHarness.NextAsync<FakeReceiverEvent.Peers>(receiver);
        Assert.Equal(["127.0.0.1"], peers.Addresses);
        Assert.Equal(IPAddress.Loopback, Assert.Single(master.Peers).Address);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (slave.Count(PtpMessageType.FollowUp) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(slave.Count(PtpMessageType.FollowUp) >= 2);
        Assert.Equal(master.ClockId, slave.MasterClockId);

        await session.TeardownAsync(CancellationToken.None);
        master.RemovePeer(peer);
        Assert.Empty(master.Peers);
    }

    [Fact]
    public async Task Failed_setpeers_removes_the_peer_again()
    {
        await using var master = new PtpMaster(new MasterClock(), null, null, new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 });
        var connection = Substitute.For<IRtspConnection>();
        connection.RemoteEndPoint.Returns(new IPEndPoint(IPAddress.Parse("192.0.2.26"), 7000));
        connection.LocalAddress.Returns(IPAddress.Parse("203.0.113.150"));
        var session = Substitute.For<IReceiverSession>();
        session.Connection.Returns(connection);
        session.SetPeersAsync(Arg.Any<IReadOnlyList<IPAddress>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("closed"));
        var setup = new SessionSetupResult(7006, 0, [IPAddress.Parse("192.0.2.26")], new PlistDictionary());

        await Assert.ThrowsAsync<IOException>(() => PtpSessionWiring.AttachAsync(session, master, setup, null, CancellationToken.None));
        Assert.Empty(master.Peers);
    }

    [Fact]
    public async Task Setpeers_lists_the_peer_first_then_our_addresses()
    {
        await using var master = new PtpMaster(new MasterClock(), null, null, new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 });
        var connection = Substitute.For<IRtspConnection>();
        connection.RemoteEndPoint.Returns(new IPEndPoint(IPAddress.Parse("192.0.2.26"), 7000));
        connection.LocalAddress.Returns(IPAddress.Parse("203.0.113.150"));
        var session = Substitute.For<IReceiverSession>();
        session.Connection.Returns(connection);
        IReadOnlyList<IPAddress>? sent = null;
        session.SetPeersAsync(Arg.Do<IReadOnlyList<IPAddress>>(p => sent = p), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var setup = new SessionSetupResult(7006, 0, [IPAddress.Parse("2001:db8:0:30:1a:2bff:fe3c:4d5e"), IPAddress.Parse("192.0.2.26")], new PlistDictionary());

        var peer = await PtpSessionWiring.AttachAsync(session, master, setup, [IPAddress.Parse("203.0.113.150"), IPAddress.Parse("2001:db8::150")], CancellationToken.None);
        Assert.Equal(IPAddress.Parse("192.0.2.26"), peer);
        Assert.Equal([IPAddress.Parse("192.0.2.26"), IPAddress.Parse("203.0.113.150"), IPAddress.Parse("2001:db8::150")], sent);
    }

    // Receiver A's answer (srcvers 366.0, SPEC.md P1): IPv4, two ULA (documentation prefix here) and one link-local address.
    private static readonly IPAddress[] SpeakerAAddresses =
    [
        IPAddress.Parse("192.0.2.26"),
        IPAddress.Parse("2001:db8:0:30:1a:2bff:fe3c:4d5e"),
        IPAddress.Parse("2001:db8:0:30::26"),
        IPAddress.Parse("fe80::1a:2bff:fe3c:4d5e"),
    ];

    [Fact]
    public void Selects_the_connected_address_when_listed()
    {
        Assert.Equal(IPAddress.Parse("192.0.2.26"), PtpSessionWiring.SelectPeerAddress(SpeakerAAddresses, IPAddress.Parse("192.0.2.26"), IPAddress.Parse("203.0.113.150")));
        Assert.Equal(IPAddress.Parse("192.0.2.26"), PtpSessionWiring.SelectPeerAddress(SpeakerAAddresses, IPAddress.Parse("192.0.2.26").MapToIPv6(), IPAddress.Parse("203.0.113.150").MapToIPv6()));
    }

    [Fact]
    public void Selects_an_address_of_our_family_when_the_connected_one_is_not_listed()
    {
        // Connected over IPv6 (ULA in the capture) to an address the receiver does not list: first ULA, link-local skipped.
        var picked = PtpSessionWiring.SelectPeerAddress(SpeakerAAddresses, IPAddress.Parse("2001:db8:0:30::99"), IPAddress.Parse("2001:db8:0:30::150"));
        Assert.Equal(IPAddress.Parse("2001:db8:0:30:1a:2bff:fe3c:4d5e"), picked);

        // We are link-local: the receiver's link-local address gets our scope.
        var local = new IPAddress(IPAddress.Parse("fe80::150").GetAddressBytes(), 7);
        var linkLocal = PtpSessionWiring.SelectPeerAddress([IPAddress.Parse("fe80::1a:2bff:fe3c:4d5e")], IPAddress.Parse("fe80::1"), local);
        Assert.Equal(7, linkLocal.ScopeId);

        // Nothing usable: the connected address.
        Assert.Equal(IPAddress.Parse("10.0.0.5"), PtpSessionWiring.SelectPeerAddress([IPAddress.Parse("fe80::1")], IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.2")));
        Assert.Equal(IPAddress.Parse("10.0.0.5"), PtpSessionWiring.SelectPeerAddress([], IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.2")));
    }
}
