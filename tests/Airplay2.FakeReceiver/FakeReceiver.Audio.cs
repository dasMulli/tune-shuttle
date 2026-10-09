using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Rtp;
using Microsoft.Extensions.Logging;

namespace Airplay2.FakeReceiver;

/// <content>
/// The UDP side (SPEC-audio.md 1-2): the data port decrypts with <c>shk</c> and ALAC-decodes every packet, tracks sequence
/// gaps and sends 0xD5 retransmit requests; the control port records 0xD4/0xD7 syncs and accepts wrapped 0xD6 replies;
/// the timing socket sends 0xD2 requests to the sender's <c>timingPort</c> and records the 0xD3 replies.
/// </content>
public sealed partial class FakeReceiver
{
    /// <summary>Datagrams of this size or less are dropped, as shairport does [sps:rtp.c:1803]: an RTP packet carries at least one payload byte.</summary>
    private const int MinimumAudioPacket = RealtimeStreamConstants.HeaderSize + RealtimeStreamConstants.TrailerSize;

    private static readonly TimeSpan TimingStartInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TimingInterval = TimeSpan.FromSeconds(3);

    // Decode buffers, used under _audioLock only: one packet at a time, no allocation per packet except the PCM handed out.
    private readonly Lock _audioLock = new();
    private readonly byte[] _alacBuffer = new byte[4096];
    private readonly short[] _pcmBuffer = new short[RealtimeStreamConstants.FramesPerPacket * 2];
    private UdpReceiveLoop? _dataLoop;
    private UdpReceiveLoop? _controlLoop;
    private UdpReceiveLoop? _secondControlLoop;
    private UdpReceiveLoop? _timingLoop;
    private StreamState? _stream;

    /// <summary>The realtime stream currently served by the UDP ports.</summary>
    private sealed class StreamState(ControlConnection owner, byte[]? key, IPEndPoint senderControl, uint? streamId)
    {
        public ControlConnection Owner { get; } = owner;

        public byte[]? Key { get; } = key;

        public uint? StreamId { get; } = streamId;

        /// <summary>Where retransmit requests go: the announced <c>controlPort</c>, replaced by the source of the first sync (see <see cref="HandleControlAsync"/>).</summary>
        public IPEndPoint SenderControl { get; set; } = senderControl;

        public bool LearnedControl { get; set; }

        public bool HaveExpected { get; set; }

        public ushort Expected { get; set; }

        public long FreshPackets { get; set; }

        public HashSet<ushort> Outstanding { get; } = [];

        public CancellationTokenSource? TimingStop { get; set; }

        public Task TimingTask { get; set; } = Task.CompletedTask;
    }

    private void StartUdp()
    {
        _dataLoop = new UdpReceiveLoop("fake data port", _logger, IPAddress.Loopback, 0, HandleDataAsync);
        _controlLoop = new UdpReceiveLoop("fake control port", _logger, IPAddress.Loopback, 0, (s, d, a, c) => HandleControlAsync(d, a, primary: true));
        _timingLoop = new UdpReceiveLoop("fake timing socket", _logger, IPAddress.Loopback, 0, HandleTimingAsync);
        _dataLoop.Start(this);
        _controlLoop.Start(this);
        _timingLoop.Start(this);
        if (Options.RetransmitFromSecondSocket)
        {
            _secondControlLoop = new UdpReceiveLoop("fake second control socket", _logger, IPAddress.Loopback, 0, (s, d, a, c) => HandleControlAsync(d, a, primary: false));
            _secondControlLoop.Start(this);
        }
    }

