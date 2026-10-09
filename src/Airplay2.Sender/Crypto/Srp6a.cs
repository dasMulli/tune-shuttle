using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Airplay2.Sender.Crypto;

/// <summary>
/// SRP-6a with the HomeKit parameters (SPEC-control.md 3.3): the RFC 5054 3072-bit group with g = 5, H = SHA-512,
/// <c>k = H(N || PAD(g))</c>, <c>u = H(PAD(A) || PAD(B))</c>, <c>x = H(s || H(I || ":" || P))</c>, <c>K = H(S)</c>,
/// <c>M1 = H((H(N) xor H(g)) || H(I) || s || A || B || K)</c>, <c>M2 = H(A || M1 || K)</c>. Public values travel
/// big-endian without leading-zero padding; <c>PAD()</c> left-pads to the 384-byte group size.
/// Hand-written over <see cref="BigInteger"/>; no third-party SRP code.
/// </summary>
public static class Srp6a
{
    /// <summary>The 3072-bit prime from RFC 5054 appendix A.</summary>
    public static readonly BigInteger N = BigInteger.Parse(
        "0" +
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183995497CEA956AE515D2261898FA0510" +
        "15728E5A8AAAC42DAD33170D04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D87602733EC86A64521F2B18177B200C" +
        "BBE117577A615D6C770988C0BAD946E208E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF",
        NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>The generator.</summary>
    public static readonly BigInteger G = new(5);

    /// <summary>Byte length of N (384).</summary>
    public const int GroupSize = 384;

    /// <summary>Hash output length (SHA-512).</summary>
    public const int HashSize = 64;

    /// <summary>HAP username for pair-setup.</summary>
    public const string PairSetupUsername = "Pair-Setup";

    /// <summary>The fixed PIN of transient pair-setup.</summary>
    public const string TransientPin = "3939";

    /// <summary>The multiplier <c>k = H(N || PAD(g))</c>.</summary>
    public static readonly BigInteger K = ToInt(Hash(ToBytes(N), Pad(G)));

    /// <summary>SHA-512 over the concatenation of the arguments.</summary>
    public static byte[] Hash(params ReadOnlySpan<byte[]> parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts)
        {
            sha.AppendData(p);
        }

        return sha.GetHashAndReset();
    }

    /// <summary>Unsigned big-endian bytes without leading zeros (the wire form of A, B and the salt-free values).</summary>
    public static byte[] ToBytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

    /// <summary>Unsigned big-endian bytes left-padded to <see cref="GroupSize"/>.</summary>
    public static byte[] Pad(BigInteger value)
    {
        var raw = ToBytes(value);
        if (raw.Length >= GroupSize)
        {
            return raw;
        }

        var padded = new byte[GroupSize];
        raw.CopyTo(padded, GroupSize - raw.Length);
        return padded;
    }

    /// <summary>Parses unsigned big-endian bytes.</summary>
    public static BigInteger ToInt(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    /// <summary><c>x = H(s || H(I || ":" || P))</c>.</summary>
    public static BigInteger ComputeX(ReadOnlySpan<byte> salt, string username, string password)
    {
        var inner = Hash(Encoding.UTF8.GetBytes(username + ":" + password));
        return ToInt(Hash(salt.ToArray(), inner));
    }

    /// <summary><c>v = g^x mod N</c>, the verifier a server stores (used by the fake receiver).</summary>
    public static BigInteger ComputeVerifier(ReadOnlySpan<byte> salt, string username, string password) =>
        BigInteger.ModPow(G, ComputeX(salt, username, password), N);

    /// <summary><c>u = H(PAD(A) || PAD(B))</c>.</summary>
    public static BigInteger ComputeU(BigInteger a, BigInteger b) => ToInt(Hash(Pad(a), Pad(b)));

    /// <summary><c>M1 = H((H(N) xor H(g)) || H(I) || s || A || B || K)</c>.</summary>
    public static byte[] ComputeClientProof(string username, ReadOnlySpan<byte> salt, BigInteger a, BigInteger b, ReadOnlySpan<byte> sessionKey)
    {
        var hn = Hash(ToBytes(N));
        var hg = Hash(ToBytes(G));
        var xor = new byte[HashSize];
        for (var i = 0; i < HashSize; i++)
        {
            xor[i] = (byte)(hn[i] ^ hg[i]);
        }

        return Hash(xor, Hash(Encoding.UTF8.GetBytes(username)), salt.ToArray(), ToBytes(a), ToBytes(b), sessionKey.ToArray());
    }

    /// <summary><c>M2 = H(A || M1 || K)</c>.</summary>
    public static byte[] ComputeServerProof(BigInteger a, ReadOnlySpan<byte> clientProof, ReadOnlySpan<byte> sessionKey) =>
        Hash(ToBytes(a), clientProof.ToArray(), sessionKey.ToArray());
}

/// <summary>The outcome of a successful SRP exchange on the client side.</summary>
/// <param name="SessionKey">K = H(S), 64 bytes. For transient pairing this is the channel shared secret.</param>
/// <param name="ClientProof">M1 to send in M3.</param>
/// <param name="ExpectedServerProof">M2 the receiver must return in M4.</param>
public sealed record SrpClientSession(byte[] SessionKey, byte[] ClientProof, byte[] ExpectedServerProof)
{
    /// <summary>Constant-time comparison of the receiver's M2 with the expected value.</summary>
    public bool VerifyServerProof(ReadOnlySpan<byte> serverProof) =>
        serverProof.Length == ExpectedServerProof.Length && CryptographicOperations.FixedTimeEquals(serverProof, ExpectedServerProof);
}

/// <summary>SRP-6a client (the sender): generates A, then derives K and the proofs from the receiver's salt and B.</summary>
public sealed class SrpClient
{
    private readonly string _username;
    private readonly string _password;
    private readonly BigInteger _a;

