using System.Net;
using System.Net.Sockets;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>
/// <see cref="PtpMaster"/> against the fake receiver's <see cref="FakePtpSlave"/> on loopback (the
/// slave's offset converges, Delay_Req is answered, a silent peer is dropped after 15 s). Each master runs on its own
/// <see cref="PtpPort"/> (SPEC-receiver.md 6.1: a port per test) and each slave on another, both on OS-chosen ports;
/// the master is told the slave's ports with <see cref="PtpMaster.AddPeer(IPAddress, int, int)"/>.
/// </summary>
public class PtpMasterLoopbackTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static PtpMasterOptions LoopbackOptions => new();

    private static PtpPortOptions LoopbackPort => new() { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 };

    private static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Patience);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<(PtpMaster Master, FakePtpSlave Slave)> StartPairAsync(IMasterClock clock, PtpMasterOptions? options = null, FakePtpSlaveOptions? slaveOptions = null)
    {
        var master = new PtpMaster(clock, NullLogger<PtpMaster>.Instance, options ?? LoopbackOptions, LoopbackPort);
        await master.StartAsync(CancellationToken.None);
        var slave = new FakePtpSlave(slaveOptions);
        await slave.StartAsync(CancellationToken.None);
        master.AddPeer(IPAddress.Loopback, slave.EventPort, slave.GeneralPort);
        return (master, slave);
    }

    [Fact]
    public async Task Sends_announce_sync_follow_up_and_signaling_to_the_right_ports()
    {
        var clock = new MasterClock();
        var (master, slave) = await StartPairAsync(clock);
        await using (master)
        await using (slave)
        {
            await WaitUntilAsync(() => slave.Count(PtpMessageType.FollowUp) >= 12 && slave.Count(PtpMessageType.Announce) >= 2 && slave.Count(PtpMessageType.Signaling) >= 2, "a second of PTP traffic");
            var messages = slave.Messages.ToList();

            // Every message carries the master's identity and port number 0x8005, transportSpecific 1, domain 0.
            Assert.All(messages, m =>
            {
                Assert.Equal(new PtpPortIdentity(master.ClockId, PtpPortIdentity.ApplePortNumber), m.Header.SourcePortIdentity);
                Assert.Equal(1, m.Header.TransportSpecific);
                Assert.Equal(0, m.Header.DomainNumber);
                Assert.Equal(m.Bytes.Length, m.Header.MessageLength);
            });

            // Sync on the event port, everything else on the general port, from the master's matching sockets.
            Assert.All(messages.Where(m => m.Header.MessageType == PtpMessageType.Sync), m =>
            {
                Assert.Equal(slave.EventPort, m.LocalPort);
                Assert.Equal(master.EventPort, m.Source.Port);
                Assert.Equal(PtpFlags.AppleTwoStep, m.Header.Flags);
                Assert.Equal(-3, m.Header.LogMessageInterval);
            });
            Assert.All(messages.Where(m => m.Header.MessageType != PtpMessageType.Sync), m =>
            {
                Assert.Equal(slave.GeneralPort, m.LocalPort);
                Assert.Equal(master.GeneralPort, m.Source.Port);
                Assert.Equal(PtpFlags.AppleGeneral, m.Header.Flags);
            });

            // The first message to a new peer is an Announce (sent on the first tick after AddPeer). The slave's two sockets
            // enqueue independently, so check the order per socket: the general port's first message is the Announce (ahead
            // of the first Follow_Up), and the first Sync seen is that same first tick's (sequence 0).
            Assert.Equal(PtpMessageType.Announce, messages.First(m => m.LocalPort == slave.GeneralPort).Header.MessageType);
            Assert.Equal(0, messages.First(m => m.Header.MessageType == PtpMessageType.Sync).Header.SequenceId);
            Assert.Equal(master.ClockId, slave.MasterClockId);

            // Follow_Ups pair with Syncs, sequence ids and t1 increase, the cadence is about 125 ms. Loopback UDP may drop a
            // Sync/Follow_Up pair while the whole suite runs, so the sequence only has to increase (not by exactly one) and
            // the cadence is measured per sequence step rather than per received sample.
            var samples = slave.Samples.ToList();
            Assert.True(samples.Count >= 10);
            for (var i = 1; i < samples.Count; i++)
            {
                Assert.True(samples[i].Sequence > samples[i - 1].Sequence, $"Sync sequence {samples[i].Sequence} after {samples[i - 1].Sequence}");
                Assert.True(samples[i].T1 > samples[i - 1].T1);
            }

            var steps = samples[^1].Sequence - samples[0].Sequence;
            Assert.True(steps <= (samples.Count - 1) * 2, $"{steps} sequence steps for {samples.Count} samples: too many Syncs lost");
            var meanInterval = (samples[^1].T1 - samples[0].T1) / (double)steps / 1_000_000.0;
            Assert.InRange(meanInterval, 100.0, 170.0);
            var announces = messages.Where(m => m.Header.MessageType == PtpMessageType.Announce).ToList();
            Assert.Equal((ushort)(announces[0].Header.SequenceId + 1), announces[1].Header.SequenceId);
            var stats = master.GetStatistics();
            Assert.True(stats.SyncsSent >= 12 && stats.FollowUpsSent >= 12 && stats.AnnouncesSent >= 2 && stats.SignalingsSent >= 2);
            Assert.Equal(0, stats.SendErrors);
        }
    }

    [Fact]
    public async Task Slave_offset_converges_and_delay_requests_are_answered()
    {
        var clock = new MasterClock();
        const long trueOffset = 5_000_000_000L; // the slave's clock runs 5 s ahead of the master's
        var (master, slave) = await StartPairAsync(clock, slaveOptions: new FakePtpSlaveOptions
        {
            LocalClock = () => clock.PtpNowNanos + (ulong)trueOffset,
            SendDelayRequests = true,
            DelayRequestInterval = TimeSpan.FromMilliseconds(200),
        });
        await using (master)
        await using (slave)
        {
            await WaitUntilAsync(() => slave.DelayResponses >= 4 && slave.Samples.Count >= 16, "delay responses and samples");
            Assert.Equal(0, slave.MismatchedDelayResponses);
            Assert.True(master.GetStatistics().DelayResponsesSent >= 4);
            Assert.InRange(slave.MeanPathDelayNanos, 0, 5_000_000);

            // Offset estimates of the last samples (with the measured path delay): median within 2 ms of the truth.
            var recent = slave.Samples.TakeLast(8).Select(s => s.OffsetNanos).Order().ToList();
            var median = recent[recent.Count / 2];
            Assert.InRange(median, trueOffset - 2_000_000, trueOffset + 2_000_000);

            // Delay_Resp went to the slave's general port and echoed its identity.
            var response = slave.Messages.First(m => m.Header.MessageType == PtpMessageType.DelayResp);
            Assert.Equal(slave.GeneralPort, response.LocalPort);
            Assert.Equal(PtpFlags.AppleTwoStep, response.Header.Flags);
            Assert.True(PtpMessages.TryReadRequestingPortIdentity(response.Bytes, out var requester));
            Assert.Equal(new PtpPortIdentity(slave.ClockId, 1), requester);

            // The slave announced itself through its Delay_Req: the master knows its port identity.
            var peer = Assert.Single(master.Peers);
            Assert.Equal(new PtpPortIdentity(slave.ClockId, 1), peer.PortIdentity);
            Assert.True(peer.MessagesReceived >= 4);
        }
    }

    [Fact]
    public async Task With_drop_silent_peers_a_silent_peer_is_dropped_and_an_active_one_is_kept()
    {
        var clock = new MasterClock();
        var options = LoopbackOptions with { PeerTimeout = TimeSpan.FromMilliseconds(1200), DropSilentPeers = true };
        var (master, silent) = await StartPairAsync(clock, options);
        await using (master)
        await using (silent)
        await using (var active = new FakePtpSlave(new FakePtpSlaveOptions { BindAddress = IPAddress.Parse("127.0.0.2"), SendDelayRequests = true, DelayRequestInterval = TimeSpan.FromMilliseconds(150) }))
        {
            if (!await TryStartAsync(active))
            {
                return; // 127.0.0.2 is not routable on this host (macOS); the silent half below still ran in other tests
            }

            master.AddPeer(IPAddress.Parse("127.0.0.2"), active.EventPort, active.GeneralPort);
            await WaitUntilAsync(() => master.Peers.Count == 1, "the silent peer to be dropped", TimeSpan.FromSeconds(5));
            Assert.Equal(IPAddress.Parse("127.0.0.2"), master.Peers[0].Address);
            var removed = await ReadEventAsync<PtpMasterEvent.PeerRemoved>(master);
            Assert.Equal(IPAddress.Loopback, removed.Address);
            Assert.Equal(PtpPeerRemovalReason.TimedOut, removed.Reason);
            Assert.Equal(1, master.GetStatistics().PeersTimedOut);

            // No more Syncs to the dropped peer; the active one keeps receiving and stays.
            var syncs = silent.Count(PtpMessageType.Sync);
            var activeSyncs = active.Count(PtpMessageType.Sync);
            await Task.Delay(1500);
            Assert.Equal(syncs, silent.Count(PtpMessageType.Sync));
            Assert.True(active.Count(PtpMessageType.Sync) > activeSyncs + 5);
            Assert.Single(master.Peers);
        }
    }

    [Fact]
    public async Task Peer_timeout_must_be_positive_or_infinite()
    {
        var clock = new MasterClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PtpMaster(clock, options: LoopbackOptions with { PeerTimeout = TimeSpan.Zero }, portOptions: LoopbackPort));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PtpMaster(clock, options: LoopbackOptions with { PeerTimeout = TimeSpan.FromSeconds(-2) }, portOptions: LoopbackPort));
        await using var master = new PtpMaster(clock, options: LoopbackOptions with { PeerTimeout = Timeout.InfiniteTimeSpan }, portOptions: LoopbackPort);
        Assert.Equal(Timeout.InfiniteTimeSpan, master.Options.PeerTimeout);
    }

    [Fact]
    public async Task Drop_silent_peers_drops_after_15_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), new PtpMasterOptions().PeerTimeout);
        var clock = new SkewableClock();
        var (master, slave) = await StartPairAsync(clock, LoopbackOptions with { DropSilentPeers = true });
        await using (master)
        await using (slave)
        {
            await WaitUntilAsync(() => slave.Count(PtpMessageType.Sync) >= 2, "Syncs");
            clock.Skew = TimeSpan.FromSeconds(14);
            await Task.Delay(400);
            Assert.Single(master.Peers);
            clock.Skew = TimeSpan.FromSeconds(15.2);
            await WaitUntilAsync(() => master.Peers.Count == 0, "the peer to be dropped at 15 s", TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// The default (nqptp sends nothing after its start-up Announces, SPEC-audio.md 4.4 note of 2026-10-09): a peer silent
    /// for 15 s is reported once and keeps getting Syncs; when it sends again the report re-arms.
    /// </summary>
    [Fact]
    public async Task By_default_a_silent_peer_is_reported_after_15_seconds_and_kept()
    {
        Assert.False(new PtpMasterOptions().DropSilentPeers);
        var clock = new SkewableClock();
        var (master, slave) = await StartPairAsync(clock);
        await using (master)
        await using (slave)
        {
            await WaitUntilAsync(() => slave.Count(PtpMessageType.Sync) >= 2, "Syncs");
            clock.Skew = TimeSpan.FromSeconds(15.2);
            var silent = await ReadEventAsync<PtpMasterEvent.PeerSilent>(master);
            Assert.Equal(IPAddress.Loopback, silent.Address);
            var syncs = slave.Count(PtpMessageType.Sync);
            await WaitUntilAsync(() => slave.Count(PtpMessageType.Sync) > syncs + 4, "Syncs after the silence report");
            Assert.Single(master.Peers);
            Assert.Equal(1, master.GetStatistics().SilentPeerReports);
            Assert.Equal(0, master.GetStatistics().PeersTimedOut);

            // The peer speaks (a Delay_Req), is live again, and is reported again after another 15 s of silence.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var request = new byte[PtpMessages.DelayReqSize];
            PtpMessages.WriteDelayReq(request, slave.ClockId, 1, 1, PtpTimestamp.Zero);
            await probe.SendToAsync(request, new IPEndPoint(IPAddress.Loopback, master.EventPort));
            await WaitUntilAsync(() => master.Peers[0].SinceLastSeen < TimeSpan.FromSeconds(1), "the peer to be seen again");
            clock.Skew = TimeSpan.FromSeconds(31);
            await ReadEventAsync<PtpMasterEvent.PeerSilent>(master);
            Assert.Equal(2, master.GetStatistics().SilentPeerReports);
        }
    }

    [Fact]
    public async Task Remove_and_re_add_a_peer()
    {
        var (master, slave) = await StartPairAsync(new MasterClock());
        await using (master)
        await using (slave)
        {
            await WaitUntilAsync(() => slave.Count(PtpMessageType.Sync) >= 2, "Syncs");
            master.RemovePeer(IPAddress.Loopback);
            Assert.Empty(master.Peers);
            await Task.Delay(300);
            var count = slave.Count(PtpMessageType.Sync);
            await Task.Delay(400);
            Assert.Equal(count, slave.Count(PtpMessageType.Sync));

            // IPv4-mapped addresses are the same peer.
            master.AddPeer(IPAddress.Loopback.MapToIPv6(), slave.EventPort, slave.GeneralPort);
            Assert.Equal(IPAddress.Loopback, Assert.Single(master.Peers).Address);
            await WaitUntilAsync(() => slave.Count(PtpMessageType.Sync) > count + 2, "Syncs after re-adding");
            Assert.IsType<PtpMasterEvent.PeerAdded>(await ReadEventAsync<PtpMasterEvent.PeerAdded>(master));
        }
    }

    [Fact]
    public async Task Answers_pdelay_requests_ignores_garbage_and_reports_foreign_announces()
    {
        var (master, slave) = await StartPairAsync(new MasterClock());
        await using (master)
        await using (slave)
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var masterEvent = new IPEndPoint(IPAddress.Loopback, master.EventPort);
            var masterGeneral = new IPEndPoint(IPAddress.Loopback, master.GeneralPort);

            // Pdelay_Req from the slave's address (the probe socket shares 127.0.0.1): Pdelay_Resp to its event port, follow-up to its general port.
            var pdelay = new byte[PtpMessages.PdelayReqSize];
            PtpMessages.WritePdelayReq(pdelay, 0x1111_2222_3333_4444UL, 1, 0x0033, PtpTimestamp.Zero);
            await probe.SendToAsync(pdelay, masterEvent);
            await WaitUntilAsync(() => slave.Count(PtpMessageType.PdelayResp) == 1 && slave.Count(PtpMessageType.PdelayRespFollowUp) == 1, "Pdelay answers");
            var resp = slave.Messages.First(m => m.Header.MessageType == PtpMessageType.PdelayResp);
            Assert.Equal(slave.EventPort, resp.LocalPort);
            Assert.Equal(0x0033, resp.Header.SequenceId);

            // Garbage is counted and dropped by the port (SPEC-receiver.md 6.1), our own clock's messages are counted as ignored.
            await probe.SendToAsync(new byte[] { 1, 2, 3 }, masterGeneral);
            var own = new byte[PtpMessages.SyncSize];
            PtpMessages.WriteSync(own, master.ClockId, 1);
            await probe.SendToAsync(own, masterEvent);
            var port = Assert.IsType<PtpPort>(master.Port);
            await WaitUntilAsync(() => master.GetStatistics().MessagesIgnored >= 1 && port.Statistics.NotPtp >= 1, "ignored datagrams");

            // Another clock's Announce: reported once, ignored otherwise.
            var announce = new byte[PtpMessages.AnnounceSize];
            PtpMessages.WriteAnnounce(announce, 0xAAAA_BBBB_CCCC_DDDDUL, 1);
            await probe.SendToAsync(announce, masterGeneral);
            await probe.SendToAsync(announce, masterGeneral);
            var foreign = await ReadEventAsync<PtpMasterEvent.ForeignAnnounce>(master);
            Assert.Equal(0xAAAA_BBBB_CCCC_DDDDUL, foreign.GrandmasterId);
            await Task.Delay(200);
            Assert.False(master.Events.TryPeek(out var next) && next is PtpMasterEvent.ForeignAnnounce);

            // A Delay_Req from another port of the peer's address is answered at the peer's general port.
            var request = new byte[PtpMessages.DelayReqSize];
            PtpMessages.WriteDelayReq(request, 0x1111_2222_3333_4444UL, 1, 9, PtpTimestamp.Zero);
            await probe.SendToAsync(request, masterEvent);
            await WaitUntilAsync(() => master.GetStatistics().DelayResponsesSent >= 1, "a Delay_Resp");
        }
    }

    [Fact]
    public async Task Port_conflict_raises_a_ptp_master_exception_and_start_is_idempotent()
    {
        await using var first = new PtpMaster(new MasterClock(), null, LoopbackOptions, LoopbackPort);
        await first.StartAsync(CancellationToken.None);
        await first.StartAsync(CancellationToken.None);
        await using var second = new PtpMaster(new MasterClock(), null, LoopbackOptions, LoopbackPort with { EventPort = first.EventPort });
        var ex = await Assert.ThrowsAsync<PtpMasterException>(() => second.StartAsync(CancellationToken.None));
        Assert.Equal(first.EventPort, ex.Port);
        Assert.Equal(first.EventPort, Assert.IsType<PtpPortException>(ex.InnerException).Port); // SPEC-receiver.md 6.1: wraps the port's failure
    }

    [Fact]
    public async Task Peers_added_before_start_receive_traffic_and_dispose_completes_events()
    {
        var master = new PtpMaster(new MasterClock(), null, LoopbackOptions with { ClockIdSeed = 0x0000_0102_0304_0506UL }, LoopbackPort);
        Assert.Equal(0xFFFF_0102_0304_0506UL, master.ClockId);
        await using var slave = new FakePtpSlave();
        await slave.StartAsync(CancellationToken.None);
        master.AddPeer(IPAddress.Loopback, slave.EventPort, slave.GeneralPort);
        await master.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => slave.Count(PtpMessageType.FollowUp) >= 1, "a Follow_Up");
        await master.DisposeAsync();
        await master.DisposeAsync();
        using (var cts = new CancellationTokenSource(Patience))
        {
            await foreach (var _ in master.Events.ReadAllAsync(cts.Token))
            {
            }
        }

        Assert.True(master.Events.Completion.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => master.AddPeer(IPAddress.Loopback));
    }

    private static async Task<T> ReadEventAsync<T>(PtpMaster master)
        where T : PtpMasterEvent
    {
        using var cts = new CancellationTokenSource(Patience);
        while (true)
        {
            var e = await master.Events.ReadAsync(cts.Token);
            if (e is T match)
            {
                return match;
            }
        }
    }

    private static async Task<bool> TryStartAsync(FakePtpSlave slave)
    {
        try
        {
            await slave.StartAsync(CancellationToken.None);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>A real clock whose monotonic face can be pushed forward (liveness tests without waiting 15 s).</summary>
    private sealed class SkewableClock : IMasterClock
    {
        private readonly MasterClock _inner = new();

        public TimeSpan Skew { get; set; }

        public long NowNanos => _inner.NowNanos + Skew.Ticks * 100;

        public ulong NtpNow => _inner.NtpNow;

        public ulong PtpNowNanos => _inner.PtpNowNanos;
    }
}
