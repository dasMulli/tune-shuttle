using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Rtp;

namespace Airplay2.Sender.Tests.Rtp;

/// <summary>
/// A minimal receiver for the UDP loopback tests, running on its own thread: a data socket and a control socket on
/// 127.0.0.1 (and optionally a third socket that sends the 0xD5 requests, to exercise the address-first lookup).
/// It records audio packets by sequence number, asks for every gap with a 0xD5 request (shairport-sync style, 8 bytes,
/// or the 16-byte airplay2-receiver form), unwraps 0xD6 replies, takes raw packets for requested sequence numbers on
/// the data port as raw retransmissions, and records sync packets and timing replies.
/// </summary>
internal sealed class LoopbackReceiver : IDisposable
{
    private readonly Socket _data;
    private readonly Socket _control;
    private readonly Socket? _requester;
    private readonly IPEndPoint _senderControl;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<ushort, byte> _requested = new();
    private ushort? _expected;

    public LoopbackReceiver(IPEndPoint senderControl, bool requestFromOtherPort = false, bool longRequests = false)
    {
        _senderControl = senderControl;
        LongRequests = longRequests;
        _data = Bind();
        _control = Bind();
        if (requestFromOtherPort)
        {
            _requester = Bind();
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "LoopbackReceiver" };
        _thread.Start();
    }

    public bool LongRequests { get; }

    public IPEndPoint DataEndPoint => (IPEndPoint)_data.LocalEndPoint!;

    public IPEndPoint ControlEndPoint => (IPEndPoint)_control.LocalEndPoint!;

    public IPEndPoint? RequesterEndPoint => (IPEndPoint?)_requester?.LocalEndPoint;

    /// <summary>Audio packets as first received on the data port (the dropped ones are absent).</summary>
    public ConcurrentDictionary<ushort, byte[]> Received { get; } = new();

    /// <summary>Retransmitted packets (unwrapped) by sequence number, with the form they came in.</summary>
    public ConcurrentDictionary<ushort, (byte[] Packet, bool Wrapped)> Retransmitted { get; } = new();

    public ConcurrentQueue<byte[]> ControlPackets { get; } = new();

    public ConcurrentQueue<byte[]> TimingReplies { get; } = new();

    public int RequestsSent;

    /// <summary>Sends a 0xD2 request (shairport-sync layout, transmit time filled in) to <paramref name="timingPort"/> from the control socket.</summary>
    public void SendTimingRequest(IPEndPoint timingPort, ulong transmit)
    {
        var request = new TimingPacket(RtpPacketTypes.TimingRequest, 7, 0, 0, transmit);
        var buffer = new byte[TimingPacket.Size];
        request.Write(buffer);
        _control.SendTo(buffer, timingPort);
    }

    /// <summary>Sends an arbitrary datagram to the sender's control port from the control socket.</summary>
    public void SendRaw(byte[] datagram) => _control.SendTo(datagram, _senderControl);

    /// <summary>Asks for <paramref name="count"/> packets from <paramref name="first"/> as a gap detector would.</summary>
    public void RequestRetransmit(ushort first, ushort count)
    {
        for (var i = 0; i < count; i++)
        {
            _requested[(ushort)(first + i)] = 0;
        }

        var buffer = new byte[RetransmitRequest.ExtendedSize];
        var length = new RetransmitRequest(first, count).Write(buffer, LongRequests ? 0xE000000000000000UL : null);
        (_requester ?? _control).SendTo(buffer.AsSpan(0, length), SocketFlags.None, _senderControl);
        Interlocked.Increment(ref RequestsSent);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        _data.Dispose();
        _control.Dispose();
        _requester?.Dispose();
        _stop.Dispose();
    }

    private static Socket Bind()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private void Run()
    {
        var buffer = new byte[4096];
        var sockets = new List<Socket>(3);
        while (!_stop.IsCancellationRequested)
        {
            sockets.Clear();
            sockets.Add(_data);
            sockets.Add(_control);
            if (_requester is not null)
            {
                sockets.Add(_requester);
            }

            try
            {
                Socket.Select(sockets, null, null, 50_000);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            foreach (var socket in sockets)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = socket.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException)
                {
                    continue;
                }

                var datagram = buffer.AsSpan(0, n).ToArray();
                if (socket == _data)
                {
                    OnData(datagram);
                }
                else
                {
                    OnControl(datagram);
                }
            }
        }
    }

    private void OnData(byte[] packet)
    {
        var sequence = RtpAudioPacketBuilder.ReadSequence(packet);
        if (_requested.TryRemove(sequence, out _))
        {
            Retransmitted[sequence] = (packet, false);
            return;
        }

        Received[sequence] = packet;
        if (_expected is { } expected && sequence != expected)
        {
            var gap = (ushort)(sequence - expected);
            if (gap < 0x8000)
            {
                RequestRetransmit(expected, gap);
            }
        }

        _expected = (ushort)(sequence + 1);
    }

    private void OnControl(byte[] datagram)
    {
        if (RetransmitReply.TryUnwrap(datagram, out var original))
        {
            var sequence = RtpAudioPacketBuilder.ReadSequence(original);
            _requested.TryRemove(sequence, out _);
            Retransmitted[sequence] = (original.ToArray(), true);
            return;
        }

        if (TimingPacket.TryParse(datagram, out var timing) && !timing.IsRequest)
        {
            TimingReplies.Enqueue(datagram);
            return;
        }

        ControlPackets.Enqueue(datagram);
    }
}
