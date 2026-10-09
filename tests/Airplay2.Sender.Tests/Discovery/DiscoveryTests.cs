using System.Net;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Xunit;

namespace Airplay2.Sender.Tests.Discovery;

/// <summary>TXT parsing and the 1.6 decision procedure against the two LAN receivers captured on 2026-10-09.</summary>
public class DiscoveryTests
{
    private static readonly string[] SpeakerAAirplay =
    [
        "acl=0", "deviceid=02:1A:2B:3C:4D:5E", "features=0x445F8A00,0x1C340", "rsf=0x0", "fv=p20.1.1.36.0x8b4c480", "flags=0x404",
        "model=Test Speaker ABCD", "manufacturer=Lautsprecher SpeakerA GmbH", "serialNumber=00000000-0000-0000-0000-000000000000",
        "protovers=1.1", "srcvers=366.0", "pi=02:1A:2B:3C:4D:5E", "gid=02:1A:2B:3C:4D:5E", "gcgl=0",
        "pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
    ];

    private static readonly string[] SpeakerARaop =
    [
        "cn=0,1", "da=true", "et=0,4", "ft=0x445F8A00,0x1C340", "fv=p20.1.1.36.0x8b4c480", "md=0,1,2", "am=Test Speaker ABCD",
        "sf=0x404", "tp=UDP", "vn=65537", "vs=366.0", "pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
    ];

    private static readonly string[] LgAirplay =
    [
        "features=0x7F8AD0,0x38BCF46", "flags=0x644", "srcvers=377.40.00", "rsf=0x3", "fex=0Ip/AEbPiwNACA", "at=0x1",
        "pi=02:4C:47:00:00:01", "psi=00000000-0000-0000-0000-024C47000001", "gid=00000000-0000-0000-0000-024C47000001",
        "model=TVMODEL0000.DEUQDJP", "pk=REDACTED-RECEIVER-KEY",
    ];

    [Fact]
    public void SpeakerA_feature_word_decodes_to_the_documented_bits()
    {
        var txt = AirplayTxtRecord.Parse(SpeakerAAirplay);
        Assert.Equal((AirplayFeatures)0x0001C340445F8A00UL, txt.Features);
        Assert.Equal(new[] { 9, 11, 15, 16, 17, 18, 19, 20, 22, 26, 30, 38, 40, 41, 46, 47, 48 }, AirplayTxtValues.Bits(txt.Features));
        Assert.Equal(AirplayStatusFlags.AudioLink | AirplayStatusFlags.HomeKitAccessControl, txt.Flags);
        Assert.Equal("0x445F8A00,0x1C340", AirplayTxtValues.FormatFeatures(txt.Features));
        Assert.Equal(0, txt.AccessControlLevel);
        Assert.Equal("02:1A:2B:3C:4D:5E", txt.DeviceId);
        Assert.Equal("02:1A:2B:3C:4D:5E", txt.PairingId);
        Assert.Equal("366.0", txt.SourceVersion);
        Assert.Equal(32, txt.PublicKey!.Length);
        Assert.False(txt.GroupContainsGroupLeader);
        Assert.True(txt.Has(AirplayFeatures.TransientPairing | AirplayFeatures.PtpClock));
        Assert.False(txt.Has(AirplayFeatures.NtpClock));

        // the same value as the decimal in GET /info
        Assert.Equal(txt.Features, AirplayTxtValues.ParseFeatures("496155769145856"));
    }

    [Fact]
    public void SpeakerA_raop_record_parses()
    {
        var raop = RaopTxtRecord.Parse(SpeakerARaop);
        Assert.Equal(new[] { 0, 4 }, raop.EncryptionTypes);
        Assert.Equal(new[] { 0, 1 }, raop.Codecs);
        Assert.Equal(new[] { 0, 1, 2 }, raop.MetadataTypes);
        Assert.Equal("UDP", raop.Transport);
        Assert.Equal((AirplayFeatures)0x0001C340445F8A00UL, raop.Features);
        Assert.Equal(44100, raop.SampleRate);
        Assert.Equal(16, raop.SampleSize);
        Assert.Equal(2, raop.Channels);
        Assert.False(raop.PasswordRequired);
    }

    [Fact]
    public void SpeakerA_capabilities_transient_realtime_alac_all_metadata()
    {
        var caps = ReceiverCapabilities.From(AirplayTxtRecord.Parse(SpeakerAAirplay), RaopTxtRecord.Parse(SpeakerARaop));
        Assert.True(caps.IsAirPlay2);
        Assert.Equal(PairingRequirement.Transient, caps.Pairing);
        Assert.True(caps.SupportsRealtimeStream);
        Assert.True(caps.SupportsBufferedStream);
        Assert.True(caps.HasPtpClock);
        Assert.False(caps.PrefersNtp);
        Assert.True(caps.SupportsAlac);
        Assert.True(caps.WantsTextMetadata);
        Assert.True(caps.WantsArtwork);
        Assert.True(caps.WantsProgress);
        Assert.False(caps.UsesNowPlayingCommands);
        Assert.False(caps.MayNeedPairSetupWithAuth);
    }

