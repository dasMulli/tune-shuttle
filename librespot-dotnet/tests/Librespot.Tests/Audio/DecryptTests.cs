// Tests for audio/src/decrypt.rs (upstream has no #[cfg(test)] module).
using System.Buffers.Binary;
using System.Security.Cryptography;
using Librespot.Audio;
using Librespot.Core;
using Librespot.Core.Crypto;
using Xunit;

namespace Librespot.Tests.Audio;

public class DecryptTests
{
    private static readonly byte[] Key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");

    private static readonly byte[] AudioAesIv = Convert.FromHexString("72e067fbddcbcf77ebe8bc643f630d93");

    private static byte[] Plaintext(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7 + 3);
        }

        return data;
    }

    private static byte[] Encrypt(byte[] plaintext)
    {
        var data = (byte[])plaintext.Clone();
        AesCtr.Transform(Key, AudioAesIv, 0, data);
        return data;
    }

    [Fact]
    public void IvConstantMatchesUpstream() => Assert.Equal(AudioAesIv, AudioDecrypt.AudioAesIv.ToArray());

    [Fact]
    public void KeystreamIsAesOfIvCounterBlocks()
    {
        // Independent of AesCtr: keystream block n = AES-128-ECB_k(AUDIO_AESIV + n) with a big-endian counter.
        using var aes = Aes.Create();
        aes.Key = Key;
        var counters = new byte[32];
        var iv = BinaryPrimitives.ReadUInt128BigEndian(AudioAesIv);
        BinaryPrimitives.WriteUInt128BigEndian(counters.AsSpan(0, 16), iv);
        BinaryPrimitives.WriteUInt128BigEndian(counters.AsSpan(16, 16), iv + 1);
        var keystream = aes.EncryptEcb(counters, PaddingMode.None);

        using var decrypt = new AudioDecrypt(new AudioKey(Key), new MemoryStream(new byte[32]));
        var output = new byte[32];
        Assert.Equal(32, decrypt.Read(output));
        Assert.Equal(keystream, output);
    }

    [Fact]
    public void ReadDecryptsWholeFile()
    {
        var plaintext = Plaintext(5000);
        using var decrypt = new AudioDecrypt(new AudioKey(Key), new MemoryStream(Encrypt(plaintext)));

        var output = new byte[plaintext.Length];
        var total = 0;
        while (total < output.Length)
        {
            // odd chunk sizes so reads straddle block boundaries
            var n = decrypt.Read(output, total, Math.Min(37, output.Length - total));
            Assert.True(n > 0);
            total += n;
        }

        Assert.Equal(plaintext, output);
    }

    [Fact]
    public void SeekIntoMiddleOfBlockThenRead()
    {
        var plaintext = Plaintext(4096);
        using var decrypt = new AudioDecrypt(new AudioKey(Key), new MemoryStream(Encrypt(plaintext)));

        Assert.Equal(1000, decrypt.Seek(1000, SeekOrigin.Begin)); // 1000 = 62 * 16 + 8
        var output = new byte[100];
        Assert.Equal(100, decrypt.Read(output));
        Assert.Equal(plaintext.AsSpan(1000, 100).ToArray(), output);

        // seek backwards relative to the current position, and via Position
        decrypt.Seek(-1095, SeekOrigin.Current);
        Assert.Equal(5, decrypt.Position);
        Assert.Equal(10, decrypt.Read(output, 0, 10));
        Assert.Equal(plaintext.AsSpan(5, 10).ToArray(), output.AsSpan(0, 10).ToArray());

        decrypt.Position = 4090;
        Assert.Equal(6, decrypt.Read(output));
        Assert.Equal(plaintext.AsSpan(4090, 6).ToArray(), output.AsSpan(0, 6).ToArray());
    }

    [Fact]
    public void NullKeyPassesDataThrough()
    {
        var data = Plaintext(100);
        using var decrypt = new AudioDecrypt(null, new MemoryStream(data));
        decrypt.Seek(10, SeekOrigin.Begin);
        var output = new byte[90];
        Assert.Equal(90, decrypt.Read(output));
        Assert.Equal(data.AsSpan(10).ToArray(), output);
    }
}
