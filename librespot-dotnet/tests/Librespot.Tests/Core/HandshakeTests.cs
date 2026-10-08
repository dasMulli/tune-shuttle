using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Librespot.Core;
using Librespot.Core.Connection;
using Librespot.Protocol.Authentication;
using Librespot.Protocol.Keyexchange;
using Xunit;
using ProtoPlatform = Librespot.Protocol.Keyexchange.Platform;

namespace Librespot.Tests.Core;

public class HandshakeTests
{
    // ---- ported from handshake.rs #[cfg(test)] ------------------------------------------------------------------

    [Fact]
    public void windows_on_arm_uses_supported_desktop_platform()
    {
        Assert.Equal(ProtoPlatform.Win32X8664, Handshake.PlatformFor("windows", "aarch64"));
        Assert.Equal(ProtoPlatform.Win32X8664, Handshake.PlatformFor("windows", "arm"));
    }

    [Fact]
    public void windows_x86_platforms_are_unchanged()
    {
        Assert.Equal(ProtoPlatform.Win32X8664, Handshake.PlatformFor("windows", "x86_64"));
        Assert.Equal(ProtoPlatform.Win32X86, Handshake.PlatformFor("windows", "x86"));
    }

    // ---- additional -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("android", "aarch64", ProtoPlatform.LinuxArm)]
    [InlineData("linux", "x86_64", ProtoPlatform.LinuxX8664)]
    [InlineData("linux", "riscv64", ProtoPlatform.LinuxX86)]
    [InlineData("macos", "aarch64", ProtoPlatform.OsxX86)]
    [InlineData("ios", "aarch64", ProtoPlatform.IphoneArm64)]
    [InlineData("openbsd", "x86_64", ProtoPlatform.FreebsdX8664)]
    [InlineData("haiku", "x86_64", ProtoPlatform.LinuxX86)]
    public void Platform_mapping_matches_upstream(string os, string arch, ProtoPlatform expected)
    {
        Assert.Equal(expected, Handshake.PlatformFor(os, arch));
    }

    [Fact]
    public void ComputeKeys_matches_hmac_sha1_vector()
    {
        // Vector computed independently with Python's hmac/hashlib:
        // shared = bytes(range(1, 97)), packets = bytes((i * 7 + 3) & 0xff for i in range(200)).
        var shared = Enumerable.Range(1, 96).Select(i => (byte)i).ToArray();
        var packets = Enumerable.Range(0, 200).Select(i => (byte)((i * 7) + 3)).ToArray();

        var (challenge, sendKey, recvKey) = Handshake.ComputeKeys(shared, packets);

        Assert.Equal("38007c5f10d260fd6d239dea6580d81b196be109", Convert.ToHexStringLower(challenge));
        Assert.Equal("4ef27e26f1f9322b646f02d3403bb1529c4f6d8a2a7c9e5de5210c3647f7382b", Convert.ToHexStringLower(sendKey));
        Assert.Equal("c7b2c56a6c293af603cc2cdd14f5b2f5d3adc78888a251017175ca0dac817a4d", Convert.ToHexStringLower(recvKey));
    }

    [Fact]
    public void ClientHello_has_upstream_shape()
    {
        var gc = Enumerable.Range(0, 96).Select(i => (byte)i).ToArray();
        var nonce = new byte[16];
        var hello = Handshake.BuildClientHello(gc, nonce);

        Assert.Equal(Product.Client, hello.BuildInfo.Product);
        Assert.Equal(LibrespotVersion.SpotifyVersion, hello.BuildInfo.Version);
        Assert.Single(hello.BuildInfo.ProductFlags);
        Assert.Equal([Cryptosuite.Shannon], hello.CryptosuitesSupported);
        Assert.Equal(gc, hello.LoginCryptoHello.DiffieHellman.Gc.ToByteArray());
        Assert.Equal(1u, hello.LoginCryptoHello.DiffieHellman.ServerKeysKnown);
        Assert.Equal([0x1e], hello.Padding.ToByteArray());
    }