    [Fact]
    public void Lg_tv_requires_pin_pairing_and_flags_bit_51()
    {
        var txt = AirplayTxtRecord.Parse(LgAirplay);
        Assert.Equal((AirplayFeatures)0x038BCF46007F8AD0UL, txt.Features);
        Assert.Equal(new[] { 4, 6, 7, 9, 11, 15, 16, 17, 18, 19, 20, 21, 22, 33, 34, 38, 40, 41, 42, 43, 46, 47, 48, 49, 51, 55, 56, 57 }, AirplayTxtValues.Bits(txt.Features));
        Assert.Equal((AirplayStatusFlags)0x644, txt.Flags);
        var caps = ReceiverCapabilities.From(txt, null);
        Assert.True(caps.IsAirPlay2);
        Assert.Equal(PairingRequirement.Pin, caps.Pairing);
        Assert.True(caps.MayNeedPairSetupWithAuth);
        Assert.True(caps.SupportsRealtimeStream);
        Assert.Null(txt.PublicKey); // odd-length hex in the fixture is rejected, not thrown
    }

    [Fact]
    public void Access_control_and_password_flags_are_honoured()
    {
        var disabled = AirplayTxtRecord.Parse(["features=0x445F8A00,0x1C340", "flags=0x404", "acl=1"]);
        Assert.Equal(PairingRequirement.Disabled, ReceiverCapabilities.From(disabled, null).Pairing);
        var password = AirplayTxtRecord.Parse(["features=0x445F8A00,0x1C340", "flags=0x84"]);
        Assert.Equal(PairingRequirement.Password, ReceiverCapabilities.From(password, null).Pairing);
        var airplay1 = AirplayTxtRecord.Parse(["features=0x5A7FFFF7", "flags=0x4"]);
        Assert.False(ReceiverCapabilities.From(airplay1, null).IsAirPlay2);
    }

    [Fact]
    public void Discovered_receiver_exposes_addresses_by_family()
    {
        var r = new DiscoveredReceiver("02:1A:2B:3C:4D:5E", "Test Speaker ABCD", [IPAddress.Parse("192.0.2.26"), IPAddress.Parse("2001:db8:1:30:1a2b:3cff:fe4d:5e01")], 7000, AirplayTxtRecord.Parse(SpeakerAAirplay), RaopTxtRecord.Parse(SpeakerARaop), DateTimeOffset.UtcNow);
        Assert.Equal(IPAddress.Parse("192.0.2.26"), r.AddressOf(System.Net.Sockets.AddressFamily.InterNetwork));
        Assert.Equal(PairingRequirement.Transient, r.Capabilities.Pairing);
    }

    [Theory]
    [InlineData(0, -144.0)]
    [InlineData(1, -29.7)]
    [InlineData(50, -15.0)]
    [InlineData(100, 0.0)]
    public void Volume_level_to_db(int level, double db)
    {
        Assert.Equal(db, VolumeMapping.LevelToDb(level), 6);
        Assert.Equal(level, VolumeMapping.DbToLevel(db));
    }

    [Fact]
    public void Volume_parameter_formatting_and_parsing()
    {
        Assert.Equal("volume: -15.000000\r\n", VolumeMapping.FormatParameter(-15));
        Assert.Equal("volume: -144.000000\r\n", VolumeMapping.FormatParameter(VolumeMapping.LevelToDb(0)));
        Assert.Equal(-18.0, VolumeMapping.ParseParameter("volume: -18.0\r\n"));
        Assert.Equal(-15.0, VolumeMapping.ParseParameter("\r\nvolume: -15.000000\r\n")); // shairport-sync's GET_PARAMETER reply has a leading blank line (rtsp.c:3332)
        Assert.Equal(0.0, VolumeMapping.ParseParameter("volume: 0\r\n")); // airplay2-receiver with volume management disabled (ap2-receiver.py:655)
        Assert.Null(VolumeMapping.ParseParameter("progress: 1/2/3"));
        Assert.Equal(0, VolumeMapping.DbToLevel(-144));
        Assert.Equal(0, VolumeMapping.DbToLevel(-31));
    }

    [Fact]
    public void Ntp_timestamps_round_trip_and_master_clock_is_monotonic()
    {
        var unixNanos = 1_791_000_000_123_456_789L;
        var ntp = NtpTimestamp.FromUnixNanos(unixNanos);
        Assert.Equal(1_791_000_000UL + NtpTimestamp.UnixEpochOffset, NtpTimestamp.Seconds(ntp));
        Assert.InRange(NtpTimestamp.ToUnixNanos(ntp), unixNanos - 1, unixNanos + 1);

        var clock = new MasterClock(unixNanos);
        var a = clock.NowNanos;
        var b = clock.NowNanos;
        Assert.True(b >= a);
        Assert.InRange(clock.UnixNanos - unixNanos, 0, 5_000_000_000L);
        Assert.Equal((ulong)clock.UnixNanos / 1_000_000_000UL + NtpTimestamp.UnixEpochOffset, NtpTimestamp.Seconds(clock.NtpNow), 1UL);
    }
}
