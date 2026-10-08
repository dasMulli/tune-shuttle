using Librespot.Core.Crypto;
using Xunit;

namespace Librespot.Tests.Core;

public class AesCtrTests
{
    // NIST SP 800-38A, F.5.1 CTR-AES128.Encrypt
    private static readonly byte[] Key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
    private static readonly byte[] Iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");

    private static readonly byte[] Plaintext = Convert.FromHexString(
        "6bc1bee22e409f96e93d7e117393172a" +
        "ae2d8a571e03ac9c9eb76fac45af8e51" +
        "30c81c46a35ce411e5fbc1191a0a52ef" +
        "f69f2445df4f9b17ad2b417be66c3710");

    private static readonly byte[] Ciphertext = Convert.FromHexString(
        "874d6191b620e3261bef6864990db6ce" +
        "9806f66b7970fdff8617187bb9fffdff" +
        "5ae4df3edbd5d35e5b4f09020db03eab" +
        "1e031dda2fbe03d1792170a0f3009cee");

    [Fact]
    public void Nist_sp800_38a_f51_vector()
    {
        var data = Plaintext.ToArray();
        AesCtr.Transform(Key, Iv, 0, data);
        Assert.Equal(Ciphertext, data);
    }

    [Fact]
    public void Decrypt_is_encrypt()
    {
        var data = Ciphertext.ToArray();
        AesCtr.Transform(Key, Iv, 0, data);
        Assert.Equal(Plaintext, data);
    }

    [Fact]
    public void Seeking_to_an_unaligned_offset_matches_the_full_stream()
    {
        foreach (var offset in new[] { 1, 5, 16, 17, 31, 47 })
        {
            var slice = Plaintext.AsSpan(offset).ToArray();
            AesCtr.Transform(Key, Iv, offset, slice);
            Assert.Equal(Ciphertext.AsSpan(offset).ToArray(), slice);
        }
    }

    [Fact]
    public void Apply_in_small_chunks_matches_one_shot()
    {
        using var ctr = new AesCtr(Key, Iv);
        var data = Plaintext.ToArray();
        var pos = 0;
        foreach (var len in new[] { 3, 13, 1, 20, 27 })
        {
            ctr.Apply(data.AsSpan(pos, len));
            pos += len;
            Assert.Equal(pos, ctr.Position);
        }

        Assert.Equal(Plaintext.Length, pos);
        Assert.Equal(Ciphertext, data);
    }

    [Fact]
    public void Counter_carries_across_the_low_64_bits()
    {
        // IV = 0x0000...FFFFFFFFFFFFFFFF: block 1 must use counter 0x0000...0001_0000000000000000, not wrap to 0.
        var iv = Convert.FromHexString("0000000000000000ffffffffffffffff");
        var zeros = new byte[32];
        AesCtr.Transform(Key, iv, 0, zeros);

        var block1 = new byte[16];
        AesCtr.Transform(Key, Convert.FromHexString("00000000000000010000000000000000"), 0, block1);
        Assert.Equal(block1, zeros.AsSpan(16).ToArray());

        var wrapped = new byte[16];
        AesCtr.Transform(Key, new byte[16], 0, wrapped);
        Assert.NotEqual(wrapped, zeros.AsSpan(16).ToArray());
    }

    [Fact]
    public void Large_buffer_spans_several_keystream_refills()
    {
        var data = new byte[5000];
        Random.Shared.NextBytes(data);
        var expected = data.ToArray();
        for (var i = 0; i < expected.Length; i += 16)
        {
            var len = Math.Min(16, expected.Length - i);
            AesCtr.Transform(Key, Iv, i, expected.AsSpan(i, len));
        }

        AesCtr.Transform(Key, Iv, 0, data);
        Assert.Equal(expected, data);
    }
}
