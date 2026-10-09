using System.Net;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;

namespace Airplay2.Sender.Streams;

/// <summary>Where one receiver's audio goes.</summary>
public sealed record ReceiverStreamEndpoint(IPEndPoint Data, IPEndPoint Control, byte[] StreamKey, int OffsetFrames, bool RetransmitOnDataPort);

/// <summary>
/// The pluggable stream layer (PLAN.md: realtime first, buffered second). One transport serves a whole group: it takes
/// each encoded ALAC frame once and delivers it to every registered receiver with per-receiver encryption, and it
/// produces whatever anchoring the stream type needs (NTP/PTP syncs or SETRATEANCHORTIME).
/// </summary>
public interface IStreamTransport : IAsyncDisposable
{
    /// <summary>
    /// The one master clock of this transport (PLAN.md: the sender is the clock master; SPEC-audio.md 8.5, SPEC.md D3).
    /// The timing responder / PTP grandmaster, the sync builder and the group player's pacer all read this instance.
    /// </summary>
    IMasterClock Clock { get; }

    /// <summary>The stream type this transport negotiates.</summary>
    StreamType Type { get; }

    /// <summary>The timing protocol it requires in the session SETUP.</summary>
    TimingProtocol TimingProtocol { get; }

    /// <summary>Our control port for the stream SETUP.</summary>
    int ControlPort { get; }

    /// <summary>Our timing port for the session SETUP (NTP) or 0 (PTP).</summary>
    int TimingPort { get; }

    /// <summary>Starts the sockets/threads; idempotent.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Adds a receiver in state CONNECTED: it gets an initial sync and a marker packet with the next frame.</summary>
    void AddReceiver(string receiverId, ReceiverStreamEndpoint endpoint);

    /// <summary>Removes a receiver and clears its history.</summary>
    void RemoveReceiver(string receiverId);

    /// <summary>Marks a receiver CONNECTED again after a FLUSH so the next packet re-anchors it.</summary>
    void Resync(string receiverId);

    /// <summary>
    /// Sends one encoded frame with the given sequence/timestamp to every receiver (encrypt per receiver, store in each
    /// history), and sends sync packets when due. Called by the pacer at 352-frame cadence.
    /// </summary>
    /// <param name="sequence">RTP sequence number of the packet.</param>
    /// <param name="timestamp">RTP timestamp of the packet's first frame.</param>
    /// <param name="alacFrame">The encoded frame (encrypted per receiver by the transport).</param>
    /// <param name="ntpNow">
    /// The instant the packet is <b>due</b> (the pacer's deadline as an NTP timestamp), not the instant of the call: it is
    /// the <c>T</c> of any sync sent with this packet, so <c>now_frame</c> and <c>T</c> advance in lock-step
    /// (SPEC-audio.md 2.3). The name is historical.
    /// </param>
    void Send(ushort sequence, uint timestamp, ReadOnlySpan<byte> alacFrame, ulong ntpNow);

    /// <summary>Sends sync packets to every receiver now (resume / re-anchor).</summary>
    void SendSync(uint nextTimestamp, ulong ntpNow, bool first);
}

// RealtimeStreamTransport (type 96, NTP and PTP syncs) lives in RealtimeStreamTransport.cs (package audio-pipeline-group-sync).

/// <summary>The PTP grandmaster the buffered stream (and the realtime stream under PTP) needs (SPEC-audio.md 4.4). Package ptp-master.</summary>
public interface IPtpMaster : IAsyncDisposable
{
    /// <summary>The clock identity announced in <c>timingPeerInfo.ClockID</c> and the 0xD7 syncs (<c>0xFFFF &lt;&lt; 48 | 48-bit seed</c>). Unsigned everywhere in code; cast to <c>long</c> only when writing the plist (SPEC-audio.md 4.4).</summary>
    ulong ClockId { get; }

    /// <summary>Adds a unicast peer (receiver address from its SETUP response).</summary>
    void AddPeer(IPAddress address);

    /// <summary>Removes a peer.</summary>
    void RemovePeer(IPAddress address);

    /// <summary>Binds UDP 319/320 and starts Announce/Sync/Follow_Up.</summary>
    Task StartAsync(CancellationToken cancellationToken);
}

// BufferedStreamTransport (type 103 over TCP, SETRATEANCHORTIME/FLUSHBUFFERED) lives in BufferedStreamTransport.cs (package buffered-stream).
