using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Airplay2.Sender.Crypto;

/// <summary>
/// The two 32-byte keys of an encrypted HAP channel, from the sender's point of view: <see cref="WriteKey"/> encrypts
/// what we send, <see cref="ReadKey"/> decrypts what we receive (SPEC-control.md 2.5, 4.3).
/// </summary>
public sealed record HapChannelKeys(byte[] WriteKey, byte[] ReadKey)
{
    /// <summary>Control-channel keys: write = <c>Control-Write-Encryption-Key</c>, read = <c>Control-Read-Encryption-Key</c>.</summary>
    public static HapChannelKeys ForControl(ReadOnlySpan<byte> sharedSecret) => new(
        HapKdf.Derive(sharedSecret, HapKeyInfo.ControlSalt, HapKeyInfo.ControlWriteKeyInfo),
        HapKdf.Derive(sharedSecret, HapKeyInfo.ControlSalt, HapKeyInfo.ControlReadKeyInfo));

    /// <summary>
    /// Event-channel keys. The channel logically belongs to the receiver, so the roles are swapped: the sender encrypts
    /// with <c>Events-Read-Encryption-Key</c> and decrypts with <c>Events-Write-Encryption-Key</c> (SPEC-control.md 4.3).
    /// </summary>
    public static HapChannelKeys ForEvents(ReadOnlySpan<byte> sharedSecret) => new(
        HapKdf.Derive(sharedSecret, HapKeyInfo.EventsSalt, HapKeyInfo.EventsReadKeyInfo),
        HapKdf.Derive(sharedSecret, HapKeyInfo.EventsSalt, HapKeyInfo.EventsWriteKeyInfo));

    /// <summary>The same keys seen from the other end (used by the fake receiver).</summary>
    public HapChannelKeys Swapped() => new(ReadKey, WriteKey);
}

/// <summary>
/// The ChaCha20-Poly1305 framing of an encrypted HAP channel (SPEC-control.md 2.5): each frame is
/// <c>len (2 bytes LE) || ciphertext (len bytes) || tag (16)</c>, nonce = 4 zero bytes + 64-bit LE frame counter per
/// direction, AAD = the 2-byte length. Sending splits at <see cref="MaxSendFramePlaintext"/> (1024, the HAP cap, not
/// configurable); receiving accepts any frame whose length fits the 16-bit field (<see cref="MaxReceiveFramePlaintext"/>),
/// because Apple-SDK receivers may send larger frames (pyatv protocols.md:1555-1556). One instance per connection; not thread-safe.
/// </summary>
public sealed class HapFrameCipher
{
    /// <summary>Largest plaintext chunk per frame we send (HAP 5.2.2; shairport-sync and pyatv split at the same size).</summary>
    public const int MaxSendFramePlaintext = 1024;

    /// <summary>Largest plaintext a received frame may carry: whatever the 2-byte length field can express.</summary>
    public const int MaxReceiveFramePlaintext = ushort.MaxValue;

    private const int LengthSize = 2;
    private readonly HapChannelKeys _keys;
    private readonly byte[] _nonce = new byte[ChaCha20Poly1305Aead.NonceSize];
    private ulong _writeCounter;
    private ulong _readCounter;

    /// <summary>Creates a cipher for the given keys with both counters at zero.</summary>
    public HapFrameCipher(HapChannelKeys keys)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    /// <summary>Number of frames encrypted so far (the next write nonce).</summary>
    public ulong WriteCounter => _writeCounter;

    /// <summary>Number of frames decrypted so far (the next read nonce).</summary>
    public ulong ReadCounter => _readCounter;

    /// <summary>Encrypts a plaintext message into one or more consecutive frames.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var frames = (plaintext.Length + MaxSendFramePlaintext - 1) / MaxSendFramePlaintext;
        if (frames == 0)
        {
            frames = 1;
        }

        var output = new byte[plaintext.Length + frames * (LengthSize + ChaCha20Poly1305Aead.TagSize)];
        var written = 0;
        var offset = 0;
        do
        {
            var chunk = Math.Min(MaxSendFramePlaintext, plaintext.Length - offset);
            var header = output.AsSpan(written, LengthSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)chunk);
            ChaCha20Poly1305Aead.NonceFromCounter(_writeCounter++, _nonce);
            ChaCha20Poly1305Aead.Encrypt(
                _keys.WriteKey,
                _nonce,
                plaintext.Slice(offset, chunk),
                header,
                output.AsSpan(written + LengthSize, chunk),
                output.AsSpan(written + LengthSize + chunk, ChaCha20Poly1305Aead.TagSize));
            written += LengthSize + chunk + ChaCha20Poly1305Aead.TagSize;
            offset += chunk;
        }
        while (offset < plaintext.Length);

        return output;
    }

    /// <summary>
    /// Decrypts as many complete frames as <paramref name="input"/> holds (each up to <see cref="MaxReceiveFramePlaintext"/>
    /// bytes of plaintext), appending the plaintext to <paramref name="plaintext"/>; returns the number of input bytes
    /// consumed. Throws <see cref="CryptographicException"/> on an authentication failure, after which the connection must be closed.
    /// </summary>
    public int DecryptFrames(ReadOnlySpan<byte> input, Stream plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var consumed = 0;
        var rented = ArrayPool<byte>.Shared.Rent(MaxReceiveFramePlaintext);
        try
        {
            var buffer = rented.AsSpan(0, MaxReceiveFramePlaintext);
            while (TryDecryptFrame(input[consumed..], buffer, out var frameLength, out var plaintextLength))
            {
                plaintext.Write(buffer[..plaintextLength]);
                consumed += frameLength;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return consumed;
    }

    /// <summary>
    /// Decrypts one frame if <paramref name="input"/> starts with a complete one. Returns false when more bytes are
    /// needed; throws <see cref="CryptographicException"/> when the tag does not verify or the frame's plaintext exceeds
    /// <paramref name="plaintext"/> (size it with <see cref="MaxReceiveFramePlaintext"/>; never assume 1024 on the receive side).
    /// </summary>
    public bool TryDecryptFrame(ReadOnlySpan<byte> input, Span<byte> plaintext, out int frameLength, out int plaintextLength)
    {
        frameLength = 0;
        plaintextLength = 0;
        if (input.Length < LengthSize)
        {
            return false;
        }

        var len = BinaryPrimitives.ReadUInt16LittleEndian(input);
        var total = LengthSize + len + ChaCha20Poly1305Aead.TagSize;
        if (input.Length < total)
        {
            return false;
        }

        if (plaintext.Length < len)
        {
            throw new CryptographicException($"HAP frame of {len} bytes does not fit the {plaintext.Length}-byte buffer.");
        }

        ChaCha20Poly1305Aead.NonceFromCounter(_readCounter, _nonce);
        if (!ChaCha20Poly1305Aead.TryDecrypt(_keys.ReadKey, _nonce, input.Slice(LengthSize, len), input.Slice(LengthSize + len, ChaCha20Poly1305Aead.TagSize), input[..LengthSize], plaintext[..len]))
        {
            throw new CryptographicException($"HAP frame {_readCounter} failed authentication.");
        }

        _readCounter++;
        frameLength = total;
        plaintextLength = len;
        return true;
    }
}
