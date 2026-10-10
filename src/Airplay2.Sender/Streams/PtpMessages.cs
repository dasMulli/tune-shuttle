using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Airplay2.Sender.Streams;

/// <summary>PTP message types (IEEE 1588-2008 table 19; the low nibble of byte 0).</summary>
public enum PtpMessageType : byte
{
    /// <summary>Sync (event port 319).</summary>
    Sync = 0x0,

    /// <summary>Delay_Req (event port 319, slave to master).</summary>
    DelayReq = 0x1,

    /// <summary>Pdelay_Req (event port 319).</summary>
    PdelayReq = 0x2,

    /// <summary>Pdelay_Resp (event port 319).</summary>
    PdelayResp = 0x3,

    /// <summary>Follow_Up (general port 320).</summary>
    FollowUp = 0x8,

    /// <summary>Delay_Resp (general port 320).</summary>
    DelayResp = 0x9,

    /// <summary>Pdelay_Resp_Follow_Up (general port 320).</summary>
    PdelayRespFollowUp = 0xA,

    /// <summary>Announce (general port 320).</summary>
    Announce = 0xB,

    /// <summary>Signaling (general port 320).</summary>
    Signaling = 0xC,

    /// <summary>Management (general port 320).</summary>
    Management = 0xD,
}

/// <summary>Bits of the PTP header <c>flagField</c> (IEEE 1588-2008 table 20, as two big-endian bytes).</summary>
[Flags]
public enum PtpFlags : ushort
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>leap61.</summary>
    Leap61 = 1 << 0,

    /// <summary>leap59.</summary>
    Leap59 = 1 << 1,

    /// <summary>currentUtcOffsetValid.</summary>
    CurrentUtcOffsetValid = 1 << 2,

    /// <summary>ptpTimescale.</summary>
    PtpTimescale = 1 << 3,

    /// <summary>timeTraceable.</summary>
    TimeTraceable = 1 << 4,

    /// <summary>frequencyTraceable.</summary>
    FrequencyTraceable = 1 << 5,

    /// <summary>alternateMasterFlag.</summary>
    AlternateMaster = 1 << 8,

    /// <summary>twoStepFlag.</summary>
    TwoStep = 1 << 9,

    /// <summary>unicastFlag.</summary>
    Unicast = 1 << 10,

    /// <summary>PTP profile specific 1.</summary>
    ProfileSpecific1 = 1 << 13,

    /// <summary>PTP profile specific 2.</summary>
    ProfileSpecific2 = 1 << 14,

    /// <summary>The value every iOS/libairptp Announce, Follow_Up and Signaling carries: UNICAST | TIMESCALE = <c>0x0408</c> (SPEC-audio.md 4.4).</summary>
    AppleGeneral = Unicast | PtpTimescale,

    /// <summary>Sync and Delay_Resp: UNICAST | TIMESCALE | TWO_STEP = <c>0x0608</c> (SPEC-audio.md 4.4).</summary>
    AppleTwoStep = Unicast | PtpTimescale | TwoStep,
}

/// <summary>
/// A PTP timestamp (IEEE 1588-2008 5.3.3): 48-bit seconds and 32-bit nanoseconds, 10 bytes big-endian on the wire
/// (<c>seconds_hi(16) || seconds_low(32) || nanoseconds(32)</c>, SPEC-audio.md 4.4).
/// </summary>
/// <param name="Seconds">Seconds (only the low 48 bits are written).</param>
/// <param name="Nanoseconds">Nanoseconds, below 10^9.</param>
public readonly record struct PtpTimestamp(ulong Seconds, uint Nanoseconds)
{
    /// <summary>Wire size.</summary>
    public const int Size = 10;

    /// <summary>The zero timestamp (iOS sends it as Announce/Sync <c>originTimestamp</c>).</summary>
    public static PtpTimestamp Zero => default;

    /// <summary>Total nanoseconds.</summary>
    public ulong TotalNanoseconds => (Seconds * 1_000_000_000UL) + Nanoseconds;

    /// <summary>Splits nanoseconds on the PTP timeline (<see cref="Rtp.IMasterClock.PtpNowNanos"/>).</summary>
    public static PtpTimestamp FromNanoseconds(ulong nanoseconds) => new(nanoseconds / 1_000_000_000UL, (uint)(nanoseconds % 1_000_000_000UL));

    /// <summary>Writes the 10 bytes.</summary>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(Seconds >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(destination[2..], (uint)Seconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], Nanoseconds);
    }

    /// <summary>Reads the 10 bytes.</summary>
    public static PtpTimestamp Read(ReadOnlySpan<byte> source) => new(
        ((ulong)BinaryPrimitives.ReadUInt16BigEndian(source) << 32) | BinaryPrimitives.ReadUInt32BigEndian(source[2..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[6..]));
}

