using System.Globalization;
using System.Security.Cryptography;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;

namespace Airplay2.Receiver;

/// <summary>
/// The receiver's identity (SPEC-receiver.md R11): one per installation, created on first start and stored by the host.
/// <see cref="DeviceId"/> is a random locally-administered MAC that doubles as the HAP accessory pairing identifier and
/// the <c>pi</c> TXT value; <see cref="Psi"/> is a UUID; <see cref="Seed"/> is the 32-byte Ed25519 seed from which the
/// advertised <c>pk</c> is derived (storing the public key alone would lose the signing key).
/// </summary>
/// <param name="DeviceId">The <c>deviceid</c>: six upper-case hex bytes separated by colons, locally administered and unicast.</param>
/// <param name="Psi">The <c>psi</c> UUID (upper-case).</param>
/// <param name="Seed">The 32-byte Ed25519 seed (LTSK).</param>
public sealed record ReceiverIdentity(string DeviceId, string Psi, byte[] Seed)
{
    /// <summary>Length of <see cref="Seed"/>.</summary>
    public const int SeedSize = Ed25519KeyPair.KeySize;

    /// <summary>
    /// Creates a fresh identity (SPEC-receiver.md R11): a random MAC with the locally-administered bit set and the
    /// multicast bit cleared, a new UUID and a new Ed25519 seed.
    /// </summary>
    public static ReceiverIdentity Create()
    {
        var mac = new byte[6];
        RandomNumberGenerator.Fill(mac);
        mac[0] = (byte)((mac[0] & 0xFC) | 0x02);
        return new ReceiverIdentity(FormatMac(mac), Guid.NewGuid().ToString().ToUpperInvariant(), Ed25519KeyPair.Generate().Seed.ToArray());
    }

    /// <summary>The Ed25519 key pair rebuilt from <see cref="Seed"/> (SPEC-receiver.md R11: <c>Ed25519KeyPair.FromSeed</c>).</summary>
    public Ed25519KeyPair KeyPair => Ed25519KeyPair.FromSeed(Seed);

    /// <summary>The 32-byte public key advertised as <c>pk</c> (SPEC-receiver.md 3.1) and sent in <c>/info</c> (3.2).</summary>
    public byte[] PublicKey => KeyPair.PublicKey;

    /// <summary>The <c>pk</c> TXT value: 64 lower-case hex characters (SPEC-control.md 1.2).</summary>
    public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();

    /// <summary>The device id without colons, the prefix of the <c>_raop._tcp</c> instance name (SPEC-receiver.md 3.1).</summary>
    public string DeviceIdCompact => DeviceId.Replace(":", string.Empty, StringComparison.Ordinal);

    /// <summary>The identity in the shape <c>HapAccessoryPairing</c> takes: the device id as pairing identifier with the same seed (SPEC-receiver.md R11, 4.2).</summary>
    public ControllerIdentity ToAccessoryIdentity() => new(DeviceId, Seed);

    /// <summary>True when <paramref name="deviceId"/> has the <c>XX:XX:XX:XX:XX:XX</c> shape and is locally administered and unicast (SPEC-receiver.md R11).</summary>
    public static bool IsLocallyAdministeredMac(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var parts = deviceId.Split(':');
        if (parts.Length != 6)
        {
            return false;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        return (bytes[0] & 0x02) != 0 && (bytes[0] & 0x01) == 0;
    }

    private static string FormatMac(byte[] mac) => string.Join(':', mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