    private async Task StopUdpAsync()
    {
        StreamState? stream;
        lock (_audioLock)
        {
            stream = _stream;
            _stream = null;
        }

        if (stream is not null)
        {
            await StopTimingAsync(stream).ConfigureAwait(false);
        }

        foreach (var loop in new[] { _dataLoop, _controlLoop, _secondControlLoop, _timingLoop })
        {
            if (loop is not null)
            {
                await loop.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Makes a stream SETUP the active stream and starts the timing requests for NTP sessions (SPEC-audio.md 2.2).</summary>
    private async Task StartStreamAsync(ControlConnection owner, byte[]? key, IPEndPoint senderControl, uint? streamId, IPEndPoint? timingTarget)
    {
        var stream = new StreamState(owner, key, senderControl, streamId);
        if (timingTarget is not null && Options.SendTimingRequests && _timingLoop?.Socket is { } socket)
        {
            // Started before the stream is published, so a concurrent stop always sees the task to cancel.
            stream.TimingStop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            stream.TimingTask = RunTimingRequestsAsync(socket, timingTarget, stream.TimingStop.Token);
            Track(stream.TimingTask, "timing requests");
        }

        StreamState? previous;
        lock (_audioLock)
        {
            previous = _stream;
            _stream = stream;
        }

        if (previous is not null)
        {
            await StopTimingAsync(previous).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Fake receiver {Name}: stream {StreamId} active, sender control {Control}, timing {Timing}",
            Options.Name,
            streamId,
            senderControl,
            timingTarget?.ToString() ?? "none");
    }

    /// <summary>Ends the active stream if <paramref name="owner"/> set it up (TEARDOWN, connection closed).</summary>
    private async Task StopStreamOwnedByAsync(ControlConnection owner)
    {
        StreamState? stream = null;
        lock (_audioLock)
        {
            if (_stream is { } current && ReferenceEquals(current.Owner, owner))
            {
                stream = current;
                _stream = null;
            }
        }

        if (stream is not null)
        {
            await StopTimingAsync(stream).ConfigureAwait(false);
        }
    }

    private bool HasStreamOwnedBy(ControlConnection owner)
    {
        lock (_audioLock)
        {
            return _stream is { } current && ReferenceEquals(current.Owner, owner);
        }
    }

    /// <summary>FLUSH: forget outstanding requests; the next packet is expected at the announced sequence (or re-anchors on arrival).</summary>
    private void FlushStream(ControlConnection owner, ushort? nextSequence)
    {
        lock (_audioLock)
        {
            if (_stream is not { } stream || !ReferenceEquals(stream.Owner, owner))
            {
                return;
            }

            stream.Outstanding.Clear();
            stream.HaveExpected = nextSequence.HasValue;
            stream.Expected = nextSequence ?? 0;
        }
    }

    private static async Task StopTimingAsync(StreamState stream)
    {
        if (stream.TimingStop is not { } stop)
        {
            return;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        await stream.TimingTask.ConfigureAwait(false); // RunTimingRequestsAsync ends quietly on cancellation
        stop.Dispose();
        stream.TimingStop = null;
    }

    /// <summary>Three requests 300 ms apart, then one every 3 s, as shairport's requester [sps:rtp.c:595-664].</summary>
    private async Task RunTimingRequestsAsync(Socket socket, IPEndPoint target, CancellationToken stop)
    {
        var buffer = new byte[TimingPacket.Size];
        for (var i = 0; !stop.IsCancellationRequested; i++)
        {
            // shairport sends seq 7 and zero times [sps:rtp.c:632-633]; we put our clock into the transmit field so the
            // reply's reference timestamp (= request transmit, SPEC-audio.md 2.2) is checkable.
            new TimingPacket(RtpPacketTypes.TimingRequest, 7, 0, 0, _clock.NtpNow).Write(buffer);
            try
            {
                await socket.SendToAsync(buffer, SocketFlags.None, target, stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug("Fake receiver {Name}: timing request to {Target} failed: {Error}", Options.Name, target, ex.SocketErrorCode);
            }

            try
            {
                await Task.Delay(i < 2 ? TimingStartInterval : TimingInterval, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private ValueTask HandleTimingAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop)
    {
        if (TimingPacket.TryParse(datagram.Span, out var packet) && !packet.IsRequest)
        {
            Write(new FakeReceiverEvent.TimingReply(packet.Origin, packet.Receive, packet.Transmit));
        }
        else
        {
            _logger.LogDebug("Fake receiver {Name}: ignored {Length}-byte datagram on the timing socket", Options.Name, datagram.Length);
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask HandleDataAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            _logger.LogDebug("Fake receiver {Name}: {Length}-byte datagram on the data port without a stream", Options.Name, datagram.Length);
            return ValueTask.CompletedTask;
        }

        if (datagram.Length <= MinimumAudioPacket)
        {
            _logger.LogDebug("Fake receiver {Name}: dropping a {Length}-byte datagram on the data port", Options.Name, datagram.Length);
            return ValueTask.CompletedTask;
        }

        // A raw retransmission on the data port (SPEC-audio.md 2.4 form 2) is recognised by its sequence being outstanding.
        var request = ProcessAudio(stream, datagram.Span, wrapped: false);
        return request is { } r ? SendRetransmitRequestAsync(stream, r, stop) : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The control port (and the second socket): 0xD4 / 0xD7 syncs are recorded, 0xD6 replies unwrapped and decoded,
    /// anything else logged and ignored (SPEC-audio.md 2.4). Like shairport, the primary port learns the sender's control
    /// address from the first sync and sends retransmit requests there [sps:rtp.c:1672-1677].
    /// </summary>
    private ValueTask HandleControlAsync(ReadOnlyMemory<byte> datagram, SocketAddress source, bool primary)
    {
        var span = datagram.Span;
        if (span.Length < 4)
        {
            Write(new FakeReceiverEvent.UnknownControlPacket(span.Length > 1 ? span[1] : (byte)0, span.Length));
            return ValueTask.CompletedTask;
        }

        var stream = Volatile.Read(ref _stream);
        switch (span[1] & 0x7F)
        {
            case RtpPacketTypes.Sync & 0x7F when SyncPacket.TryParse(span, out var sync):
                LearnControlAddress(stream, source, primary);
                Write(new FakeReceiverEvent.Sync(sync.First, sync.NowFrame, sync.NtpTime, sync.NextFrame));
                break;
            case RtpPacketTypes.PtpSync & 0x7F when PtpSyncPacket.TryParse(span, out var ptp):
                LearnControlAddress(stream, source, primary);
                Write(new FakeReceiverEvent.PtpSync(ptp.First, ptp.NowFrame, ptp.PtpTimeNanos, ptp.NextFrame, ptp.ClockId));
                break;
            case RtpPacketTypes.RetransmitReply & 0x7F when RetransmitReply.TryUnwrap(span, out var original):
                if (stream is not null && original.Length > MinimumAudioPacket)
                {
                    // A wrapped reply never yields a new request: only outstanding sequences are accepted.
                    ProcessAudio(stream, original, wrapped: true);
                }
                else
                {
                    _logger.LogDebug("Fake receiver {Name}: retransmit reply without a stream or payload ignored", Options.Name);
                }

                break;
            default:
                _logger.LogDebug("Fake receiver {Name}: unknown control packet 0x{Type:X2} ({Length} bytes)", Options.Name, span[1], span.Length);
                Write(new FakeReceiverEvent.UnknownControlPacket(span[1], span.Length));
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void LearnControlAddress(StreamState? stream, SocketAddress source, bool primary)
    {
        if (!primary || stream is null)
        {
            return;
        }

        lock (_audioLock)
        {
            if (!stream.LearnedControl)
            {
                stream.LearnedControl = true;
                stream.SenderControl = UdpSockets.ToEndPoint(source);
            }
        }
    }

    /// <summary>Gap tracking, simulated loss and decoding of one packet; returns the retransmit request to send, if any.</summary>
    private RetransmitRequest? ProcessAudio(StreamState stream, ReadOnlySpan<byte> packet, bool wrapped)
    {
        var sequence = RtpAudioPacketBuilder.ReadSequence(packet);
        var timestamp = RtpAudioPacketBuilder.ReadTimestamp(packet);
        var marker = RtpAudioPacketBuilder.ReadMarker(packet);
        RetransmitRequest? request = null;
        lock (_audioLock)
        {
            if (!ReferenceEquals(_stream, stream))
            {
                return null;
            }

            var answered = stream.Outstanding.Remove(sequence);
            if (wrapped && !answered)
            {
                _logger.LogDebug("Fake receiver {Name}: unrequested or duplicate retransmission of {Sequence}", Options.Name, sequence);
                return null;
            }

            if (!answered)
            {
                stream.FreshPackets++;
                var n = Options.DropEveryNthPacket;
                if (n > 0 && stream.HaveExpected && !marker && stream.FreshPackets % n == 0)
                {
                    Write(new FakeReceiverEvent.PacketDropped(sequence));
                    return null;
                }

                request = TrackSequence(stream, sequence, marker);
            }

            Decode(stream, packet, sequence, timestamp, marker);
            if (answered)
            {
                Write(new FakeReceiverEvent.RetransmitAnswered(sequence, wrapped));
            }
        }

        return request;
    }

    /// <summary>Advances the expected sequence (wrap-safe); a jump forward within the history window is a gap to request.</summary>
    private RetransmitRequest? TrackSequence(StreamState stream, ushort sequence, bool marker)
    {
        if (marker || !stream.HaveExpected)
        {
            // First packet, or a re-anchor after a join/flush (marker bit, SPEC-audio.md 1.2): no gap.
            stream.HaveExpected = true;
            stream.Expected = unchecked((ushort)(sequence + 1));
            return null;
        }

        var gap = unchecked((short)(sequence - stream.Expected));
        if (gap == 0)
        {
            stream.Expected = unchecked((ushort)(sequence + 1));
            return null;
        }

        if (gap < 0)
        {
            _logger.LogDebug("Fake receiver {Name}: late or duplicate packet {Sequence} (expected {Expected})", Options.Name, sequence, stream.Expected);
            return null;
        }

        var first = stream.Expected;
        stream.Expected = unchecked((ushort)(sequence + 1));
        if (!Options.RequestRetransmits)
        {
            return null;
        }

        if (gap >= RealtimeStreamConstants.HistoryPackets)
        {
            // Beyond the sender's history (SPEC-audio.md 2.4): resynchronise instead of asking.
            _logger.LogWarning("Fake receiver {Name}: {Gap} packets missing before {Sequence}; resynchronising", Options.Name, gap, sequence);
            return null;
        }

        for (var i = 0; i < gap; i++)
        {
            stream.Outstanding.Add(unchecked((ushort)(first + i)));
        }

        return new RetransmitRequest(first, (ushort)gap);
    }

    private void Decode(StreamState stream, ReadOnlySpan<byte> packet, ushort sequence, uint timestamp, bool marker)
    {
        if (stream.Key is not { } key)
        {
            Write(new FakeReceiverEvent.BadAudioPacket(sequence, packet.Length, "no usable shk"));
            return;
        }

        if (!RtpAudioPacketBuilder.TryDecrypt(key, packet, _alacBuffer, out var length))
        {
            _logger.LogWarning("Fake receiver {Name}: packet {Sequence} failed authentication", Options.Name, sequence);
            Write(new FakeReceiverEvent.BadAudioPacket(sequence, packet.Length, "authentication failed"));
            return;
        }

        var bits = new BitBuffer(_alacBuffer, length);
        var frames = Decoder.Decode(ref bits, _pcmBuffer, (int)Decoder.Config.FrameLength, 2);
        if (frames < 0)
        {
            _logger.LogWarning("Fake receiver {Name}: packet {Sequence} is not a valid ALAC frame", Options.Name, sequence);
            Write(new FakeReceiverEvent.BadAudioPacket(sequence, packet.Length, "ALAC decode failed"));
            return;
        }

        Write(new FakeReceiverEvent.AudioPacket(sequence, timestamp, marker, _pcmBuffer.AsSpan(0, frames * 2).ToArray()));
    }

    /// <summary>Sends a 0xD5 request (8- or 16-byte form) from the control port or the second socket to the sender's control address.</summary>
    private async ValueTask SendRetransmitRequestAsync(StreamState stream, RetransmitRequest request, CancellationToken stop)
    {
        var socket = (Options.RetransmitFromSecondSocket ? _secondControlLoop : _controlLoop)?.Socket;
        if (socket is null)
        {
            return;
        }

        var buffer = new byte[RetransmitRequest.ExtendedSize];
        var length = request.Write(buffer, Options.ExtendedRetransmitRequests ? _clock.NtpNow : null);
        IPEndPoint target;
        lock (_audioLock)
        {
            target = stream.SenderControl;
        }

        // Recorded before sending so the answer can never precede the request in Events.
        Write(new FakeReceiverEvent.RetransmitRequested(request.FirstSequence, request.Count));
        await socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, target, stop).ConfigureAwait(false);
    }
}
