// Not a port of a single upstream file: upstream uses the `aes` + `ctr` crates (`Ctr128BE<Aes128>`) in
// audio/src/decrypt.rs (AudioDecrypt) and discovery/src/server.rs (addUser blob). .NET has no CTR mode, so this
// helper builds AES-128-CTR (big-endian 128-bit counter, NIST SP 800-38A) on top of AES-ECB keystream blocks.
// Owned by package `foundation` (done); audio-fetch and discovery only consume it.
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Librespot.Core.Crypto;

/// <summary>
/// AES-128 in CTR mode with a big-endian 128-bit counter (<c>ctr::Ctr128BE&lt;Aes128&gt;</c>): the keystream block
/// for byte offset <c>p</c> is <c>AES_k(IV + p / 16)</c>, and encryption equals decryption.
/// </summary>
public sealed class AesCtr : IDisposable
{
    /// <summary>AES block size in bytes.</summary>
    public const int BlockSize = 16;

    private const int KeystreamBlocks = 64; // 1 KiB of keystream per ECB call

    private readonly Aes _aes;
    private readonly UInt128 _iv;
    private readonly byte[] _counters = new byte[KeystreamBlocks * BlockSize];
    private readonly byte[] _keystream = new byte[KeystreamBlocks * BlockSize];
    private long _position;

    /// <param name="key">16-byte AES-128 key.</param>
    /// <param name="iv">16-byte initial counter block (big-endian).</param>
    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != BlockSize)
        {
            throw new ArgumentException("AES-128 key must be 16 bytes", nameof(key));
        }

        if (iv.Length != BlockSize)
        {
            throw new ArgumentException("IV must be 16 bytes", nameof(iv));
        }

        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _iv = BinaryPrimitives.ReadUInt128BigEndian(iv);
    }

    /// <summary>
    /// The byte offset in the stream the next <see cref="Apply"/> starts at. Setting it recomputes the counter
    /// (<c>IV + position / 16</c>) and the intra-block offset (upstream <c>cipher.seek(position)</c>).
    /// </summary>
    public long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>XORs <paramref name="data"/> in place with the keystream starting at <see cref="Position"/> and advances it.</summary>
    public void Apply(Span<byte> data)
    {
        while (!data.IsEmpty)
        {
            var blockIndex = (ulong)(_position / BlockSize);
            var offsetInBlock = (int)(_position % BlockSize);

            // Fill the keystream buffer with consecutive counter blocks starting at blockIndex.
            var blocksNeeded = Math.Min(KeystreamBlocks, (int)((offsetInBlock + data.Length + BlockSize - 1) / BlockSize));
            for (var i = 0; i < blocksNeeded; i++)
            {
                BinaryPrimitives.WriteUInt128BigEndian(_counters.AsSpan(i * BlockSize, BlockSize), unchecked(_iv + blockIndex + (ulong)i));
            }

            var produced = _aes.EncryptEcb(_counters.AsSpan(0, blocksNeeded * BlockSize), _keystream, PaddingMode.None);
            var usable = Math.Min(data.Length, produced - offsetInBlock);

            var ks = _keystream.AsSpan(offsetInBlock, usable);
            for (var i = 0; i < usable; i++)
            {
                data[i] ^= ks[i];
            }

            data = data[usable..];
            _position += usable;
        }
    }

    /// <summary>One-shot helper: transforms <paramref name="data"/> as if it sat at <paramref name="byteOffset"/> of the stream.</summary>
    public static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, long byteOffset, Span<byte> data)
    {
        using var ctr = new AesCtr(key, iv) { Position = byteOffset };
        ctr.Apply(data);
    }

    public void Dispose() => _aes.Dispose();
}
