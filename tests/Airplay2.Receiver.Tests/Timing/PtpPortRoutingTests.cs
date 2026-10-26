using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Streams;
using Xunit;

namespace Airplay2.Receiver.Tests.Timing;

/// <summary>
/// <see cref="PtpPort"/> (SPEC-receiver.md 6.1, 11.1): routing by message type and source address on loopback with
/// OS-chosen ports, the default handler fallback, counting what nobody handles, and <see cref="PtpPortException"/> on a
/// bind conflict.
/// </summary>
public class PtpPortRoutingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static PtpPortOptions Loopback => new() { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 };

    [Fact]
    public async Task Both_peers_on_one_address_are_separated_by_message_type()
    {
        await using var port = new PtpPort(Loopback);
        await port.EnsureBoundAsync(CancellationToken.None);
        var slave = new Recorder();
        var master = new Recorder();
        port.Register(IPAddress.Loopback, slave);
        port.SetDefaultHandler(master);
        using var peer = Bind(IPAddress.Loopback);

        await SendAsync(peer, port, Message(PtpMessageType.Sync), eventPort: true);
        await SendAsync(peer, port, Message(PtpMessageType.FollowUp), eventPort: false);
        await SendAsync(peer, port, Message(PtpMessageType.Announce), eventPort: false);
        await SendAsync(peer, port, Message(PtpMessageType.Signaling), eventPort: false);
        await SendAsync(peer, port, Message(PtpMessageType.DelayResp), eventPort: false);
        await SendAsync(peer, port, Message(PtpMessageType.DelayReq), eventPort: true);
        await SendAsync(peer, port, Message(PtpMessageType.PdelayReq), eventPort: true);
        await WaitUntilAsync(() => slave.Count + master.Count >= 7, "seven datagrams");

        PtpMessageType[] toSlave = [PtpMessageType.Sync, PtpMessageType.FollowUp, PtpMessageType.Announce, PtpMessageType.Signaling, PtpMessageType.DelayResp];
        PtpMessageType[] toMaster = [PtpMessageType.DelayReq, PtpMessageType.PdelayReq];
        Assert.Equal(toSlave.Order(), slave.Types.Order());
        Assert.Equal(toMaster.Order(), master.Types.Order());
        Assert.All(slave.Received, r => Assert.Equal(r.Type == PtpMessageType.Sync, r.EventPort));
        Assert.All(slave.Received, r => Assert.Equal(((IPEndPoint)peer.LocalEndPoint!).Port, r.Source.Port));
        var stats = port.Statistics;
        Assert.Equal((7L, 5L, 2L, 0L, 0L), (stats.Received, stats.RoutedToSlaves, stats.RoutedToDefault, stats.Unrouted, stats.NotPtp));
    }

    [Fact]
    public async Task Unregistered_sources_go_to_the_default_handler_and_nothing_handles_them_without_one()
    {
        await using var port = new PtpPort(Loopback);
        await port.EnsureBoundAsync(CancellationToken.None);
        var slave = new Recorder();
        var master = new Recorder();
        port.Register(IPAddress.Parse("192.0.2.77"), slave);
        port.SetDefaultHandler(master);
        using var peer = Bind(IPAddress.Loopback);

        await SendAsync(peer, port, Message(PtpMessageType.Sync), eventPort: true);
        await SendAsync(peer, port, Message(PtpMessageType.Announce), eventPort: false);
        await WaitUntilAsync(() => master.Count == 2, "the default handler");
        Assert.Equal(0, slave.Count);

        // Garbage is counted and dropped; without a default handler PTP messages are counted and dropped.
        await peer.SendToAsync(new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, port.GeneralPort));
        port.SetDefaultHandler(null);
        await SendAsync(peer, port, Message(PtpMessageType.DelayReq), eventPort: true);
        await WaitUntilAsync(() => port.Statistics.NotPtp == 1 && port.Statistics.Unrouted == 1, "dropped datagrams");
        Assert.Equal(2, master.Count);
    }

    [Fact]
    public async Task Register_replaces_and_unregister_removes_only_the_current_handler()
    {
        await using var port = new PtpPort(Loopback);
        await port.EnsureBoundAsync(CancellationToken.None);
        var first = new Recorder();
        var second = new Recorder();
        var master = new Recorder();
        port.SetDefaultHandler(master);
        port.Register(IPAddress.Loopback, first);
        port.Register(IPAddress.Loopback.MapToIPv6(), second); // the same address: the newest session's slave wins (4.3)
        using var peer = Bind(IPAddress.Loopback);

        await SendAsync(peer, port, Message(PtpMessageType.Sync), eventPort: true);
        await WaitUntilAsync(() => second.Count == 1, "the second slave");
        Assert.Equal(0, first.Count);

        Assert.False(port.Unregister(IPAddress.Loopback, first)); // no longer its handler
        await SendAsync(peer, port, Message(PtpMessageType.Sync), eventPort: true);
        await WaitUntilAsync(() => second.Count == 2, "the second slave again");

        Assert.True(port.Unregister(IPAddress.Loopback, second));
        await SendAsync(peer, port, Message(PtpMessageType.Sync), eventPort: true);
        await WaitUntilAsync(() => master.Count == 1, "the default handler after unregistering");
    }

    [Fact]
    public async Task A_bind_conflict_raises_a_ptp_port_exception_and_binding_is_idempotent()
    {
        await using var first = new PtpPort(Loopback);
        await first.EnsureBoundAsync(CancellationToken.None);
        var (eventPort, generalPort) = (first.EventPort, first.GeneralPort);
        await first.EnsureBoundAsync(CancellationToken.None);
        Assert.Equal((eventPort, generalPort), (first.EventPort, first.GeneralPort));

        await using var eventConflict = new PtpPort(Loopback with { EventPort = first.EventPort });
        var ex = await Assert.ThrowsAsync<PtpPortException>(() => eventConflict.EnsureBoundAsync(CancellationToken.None));
        Assert.Equal(first.EventPort, ex.Port);
        Assert.IsType<SocketException>(ex.InnerException);
        Assert.False(eventConflict.IsBound);

        await using var generalConflict = new PtpPort(Loopback with { GeneralPort = first.GeneralPort });
        Assert.Equal(first.GeneralPort, (await Assert.ThrowsAsync<PtpPortException>(() => generalConflict.EnsureBoundAsync(CancellationToken.None))).Port);
        Assert.False(generalConflict.IsBound);
    }

    [Fact]
    public async Task Send_needs_a_bound_port_and_uses_the_socket_of_the_requested_port()
    {
        await using var port = new PtpPort(Loopback);
        using var peer = Bind(IPAddress.Loopback);
        var target = (IPEndPoint)peer.LocalEndPoint!;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await port.SendAsync(target, Message(PtpMessageType.Sync), eventPort: true, CancellationToken.None));

        await port.EnsureBoundAsync(CancellationToken.None);
        await port.SendAsync(target, Message(PtpMessageType.Sync), eventPort: true, CancellationToken.None);
        await port.SendAsync(target, Message(PtpMessageType.FollowUp), eventPort: false, CancellationToken.None);
        var from = new Dictionary<PtpMessageType, int>();
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(Patience);
        for (var i = 0; i < 2; i++)
        {
            var result = await peer.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            from[(PtpMessageType)(buffer[0] & 0x0F)] = ((IPEndPoint)result.RemoteEndPoint).Port;
        }

        Assert.Equal(port.EventPort, from[PtpMessageType.Sync]);
        Assert.Equal(port.GeneralPort, from[PtpMessageType.FollowUp]);

        await port.DisposeAsync();
        Assert.False(port.IsBound);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => port.EnsureBoundAsync(CancellationToken.None));
    }

    internal static Socket Bind(IPAddress address)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(address, 0));
        return socket;
    }

    internal static byte[] Message(PtpMessageType type, ulong clockId = 0xFFFF_0000_0000_1234UL, ushort sequence = 1)
    {
        var buffer = new byte[PtpMessages.MaxMessageSize];
        var length = type switch
        {
            PtpMessageType.Sync => PtpMessages.WriteSync(buffer, clockId, sequence),
            PtpMessageType.FollowUp => PtpMessages.WriteFollowUp(buffer, clockId, sequence, PtpTimestamp.FromNanoseconds(1_000_000_000UL)),
            PtpMessageType.Announce => PtpMessages.WriteAnnounce(buffer, clockId, sequence),
            PtpMessageType.Signaling => PtpMessages.WriteSignaling(buffer, clockId, sequence),
            PtpMessageType.DelayReq => PtpMessages.WriteDelayReq(buffer, clockId, 1, sequence, PtpTimestamp.Zero),
            PtpMessageType.PdelayReq => PtpMessages.WritePdelayReq(buffer, clockId, 1, sequence, PtpTimestamp.Zero),
            PtpMessageType.DelayResp => DelayResp(buffer, clockId, sequence),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        return buffer[..length];
    }

    private static int DelayResp(byte[] buffer, ulong clockId, ushort sequence)
    {
        var request = new byte[PtpMessages.DelayReqSize];
        PtpMessages.WriteDelayReq(request, 0x0200_5EFF_FE10_2030UL, 1, sequence, PtpTimestamp.Zero);
        Assert.True(PtpHeader.TryRead(request, out var header));
        return PtpMessages.WriteDelayResp(buffer, clockId, header, PtpTimestamp.Zero);
    }

    private static Task SendAsync(Socket from, PtpPort to, byte[] message, bool eventPort) =>
        from.SendToAsync(message, new IPEndPoint(IPAddress.Loopback, eventPort ? to.EventPort : to.GeneralPort));

    internal static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>An endpoint that records what the port handed it.</summary>
    private sealed class Recorder : IPtpEndpoint
    {
        private readonly ConcurrentQueue<(PtpMessageType Type, IPEndPoint Source, bool EventPort)> _received = new();

        public int Count => _received.Count;

        public IEnumerable<(PtpMessageType Type, IPEndPoint Source, bool EventPort)> Received => _received;

        public IEnumerable<PtpMessageType> Types => _received.Select(r => r.Type);

        public ValueTask HandleAsync(PtpHeader header, ReadOnlyMemory<byte> datagram, IPEndPoint source, bool eventPort, ulong receivedAtNanos, CancellationToken cancellationToken)
        {
            _received.Enqueue((header.MessageType, source, eventPort));
            return ValueTask.CompletedTask;
        }
    }
}
