// Test helper: the Spotify-client side of the zeroconf exchange (getInfo -> addUser), written independently of
// src/Librespot.Discovery/Server.cs so the server's crypto path is checked end to end.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Librespot.Core;
using Librespot.Core.Crypto;
using Librespot.Protocol.Authentication;

namespace Librespot.Tests.Discovery;

internal static class ZeroconfClient
{
    /// <summary>Builds the form body of an <c>addUser</c> request for <paramref name="serverPublicKey"/>.</summary>
    public static string AddUserBody(
        byte[] serverPublicKey, string username, string deviceId, byte[] authData, bool corruptMac = false, DhLocalKeys? clientKeys = null)
    {
        clientKeys ??= DhLocalKeys.Random();
        var shared = clientKeys.SharedSecret(serverPublicKey);

        var baseKey = SHA1.HashData(shared)[..16];
        var checksumKey = HMACSHA1.HashData(baseKey, Encoding.ASCII.GetBytes("checksum"));
        var encryptionKey = HMACSHA1.HashData(baseKey, Encoding.ASCII.GetBytes("encryption"))[..16];

        // Inner blob: what Credentials.WithBlob decodes (base64 text of the AES-192-ECB encrypted credentials).
        var inner = Encoding.ASCII.GetBytes(Convert.ToBase64String(EncryptCredentialsBlob(username, deviceId, authData)));

        var iv = RandomNumberGenerator.GetBytes(16);
        var encrypted = (byte[])inner.Clone();
        AesCtr.Transform(encryptionKey, iv, 0, encrypted);
        var mac = HMACSHA1.HashData(checksumKey, encrypted);
        if (corruptMac)
        {
            mac[0] ^= 0xff;
        }

        var blob = Convert.ToBase64String([.. iv, .. encrypted, .. mac]);
        return "action=addUser"
            + "&userName=" + Uri.EscapeDataString(username)
            + "&blob=" + Uri.EscapeDataString(blob)
            + "&clientKey=" + Uri.EscapeDataString(Convert.ToBase64String(clientKeys.PublicKey()));
    }

    private static byte[] EncryptCredentialsBlob(string username, string deviceId, byte[] authData)
    {
        var ms = new MemoryStream();
        ms.WriteByte(0x49);
        WriteBytes(ms, Encoding.UTF8.GetBytes(username));
        ms.WriteByte(0x50);
        WriteInt(ms, (uint)AuthenticationType.AuthenticationStoredSpotifyCredentials);
        ms.WriteByte(0x51);
        WriteBytes(ms, authData);
        var plaintext = ms.ToArray();

        var data = new byte[(plaintext.Length + 15) / 16 * 16];
        plaintext.CopyTo(data, 0);
        for (var j = 16; j < data.Length; j++)
        {
            data[j] ^= data[j - 16];
        }

        var secret = SHA1.HashData(Encoding.UTF8.GetBytes(deviceId));
        var derived = Rfc2898DeriveBytes.Pbkdf2(secret, Encoding.UTF8.GetBytes(username), 0x100, HashAlgorithmName.SHA1, 20);
        var key = new byte[24];
        SHA1.HashData(derived, key);
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(20), 20);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.EncryptEcb(data, data, PaddingMode.None);
        return data;
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
}
