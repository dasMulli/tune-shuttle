// Port of core/src/connection/handshake.rs.
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Librespot.Protocol.Keyexchange;
using ProtoPlatform = Librespot.Protocol.Keyexchange.Platform;

namespace Librespot.Core.Connection;

/// <summary>Upstream <c>HandshakeError</c> texts; surfaced as <see cref="InvalidDataException"/> like upstream's <c>io::ErrorKind::InvalidData</c>.</summary>
internal static class HandshakeError
{
    /// <summary>Upstream <c>HandshakeError::VerificationFailed</c>.</summary>
    public const string VerificationFailed = "server key verification failed";
}

/// <summary>The access point key exchange (upstream <c>connection::handshake</c>).</summary>
public static class Handshake
{
    /// <summary>Spotify's RSA public key modulus (exponent 65537) that signs the AP's Diffie-Hellman key.</summary>
    internal static ReadOnlySpan<byte> ServerKey =>
    [
        0xac, 0xe0, 0x46, 0x0b, 0xff, 0xc2, 0x30, 0xaf, 0xf4, 0x6b, 0xfe, 0xc3, 0xbf, 0xbf, 0x86, 0x3d,
        0xa1, 0x91, 0xc6, 0xcc, 0x33, 0x6c, 0x93, 0xa1, 0x4f, 0xb3, 0xb0, 0x16, 0x12, 0xac, 0xac, 0x6a,
        0xf1, 0x80, 0xe7, 0xf6, 0x14, 0xd9, 0x42, 0x9d, 0xbe, 0x2e, 0x34, 0x66, 0x43, 0xe3, 0x62, 0xd2,
        0x32, 0x7a, 0x1a, 0x0d, 0x92, 0x3b, 0xae, 0xdd, 0x14, 0x02, 0xb1, 0x81, 0x55, 0x05, 0x61, 0x04,
        0xd5, 0x2c, 0x96, 0xa4, 0x4c, 0x1e, 0xcc, 0x02, 0x4a, 0xd4, 0xb2, 0x0c, 0x00, 0x1f, 0x17, 0xed,
        0xc2, 0x2f, 0xc4, 0x35, 0x21, 0xc8, 0xf0, 0xcb, 0xae, 0xd2, 0xad, 0xd7, 0x2b, 0x0f, 0x9d, 0xb3,
        0xc5, 0x32, 0x1a, 0x2a, 0xfe, 0x59, 0xf3, 0x5a, 0x0d, 0xac, 0x68, 0xf1, 0xfa, 0x62, 0x1e, 0xfb,
        0x2c, 0x8d, 0x0c, 0xb7, 0x39, 0x2d, 0x92, 0x47, 0xe3, 0xd7, 0x35, 0x1a, 0x6d, 0xbd, 0x24, 0xc2,
        0xae, 0x25, 0x5b, 0x88, 0xff, 0xab, 0x73, 0x29, 0x8a, 0x0b, 0xcc, 0xcd, 0x0c, 0x58, 0x67, 0x31,
        0x89, 0xe8, 0xbd, 0x34, 0x80, 0x78, 0x4a, 0x5f, 0xc9, 0x6b, 0x89, 0x9d, 0x95, 0x6b, 0xfc, 0x86,
        0xd7, 0x4f, 0x33, 0xa6, 0x78, 0x17, 0x96, 0xc9, 0xc3, 0x2d, 0x0d, 0x32, 0xa5, 0xab, 0xcd, 0x05,
        0x27, 0xe2, 0xf7, 0x10, 0xa3, 0x96, 0x13, 0xc4, 0x2f, 0x99, 0xc0, 0x27, 0xbf, 0xed, 0x04, 0x9c,
        0x3c, 0x27, 0x58, 0x04, 0xb6, 0xb2, 0x19, 0xf9, 0xc1, 0x2f, 0x02, 0xe9, 0x48, 0x63, 0xec, 0xa1,
        0xb6, 0x42, 0xa0, 0x9d, 0x48, 0x25, 0xf8, 0xb3, 0x9d, 0xd0, 0xe8, 0x6a, 0xf9, 0x48, 0x4d, 0xa1,
        0xc2, 0xba, 0x86, 0x30, 0x42, 0xea, 0x9d, 0xb3, 0x08, 0x6c, 0x19, 0x0e, 0x48, 0xb3, 0x9d, 0x66,
        0xeb, 0x00, 0x06, 0xa2, 0x5a, 0xee, 0xa1, 0x1b, 0x13, 0x87, 0x3c, 0xd7, 0x19, 0xe6, 0x55, 0xbd,
    ];

#if DEBUG
    private const ProductFlags ProductFlag = ProductFlags.ProductFlagDevBuild;
#else
    private const ProductFlags ProductFlag = ProductFlags.ProductFlagNone;
#endif

