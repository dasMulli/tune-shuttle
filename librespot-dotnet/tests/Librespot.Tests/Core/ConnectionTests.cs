using Google.Protobuf;
using Librespot.Core;
using Librespot.Core.Connection;
using Librespot.Protocol.Authentication;
using Librespot.Protocol.Keyexchange;
using Xunit;

namespace Librespot.Tests.Core;

public class ConnectionTests
{
    [Fact]
    public async Task Transport_reassembles_packets_split_across_reads()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;
        using var __ = server.Client;

        var frame1 = server.Codec.Encode((byte)PacketType.Ping, [1, 2, 3, 4]);
        var frame2 = server.Codec.Encode((byte)PacketType.CountryCode, "DE"u8);
        var all = frame1.Concat(frame2).ToArray();

        var receive = Task.Run(async () =>
        {
            var p1 = await transport.ReceiveAsync();
            var p2 = await transport.ReceiveAsync();
            return (p1, p2);
        });

        // Dribble the bytes: 1 byte, then the rest of the header, then the remainder in two chunks.
        await server.Stream.WriteAsync(all.AsMemory(0, 1));
        await server.Stream.FlushAsync();
        await Task.Delay(20);
        await server.Stream.WriteAsync(all.AsMemory(1, 4));
        await server.Stream.FlushAsync();
        await Task.Delay(20);
        await server.Stream.WriteAsync(all.AsMemory(5, 5));
        await server.Stream.FlushAsync();
        await Task.Delay(20);
        await server.Stream.WriteAsync(all.AsMemory(10));
        await server.Stream.FlushAsync();

