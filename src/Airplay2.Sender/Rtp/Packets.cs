using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Airplay2.Sender.Crypto;

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
/// <remarks>
/// The nonce counter starts at 0 and is never reset while the key lives (SPEC.md D9): owntone's 16-bit sequence-number
/// nonce would repeat every 65 536 packets under one key (SPEC-audio.md 1.3), pyatv's 64-bit counter does not
/// (<c>[pyatv] support/chacha20.py:57-59, 76-106</c>). A retransmission must be byte-identical to the original, so it
/// is served from the <see cref="IPacketHistory"/>, never rebuilt. Not thread-safe: one owner (the pacer thread).
/// </remarks>
public sealed class RtpAudioPacketBuilder : IDisposable
{
    // Reused across packets: constructing the BCL AEAD imports the key into a native handle, which would otherwise
    // happen about 125 times a second per receiver. Null where the OS lacks ChaCha20-Poly1305 (BouncyCastle fallback).
    private readonly ChaCha20Poly1305? _aead;
    private bool _disposed;

    /// <summary>Creates a builder for one receiver's stream key.</summary>
    /// <param name="streamKey">The 32-byte <c>shk</c> sent in the stream SETUP.</param>
    /// <param name="ssrc">The SSRC written into every packet (receivers ignore it on the realtime stream).</param>
    public RtpAudioPacketBuilder(ReadOnlySpan<byte> streamKey, uint ssrc)
    {
        if (streamKey.Length != 32)
        {
            throw new ArgumentException("shk must be 32 bytes.", nameof(streamKey));
        }

        StreamKey = streamKey.ToArray();
        Ssrc = ssrc;
        if (ChaCha20Poly1305.IsSupported)
        {
            _aead = new ChaCha20Poly1305(StreamKey);
        }
    }

    /// <summary>The 32-byte <c>shk</c>.</summary>
    public byte[] StreamKey { get; }

    /// <summary>The SSRC written into every packet.</summary>
    public uint Ssrc { get; }

    /// <summary>The next nonce counter; starts at 0, never reset while the key lives.</summary>
    public ulong NextCounter { get; private set; }

    /// <summary>The packet length for an ALAC frame of <paramref name="alacFrameLength"/> bytes: 12 + n + 24.</summary>
    public static int PacketLength(int alacFrameLength) =>
        RealtimeStreamConstants.HeaderSize + alacFrameLength + RealtimeStreamConstants.TrailerSize;

    /// <summary>
    /// Builds one packet into <paramref name="destination"/> and returns its length. Consumes one nonce counter value.
    /// </summary>
    /// <param name="destination">At least <see cref="PacketLength"/> bytes; must not overlap <paramref name="alacFrame"/>.</param>
    /// <param name="sequence">RTP sequence number (shared by the group).</param>
    /// <param name="timestamp">RTP timestamp in frames (shared by the group).</param>
    /// <param name="marker">Set the marker bit (first packet of a stream, or after a join or flush).</param>
    /// <param name="alacFrame">The plaintext ALAC frame; never empty (SPEC-audio.md 1.2: shairport-sync drops datagrams of 36 bytes or fewer).</param>
    public int Build(Span<byte> destination, ushort sequence, uint timestamp, bool marker, ReadOnlySpan<byte> alacFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (alacFrame.IsEmpty)
        {
            throw new ArgumentException("An RTP audio packet must carry at least one payload byte.", nameof(alacFrame));
        }

        var length = PacketLength(alacFrame.Length);
        if (destination.Length < length)
        {
            throw new ArgumentException($"Destination holds {destination.Length} bytes, the packet needs {length}.", nameof(destination));
        }

        if (destination[..length].Overlaps(alacFrame))
        {
            throw new ArgumentException("Destination must not overlap the ALAC frame.", nameof(destination));
        }

        if (NextCounter == ulong.MaxValue)
        {
            // 2^64 packets is unreachable in practice; refusing is still better than wrapping into a nonce reuse.
            throw new InvalidOperationException("The RTP nonce counter is exhausted for this stream key.");
        }

        var packet = destination[..length];
        WriteHeader(packet, sequence, timestamp, Ssrc, marker);

        var n = alacFrame.Length;
        var aad = packet.Slice(4, 8);
        var ciphertext = packet.Slice(RealtimeStreamConstants.HeaderSize, n);
        var tag = packet.Slice(RealtimeStreamConstants.HeaderSize + n, ChaCha20Poly1305Aead.TagSize);
        var nonceTail = packet.Slice(RealtimeStreamConstants.HeaderSize + n + ChaCha20Poly1305Aead.TagSize, 8);

        Span<byte> nonce = stackalloc byte[ChaCha20Poly1305Aead.NonceSize];
        ChaCha20Poly1305Aead.NonceFromCounter(NextCounter, nonce);
        if (_aead is not null)
        {
            _aead.Encrypt(nonce, alacFrame, ciphertext, tag, aad);
        }
        else
        {
            ChaCha20Poly1305Aead.Encrypt(StreamKey, nonce, alacFrame, aad, ciphertext, tag);
        }

        nonce[4..].CopyTo(nonceTail);
        NextCounter++;
        return length;
    }

