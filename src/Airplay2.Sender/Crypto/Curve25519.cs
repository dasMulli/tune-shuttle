using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Airplay2.Sender.Crypto;

/// <summary>
/// X25519 key agreement (RFC 7748) for pair-verify. .NET 10 has no X25519 in the BCL, so this wraps BouncyCastle's
/// <see cref="X25519"/>; the API is span-based and the private key is always 32 bytes.
/// </summary>
public sealed class X25519KeyPair
{
    /// <summary>Key length in bytes for private key, public key and shared secret.</summary>
    public const int KeySize = 32;

    private readonly byte[] _privateKey;

    private X25519KeyPair(byte[] privateKey, byte[] publicKey)
    {
        _privateKey = privateKey;
        PublicKey = publicKey;
    }

    /// <summary>The 32-byte public key (u-coordinate, little-endian as on the wire).</summary>
    public byte[] PublicKey { get; }

    /// <summary>The private scalar (not clamped; clamping happens inside the scalar multiplication).</summary>
    public ReadOnlySpan<byte> PrivateKey => _privateKey;

    /// <summary>Generates a fresh ephemeral key pair.</summary>
    public static X25519KeyPair Generate()
    {
        var sk = new byte[KeySize];
        RandomNumberGenerator.Fill(sk);
        return FromPrivateKey(sk);
    }

    /// <summary>Builds the key pair for a given 32-byte private key.</summary>
    public static X25519KeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeySize)
        {
            throw new ArgumentException("X25519 private keys are 32 bytes.", nameof(privateKey));
        }

        var sk = privateKey.ToArray();
        var pk = new byte[KeySize];
        X25519.ScalarMultBase(sk, 0, pk, 0);
        return new X25519KeyPair(sk, pk);
    }

    /// <summary>Computes the shared secret with the peer's public key. Throws if the result is all zero (low-order point).</summary>
    public byte[] Agree(ReadOnlySpan<byte> peerPublicKey)
    {
        if (peerPublicKey.Length != KeySize)
        {
            throw new ArgumentException("X25519 public keys are 32 bytes.", nameof(peerPublicKey));
        }

        var shared = new byte[KeySize];
        if (!X25519.CalculateAgreement(_privateKey, 0, peerPublicKey.ToArray(), 0, shared, 0))
        {
            throw new CryptographicException("X25519 agreement produced the all-zero shared secret.");
        }

        return shared;
    }

    /// <summary>Raw scalar multiplication <c>k * u</c> (RFC 7748 section 5.2 test vectors).</summary>
    public static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> u)
    {
        var result = new byte[KeySize];
        X25519.ScalarMult(scalar.ToArray(), 0, u.ToArray(), 0, result, 0);
        return result;
    }
}

/// <summary>
/// Ed25519 signatures (RFC 8032) for the HomeKit long-term identity keys. Wraps BouncyCastle's <see cref="Ed25519"/>.
/// A key pair is identified by its 32-byte seed ("private key"); pyatv persists the same 32 bytes as <c>ltsk</c>.
/// </summary>
public sealed class Ed25519KeyPair
{
    /// <summary>Seed and public key length in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>Signature length in bytes.</summary>
    public const int SignatureSize = 64;

    private readonly byte[] _seed;

    private Ed25519KeyPair(byte[] seed, byte[] publicKey)
    {
        _seed = seed;
        PublicKey = publicKey;
    }

    /// <summary>The 32-byte public key (LTPK on the wire).</summary>
    public byte[] PublicKey { get; }

    /// <summary>The 32-byte seed to persist (LTSK).</summary>
    public ReadOnlySpan<byte> Seed => _seed;

    /// <summary>Generates a fresh long-term key pair.</summary>
    public static Ed25519KeyPair Generate()
    {
        var seed = new byte[KeySize];
        RandomNumberGenerator.Fill(seed);
        return FromSeed(seed);
    }

    /// <summary>Rebuilds the key pair from a persisted 32-byte seed.</summary>
    public static Ed25519KeyPair FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != KeySize)
        {
            throw new ArgumentException("Ed25519 seeds are 32 bytes.", nameof(seed));
        }

        var sk = seed.ToArray();
        var pk = new byte[KeySize];
        Ed25519.GeneratePublicKey(sk, 0, pk, 0);
        return new Ed25519KeyPair(sk, pk);
    }

    /// <summary>Signs <paramref name="message"/> and returns the 64-byte signature.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var sig = new byte[SignatureSize];
        var m = message.ToArray();
        Ed25519.Sign(_seed, 0, PublicKey, 0, m, 0, m.Length, sig, 0);
        return sig;
    }

    /// <summary>Verifies a signature against a 32-byte public key.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != KeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        var m = message.ToArray();
        return Ed25519.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, m, 0, m.Length);
    }
}
