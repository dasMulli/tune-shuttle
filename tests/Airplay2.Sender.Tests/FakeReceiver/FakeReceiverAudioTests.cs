using System.Net;
using System.Net.Sockets;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Rtp;
using Xunit;
using static Airplay2.Sender.Tests.Fake.FakeReceiverHarness;

namespace Airplay2.Sender.Tests.Fake;

/// <summary>
/// Self-tests of the fake receiver's UDP side (WORK-PLAN fake-receiver "Verify": audio decode of the reference ALAC packets
/// sent as RTP; gap detection and retransmit requests in both reply forms; timing requests; sync recording).
/// </summary>
public class FakeReceiverAudioTests
{
    private const uint Ssrc = 0x12345678;

    [Fact]
    public async Task Reference_ALAC_packets_sent_as_RTP_decode_to_their_PCM()
    {
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync();
        await sender.SetupStreamAsync(controlPort: 6001);

        var vectors = AlacVectors();
        using var builder = new RtpAudioPacketBuilder(sender.Pairing.DefaultStreamKey, Ssrc);
        using var socket = NewUdpSocket();
        var target = new IPEndPoint(IPAddress.Loopback, receiver.DataPort);
        for (var i = 0; i < vectors.Count; i++)
        {
            await SendAsync(socket, target, Build(builder, (ushort)(65533 + i), 5000u + (uint)(352 * i), i == 0, vectors[i].Alac));
        }

        for (var i = 0; i < vectors.Count; i++)
        {
            var packet = await NextAsync<FakeReceiverEvent.AudioPacket>(receiver);
            Assert.Equal(unchecked((ushort)(65533 + i)), packet.Sequence); // wraps through 0
            Assert.Equal(5000u + (uint)(352 * i), packet.Timestamp);
            Assert.Equal(i == 0, packet.Marker);
            Assert.Equal(vectors[i].Pcm, packet.Pcm);
        }

        var rest = await DrainAsync(receiver, TimeSpan.FromMilliseconds(200));
        Assert.DoesNotContain(rest, e => e is FakeReceiverEvent.BadAudioPacket or FakeReceiverEvent.RetransmitRequested);
    }

    [Fact]
    public async Task Packets_under_a_wrong_key_are_reported_as_bad()
    {
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync();
        await sender.SetupStreamAsync(controlPort: 6001);

        using var builder = new RtpAudioPacketBuilder(new byte[32], Ssrc);
        using var socket = NewUdpSocket();
        await SendAsync(socket, new IPEndPoint(IPAddress.Loopback, receiver.DataPort), Build(builder, 1, 0, true, AlacVectors()[0].Alac));
        Assert.Equal("authentication failed", (await NextAsync<FakeReceiverEvent.BadAudioPacket>(receiver)).Reason);
    }

    [Fact]
    public async Task Ntp_and_ptp_sync_packets_are_recorded()
    {
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync();
        await sender.SetupStreamAsync(controlPort: 6001);
        using var socket = NewUdpSocket();
        var control = new IPEndPoint(IPAddress.Loopback, receiver.ControlPort);

        var ntp = new byte[SyncPacket.Size];
        new SyncPacket(true, 1000, 0x83AA7E8000000001UL, 1000 + 77175).Write(ntp);
        await SendAsync(socket, control, ntp);
        Assert.Equal(new FakeReceiverEvent.Sync(true, 1000, 0x83AA7E8000000001UL, 1000 + 77175), await NextAsync<FakeReceiverEvent.Sync>(receiver));

        var ptp = new byte[PtpSyncPacket.Size];
        new PtpSyncPacket(false, 2000, 123_456_789UL, 2000 + 77175, 0xFFFF_0000_1234_5678UL).Write(ptp);
        await SendAsync(socket, control, ptp);
        Assert.Equal(new FakeReceiverEvent.PtpSync(false, 2000, 123_456_789UL, 2000 + 77175, 0xFFFF_0000_1234_5678UL), await NextAsync<FakeReceiverEvent.PtpSync>(receiver));

        await SendAsync(socket, control, [0x80, 0xE0, 0, 0, 0, 0]);
        Assert.Equal(0xE0, (await NextAsync<FakeReceiverEvent.UnknownControlPacket>(receiver)).Type);
    }

    [Fact]
    public async Task Ntp_session_sends_timing_requests_to_the_announced_timing_port()
    {
        await using var responder = new TimingResponder(new MasterClock(), bindAddress: IPAddress.Loopback);
        await responder.StartAsync(CancellationToken.None);
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync(NtpSessionBody(responder.Port));
        var started = DateTime.UtcNow;
        await sender.SetupStreamAsync(controlPort: 6001);

        for (var i = 0; i < 3; i++)
        {
            var reply = await NextAsync<FakeReceiverEvent.TimingReply>(receiver);
            Assert.NotEqual(0UL, reply.Origin); // the reference timestamp is our request's transmit time
            Assert.True(reply.Transmit >= reply.Receive);
        }

        // Three requests 300 ms apart (SPEC-audio.md 2.2): all three within well under the 3 s steady interval.
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.True(responder.RepliesSent >= 3);
    }

