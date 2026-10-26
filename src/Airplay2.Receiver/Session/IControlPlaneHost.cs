using System.Net;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Session;

namespace Airplay2.Receiver.Session;

/// <summary>
/// What the control plane (<see cref="ReceiverControlPlane"/>, <see cref="ControlSession"/>) needs from the receiver that
/// hosts it (SPEC-receiver.md 4.1-4.9): the <c>/info</c> dictionary, the output and its level, the event sink, and the
/// factories that bind the PTP slave and the stream sockets. The facade (<see cref="AirplayReceiver"/>) implements it; the
/// loopback tests implement it with recorders. Every member may be called from any connection's handler, one at a time
/// (the control plane serialises its handlers, 4.9).
/// </summary>
public interface IControlPlaneHost
{
    /// <summary>True when an AAC decoder loaded (SPEC-receiver.md R3, R4): decides which buffered <c>audioFormat</c>s a stream SETUP accepts.</summary>
    bool AacAvailable { get; }

    /// <summary>The output the volume goes to and that is released at the end of a stream (SPEC-receiver.md 5.5, 4.9).</summary>
    IReceiverAudioOutput Output { get; }

    /// <summary>
    /// The <c>/info</c> dictionary for one request (SPEC-receiver.md 3.2), also the <c>value</c> of the event channel's
    /// <c>updateInfo</c> (4.4).
    /// </summary>
    /// <param name="requester">The remote endpoint of the requesting connection (<c>senderAddress</c>).</param>
    /// <param name="includeTxt">Add <c>txtAirPlay</c> (the stage-1 request with a <c>qualifier</c>, and <c>updateInfo</c>).</param>
    /// <param name="streamActive">A stream is set up: status flag bit 17 (SPEC-receiver.md 4.6).</param>
    PlistDictionary BuildInfo(IPEndPoint requester, bool includeTxt, bool streamActive);

    /// <summary>The current master level G of the selected output, or null while no output is selected (SPEC-receiver.md R9).</summary>
    ValueTask<int?> GetOutputLevelAsync(CancellationToken cancellationToken);

    /// <summary>Reports an event to the host (SPEC-receiver.md 8.3); must not block.</summary>
    void Publish(ReceiverEvent receiverEvent);

    /// <summary>
    /// Starts the PTP slave of a PTP session SETUP for the sender's addresses and clock id (SPEC-receiver.md 4.3, R5);
    /// null runs the session in anchor-only timing (R6). Called before the session SETUP is answered.
    /// </summary>
    ValueTask<ISenderTiming?> StartTimingAsync(SenderTimingPeer peer, CancellationToken cancellationToken);

    /// <summary>
    /// Binds the sockets of one stream SETUP and starts receiving (SPEC-receiver.md 4.6): UDP data and control (type 96) or a
    /// TCP data listener and an idle UDP control socket (type 103), each on port 0, feeding the stream's
    /// <see cref="PlayoutBuffer"/>. The request has been validated (shk, spf/sr, audioFormat); a failure is answered 500.
    /// <see cref="ReceiverStreamFactory"/> is the product implementation.
    /// </summary>
    ValueTask<IReceiverStream> CreateStreamAsync(ReceiverStreamRequest request, ISenderTiming? timing, CancellationToken cancellationToken);
}

/// <summary>The sender's timing peer of a PTP session SETUP (SPEC-receiver.md 4.3: <c>timingPeerInfo.Addresses</c>, <c>timingPeerInfo.ClockID</c>).</summary>
/// <param name="Addresses">The sender's PTP addresses (the control connection's remote address when the body lists none).</param>
/// <param name="ClockId">The sender's grandmaster clock id, or null when absent.</param>
/// <param name="ControlAddress">The control connection's remote address.</param>
public sealed record SenderTimingPeer(IReadOnlyList<IPAddress> Addresses, ulong? ClockId, IPAddress ControlAddress);

/// <summary>The PTP side of one sender session (SPEC-receiver.md R5, 4.3, 4.5): its slave and the accepted source addresses.</summary>
public interface ISenderTiming : IAsyncDisposable
{
    /// <summary>The session's PTP slave (SPEC-receiver.md 6.2), or null when the host runs anchor-only (R6).</summary>
    IPtpSlave? Slave { get; }

