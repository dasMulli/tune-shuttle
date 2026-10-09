using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Fake;
using Xunit;

namespace Airplay2.Sender.Tests.Playback;

/// <summary>
/// <see cref="RealtimeStreamTransport"/> against loopback sockets standing in for receivers (SPEC-audio.md 1.2-1.3, 2.3,
/// 2.4, 3.1-3.3, 4.4; SPEC.md D3, D8, D11): per-receiver keys, initial 0x90 sync + marker, periodic cadence, offset_frames,
/// resync, history, both sync flavours.
/// </summary>
public sealed class RealtimeStreamTransportTests
{
    private const int L = RealtimeStreamConstants.DefaultLatencyFrames;
    private const long PacketNanos = 352L * 1_000_000_000 / 44100;

    [Fact]
    public async Task Each_receiver_gets_the_same_packets_under_its_own_key_with_0x90_sync_and_marker_first()
    {
        await using var setup = await Setup.StartAsync(ptp: null);
        using var a = new LoopbackReceiverSockets();
        using var b = new LoopbackReceiverSockets();
        setup.Transport.AddReceiver("a", a.Endpoint());
        setup.Transport.AddReceiver("b", b.Endpoint(offset: 1000));
        Assert.Equal(2, setup.Control.RegisteredCount);

        var frames = FakeReceiverHarness.AlacVectors().Select(v => v.Alac).ToArray();
        const ushort firstSeq = 65530;
        const uint firstTs = 4_000_000_000;
        var baseUnix = 1_700_000_000_000_000_000L;
        for (var k = 0; k < 130; k++)
        {
            setup.Transport.Send(unchecked((ushort)(firstSeq + k)), unchecked(firstTs + (uint)(352 * k)), frames[k % frames.Length], NtpTimestamp.FromUnixNanos(baseUnix + (k * PacketNanos)));
        }

        foreach (var (receiver, offset) in new[] { (a, 0), (b, 1000) })
        {
            var syncs = await LoopbackReceiverSockets.DrainAsync(receiver.Control, TimeSpan.FromMilliseconds(300));
            Assert.Equal(2, syncs.Count); // the initial one and one periodic one 126 packets (44 352 frames) later
            Assert.True(SyncPacket.TryParse(syncs[0], out var first));
            Assert.True(first.First);
            Assert.Equal(0x90, syncs[0][0]);
            Assert.Equal(0x0007, BinaryPrimitives.ReadUInt16BigEndian(syncs[0].AsSpan(2)));
            Assert.Equal(firstTs, first.NextFrame);
            Assert.Equal(unchecked(firstTs - (uint)L - (uint)offset), first.NowFrame);
            Assert.Equal(NtpTimestamp.FromUnixNanos(baseUnix), first.NtpTime);

            Assert.True(SyncPacket.TryParse(syncs[1], out var periodic));
            Assert.False(periodic.First);
            Assert.Equal(unchecked(firstTs + (126u * 352u)), periodic.NextFrame);
            Assert.Equal(unchecked(periodic.NextFrame - (uint)L - (uint)offset), periodic.NowFrame);

            // now_frame and T advance in lock-step (SPEC-audio.md 2.3).
            var seconds = (NtpTimestamp.ToUnixNanos(periodic.NtpTime) - NtpTimestamp.ToUnixNanos(first.NtpTime)) / 1e9;
            Assert.Equal(unchecked(periodic.NowFrame - first.NowFrame), seconds * 44100, 0.5);

            var packets = await LoopbackReceiverSockets.DrainAsync(receiver.Data, TimeSpan.FromMilliseconds(300));
            Assert.Equal(130, packets.Count);
            var plain = new byte[2048];
            for (var k = 0; k < packets.Count; k++)
            {
                Assert.Equal(unchecked((ushort)(firstSeq + k)), RtpAudioPacketBuilder.ReadSequence(packets[k]));
                Assert.Equal(unchecked(firstTs + (uint)(352 * k)), RtpAudioPacketBuilder.ReadTimestamp(packets[k]));
                Assert.Equal(k == 0, RtpAudioPacketBuilder.ReadMarker(packets[k]));
                Assert.True(RtpAudioPacketBuilder.TryDecrypt(receiver.Key, packets[k], plain, out var n));
                Assert.Equal(frames[k % frames.Length], plain[..n]);
            }

            // The nonce counter starts at 0 for each receiver (SPEC.md D9).
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(packets[0].AsSpan(packets[0].Length - 8)));
        }
    }

    [Fact]
    public async Task Resync_withhold_retransmit_send_sync_flags_and_remove()
    {
        await using var setup = await Setup.StartAsync(ptp: null);
        using var a = new LoopbackReceiverSockets();
        using var b = new LoopbackReceiverSockets();
        setup.Transport.AddReceiver("a", a.Endpoint());
        setup.Transport.AddReceiver("b", b.Endpoint());
        var frame = FakeReceiverHarness.AlacVectors()[2].Alac;
        ushort seq = 100;
        uint ts = 0;
        void Send(int n)
        {
            for (var i = 0; i < n; i++)
            {
                setup.Transport.Send(seq++, ts, frame, 0x83AA7E80_00000000UL + ((ulong)ts << 16));
                ts += 352;
            }
        }

        Send(3);
        await LoopbackReceiverSockets.DrainAsync(a.Control, TimeSpan.FromMilliseconds(100));
        await LoopbackReceiverSockets.DrainAsync(b.Control, TimeSpan.FromMilliseconds(100));
        await LoopbackReceiverSockets.DrainAsync(a.Data, TimeSpan.FromMilliseconds(100));
        await LoopbackReceiverSockets.DrainAsync(b.Data, TimeSpan.FromMilliseconds(100));

        // Resync (after a FLUSH): only a gets 0x90 + marker again; the history is kept.
        setup.Transport.Resync("a");
        Assert.False(setup.Transport.ReceiverStates["a"]);
        Send(1);
        var syncA = await LoopbackReceiverSockets.ReceiveAsync(a.Control);
        Assert.NotNull(syncA);
        Assert.Equal(0x90, syncA![0]);
        Assert.Equal(ts - 352, BinaryPrimitives.ReadUInt32BigEndian(syncA.AsSpan(16)));
        Assert.Null(await LoopbackReceiverSockets.ReceiveAsync(b.Control, TimeSpan.FromMilliseconds(150)));
        Assert.True(RtpAudioPacketBuilder.ReadMarker((await LoopbackReceiverSockets.ReceiveAsync(a.Data))!));
        Assert.False(RtpAudioPacketBuilder.ReadMarker((await LoopbackReceiverSockets.ReceiveAsync(b.Data))!));

        // Withholding (probe P14): every 3rd packet is stored but not sent; the control port answers 0xD5 from the history.
        setup.Transport.WithholdEveryNthPacket = 3;
        var before = seq;
        Send(6);
        setup.Transport.WithholdEveryNthPacket = 0;
        var gotA = await LoopbackReceiverSockets.DrainAsync(a.Data, TimeSpan.FromMilliseconds(150));
        Assert.Equal(4, gotA.Count);
        var missing = Enumerable.Range(before, 6).Select(i => (ushort)i).Except(gotA.Select(p => RtpAudioPacketBuilder.ReadSequence(p))).ToArray();
        Assert.Equal(2, missing.Length);
        var request = new byte[8];
        new RetransmitRequest(missing[0], 1).Write(request);
        await a.Control.SendToAsync(request, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, setup.Control.Port));
        var reply = await LoopbackReceiverSockets.ReceiveAsync(a.Control);
        Assert.NotNull(reply);
        Assert.True(RetransmitReply.TryUnwrap(reply, out var original));
        Assert.Equal(missing[0], RtpAudioPacketBuilder.ReadSequence(original));
        Assert.True(RtpAudioPacketBuilder.TryDecrypt(a.Key, original, new byte[2048], out _));
        Assert.Equal(4, setup.Transport.PacketsWithheld); // 2 per receiver

        // SendSync with first: both get 0x90 with the given next frame; flags override (probe P7).
        setup.Transport.SyncFlagsOverride = 4;
        setup.Transport.SendSync(777_000, 0x83AA7E80_00000000UL, first: true);
        foreach (var r in new[] { a, b })
        {
            var sync = await LoopbackReceiverSockets.ReceiveAsync(r.Control);
            Assert.NotNull(sync);
            Assert.Equal(0x90, sync![0]);
            Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(sync.AsSpan(2)));
            Assert.Equal(777_000u, BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(16)));
            Assert.Equal(777_000u - (uint)L, BinaryPrimitives.ReadUInt32BigEndian(sync.AsSpan(4)));
        }

        // Suspended periodic syncs (probe P18): nothing on the control port even across the 1 s cadence.
        setup.Transport.PeriodicSyncsSuspended = true;
        Send(130);
        Assert.Null(await LoopbackReceiverSockets.ReceiveAsync(a.Control, TimeSpan.FromMilliseconds(150)));
        setup.Transport.PeriodicSyncsSuspended = false;

        setup.Transport.RemoveReceiver("b");
        Assert.Equal(1, setup.Control.RegisteredCount);
        await LoopbackReceiverSockets.DrainAsync(b.Data, TimeSpan.FromMilliseconds(100));
        Send(1);
        Assert.Null(await LoopbackReceiverSockets.ReceiveAsync(b.Data, TimeSpan.FromMilliseconds(150)));
        Assert.False(setup.Transport.ReceiverStates.ContainsKey("b"));
    }

    [Fact]
    public async Task Under_ptp_the_syncs_are_0xD7_with_ptp_time_and_clock_id()
    {
        var ptp = new StubPtpMaster();
        await using var setup = await Setup.StartAsync(ptp);
        Assert.Equal(TimingProtocol.Ptp, setup.Transport.TimingProtocol);
        Assert.Equal(0, setup.Transport.TimingPort);
        Assert.Equal(1, ptp.Starts);
        Assert.Equal(0u, setup.Transport.Ssrc);
        using var a = new LoopbackReceiverSockets();
        setup.Transport.AddReceiver("a", a.Endpoint(offset: 352));
        setup.Transport.AddTimingPeer(IPAddress.Loopback);
        setup.Transport.AddTimingPeer(IPAddress.Loopback);
        setup.Transport.RemoveTimingPeer(IPAddress.Loopback);
        Assert.Equal(new[] { "add 127.0.0.1" }, ptp.Calls);

        var frame = FakeReceiverHarness.AlacVectors()[0].Alac;
        var unix = setup.Clock.UnixNanos;
        for (var k = 0; k < 127; k++)
        {
            setup.Transport.Send((ushort)k, (uint)(1000 + (352 * k)), frame, NtpTimestamp.FromUnixNanos(unix + (k * PacketNanos)));
        }

        var syncs = await LoopbackReceiverSockets.DrainAsync(a.Control, TimeSpan.FromMilliseconds(300));
        Assert.Equal(2, syncs.Count);
        Assert.True(PtpSyncPacket.TryParse(syncs[0], out var first));
        Assert.True(PtpSyncPacket.TryParse(syncs[1], out var periodic));
        Assert.Equal(PtpSyncPacket.Size, syncs[0].Length);
        Assert.True(first.First);
        Assert.False(periodic.First);
        Assert.Equal(ptp.ClockId, first.ClockId);
        Assert.Equal(1000u, first.NextFrame);
        Assert.Equal(unchecked(1000u - (uint)L - 352u), first.NowFrame);

        // The PTP time is the master clock's PTP face of the same instant (MasterClock: PTP nanos = its Unix nanos).
        Assert.InRange((long)first.PtpTimeNanos - unix, -20_000, 20_000);
        Assert.Equal(126 * PacketNanos, (long)(periodic.PtpTimeNanos - first.PtpTimeNanos), 2.0);
        Assert.Equal(126u * 352u, periodic.NowFrame - first.NowFrame);

        await setup.Transport.DisposeAsync();
        Assert.Contains("remove 127.0.0.1", ptp.Calls);
    }

    [Fact]
    public async Task Send_before_start_and_bad_keys_are_rejected()
    {
        var clock = new MasterClock();
        await using var timing = new TimingResponder(clock, bindAddress: IPAddress.Loopback);
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await using var transport = new RealtimeStreamTransport(clock, timing, control);
        using var a = new LoopbackReceiverSockets();
        Assert.Throws<InvalidOperationException>(() => transport.AddReceiver("a", a.Endpoint()));
        Assert.Throws<InvalidOperationException>(() => transport.Send(0, 0, new byte[32], 0));
        await transport.StartAsync(CancellationToken.None);
        await transport.StartAsync(CancellationToken.None); // idempotent
        Assert.Equal(TimingProtocol.Ntp, transport.TimingProtocol);
        Assert.Equal(timing.Port, transport.TimingPort);
        Assert.Equal(control.Port, transport.ControlPort);
        Assert.Throws<ArgumentException>(() => transport.AddReceiver("a", a.Endpoint() with { StreamKey = new byte[16] }));
        Assert.Throws<ArgumentException>(() => transport.Send(0, 0, ReadOnlySpan<byte>.Empty, 0));
        transport.RemoveReceiver("unknown");
        transport.Resync("unknown");
    }

    private sealed class Setup : IAsyncDisposable
    {
        private Setup(MasterClock clock, TimingResponder timing, ControlPort control, RealtimeStreamTransport transport)
        {
            Clock = clock;
            Timing = timing;
            Control = control;
            Transport = transport;
        }

        public MasterClock Clock { get; }

        public TimingResponder Timing { get; }

        public ControlPort Control { get; }

        public RealtimeStreamTransport Transport { get; }

        public static async Task<Setup> StartAsync(IPtpMaster? ptp)
        {
            var clock = new MasterClock();
            var timing = new TimingResponder(clock, bindAddress: IPAddress.Loopback);
            var control = new ControlPort(bindAddress: IPAddress.Loopback);
            var transport = new RealtimeStreamTransport(clock, timing, control, L, ptp);
            await transport.StartAsync(CancellationToken.None);
            return new Setup(clock, timing, control, transport);
        }

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            await Control.DisposeAsync();
            await Timing.DisposeAsync();
        }
    }
}
