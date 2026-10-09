using System.Net;

namespace Airplay2.Sender.Rtp;

/// <summary>Packet type bytes on the UDP ports (SPEC-audio.md 2).</summary>
public static class RtpPacketTypes
{
    /// <summary>Timing request, receiver to sender (timing port).</summary>
    public const byte TimingRequest = 0xD2;

    /// <summary>Timing reply, sender to receiver.</summary>
    public const byte TimingReply = 0xD3;

    /// <summary>NTP sync, sender to receiver control port.</summary>
    public const byte Sync = 0xD4;

    /// <summary>Retransmit request, receiver to sender control port.</summary>
    public const byte RetransmitRequest = 0xD5;

    /// <summary>Retransmit reply (wrapped original packet), sender to receiver control port.</summary>
    public const byte RetransmitReply = 0xD6;

    /// <summary>PTP sync, sender to receiver control port.</summary>
    public const byte PtpSync = 0xD7;

    /// <summary>Realtime audio payload type.</summary>
    public const byte AudioPayloadType = 0x60;
}

/// <summary>The constants of the realtime stream (SPEC-audio.md 1).</summary>
public static class RealtimeStreamConstants
{
    /// <summary>Frames per packet.</summary>
    public const int FramesPerPacket = 352;

    /// <summary>Sample rate.</summary>
    public const int SampleRate = 44100;

    /// <summary>RTP header size.</summary>
    public const int HeaderSize = 12;

    /// <summary>Trailer: 16-byte tag + 8-byte nonce.</summary>
    public const int TrailerSize = 24;

    /// <summary>Largest ALAC frame (escape) for 352 frames of 16-bit stereo.</summary>
    public const int MaxAlacFrame = 1412;

    /// <summary>Largest UDP payload: 12 + 1412 + 24.</summary>
    public const int MaxPacketSize = HeaderSize + MaxAlacFrame + TrailerSize;

    /// <summary>Default sender latency L in frames (SPEC.md decision D1: 77175, Apple's wire value).</summary>
    public const int DefaultLatencyFrames = 77175;

    /// <summary>Retransmit history length in packets (8 s).</summary>
    public const int HistoryPackets = 1000;

    /// <summary>Sync packet cadence in frames (one per second).</summary>
    public const int SyncIntervalFrames = SampleRate;
}

/// <summary>
/// Builds and encrypts realtime RTP audio packets (SPEC-audio.md 1.2, 1.3): header
/// <c>80 60|E0 seq16 ts32 ssrc32</c>, ChaCha20-Poly1305 with key <c>shk</c>, nonce = 4 zero bytes + 64-bit LE counter,
/// AAD = header bytes 4..11, output <c>header || ciphertext || tag || nonce[4..11]</c>.
/// One instance per receiver (own key and counter); the plaintext ALAC frame is shared by the group.
/// </summary>
public sealed class RtpAudioPacketBuilder
{
    /// <summary>Creates a builder for one receiver's stream key.</summary>
    public RtpAudioPacketBuilder(ReadOnlySpan<byte> streamKey, uint ssrc)
    {
        if (streamKey.Length != 32)
        {
            throw new ArgumentException("shk must be 32 bytes.", nameof(streamKey));
        }

        StreamKey = streamKey.ToArray();
        Ssrc = ssrc;
    }

    /// <summary>The 32-byte <c>shk</c>.</summary>
    public byte[] StreamKey { get; }

    /// <summary>The SSRC written into every packet.</summary>
    public uint Ssrc { get; }

    /// <summary>The next nonce counter; starts at 0, never reset while the key lives.</summary>
    public ulong NextCounter { get; private set; }

    /// <summary>Builds one packet into <paramref name="destination"/> and returns its length.</summary>
    public int Build(Span<byte> destination, ushort sequence, uint timestamp, bool marker, ReadOnlySpan<byte> alacFrame) =>
        throw new NotImplementedException("TODO(impl: rtp-timing-control)");
}

/// <summary>A 20-byte NTP sync packet (type 0xD4, SPEC-audio.md 2.3).</summary>
/// <param name="First">Set the 0x10 "extension" bit (first sync of a stream or after a join/flush).</param>
/// <param name="NowFrame">The RTP timestamp that should be at the loudspeaker at <paramref name="NtpTime"/>.</param>
/// <param name="NtpTime">The sender clock time this sync describes.</param>
/// <param name="NextFrame"><c>NowFrame + L</c>: the RTP timestamp of the next packet to be sent.</param>
public readonly record struct SyncPacket(bool First, uint NowFrame, ulong NtpTime, uint NextFrame)
{
    /// <summary>Packet size.</summary>
    public const int Size = 20;

    /// <summary>The flags/length field; 7 adds 11 025 frames on shairport-sync, see open question A-Q9.</summary>
    public const ushort Flags = 0x0007;

    /// <summary>Writes the packet.</summary>
    public void Write(Span<byte> destination) => throw new NotImplementedException("TODO(impl: rtp-timing-control)");
}

