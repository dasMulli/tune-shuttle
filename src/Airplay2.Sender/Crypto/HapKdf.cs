using System.Security.Cryptography;
using System.Text;

namespace Airplay2.Sender.Crypto;

/// <summary>
/// HKDF-SHA-512 as HomeKit uses it (SPEC-control.md 2.5, 3.4): salt and info are ASCII strings without terminator,
/// output length 32 unless stated otherwise. Wraps <see cref="HKDF"/> (RFC 5869 extract-then-expand).
/// </summary>
public static class HapKdf
{
    /// <summary>Default HKDF output length used by every HAP derivation.</summary>
    public const int DefaultKeyLength = 32;

    /// <summary>Derives <paramref name="length"/> bytes from <paramref name="ikm"/> with the given ASCII salt and info.</summary>
    public static byte[] Derive(ReadOnlySpan<byte> ikm, string salt, string info, int length = DefaultKeyLength) =>
        Derive(HashAlgorithmName.SHA512, ikm, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info), length);

    /// <summary>Derives with raw salt and info bytes and any hash (the RFC 5869 test vectors use SHA-256 and binary salt/info).</summary>
    public static byte[] Derive(HashAlgorithmName hash, ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int length)
    {
        var output = new byte[length];
        HKDF.DeriveKey(hash, ikm, output, salt, info);
        return output;
    }
}

/// <summary>The HKDF salt/info string pairs of the HomeKit pairing and channel key schedule (SPEC-control.md 3.4).</summary>
public static class HapKeyInfo
{
    public const string PairSetupEncryptSalt = "Pair-Setup-Encrypt-Salt";
    public const string PairSetupEncryptInfo = "Pair-Setup-Encrypt-Info";
    public const string PairSetupControllerSignSalt = "Pair-Setup-Controller-Sign-Salt";
    public const string PairSetupControllerSignInfo = "Pair-Setup-Controller-Sign-Info";
    public const string PairSetupAccessorySignSalt = "Pair-Setup-Accessory-Sign-Salt";
    public const string PairSetupAccessorySignInfo = "Pair-Setup-Accessory-Sign-Info";
    public const string PairVerifyEncryptSalt = "Pair-Verify-Encrypt-Salt";
    public const string PairVerifyEncryptInfo = "Pair-Verify-Encrypt-Info";
    public const string ControlSalt = "Control-Salt";
    public const string ControlWriteKeyInfo = "Control-Write-Encryption-Key";
    public const string ControlReadKeyInfo = "Control-Read-Encryption-Key";
    public const string EventsSalt = "Events-Salt";
    public const string EventsWriteKeyInfo = "Events-Write-Encryption-Key";
    public const string EventsReadKeyInfo = "Events-Read-Encryption-Key";
    public const string DataStreamSaltPrefix = "DataStream-Salt";
    public const string DataStreamOutputKeyInfo = "DataStream-Output-Encryption-Key";
    public const string DataStreamInputKeyInfo = "DataStream-Input-Encryption-Key";

    /// <summary>Nonce labels of the pairing AEAD operations: the 12-byte nonce is four zero bytes followed by these 8 ASCII bytes.</summary>
    public const string PairSetupM5Nonce = "PS-Msg05";
    public const string PairSetupM6Nonce = "PS-Msg06";
    public const string PairVerifyM2Nonce = "PV-Msg02";
    public const string PairVerifyM3Nonce = "PV-Msg03";
}