    [Fact]
    public async Task Ptp_session_sends_no_timing_requests()
    {
        await using var responder = new TimingResponder(new MasterClock(), bindAddress: IPAddress.Loopback);
        await responder.StartAsync(CancellationToken.None);
        await using var receiver = await StartAsync();
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync(NtpSessionBody(responder.Port, protocol: "PTP"));
        await sender.SetupStreamAsync(controlPort: 6001);
        await Task.Delay(700);
        Assert.Equal(0, responder.RepliesSent);
    }

    [Theory]
    [InlineData(false, false, false)] // wrapped 0xD6 on the control port, 8-byte requests (shairport)
    [InlineData(true, false, false)] // raw packet on the data port (owntone's amplifier quirk)
    [InlineData(false, true, false)] // requests from a second socket: the sender's address-first lookup
    [InlineData(false, false, true)] // 16-byte requests (airplay2-receiver)
    public async Task Dropped_packets_are_requested_and_answered(bool rawReplies, bool secondSocket, bool extended)
    {
        var options = new FakeReceiverOptions { DropEveryNthPacket = 5, RetransmitFromSecondSocket = secondSocket, ExtendedRetransmitRequests = extended };
        await using var receiver = await StartAsync(options);
        await using var controlPort = new ControlPort(bindAddress: IPAddress.Loopback);
        await controlPort.StartAsync(CancellationToken.None);
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync();
        await sender.SetupStreamAsync(controlPort.Port);

        var history = new PacketHistory();
        var data = new IPEndPoint(IPAddress.Loopback, receiver.DataPort);
        controlPort.Register(new IPEndPoint(IPAddress.Loopback, receiver.ControlPort), history, rawReplies, data);

        var vectors = AlacVectors();
        const int count = 32;
        const ushort first = 500;
        using var builder = new RtpAudioPacketBuilder(sender.Pairing.DefaultStreamKey, Ssrc);
        using var socket = NewUdpSocket();
        for (var i = 0; i < count; i++)
        {
            var sequence = (ushort)(first + i);
            var packet = Build(builder, sequence, (uint)(352 * i), i == 0, vectors[i % vectors.Count].Alac);
            history.Store(sequence, packet);
            await SendAsync(socket, data, packet);
        }

        // Every 5th fresh packet is dropped (6 of 32, the last one at index 29, followed by two more), each requested and resent.
        var decoded = new Dictionary<ushort, short[]>();
        var dropped = new List<ushort>();
        var answered = new List<FakeReceiverEvent.RetransmitAnswered>();
        var requested = 0;
        using var cts = new CancellationTokenSource(Patience);
        while (decoded.Count < count)
        {
            switch (await receiver.Events.ReadAsync(cts.Token))
            {
                case FakeReceiverEvent.AudioPacket audio:
                    decoded[audio.Sequence] = audio.Pcm;
                    break;
                case FakeReceiverEvent.PacketDropped drop:
                    dropped.Add(drop.Sequence);
                    break;
                case FakeReceiverEvent.RetransmitRequested request:
                    requested += request.Count;
                    break;
                case FakeReceiverEvent.RetransmitAnswered answer:
                    answered.Add(answer);
                    break;
                case FakeReceiverEvent.BadAudioPacket bad:
                    Assert.Fail($"bad packet {bad.Sequence}: {bad.Reason}");
                    break;
            }
        }

        Assert.Equal(6, dropped.Count);
        Assert.Equal(6, requested);
        Assert.Equal(dropped.Order(), answered.Select(a => a.Sequence).Order());
        Assert.All(answered, a => Assert.Equal(!rawReplies, a.Wrapped));
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(vectors[i % vectors.Count].Pcm, decoded[(ushort)(first + i)]);
        }

        Assert.Equal(6, controlPort.RetransmitRequestsReceived);
        Assert.Equal(0, controlPort.UnmatchedRequests);
    }

    [Fact]
    public async Task No_requests_when_RequestRetransmits_is_off()
    {
        await using var receiver = await StartAsync(new FakeReceiverOptions { DropEveryNthPacket = 2, RequestRetransmits = false });
        await using var sender = await SenderSide.ConnectAsync(receiver);
        await sender.SetupSessionAsync();
        await sender.SetupStreamAsync(controlPort: 6001);
        using var builder = new RtpAudioPacketBuilder(sender.Pairing.DefaultStreamKey, Ssrc);
        using var socket = NewUdpSocket();
        var frame = AlacVectors()[0].Alac;
        for (var i = 0; i < 6; i++)
        {
            await SendAsync(socket, new IPEndPoint(IPAddress.Loopback, receiver.DataPort), Build(builder, (ushort)i, (uint)(352 * i), i == 0, frame));
        }

        var events = await DrainAsync(receiver, TimeSpan.FromMilliseconds(300));
        Assert.Equal(3, events.OfType<FakeReceiverEvent.PacketDropped>().Count());
        Assert.Empty(events.OfType<FakeReceiverEvent.RetransmitRequested>());
    }

    private static Socket NewUdpSocket() => new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    private static byte[] Build(RtpAudioPacketBuilder builder, ushort sequence, uint timestamp, bool marker, byte[] frame)
    {
        var buffer = new byte[RtpAudioPacketBuilder.PacketLength(frame.Length)];
        builder.Build(buffer, sequence, timestamp, marker, frame);
        return buffer;
    }

    private static async Task SendAsync(Socket socket, IPEndPoint target, byte[] datagram)
    {
        await socket.SendToAsync(datagram, SocketFlags.None, target);
        await Task.Delay(2); // keep loopback order deterministic
    }
}
