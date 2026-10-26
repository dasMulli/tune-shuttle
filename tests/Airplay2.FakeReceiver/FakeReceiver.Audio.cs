using System.Net;
using System.Net.Sockets;
using Airplay2.Receiver.Streams;
using Airplay2.Sender.Rtp;
using Microsoft.Extensions.Logging;

namespace Airplay2.FakeReceiver;

/// <content>
/// The UDP side (SPEC-audio.md 1-2): the data and control ports are the library's <see cref="RealtimeStreamReceiver"/>
/// (SPEC-receiver.md 5.1), which decrypts with <c>shk</c>, ALAC-decodes every packet, tracks sequence gaps, sends 0xD5
/// retransmit requests, reports 0xD4/0xD7 syncs and accepts wrapped 0xD6 replies; the fake adds the simulated loss and
/// the request-form switches of <see cref="FakeReceiverOptions"/>, records what it reports as <see cref="FakeReceiverEvent"/>s,
/// and keeps the NTP timing socket that sends 0xD2 requests to the sender's <c>timingPort</c> and records the 0xD3 replies.
/// </content>
public sealed partial class FakeReceiver
{
    private static readonly TimeSpan TimingStartInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TimingInterval = TimeSpan.FromSeconds(3);

    private readonly Lock _timingLock = new();
    private readonly RealtimeStreamReceiver _realtime;
    private UdpReceiveLoop? _timingLoop;
    private StreamTiming? _streamTiming;

    /// <summary>The NTP timing requests of the active stream (SPEC-audio.md 2.2), owned by the connection that set the stream up.</summary>
    private sealed class StreamTiming(ControlConnection owner, CancellationTokenSource stop, Task task)
    {
        public ControlConnection Owner { get; } = owner;

        public CancellationTokenSource Stop { get; } = stop;

        public Task Task { get; } = task;
    }

    private RealtimeStreamReceiver CreateRealtimeReceiver(FakeReceiverOptions options)
    {
        var n = options.DropEveryNthPacket;
        var receiverOptions = new RealtimeStreamReceiverOptions
        {
            BindAddress = IPAddress.Loopback,
            Name = "fake",
            RequestRetransmits = options.RequestRetransmits,
            ExtendedRetransmitRequests = options.ExtendedRetransmitRequests,
            RetransmitFromSecondSocket = options.RetransmitFromSecondSocket,
            SimulateLoss = n > 0 ? (fresh, _) => fresh % n == 0 : null,
            Clock = _clock,
        };
        return new RealtimeStreamReceiver(receiverOptions, new AudioObserver(this), options.Logger);
    }

    private void StartUdp()
    {
        _realtime.Start();
        _timingLoop = new UdpReceiveLoop("fake timing socket", _logger, IPAddress.Loopback, 0, HandleTimingAsync);
        _timingLoop.Start(this);
    }

    private async Task StopUdpAsync()
    {
        StreamTiming? timing;
        lock (_timingLock)
        {
            timing = _streamTiming;
            _streamTiming = null;
        }

        await StopTimingAsync(timing).ConfigureAwait(false);
        await _realtime.DisposeAsync().ConfigureAwait(false);

        if (_timingLoop is not null)
        {
            await _timingLoop.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Makes a stream SETUP the active stream and starts the timing requests for NTP sessions (SPEC-audio.md 2.2).</summary>
    private async Task StartStreamAsync(ControlConnection owner, byte[]? key, IPEndPoint senderControl, uint? streamId, IPEndPoint? timingTarget)
    {
        StreamTiming? timing = null;
        if (timingTarget is not null && Options.SendTimingRequests && _timingLoop?.Socket is { } socket)
        {
            // Started before the stream is published, so a concurrent stop always sees the task to cancel.
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            timing = new StreamTiming(owner, stop, RunTimingRequestsAsync(socket, timingTarget, stop.Token));
            Track(timing.Task, "timing requests");
        }

        StreamTiming? previous;
        lock (_timingLock)
        {
            previous = _streamTiming;
            _streamTiming = timing;
            _realtime.BeginStream(owner, key, senderControl);
        }

        await StopTimingAsync(previous).ConfigureAwait(false);
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
        StreamTiming? timing = null;
        lock (_timingLock)
        {
            if (_realtime.EndStream(owner) && _streamTiming is { } current && ReferenceEquals(current.Owner, owner))
            {
                timing = current;
                _streamTiming = null;
            }
        }

        await StopTimingAsync(timing).ConfigureAwait(false);
    }

    private bool HasStreamOwnedBy(ControlConnection owner) => _realtime.HasStream(owner);

    /// <summary>FLUSH: forget outstanding requests; the next packet is expected at the announced sequence (or re-anchors on arrival).</summary>
    private void FlushStream(ControlConnection owner, ushort? nextSequence) => _realtime.Flush(owner, nextSequence);

    private static async Task StopTimingAsync(StreamTiming? timing)
    {
        if (timing is null)
        {
            return;
        }

        await timing.Stop.CancelAsync().ConfigureAwait(false);
        await timing.Task.ConfigureAwait(false); // RunTimingRequestsAsync ends quietly on cancellation
        timing.Stop.Dispose();
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

    /// <summary>Records what the library's realtime receiver reports as <see cref="FakeReceiverEvent"/>s, in the order it reports them.</summary>
    private sealed class AudioObserver(FakeReceiver owner) : IRealtimeStreamObserver
    {
        public void OnAudioPacket(ushort sequence, uint timestamp, bool marker, ReadOnlySpan<short> pcm) =>
            owner.Write(new FakeReceiverEvent.AudioPacket(sequence, timestamp, marker, pcm.ToArray()));

        public void OnNtpSync(SyncPacket sync) => owner.Write(new FakeReceiverEvent.Sync(sync.First, sync.NowFrame, sync.NtpTime, sync.NextFrame));

        public void OnPtpSync(PtpSyncPacket sync) =>
            owner.Write(new FakeReceiverEvent.PtpSync(sync.First, sync.NowFrame, sync.PtpTimeNanos, sync.NextFrame, sync.ClockId));

        public void OnRetransmitAnswered(ushort sequence, bool wrapped) => owner.Write(new FakeReceiverEvent.RetransmitAnswered(sequence, wrapped));

        public void OnRetransmitRequested(ushort firstSequence, ushort count) => owner.Write(new FakeReceiverEvent.RetransmitRequested(firstSequence, count));

        public void OnPacketDropped(ushort sequence) => owner.Write(new FakeReceiverEvent.PacketDropped(sequence));

        public void OnBadPacket(ushort sequence, int length, string reason) => owner.Write(new FakeReceiverEvent.BadAudioPacket(sequence, length, reason));

        public void OnUnknownControlPacket(byte type, int length) => owner.Write(new FakeReceiverEvent.UnknownControlPacket(type, length));
    }
}