/// <summary>A PTP port identity (IEEE 1588-2008 5.3.5): the 8-byte clock identity and a 16-bit port number.</summary>
/// <param name="ClockId">Clock identity, big-endian on the wire.</param>
/// <param name="PortNumber">Port number.</param>
public readonly record struct PtpPortIdentity(ulong ClockId, ushort PortNumber)
{
    /// <summary>Wire size.</summary>
    public const int Size = 10;

    /// <summary>
    /// The port number every iOS sender uses in <c>sourcePortIdentity</c> (bytes <c>0x80 0x05</c>), which libairptp copies
    /// "same as iOS" (SPEC-audio.md 4.4, <c>[ot] libairptp/src/ptp_msg_handle.c:232-255</c>).
    /// </summary>
    public const ushort ApplePortNumber = 0x8005;

    /// <summary>Writes the 10 bytes.</summary>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, ClockId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], PortNumber);
    }

    /// <summary>Reads the 10 bytes.</summary>
    public static PtpPortIdentity Read(ReadOnlySpan<byte> source) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(source), BinaryPrimitives.ReadUInt16BigEndian(source[8..]));
}

/// <summary>
/// The 34-byte PTP common header (IEEE 1588-2008 13.3, SPEC-audio.md 4.4).
/// </summary>
/// <param name="MessageType">Low nibble of byte 0.</param>
/// <param name="TransportSpecific">High nibble of byte 0; 1 on everything we send ("transportSpecific = 1 which is expected by nqptp").</param>
/// <param name="Version">Low nibble of byte 1 (versionPTP, 2).</param>
/// <param name="MessageLength">Total message length including the header.</param>
/// <param name="DomainNumber">Domain (0).</param>
/// <param name="Flags">flagField.</param>
/// <param name="CorrectionField">correctionField in 2^-16 ns.</param>
/// <param name="SourcePortIdentity">Sender's clock id and port number.</param>
/// <param name="SequenceId">Per-message-type sequence number.</param>
/// <param name="ControlField">Deprecated control field (0, 5 for Signaling/"other").</param>
/// <param name="LogMessageInterval">log2 of the message interval in seconds.</param>
public readonly record struct PtpHeader(
    PtpMessageType MessageType,
    byte TransportSpecific,
    byte Version,
    ushort MessageLength,
    byte DomainNumber,
    PtpFlags Flags,
    long CorrectionField,
    PtpPortIdentity SourcePortIdentity,
    ushort SequenceId,
    byte ControlField,
    sbyte LogMessageInterval)
{
    /// <summary>Header size.</summary>
    public const int Size = 34;

    /// <summary>PTP version 2.</summary>
    public const byte PtpVersion = 2;

    /// <summary>transportSpecific value nqptp expects (SPEC-audio.md 4.4).</summary>
    public const byte AppleTransportSpecific = 1;

    /// <summary>Writes the header.</summary>
    public void Write(Span<byte> destination)
    {
        destination[0] = (byte)((TransportSpecific << 4) | ((byte)MessageType & 0x0F));
        destination[1] = (byte)(Version & 0x0F);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], MessageLength);
        destination[4] = DomainNumber;
        destination[5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], (ushort)Flags);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], CorrectionField);
        destination[16..20].Clear();
        SourcePortIdentity.Write(destination[20..]);
        BinaryPrimitives.WriteUInt16BigEndian(destination[30..], SequenceId);
        destination[32] = ControlField;
        destination[33] = unchecked((byte)LogMessageInterval);
    }

    /// <summary>
    /// Parses a header; false when the datagram is shorter than 34 bytes, is not PTP version 2, or its
    /// <c>messageLength</c> is below the header size or beyond the datagram.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> message, out PtpHeader header)
    {
        header = default;
        if (message.Length < Size || (message[1] & 0x0F) != PtpVersion)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if (length < Size || length > message.Length)
        {
            return false;
        }

        header = new PtpHeader(
            (PtpMessageType)(message[0] & 0x0F),
            (byte)(message[0] >> 4),
            (byte)(message[1] & 0x0F),
            length,
            message[4],
            (PtpFlags)BinaryPrimitives.ReadUInt16BigEndian(message[6..]),
            BinaryPrimitives.ReadInt64BigEndian(message[8..]),
            PtpPortIdentity.Read(message[20..]),
            BinaryPrimitives.ReadUInt16BigEndian(message[30..]),
            message[32],
            unchecked((sbyte)message[33]));
        return true;
    }
}