        var (first, second) = await receive;
        Assert.Equal((byte)PacketType.Ping, first!.Value.Cmd);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, first.Value.Payload.ToArray());
        Assert.Equal((byte)PacketType.CountryCode, second!.Value.Cmd);
        Assert.Equal("DE"u8.ToArray(), second.Value.Payload.ToArray());
    }

    [Fact]
    public async Task Transport_returns_null_on_clean_close()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;

        await FakeAccessPoint.WritePacketAsync(server.Stream, server.Codec, (byte)PacketType.Ping, [0, 0, 0, 0]);
        server.Client.Dispose();

        Assert.NotNull(await transport.ReceiveAsync());
        Assert.Null(await transport.ReceiveAsync());
    }

    [Fact]
    public async Task Transport_throws_on_close_mid_packet()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;

        var frame = server.Codec.Encode((byte)PacketType.Ping, [0, 0, 0, 0]);
        await server.Stream.WriteAsync(frame.AsMemory(0, frame.Length - 2));
        await server.Stream.FlushAsync();
        server.Client.Dispose();

        var error = await Assert.ThrowsAsync<IOException>(async () => await transport.ReceiveAsync());
        Assert.Equal("bytes remaining on stream", error.Message);
    }

    [Fact]
    public async Task Transport_rejects_bad_mac()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;
        using var __ = server.Client;

        var frame = server.Codec.Encode((byte)PacketType.Ping, [0, 0, 0, 0]);
        frame[^1] ^= 0xff;
        await server.Stream.WriteAsync(frame);
        await server.Stream.FlushAsync();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await transport.ReceiveAsync());
    }

    [Fact]
    public async Task Concurrent_sends_are_serialized_in_nonce_order()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;
        using var __ = server.Client;

        const int count = 50;
        await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
            transport.SendAsync(PacketType.MercuryReq, BitConverter.GetBytes(i)).AsTask()));

        var seen = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var packet = await FakeAccessPoint.ReadPacketAsync(server.Stream, server.Codec);
            Assert.Equal((byte)PacketType.MercuryReq, packet.Cmd);
            seen.Add(BitConverter.ToInt32(packet.Payload.Span));
        }

        Assert.Equal(count, seen.Count);
    }

    [Fact]
    public void Login_packet_has_upstream_shape()
    {
        var packet = ApConnection.BuildLoginPacket(Credentials.WithAccessToken("tok"), "dev");

        Assert.False(packet.LoginCredentials.HasUsername);
        Assert.Equal(AuthenticationType.AuthenticationSpotifyToken, packet.LoginCredentials.Typ);
        Assert.Equal("tok", packet.LoginCredentials.AuthData.ToStringUtf8());
        Assert.Equal("dev", packet.SystemInfo.DeviceId);
        Assert.Equal($"librespot-{LibrespotVersion.ShaShort}-{LibrespotVersion.BuildId}", packet.SystemInfo.SystemInformationString);
        Assert.Equal("librespot 0.8.0", packet.VersionString);

        var parsed = ClientResponseEncrypted.Parser.ParseFrom(packet.ToByteArray());
        Assert.Equal(packet, parsed);
    }

    [Theory]
    [InlineData("x86_64", CpuFamily.CpuX8664)]
    [InlineData("aarch64", CpuFamily.CpuArm)]
    [InlineData("x86", CpuFamily.CpuX86)]
    [InlineData("riscv64", CpuFamily.CpuUnknown)]
    public void Cpu_family_mapping_matches_upstream(string arch, CpuFamily expected)
    {
        Assert.Equal(expected, ApConnection.CpuFamilyFor(arch));
    }

    [Theory]
    [InlineData("windows", Os.Windows)]
    [InlineData("macos", Os.Osx)]
    [InlineData("netbsd", Os.Freebsd)]
    [InlineData("ios", Os.Iphone)]
    [InlineData("haiku", Os.Unknown)]
    public void Os_mapping_matches_upstream(string os, Os expected)
    {
        Assert.Equal(expected, ApConnection.OsFor(os));
    }

    [Fact]
    public void Login_error_messages_match_upstream()
    {
        Assert.Equal("Try another access point", ApAuthenticationException.LoginErrorMessage(ErrorCode.TryAnotherAp));
        Assert.Equal("Premium account required", ApAuthenticationException.LoginErrorMessage(ErrorCode.PremiumAccountRequired));
    }

    [Fact]
    public async Task ConnectWithRetry_retries_then_fails()
    {
        // The "AP" accepts and immediately closes every connection, so each handshake fails; 1 + 2 retries = 3
        // attempts, then the last error surfaces.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var accepts = 0;
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref accepts);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            var port = (ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var error = await Assert.ThrowsAnyAsync<Exception>(() => ApConnection.ConnectWithRetryAsync("127.0.0.1", port, null, 2));
            Assert.True(error is IOException or System.Net.Sockets.SocketException, error.ToString());
            Assert.Equal(3, Volatile.Read(ref accepts));
        }
        finally
        {
            stop.Cancel();
            await acceptLoop;
            listener.Stop();
        }
    }

    [Fact]
    public async Task Dispose_faults_a_pending_receive_and_rejects_later_calls()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        using var __ = server.Client;

        var receive = transport.ReceiveAsync().AsTask();
        await Task.Delay(50);
        Assert.False(receive.IsCompleted);

        await transport.DisposeAsync();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => receive);
        Assert.True(error is IOException or ObjectDisposedException or OperationCanceledException, error.ToString());

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.ReceiveAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.SendAsync(PacketType.Pong, new byte[4]).AsTask());

        // Disposing twice is a no-op.
        await transport.DisposeAsync();
    }

    [Fact]
    public void Missing_required_fields_fail_to_parse()
    {
        // Upstream rust-protobuf rejects messages with missing required fields; Google.Protobuf does not, so the
        // port checks explicitly.
        var bytes = new APLoginFailed().ToByteArray();
        var error = Assert.Throws<InvalidDataException>(() => ProtobufUtil.ParseRequired(APLoginFailed.Parser, bytes));
        Assert.Equal("Message `APLoginFailed` is missing required fields", error.Message);

        var ok = new APLoginFailed { ErrorCode = ErrorCode.TryAnotherAp }.ToByteArray();
        Assert.Equal(ErrorCode.TryAnotherAp, ProtobufUtil.ParseRequired(APLoginFailed.Parser, ok).ErrorCode);
    }
}
