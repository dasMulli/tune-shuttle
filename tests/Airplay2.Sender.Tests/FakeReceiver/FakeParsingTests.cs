using Airplay2.FakeReceiver;
using Xunit;

namespace Airplay2.Sender.Tests.Fake;

/// <summary>Unit tests of the fake receiver's header and body parsers.</summary>
public class FakeParsingTests
{
    [Theory]
    [InlineData("seq=12;rtptime=34", (ushort)12, 34u)]
    [InlineData("rtptime=4294967295", null, 4294967295u)]
    [InlineData("seq=65536;rtptime=1", (ushort)0, 1u)]
    [InlineData("", null, null)]
    public void Rtp_info(string header, ushort? seq, uint? rtpTime)
    {
        Assert.Equal((seq, rtpTime), FakeParsing.ParseRtpInfo(header));
    }

    [Fact]
    public void Text_parameters_volume_progress_and_other()
    {
        var parsed = FakeParsing.ParseTextParameters("\r\nvolume: -15.250000\r\nprogress: 1/2/3\r\nfoo: bar\r\n");
        Assert.Equal(
            new FakeParsing.TextParameter[]
            {
                new FakeParsing.TextParameter.VolumeValue(-15.25),
                new FakeParsing.TextParameter.ProgressValue(1, 2, 3),
                new FakeParsing.TextParameter.Other("foo: bar"),
            },
            parsed);
    }

    [Fact]
    public void Dmap_listing_item_with_truncated_tail()
    {
        byte[] body =
        [
            .. "mlit"u8, 0, 0, 0, 0, // outer length is not checked [sps:rtsp.c:3282-3314]
            .. "minm"u8, 0, 0, 0, 2, .. "Hi"u8,
            .. "astn"u8, 0, 0, 0, 2, 0, 7,
            .. "xxxx"u8, 0, 0, 0, 3, 1, 2, 3,
            .. "asal"u8, 0, 0, 0, 9, .. "cut"u8,
        ];
        var items = FakeParsing.ParseDmapListingItem(body);
        Assert.Equal("Hi", items["minm"]);
        Assert.Equal(7UL, items["astn"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, items["xxxx"]);
        Assert.False(items.ContainsKey("asal"));
    }

    [Theory]
    [InlineData(-20.0, "-20.0")]
    [InlineData(-15.5, "-15.5")]
    [InlineData(0.0, "0.0")]
    public void Python_float(double value, string expected) => Assert.Equal(expected, FakeParsing.PythonFloat(value));
}