/// <summary>A TLV (IEEE 1588-2008 14.1) found in a received message: type, and the value without the 4-byte type/length prefix.</summary>
/// <param name="type">tlvType.</param>
/// <param name="value">The value bytes (a slice of the datagram).</param>
public readonly ref struct PtpTlv(ushort type, ReadOnlySpan<byte> value)
{
    /// <summary>tlvType.</summary>
    public ushort Type { get; } = type;

    /// <summary>The value bytes.</summary>
    public ReadOnlySpan<byte> Value { get; } = value;

    /// <summary>For ORGANIZATION_EXTENSION TLVs: the 3-byte organizationId as an integer (e.g. <c>0x000D93</c> for Apple).</summary>
    public int OrganizationId => Value.Length >= 3 ? (Value[0] << 16) | (Value[1] << 8) | Value[2] : -1;

    /// <summary>For ORGANIZATION_EXTENSION TLVs: the 3-byte organizationSubType as an integer.</summary>
    public int OrganizationSubType => Value.Length >= 6 ? (Value[3] << 16) | (Value[4] << 8) | Value[5] : -1;
}

/// <summary>Enumerates the TLVs that follow a message body; stops at the first truncated TLV (<see cref="Malformed"/>).</summary>
public ref struct PtpTlvReader
{
    private ReadOnlySpan<byte> _remaining;

    /// <summary>Starts at <paramref name="tlvs"/>, the bytes after the fixed message body up to <c>messageLength</c>.</summary>
    public PtpTlvReader(ReadOnlySpan<byte> tlvs)
    {
        _remaining = tlvs;
        Malformed = false;
    }

    /// <summary>True once a TLV header or value ran past the end.</summary>
    public bool Malformed { get; private set; }

    /// <summary>Reads the next TLV; false at the end or on a malformed one.</summary>
    public bool TryRead(out PtpTlv tlv)
    {
        tlv = default;
        if (_remaining.IsEmpty)
        {
            return false;
        }

        if (_remaining.Length < PtpMessages.TlvHeaderSize)
        {
            Malformed = true;
            return false;
        }

        var type = BinaryPrimitives.ReadUInt16BigEndian(_remaining);
        var length = BinaryPrimitives.ReadUInt16BigEndian(_remaining[2..]);
        if (_remaining.Length < PtpMessages.TlvHeaderSize + length)
        {
            Malformed = true;
            return false;
        }

        tlv = new PtpTlv(type, _remaining.Slice(PtpMessages.TlvHeaderSize, length));
        _remaining = _remaining[(PtpMessages.TlvHeaderSize + length)..];
        return true;
    }
}

/// <summary>
/// Writers and parsers for the PTP messages the AirPlay grandmaster exchanges, with exactly the layouts libairptp sends
/// and nqptp/Apple receivers accept (SPEC-audio.md 4.4; behaviour from <c>[ot] libairptp/src/ptp_msg_handle.c</c> and
/// <c>ptp_definitions.h</c>, read for understanding and reimplemented from the spec's tables). Every writer returns the
/// number of bytes written into a caller-owned buffer, so the grandmaster's send loop allocates nothing per message.
/// </summary>
public static class PtpMessages
{
    /// <summary>The PTP event port (Sync, Delay_Req, Pdelay_*).</summary>
    public const int EventPort = 319;