    /// <summary><c>SETPEERS</c>/<c>SETPEERSX</c> (SPEC-receiver.md 4.5): the addresses are added to the slave's accepted sources.</summary>
    void AddPeers(IReadOnlyList<IPAddress> addresses);
}

/// <summary>A validated stream SETUP (SPEC-receiver.md 4.6, SPEC-control.md 4.4).</summary>
/// <param name="Type">Realtime (96) or buffered (103).</param>
/// <param name="SharedKey">The 32-byte <c>shk</c>.</param>
/// <param name="AudioFormat">The <c>audioFormat</c> bitmask (R3).</param>
/// <param name="Format">The decoder it selects: <c>ALAC/44100/16/2</c> or <c>AAC-LC/44100/2</c>.</param>
/// <param name="SenderControl">The sender's control endpoint (its address and announced <c>controlPort</c>; 0xD5 requests go there).</param>
/// <param name="BindAddress">The local address the stream sockets bind (the control connection's local address).</param>
/// <param name="Raw">The <c>streams[0]</c> dictionary.</param>
public sealed record ReceiverStreamRequest(StreamType Type, byte[] SharedKey, ulong AudioFormat, string Format, IPEndPoint SenderControl, IPAddress BindAddress, PlistDictionary Raw);

/// <summary>
/// One set-up stream as the control plane drives it (SPEC-receiver.md 4.5-4.7, 4.9): its bound ports and the playout
/// actions of SETRATEANCHORTIME, FLUSH and FLUSHBUFFERED, forwarded to its <see cref="PlayoutBuffer"/>. Disposing it is the
/// stream TEARDOWN (sockets closed, buffer dropped); the control plane releases the output afterwards.
/// </summary>
public interface IReceiverStream : IAsyncDisposable
{
    /// <summary>Realtime (96) or buffered (103).</summary>
    StreamType Type { get; }

    /// <summary>The <c>dataPort</c> of the answer (UDP for 96, TCP for 103).</summary>
    int DataPort { get; }

    /// <summary>The <c>controlPort</c> of the answer (UDP; idle for 103, SPEC-receiver.md 4.6).</summary>
    int ControlPort { get; }

    /// <summary>The <c>audioBufferSize</c> of the answer for a buffered stream (8 MiB, SPEC-receiver.md 4.6), or null.</summary>
    long? AudioBufferSize { get; }

    /// <summary><c>SETRATEANCHORTIME rate 1</c> with an anchor (SPEC-receiver.md 4.7): <see cref="PlayoutBuffer.SetAnchor"/>.</summary>
    void SetAnchor(uint rtpTime, ulong networkTimeNanos, ulong timelineId);

    /// <summary><c>SETRATEANCHORTIME rate 0</c> (SPEC-receiver.md 4.7): <see cref="PlayoutBuffer.Pause"/>.</summary>
    void Pause();

    /// <summary><c>SETRATEANCHORTIME rate 1</c> (SPEC-receiver.md 4.7): <see cref="PlayoutBuffer.Resume"/>.</summary>
    void Resume();

    /// <summary><c>FLUSH</c> of a realtime stream (SPEC-receiver.md 4.5): every buffered frame dropped, the output flushed; the next packet expected at <paramref name="nextSequence"/>.</summary>
    void Flush(ushort? nextSequence);

    /// <summary>Immediate <c>FLUSHBUFFERED</c> (SPEC-receiver.md 4.7): frames below <paramref name="untilSequence"/> dropped, the output flushed and paused.</summary>
    void FlushBuffered(uint untilSequence);

    /// <summary>Deferred <c>FLUSHBUFFERED</c> (SPEC-receiver.md 4.7): a splice registered with <see cref="PlayoutBuffer.Splice"/>.</summary>
    void Splice(uint fromSequence, uint untilSequence);

    /// <summary>The stream's <see cref="PlayoutBuffer.Statistics"/> (SPEC-receiver.md 5.3, 9.1), or null when the stream has no buffer.</summary>
    PlayoutStatistics? Statistics => null;
}