    /// <summary>
    /// Performs the key exchange on a freshly connected socket and returns the transport whose
    /// <see cref="ApCodec"/> is keyed with the derived send/recv keys. The transport owns <paramref name="client"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The server's key signature did not verify.</exception>
    /// <exception cref="IOException">The connection failed or closed early.</exception>
    public static Task<Transport> PerformAsync(TcpClient client, CancellationToken cancellationToken = default) =>
        PerformAsync(client.GetStream(), client, ServerKey.ToArray(), cancellationToken);

    /// <summary>
    /// The handshake over any duplex stream, verifying the AP key against <paramref name="serverKeyModulus"/>
    /// (always <see cref="ServerKey"/> outside tests).
    /// </summary>
    internal static async Task<Transport> PerformAsync(Stream connection, IDisposable? owner, byte[] serverKeyModulus, CancellationToken cancellationToken)
    {
        var localKeys = DhLocalKeys.Random();
        var gc = localKeys.PublicKey();
        var accumulator = await ClientHelloAsync(connection, gc, cancellationToken).ConfigureAwait(false);
        var message = await RecvPacketAsync(connection, accumulator, APResponseMessage.Parser, cancellationToken).ConfigureAwait(false);

        // Upstream `get_or_default()` on every level: missing sub-messages read as empty.
        var dh = message.Challenge?.LoginCryptoChallenge?.DiffieHellman;
        var remoteKey = dh?.Gs.ToByteArray() ?? [];
        var remoteSignature = dh?.GsSignature.ToByteArray() ?? [];

        // Prevent man-in-the-middle attacks: check server signature
        if (!VerifyServerSignature(serverKeyModulus, remoteKey, remoteSignature))
        {
            throw new InvalidDataException(HandshakeError.VerificationFailed);
        }

        // OK to proceed
        var sharedSecret = localKeys.SharedSecret(remoteKey);
        var (challenge, sendKey, recvKey) = ComputeKeys(sharedSecret, accumulator.ToArray());
        var codec = new ApCodec(sendKey, recvKey);

        await ClientResponseAsync(connection, challenge, cancellationToken).ConfigureAwait(false);

        return new Transport(connection, codec, owner);
    }