    /// <summary>The PTP general port (Announce, Follow_Up, Delay_Resp, Signaling).</summary>
    public const int GeneralPort = 320;

    /// <summary>TLV type + length prefix.</summary>
    public const int TlvHeaderSize = 4;

    /// <summary>tlvType ORGANIZATION_EXTENSION.</summary>
    public const ushort TlvOrganizationExtension = 0x0003;

    /// <summary>tlvType PATH_TRACE.</summary>
    public const ushort TlvPathTrace = 0x0008;

    /// <summary>IEEE 802.1 organizationId <c>00-80-C2</c>.</summary>
    public const int OrganizationIeee8021 = 0x0080C2;

    /// <summary>Apple organizationId <c>00-0D-93</c>.</summary>
    public const int OrganizationApple = 0x000D93;

    /// <summary>802.1AS Follow_Up information TLV subtype <c>00-00-01</c>.</summary>
    public const int SubtypeFollowUpInformation = 0x000001;

    /// <summary>802.1AS message interval request TLV subtype <c>00-00-02</c> (gPTP Signaling).</summary>
    public const int SubtypeMessageIntervalRequest = 0x000002;

    /// <summary>Apple Signaling TLV subtype <c>00-00-01</c> (meaning unknown).</summary>
    public const int SubtypeAppleSignaling1 = 0x000001;

    /// <summary>Apple Follow_Up clock id TLV subtype <c>00-00-04</c>.</summary>
    public const int SubtypeAppleClockId = 0x000004;

    /// <summary>Apple Signaling TLV subtype <c>00-00-05</c> (meaning unknown).</summary>
    public const int SubtypeAppleSignaling5 = 0x000005;

    /// <summary>Announce: header + body (30) + PATH_TRACE TLV (12).</summary>
    public const int AnnounceSize = PtpHeader.Size + 30 + 12;

    /// <summary>Sync: header + originTimestamp.</summary>
    public const int SyncSize = PtpHeader.Size + PtpTimestamp.Size;

    /// <summary>Delay_Req: the same layout as a Sync (header + originTimestamp).</summary>
    public const int DelayReqSize = SyncSize;

    /// <summary>Follow_Up: header + preciseOriginTimestamp + 802.1AS TLV (32) + Apple clock id TLV (20).</summary>
    public const int FollowUpSize = PtpHeader.Size + PtpTimestamp.Size + FollowUpInformationTlvSize + AppleClockIdTlvSize;

    /// <summary>Delay_Resp, Pdelay_Resp, Pdelay_Resp_Follow_Up: header + timestamp + requestingPortIdentity.</summary>
    public const int DelayRespSize = PtpHeader.Size + PtpTimestamp.Size + PtpPortIdentity.Size;

    /// <summary>Pdelay_Req: header + originTimestamp + 10 reserved bytes.</summary>
    public const int PdelayReqSize = PtpHeader.Size + PtpTimestamp.Size + 10;

    /// <summary>Signaling: header + targetPortIdentity + two Apple TLVs (26 and 36 bytes).</summary>
    public const int SignalingSize = PtpHeader.Size + PtpPortIdentity.Size + AppleSignalingTlv1Size + AppleSignalingTlv2Size;

    /// <summary>The largest message we send.</summary>
    public const int MaxMessageSize = SignalingSize;

    /// <summary>Announce interval libairptp, nqptp and an amplifier use: log2 = 0, one per second (the Apple TV uses -2).</summary>
    public const sbyte AnnounceLogInterval = 0;

    /// <summary>Sync interval of iOS, the Apple TV, nqptp and libairptp: log2 = -3, 125 ms.</summary>
    public const sbyte SyncLogInterval = -3;

    /// <summary>Delay_Resp logMessageInterval libairptp writes.</summary>
    public const sbyte DelayRespLogInterval = -3;

    /// <summary>Signaling logMessageInterval of iOS (<c>0x80</c>, "not applicable").</summary>
    public const sbyte SignalingLogInterval = -128;

    /// <summary>Signaling <c>controlField</c> ("other message").</summary>
    public const byte ControlFieldOther = 5;

    /// <summary>grandmasterClockQuality as iOS sends it: class 6 (GPS-locked primary reference), accuracy 0x21 (100 ns), variance 0x436A.</summary>
    public const uint AppleClockQuality = 0x0621436A;

