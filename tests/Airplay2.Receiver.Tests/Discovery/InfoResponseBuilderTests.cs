using System.Net;
using Airplay2.Receiver.Discovery;
using Airplay2.Receiver.Rtsp;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Xunit;

namespace Airplay2.Receiver.Tests.Discovery;

/// <summary>The <c>/info</c> answer (SPEC-receiver.md 3.2, R3, R9, R15) and its GET / stage-1 POST handling.</summary>
public class InfoResponseBuilderTests
{
    private static readonly IPEndPoint Sender = new(IPAddress.Parse("192.0.2.10"), 49152);

    private static readonly string[] ReplacedKeys =
    [
        "name", "model", "manufacturer", "deviceID", "pi", "psi", "pk", "features", "statusFlags", "sourceVersion",
        "protocolVersion", "senderAddress", "initialVolume", "supportedFormats", "txtAirPlay",
    ];

    private static InfoResponseBuilder Builder(string? password = null) => new(ReceiverTxtRecordsTests.Options(password));

    [Fact]
    public void Embedded_template_is_the_test_vector()
    {
        var vector = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestVectors", "info-response.bplist"));
        Assert.Equal(vector, InfoResponseBuilder.DefaultTemplate.ToArray());
    }

    [Fact]
    public void Info_round_trips_through_binary_plist_with_every_3_2_key()
    {
        var builder = Builder();
        var flags = ReceiverFeatures.StatusFlagsFor(passwordSet: false, sessionActive: false);
        var info = BinaryPlist.DecodeDictionary(BinaryPlist.Encode(builder.Build(ReceiverFeatures.Default, flags, -15.0, Sender)));

        foreach (var key in ReplacedKeys)
        {
            Assert.True(info.ContainsKey(key), key);
        }

        var identity = ReceiverTxtRecordsTests.Identity;
        Assert.Equal("Test Receiver", info.GetString("name"));
        Assert.Equal("TuneShuttle1,1", info.GetString("model"));
        Assert.Equal("TuneShuttle", info.GetString("manufacturer"));
        Assert.Equal(identity.DeviceId, info.GetString("deviceID"));
        Assert.Equal(identity.DeviceId, info.GetString("pi"));
        Assert.Equal(identity.Psi, info.GetString("psi"));
        Assert.Equal(identity.PublicKey, info.GetData("pk"));
        Assert.Equal(32, info.GetData("pk")!.Length);
        Assert.Equal(unchecked((long)ReceiverFeatures.Default), info.GetInt64("features"));
        Assert.Equal(0x4L, info.GetInt64("statusFlags"));
        Assert.Equal("366.0", info.GetString("sourceVersion"));
        Assert.Equal("1.1", info.GetString("protocolVersion"));
        Assert.Equal("192.0.2.10:49152", info.GetString("senderAddress"));
        Assert.Equal(-15.0, info.GetDouble("initialVolume"));

        var formats = info.GetDictionary("supportedFormats")!;
        Assert.Equal(0x40000L, formats.GetInt64("audioStream"));
        Assert.Equal(0x440000L, formats.GetInt64("bufferStream"));

        var txt = ReceiverTxtRecordsTests.ParseBlob(info.GetData("txtAirPlay")!);
        var options = ReceiverTxtRecordsTests.Options();
        Assert.Equal(ReceiverTxtRecords.Airplay(options, ReceiverFeatures.Default, ReceiverTxtRecords.TxtFlagsFor(options)), txt);

        // Template keys we do not replace are kept.
        Assert.Equal("AirPlay;2.0.10", info.GetString("PTPInfo"));
        Assert.True(info.GetBool("keepAliveSendStatsAsBody"));
    }

    [Fact]
    public void Alac_only_word_advertises_buffered_alac_only()
    {
        var info = Builder().Build(ReceiverFeatures.AlacOnly, AirplayStatusFlags.AudioLink, -15.0, Sender);

        Assert.Equal(unchecked((long)ReceiverFeatures.AlacOnly), info.GetInt64("features"));
        var formats = info.GetDictionary("supportedFormats")!;
        Assert.Equal(0x40000L, formats.GetInt64("audioStream"));
        Assert.Equal(0x40000L, formats.GetInt64("bufferStream"));
        Assert.Contains("features=0x404FCA00,0x18340", ReceiverTxtRecordsTests.ParseBlob(info.GetData("txtAirPlay")!));
    }

