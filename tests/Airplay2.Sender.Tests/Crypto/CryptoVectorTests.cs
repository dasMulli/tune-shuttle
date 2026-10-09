using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Airplay2.Sender.Crypto;
using Xunit;

namespace Airplay2.Sender.Tests.Crypto;

/// <summary>RFC test vectors for the primitives the pairing and channels use.</summary>
public class CryptoVectorTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", string.Empty, StringComparison.Ordinal));

    [Fact]
    public void X25519_Rfc7748_section_5_2_scalar_mult()
    {
        var scalar = Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
        var u = Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
        var expected = Hex("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552");
        Assert.Equal(expected, X25519KeyPair.ScalarMult(scalar, u));
    }

    [Fact]
    public void X25519_Rfc7748_section_6_1_key_agreement()
    {
        var alice = X25519KeyPair.FromPrivateKey(Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"));
        var bob = X25519KeyPair.FromPrivateKey(Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"));
        Assert.Equal(Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"), alice.PublicKey);
        Assert.Equal(Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"), bob.PublicKey);
        var shared = Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
        Assert.Equal(shared, alice.Agree(bob.PublicKey));
        Assert.Equal(shared, bob.Agree(alice.PublicKey));
    }

    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "", "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72", "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82", "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    public void Ed25519_Rfc8032_section_7_1(string seed, string publicKey, string message, string signature)
    {
        var kp = Ed25519KeyPair.FromSeed(Hex(seed));
        Assert.Equal(Hex(publicKey), kp.PublicKey);
        var sig = kp.Sign(Hex(message));
        Assert.Equal(Hex(signature), sig);
        Assert.True(Ed25519KeyPair.Verify(kp.PublicKey, Hex(message), sig));
        sig[0] ^= 1;
        Assert.False(Ed25519KeyPair.Verify(kp.PublicKey, Hex(message), sig));
    }

    [Fact]
    public void ChaCha20Poly1305_Rfc8439_section_2_8_2()
    {
        var key = Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        var nonce = Hex("070000004041424344454647");
        var aad = Hex("50515253c0c1c2c3c4c5c6c7");
        var plaintext = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var expectedCiphertext = Hex("d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc3ff4def08e4b7a9de576d26586cec64b6116");
        var expectedTag = Hex("1ae10b594f09e26a7e902ecbd0600691");

        var output = ChaCha20Poly1305Aead.Encrypt(key, nonce, plaintext, aad);
        Assert.Equal(expectedCiphertext, output[..plaintext.Length]);
        Assert.Equal(expectedTag, output[plaintext.Length..]);

        var decrypted = ChaCha20Poly1305Aead.Decrypt(key, nonce, output, aad);
        Assert.Equal(plaintext, decrypted);

        output[3] ^= 0x80;
        Assert.Null(ChaCha20Poly1305Aead.Decrypt(key, nonce, output, aad));
    }

    [Fact]
    public void ChaCha20Poly1305_nonces()
    {
        Assert.Equal(Hex("0000000050532d4d73673035"), ChaCha20Poly1305Aead.NonceFromLabel("PS-Msg05"));
        Assert.Equal(Hex("000000000700000000000000"), ChaCha20Poly1305Aead.NonceFromCounter(7));
        Assert.Equal(Hex("00000000ffffffffffffffff"), ChaCha20Poly1305Aead.NonceFromCounter(ulong.MaxValue));
    }

    [Fact]
    public void Hkdf_Rfc5869_test_case_1_sha256()
    {
        var ikm = Hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var salt = Hex("000102030405060708090a0b0c");
        var info = Hex("f0f1f2f3f4f5f6f7f8f9");
        var okm = HapKdf.Derive(HashAlgorithmName.SHA256, ikm, salt, info, 42);
        Assert.Equal(Hex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"), okm);
    }

    [Fact]
    public void Hkdf_sha512_matches_manual_extract_expand()
    {
        // Spells out SPEC-control.md 2.5: PRK = HMAC-SHA512(salt, SS), OKM = HMAC-SHA512(PRK, info || 0x01)[0:32]
        var ss = RandomNumberGenerator.GetBytes(64);
        var salt = Encoding.ASCII.GetBytes(HapKeyInfo.ControlSalt);
        var info = Encoding.ASCII.GetBytes(HapKeyInfo.ControlWriteKeyInfo);
        var prk = HMACSHA512.HashData(salt, ss);
        var okm = HMACSHA512.HashData(prk, (byte[])[.. info, 0x01])[..32];
        Assert.Equal(okm, HapKdf.Derive(ss, HapKeyInfo.ControlSalt, HapKeyInfo.ControlWriteKeyInfo));
    }

    /// <summary>
    /// Known answers for the HomeKit HKDF-SHA512 schedule, computed independently with Python's standard library
    /// (hmac + hashlib, RFC 5869 extract/expand; generator docs/airplay2/probes/gen_hkdf_vectors.py) from two fixed
    /// shared secrets: the 64-byte transient secret 10 11 .. 4F and the 32-byte verified secret 20 21 .. 3F.
    /// </summary>
    [Theory]
    [InlineData(64, "Control-Salt", "Control-Write-Encryption-Key", "615aa5558f8b65f79d3b166fd4dbcdcf0647ae9dada6735d44f33d20fac02dc9")]
    [InlineData(64, "Control-Salt", "Control-Read-Encryption-Key", "417aaaf6bd2fbe21b7ea21a912514bc0ea07175ab6354626fbddc3f29a83e7b7")]
    [InlineData(64, "Events-Salt", "Events-Write-Encryption-Key", "019fdc53e48a20fd8b7c925da8fc99b9d7b898fcc358652674bdb02e2159a126")]
    [InlineData(64, "Events-Salt", "Events-Read-Encryption-Key", "19a5e366324b18e21da931bfb077520a192819aaa53e66c3b0ec93a659b75be7")]
    [InlineData(64, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info", "fbddbab11b78253936ee58b7233d597fcdbb3b83288b6842168417850daddcef")]
    [InlineData(32, "Control-Salt", "Control-Write-Encryption-Key", "44418339bea423290bbd5f36bf5fc98bdad7cded8831904b0dfbfc61cc71cfb9")]
    [InlineData(32, "Control-Salt", "Control-Read-Encryption-Key", "ed0bcfb6c73ad06f66f797c729da88acb06166c76ff40f626afaf5dea08c44fc")]
    [InlineData(32, "Events-Salt", "Events-Write-Encryption-Key", "9e94a0b77461685dfb2e1766b8477ae5461347fef5ba562fc571bc50d2eace70")]
    [InlineData(32, "Events-Salt", "Events-Read-Encryption-Key", "4d5d962c34d1a60d96a9aaa1888163365689fcafbe28da35985461429a0b8f97")]
    [InlineData(32, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info", "b74869bc3328f6f648bd2d3cdf5308206ad1ce049f932103c73f782465390d99")]
    public void Hkdf_sha512_homekit_known_answers(int secretLength, string salt, string info, string expectedHex)
    {
        var start = secretLength == 64 ? 0x10 : 0x20;
        var ss = Enumerable.Range(start, secretLength).Select(i => (byte)i).ToArray();
        Assert.Equal(Hex(expectedHex), HapKdf.Derive(ss, salt, info));
    }

    [Fact]
    public void Channel_keys_from_known_answers()
    {
        var ss = Enumerable.Range(0x10, 64).Select(i => (byte)i).ToArray();
        var control = HapChannelKeys.ForControl(ss);
        Assert.Equal(Hex("615aa5558f8b65f79d3b166fd4dbcdcf0647ae9dada6735d44f33d20fac02dc9"), control.WriteKey);
        Assert.Equal(Hex("417aaaf6bd2fbe21b7ea21a912514bc0ea07175ab6354626fbddc3f29a83e7b7"), control.ReadKey);
        var events = HapChannelKeys.ForEvents(ss);
        Assert.Equal(Hex("19a5e366324b18e21da931bfb077520a192819aaa53e66c3b0ec93a659b75be7"), events.WriteKey); // Events-Read (swapped)
        Assert.Equal(Hex("019fdc53e48a20fd8b7c925da8fc99b9d7b898fcc358652674bdb02e2159a126"), events.ReadKey); // Events-Write (swapped)
    }

    [Fact]
    public void Receive_side_accepts_frames_larger_than_the_send_cap()
    {
        // SPEC-control.md 2.5: we send <= 1024 bytes per frame, but accept any length that fits the 16-bit field.
        var ss = RandomNumberGenerator.GetBytes(32);
        var keys = HapChannelKeys.ForControl(ss);
        var receiverSide = new HapFrameCipher(keys.Swapped()); // "receiver" encrypts with our read key
        var us = new HapFrameCipher(keys);

        // Build one 3000-byte frame by hand (the receiver's cipher would otherwise split it at 1024).
        var payload = RandomNumberGenerator.GetBytes(3000);
        var frame = new byte[2 + payload.Length + 16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);
        var nonce = ChaCha20Poly1305Aead.NonceFromCounter(0);
        ChaCha20Poly1305Aead.Encrypt(keys.ReadKey, nonce, payload, frame.AsSpan(0, 2), frame.AsSpan(2, payload.Length), frame.AsSpan(2 + payload.Length, 16));

        var plaintext = new MemoryStream();
        Assert.Equal(frame.Length, us.DecryptFrames(frame, plaintext));
        Assert.Equal(payload, plaintext.ToArray());
        Assert.Equal(1UL, us.ReadCounter);

        // the largest possible frame also fits
        var big = RandomNumberGenerator.GetBytes(HapFrameCipher.MaxReceiveFramePlaintext);
        var bigFrame = new byte[2 + big.Length + 16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bigFrame, (ushort)big.Length);
        ChaCha20Poly1305Aead.Encrypt(keys.ReadKey, ChaCha20Poly1305Aead.NonceFromCounter(1), big, bigFrame.AsSpan(0, 2), bigFrame.AsSpan(2, big.Length), bigFrame.AsSpan(2 + big.Length, 16));
        var plaintext2 = new MemoryStream();
        Assert.Equal(bigFrame.Length, us.DecryptFrames(bigFrame, plaintext2));
        Assert.Equal(big, plaintext2.ToArray());

        // and what we send is always split at 1024 (non-configurable)
        var wire = receiverSide.Encrypt(payload);
        Assert.Equal(3000 + 3 * 18, wire.Length);
        Assert.Equal(1024, HapFrameCipher.MaxSendFramePlaintext);
    }

    [Fact]
    public void Channel_keys_and_frames_round_trip_and_fragment()
    {
        var ss = RandomNumberGenerator.GetBytes(32);
        var sender = new HapFrameCipher(HapChannelKeys.ForControl(ss));
        var receiver = new HapFrameCipher(HapChannelKeys.ForControl(ss).Swapped());

        var message = RandomNumberGenerator.GetBytes(2500); // three frames: 1024 + 1024 + 452
        var wire = sender.Encrypt(message);
        Assert.Equal(2500 + 3 * 18, wire.Length);
        Assert.Equal(3UL, sender.WriteCounter);

        // feed in two arbitrary chunks: frames need not align with reads
        var plaintext = new MemoryStream();
        var consumed = receiver.DecryptFrames(wire.AsSpan(0, 1500), plaintext);
        Assert.Equal(1024 + 18, consumed);
        consumed += receiver.DecryptFrames(wire.AsSpan(consumed), plaintext);
        Assert.Equal(wire.Length, consumed);
        Assert.Equal(message, plaintext.ToArray());
        Assert.Equal(3UL, receiver.ReadCounter);

        // event keys are swapped relative to control keys
        var ev = HapChannelKeys.ForEvents(ss);
        Assert.Equal(HapKdf.Derive(ss, HapKeyInfo.EventsSalt, HapKeyInfo.EventsReadKeyInfo), ev.WriteKey);
        Assert.Equal(HapKdf.Derive(ss, HapKeyInfo.EventsSalt, HapKeyInfo.EventsWriteKeyInfo), ev.ReadKey);

        // tampering is fatal
        wire[5] ^= 1;
        var fresh = new HapFrameCipher(HapChannelKeys.ForControl(ss).Swapped());
        Assert.Throws<CryptographicException>(() => fresh.DecryptFrames(wire, new MemoryStream()));
    }

    [Fact]
    public void Srp_vectors_from_independent_implementation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "srp-3072-sha512.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            string S(string name) => v.GetProperty(name).GetString()!;
            var salt = Hex(S("s"));
            var client = new SrpClient(S("I"), S("P"), Hex(S("a")));
            Assert.Equal(S("A"), Convert.ToHexString(client.PublicKey));
            Assert.Equal(S("k"), Convert.ToHexString(Srp6a.ToBytes(Srp6a.K)));
            Assert.Equal(S("x"), Convert.ToHexString(Srp6a.ToBytes(Srp6a.ComputeX(salt, S("I"), S("P")))));
            Assert.Equal(S("v"), Convert.ToHexString(Srp6a.ToBytes(Srp6a.ComputeVerifier(salt, S("I"), S("P")))));

            var server = new SrpServer(S("I"), S("P"), salt, Hex(S("b")));
            Assert.Equal(S("B"), Convert.ToHexString(server.PublicKey));

            var session = client.ProcessChallenge(salt, server.PublicKey);
            Assert.Equal(S("K"), Convert.ToHexString(session.SessionKey));
            Assert.Equal(S("M1"), Convert.ToHexString(session.ClientProof));
            Assert.Equal(S("M2"), Convert.ToHexString(session.ExpectedServerProof));

            var serverResult = server.ProcessClientProof(client.PublicKey, session.ClientProof);
            Assert.NotNull(serverResult);
            Assert.Equal(session.SessionKey, serverResult.Value.SessionKey);
            Assert.True(session.VerifyServerProof(serverResult.Value.ServerProof));
        }
    }

    [Fact]
    public void Srp_wrong_pin_fails_and_random_keys_agree()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var server = new SrpServer(Srp6a.PairSetupUsername, Srp6a.TransientPin, salt);
        var good = new SrpClient(Srp6a.PairSetupUsername, Srp6a.TransientPin);
        var goodSession = good.ProcessChallenge(salt, server.PublicKey);
        var result = server.ProcessClientProof(good.PublicKey, goodSession.ClientProof);
        Assert.NotNull(result);
        Assert.True(goodSession.VerifyServerProof(result.Value.ServerProof));
        Assert.Equal(64, goodSession.SessionKey.Length);

        var bad = new SrpClient(Srp6a.PairSetupUsername, "1234");
        var badSession = bad.ProcessChallenge(salt, server.PublicKey);
        Assert.Null(server.ProcessClientProof(bad.PublicKey, badSession.ClientProof));
        Assert.Throws<CryptographicException>(() => good.ProcessChallenge(salt, new byte[384]));
    }
}