    /// <summary>grandmasterPriority1 and 2.</summary>
    public const byte DefaultPriority = 128;

    /// <summary>timeSource GPS.</summary>
    public const byte TimeSourceGps = 0x20;

    /// <summary>The 4 bytes iOS puts after the org/subtype of both Signaling TLVs; meaning unknown.</summary>
    private static ReadOnlySpan<byte> AppleSignalingValue => [0x00, 0x00, 0x03, 0x01];

    private const int FollowUpInformationTlvSize = TlvHeaderSize + 28;
    private const int AppleClockIdTlvSize = TlvHeaderSize + 16;
    private const int AppleSignalingTlv1Size = TlvHeaderSize + 22;
    private const int AppleSignalingTlv2Size = TlvHeaderSize + 32;
    private const int PathTraceTlvSize = TlvHeaderSize + 8;

    /// <summary>
    /// Builds a non-EUI-64 clock identity (IEEE 1588-2008 7.5.2.2.3): <c>0xFFFF &lt;&lt; 48 | 48-bit seed</c>, the shape
    /// libairptp uses (<c>[ot] libairptp/src/airptp.c:156-165</c>) and the session SETUP announces as <c>ClockID</c>.
    /// </summary>
    public static ulong ClockIdFromSeed(ulong seed) => 0xFFFF_0000_0000_0000UL | (seed & 0x0000_FFFF_FFFF_FFFFUL);

