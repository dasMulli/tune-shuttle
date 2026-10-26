using System.Text;
using Airplay2.Receiver.Discovery;
using Airplay2.Sender.Discovery;
using Xunit;

namespace Airplay2.Receiver.Tests.Discovery;

/// <summary>The TXT records of both services (SPEC-receiver.md 3.1, R3, R7, R15), byte for byte.</summary>
public class ReceiverTxtRecordsTests
{
    /// <summary>A synthetic identity: locally administered MAC, made-up UUID, seed 0x01..0x20.</summary>
    internal static readonly ReceiverIdentity Identity = new(
        "02:00:5E:10:20:30",
        "6B1C8F0A-2D3E-4F50-8A6B-7C8D9E0F1A2B",
        Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    internal static AirplayReceiverOptions Options(string? password = null, string name = "Test Receiver") =>
        new() { Identity = Identity, Name = name, Password = password };

    [Fact]
    public void Airplay_txt_is_the_3_1_set_in_order()
    {
        var options = Options();
        var txt = ReceiverTxtRecords.Airplay(options, ReceiverFeatures.Default, ReceiverTxtRecords.TxtFlagsFor(options));

        string[] expected =
        [
            "acl=0",
            "deviceid=02:00:5E:10:20:30",
            "features=0x405FCA00,0x18340",
            "flags=0x4",
            "gid=02:00:5E:10:20:30",
            "gcgl=0",
            "model=TuneShuttle1,1",
            "manufacturer=TuneShuttle",
            "protovers=1.1",
            "pi=02:00:5E:10:20:30",
            "psi=6B1C8F0A-2D3E-4F50-8A6B-7C8D9E0F1A2B",
            "pk=" + Identity.PublicKeyHex,
            "srcvers=366.0",
            "fex=AMpfQECDAQA",
        ];
        Assert.Equal(expected, txt);
        Assert.Matches("^pk=[0-9a-f]{64}$", txt[11]);
    }

    [Fact]
    public void Fex_is_the_little_endian_word_without_padding()
    {
        // 0x00018340405FCA00 little endian: 00 CA 5F 40 40 83 01 00.
        Assert.Equal(Convert.ToBase64String([0x00, 0xCA, 0x5F, 0x40, 0x40, 0x83, 0x01, 0x00]).TrimEnd('='), ReceiverFeatures.ToFex(ReceiverFeatures.Default));
        Assert.Equal("AMpfQECDAQA", ReceiverFeatures.ToFex(ReceiverFeatures.Default));
        Assert.Equal("AMpPQECDAQA", ReceiverFeatures.ToFex(ReceiverFeatures.AlacOnly));
    }

    [Fact]
    public void Raop_txt_is_the_3_1_set_in_order()
    {
        var options = Options();
        var txt = ReceiverTxtRecords.Raop(options, ReceiverFeatures.Default, ReceiverTxtRecords.TxtFlagsFor(options));

        string[] expected =
        [
            "cn=0,1,2",
            "da=true",
            "et=0",
            "ft=0x405FCA00,0x18340",
            "md=0,1,2",
            "am=TuneShuttle1,1",
            "pk=" + Identity.PublicKeyHex,
            "sf=0x4",
            "tp=UDP",
            "vn=65537",
            "vs=366.0",
            "ss=16",
            "sr=44100",
            "ch=2",
            "txtvers=1",
            "pw=false",
        ];
        Assert.Equal(expected, txt);
    }

    [Fact]
    public void Password_sets_flags_0x84_and_pw_true_in_both_services()
    {
        var options = Options(password: "secret");
        var flags = ReceiverTxtRecords.TxtFlagsFor(options);
        Assert.Equal(0x84u, (uint)flags);

        var airplay = ReceiverTxtRecords.Airplay(options, ReceiverFeatures.Default, flags);
        var raop = ReceiverTxtRecords.Raop(options, ReceiverFeatures.Default, flags);
        Assert.Contains("flags=0x84", airplay);
        Assert.Contains("sf=0x84", raop);
        Assert.Contains("pw=true", raop);
        Assert.DoesNotContain("pw=false", raop);

        // An empty password is the open mode (R7).
        var open = Options(password: string.Empty);
        Assert.Equal(0x4u, (uint)ReceiverTxtRecords.TxtFlagsFor(open));
        Assert.Contains("pw=false", ReceiverTxtRecords.Raop(open, ReceiverFeatures.Default, ReceiverTxtRecords.TxtFlagsFor(open)));
    }

    [Fact]
    public void Session_active_bit_never_reaches_the_txt_records()
    {
        var options = Options(password: "secret");
        var flags = ReceiverFeatures.StatusFlagsFor(passwordSet: true, sessionActive: true);

        Assert.Contains("flags=0x84", ReceiverTxtRecords.Airplay(options, ReceiverFeatures.Default, flags));
        Assert.Contains("sf=0x84", ReceiverTxtRecords.Raop(options, ReceiverFeatures.Default, flags));
    }

    [Fact]
    public void Alac_only_variant_is_consistent_in_features_ft_fex_and_cn()
    {
        var options = Options();
        var flags = ReceiverTxtRecords.TxtFlagsFor(options);
        var airplay = ReceiverTxtRecords.Airplay(options, ReceiverFeatures.AlacOnly, flags);
        var raop = ReceiverTxtRecords.Raop(options, ReceiverFeatures.AlacOnly, flags);

        Assert.Contains("features=0x404FCA00,0x18340", airplay);
        Assert.Contains("fex=AMpPQECDAQA", airplay);
        Assert.Contains("ft=0x404FCA00,0x18340", raop);
        Assert.Equal("cn=0,1", raop[0]);
    }

    [Fact]
    public void Raop_instance_name_is_the_compact_device_id_at_the_name()
    {
        Assert.Equal("02005E102030@Test Receiver", ReceiverTxtRecords.RaopInstanceName(Identity, "Test Receiver"));
    }

    [Fact]
    public void Advertisement_combines_names_port_and_both_txt_sets()
    {
        var options = Options(password: "secret", name: "Kitchen Test");
        var advertisement = ReceiverTxtRecords.Advertisement(options, 7000, ReceiverFeatures.Default);

        Assert.Equal("Kitchen Test", advertisement.Name);
        Assert.Equal("02005E102030@Kitchen Test", advertisement.RaopInstanceName);
        Assert.Equal(7000, advertisement.Port);
        Assert.Equal(ReceiverTxtRecords.Airplay(options, ReceiverFeatures.Default, ReceiverTxtRecords.TxtFlagsFor(options)), advertisement.AirplayTxt);
        Assert.Equal(ReceiverTxtRecords.Raop(options, ReceiverFeatures.Default, ReceiverTxtRecords.TxtFlagsFor(options)), advertisement.RaopTxt);
        Assert.True(advertisement.SameRecordsAs(ReceiverTxtRecords.Advertisement(options, 7000, ReceiverFeatures.Default)));
        Assert.False(advertisement.SameRecordsAs(ReceiverTxtRecords.Advertisement(options with { Password = null }, 7000, ReceiverFeatures.Default)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReceiverTxtRecords.Advertisement(options, 0, ReceiverFeatures.Default));
    }

    [Fact]
    public void Sender_side_parsers_read_our_records()
    {
        var options = Options(password: "secret");
        var flags = ReceiverTxtRecords.TxtFlagsFor(options);
        var airplay = AirplayTxtRecord.Parse(ReceiverTxtRecords.Airplay(options, ReceiverFeatures.Default, flags));
        var raop = RaopTxtRecord.Parse(ReceiverTxtRecords.Raop(options, ReceiverFeatures.Default, flags));

        Assert.Equal(Identity.PublicKey, airplay.PublicKey);
        Assert.Equal(Identity.PublicKey, raop.PublicKey);
        Assert.Equal(new[] { 0, 1, 2 }, raop.Codecs);
        Assert.Equal((AirplayFeatures)ReceiverFeatures.Default, airplay.Features);
        Assert.Equal((AirplayFeatures)ReceiverFeatures.Default, raop.Features);
        Assert.Equal(flags, airplay.Flags);
        Assert.Equal(flags, raop.Flags);
        Assert.True(airplay.Flags.HasFlag(AirplayStatusFlags.PasswordRequired));
        Assert.True(raop.PasswordRequired);
        Assert.Equal(Identity.DeviceId, airplay.DeviceId);
        Assert.Equal(44100, raop.SampleRate);
    }

    [Fact]
    public void Blob_is_length_prefixed_utf8_entries()
    {
        var blob = ReceiverTxtRecords.ToBlob(["acl=0", "name=Küche"]);

        var expected = new List<byte> { 5 };
        expected.AddRange(Encoding.ASCII.GetBytes("acl=0"));
        var name = Encoding.UTF8.GetBytes("name=Küche");
        expected.Add((byte)name.Length);
        expected.AddRange(name);
        Assert.Equal(expected, blob);
        Assert.Empty(ReceiverTxtRecords.ToBlob([]));
        Assert.Throws<ArgumentException>(() => ReceiverTxtRecords.ToBlob([new string('x', 256)]));
        Assert.Equal(256, ReceiverTxtRecords.ToBlob([new string('x', 255)]).Length);
    }

    [Fact]
    public void Blob_of_the_airplay_txt_parses_back_to_the_entries()
    {
        var options = Options();
        var txt = ReceiverTxtRecords.Airplay(options, ReceiverFeatures.Default, ReceiverTxtRecords.TxtFlagsFor(options));

        Assert.Equal(txt, ParseBlob(ReceiverTxtRecords.ToBlob(txt)));
    }

    /// <summary>Splits a TXT wire blob back into its entries.</summary>
    internal static List<string> ParseBlob(byte[] blob)
    {
        var entries = new List<string>();
        var offset = 0;
        while (offset < blob.Length)
        {
            var length = blob[offset++];
            entries.Add(Encoding.UTF8.GetString(blob, offset, length));
            offset += length;
        }

        Assert.Equal(blob.Length, offset);
        return entries;
    }
}
