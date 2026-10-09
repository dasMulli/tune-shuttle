// Tests for src/Librespot.Discovery/Mdns/DnsPacket.cs (new code replacing libmdns' dns_parser).
using System.Net;
using Librespot.Discovery.Mdns;
using Xunit;

namespace Librespot.Tests.Discovery;

public class DnsPacketTests
{
    // A one-shot query as sent by the Spotify app: id 0, flags 0, one question
    // "_spotify-connect._tcp.local" PTR, class IN with the QU bit (0x8001).
    private const string SpotifyQueryHex =
        "000000000001000000000000"
        + "105f73706f746966792d636f6e6e656374" // 16 "_spotify-connect"
        + "045f746370" // 4 "_tcp"
        + "056c6f63616c00" // 5 "local" 0
        + "000c8001";

    // The same query with a known answer whose owner and target use compression pointers to offset 12:
    // answer: c00c PTR IN ttl=4500 rdlength=10 rdata="Kitchen" c00c
    private const string QueryWithKnownAnswerHex =
        "000000000001000100000000"
        + "105f73706f746966792d636f6e6e656374045f746370056c6f63616c00"
        + "000c0001"
        + "c00c000c000100001194000a"
        + "074b69746368656e" + "c00c";

    [Fact]
    public void Decodes_captured_spotify_connect_query()
    {
        var message = DnsMessage.Parse(Convert.FromHexString(SpotifyQueryHex));

        Assert.False(message.IsResponse);
        Assert.Equal(0, message.Opcode);
        var question = Assert.Single(message.Questions);
        Assert.Equal(DnsName.Parse("_spotify-connect._tcp.local"), question.Name);
        Assert.Equal(DnsType.Ptr, question.Type);
        Assert.Equal(DnsClass.In, question.Class);
        Assert.True(question.UnicastResponse);
        Assert.Empty(message.Answers);

        // Re-encoding gives the same bytes.
        Assert.Equal(SpotifyQueryHex, Convert.ToHexStringLower(message.Encode()));
    }

    [Fact]
    public void Decodes_compression_pointers_in_owner_and_rdata()
    {
        var message = DnsMessage.Parse(Convert.FromHexString(QueryWithKnownAnswerHex));

        Assert.False(Assert.Single(message.Questions).UnicastResponse);
        var answer = Assert.Single(message.Answers);
        Assert.Equal(DnsName.Parse("_spotify-connect._tcp.local"), answer.Name);
        Assert.Equal(DnsType.Ptr, answer.Type);
        Assert.Equal(4500u, answer.Ttl);
        Assert.False(answer.CacheFlush);
        var ptr = Assert.IsType<DnsRecordData.Ptr>(answer.Data);
        Assert.Equal(["Kitchen", "_spotify-connect", "_tcp", "local"], ptr.Target.Labels);
    }

    [Fact]
    public void Encodes_ptr_answer_with_name_compression()
    {
        var type = DnsName.Parse("_spotify-connect._tcp.local");
        var message = DnsMessage.NewResponse(0);
        message.Answers.Add(DnsRecord.Create(type, 60, new DnsRecordData.Ptr(type.Prepend("Test"))));

        var expected =
            "000084000000000100000000"
            + "105f73706f746966792d636f6e6e656374045f746370056c6f63616c00"
            + "000c0001" + "0000003c" + "0007"
            + "0454657374" + "c00c";
        Assert.Equal(expected, Convert.ToHexStringLower(message.Encode()));
    }

    [Fact]
    public void Ptr_srv_txt_a_aaaa_roundtrip()
    {
        var type = DnsName.Parse("_spotify-connect._tcp.local");
        var instance = type.Prepend("My.Speaker"); // a '.' inside the instance label stays one label
        var host = DnsName.Parse("myhost.local");

        var message = DnsMessage.NewResponse(0x1234);
        message.Answers.Add(DnsRecord.Create(type, 60, new DnsRecordData.Ptr(instance)));
        message.Answers.Add(DnsRecord.Create(instance, 60, new DnsRecordData.Srv(0, 0, 49152, host)) with { CacheFlush = true });
        message.Answers.Add(DnsRecord.Create(instance, 60, DnsRecordData.Txt.FromStrings(["VERSION=1.0", "CPath=/"])));
        message.Additionals.Add(DnsRecord.Create(host, 120, new DnsRecordData.A(IPAddress.Parse("198.51.100.20"))));
        message.Additionals.Add(DnsRecord.Create(host, 120, new DnsRecordData.Aaaa(IPAddress.Parse("fe80::1234:5678"))));

        var bytes = message.Encode();
        var decoded = DnsMessage.Parse(bytes);

        Assert.Equal(0x1234, decoded.Id);
        Assert.True(decoded.IsResponse);
        Assert.Equal(message.Answers, decoded.Answers);
        Assert.Equal(message.Additionals, decoded.Additionals);
        Assert.Equal(4, Assert.IsType<DnsRecordData.Ptr>(decoded.Answers[0].Data).Target.Labels.Count);
        Assert.True(decoded.Answers[1].CacheFlush);
        Assert.Equal(["VERSION=1.0", "CPath=/"], Assert.IsType<DnsRecordData.Txt>(decoded.Answers[2].Data).Strings());

        // Every repeated name after the first is a 2-byte pointer: the packet is much smaller than uncompressed.
        Assert.True(bytes.Length < 200, $"packet is {bytes.Length} bytes");
    }

    [Fact]
    public void Txt_wire_format_matches_libmdns()
    {
        Assert.Equal("0b56455253494f4e3d312e300743506174683d2f", Convert.ToHexStringLower(DnsRecordData.Txt.FromStrings(["VERSION=1.0", "CPath=/"]).Raw));
        Assert.Equal(new byte[] { 0 }, DnsRecordData.Txt.FromStrings([]).Raw);
    }

    [Fact]
    public void Names_compare_case_insensitively()
    {
        Assert.Equal(DnsName.Parse("_Spotify-Connect._TCP.Local"), DnsName.Parse("_spotify-connect._tcp.local"));
        Assert.Equal(DnsName.Parse("A.b").GetHashCode(), DnsName.Parse("a.B").GetHashCode());
        Assert.NotEqual(DnsName.Parse("a.b"), DnsName.Parse("a.b.c"));
    }

    [Fact]
    public void Long_labels_are_truncated_on_utf8_boundaries()
    {
        var name = new string('é', 40); // 80 UTF-8 bytes
        var label = DnsName.TruncateLabel(name);
        Assert.Equal(31, label.Length); // 62 bytes; a 32nd character would need 64
        Assert.Throws<ArgumentException>(() => new DnsName([name]));
    }

    [Theory]
    [InlineData("0000000000010000000000")] // truncated header
    [InlineData("000000000001000000000000c00c000c0001")] // pointer to itself
    [InlineData("000000000001000000000000c00e000c0001")] // forward pointer
    [InlineData("0000000000010000000000000a6162")] // label exceeds packet
    [InlineData("000000000000000100000000" + "00" + "0001000100000000" + "0005" + "01020304")] // A with 5-byte rdata length
    public void Malformed_packets_are_rejected(string hex)
    {
        Assert.False(DnsMessage.TryParse(Convert.FromHexString(hex), out var message, out var error));
        Assert.Null(message);
        Assert.NotNull(error);
    }
}
