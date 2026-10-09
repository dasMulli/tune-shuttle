using System.Collections.Concurrent;
using System.Net;
using Airplay2.Sender.Rtp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Airplay2.Sender.Tests.Rtp;

/// <summary>
/// UDP loopback tests of <see cref="TimingResponder"/> and <see cref="ControlPort"/> against <see cref="LoopbackReceiver"/>
/// (WORK-PLAN rtp-timing-control "Verify"; the drop-every-50th test is probe P14 on the fake).
/// </summary>
public class ControlAndTimingLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static byte[] KeyFor(int receiver) => Enumerable.Range(0, 32).Select(i => (byte)(i + 17 * receiver)).ToArray();

    private static byte[] FrameFor(int index) => Enumerable.Range(0, 32 + index % 7).Select(i => (byte)(i * 3 + index)).ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>One receiver's side of the sender: builder + history, as the realtime transport will hold them.</summary>
    private sealed class SenderLeg(byte[] key) : IDisposable
    {
        public RtpAudioPacketBuilder Builder { get; } = new(key, 0x0BADF00D);

        public PacketHistory History { get; } = new();

        public Dictionary<ushort, byte[]> Sent { get; } = [];

        public byte[] Key { get; } = key;

        public byte[] Build(ushort sequence, uint timestamp, bool marker, byte[] frame)
        {
            var buffer = new byte[RealtimeStreamConstants.MaxPacketSize];
            var length = Builder.Build(buffer, sequence, timestamp, marker, frame);
            var packet = buffer[..length];
            History.Store(sequence, packet);
            Sent[sequence] = packet;
            return packet;
        }

        public void Dispose() => Builder.Dispose();
    }

    /// <summary>Streams <paramref name="count"/> packets to <paramref name="receiver"/>, dropping every 50th (index % 50 == 25).</summary>
    private static int Stream(SenderLeg leg, LoopbackReceiver receiver, ushort firstSequence, int count, System.Net.Sockets.Socket dataSocket)
    {
        var dropped = 0;
        for (var i = 0; i < count; i++)
        {
            var sequence = (ushort)(firstSequence + i);
            var packet = leg.Build(sequence, (uint)(1000 + i * RealtimeStreamConstants.FramesPerPacket), i == 0, FrameFor(i));
            if (i % 50 == 25)
            {
                dropped++;
                continue;
            }

            dataSocket.SendTo(packet, receiver.DataEndPoint);
        }

        return dropped;
    }

    private static System.Net.Sockets.Socket NewDataSocket()
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static void AssertRetransmissionsIdentical(SenderLeg leg, LoopbackReceiver receiver, bool wrapped)
    {
        foreach (var (sequence, (packet, isWrapped)) in receiver.Retransmitted)
        {
            Assert.Equal(wrapped, isWrapped);
            Assert.Equal(leg.Sent[sequence], packet);
            var frame = new byte[packet.Length];
            Assert.True(RtpAudioPacketBuilder.TryDecrypt(leg.Key, packet, frame, out _));
        }
    }

    [Fact]
    public async Task Timing_requests_are_answered_with_reference_receive_and_transmit()
    {
        var clock = new MasterClock();
        await using var responder = new TimingResponder(clock, bindAddress: IPAddress.Loopback);
        await responder.StartAsync(CancellationToken.None);
        await responder.StartAsync(CancellationToken.None); // idempotent
        Assert.NotEqual(0, responder.Port);

        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        using var receiver = new LoopbackReceiver(new IPEndPoint(IPAddress.Loopback, control.Port));
        var timingPort = new IPEndPoint(IPAddress.Loopback, responder.Port);

        var before = clock.NtpNow;
        for (ulong i = 1; i <= 3; i++)
        {
            receiver.SendTimingRequest(timingPort, 0xE000000000000000UL + i);
        }

        await WaitUntilAsync(() => receiver.TimingReplies.Count == 3, "three timing replies");
        var after = clock.NtpNow;
        Assert.Equal(3, responder.RepliesSent);

        var origins = new HashSet<ulong>();
        foreach (var datagram in receiver.TimingReplies)
        {
            Assert.Equal(TimingPacket.Size, datagram.Length);
            Assert.Equal(0x80, datagram[0]);
            Assert.Equal(RtpPacketTypes.TimingReply, datagram[1]);
            Assert.True(TimingPacket.TryParse(datagram, out var reply));
            Assert.Equal((ushort)7, reply.Sequence);
            origins.Add(reply.Origin);
            Assert.InRange(reply.Receive, before, after);
            Assert.InRange(reply.Transmit, reply.Receive, after);
        }

        Assert.Equal(new[] { 0xE000000000000001UL, 0xE000000000000002UL, 0xE000000000000003UL }, origins.Order());

        // Not a timing request: ignored, no reply.
        var bogus = new byte[TimingPacket.Size];
        new TimingPacket(RtpPacketTypes.TimingReply, 7, 1, 2, 3).Write(bogus);
        using var raw = NewDataSocket();
        raw.SendTo(bogus, timingPort);
        raw.SendTo(new byte[] { 0x80, 0xD2, 0, 7 }, timingPort);
        await WaitUntilAsync(() => responder.PacketsIgnored == 2, "two ignored datagrams");
        Assert.Equal(3, responder.RepliesSent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_50th_packet_dropped_is_retransmitted_byte_identical_wrapped(bool longRequests)
    {
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        using var receiver = new LoopbackReceiver(new IPEndPoint(IPAddress.Loopback, control.Port), longRequests: longRequests);
        using var leg = new SenderLeg(KeyFor(1));
        control.Register(receiver.ControlEndPoint, leg.History, replyOnDataPort: false, receiver.DataEndPoint);
        using var data = NewDataSocket();

        // Start just below the 16-bit wrap so the history and the lookup cross 65535 -> 0.
        var dropped = Stream(leg, receiver, 65400, 300, data);
        Assert.Equal(6, dropped);

        await WaitUntilAsync(() => receiver.Retransmitted.Count == dropped && receiver.Received.Count == 300 - dropped, "all packets, the dropped ones retransmitted");
        Assert.Equal(300, receiver.Received.Count + receiver.Retransmitted.Count);
        AssertRetransmissionsIdentical(leg, receiver, wrapped: true);
        Assert.Equal(dropped, control.RetransmitRequestsReceived);
        Assert.Equal(dropped, control.PacketsRetransmitted);
        Assert.Equal(0, control.PacketsMissing);
    }

    [Fact]
    public async Task Raw_quirk_sends_the_original_packet_to_the_data_port()
    {
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        using var receiver = new LoopbackReceiver(new IPEndPoint(IPAddress.Loopback, control.Port));
        using var leg = new SenderLeg(KeyFor(2));
        control.Register(receiver.ControlEndPoint, leg.History, replyOnDataPort: true, receiver.DataEndPoint);
        using var data = NewDataSocket();

        var dropped = Stream(leg, receiver, 0, 120, data);
        await WaitUntilAsync(() => receiver.Retransmitted.Count == dropped, "raw retransmissions");
        AssertRetransmissionsIdentical(leg, receiver, wrapped: false);
    }

    [Fact]
    public async Task Request_from_a_non_announced_port_is_still_answered()
    {
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        using var receiver = new LoopbackReceiver(new IPEndPoint(IPAddress.Loopback, control.Port), requestFromOtherPort: true);
        using var leg = new SenderLeg(KeyFor(3));
        control.Register(receiver.ControlEndPoint, leg.History, replyOnDataPort: false, receiver.DataEndPoint);
        Assert.NotEqual(receiver.ControlEndPoint.Port, receiver.RequesterEndPoint!.Port);
        using var data = NewDataSocket();

        var dropped = Stream(leg, receiver, 1000, 110, data);
        await WaitUntilAsync(() => receiver.Retransmitted.Count == dropped, "retransmissions to the requesting socket");
        AssertRetransmissionsIdentical(leg, receiver, wrapped: true);
        Assert.Equal(0, control.UnmatchedRequests);
    }

    [Fact]
    public async Task Two_receivers_on_one_address_are_told_apart_by_port()
    {
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        var senderControl = new IPEndPoint(IPAddress.Loopback, control.Port);
        using var first = new LoopbackReceiver(senderControl);
        using var second = new LoopbackReceiver(senderControl);
        using var legA = new SenderLeg(KeyFor(4));
        using var legB = new SenderLeg(KeyFor(5));
        control.Register(first.ControlEndPoint, legA.History, false, first.DataEndPoint);
        control.Register(second.ControlEndPoint, legB.History, false, second.DataEndPoint);
        Assert.Equal(2, control.RegisteredCount);
        using var data = NewDataSocket();

        // One group timeline: same sequence numbers and frames, per-receiver keys.
        var droppedA = Stream(legA, first, 500, 160, data);
        var droppedB = Stream(legB, second, 500, 160, data);
        await WaitUntilAsync(() => first.Retransmitted.Count == droppedA && second.Retransmitted.Count == droppedB, "both receivers' retransmissions");

        // Each got the packet encrypted under its own key, so each was found by its port.
        AssertRetransmissionsIdentical(legA, first, wrapped: true);
        AssertRetransmissionsIdentical(legB, second, wrapped: true);
        Assert.NotEqual(legA.Sent[525], legB.Sent[525]);
    }

    [Fact]
    public async Task Ambiguous_request_from_an_unannounced_port_on_a_shared_address_is_dropped()
    {
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        var senderControl = new IPEndPoint(IPAddress.Loopback, control.Port);
        using var first = new LoopbackReceiver(senderControl);
        using var other = new LoopbackReceiver(senderControl, requestFromOtherPort: true);
        using var legA = new SenderLeg(KeyFor(6));
        using var legB = new SenderLeg(KeyFor(7));
        control.Register(first.ControlEndPoint, legA.History, false, first.DataEndPoint);
        control.Register(other.ControlEndPoint, legB.History, false, other.DataEndPoint);
        legB.Build(10, 0, false, FrameFor(0));

        other.RequestRetransmit(10, 1);
        await WaitUntilAsync(() => control.UnmatchedRequests == 1, "the unmatched request");
        Assert.Equal(0, control.PacketsRetransmitted);

        // After the other receiver leaves, the address alone identifies the remaining one again.
        control.Unregister(other.ControlEndPoint);
        legA.Build(11, 0, false, FrameFor(1));
        var lone = new LoopbackReceiver(senderControl, requestFromOtherPort: true);
        try
        {
            control.Register(first.ControlEndPoint, legA.History, false, lone.DataEndPoint);
            lone.RequestRetransmit(11, 1);
            await WaitUntilAsync(() => lone.Retransmitted.ContainsKey(11), "the answer after unregistering");
            Assert.Equal(legA.Sent[11], lone.Retransmitted[11].Packet);
        }
        finally
        {
            lone.Dispose();
        }
    }

    [Fact]
    public async Task Requests_for_packets_outside_the_history_are_counted_missing()
    {
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        using var receiver = new LoopbackReceiver(new IPEndPoint(IPAddress.Loopback, control.Port));
        using var leg = new SenderLeg(KeyFor(8));
        control.Register(receiver.ControlEndPoint, leg.History, false, receiver.DataEndPoint);
        for (var i = 0; i < 1200; i++)
        {
            leg.Build((ushort)i, 0, false, FrameFor(i));
        }

        receiver.RequestRetransmit(100, 3); // fell out of the 1000-packet window
        receiver.RequestRetransmit(1199, 2); // 1199 is there, 1200 is not yet sent
        await WaitUntilAsync(() => control.RetransmitRequestsReceived == 2 && control.PacketsMissing == 4, "both requests");
        await WaitUntilAsync(() => receiver.Retransmitted.ContainsKey(1199), "the one available packet");
        Assert.Equal(1, control.PacketsRetransmitted);

        // TEARDOWN clears the history (FLUSH does not; that is the caller's choice, not the port's).
        leg.History.Clear();
        receiver.RequestRetransmit(1199, 1);
        await WaitUntilAsync(() => control.PacketsMissing == 5, "the request after the clear");
    }

    [Fact]
    public async Task Sync_packets_are_sent_from_the_control_socket()
    {
        await using var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await control.SendSyncAsync(new IPEndPoint(IPAddress.Loopback, 9), new byte[20], CancellationToken.None));
        await control.StartAsync(CancellationToken.None);
        using var receiver = new LoopbackReceiver(new IPEndPoint(IPAddress.Loopback, control.Port));

        var ntp = new byte[SyncPacket.Size];
        new SyncPacket(true, unchecked((uint)(1000 - 77175)), 0x0123456789ABCDEFUL, 1000).Write(ntp);
        var ptp = new byte[PtpSyncPacket.Size];
        new PtpSyncPacket(false, 5000, 123_456_789_000UL, 5000 + 77175, 0xFFFF_123456789ABCUL).Write(ptp);
        await control.SendSyncAsync(receiver.ControlEndPoint, ntp, CancellationToken.None);
        await control.SendSyncAsync(receiver.ControlEndPoint, ptp, CancellationToken.None);

        await WaitUntilAsync(() => receiver.ControlPackets.Count == 2, "two syncs");
        var packets = receiver.ControlPackets.ToArray();
        Assert.Equal(ntp, packets[0]);
        Assert.Equal(ptp, packets[1]);
        Assert.Equal(2, control.SyncsSent);
    }

    [Fact]
    public async Task Unknown_control_packets_are_logged_and_ignored()
    {
        var logger = new ListLogger<ControlPort>();
        await using var control = new ControlPort(logger, IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        using var receiver = new LoopbackReceiver(new IPEndPoint(IPAddress.Loopback, control.Port));

        var sync = new byte[SyncPacket.Size];
        new SyncPacket(true, 1, 2, 3).Write(sync);
        receiver.SendRaw(sync);
        receiver.SendRaw(sync);
        receiver.SendRaw([0x80, 0xD5, 0x00]); // truncated request
        await WaitUntilAsync(() => control.UnknownPacketsReceived == 3, "three unknown datagrams");
        Assert.Equal(0, control.RetransmitRequestsReceived);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("0xD4", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("0xD4", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("0xD5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_history_is_logged_and_the_port_keeps_answering()
    {
        var logger = new ListLogger<ControlPort>();
        var control = new ControlPort(logger, IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        using var receiver = new LoopbackReceiver(new IPEndPoint(IPAddress.Loopback, control.Port));
        control.Register(receiver.ControlEndPoint, new ThrowingHistory(), false, receiver.DataEndPoint);

        receiver.RequestRetransmit(10, 1);
        await WaitUntilAsync(() => logger.Entries.Any(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException), "the logged handler failure");

        // The loop survived: the next request (after re-registering with a working history) is answered.
        using var leg = new SenderLeg(KeyFor(9));
        control.Register(receiver.ControlEndPoint, leg.History, false, receiver.DataEndPoint);
        leg.Build(11, 0, false, FrameFor(0));
        receiver.RequestRetransmit(11, 1);
        await WaitUntilAsync(() => receiver.Retransmitted.ContainsKey(11), "the answer after the failure");
        Assert.Equal(2, control.RetransmitRequestsReceived);

        await control.DisposeAsync(); // does not rethrow
    }

    private sealed class ThrowingHistory : IPacketHistory
    {
        public void Store(ushort sequence, ReadOnlySpan<byte> packet) => throw new InvalidOperationException("broken history");

        public bool TryGet(ushort sequence, Span<byte> destination, out int length) => throw new InvalidOperationException("broken history");

        public void Clear()
        {
        }
    }

    [Fact]
    public async Task Dispose_stops_the_loops_and_releases_the_ports()
    {
        var control = new ControlPort(bindAddress: IPAddress.Loopback);
        await control.StartAsync(CancellationToken.None);
        var responder = new TimingResponder(new MasterClock(), bindAddress: IPAddress.Loopback);
        await responder.StartAsync(CancellationToken.None);
        var controlPort = control.Port;
        var timingPort = responder.Port;
        await control.DisposeAsync();
        await responder.DisposeAsync();
        await control.DisposeAsync(); // idempotent
        await Assert.ThrowsAsync<ObjectDisposedException>(() => control.StartAsync(CancellationToken.None));

        // Rebinding the same ports works once the sockets are closed.
        await using var again = new ControlPort(bindAddress: IPAddress.Loopback, port: controlPort);
        await again.StartAsync(CancellationToken.None);
        Assert.Equal(controlPort, again.Port);
        await using var timingAgain = new TimingResponder(new MasterClock(), bindAddress: IPAddress.Loopback, port: timingPort);
        await timingAgain.StartAsync(CancellationToken.None);
        Assert.Equal(timingPort, timingAgain.Port);
    }

    [Fact]
    public void Lookup_rule_is_address_first_then_port()
    {
        var historyA = new PacketHistory(1, 64);
        var historyB = new PacketHistory(1, 64);
        var a = new ControlPort.Registration(new IPEndPoint(IPAddress.Parse("192.0.2.26"), 6001), historyA, false, new IPEndPoint(IPAddress.Parse("192.0.2.26"), 6000));
        var b = new ControlPort.Registration(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7001), historyB, false, new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7000));
        var c = b with { Control = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8001) };
        System.Collections.Immutable.ImmutableArray<ControlPort.Registration> registrations = [a, b, c];

        Assert.Same(a, ControlPort.Find(registrations, new IPEndPoint(IPAddress.Parse("192.0.2.26"), 6001)));
        Assert.Same(a, ControlPort.Find(registrations, new IPEndPoint(IPAddress.Parse("192.0.2.26"), 40000)));
        Assert.Same(b, ControlPort.Find(registrations, new IPEndPoint(IPAddress.Loopback, 7001)));
        Assert.Same(c, ControlPort.Find(registrations, new IPEndPoint(IPAddress.Loopback, 8001)));
        Assert.Null(ControlPort.Find(registrations, new IPEndPoint(IPAddress.Loopback, 9001)));
        Assert.Null(ControlPort.Find(registrations, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6001)));
    }

    [Fact]
    public void Ipv4_mapped_sources_of_a_dual_mode_socket_are_normalised_before_the_lookup()
    {
        // What a dual-mode IPv6 socket (the default bind) reports for an IPv4 receiver.
        var mapped = new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.26"), 6001).Serialize();
        var source = UdpSockets.ToEndPoint(mapped);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.0.2.26"), 6001), source);

        var registration = new ControlPort.Registration(new IPEndPoint(IPAddress.Parse("192.0.2.26"), 6001), new PacketHistory(1, 64), false, new IPEndPoint(IPAddress.Parse("192.0.2.26"), 6000));
        Assert.Same(registration, ControlPort.Find([registration], source));

        var v6 = UdpSockets.ToEndPoint(new IPEndPoint(IPAddress.Parse("fe80::1"), 7000).Serialize());
        Assert.Equal(IPAddress.Parse("fe80::1"), v6.Address);
        Assert.Null(ControlPort.Find([registration], v6));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