    /// <summary>
    /// Receiver side of <see cref="Build"/> (fake receiver, tests): verifies and decrypts one packet. The nonce is the
    /// trailing 8 bytes front-padded with 4 zero bytes, the AAD is header bytes 4..11 (SPEC-audio.md 1.2, shairport-sync
    /// <c>rtp.c:1558-1619</c>).
    /// </summary>
    /// <param name="streamKey">The 32-byte <c>shk</c>.</param>
    /// <param name="packet">The complete RTP packet.</param>
    /// <param name="alacFrame">Receives the plaintext; at least <c>packet.Length - 36</c> bytes.</param>
    /// <param name="frameLength">The plaintext length.</param>
    /// <returns>False when the packet is too short or fails authentication.</returns>
    public static bool TryDecrypt(ReadOnlySpan<byte> streamKey, ReadOnlySpan<byte> packet, Span<byte> alacFrame, out int frameLength)
    {
        frameLength = 0;
        const int overhead = RealtimeStreamConstants.HeaderSize + RealtimeStreamConstants.TrailerSize;
        if (streamKey.Length != ChaCha20Poly1305Aead.KeySize || packet.Length <= overhead)
        {
            return false;
        }

        var n = packet.Length - overhead;
        if (alacFrame.Length < n)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[ChaCha20Poly1305Aead.NonceSize];
        nonce[..4].Clear();
        packet[^8..].CopyTo(nonce[4..]);
        var ciphertext = packet.Slice(RealtimeStreamConstants.HeaderSize, n);
        var tag = packet.Slice(RealtimeStreamConstants.HeaderSize + n, ChaCha20Poly1305Aead.TagSize);
        if (!ChaCha20Poly1305Aead.TryDecrypt(streamKey, nonce, ciphertext, tag, packet.Slice(4, 8), alacFrame[..n]))
        {
            return false;
        }

        frameLength = n;
        return true;
    }

    /// <summary>Writes the 12-byte RTP header <c>80 60|E0 seq ts ssrc</c> (SPEC-audio.md 1.2).</summary>
    public static void WriteHeader(Span<byte> destination, ushort sequence, uint timestamp, uint ssrc, bool marker)
    {
        if (destination.Length < RealtimeStreamConstants.HeaderSize)
        {
            throw new ArgumentException("An RTP header is 12 bytes.", nameof(destination));
        }

        destination[0] = 0x80;
        destination[1] = (byte)(RtpPacketTypes.AudioPayloadType | (marker ? 0x80 : 0x00));
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], ssrc);
    }

    /// <summary>Reads the sequence number (bytes 2..3) of an RTP packet.</summary>
    public static ushort ReadSequence(ReadOnlySpan<byte> packet) => BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);

    /// <summary>Reads the RTP timestamp (bytes 4..7) of an RTP packet.</summary>
    public static uint ReadTimestamp(ReadOnlySpan<byte> packet) => BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);

    /// <summary>True when the marker bit (byte 1, bit 7) is set.</summary>
    public static bool ReadMarker(ReadOnlySpan<byte> packet) => (packet[1] & 0x80) != 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _aead?.Dispose();
    }
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

    /// <summary>
    /// Writes the packet: <c>90|80 D4 0007 now_frame ntp_seconds ntp_fraction next_frame</c>, all BE
    /// (<c>[pyatv] stream_client.py:112-115</c>, <c>[sps] rtp.c:389-440</c>).
    /// </summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"A sync packet is {Size} bytes.", nameof(destination));
        }

        destination[0] = First ? (byte)0x90 : (byte)0x80;
        destination[1] = RtpPacketTypes.Sync;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], Flags);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], NowFrame);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], NtpTime);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], NextFrame);
    }

    /// <summary>Parses a 0xD4 packet (fake receiver, tests); false when malformed. The flags field is not checked.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out SyncPacket result)
    {
        result = default;
        if (packet.Length < Size || (packet[1] & 0x7F) != (RtpPacketTypes.Sync & 0x7F))
        {
            return false;
        }

        result = new SyncPacket(
            (packet[0] & 0x10) != 0,
            BinaryPrimitives.ReadUInt32BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(packet[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[16..]));
        return true;
    }
}