    /// <summary>Creates a client for the HAP username and the PIN/password; <paramref name="privateKey"/> is 32 random bytes when null.</summary>
    public SrpClient(string username, string password, ReadOnlySpan<byte> privateKey = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        _username = username;
        _password = password;
        if (privateKey.IsEmpty)
        {
            var random = new byte[32];
            RandomNumberGenerator.Fill(random);
            _a = Srp6a.ToInt(random);
        }
        else
        {
            _a = Srp6a.ToInt(privateKey);
        }

        PublicKey = Srp6a.ToBytes(BigInteger.ModPow(Srp6a.G, _a, Srp6a.N));
    }

    /// <summary><c>A = g^a mod N</c>, big-endian without padding (goes into TLV PublicKey of M3).</summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// Processes the receiver's M2 (salt and B): computes u, S, K, M1 and the expected M2.
    /// Throws <see cref="CryptographicException"/> when <c>B mod N == 0</c> or <c>u == 0</c> (RFC 5054 2.5.4).
    /// </summary>
    public SrpClientSession ProcessChallenge(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> serverPublicKey)
    {
        var b = Srp6a.ToInt(serverPublicKey);
        if (b.IsZero || (b % Srp6a.N).IsZero)
        {
            throw new CryptographicException("SRP: invalid server public key (B mod N == 0).");
        }

        var a = Srp6a.ToInt(PublicKey);
        var u = Srp6a.ComputeU(a, b);
        if (u.IsZero)
        {
            throw new CryptographicException("SRP: u == 0.");
        }

        var x = Srp6a.ComputeX(salt, _username, _password);
        // S = (B - k * g^x) ^ (a + u * x) mod N
        var gx = BigInteger.ModPow(Srp6a.G, x, Srp6a.N);
        var baseValue = ((b - Srp6a.K * gx) % Srp6a.N + Srp6a.N) % Srp6a.N;
        var exponent = _a + u * x;
        var s = BigInteger.ModPow(baseValue, exponent, Srp6a.N);
        var sessionKey = Srp6a.Hash(Srp6a.ToBytes(s));
        var m1 = Srp6a.ComputeClientProof(_username, salt, a, b, sessionKey);
        var m2 = Srp6a.ComputeServerProof(a, m1, sessionKey);
        return new SrpClientSession(sessionKey, m1, m2);
    }
}

/// <summary>SRP-6a server (the receiver side, for the in-process fake receiver and tests).</summary>
public sealed class SrpServer
{
    private readonly string _username;
    private readonly byte[] _salt;
    private readonly BigInteger _verifier;
    private readonly BigInteger _b;

    /// <summary>Creates a server for a username/password; <paramref name="privateKey"/> is 32 random bytes when null.</summary>
    public SrpServer(string username, string password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> privateKey = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        _username = username;
        _salt = salt.ToArray();
        _verifier = Srp6a.ComputeVerifier(salt, username, password);
        if (privateKey.IsEmpty)
        {
            var random = new byte[32];
            RandomNumberGenerator.Fill(random);
            _b = Srp6a.ToInt(random);
        }
        else
        {
            _b = Srp6a.ToInt(privateKey);
        }

        // B = (k * v + g^b) mod N
        PublicKey = Srp6a.ToBytes((Srp6a.K * _verifier + BigInteger.ModPow(Srp6a.G, _b, Srp6a.N)) % Srp6a.N);
    }

    /// <summary>The salt sent in M2.</summary>
    public ReadOnlySpan<byte> Salt => _salt;

    /// <summary><c>B</c> sent in M2.</summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// Processes the client's A and M1; returns K and M2 on success, null when the proof does not match
    /// (wrong PIN) or A is invalid.
    /// </summary>
    public (byte[] SessionKey, byte[] ServerProof)? ProcessClientProof(ReadOnlySpan<byte> clientPublicKey, ReadOnlySpan<byte> clientProof)
    {
        var a = Srp6a.ToInt(clientPublicKey);
        if (a.IsZero || (a % Srp6a.N).IsZero)
        {
            return null;
        }

        var b = Srp6a.ToInt(PublicKey);
        var u = Srp6a.ComputeU(a, b);
        // S = (A * v^u) ^ b mod N
        var s = BigInteger.ModPow(a * BigInteger.ModPow(_verifier, u, Srp6a.N) % Srp6a.N, _b, Srp6a.N);
        var sessionKey = Srp6a.Hash(Srp6a.ToBytes(s));
        var expectedM1 = Srp6a.ComputeClientProof(_username, _salt, a, b, sessionKey);
        if (clientProof.Length != expectedM1.Length || !CryptographicOperations.FixedTimeEquals(clientProof, expectedM1))
        {
            return null;
        }

        return (sessionKey, Srp6a.ComputeServerProof(a, expectedM1, sessionKey));
    }
}
