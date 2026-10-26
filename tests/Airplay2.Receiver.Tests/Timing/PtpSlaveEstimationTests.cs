using System.Net;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Streams;
using Xunit;

namespace Airplay2.Receiver.Tests.Timing;

/// <summary>
/// <see cref="PtpSlave"/> offset estimation (SPEC-receiver.md 6.2, 11.1): synthetic Sync/Follow_Up pairs with a known
/// offset and known network delays are fed straight into the slave as its port would route them; the estimate is the
/// minimum over the last 8 pairs, starts again when the grandmaster changes, and <see cref="PtpSlave.TryMapToLocal"/>
/// applies it.
/// </summary>
public class PtpSlaveEstimationTests
{
    private const ulong MasterA = 0xFFFF_0000_0000_0A0AUL;
    private const ulong MasterB = 0xFFFF_0000_0000_0B0BUL;
    private const long TrueOffset = 3_000_000_000L; // the slave's clock runs 3 s ahead of the sender's
    private const long Tolerance = 0; // the test clock is exact and no port lag is subtracted (see Feed.DeliverAsync)

    private static readonly IPEndPoint Sender = new(IPAddress.Parse("192.0.2.10"), PtpMessages.EventPort);

    [Fact]
    public async Task No_estimate_before_the_first_pair()
    {
        var feed = new Feed();
        await using var slave = feed.Slave;

        Assert.False(slave.TryMapToLocal(1_000, out _));
        Assert.Null(slave.OffsetNanos);
        Assert.Equal(new PtpSlaveStatistics(null, 0, null, 0), slave.Statistics);

        // A Sync without its Follow_Up is not a pair.
        await feed.SyncAsync(MasterA, 1, 10_000_000_000UL, delay: 1_000_000);
        Assert.False(slave.TryMapToLocal(1_000, out _));
        Assert.Equal(0, slave.Statistics.PairsSeen);
        Assert.NotNull(slave.Statistics.LastSyncAge);
    }

    [Fact]
    public async Task Estimate_is_the_minimum_over_a_sliding_window_of_eight_pairs()
    {
        var feed = new Feed();
        await using var slave = feed.Slave;
        long[] delays = [3_000_000, 1_000_000, 5_000_000, 200_000, 2_000_000, 4_000_000, 6_000_000, 7_000_000];
        ushort sequence = 0;
        foreach (var delay in delays)
        {
            await feed.PairAsync(MasterA, sequence++, delay);
        }

        Assert.InRange(slave.OffsetNanos!.Value, TrueOffset + 200_000 - Tolerance, TrueOffset + 200_000);
        Assert.Equal(8, slave.Statistics.PairsSeen);
        Assert.Equal(slave.OffsetNanos, slave.Statistics.OffsetNanos);
        Assert.Equal(MasterA, slave.Statistics.MasterClockId);

        // Four more pairs push the 3, 1, 5 and 0.2 ms samples out: the window minimum is now the 2 ms one.
        foreach (var delay in new long[] { 2_500_000, 3_500_000, 4_500_000, 5_500_000 })
        {
            await feed.PairAsync(MasterA, sequence++, delay);
        }

        Assert.InRange(slave.OffsetNanos!.Value, TrueOffset + 2_000_000 - Tolerance, TrueOffset + 2_000_000);
        Assert.Equal(12, slave.Statistics.PairsSeen);

        // TryMapToLocal: sender time plus the estimate.
        const ulong senderNs = 50_000_000_000UL;
        Assert.True(slave.TryMapToLocal(senderNs, out var local));
        Assert.Equal(unchecked(senderNs + (ulong)slave.OffsetNanos!.Value), local);
    }

    [Fact]
    public async Task A_follow_up_processed_before_its_sync_still_pairs()
    {
        var feed = new Feed();
        await using var slave = feed.Slave;

        await feed.PairAsync(MasterA, 7, 1_000_000, followUpFirst: true);

        Assert.Equal(1, slave.Statistics.PairsSeen);
        Assert.InRange(slave.OffsetNanos!.Value, TrueOffset + 1_000_000 - Tolerance, TrueOffset + 1_000_000);
    }

    [Fact]
    public async Task A_grandmaster_change_restarts_the_estimate()
    {
        var feed = new Feed();
        await using var slave = feed.Slave;
        for (ushort s = 0; s < 8; s++)
        {
            await feed.PairAsync(MasterA, s, 500_000);
        }

        Assert.InRange(slave.OffsetNanos!.Value, TrueOffset + 500_000 - Tolerance, TrueOffset + 500_000);

        // The sender switches grandmaster: the new timeline is 2 s behind the old one, so the old window minimum would
        // otherwise hide the new offset for 8 pairs.
        const long shift = 2_000_000_000L;
        await feed.PairAsync(MasterB, 0, 4_000_000, masterShift: -shift);

        Assert.Equal(1, slave.Rebases);
        Assert.Equal(MasterB, slave.MasterClockId);
        var expected = TrueOffset + shift + 4_000_000;
        Assert.InRange(slave.OffsetNanos!.Value, expected - Tolerance, expected);
        Assert.True(slave.TryMapToLocal(10_000_000_000UL, out var local));
        Assert.InRange(unchecked((long)(local - 10_000_000_000UL)), expected - Tolerance, expected);
    }

