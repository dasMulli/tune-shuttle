using System.Net;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Receiver.Tests.Timing;

/// <summary>
/// Our <see cref="PtpMaster"/> and the receiver's <see cref="PtpSlave"/> in one process (SPEC-receiver.md 6.1, 6.2, 11.1):
/// each on its own <see cref="PtpPort"/> on loopback with OS-chosen ports, the slave's peer ports pointing at the
/// master's, the slave's clock the master's plus a known offset. The slave follows the master and
/// <see cref="PtpSlave.TryMapToLocal"/> lands within 5 ms of the injected truth.
/// </summary>
public class PtpSlaveLoopbackTests
{
    private const long TrueOffset = 7_000_000_000L; // the slave's clock runs 7 s ahead of the master's
    private const long FiveMs = 5_000_000;

    private static PtpPortOptions Loopback => new() { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 };

    [Fact]
    public async Task Link_local_senders_route_unscoped_but_announce_to_the_control_connections_scope()
    {
        // SPEC-receiver.md 6.1/6.2: the plist carries fe80:: without a scope, the control connection's remote address has
        // one; the routing key is unscoped, the Announce target borrows the scope (an unscoped link-local send fails).
        await using var port = new PtpPort(Loopback); // never bound: the slave is not started
        await using var slave = new PtpSlave(new PtpSlaveOptions
        {
            Port = port,
            SenderAddresses = [IPAddress.Parse("fe80::10"), IPAddress.Parse("192.0.2.20"), IPAddress.Parse("fe80::10%5"), IPAddress.Parse("fe80::11")],
        });

        Assert.Equal(
            [IPAddress.Parse("fe80::10%5"), IPAddress.Parse("192.0.2.20"), IPAddress.Parse("fe80::11%5")],
            slave.AnnounceTargets);
        Assert.Equal(IPAddress.Parse("192.0.2.21"), PtpSlave.SendTarget(IPAddress.Parse("::ffff:192.0.2.21"), 5));
        Assert.Equal(IPAddress.Parse("fe80::12"), PtpSlave.SendTarget(IPAddress.Parse("fe80::12"), 0));
        Assert.Equal(7, PtpSlave.SendTarget(IPAddress.Parse("fe80::12%7"), 5).ScopeId);
    }

    [Fact]
    public async Task Slave_follows_the_master_within_five_milliseconds_and_announces_itself_twice()
    {
        var clock = new MasterClock();
        await using var masterPort = new PtpPort(Loopback);
        await using var slavePort = new PtpPort(Loopback);
        await using var master = new PtpMaster(clock, masterPort, NullLogger<PtpMaster>.Instance);
        await master.StartAsync(CancellationToken.None);
        await using var slave = new PtpSlave(new PtpSlaveOptions
        {
            Port = slavePort,
            SenderAddresses = [IPAddress.Loopback],
            PeerEventPort = masterPort.EventPort,
            PeerGeneralPort = masterPort.GeneralPort,
            LocalClock = MonotonicClock.FromDelegate(() => clock.PtpNowNanos + (ulong)TrueOffset),
        });
        await slave.StartAsync(CancellationToken.None);
        Assert.True(slavePort.IsBound);
        master.AddPeer(IPAddress.Loopback, slavePort.EventPort, slavePort.GeneralPort);

        await PtpPortRoutingTests.WaitUntilAsync(() => slave.Statistics.PairsSeen >= 16, "16 Sync/Follow_Up pairs");

        var senderNow = clock.PtpNowNanos;
        Assert.True(slave.TryMapToLocal(senderNow, out var local));
        Assert.InRange(unchecked((long)(local - senderNow)), TrueOffset - FiveMs, TrueOffset + FiveMs);
        var stats = slave.Statistics;
        Assert.InRange(stats.OffsetNanos!.Value, TrueOffset - FiveMs, TrueOffset + FiveMs);
        Assert.Equal(master.ClockId, stats.MasterClockId);
        Assert.NotNull(stats.LastSyncAge);
        Assert.True(stats.LastSyncAge < TimeSpan.FromSeconds(1));

        // The two start-up Announces of the slave's own clock reached the master (as nqptp's do), which reports the
        // foreign clock once and stays master.
        Assert.Equal(2, slave.AnnouncesSent);
        var foreign = await ReadEventAsync<PtpMasterEvent.ForeignAnnounce>(master);
        Assert.Equal(slave.ClockId, foreign.ClockId);
        Assert.Equal(slave.ClockId, foreign.GrandmasterId);

        // Everything the slave got came through its registration; the master's port routed the Announces to the master.
        Assert.True(slavePort.Statistics.RoutedToSlaves >= 32);
        Assert.Equal(0, slavePort.Statistics.RoutedToDefault);
        Assert.True(masterPort.Statistics.RoutedToDefault >= 2);
    }

