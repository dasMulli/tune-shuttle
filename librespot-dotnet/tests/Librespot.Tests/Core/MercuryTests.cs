// Tests for core/src/mercury/{mod,types,sender}.rs (no upstream tests; byte vectors built by hand from the layout).
using Librespot.Core;
using Librespot.Core.Mercury;
using Xunit;

namespace Librespot.Tests.Core;

public class MercuryTests
{
    private static readonly byte[] Seq1 = [0, 0, 0, 0, 0, 0, 0, 1];

    [Fact]
    public void EncodeGetRequest()
    {
        var request = new MercuryRequest(MercuryMethod.Get, "hm://a", null, []);

        byte[] expected =
        [
            0x00, 0x08, 0, 0, 0, 0, 0, 0, 0, 1, // seq len + seq
            0x01, // flags: FINAL
            0x00, 0x01, // part count
            0x00, 0x0d, // header len
            0x0a, 0x06, (byte)'h', (byte)'m', (byte)':', (byte)'/', (byte)'/', (byte)'a', // uri
            0x1a, 0x03, (byte)'G', (byte)'E', (byte)'T', // method
        ];

        Assert.Equal(expected, request.Encode(Seq1));
    }

    [Fact]
    public void EncodeSendRequestWithContentTypeAndPayload()
    {
        var request = new MercuryRequest(MercuryMethod.Send, "hm://a", "ct", [[0xaa, 0xbb], []]);

        byte[] expected =
        [
            0x00, 0x02, 0x12, 0x34, // seq len + seq
            0x01, // flags: FINAL
            0x00, 0x03, // part count = header + 2 payload parts
            0x00, 0x12, // header len = 18
            0x0a, 0x06, (byte)'h', (byte)'m', (byte)':', (byte)'/', (byte)'/', (byte)'a', // uri
            0x12, 0x02, (byte)'c', (byte)'t', // content_type
            0x1a, 0x04, (byte)'S', (byte)'E', (byte)'N', (byte)'D', // method
            0x00, 0x02, 0xaa, 0xbb, // part 1
            0x00, 0x00, // part 2 (empty)
        ];

        Assert.Equal(expected, request.Encode([0x12, 0x34]));
    }

    [Theory]
    [InlineData(MercuryMethod.Get, "GET", PacketType.MercuryReq)]
    [InlineData(MercuryMethod.Send, "SEND", PacketType.MercuryReq)]
    [InlineData(MercuryMethod.Sub, "SUB", PacketType.MercurySub)]
    [InlineData(MercuryMethod.Unsub, "UNSUB", PacketType.MercuryUnsub)]
    public void MethodWireNameAndCommand(MercuryMethod method, string wire, PacketType command)
    {
        Assert.Equal(wire, method.ToWireString());
        Assert.Equal(command, method.Command());
    }

    [Theory]
    [InlineData("hm://remote/user/abc", "hm://remote/user/abc")]
    [InlineData("hm://remote/user/a b", "hm://remote/user/a+b")]
    [InlineData("hm://remote/user/jürgen", "hm://remote/user/j%C3%BCrgen")]
    [InlineData("hm://x/a*-._~!", "hm://x/a*-._%7E%21")]
    [InlineData("noslash", "noslash")]
    public void EncodeUriFormEncodesEveryComponentButTheFirst(string uri, string expected)
    {
        Assert.Equal(expected, MercuryManager.EncodeUri(uri));
    }

    [Fact]
    public async Task RequestWithoutConnectionFails()
    {
        var session = new Session(SessionConfig.Default(), null);
        var error = await Assert.ThrowsAsync<SessionException>(() => session.Mercury.GetAsync("hm://a"));
        Assert.Equal(SessionException.Reason.NotConnected, error.Cause);
    }

    [Fact]
    public void DispatchOfUnknownSequenceIsAnError()
    {
        var session = new Session(SessionConfig.Default(), null);
        byte[] packet = [0x00, 0x01, 0x07, 0x01, 0x00, 0x00];
        var error = Assert.Throws<MercuryException>(() => session.Mercury.Dispatch(PacketType.MercuryReq, packet));
        Assert.Equal(MercuryException.Reason.Command, error.Cause);
        Assert.Equal(ErrorKind.Unimplemented, error.Kind);
    }

    [Fact]
    public async Task ListenForEndsOnShutdown()
    {
        var session = new Session(SessionConfig.Default(), null);
        var reader = session.Mercury.ListenFor("hm://a/");
        session.Shutdown();
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // After shutdown new listeners end immediately.
        await session.Mercury.ListenFor("hm://b/").Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SenderStartsFlushed()
    {
        var session = new Session(SessionConfig.Default(), null);
        var sender = session.Mercury.Sender("hm://a");
        Assert.True(sender.IsFlushed);
        Assert.Throws<SessionException>(() => sender.Send([1]));
        Assert.True(sender.IsFlushed);
        Assert.Equal("hm://a", sender.Clone().Uri);
    }
}