/// <summary>A 28-byte PTP sync packet (type 0xD7, SPEC-audio.md 4.4): <c>90|80 D7 0006 now_frame ptp_nanos(8) next_frame clock_id(8)</c>, all BE. Layout owned by rtp-timing-control; sent by the realtime transport under PTP.</summary>
/// <param name="First">Set the 0x10 bit (first sync of a stream or after a join/flush).</param>
/// <param name="NowFrame">The RTP timestamp that should be at the loudspeaker at <paramref name="PtpTimeNanos"/>.</param>
/// <param name="PtpTimeNanos">T in nanoseconds on the grandmaster's PTP timeline.</param>
/// <param name="NextFrame"><c>NowFrame + L</c>.</param>
/// <param name="ClockId">The grandmaster clock identity.</param>
public readonly record struct PtpSyncPacket(bool First, uint NowFrame, ulong PtpTimeNanos, uint NextFrame, ulong ClockId)
{
    /// <summary>Packet size.</summary>
    public const int Size = 28;

    /// <summary>The "length" field.</summary>
    public const ushort Flags = 0x0006;

    /// <summary>Writes the packet (<c>[sps] rtp.c:1679-1734</c>, <c>[ap2r] control.py:44-52</c>).</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"A PTP sync packet is {Size} bytes.", nameof(destination));
        }

        destination[0] = First ? (byte)0x90 : (byte)0x80;
        destination[1] = RtpPacketTypes.PtpSync;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], Flags);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], NowFrame);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], PtpTimeNanos);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], NextFrame);
        BinaryPrimitives.WriteUInt64BigEndian(destination[20..], ClockId);
    }

    /// <summary>Parses a 0xD7 packet (fake receiver, tests); false when malformed.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out PtpSyncPacket result)
    {
        result = default;
        if (packet.Length < Size || (packet[1] & 0x7F) != (RtpPacketTypes.PtpSync & 0x7F))
        {
            return false;
        }

        result = new PtpSyncPacket(
            (packet[0] & 0x10) != 0,
            BinaryPrimitives.ReadUInt32BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(packet[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(packet[20..]));
        return true;
    }
}

/// <summary>A 32-byte timing request/reply (types 0xD2/0xD3, SPEC-audio.md 2.2).</summary>
/// <param name="Type">The type byte as on the wire (<see cref="RtpPacketTypes.TimingRequest"/> or <see cref="RtpPacketTypes.TimingReply"/>).</param>
/// <param name="Sequence">Bytes 2..3 (shairport-sync sends 7; the reply copies the request's value).</param>
/// <param name="Origin">Bytes 8..15: origin (request) or reference (reply) timestamp.</param>
/// <param name="Receive">Bytes 16..23: receive timestamp.</param>
/// <param name="Transmit">Bytes 24..31: transmit timestamp.</param>
public readonly record struct TimingPacket(byte Type, ushort Sequence, ulong Origin, ulong Receive, ulong Transmit)
{
    /// <summary>Packet size.</summary>
    public const int Size = 32;

    /// <summary>True when this is a timing request (0x52, marker bit ignored).</summary>
    public bool IsRequest => (Type & 0x7F) == (RtpPacketTypes.TimingRequest & 0x7F);

    /// <summary>
    /// Parses a request (or a reply, for the fake receiver); false when shorter than 32 bytes or not of type 0x52/0x53.
    /// The marker bit of the type byte is ignored, as pyatv does on its control port (<c>stream_client.py:148</c>).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out TimingPacket result)
    {
        result = default;
        if (packet.Length < Size)
        {
            return false;
        }

        var type = packet[1] & 0x7F;
        if (type != (RtpPacketTypes.TimingRequest & 0x7F) && type != (RtpPacketTypes.TimingReply & 0x7F))
        {
            return false;
        }

        result = new TimingPacket(
            packet[1],
            BinaryPrimitives.ReadUInt16BigEndian(packet[2..]),
            BinaryPrimitives.ReadUInt64BigEndian(packet[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(packet[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(packet[24..]));
        return true;
    }

    /// <summary>Builds the reply to a request: reference = request transmit, receive = our receive time, transmit = our send time.</summary>
    /// <remarks>
    /// The sequence field is copied whole (owntone copies one byte, <c>airplay.c:2307</c>; pyatv writes 7; both are 7 in
    /// practice because shairport-sync's requester sends 7).
    /// </remarks>
    public static TimingPacket Reply(in TimingPacket request, ulong receiveNtp, ulong transmitNtp) =>
        new(RtpPacketTypes.TimingReply, request.Sequence, request.Transmit, receiveNtp, transmitNtp);

    /// <summary>Writes the packet: <c>80 type seq 00000000 origin receive transmit</c>, all BE.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"A timing packet is {Size} bytes.", nameof(destination));
        }

        destination[0] = 0x80;
        destination[1] = Type;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], Sequence);
        destination.Slice(4, 4).Clear();
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], Origin);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], Receive);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], Transmit);
    }
}

