using Librespot.Core.Connection;
using Xunit;

namespace Librespot.Tests.Core;

/// <summary>
/// Test vectors generated with the Rust `shannon` 0.2.0 crate (the crate librespot uses), via a throw-away
/// cargo project: Shannon::new(key); nonce_u32(n); encrypt(pt); finish(mac).
/// </summary>
public class ShannonTests
{
    public static IEnumerable<object[]> Vectors =>
    [
        // key, nonce, plaintext, ciphertext, mac
        ["000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", 0u, "48656c6c6f2c205368616e6e6f6e2121", "821885475785ea2a23135df4b76708c3", "41a41064"],
        ["000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", 1u, "48656c6c6f2c205368616e6e6f6e2121", "f3f24ac33812327aca9e91f7d8e0c624", "fd006bcc"],
        ["74657374206b65792031323862697473", 0u, "", "", "264cfec1"],
        ["74657374206b65792031323862697473", 7u, "616263", "0b8a1e", "aa22dd28"],
        ["000102030405060708090a0b0c0d0e0f10111213", 3u, "04000400000000", "e4dcb4839c3a01", "57222313"],
        ["aaaaaaaaaaaaaa", 0xdeadbeefu, "6f6464206b6579206c656e67746820616e64203133206279", "f68a0e9b52f8de731aeda3505d9876933dd5e26cf88427a7", "8e8c4cd9"],
        [
            "000102030405060708090a0b0c0d0e0f10111213", 0u,
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebfc0c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7e8e9eaebecedeeeff0f1f2f3f4f5f6f7f8f9fafbfcfdfeff",
            "73d26e7946a33e75418ae2ccf23932c06f37fa32db313abbf98a7e07861c830dc4c1e05557d3bfb4395a44a222b2aca53d6cfa2e3f51e15275ea00923a6e6347fb17c9a1c84de28fa84e41880b95694bcb09bc6f2dcfad0f43647393d8c14c5fab4aed8e2a3f8f974fa44578d11ddc8b630e60a1298d5acf75840334f9476ccdf4faf192fb11ad560aaebd749de79aef09b2fc19a736b802ccf9759856f25c9f41eb3974957b1537140726266155bb70975b6bfb8b96e30122b2f99e43ec354f5f1e58c891f533bc12f1b981391b6aa9d9e9aff1cf6400161a223055bc687e7c676a618ae6c3209e294a7b4307028b612473332e474866850b4eb5ecad7de225",
            "958b540e",
        ],
    ];

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Encrypt_matches_rust_crate(string keyHex, uint nonce, string ptHex, string ctHex, string macHex)
    {
        var key = Convert.FromHexString(keyHex);
        var buf = Convert.FromHexString(ptHex);

        var c = new Shannon(key);
        c.NonceU32(nonce);
        c.Encrypt(buf);
        var mac = new byte[4];
        c.Finish(mac);

        Assert.Equal(ctHex, Convert.ToHexStringLower(buf));
        Assert.Equal(macHex, Convert.ToHexStringLower(mac));
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Decrypt_roundtrips_and_checks_mac(string keyHex, uint nonce, string ptHex, string ctHex, string macHex)
    {
        var key = Convert.FromHexString(keyHex);
        var buf = Convert.FromHexString(ctHex);

        var d = new Shannon(key);
        d.NonceU32(nonce);
        d.Decrypt(buf);
        d.CheckMac(Convert.FromHexString(macHex));

        Assert.Equal(ptHex, Convert.ToHexStringLower(buf));
    }

    [Fact]
    public void Chunked_encrypt_with_nonce_reuse_matches_rust_crate()
    {
        // Same cipher instance, nonce 0 then 1, each message encrypted in two calls ("first-part-" + "second").
        var key = Convert.FromHexString("6465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f80818283");
        var c = new Shannon(key);

        string[] expectedCt1 = ["0ee93d3f6afa7672737a56", "60b0d4981130cb999445f2"];
        string[] expectedCt2 = ["e8452a34e938", "014ecebe752b"];
        string[] expectedMac = ["05de5618", "1b7455fd"];

        for (var n = 0u; n < 2; n++)
        {
            c.NonceU32(n);
            var a = "first-part-"u8.ToArray();
            var b = "second"u8.ToArray();
            c.Encrypt(a);
            c.Encrypt(b);
            var mac = new byte[4];
            c.Finish(mac);

            Assert.Equal(expectedCt1[n], Convert.ToHexStringLower(a));
            Assert.Equal(expectedCt2[n], Convert.ToHexStringLower(b));
            Assert.Equal(expectedMac[n], Convert.ToHexStringLower(mac));
        }
    }

    [Fact]
    public void Eight_byte_mac_matches_rust_crate()
    {
        var key = Convert.FromHexString("6465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f80818283");
        var c = new Shannon(key);
        c.NonceU32(5);
        var buf = "mac8"u8.ToArray();
        c.Encrypt(buf);
        var mac = new byte[8];
        c.Finish(mac);

        Assert.Equal("557ab004", Convert.ToHexStringLower(buf));
        Assert.Equal("4c8dd0facaf78eab", Convert.ToHexStringLower(mac));
    }

    [Fact]
    public void Wrong_mac_is_rejected()
    {
        var key = Convert.FromHexString("74657374206b65792031323862697473");
        var d = new Shannon(key);
        d.NonceU32(7);
        var buf = Convert.FromHexString("0b8a1e");
        d.Decrypt(buf);
        Assert.Throws<InvalidDataException>(() => d.CheckMac(Convert.FromHexString("aa22dd29")));
    }
}
