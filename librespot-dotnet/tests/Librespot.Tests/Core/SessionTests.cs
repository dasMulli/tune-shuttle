// Tests for core/src/session.rs and core/src/audio_key.rs: packet routing of the dispatch task over a loopback
// transport (no upstream tests exist for these files).
using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using Librespot.Core;
using Librespot.Core.Connection;
using Librespot.Core.Mercury;
using Xunit;
using ProtoHeader = Librespot.Protocol.Mercury.Header;

namespace Librespot.Tests.Core;

public class SessionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<(Session Session, FakeAccessPoint.Server Server)> ConnectedSessionAsync(
        Session.KeepAliveTimeouts? timeouts = null)
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        var session = new Session(SessionConfig.Default(), null);
        session.StartTasks(transport, timeouts);
        return (session, server);
    }

    private static Session.KeepAliveTimeouts Timeouts(
        TimeSpan? initialPing = null,
        TimeSpan? ping = null,
        TimeSpan? pongDelay = null,
        TimeSpan? pongAck = null)
    {
        var d = Session.KeepAliveTimeouts.Default;
        return new(initialPing ?? d.InitialPing, ping ?? d.Ping, pongDelay ?? d.PongDelay, pongAck ?? d.PongAck);
    }

    private static byte[] PingPayload()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return payload;
    }

    /// <summary>A duplex stream whose reads never complete and whose writes fail (a dead sink).</summary>
    private sealed class BrokenSinkStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("broken pipe");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("broken pipe"));
    }

    private static Task SendAsync(FakeAccessPoint.Server server, PacketType cmd, byte[] payload) =>
        FakeAccessPoint.WritePacketAsync(server.Stream, server.Codec, (byte)cmd, payload);

    private static Task<ApPacket> ReceiveAsync(FakeAccessPoint.Server server) =>
        FakeAccessPoint.ReadPacketAsync(server.Stream, server.Codec).WaitAsync(Wait);

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }

    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

    /// <summary>A Mercury response packet: seq len | seq | flags | count | (len | part)*.</summary>
    private static byte[] MercuryPacket(byte[] seq, byte flags, params byte[][] parts)
    {
        var packet = new List<byte>();
        packet.AddRange(U16(seq.Length));
        packet.AddRange(seq);
        packet.Add(flags);
        packet.AddRange(U16(parts.Length));
        foreach (var part in parts)
        {
            packet.AddRange(U16(part.Length));
            packet.AddRange(part);
        }

        return [.. packet];
    }

    private static byte[] Header(string uri, int status) =>
        new ProtoHeader { Uri = uri, StatusCode = status }.ToByteArray();

    /// <summary>Parses a Mercury request packet the way the AP would.</summary>
    private static (byte[] Seq, ProtoHeader Header) ParseRequest(ApPacket packet)
    {
        var span = packet.Payload.Span;
        var seqLen = BinaryPrimitives.ReadUInt16BigEndian(span);
        var seq = span.Slice(2, seqLen).ToArray();
        span = span[(2 + seqLen)..];
        Assert.Equal(1, span[0]);
        span = span[3..];
        var headerLen = BinaryPrimitives.ReadUInt16BigEndian(span);
        return (seq, ProtoHeader.Parser.ParseFrom(span.Slice(2, headerLen)));
    }

    [Fact]
    public async Task SendPacketBeforeConnectFails()
    {
        var session = new Session(SessionConfig.Default(), null);
        var error = Assert.Throws<SessionException>(() => session.SendPacket(PacketType.Pong, new byte[4]));
        Assert.Equal(SessionException.Reason.NotConnected, error.Cause);
        Assert.Equal("Service unavailable { Session is not connected }", error.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task PingSetsTimeDelta()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        var serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1000;
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)serverTime);
        await SendAsync(server, PacketType.Ping, payload);

        await Eventually(() => session.TimeDelta != 0);
        Assert.InRange(session.TimeDelta, 998, 1002);
    }

    [Fact]
    public async Task CountryCodeAndProductInfoUpdateUserData()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;
        string? unsupported = null;
        session.UnsupportedAccountType += t => unsupported = t;

        await SendAsync(server, PacketType.CountryCode, Encoding.UTF8.GetBytes("SE"));
        await SendAsync(
            server,
            PacketType.ProductInfo,
            Encoding.UTF8.GetBytes("<products><product><type>premium</type><autoplay>1</autoplay><filter-explicit-content>0</filter-explicit-content></product></products>"));

        await Eventually(() => session.GetUserAttribute("type") is not null);
        Assert.Equal("SE", session.Country);
        Assert.Equal("premium", session.UserData.Attributes["type"]);
        Assert.True(session.Autoplay);
        Assert.False(session.FilterExplicitContent);
        Assert.Null(unsupported);

        // ProductInfo replaces the attributes; a non-premium account raises the event.
        await SendAsync(server, PacketType.ProductInfo, Encoding.UTF8.GetBytes("<products><product><type>free</type></product></products>"));
        await Eventually(() => session.GetUserAttribute("type") == "free");
        Assert.Equal("free", unsupported);
        Assert.Null(session.GetUserAttribute("autoplay"));
    }

    [Fact]
    public void ProductInfoParsingFollowsQuickXmlLoop()
    {
        var xml = "<products>\n  <product>\n    <type>premium</type>\n    <empty/>\n    <head-files-url>https://heads-fa.spotify.com/head/{file_id}</head-files-url>\n  </product>\n</products>";
        var attributes = Session.ParseProductInfo(Encoding.UTF8.GetBytes(xml));

        Assert.Equal("premium", attributes["type"]);
        Assert.Equal("https://heads-fa.spotify.com/head/{file_id}", attributes["head-files-url"]);
        // quick-xml reports whitespace text too; it lands under the enclosing start tag.
        Assert.Equal("\n  ", attributes["products"]);
        Assert.Equal("\n    ", attributes["product"]);
        Assert.False(attributes.ContainsKey("empty"));
    }

    [Fact]
    public async Task MercuryGetReassemblesPartialResponses()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        var response = session.Mercury.GetAsync("hm://test/get");

        var request = await ReceiveAsync(server);
        Assert.Equal((byte)PacketType.MercuryReq, request.Cmd);
        var (seq, header) = ParseRequest(request);
        Assert.Equal(8, seq.Length);
        Assert.Equal("hm://test/get", header.Uri);
        Assert.Equal("GET", header.Method);

        // First packet: header part + the first half of a payload part (flags 2 = partial), then the rest.
        await SendAsync(server, PacketType.MercuryReq, MercuryPacket(seq, 2, Header("hm://test/get", 200), [1, 2]));
        await SendAsync(server, PacketType.MercuryReq, MercuryPacket(seq, 1, [3, 4], [5]));

        var result = await response.WaitAsync(Wait);
        Assert.Equal("hm://test/get", result.Uri);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, result.Payload.Count);
        Assert.Equal([1, 2, 3, 4], result.Payload[0]);
        Assert.Equal([5], result.Payload[1]);
    }

    [Fact]
    public async Task MercuryErrorStatusFailsTheRequest()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        var response = session.Mercury.GetAsync("hm://test/missing");
        var (seq, _) = ParseRequest(await ReceiveAsync(server));
        await SendAsync(server, PacketType.MercuryReq, MercuryPacket(seq, 1, Header("hm://test/missing", 404)));

        var error = await Assert.ThrowsAsync<MercuryException>(() => response.WaitAsync(Wait));
        Assert.Equal(MercuryException.Reason.Response, error.Cause);
        Assert.Equal(404, error.Response!.StatusCode);
    }

    [Fact]
    public async Task MercuryEventsReachListeners()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        var events = session.Mercury.ListenFor("hm://pusher/v1/user/a+b");

        // Events use a sequence nobody is waiting for; the uri's components are form-encoded before matching.
        await SendAsync(server, PacketType.MercuryEvent, MercuryPacket([9], 1, Header("hm://pusher/v1/user/a b/x", 200), [7]));

        var ev = await events.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal("hm://pusher/v1/user/a b/x", ev.Uri);
        Assert.Equal([7], ev.Payload[0]);
    }

    [Fact]
    public async Task MercurySubscribeUsesTheRequestedUriWithTheNewProtocol()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        var subscription = session.Mercury.SubscribeAsync("hm://sub/");
        var request = await ReceiveAsync(server);
        Assert.Equal((byte)PacketType.MercurySub, request.Cmd);
        var (seq, header) = ParseRequest(request);
        Assert.Equal("SUB", header.Method);
        await SendAsync(server, PacketType.MercurySub, MercuryPacket(seq, 1, Header("hm://sub/", 200)));
        var events = await subscription.WaitAsync(Wait);

        await SendAsync(server, PacketType.MercuryEvent, MercuryPacket([1], 1, Header("hm://sub/thing", 200)));
        Assert.Equal("hm://sub/thing", (await events.ReadAsync().AsTask().WaitAsync(Wait)).Uri);
    }

    [Fact]
    public async Task AudioKeyRequestAndResponse()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        var track = new SpotifyId(UInt128.Parse("1234567890"));
        var file = FileId.FromRaw(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray());
        var keyTask = session.AudioKey.RequestAsync(track, file);

        var request = await ReceiveAsync(server);
        Assert.Equal((byte)PacketType.RequestKey, request.Cmd);
        Assert.Equal(AudioKeyManager.BuildKeyRequest(0, track, file), request.Payload.ToArray());

        var key = Enumerable.Range(0x10, 16).Select(i => (byte)i).ToArray();
        await SendAsync(server, PacketType.AesKey, [.. request.Payload.Span.Slice(36, 4), .. key]);

        Assert.Equal(new AudioKey(key), await keyTask.WaitAsync(Wait));

        // The second request uses sequence 1 and gets an AesKeyError.
        var errorTask = session.AudioKey.RequestAsync(track, file);
        var second = await ReceiveAsync(server);
        Assert.Equal([0, 0, 0, 1], second.Payload.Span.Slice(36, 4).ToArray());
        await SendAsync(server, PacketType.AesKeyError, [0, 0, 0, 1, 0x00, 0x01]);
        var error = await Assert.ThrowsAsync<AudioKeyException>(() => errorTask.WaitAsync(Wait));
        Assert.Equal(AudioKeyException.Reason.AesKey, error.Cause);
    }

    [Fact]
    public async Task AudioKeyTimesOut()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        var keyTask = session.AudioKey.RequestAsync(new SpotifyId(1), FileId.FromRaw(new byte[20]));
        await ReceiveAsync(server);

        var error = await Assert.ThrowsAsync<AudioKeyException>(() => keyTask.WaitAsync(Wait));
        Assert.Equal(AudioKeyException.Reason.Timeout, error.Cause);
    }

    [Fact]
    public async Task ConnectionCloseShutsTheSessionDown()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        var listener = session.Mercury.ListenFor("hm://x/");
        var pending = session.Mercury.GetAsync("hm://x/pending");
        await ReceiveAsync(server);

        server.Client.Dispose();

        await Eventually(() => session.IsInvalid);
        await listener.Completion.WaitAsync(Wait);
        var error = await Assert.ThrowsAnyAsync<LibrespotException>(() => pending.WaitAsync(Wait));
        Assert.Equal(ErrorKind.Internal, error.Kind);
    }

    [Fact]
    public async Task UnknownPacketsAreIgnored()
    {
        var (session, server) = await ConnectedSessionAsync();
        await using var _ = session;

        await SendAsync(server, (PacketType)0x99, [1, 2, 3]);
        await SendAsync(server, PacketType.LicenseVersion, [1]);
        await SendAsync(server, PacketType.CountryCode, Encoding.UTF8.GetBytes("DE"));

        await Eventually(() => session.Country == "DE");
        Assert.False(session.IsInvalid);
    }

    [Fact]
    public async Task DisposeStopsTheSession()
    {
        var (session, server) = await ConnectedSessionAsync();
        await session.DisposeAsync();

        Assert.True(session.IsInvalid);

        // The connection is closed: the server side reads end of stream.
        await Assert.ThrowsAsync<EndOfStreamException>(() => ReceiveAsync(server));
        server.Client.Dispose();
    }

    [Fact]
    public async Task SenderFailureClosesThePacketQueue()
    {
        var session = new Session(SessionConfig.Default(), null);
        await using var _ = session;
        var key = new byte[32];
        session.StartTasks(new Transport(new BrokenSinkStream(), new ApCodec(key, key)));

        // The first packet is queued, then the sender fails writing it and shuts the session down.
        session.SendPacket(PacketType.Pong, new byte[4]);
        await Eventually(() => session.IsInvalid);

        // Upstream: rx_connection was dropped, so send_packet fails with a SendError (kind Internal).
        var error = Assert.ThrowsAny<LibrespotException>(() => session.SendPacket(PacketType.Pong, new byte[4]));
        Assert.IsNotType<SessionException>(error);
        Assert.Equal(ErrorKind.Internal, error.Kind);
    }

    [Fact]
    public async Task StartTasksAfterDisposeFailsWithNotConnected()
    {
        var session = new Session(SessionConfig.Default(), null);
        await session.DisposeAsync();

        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        var error = Assert.Throws<SessionException>(() => session.StartTasks(transport));
        Assert.Equal(SessionException.Reason.NotConnected, error.Cause);

        // The transport is dropped: the server side reads end of stream.
        await Assert.ThrowsAnyAsync<Exception>(() => ReceiveAsync(server));
        server.Client.Dispose();
    }

    [Fact]
    public async Task PingIsAnsweredWithPongAfterPongDelay()
    {
        var (session, server) = await ConnectedSessionAsync(Timeouts(pongDelay: TimeSpan.FromMilliseconds(50)));
        await using var _ = session;

        await SendAsync(server, PacketType.Ping, PingPayload());

        var pong = await ReceiveAsync(server);
        Assert.Equal((byte)PacketType.Pong, pong.Cmd);
        Assert.Equal([0, 0, 0, 0], pong.Payload.ToArray());
        Assert.False(session.IsInvalid);
    }

    [Fact]
    public async Task MissingPongAckShutsTheSessionDown()
    {
        var (session, server) = await ConnectedSessionAsync(
            Timeouts(pongDelay: TimeSpan.FromMilliseconds(50), pongAck: TimeSpan.FromMilliseconds(100)));
        await using var _ = session;

        await SendAsync(server, PacketType.Ping, PingPayload());
        Assert.Equal((byte)PacketType.Pong, (await ReceiveAsync(server)).Cmd);

        await Eventually(() => session.IsInvalid);
    }

    [Fact]
    public async Task PongAckReturnsToExpectingPing()
    {
        var (session, server) = await ConnectedSessionAsync(
            Timeouts(pongDelay: TimeSpan.FromMilliseconds(50), pongAck: TimeSpan.FromSeconds(1), ping: TimeSpan.FromSeconds(1)));
        await using var _ = session;

        // Two full Ping / Pong / PongAck rounds: each PongAck resets the deadline to the Ping timeout.
        for (var i = 0; i < 2; i++)
        {
            await SendAsync(server, PacketType.Ping, PingPayload());
            Assert.Equal((byte)PacketType.Pong, (await ReceiveAsync(server)).Cmd);
            await SendAsync(server, PacketType.PongAck, []);
        }

        Assert.False(session.IsInvalid);

        // Without a further Ping the Ping timeout expires.
        await Eventually(() => session.IsInvalid);
    }

    [Fact]
    public async Task MissingInitialPingShutsTheSessionDown()
    {
        var (session, server) = await ConnectedSessionAsync(Timeouts(initialPing: TimeSpan.FromMilliseconds(100)));
        await using var _ = session;
        using var __ = server.Client;

        await Eventually(() => session.IsInvalid);
    }

    [Fact]
    public void BuildKeyRequestLayout()
    {
        var track = new SpotifyId(UInt128.Parse("0102030405060708090a0b0c0d0e0f10", System.Globalization.NumberStyles.HexNumber));
        var file = FileId.FromRaw(Enumerable.Range(0xa0, 20).Select(i => (byte)i).ToArray());

        byte[] expected =
        [
            0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf, 0xb0, 0xb1, 0xb2, 0xb3,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
            0xde, 0xad, 0xbe, 0xef,
            0x00, 0x00,
        ];

        Assert.Equal(expected, AudioKeyManager.BuildKeyRequest(0xdeadbeef, track, file));
    }
}