/// <summary>A retransmit request (type 0xD5, 8 or 16 bytes, SPEC-audio.md 2.4).</summary>
/// <param name="FirstSequence">Bytes 4..5: the first missing sequence number.</param>
/// <param name="Count">Bytes 6..7: the number of consecutive missing packets.</param>
public readonly record struct RetransmitRequest(ushort FirstSequence, ushort Count)
{
    /// <summary>The short form (shairport-sync, owntone, pyatv).</summary>
    public const int Size = 8;

    /// <summary>The long form with the NTP time of the first missing packet (airplay2-receiver <c>control.py:93-95</c>).</summary>
    public const int ExtendedSize = 16;

    /// <summary>
    /// Parses a request; accepts the 8- and 16-byte forms. Bytes 2..3 ("sequence": 1 from shairport-sync, the length in
    /// 32-bit words from airplay2-receiver) and the optional NTP time are ignored; the marker bit of the type byte is
    /// ignored as in pyatv (<c>stream_client.py:148</c>).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out RetransmitRequest result)
    {
        result = default;
        if ((packet.Length != Size && packet.Length != ExtendedSize) || (packet[1] & 0x7F) != (RtpPacketTypes.RetransmitRequest & 0x7F))
        {
            return false;
        }

        result = new RetransmitRequest(
            BinaryPrimitives.ReadUInt16BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[6..]));
        return true;
    }

    /// <summary>
    /// Writes a request (fake receiver, tests): the 8-byte form as shairport-sync sends it (sequence field 1), or the
    /// 16-byte form as airplay2-receiver sends it (length field 4 words, then the NTP time) when <paramref name="ntpTime"/>
    /// is given. Returns the length written.
    /// </summary>
    public int Write(Span<byte> destination, ulong? ntpTime = null)
    {
        var size = ntpTime is null ? Size : ExtendedSize;
        if (destination.Length < size)
        {
            throw new ArgumentException($"A retransmit request is {size} bytes.", nameof(destination));
        }

        destination[0] = 0x80;
        destination[1] = RtpPacketTypes.RetransmitRequest;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], ntpTime is null ? (ushort)1 : (ushort)(ExtendedSize / 4));
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], FirstSequence);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], Count);
        if (ntpTime is { } ntp)
        {
            BinaryPrimitives.WriteUInt64BigEndian(destination[8..], ntp);
        }

        return size;
    }
}

/// <summary>
/// The wrapped retransmit reply (type 0xD6, SPEC-audio.md 2.4 form 1): <c>80 D6 seq(2) original-packet</c>, as pyatv
/// sends it (<c>stream_client.py:155-170</c>) and shairport-sync parses it (<c>rtp.c:1735-1739</c>).
/// </summary>
public static class RetransmitReply
{
    /// <summary>The 4-byte wrapper in front of the original packet.</summary>
    public const int HeaderSize = 4;

    /// <summary>Writes the 4-byte wrapper for <paramref name="original"/> into <paramref name="destination"/>; the caller places the packet after it.</summary>
    public static void WriteHeader(Span<byte> destination, ReadOnlySpan<byte> original)
    {
        if (destination.Length < HeaderSize || original.Length < RealtimeStreamConstants.HeaderSize)
        {
            throw new ArgumentException("The wrapper needs 4 bytes and the original a full RTP header.", nameof(destination));
        }

        // Copy the sequence first: original may live right behind the wrapper in the same buffer.
        var hi = original[2];
        var lo = original[3];
        destination[0] = 0x80;
        destination[1] = RtpPacketTypes.RetransmitReply;
        destination[2] = hi;
        destination[3] = lo;
    }

    /// <summary>Unwraps a 0xD6 reply (fake receiver, tests); false when it is not one.</summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> original)
    {
        original = default;
        if (packet.Length < HeaderSize + RealtimeStreamConstants.HeaderSize || (packet[1] & 0x7F) != (RtpPacketTypes.RetransmitReply & 0x7F))
        {
            return false;
        }

        original = packet[HeaderSize..];
        return true;
    }
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
