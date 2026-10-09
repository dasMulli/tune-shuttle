using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;

namespace Airplay2.Sender.Crypto;

/// <summary>
/// ChaCha20-Poly1305 AEAD (RFC 8439, 96-bit nonce, 128-bit tag) as used by the HAP pairing messages, the encrypted
/// control/event channels (SPEC-control.md 2.5) and the RTP payloads (SPEC-audio.md 1.3). Uses the BCL implementation
/// when the platform supports it and falls back to BouncyCastle otherwise, so Linux containers without a recent
/// OpenSSL and older Windows builds behave the same.
/// </summary>
public static class ChaCha20Poly1305Aead
{
    /// <summary>Key length in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>Nonce length in bytes.</summary>
    public const int NonceSize = 12;

    /// <summary>Tag length in bytes.</summary>
    public const int TagSize = 16;

    private static readonly bool UseBcl = ChaCha20Poly1305.IsSupported;

    /// <summary>Encrypts and returns <c>ciphertext || tag</c> (the layout every HAP message uses).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        var output = new byte[plaintext.Length + TagSize];
        Encrypt(key, nonce, plaintext, aad, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, TagSize));
        return output;
    }

    /// <summary>Encrypts into separate ciphertext and tag spans.</summary>
    public static void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad, Span<byte> ciphertext, Span<byte> tag)
    {
        CheckSizes(key, nonce);
        if (UseBcl)
        {
            using var aead = new ChaCha20Poly1305(key);
            aead.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return;
        }

        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key.ToArray()), TagSize * 8, nonce.ToArray(), aad.ToArray()));
        var buffer = new byte[cipher.GetOutputSize(plaintext.Length)];
        var n = cipher.ProcessBytes(plaintext.ToArray(), 0, plaintext.Length, buffer, 0);
        cipher.DoFinal(buffer, n);
        buffer.AsSpan(0, plaintext.Length).CopyTo(ciphertext);
        buffer.AsSpan(plaintext.Length, TagSize).CopyTo(tag);
    }

    /// <summary>Decrypts <c>ciphertext || tag</c>; returns false on an authentication failure.</summary>
    public static bool TryDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad, Span<byte> plaintext)
    {
        if (ciphertextAndTag.Length < TagSize || plaintext.Length < ciphertextAndTag.Length - TagSize)
        {
            return false;
        }

        var ciphertext = ciphertextAndTag[..^TagSize];
        var tag = ciphertextAndTag[^TagSize..];
        return TryDecrypt(key, nonce, ciphertext, tag, aad, plaintext[..ciphertext.Length]);
    }

    /// <summary>Decrypts with separate ciphertext and tag; returns false on an authentication failure.</summary>
    public static bool TryDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> aad, Span<byte> plaintext)
    {
        CheckSizes(key, nonce);
        if (UseBcl)
        {
            try
            {
                using var aead = new ChaCha20Poly1305(key);
                aead.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                return true;
            }
            catch (AuthenticationTagMismatchException)
            {
                return false;
            }
        }

        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(false, new AeadParameters(new KeyParameter(key.ToArray()), TagSize * 8, nonce.ToArray(), aad.ToArray()));
        var input = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(input);
        tag.CopyTo(input.AsSpan(ciphertext.Length));
        var buffer = new byte[cipher.GetOutputSize(input.Length)];
        try
        {
            var n = cipher.ProcessBytes(input, 0, input.Length, buffer, 0);
            cipher.DoFinal(buffer, n);
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return false;
        }

        buffer.AsSpan(0, ciphertext.Length).CopyTo(plaintext);
        return true;
    }

    /// <summary>Decrypts <c>ciphertext || tag</c> and returns the plaintext, or null on an authentication failure.</summary>
    public static byte[]? Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextAndTag, ReadOnlySpan<byte> aad)
    {
        if (ciphertextAndTag.Length < TagSize)
        {
            return null;
        }

        var plaintext = new byte[ciphertextAndTag.Length - TagSize];
        return TryDecrypt(key, nonce, ciphertextAndTag, aad, plaintext) ? plaintext : null;
    }

    /// <summary>
    /// The 12-byte nonce used for the pairing messages: four zero bytes followed by an 8-character ASCII label such as
    /// <c>PS-Msg05</c> (SPEC-control.md 3.4).
    /// </summary>
    public static byte[] NonceFromLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.Length != 8)
        {
            throw new ArgumentException("HAP nonce labels are exactly 8 ASCII characters.", nameof(label));
        }

        var nonce = new byte[NonceSize];
        Encoding.ASCII.GetBytes(label, nonce.AsSpan(4));
        return nonce;
    }

    /// <summary>
    /// The 12-byte nonce used for channel frames and RTP payloads: four zero bytes followed by a 64-bit little-endian
    /// counter (SPEC-control.md 2.5, SPEC-audio.md 1.3).
    /// </summary>
    public static void NonceFromCounter(ulong counter, Span<byte> nonce)
    {
        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException("Nonce must be 12 bytes.", nameof(nonce));
        }

        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    /// <summary>Allocating variant of <see cref="NonceFromCounter(ulong, Span{byte})"/>.</summary>
    public static byte[] NonceFromCounter(ulong counter)
    {
        var nonce = new byte[NonceSize];
        NonceFromCounter(counter, nonce);
        return nonce;
    }

    private static void CheckSizes(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("ChaCha20-Poly1305 keys are 32 bytes.", nameof(key));
        }

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException("ChaCha20-Poly1305 (IETF) nonces are 12 bytes.", nameof(nonce));
        }
    }
}