/// <summary>A 28-byte PTP sync packet (type 0xD7, SPEC-audio.md 4.4): <c>90|80 D7 0006 now_frame ptp_nanos(8) next_frame clock_id(8)</c>, all BE. Layout owned by rtp-timing-control; sent by the realtime transport under PTP.</summary>
public readonly record struct PtpSyncPacket(bool First, uint NowFrame, ulong PtpTimeNanos, uint NextFrame, ulong ClockId)
{
    /// <summary>Packet size.</summary>
    public const int Size = 28;

    /// <summary>The "length" field.</summary>
    public const ushort Flags = 0x0006;

    /// <summary>Writes the packet.</summary>
    public void Write(Span<byte> destination) => throw new NotImplementedException("TODO(impl: rtp-timing-control)");
}

/// <summary>A 32-byte timing request/reply (types 0xD2/0xD3, SPEC-audio.md 2.2).</summary>
public readonly record struct TimingPacket(byte Type, ushort Sequence, ulong Origin, ulong Receive, ulong Transmit)
{
    /// <summary>Packet size.</summary>
    public const int Size = 32;

    /// <summary>Parses a request; false when malformed.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out TimingPacket result) => throw new NotImplementedException("TODO(impl: rtp-timing-control)");

    /// <summary>Builds the reply to a request: reference = request transmit, receive = our receive time, transmit = our send time.</summary>
    public static TimingPacket Reply(in TimingPacket request, ulong receiveNtp, ulong transmitNtp) => throw new NotImplementedException("TODO(impl: rtp-timing-control)");

    /// <summary>Writes the packet.</summary>
    public void Write(Span<byte> destination) => throw new NotImplementedException("TODO(impl: rtp-timing-control)");
}

/// <summary>A retransmit request (type 0xD5, 8 or 16 bytes, SPEC-audio.md 2.4).</summary>
public readonly record struct RetransmitRequest(ushort FirstSequence, ushort Count)
{
    /// <summary>Parses a request; accepts the 8- and 16-byte forms.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out RetransmitRequest result) => throw new NotImplementedException("TODO(impl: rtp-timing-control)");
}

/// <summary>Ring of the last <see cref="RealtimeStreamConstants.HistoryPackets"/> encrypted packets per receiver, indexed by sequence number.</summary>
public interface IPacketHistory
{
    /// <summary>Stores a fully built packet.</summary>
    void Store(ushort sequence, ReadOnlySpan<byte> packet);

    /// <summary>Copies a stored packet; false when it is outside the window.</summary>
    bool TryGet(ushort sequence, Span<byte> destination, out int length);

    /// <summary>Clears the ring (TEARDOWN, not FLUSH).</summary>
    void Clear();
}

/// <summary>Answers 0xD2 timing requests from the shared timing socket (SPEC-audio.md 2.2); one per process.</summary>
public interface ITimingResponder : IAsyncDisposable
{
    /// <summary>The bound UDP port announced as <c>timingPort</c>.</summary>
    int Port { get; }

    /// <summary>Binds and starts answering.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Count of replies sent (diagnostics; open question A-Q3 checks it goes above zero).</summary>
    long RepliesSent { get; }
}

/// <summary>
/// The shared control socket (SPEC-audio.md 2.3, 2.4): sends syncs, receives 0xD5 requests and answers them from each
/// receiver's history. Lookup rule for an incoming request: match the registered receivers by source <b>address</b>
/// first; only when several registered receivers share that address (two sessions on one host, e.g. two fake receivers
/// on loopback) is the source <b>port</b> compared with the announced control port. shairport-sync sends 0xD5 from the
/// socket bound to its announced <c>controlPort</c> (rtp.c:1224-1230), owntone matches by address only (airplay.c:2380),
/// so a receiver that requests from another port must still be found. Replies go to the request's source endpoint
/// (wrapped 0xD6) or to the registered data endpoint (raw form).
/// </summary>
public interface IControlPort : IAsyncDisposable
{
    /// <summary>The bound UDP port announced as <c>controlPort</c>.</summary>
    int Port { get; }

    /// <summary>Registers a receiver's announced control endpoint with its history and reply mode (see the lookup rule on the interface).</summary>
    void Register(IPEndPoint receiverControl, IPacketHistory history, bool replyOnDataPort, IPEndPoint receiverData);

    /// <summary>Unregisters a receiver.</summary>
    void Unregister(IPEndPoint receiverControl);

    /// <summary>Sends a sync packet to one receiver.</summary>
    ValueTask SendSyncAsync(IPEndPoint receiverControl, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);

    /// <summary>Binds and starts receiving.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Retransmit requests received (diagnostics, open question A-Q6 / C-7.14).</summary>
    long RetransmitRequestsReceived { get; }
}