    [Fact]
    public void Embedded_server_key_is_a_2048_bit_modulus()
    {
        Assert.Equal(256, Handshake.ServerKey.Length);
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Handshake.ServerKey.ToArray(), Exponent = [1, 0, 1] });
        Assert.Equal(2048, rsa.KeySize);
    }

    [Fact]
    public async Task Handshake_and_authenticate_against_fake_access_point()
    {
        using var serverRsa = RSA.Create(2048);
        var modulus = serverRsa.ExportParameters(false).Modulus!;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync();
            var stream = serverClient.GetStream();
            var (serverCodec, hello) = await FakeAccessPoint.HandshakeAsync(stream, serverRsa);
            Assert.Equal(Product.Client, hello.BuildInfo.Product);

            // Expect the login packet, answer with APWelcome.
            var login = await FakeAccessPoint.ReadPacketAsync(stream, serverCodec);
            Assert.Equal((byte)PacketType.Login, login.Cmd);
            var request = ClientResponseEncrypted.Parser.ParseFrom(login.Payload.Span);
            Assert.Equal("user", request.LoginCredentials.Username);
            Assert.Equal(AuthenticationType.AuthenticationUserPass, request.LoginCredentials.Typ);
            Assert.Equal("secret", request.LoginCredentials.AuthData.ToStringUtf8());
            Assert.Equal("device-id", request.SystemInfo.DeviceId);
            Assert.Equal($"librespot {LibrespotVersion.SemVer}", request.VersionString);
            Assert.StartsWith($"librespot-{LibrespotVersion.ShaShort}-", request.SystemInfo.SystemInformationString);

            var welcome = new APWelcome
            {
                CanonicalUsername = "canonical",
                AccountTypeLoggedIn = AccountType.Spotify,
                CredentialsTypeLoggedIn = AccountType.Spotify,
                ReusableAuthCredentialsType = AuthenticationType.AuthenticationStoredSpotifyCredentials,
                ReusableAuthCredentials = ByteString.CopyFromUtf8("reusable"),
            };
            await FakeAccessPoint.WritePacketAsync(stream, serverCodec, (byte)PacketType.ApWelcome, welcome.ToByteArray());

            // Echo one packet back to prove both directions stay in sync.
            var ping = await FakeAccessPoint.ReadPacketAsync(stream, serverCodec);
            await FakeAccessPoint.WritePacketAsync(stream, serverCodec, ping.Cmd, ping.Payload.ToArray());
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var transport = await Handshake.PerformAsync(client.GetStream(), client, modulus, CancellationToken.None);

        var reusable = await ApConnection.AuthenticateAsync(transport, Credentials.WithPassword("user", "secret"), "device-id");
        Assert.Equal("canonical", reusable.Username);
        Assert.Equal(AuthenticationType.AuthenticationStoredSpotifyCredentials, reusable.AuthType);
        Assert.Equal("reusable"u8.ToArray(), reusable.AuthData);

        await transport.SendAsync(PacketType.Pong, new byte[] { 1, 2, 3, 4 });
        var echoed = await transport.ReceiveAsync();
        Assert.NotNull(echoed);
        Assert.Equal((byte)PacketType.Pong, echoed.Value.Cmd);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, echoed.Value.Payload.ToArray());

        await serverTask;
    }

    [Fact]
    public async Task Handshake_rejects_signature_from_unknown_key()
    {
        using var serverRsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync();
            try
            {
                await FakeAccessPoint.HandshakeAsync(serverClient.GetStream(), serverRsa);
            }
            catch (IOException)
            {
                // The client hangs up after rejecting the key.
            }
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Handshake.PerformAsync(client.GetStream(), null, otherRsa.ExportParameters(false).Modulus!, CancellationToken.None));
        Assert.Equal("server key verification failed", error.Message);
        Assert.Equal(ErrorKind.FailedPrecondition, LibrespotException.From(error).Kind);
        client.Dispose();

        await serverTask;
    }

    [Fact]
    public async Task Authenticate_maps_auth_failure_to_login_failed()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;
        using var __ = server.Client;

        var serverTask = Task.Run(async () =>
        {
            await FakeAccessPoint.ReadPacketAsync(server.Stream, server.Codec);
            var failed = new APLoginFailed { ErrorCode = ErrorCode.BadCredentials };
            await FakeAccessPoint.WritePacketAsync(server.Stream, server.Codec, (byte)PacketType.AuthFailure, failed.ToByteArray());
        });

        var error = await Assert.ThrowsAsync<ApAuthenticationException>(() =>
            ApConnection.AuthenticateAsync(transport, Credentials.WithAccessToken("token"), "device"));
        Assert.Equal(ApAuthenticationException.Reason.LoginFailed, error.Cause);
        Assert.Equal(ErrorCode.BadCredentials, error.ErrorCode);
        Assert.Equal(ErrorKind.PermissionDenied, error.Kind);
        Assert.Equal("Permission denied { Login failed with reason: Bad credentials }", error.Message);
        await serverTask;
    }

    [Fact]
    public async Task Authenticate_rejects_unexpected_packet()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;
        using var __ = server.Client;

        var serverTask = Task.Run(async () =>
        {
            await FakeAccessPoint.ReadPacketAsync(server.Stream, server.Codec);
            await FakeAccessPoint.WritePacketAsync(server.Stream, server.Codec, (byte)PacketType.AesKey, [1, 2, 3]);
        });

        var error = await Assert.ThrowsAsync<ApAuthenticationException>(() =>
            ApConnection.AuthenticateAsync(transport, Credentials.WithAccessToken("token"), "device"));
        Assert.Equal(ApAuthenticationException.Reason.Packet, error.Cause);
        Assert.Equal(ErrorKind.Unimplemented, error.Kind);
        Assert.Equal("invalid packet 13", error.Detail);
        await serverTask;
    }

    [Fact]
    public async Task Authenticate_reports_closed_transport()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;

        var serverTask = Task.Run(async () =>
        {
            await FakeAccessPoint.ReadPacketAsync(server.Stream, server.Codec);
            server.Client.Dispose();
        });

        var error = await Assert.ThrowsAsync<ApAuthenticationException>(() =>
            ApConnection.AuthenticateAsync(transport, Credentials.WithAccessToken("token"), "device"));
        Assert.Equal(ApAuthenticationException.Reason.Transport, error.Cause);
        Assert.Equal(ErrorKind.Unavailable, error.Kind);
        await serverTask;
    }

    [Fact]
    public async Task Authenticate_rejects_welcome_missing_required_fields()
    {
        var (transport, server) = await FakeAccessPoint.ConnectedPairAsync();
        await using var _ = transport;
        using var __ = server.Client;

        var serverTask = Task.Run(async () =>
        {
            await FakeAccessPoint.ReadPacketAsync(server.Stream, server.Codec);
            var welcome = new APWelcome { CanonicalUsername = "only-this" };
            await FakeAccessPoint.WritePacketAsync(server.Stream, server.Codec, (byte)PacketType.ApWelcome, welcome.ToByteArray());
        });

        var error = await Assert.ThrowsAsync<LibrespotException>(() =>
            ApConnection.AuthenticateAsync(transport, Credentials.WithAccessToken("token"), "device"));
        Assert.Equal(ErrorKind.FailedPrecondition, error.Kind);
        Assert.Equal("Message `APWelcome` is missing required fields", error.Detail);
        await serverTask;
    }

    [Fact(Skip = "network")]
    public async Task Connect_to_real_access_point_completes_handshake()
    {
        await using var transport = await ApConnection.ConnectWithRetryAsync("ap.spotify.com", 4070, null, 1);
        Assert.NotNull(transport);
    }
}