    private static bool VerifyServerSignature(byte[] modulus, byte[] remoteKey, byte[] remoteSignature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = [0x01, 0x00, 0x01] });
            return rsa.VerifyData(remoteKey, remoteSignature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            // e.g. a signature of the wrong length; upstream maps every failure to VerificationFailed.
            return false;
        }
    }

    /// <summary>
    /// Upstream <c>platform_for(os, arch)</c>, with <c>os</c>/<c>arch</c> in Rust's <c>std::env::consts</c> vocabulary.
    /// </summary>
    internal static ProtoPlatform PlatformFor(string os, string arch) => os switch
    {
        "freebsd" or "netbsd" or "openbsd" => arch switch
        {
            "x86_64" => ProtoPlatform.FreebsdX8664,
            _ => ProtoPlatform.FreebsdX86,
        },
        "ios" => arch switch
        {
            "aarch64" => ProtoPlatform.IphoneArm64,
            _ => ProtoPlatform.IphoneArm,
        },
        // Rather than sending `Platform::PLATFORM_ANDROID_ARM` for "android",
        // we are spoofing "android" as "linux", as otherwise during Session::connect
        // all APs will reject the client with TryAnotherAP, no matter the credentials
        // used was obtained via OAuth using KEYMASTER or ANDROID's client ID or
        // Login5Manager::login
        "linux" or "android" => arch switch
        {
            "arm" or "aarch64" => ProtoPlatform.LinuxArm,
            "blackfin" => ProtoPlatform.LinuxBlackfin,
            "mips" => ProtoPlatform.LinuxMips,
            "sh" => ProtoPlatform.LinuxSh,
            "x86_64" => ProtoPlatform.LinuxX8664,
            _ => ProtoPlatform.LinuxX86,
        },
        "macos" => arch switch
        {
            "ppc" or "ppc64" => ProtoPlatform.OsxPpc,
            "x86_64" => ProtoPlatform.OsxX8664,
            _ => ProtoPlatform.OsxX86,
        },
        // Spotify APs reject the legacy Windows CE ARM platform even for Premium
        // accounts. Windows on ARM is a desktop client, so identify it as Win32.
        "windows" => arch switch
        {
            "arm" or "aarch64" or "x86_64" => ProtoPlatform.Win32X8664,
            _ => ProtoPlatform.Win32X86,
        },
        _ => ProtoPlatform.LinuxX86,
    };

    /// <summary>Builds the upstream <c>ClientHello</c> for the given DH public key and nonce.</summary>
    internal static ClientHello BuildClientHello(byte[] gc, byte[] clientNonce)
    {
        var platform = PlatformFor(Core.Platform.Os, ApConnection.RustArch);

        var packet = new ClientHello
        {
            BuildInfo = new BuildInfo
            {
                // ProductInfo won't push autoplay and perhaps other settings
                // when set to anything else than PRODUCT_CLIENT
                Product = Product.Client,
                Platform = platform,
                Version = LibrespotVersion.SpotifyVersion,
            },
            LoginCryptoHello = new LoginCryptoHelloUnion
            {
                DiffieHellman = new LoginCryptoDiffieHellmanHello
                {
                    Gc = ByteString.CopyFrom(gc),
                    ServerKeysKnown = 1,
                },
            },
            ClientNonce = ByteString.CopyFrom(clientNonce),
            Padding = ByteString.CopyFrom(0x1e),
        };
        packet.BuildInfo.ProductFlags.Add(ProductFlag);
        packet.CryptosuitesSupported.Add(Cryptosuite.Shannon);
        return packet;
    }

    /// <summary>Sends the ClientHello framed as <c>00 04 | u32 BE size | ClientHello</c> and returns the sent bytes.</summary>
    private static async Task<MemoryStream> ClientHelloAsync(Stream connection, byte[] gc, CancellationToken cancellationToken)
    {
        var clientNonce = new byte[0x10];
        RandomNumberGenerator.Fill(clientNonce);

        var packet = BuildClientHello(gc, clientNonce);

        var size = 2 + 4 + packet.CalculateSize();
        var buffer = new byte[size];
        buffer[0] = 0;
        buffer[1] = 4;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(2), checked((uint)size));
        packet.WriteTo(buffer.AsSpan(6));

        await connection.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await connection.FlushAsync(cancellationToken).ConfigureAwait(false);

        var accumulator = new MemoryStream();
        accumulator.Write(buffer);
        return accumulator;
    }

    /// <summary>Sends the <c>ClientResponsePlaintext</c> framed as <c>u32 BE size | message</c>.</summary>
    private static async Task ClientResponseAsync(Stream connection, byte[] challenge, CancellationToken cancellationToken)
    {
        var packet = new ClientResponsePlaintext
        {
            LoginCryptoResponse = new LoginCryptoResponseUnion
            {
                DiffieHellman = new LoginCryptoDiffieHellmanResponse { Hmac = ByteString.CopyFrom(challenge) },
            },
            PowResponse = new PoWResponseUnion(),
            CryptoResponse = new CryptoResponseUnion(),
        };

        var size = 4 + packet.CalculateSize();
        var buffer = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, checked((uint)size));
        packet.WriteTo(buffer.AsSpan(4));

        await connection.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await connection.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one <c>u32 BE size | message</c> packet, appending the raw bytes to <paramref name="acc"/>.</summary>
    private static async Task<T> RecvPacketAsync<T>(Stream connection, MemoryStream acc, MessageParser<T> parser, CancellationToken cancellationToken)
        where T : IMessage<T>
    {
        var header = await ReadIntoAccumulatorAsync(connection, 4, acc, cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size < 4)
        {
            // Upstream's `size - 4` would underflow and panic.
            throw new InvalidDataException($"invalid handshake packet size {size}");
        }

        var data = await ReadIntoAccumulatorAsync(connection, checked((int)(size - 4)), acc, cancellationToken).ConfigureAwait(false);
        return ProtobufUtil.ParseRequired(parser, data);
    }

    private static async Task<byte[]> ReadIntoAccumulatorAsync(Stream connection, int size, MemoryStream acc, CancellationToken cancellationToken)
    {
        var data = new byte[size];
        await connection.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        acc.Write(data);
        return data;
    }

    /// <summary>
    /// Upstream <c>compute_keys(shared_secret, packets)</c>: HMAC-SHA1(shared_secret, packets || i) for i in 1..=5
    /// gives 100 bytes of key material; the challenge is HMAC-SHA1(material[..20], packets), the send key
    /// material[20..52] and the receive key material[52..84]. Upstream's <c>HandshakeError::InvalidLength</c> cannot
    /// occur: .NET's HMAC accepts keys of any length.
    /// </summary>
    internal static (byte[] Challenge, byte[] SendKey, byte[] RecvKey) ComputeKeys(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> packets)
    {
        var data = new byte[5 * HMACSHA1.HashSizeInBytes];
        var input = new byte[packets.Length + 1];
        packets.CopyTo(input);

        for (byte i = 1; i < 6; i++)
        {
            input[^1] = i;
            HMACSHA1.HashData(sharedSecret, input, data.AsSpan((i - 1) * HMACSHA1.HashSizeInBytes));
        }

        var challenge = HMACSHA1.HashData(data.AsSpan(0, 0x14), packets);

        return (challenge, data[0x14..0x34], data[0x34..0x54]);
    }
}