    [Fact]
    public async Task Optional_delay_requests_go_to_the_peer_event_port_and_their_answers_come_back_to_the_slave()
    {
        var clock = new MasterClock();
        await using var masterPort = new PtpPort(Loopback);
        await using var slavePort = new PtpPort(Loopback);
        await using var master = new PtpMaster(clock, masterPort, NullLogger<PtpMaster>.Instance);
        await master.StartAsync(CancellationToken.None);
        await using var slave = new PtpSlave(new PtpSlaveOptions
        {
            Port = slavePort,
            SenderAddresses = [IPAddress.Loopback],
            PeerEventPort = masterPort.EventPort,
            PeerGeneralPort = masterPort.GeneralPort,
            SendDelayRequests = true,
            DelayRequestInterval = TimeSpan.FromMilliseconds(150),
            LocalClock = MonotonicClock.FromDelegate(() => clock.PtpNowNanos + (ulong)TrueOffset),
        });
        await slave.StartAsync(CancellationToken.None);
        master.AddPeer(IPAddress.Loopback, slavePort.EventPort, slavePort.GeneralPort);

        await PtpPortRoutingTests.WaitUntilAsync(() => slave.DelayResponses >= 3 && slave.Statistics.PairsSeen >= 8, "Delay_Resp answers");

        Assert.Equal(0, slave.MismatchedDelayResponses);
        Assert.True(master.GetStatistics().DelayResponsesSent >= 3);
        Assert.InRange(slave.MeanPathDelayNanos, 0, FiveMs);
        Assert.InRange(slave.OffsetNanos!.Value, TrueOffset - FiveMs, TrueOffset + FiveMs);
    }

    [Fact]
    public async Task A_port_that_cannot_bind_fails_the_slave_start_with_a_ptp_port_exception()
    {
        await using var taken = new PtpPort(Loopback);
        await taken.EnsureBoundAsync(CancellationToken.None);
        await using var conflict = new PtpPort(Loopback with { EventPort = taken.EventPort });
        await using var slave = new PtpSlave(new PtpSlaveOptions { Port = conflict, SenderAddresses = [IPAddress.Loopback] });

        var ex = await Assert.ThrowsAsync<PtpPortException>(() => slave.StartAsync(CancellationToken.None));
        Assert.Equal(taken.EventPort, ex.Port);
        Assert.False(slave.TryMapToLocal(0, out _)); // the receiver runs anchor-only (R5, R6)
    }

    [Fact]
    public async Task Dispose_unregisters_only_its_own_addresses_and_leaves_a_shared_port_open()
    {
        await using var port = new PtpPort(Loopback);
        var slave = new PtpSlave(new PtpSlaveOptions { Port = port, SenderAddresses = [IPAddress.Loopback], StartAnnounces = 0 });
        await slave.StartAsync(CancellationToken.None);
        var newer = new PtpSlave(new PtpSlaveOptions { Port = port, SenderAddresses = [IPAddress.Loopback], StartAnnounces = 0 });
        await newer.StartAsync(CancellationToken.None); // the newest session's slave takes the address (4.3)
        newer.AddSenderAddress(IPAddress.Parse("127.0.0.3"));

        await slave.DisposeAsync();
        Assert.True(port.IsBound);
        Assert.True(port.Unregister(IPAddress.Loopback, newer)); // still the newer slave's: the older one did not remove it
        port.Register(IPAddress.Loopback, newer);
        await newer.DisposeAsync();
        Assert.False(port.Unregister(IPAddress.Loopback, newer));
        Assert.False(port.Unregister(IPAddress.Parse("127.0.0.3"), newer));
        Assert.True(port.IsBound);
    }

    private static async Task<T> ReadEventAsync<T>(PtpMaster master)
        where T : PtpMasterEvent
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var e = await master.Events.ReadAsync(cts.Token);
            if (e is T match)
            {
                return match;
            }
        }
    }
}
