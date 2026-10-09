using Airplay2.Sender.Streams;
using Xunit;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>
/// Byte-exact layouts of every PTP message the grandmaster sends (SPEC-audio.md 4.4, WORK-PLAN ptp-master "Verify"). The
/// expected bytes are written out field by field from the spec's table (header: <c>messageType | 0x10</c>, version 2,
/// length, domain 0, flags, correction 0, sourcePortIdentity = clock id + <c>80 05</c>, sequence, control, log interval).
/// </summary>
public class PtpMessageTests
{
    private const ulong Clock = 0xFFFF_1234_5678_9ABCUL;
    private const string ClockHex = "FFFF123456789ABC";
    private const string SourcePort = ClockHex + "8005";

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    private static string Zeros(int bytes) => new('0', bytes * 2);

    [Fact]
    public void Announce_layout()
    {
        var buffer = new byte[200];
        var length = PtpMessages.WriteAnnounce(buffer, Clock, 0x0102);
        var expected = Hex(
            "1B 02 004C 00 00 0408" + Zeros(8) + Zeros(4) + SourcePort + "0102 00 00" // header, log interval 0
            + Zeros(10) // originTimestamp 0 ("iOS just sends 0")
            + "0000 00 80 0621436A 80" + ClockHex + "0000 20" // utcOffset, reserved, p1, quality, p2, gm id, stepsRemoved, GPS
            + "0008 0008" + ClockHex); // PATH_TRACE TLV with the clock id
        Assert.Equal(76, length);
        Assert.Equal(PtpMessages.AnnounceSize, length);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(buffer, 0, length));
    }

    [Fact]
    public void Sync_layout_is_two_step_with_zero_origin()
    {
        var buffer = new byte[100];
        var length = PtpMessages.WriteSync(buffer, Clock, 0xBEEF);
        var expected = Hex("10 02 002C 00 00 0608" + Zeros(8) + Zeros(4) + SourcePort + "BEEF 00 FD" + Zeros(10));
        Assert.Equal(44, length);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(buffer, 0, length));
    }

    [Fact]
    public void Follow_up_layout_with_8021as_and_apple_tlvs()
    {
        var buffer = new byte[200];
        var t1 = PtpTimestamp.FromNanoseconds(1_700_000_000_123_456_789UL);
        var length = PtpMessages.WriteFollowUp(buffer, Clock, 0x0102, t1);
        var expected = Hex(
            "18 02 0060 00 00 0408" + Zeros(8) + Zeros(4) + SourcePort + "0102 00 FD"
            + "0000 6553F100 075BCD15" // preciseOriginTimestamp: 1700000000 s, 123456789 ns
            + "0003 001C 0080C2 000001" + Zeros(22) // 802.1AS Follow_Up information TLV, all zero
            + "0003 0010 000D93 000004" + ClockHex + "0000"); // Apple clock id TLV, padded to 16 value bytes
        Assert.Equal(96, length);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(buffer, 0, length));
    }

    [Fact]
    public void Delay_resp_answers_the_request_identity_and_sequence()
    {
        var request = new byte[PtpMessages.DelayReqSize];
        PtpMessages.WriteDelayReq(request, 0x501E2DFFFE6DB69BUL, 1, 0x0007, PtpTimestamp.Zero);
        request[4] = 0x05; // domainNumber 5 and correctionField 0x40: answered with 0 for both, as libairptp does
        request[15] = 0x40;
        Assert.True(PtpHeader.TryRead(request, out var header));
        var buffer = new byte[100];
        var length = PtpMessages.WriteDelayResp(buffer, Clock, header, new PtpTimestamp(0x0001_0000_0002UL, 999_999_999));
        var expected = Hex(
            "19 02 0036 00 00 0608 0000000000000000" + Zeros(4) + SourcePort + "0007 00 FD"
            + "0001 00000002 3B9AC9FF" // receiveTimestamp with seconds above 32 bits
            + "501E2DFFFE6DB69B 0001"); // requestingPortIdentity
        Assert.Equal(54, length);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(buffer, 0, length));
    }

    [Fact]
    public void Signaling_layout_with_two_apple_tlvs()
    {
        var buffer = new byte[200];
        var length = PtpMessages.WriteSignaling(buffer, Clock, 0x0003);
        var expected = Hex(
            "1C 02 006A 00 00 0408" + Zeros(8) + Zeros(4) + SourcePort + "0003 05 80" // controlField 5, log interval -128
            + Zeros(10) // targetPortIdentity zero (libairptp)
            + "0003 0016 000D93 000001 00000301" + Zeros(12)
            + "0003 0020 000D93 000005 00000301" + Zeros(22));
        Assert.Equal(106, length);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(buffer, 0, length));
    }

    [Fact]
    public void Signaling_can_target_a_peer()
    {
        var buffer = new byte[200];
        PtpMessages.WriteSignaling(buffer, Clock, 1, new PtpPortIdentity(0x0102030405060708UL, 0x8001));
        Assert.Equal("01020304050607088001", Convert.ToHexString(buffer, 34, 10));
    }

    [Fact]
    public void Pdelay_answers_follow_ieee_option_b()
    {
        var request = new byte[PtpMessages.PdelayReqSize];
        PtpMessages.WritePdelayReq(request, 0x0A0B0C0D0E0F1011UL, 2, 0x0042, PtpTimestamp.Zero);
        Assert.True(PtpHeader.TryRead(request, out var header));
        Assert.Equal(PtpMessageType.PdelayReq, header.MessageType);
        var buffer = new byte[100];
        var length = PtpMessages.WritePdelayResp(buffer, Clock, header, new PtpTimestamp(5, 6));
        Assert.Equal("13 02 0036 00 00 0608".Replace(" ", string.Empty, StringComparison.Ordinal), Convert.ToHexString(buffer, 0, 8));
        Assert.Equal("0042", Convert.ToHexString(buffer, 30, 2));
        Assert.Equal("0A0B0C0D0E0F10110002", Convert.ToHexString(buffer, 44, 10));
        length = PtpMessages.WritePdelayRespFollowUp(buffer, Clock, header, new PtpTimestamp(5, 7));
        Assert.Equal(54, length);
        Assert.Equal("1A0200360000" + "0408", Convert.ToHexString(buffer, 0, 8));
        Assert.Equal("0000" + "00000005" + "00000007", Convert.ToHexString(buffer, 34, 10));
    }

    [Fact]
    public void Header_round_trip_and_rejections()
    {
        var buffer = new byte[PtpMessages.FollowUpSize];
        PtpMessages.WriteFollowUp(buffer, Clock, 77, new PtpTimestamp(1, 2), logInterval: -3);
        Assert.True(PtpHeader.TryRead(buffer, out var header));
        Assert.Equal(PtpMessageType.FollowUp, header.MessageType);
        Assert.Equal(PtpHeader.AppleTransportSpecific, header.TransportSpecific);
        Assert.Equal(2, header.Version);
        Assert.Equal(96, header.MessageLength);
        Assert.Equal(PtpFlags.AppleGeneral, header.Flags);
        Assert.Equal(new PtpPortIdentity(Clock, 0x8005), header.SourcePortIdentity);
        Assert.Equal(77, header.SequenceId);
        Assert.Equal(-3, header.LogMessageInterval);

        Assert.False(PtpHeader.TryRead(buffer.AsSpan(0, 33), out _)); // too short
        var v1 = (byte[])buffer.Clone();
        v1[1] = 1;
        Assert.False(PtpHeader.TryRead(v1, out _)); // PTPv1
        var longer = (byte[])buffer.Clone();
        longer[2] = 0x10; // messageLength 0x1060 beyond the datagram
        Assert.False(PtpHeader.TryRead(longer, out _));
    }

    [Fact]
    public void Tlv_reader_finds_both_follow_up_tlvs_and_flags_truncation()
    {
        var buffer = new byte[PtpMessages.FollowUpSize];
        PtpMessages.WriteFollowUp(buffer, Clock, 1, PtpTimestamp.Zero);
        Assert.True(PtpHeader.TryRead(buffer, out var header));
        var reader = new PtpTlvReader(PtpMessages.TlvArea(buffer, header));
        Assert.True(reader.TryRead(out var first));
        Assert.Equal(PtpMessages.TlvOrganizationExtension, first.Type);
        Assert.Equal(PtpMessages.OrganizationIeee8021, first.OrganizationId);
        Assert.Equal(PtpMessages.SubtypeFollowUpInformation, first.OrganizationSubType);
        Assert.Equal(28, first.Value.Length);
        Assert.True(reader.TryRead(out var second));
        Assert.Equal(PtpMessages.OrganizationApple, second.OrganizationId);
        Assert.Equal(PtpMessages.SubtypeAppleClockId, second.OrganizationSubType);
        Assert.Equal(ClockHex, Convert.ToHexString(second.Value.Slice(6, 8)));
        Assert.False(reader.TryRead(out _));
        Assert.False(reader.Malformed);

        var truncated = new PtpTlvReader(PtpMessages.TlvArea(buffer, header)[..40]);
        Assert.True(truncated.TryRead(out _));
        Assert.False(truncated.TryRead(out _));
        Assert.True(truncated.Malformed);
    }

    [Fact]
    public void Announce_grandmaster_and_tlv_area()
    {
        var buffer = new byte[PtpMessages.AnnounceSize];
        PtpMessages.WriteAnnounce(buffer, Clock, 9);
        Assert.True(PtpMessages.TryReadGrandmasterIdentity(buffer, out var gm));
        Assert.Equal(Clock, gm);
        Assert.True(PtpHeader.TryRead(buffer, out var header));
        var reader = new PtpTlvReader(PtpMessages.TlvArea(buffer, header));
        Assert.True(reader.TryRead(out var path));
        Assert.Equal(PtpMessages.TlvPathTrace, path.Type);
        Assert.Equal(ClockHex, Convert.ToHexString(path.Value));
    }

    [Theory]
    [InlineData(0x0000_1234_5678_9ABCUL, 0xFFFF_1234_5678_9ABCUL)]
    [InlineData(0xABCD_1234_5678_9ABCUL, 0xFFFF_1234_5678_9ABCUL)] // only 48 bits of the seed are used
    [InlineData(0UL, 0xFFFF_0000_0000_0000UL)]
    public void Clock_id_is_ffff_and_48_bit_seed(ulong seed, ulong expected) => Assert.Equal(expected, PtpMessages.ClockIdFromSeed(seed));

    [Fact]
    public void Random_seed_is_48_bits()
    {
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(0UL, PtpMessages.RandomSeed() >> 48);
        }
    }

    [Theory]
    [InlineData(125, -3)]
    [InlineData(250, -2)]
    [InlineData(1000, 0)]
    [InlineData(2000, 1)]
    public void Log_interval(int milliseconds, int expected) => Assert.Equal(expected, PtpMessages.LogInterval(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void Timestamp_round_trip()
    {
        var ts = PtpTimestamp.FromNanoseconds(0x0001_0000_0000UL * 1_000_000_000UL + 5);
        Assert.Equal(0x0001_0000_0000UL, ts.Seconds);
        Assert.Equal(5u, ts.Nanoseconds);
        var buffer = new byte[10];
        ts.Write(buffer);
        Assert.Equal("00010000000000000005", Convert.ToHexString(buffer));
        Assert.Equal(ts, PtpTimestamp.Read(buffer));
        Assert.Equal(0x0001_0000_0000UL * 1_000_000_000UL + 5, ts.TotalNanoseconds);
    }
}