    [Fact]
    public async Task A_sync_and_a_follow_up_of_different_clocks_do_not_pair()
    {
        var feed = new Feed();
        await using var slave = feed.Slave;

        await feed.SyncAsync(MasterA, 3, 10_000_000_000UL, delay: 1_000_000);
        await feed.FollowUpAsync(MasterB, 3, 10_000_000_000UL);

        Assert.Equal(0, slave.Statistics.PairsSeen);
        Assert.False(slave.TryMapToLocal(0, out _));
    }

    [Fact]
    public async Task Announces_from_the_sender_are_recorded()
    {
        var feed = new Feed();
        await using var slave = feed.Slave;
        var announce = new byte[PtpMessages.AnnounceSize];
        PtpMessages.WriteAnnounce(announce, MasterA, 1);

        await feed.DeliverAsync(announce, eventPort: false);

        Assert.Equal(MasterA, slave.AnnouncedGrandmaster);
        Assert.Equal((byte)PtpMessages.DefaultPriority, slave.AnnouncedPriority1);
        Assert.Equal(MasterA, slave.MasterClockId);
    }

    [Fact]
    public void Clock_id_derives_from_the_receiver_device_id_as_eui64()
    {
        Assert.Equal(0x02005EFFFE102030UL, PtpSlave.ClockIdFromDeviceId("02:00:5E:10:20:30"));
        Assert.Throws<FormatException>(() => PtpSlave.ClockIdFromDeviceId("02:00:5E:10:20"));
        Assert.Throws<FormatException>(() => PtpSlave.ClockIdFromDeviceId("02:00:5E:10:20:XY"));

        var identity = ReceiverIdentity.Create();
        var options = new PtpSlaveOptions { ClockId = PtpSlave.ClockIdFromDeviceId(identity.DeviceId) };
        Assert.Equal(0xFFFEUL, (options.ClockId!.Value >> 24) & 0xFFFF);
    }

    [Fact]
    public async Task Without_a_configured_clock_id_the_slave_picks_a_locally_administered_eui64()
    {
        await using var slave = new PtpSlave();
        Assert.Equal(0xFFFEUL, (slave.ClockId >> 24) & 0xFFFF);
        Assert.Equal(0x02UL, (slave.ClockId >> 56) & 0x03);
    }

    /// <summary>Feeds synthetic messages into a slave whose local clock the test sets before each Sync.</summary>
    private sealed class Feed
    {
        private ulong _now = 1_000_000_000UL;
        private ulong _masterNow = 100_000_000_000UL;

        public Feed()
        {
            Slave = new PtpSlave(new PtpSlaveOptions { LocalClock = MonotonicClock.FromDelegate(() => Volatile.Read(ref _now)) });
        }

        public PtpSlave Slave { get; }

        /// <summary>One pair 125 ms after the previous one: the Sync leaves at master time t1 and arrives after <paramref name="delay"/>.</summary>
        public async Task PairAsync(ulong master, ushort sequence, long delay, bool followUpFirst = false, long masterShift = 0)
        {
            _masterNow += 125_000_000UL;
            var t1 = unchecked(_masterNow + (ulong)masterShift);
            if (followUpFirst)
            {
                await FollowUpAsync(master, sequence, t1);
                await SyncAsync(master, sequence, t1, delay, masterShift);
            }
            else
            {
                await SyncAsync(master, sequence, t1, delay, masterShift);
                await FollowUpAsync(master, sequence, t1);
            }
        }

        public Task SyncAsync(ulong master, ushort sequence, ulong t1, long delay, long masterShift = 0)
        {
            // Local time at arrival: the unshifted sender time plus the true offset plus the delay; a grandmaster whose
            // timeline is shifted by masterShift therefore shows the offset TrueOffset - masterShift.
            Volatile.Write(ref _now, unchecked(t1 - (ulong)masterShift + (ulong)TrueOffset + (ulong)delay));
            var sync = new byte[PtpMessages.SyncSize];
            PtpMessages.WriteSync(sync, master, sequence);
            return DeliverAsync(sync, eventPort: true);
        }

        public Task FollowUpAsync(ulong master, ushort sequence, ulong t1)
        {
            var followUp = new byte[PtpMessages.FollowUpSize];
            PtpMessages.WriteFollowUp(followUp, master, sequence, PtpTimestamp.FromNanoseconds(t1));
            return DeliverAsync(followUp, eventPort: false);
        }

        public async Task DeliverAsync(byte[] datagram, bool eventPort)
        {
            Assert.True(PtpHeader.TryRead(datagram, out var header));
            // An arrival "in the future" of the port clock means no queueing lag to subtract: the test clock is exact.
            await ((IPtpEndpoint)Slave).HandleAsync(header, datagram, Sender, eventPort, ulong.MaxValue, CancellationToken.None);
        }
    }
}