    /// <summary>A random 48-bit seed.</summary>
    public static ulong RandomSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes) & 0x0000_FFFF_FFFF_FFFFUL;
    }

    /// <summary>log2 of an interval in seconds, rounded, clamped to the sbyte range (IEEE 1588-2008 7.7.2.1).</summary>
    public static sbyte LogInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return sbyte.MinValue;
        }

        var log = Math.Round(Math.Log2(interval.TotalSeconds));
        return (sbyte)Math.Clamp(log, sbyte.MinValue + 1, sbyte.MaxValue);
    }

    /// <summary>
    /// Announce (SPEC-audio.md 4.4): flags 0x0408, <c>originTimestamp</c> 0 ("iOS just sends 0"), currentUtcOffset 0,
    /// priority1/2 128, clock quality 0x0621436A, grandmasterIdentity = our clock id, stepsRemoved 0, timeSource GPS, then a
    /// PATH_TRACE TLV (type 8, length 8) carrying the clock id again. Returns <see cref="AnnounceSize"/>.
    /// </summary>
    public static int WriteAnnounce(Span<byte> destination, ulong clockId, ushort sequenceId, sbyte logInterval = AnnounceLogInterval)
    {
        var message = destination[..AnnounceSize];
        Header(PtpMessageType.Announce, AnnounceSize, PtpFlags.AppleGeneral, clockId, sequenceId, 0, logInterval).Write(message);
        var body = message[PtpHeader.Size..];
        PtpTimestamp.Zero.Write(body);
        BinaryPrimitives.WriteInt16BigEndian(body[10..], 0); // currentUtcOffset
        body[12] = 0; // reserved
        body[13] = DefaultPriority; // grandmasterPriority1
        BinaryPrimitives.WriteUInt32BigEndian(body[14..], AppleClockQuality);
        body[18] = DefaultPriority; // grandmasterPriority2
        BinaryPrimitives.WriteUInt64BigEndian(body[19..], clockId); // grandmasterIdentity
        BinaryPrimitives.WriteUInt16BigEndian(body[27..], 0); // stepsRemoved
        body[29] = TimeSourceGps;
        var tlv = body[30..];
        WriteTlvHeader(tlv, TlvPathTrace, PathTraceTlvSize - TlvHeaderSize);
        BinaryPrimitives.WriteUInt64BigEndian(tlv[4..], clockId);
        return AnnounceSize;
    }

    /// <summary>Two-step Sync (SPEC-audio.md 4.4): flags 0x0608, log interval -3, <c>originTimestamp</c> 0 (the precise time follows in the Follow_Up). Returns <see cref="SyncSize"/>.</summary>
    public static int WriteSync(Span<byte> destination, ulong clockId, ushort sequenceId, sbyte logInterval = SyncLogInterval)
    {
        var message = destination[..SyncSize];
        Header(PtpMessageType.Sync, SyncSize, PtpFlags.AppleTwoStep, clockId, sequenceId, 0, logInterval).Write(message);
        PtpTimestamp.Zero.Write(message[PtpHeader.Size..]);
        return SyncSize;
    }

    /// <summary>
    /// Follow_Up (SPEC-audio.md 4.4): the Sync's sequence id, flags 0x0408, <c>preciseOriginTimestamp</c> = the Sync's send
    /// time, then the 802.1AS Follow_Up information TLV (org 00-80-C2, subtype 1, 22 zero bytes: cumulativeScaledRateOffset,
    /// gmTimeBaseIndicator, lastGmPhaseChange, scaledLastGmFreqChange) and the Apple TLV (org 00-0D-93, subtype 4) with
    /// the clock id, padded to 16 value bytes. Returns <see cref="FollowUpSize"/>.
    /// </summary>
    public static int WriteFollowUp(Span<byte> destination, ulong clockId, ushort sequenceId, PtpTimestamp preciseOriginTimestamp, sbyte logInterval = SyncLogInterval)
    {
        var message = destination[..FollowUpSize];
        Header(PtpMessageType.FollowUp, FollowUpSize, PtpFlags.AppleGeneral, clockId, sequenceId, 0, logInterval).Write(message);
        var body = message[PtpHeader.Size..];
        preciseOriginTimestamp.Write(body);
        var tlv1 = body[PtpTimestamp.Size..];
        WriteOrganizationTlv(tlv1[..FollowUpInformationTlvSize], OrganizationIeee8021, SubtypeFollowUpInformation);
        var tlv2 = tlv1[FollowUpInformationTlvSize..];
        WriteOrganizationTlv(tlv2[..AppleClockIdTlvSize], OrganizationApple, SubtypeAppleClockId);
        BinaryPrimitives.WriteUInt64BigEndian(tlv2[10..], clockId);
        return FollowUpSize;
    }

    /// <summary>
    /// Delay_Resp (SPEC-audio.md 4.4): flags 0x0608, the requester's sequence id, <c>receiveTimestamp</c> = when its
    /// Delay_Req arrived, <c>requestingPortIdentity</c> = its <c>sourcePortIdentity</c>. Domain and correction field are 0
    /// as libairptp writes them (<c>ptp_msg_handle.c:236-244, 424-436</c>) rather than the request's (IEEE 1588-2008 11.3.2
    /// would echo its correction field): the subset nqptp and Apple receivers demonstrably accept; the srcvers-366 speaker sends 0 for
    /// both anyway. Returns <see cref="DelayRespSize"/>.
    /// </summary>
    public static int WriteDelayResp(Span<byte> destination, ulong clockId, in PtpHeader request, PtpTimestamp receiveTimestamp, sbyte logInterval = DelayRespLogInterval)
    {
        var message = destination[..DelayRespSize];
        new PtpHeader(PtpMessageType.DelayResp, PtpHeader.AppleTransportSpecific, PtpHeader.PtpVersion, DelayRespSize, 0, PtpFlags.AppleTwoStep,
            0, new PtpPortIdentity(clockId, PtpPortIdentity.ApplePortNumber), request.SequenceId, 0, logInterval).Write(message);
        receiveTimestamp.Write(message[PtpHeader.Size..]);
        request.SourcePortIdentity.Write(message[(PtpHeader.Size + PtpTimestamp.Size)..]);
        return DelayRespSize;
    }

    /// <summary>
    /// Pdelay_Resp: flags 0x0608, <c>requestReceiptTimestamp</c> = t2, <c>requestingPortIdentity</c>. libairptp's version is a
    /// guess ("Haven't seen these messages from iOS", SPEC-audio.md 4.4); this follows IEEE 1588-2008 11.4.3 option b
    /// with the same header values. Returns <see cref="DelayRespSize"/>.
    /// </summary>
    public static int WritePdelayResp(Span<byte> destination, ulong clockId, in PtpHeader request, PtpTimestamp requestReceiptTimestamp)
    {
        var message = destination[..DelayRespSize];
        new PtpHeader(PtpMessageType.PdelayResp, PtpHeader.AppleTransportSpecific, PtpHeader.PtpVersion, DelayRespSize, 0, PtpFlags.AppleTwoStep,
            0, new PtpPortIdentity(clockId, PtpPortIdentity.ApplePortNumber), request.SequenceId, 0, SyncLogInterval).Write(message);
        requestReceiptTimestamp.Write(message[PtpHeader.Size..]);
        request.SourcePortIdentity.Write(message[(PtpHeader.Size + PtpTimestamp.Size)..]);
        return DelayRespSize;
    }

    /// <summary>Pdelay_Resp_Follow_Up: flags 0x0408, <c>responseOriginTimestamp</c> = t3 (IEEE 1588-2008 11.4.3 option b). Returns <see cref="DelayRespSize"/>.</summary>
    public static int WritePdelayRespFollowUp(Span<byte> destination, ulong clockId, in PtpHeader request, PtpTimestamp responseOriginTimestamp)
    {
        var message = destination[..DelayRespSize];
        new PtpHeader(PtpMessageType.PdelayRespFollowUp, PtpHeader.AppleTransportSpecific, PtpHeader.PtpVersion, DelayRespSize, 0, PtpFlags.AppleGeneral,
            0, new PtpPortIdentity(clockId, PtpPortIdentity.ApplePortNumber), request.SequenceId, 0, SyncLogInterval).Write(message);
        responseOriginTimestamp.Write(message[PtpHeader.Size..]);
        request.SourcePortIdentity.Write(message[(PtpHeader.Size + PtpTimestamp.Size)..]);
        return DelayRespSize;
    }

    /// <summary>
    /// Signaling as iOS sends it every second (SPEC-audio.md 4.4): flags 0x0408, controlField 5, logMessageInterval -128,
    /// <c>targetPortIdentity</c> (zero unless given: libairptp leaves it zero, iOS fills it in), then two Apple TLVs
    /// (subtypes 1 and 5) whose value after org/subtype is <c>00 00 03 01</c>, zero-padded to 22 and 32 bytes. Returns <see cref="SignalingSize"/>.
    /// </summary>
    public static int WriteSignaling(Span<byte> destination, ulong clockId, ushort sequenceId, PtpPortIdentity targetPortIdentity = default)
    {
        var message = destination[..SignalingSize];
        Header(PtpMessageType.Signaling, SignalingSize, PtpFlags.AppleGeneral, clockId, sequenceId, ControlFieldOther, SignalingLogInterval).Write(message);
        var body = message[PtpHeader.Size..];
        targetPortIdentity.Write(body);
        var tlv1 = body[PtpPortIdentity.Size..];
        WriteOrganizationTlv(tlv1[..AppleSignalingTlv1Size], OrganizationApple, SubtypeAppleSignaling1);
        AppleSignalingValue.CopyTo(tlv1[10..]);
        var tlv2 = tlv1[AppleSignalingTlv1Size..];
        WriteOrganizationTlv(tlv2[..AppleSignalingTlv2Size], OrganizationApple, SubtypeAppleSignaling5);
        AppleSignalingValue.CopyTo(tlv2[10..]);
        return SignalingSize;
    }

    /// <summary>Delay_Req as a slave sends it (fake receiver, tests): flags 0x0408, the given origin timestamp. Returns <see cref="DelayReqSize"/>.</summary>
    public static int WriteDelayReq(Span<byte> destination, ulong clockId, ushort portNumber, ushort sequenceId, PtpTimestamp originTimestamp)
    {
        var message = destination[..DelayReqSize];
        new PtpHeader(PtpMessageType.DelayReq, PtpHeader.AppleTransportSpecific, PtpHeader.PtpVersion, DelayReqSize, 0, PtpFlags.AppleGeneral,
            0, new PtpPortIdentity(clockId, portNumber), sequenceId, 1, 0x7F).Write(message);
        originTimestamp.Write(message[PtpHeader.Size..]);
        return DelayReqSize;
    }

    /// <summary>Pdelay_Req (tests): the given origin timestamp and 10 reserved bytes. Returns <see cref="PdelayReqSize"/>.</summary>
    public static int WritePdelayReq(Span<byte> destination, ulong clockId, ushort portNumber, ushort sequenceId, PtpTimestamp originTimestamp)
    {
        var message = destination[..PdelayReqSize];
        new PtpHeader(PtpMessageType.PdelayReq, PtpHeader.AppleTransportSpecific, PtpHeader.PtpVersion, PdelayReqSize, 0, PtpFlags.Unicast,
            0, new PtpPortIdentity(clockId, portNumber), sequenceId, 5, 0x7F).Write(message);
        originTimestamp.Write(message[PtpHeader.Size..]);
        message[(PtpHeader.Size + PtpTimestamp.Size)..].Clear();
        return PdelayReqSize;
    }

    /// <summary>The 10-byte timestamp right after the header (originTimestamp, preciseOriginTimestamp, receiveTimestamp, ...); false when too short.</summary>
    public static bool TryReadTimestamp(ReadOnlySpan<byte> message, out PtpTimestamp timestamp)
    {
        timestamp = default;
        if (message.Length < PtpHeader.Size + PtpTimestamp.Size)
        {
            return false;
        }

        timestamp = PtpTimestamp.Read(message[PtpHeader.Size..]);
        return true;
    }

    /// <summary>The requestingPortIdentity of a Delay_Resp/Pdelay_Resp/Pdelay_Resp_Follow_Up; false when too short.</summary>
    public static bool TryReadRequestingPortIdentity(ReadOnlySpan<byte> message, out PtpPortIdentity identity)
    {
        identity = default;
        if (message.Length < DelayRespSize)
        {
            return false;
        }

        identity = PtpPortIdentity.Read(message[(PtpHeader.Size + PtpTimestamp.Size)..]);
        return true;
    }

    /// <summary>The grandmasterIdentity of an Announce; false when too short.</summary>
    public static bool TryReadGrandmasterIdentity(ReadOnlySpan<byte> message, out ulong grandmasterIdentity)
    {
        grandmasterIdentity = 0;
        if (message.Length < PtpHeader.Size + 30)
        {
            return false;
        }

        grandmasterIdentity = BinaryPrimitives.ReadUInt64BigEndian(message[(PtpHeader.Size + 19)..]);
        return true;
    }

    /// <summary>
    /// The TLV area of a message: the bytes after the fixed body of its type up to <c>messageLength</c>; empty for types
    /// without TLVs or when the message is too short.
    /// </summary>
    public static ReadOnlySpan<byte> TlvArea(ReadOnlySpan<byte> message, in PtpHeader header)
    {
        var bodyEnd = header.MessageType switch
        {
            PtpMessageType.Announce => PtpHeader.Size + 30,
            PtpMessageType.FollowUp => PtpHeader.Size + PtpTimestamp.Size,
            PtpMessageType.Signaling => PtpHeader.Size + PtpPortIdentity.Size,
            _ => -1,
        };
        var end = Math.Min(header.MessageLength, message.Length);
        return bodyEnd < 0 || end <= bodyEnd ? [] : message[bodyEnd..end];
    }

    private static PtpHeader Header(PtpMessageType type, int length, PtpFlags flags, ulong clockId, ushort sequenceId, byte controlField, sbyte logInterval) =>
        new(type, PtpHeader.AppleTransportSpecific, PtpHeader.PtpVersion, (ushort)length, 0, flags, 0,
            new PtpPortIdentity(clockId, PtpPortIdentity.ApplePortNumber), sequenceId, controlField, logInterval);

    private static void WriteTlvHeader(Span<byte> destination, ushort type, int valueLength)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, type);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)valueLength);
    }

    /// <summary>Writes an ORGANIZATION_EXTENSION TLV filling <paramref name="tlv"/>: header, org id, subtype, zero data.</summary>
    private static void WriteOrganizationTlv(Span<byte> tlv, int organizationId, int subtype)
    {
        WriteTlvHeader(tlv, TlvOrganizationExtension, tlv.Length - TlvHeaderSize);
        tlv[4] = (byte)(organizationId >> 16);
        tlv[5] = (byte)(organizationId >> 8);
        tlv[6] = (byte)organizationId;
        tlv[7] = (byte)(subtype >> 16);
        tlv[8] = (byte)(subtype >> 8);
        tlv[9] = (byte)subtype;
        tlv[10..].Clear();
    }
}
