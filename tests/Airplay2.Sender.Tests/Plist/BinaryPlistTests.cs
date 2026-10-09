using System.Text;
using Airplay2.Sender.Plist;
using Xunit;

namespace Airplay2.Sender.Tests.Plist;

public class BinaryPlistTests
{
    private static string Vector(string name) => Path.Combine(AppContext.BaseDirectory, "TestVectors", name);

    [Fact]
    public void Decodes_the_SpeakerA_info_response()
    {
        var raw = File.ReadAllBytes(Vector("SpeakerA-info-response.bplist"));
        Assert.Equal(525, raw.Length);
        Assert.True(BinaryPlist.IsBinaryPlist(raw));
        var dict = BinaryPlist.DecodeDictionary(raw);

        Assert.Equal("Test Speaker ABCD", dict.GetString("name"));
        Assert.Equal("02:1A:2B:3C:4D:5E", dict.GetString("deviceID"));
        Assert.Equal("366.0", dict.GetString("sourceVersion"));
        Assert.Equal(496155769145856L, dict.GetInt64("features"));
        Assert.Equal(0x1C340445F8A00L, dict.GetInt64("features"));
        Assert.Equal(1028L, dict.GetInt64("statusFlags"));
        Assert.True(dict.GetBool("keepAliveLowPower"));
        Assert.True(dict.GetBool("keepAliveSendStatsAsBody"));
        Assert.False(dict.GetBool("nameIsFactoryDefault"));
        Assert.Equal(string.Empty, dict.GetString("firmwareBuildDate"));
        Assert.Equal(19, dict.Count);
        Assert.Equal("sdk", dict.Keys.First()); // key order preserved as emitted by the receiver
        Assert.Equal("name", dict.Keys.Last());
        Assert.Null(dict.GetString("initialVolume"));
    }

    [Fact]
    public void Decodes_the_stage1_response_with_txtAirPlay_data()
    {
        var raw = File.ReadAllBytes(Vector("SpeakerA-info-response-stage1.bplist"));
        Assert.Equal(909, raw.Length);
        var dict = BinaryPlist.DecodeDictionary(raw);
        var txt = dict.GetData("txtAirPlay");
        Assert.NotNull(txt);
        Assert.Equal(363, txt.Length);

        // Pascal strings reproducing the _airplay TXT record
        var strings = new List<string>();
        for (var i = 0; i < txt.Length;)
        {
            int len = txt[i++];
            strings.Add(Encoding.ASCII.GetString(txt, i, len));
            i += len;
        }

        Assert.Contains("features=0x445F8A00,0x1C340", strings);
        Assert.Contains("flags=0x404", strings);
        Assert.Contains("pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", strings);
        Assert.Equal(15, strings.Count);
    }

    [Fact]
    public void Re_encoding_the_SpeakerA_response_round_trips()
    {
        var raw = File.ReadAllBytes(Vector("SpeakerA-info-response-stage1.bplist"));
        var dict = BinaryPlist.DecodeDictionary(raw);
        var encoded = BinaryPlist.Encode(dict);
        var again = BinaryPlist.DecodeDictionary(encoded);
        Assert.Equal(dict.Keys, again.Keys);
        foreach (var key in dict.Keys)
        {
            AssertPlistEqual(dict[key], again[key]);
        }
    }

    [Fact]
    public void Decodes_plistlib_output_for_the_info_qualifier_body()
    {
        // python: plistlib.dumps({"qualifier": ["txtAirPlay"]}, fmt=plistlib.FMT_BINARY)
        var raw = Convert.FromHexString("62706c6973743030d10102597175616c6966696572a1035a747874416972506c6179080b15170000000000000101000000000000000400000000000000000000000000000022");
        var dict = BinaryPlist.DecodeDictionary(raw);
        var list = dict.GetArray("qualifier");
        Assert.NotNull(list);
        Assert.Equal("txtAirPlay", Assert.Single(list));
    }

    [Fact]
    public void Round_trips_every_value_type()
    {
        var dict = new PlistDictionary
        {
            ["string"] = "hello",
            ["unicode"] = "Lautsprecher SpeakerA GmbH – äöü \U0001F3B5",
            ["empty"] = string.Empty,
            ["true"] = true,
            ["false"] = false,
            ["small"] = 5L,
            ["u16"] = 44100L,
            ["u32"] = 0x80000000L,
            ["i64"] = 0x1C340445F8A00L,
            ["negative"] = -42L,
            ["clockId"] = -1234567890123456789L,
            ["big"] = ulong.MaxValue,
            ["real"] = 44100.0,
            ["realNeg"] = -27.5,
            ["data"] = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 },
            ["emptyData"] = Array.Empty<byte>(),
            ["date"] = new DateTime(2026, 10, 9, 12, 34, 56, DateTimeKind.Utc),
            ["null"] = null,
            ["uid"] = new PlistUid(7),
            ["array"] = new List<object?> { "a", 1L, true, new byte[] { 9 }, new List<object?>(), new PlistDictionary() },
            ["nested"] = new PlistDictionary { ["streams"] = new List<object?> { new PlistDictionary { ["type"] = 96L, ["dataPort"] = 6000L } } },
            ["longString"] = new string('x', 300),
            ["longArray"] = Enumerable.Range(0, 20).Select(i => (object?)(long)i).ToList(),
        };

        var encoded = BinaryPlist.Encode(dict);
        Assert.True(BinaryPlist.IsBinaryPlist(encoded));
        var decoded = BinaryPlist.DecodeDictionary(encoded);
        Assert.Equal(dict.Keys, decoded.Keys);
        foreach (var key in dict.Keys)
        {
            AssertPlistEqual(dict[key], decoded[key]);
        }
    }

    [Fact]
    public void Writer_uses_wide_references_for_more_than_255_objects()
    {
        var list = Enumerable.Range(0, 300).Select(i => (object?)("s" + i)).ToList();
        var encoded = BinaryPlist.Encode(list);
        Assert.Equal(2, encoded[encoded.Length - 32 + 7]); // ref size in the trailer
        var decoded = Assert.IsType<List<object?>>(BinaryPlist.Decode(encoded));
        Assert.Equal(300, decoded.Count);
        Assert.Equal("s299", decoded[299]);
    }

    [Fact]
    public void Rejects_garbage()
    {
        Assert.Throws<FormatException>(() => BinaryPlist.Decode(Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><plist/>")));
        Assert.Throws<FormatException>(() => BinaryPlist.Decode("bplist00"u8.ToArray()));
        var raw = File.ReadAllBytes(Vector("SpeakerA-info-response.bplist"));
        raw[^1] = 0xFF; // offset table far beyond the end
        Assert.Throws<FormatException>(() => BinaryPlist.Decode(raw));
    }

    private static void AssertPlistEqual(object? expected, object? actual)
    {
        switch (expected)
        {
            case null:
                Assert.Null(actual);
                break;
            case byte[] b:
                Assert.Equal(b, Assert.IsType<byte[]>(actual));
                break;
            case DateTime d:
                Assert.Equal(d, Assert.IsType<DateTime>(actual), TimeSpan.FromMilliseconds(1));
                break;
            case PlistDictionary pd:
                var ad = Assert.IsType<PlistDictionary>(actual);
                Assert.Equal(pd.Keys, ad.Keys);
                foreach (var k in pd.Keys)
                {
                    AssertPlistEqual(pd[k], ad[k]);
                }

                break;
            case List<object?> list:
                var al = Assert.IsType<List<object?>>(actual);
                Assert.Equal(list.Count, al.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    AssertPlistEqual(list[i], al[i]);
                }

                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }
}