/// <summary>Server side of the AP handshake, for tests (mirrors upstream's client code from the other end).</summary>
internal static class FakeAccessPoint
{
    internal sealed record Server(TcpClient Client, NetworkStream Stream, ApCodec Codec);

    public static async Task<(ApCodec Codec, ClientHello Hello)> HandshakeAsync(Stream stream, RSA serverRsa)
    {
        var accumulator = new MemoryStream();

        var helloHeader = new byte[6];
        await stream.ReadExactlyAsync(helloHeader);
        Assert.Equal(0, helloHeader[0]);
        Assert.Equal(4, helloHeader[1]);
        var helloSize = (int)BinaryPrimitives.ReadUInt32BigEndian(helloHeader.AsSpan(2));
        var helloBody = new byte[helloSize - 6];
        await stream.ReadExactlyAsync(helloBody);
        accumulator.Write(helloHeader);
        accumulator.Write(helloBody);
        var hello = ClientHello.Parser.ParseFrom(helloBody);

        var serverKeys = DhLocalKeys.Random();
        var gs = serverKeys.PublicKey();
        var signature = serverRsa.SignData(gs, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

        var response = new APResponseMessage
        {
            Challenge = new APChallenge
            {
                LoginCryptoChallenge = new LoginCryptoChallengeUnion
                {
                    DiffieHellman = new LoginCryptoDiffieHellmanChallenge
                    {
                        Gs = ByteString.CopyFrom(gs),
                        ServerSignatureKey = 0,
                        GsSignature = ByteString.CopyFrom(signature),
                    },
                },
                FingerprintChallenge = new FingerprintChallengeUnion(),
                PowChallenge = new PoWChallengeUnion(),
                CryptoChallenge = new CryptoChallengeUnion(),
                ServerNonce = ByteString.CopyFrom(new byte[16]),
            },
        };

        var responseBytes = new byte[4 + response.CalculateSize()];
        BinaryPrimitives.WriteUInt32BigEndian(responseBytes, (uint)responseBytes.Length);
        response.WriteTo(responseBytes.AsSpan(4));
        await stream.WriteAsync(responseBytes);
        accumulator.Write(responseBytes);

        var shared = serverKeys.SharedSecret(hello.LoginCryptoHello.DiffieHellman.Gc.Span);
        var (challenge, clientSendKey, clientRecvKey) = Handshake.ComputeKeys(shared, accumulator.ToArray());

        var plainHeader = new byte[4];
        await stream.ReadExactlyAsync(plainHeader);
        var plainBody = new byte[BinaryPrimitives.ReadUInt32BigEndian(plainHeader) - 4];
        await stream.ReadExactlyAsync(plainBody);
        var plaintext = ClientResponsePlaintext.Parser.ParseFrom(plainBody);
        Assert.Equal(challenge, plaintext.LoginCryptoResponse.DiffieHellman.Hmac.ToByteArray());

        // The server encrypts with the client's receive key and vice versa.
        return (new ApCodec(clientRecvKey, clientSendKey), hello);
    }

    // Bytes read from the socket but not yet decoded, per server codec.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ApCodec, List<byte>> Pending = new();

    public static async Task<ApPacket> ReadPacketAsync(Stream stream, ApCodec codec)
    {
        var data = Pending.GetValue(codec, _ => []);
        var chunk = new byte[4096];
        while (true)
        {
            var sequence = new ReadOnlySequence<byte>(data.ToArray());
            var decoded = codec.TryDecode(ref sequence, out var packet);

            // TryDecode consumes the header and/or the packet: keep only the unconsumed rest.
            var rest = sequence.ToArray();
            data.Clear();
            data.AddRange(rest);
            if (decoded)
            {
                return packet;
            }

            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            data.AddRange(chunk.AsSpan(0, read).ToArray());
        }
    }

    public static async Task WritePacketAsync(Stream stream, ApCodec codec, byte cmd, byte[] payload)
    {
        await stream.WriteAsync(codec.Encode(cmd, payload));
        await stream.FlushAsync();
    }

    /// <summary>A transport and the matching server end, connected over loopback with fixed keys.</summary>
    public static async Task<(Transport Transport, Server Server)> ConnectedPairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var serverClient = await acceptTask;

        var sendKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var recvKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

        var transport = new Transport(client.GetStream(), new ApCodec(sendKey, recvKey), client);
        var server = new Server(serverClient, serverClient.GetStream(), new ApCodec(recvKey, sendKey));
        return (transport, server);
    }
}