    [Fact]
    public void Status_flags_carry_password_and_session_bits_but_txt_blob_does_not()
    {
        var builder = Builder(password: "secret");
        var flags = ReceiverFeatures.StatusFlagsFor(passwordSet: true, sessionActive: true);
        var info = builder.Build(ReceiverFeatures.Default, flags, -20.0, Sender);

        Assert.Equal(0x20084L, info.GetInt64("statusFlags"));
        Assert.Contains("flags=0x84", ReceiverTxtRecordsTests.ParseBlob(info.GetData("txtAirPlay")!));
    }

    [Fact]
    public void Explicit_txt_blob_is_used_as_given()
    {
        byte[] blob = [5, .. "acl=0"u8];
        var info = Builder().Build(ReceiverFeatures.Default, AirplayStatusFlags.AudioLink, 0.0, Sender, blob);
        Assert.Equal(blob, info.GetData("txtAirPlay"));
    }

    [Fact]
    public void Sender_address_formats_ipv4_mapped_and_ipv6()
    {
        Assert.Equal("203.0.113.5:7000", InfoResponseBuilder.SenderAddress(new IPEndPoint(IPAddress.Parse("::ffff:203.0.113.5"), 7000)));
        Assert.Equal("2001:db8::1:5000", InfoResponseBuilder.SenderAddress(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 5000)));
    }

    [Fact]
    public void Each_build_starts_from_a_fresh_template()
    {
        var builder = Builder();
        var first = builder.Build(ReceiverFeatures.Default, AirplayStatusFlags.AudioLink, -10.0, Sender);
        first["name"] = "changed";
        var second = builder.Build(ReceiverFeatures.Default, AirplayStatusFlags.AudioLink, -10.0, Sender);
        Assert.Equal("Test Receiver", second.GetString("name"));
    }

    [Fact]
    public void Broken_template_fails_at_construction()
    {
        Assert.ThrowsAny<Exception>(() => new InfoResponseBuilder(ReceiverTxtRecordsTests.Options(), new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("GET", "/info", true)]
    [InlineData("POST", "/info", true)]
    [InlineData("get", "/info?x=1", true)]
    [InlineData("GET", "/information", false)]
    [InlineData("SETUP", "/info", false)]
    public void Info_requests_are_get_and_post_on_info(string method, string uri, bool expected)
    {
        Assert.Equal(expected, InfoResponseBuilder.IsInfoRequest(Request(method, uri, ReadOnlyMemory<byte>.Empty)));
    }

    [Fact]
    public void Bare_get_and_stage_one_post_get_the_same_dictionary()
    {
        var builder = Builder();
        var stageOne = BinaryPlist.Encode(new PlistDictionary { ["qualifier"] = new List<object?> { "txtAirPlay" } });

        var bare = builder.Respond(Request("GET", "/info", ReadOnlyMemory<byte>.Empty), ReceiverFeatures.Default, AirplayStatusFlags.AudioLink, -15.0, Sender);
        var post = builder.Respond(Request("POST", "/info", stageOne), ReceiverFeatures.Default, AirplayStatusFlags.AudioLink, -15.0, Sender);

        Assert.Equal(RtspStatus.Ok, bare.StatusCode);
        Assert.Equal(RtspStatus.Ok, post.StatusCode);
        Assert.Equal(RtspContentTypes.BinaryPlist, post.ContentType);
        Assert.Equal(bare.Body.ToArray(), post.Body.ToArray());
        var info = BinaryPlist.DecodeDictionary(post.Body.Span);
        Assert.NotNull(info.GetData("txtAirPlay"));
    }

    [Fact]
    public void Stage_one_body_without_a_qualifier_is_answered_400()
    {
        var builder = Builder();
        var noQualifier = BinaryPlist.Encode(new PlistDictionary { ["other"] = "x" });

        Assert.Equal(RtspStatus.BadRequest, builder.Respond(Request("POST", "/info", noQualifier), ReceiverFeatures.Default, AirplayStatusFlags.AudioLink, 0, Sender).StatusCode);
        Assert.Equal(RtspStatus.BadRequest, builder.Respond(Request("POST", "/info", "not a plist"u8.ToArray()), ReceiverFeatures.Default, AirplayStatusFlags.AudioLink, 0, Sender).StatusCode);
    }

    private static RtspIncomingRequest Request(string method, string uri, ReadOnlyMemory<byte> body) =>
        new(method, uri, "RTSP/1.0", new Dictionary<string, string> { ["CSeq"] = "1" }, body);
}
