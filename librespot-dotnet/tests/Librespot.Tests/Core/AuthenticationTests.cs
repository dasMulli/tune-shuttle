using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Librespot.Core;
using Librespot.Protocol.Authentication;
using Xunit;

namespace Librespot.Tests.Core;

public class AuthenticationTests
{
    private const string Username = "testuser";
    private static readonly byte[] DeviceId = "0123456789abcdef0123456789abcdef01234567"u8.ToArray();

    // AES-192 key for (DeviceId, Username) computed independently with Python:
    // sha1(pbkdf2_hmac('sha1', sha1(device_id), username, 0x100, 20)) || be_u32(20)
    private const string ExpectedKeyHex = "63051338ca0b6e65e0c596c20b000c592a0c020500000014";

    [Fact]
    public void Key_derivation_matches_reference()
    {
        Assert.Equal(ExpectedKeyHex, Convert.ToHexStringLower(DeriveKey(Username, DeviceId)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void With_blob_decrypts_known_plaintext(int paddingBytes)
    {
        var authData = Enumerable.Range(0, 200).Select(i => (byte)(i * 3)).ToArray(); // length 200 needs the 2-byte varint
        var plaintext = BuildPlaintext(Encoding.UTF8.GetBytes(Username), (uint)AuthenticationType.AuthenticationStoredSpotifyCredentials, authData);

        // Pad to a block multiple, then optionally append a partial block (left untouched by ECB, like upstream).
        var padded = new byte[((plaintext.Length + 15) / 16 * 16) + paddingBytes];
        plaintext.CopyTo(padded, 0);

        var blob = Encrypt(padded, Convert.FromHexString(ExpectedKeyHex));

        var credentials = Credentials.WithBlob(Username, Encoding.ASCII.GetBytes(Convert.ToBase64String(blob)), DeviceId);

        Assert.Equal(Username, credentials.Username);
        Assert.Equal(AuthenticationType.AuthenticationStoredSpotifyCredentials, credentials.AuthType);
        Assert.Equal(authData, credentials.AuthData);
    }

    [Fact]
    public void With_blob_rejects_unknown_auth_type()
    {
        var plaintext = BuildPlaintext([1, 2], 99, [3]);
        var padded = new byte[(plaintext.Length + 15) / 16 * 16];
        plaintext.CopyTo(padded, 0);
        var blob = Encrypt(padded, DeriveKey(Username, DeviceId));

        var error = Assert.Throws<AuthenticationException>(() =>
            Credentials.WithBlob(Username, Encoding.ASCII.GetBytes(Convert.ToBase64String(blob)), DeviceId));
        Assert.Equal(AuthenticationException.Reason.AuthType, error.Cause);
        Assert.Equal("unknown authentication type 99", error.Detail);
        Assert.Equal(ErrorKind.InvalidArgument, error.Kind);
    }

    [Fact]
    public void With_blob_rejects_invalid_base64()
    {
        var error = Assert.Throws<LibrespotException>(() => Credentials.WithBlob(Username, "not base64!"u8, DeviceId));
        Assert.Equal(ErrorKind.FailedPrecondition, error.Kind);
    }

    [Fact]
    public void With_blob_rejects_truncated_blob()
    {
        var blob = Encrypt(new byte[32], DeriveKey(Username, DeviceId));
        // All-zero plaintext: u8, bytes(len 0), u8, int 0 (UserPass), u8, bytes(len 0) -> valid. Truncate instead.
        var error = Assert.Throws<LibrespotException>(() =>
            Credentials.WithBlob(Username, Encoding.ASCII.GetBytes(Convert.ToBase64String(blob[..3])), DeviceId));
        Assert.Equal(ErrorKind.FailedPrecondition, error.Kind);
    }

    [Fact]
    public void Credentials_json_roundtrip_matches_upstream_shape()
    {
        var credentials = Credentials.WithPassword("user", "pass");
        var json = System.Text.Json.JsonSerializer.Serialize(credentials);
        Assert.Equal("{\"username\":\"user\",\"auth_type\":0,\"auth_data\":\"cGFzcw==\"}", json);
        Assert.Equal(credentials, System.Text.Json.JsonSerializer.Deserialize<Credentials>(json));
    }

    private static byte[] DeriveKey(string username, byte[] deviceId)
    {
        var secret = SHA1.HashData(deviceId);
        var derived = Rfc2898DeriveBytes.Pbkdf2(secret, Encoding.UTF8.GetBytes(username), 0x100, HashAlgorithmName.SHA1, 20);
        var key = new byte[24];
        SHA1.HashData(derived, key);
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(20), 20);
        return key;
    }

    private static byte[] BuildPlaintext(byte[] first, uint authType, byte[] authData)
    {
        var ms = new MemoryStream();
        ms.WriteByte(0x49);
        WriteBytes(ms, first);
        ms.WriteByte(0x50);
        WriteInt(ms, authType);
        ms.WriteByte(0x51);
        WriteBytes(ms, authData);
        return ms.ToArray();
    }

    private static void WriteInt(Stream s, uint value)
    {
        if (value < 0x80)
        {
            s.WriteByte((byte)value);
            return;
        }

        s.WriteByte((byte)((value & 0x7f) | 0x80));
        s.WriteByte((byte)(value >> 7));
    }

    private static void WriteBytes(Stream s, byte[] data)
    {
        WriteInt(s, (uint)data.Length);
        s.Write(data);
    }

    /// <summary>Inverse of upstream's decrypt: chain forward (data[j] ^= data[j - 16]), then AES-192-ECB full blocks.</summary>
    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var data = (byte[])plaintext.Clone();
        for (var j = 16; j < data.Length; j++)
        {
            data[j] ^= data[j - 16];
        }

        using var aes = Aes.Create();
        aes.Key = key;
        var full = data.Length / 16 * 16;
        aes.EncryptEcb(data.AsSpan(0, full), data.AsSpan(0, full), PaddingMode.None);
        return data;
    }
}
