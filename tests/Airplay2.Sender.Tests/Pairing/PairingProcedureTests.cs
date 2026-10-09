using System.Text;
using System.Text.Json;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Sender.Tests.Pairing;

/// <summary>A client connection wired to an accessory, plus what the accessory side observed.</summary>
internal interface IPairingLink : IAsyncDisposable
{
    /// <summary>The client end.</summary>
    IRtspConnection Connection { get; }

    /// <summary>Requests as the accessory received them.</summary>
    IReadOnlyList<RecordedRequest> Requests { get; }

    /// <summary>Raw response bodies the accessory sent.</summary>
    IReadOnlyList<byte[]> ResponseBodies { get; }

    /// <summary>The session the accessory reported when a procedure completed.</summary>
    PairingSession? AccessorySession { get; }
}

/// <summary>
/// Client (<see cref="HapPairingClient"/>) against accessory (<see cref="HapAccessoryPairing"/>) for all three procedures
/// of SPEC-control.md 3, including the error paths of 3.9. The transport is supplied by the derived class: an in-memory
/// double here, a loopback socket pair where rtsp-transport is available.
/// </summary>
public abstract class PairingProcedureTests
{
    private static readonly JsonElement SrpVector = LoadSrpVector();

    /// <summary>Pinned HKDF-SHA512 known answers, computed with Python's hmac/hashlib from the vector's K (srptools) and from the RFC 7748 6.1 secret.</summary>
    private const string TransientControlWrite = "4cd30fa01e6834ec9ca347569da86cc1ddbe28ae3d1a694c3a13b6aa25a24f24";
    private const string TransientControlRead = "93470be57397c4599771e2e4766be86a461d0ccef394520617fcaac08251203a";
    private const string VerifyShared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
    private const string VerifyControlWrite = "55af022545eaa2dc8d998d047e055ec37025a759d96aef78d1523a832844b011";
    private const string VerifyControlRead = "522fa526a28d246e23ad85295ab1be5f06d279cf67da2e1263e34dab427fe463";

    /// <summary>Opens a client connection to <paramref name="accessory"/>.</summary>
    private protected abstract Task<IPairingLink> OpenAsync(HapAccessoryPairing accessory, ResponseTransform? transform = null);

    protected static byte[] Hex(string s) => Convert.FromHexString(s);

    protected static HapPairingClient NewClient(Func<byte[]>? srpPrivateKey = null, Func<X25519KeyPair>? ephemeral = null) =>
        new(NullLogger<HapPairingClient>.Instance) { SrpPrivateKeyFactory = srpPrivateKey, EphemeralKeyFactory = ephemeral };

    private protected static HapAccessoryPairing NewAccessory(string pin = "1234", bool allowTransient = true, bool deterministicSrp = false, bool allowMethod1 = false) =>
        new(new HapAccessoryOptions(new ControllerIdentity("AA:BB:CC:DD:EE:FF", Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f")), pin, allowTransient)
        {
            SrpSalt = deterministicSrp ? Hex(SrpVector.GetProperty("s").GetString()!) : null,
            SrpPrivateKey = deterministicSrp ? Hex(SrpVector.GetProperty("b").GetString()!) : null,
            AllowPairSetupWithAuth = allowMethod1,
        });

    private static JsonElement LoadSrpVector()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestVectors", "srp-3072-sha512.json")));
        return doc.RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("I").GetString() == "Pair-Setup").Clone();
    }

    private static byte[] VectorA() => Hex(SrpVector.GetProperty("a").GetString()!);

    private static ControllerIdentity Controller() =>
        new("6F3B2A1C-0000-4000-8000-0123456789AB", Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));

    private static void AssertKeysMirror(PairingSession client, PairingSession? accessory)
    {
        Assert.NotNull(accessory);
        Assert.Equal(client.Kind, accessory.Kind);
        Assert.Equal(client.SharedSecret, accessory.SharedSecret);
        var accessoryKeys = HapAccessoryPairing.ControlKeysFor(accessory);
        Assert.Equal(client.ControlKeys.WriteKey, accessoryKeys.ReadKey);
        Assert.Equal(client.ControlKeys.ReadKey, accessoryKeys.WriteKey);

        // And the keys actually work as a channel in both directions.
        var us = new HapFrameCipher(client.ControlKeys);
        var them = new HapFrameCipher(accessoryKeys);
        var message = Encoding.ASCII.GetBytes("GET /info RTSP/1.0\r\nCSeq: 5\r\n\r\n");
        using var received = new MemoryStream();
        var frames = us.Encrypt(message);
        Assert.Equal(frames.Length, them.DecryptFrames(frames, received));
        Assert.Equal(message, received.ToArray());
        using var back = new MemoryStream();
        var reply = them.Encrypt(message);
        Assert.Equal(reply.Length, us.DecryptFrames(reply, back));
    }

    private static async Task<PairingCredentials> PairWithPinAsync(HapAccessoryPairing accessory, IPairingLink link, string pin, HapPairingClient? client = null)
    {
        client ??= NewClient();
        var started = await client.StartPinPairingAsync(link.Connection, Controller(), TlvMethod.PairSetup, CancellationToken.None);
        return await client.FinishPinPairingAsync(started, pin, CancellationToken.None);
    }

    // ---------------------------------------------------------------- transient (3.5)

    [Fact]
    public async Task Transient_pair_setup_derives_the_same_keys_on_both_sides()
    {
        var accessory = NewAccessory();
        await using var link = await OpenAsync(accessory);
        var session = await NewClient().PairTransientAsync(link.Connection, CancellationToken.None);

        Assert.Equal(PairingKind.Transient, session.Kind);
        Assert.Equal(64, session.SharedSecret.Length);
        AssertKeysMirror(session, link.AccessorySession);
        Assert.False(link.Connection.IsEncrypted); // the caller enables encryption, after the plaintext M4 (SPEC-control.md 2.5)
    }

    [Fact]
    public async Task Transient_pair_setup_with_fixed_inputs_matches_the_srp_vector_and_the_pinned_hkdf_answers()
    {
        var accessory = NewAccessory(deterministicSrp: true);
        await using var link = await OpenAsync(accessory);
        var session = await NewClient(VectorA).PairTransientAsync(link.Connection, CancellationToken.None);

        Assert.Equal(Hex(SrpVector.GetProperty("K").GetString()!), session.SharedSecret);
        Assert.Equal(Hex(TransientControlWrite), session.ControlKeys.WriteKey);
        Assert.Equal(Hex(TransientControlRead), session.ControlKeys.ReadKey);
        var accessoryKeys = HapAccessoryPairing.ControlKeysFor(link.AccessorySession!);
        Assert.Equal(Hex(TransientControlRead), accessoryKeys.WriteKey);
        Assert.Equal(Hex(TransientControlWrite), accessoryKeys.ReadKey);

        // M3 carries the vector's A and M1, M4 the vector's M2.
        var m3 = Tlv8Document.Decode(link.Requests[2].Body);
        Assert.Equal(Hex(SrpVector.GetProperty("A").GetString()!), m3.Get(TlvType.PublicKey));
        Assert.Equal(Hex(SrpVector.GetProperty("M1").GetString()!), m3.Get(TlvType.Proof));
        Assert.Equal(Hex(SrpVector.GetProperty("M2").GetString()!), Tlv8Document.Decode(link.ResponseBodies[2]).Get(TlvType.Proof));
    }

    [Fact]
    public async Task Transient_messages_carry_the_headers_bodies_and_fragmented_384_byte_keys()
    {
        var accessory = NewAccessory(deterministicSrp: true);
        await using var link = await OpenAsync(accessory);
        await NewClient(VectorA).PairTransientAsync(link.Connection, CancellationToken.None);

        Assert.Equal(["/pair-pin-start", "/pair-setup", "/pair-setup"], link.Requests.Select(r => r.Uri));
        foreach (var request in link.Requests)
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal("4", request.Header("X-Apple-HKP"));
            Assert.Equal("application/octet-stream", request.Header("Content-Type"));
            Assert.Equal(request.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Header("Content-Length"));
            Assert.Equal("keep-alive", request.Header("Connection"));
        }

        Assert.Empty(link.Requests[0].Body); // pin-start: Content-Type + Content-Length 0, no body
        Assert.Equal(Hex("000100060101130110"), link.Requests[1].Body); // Method 0, State 1, Flags 0x10

        // A (M3) and B (M2) are 384 bytes: items of 255 + 129 bytes with the same type (SPEC-control.md 3.2).
        AssertFragmented(link.Requests[2].Body, TlvType.PublicKey, 384);
        AssertFragmented(link.ResponseBodies[1], TlvType.PublicKey, 384);
    }

    private static void AssertFragmented(byte[] tlv, TlvType type, int totalLength)
    {
        var lengths = new List<int>();
        var pos = 0;
        while (pos < tlv.Length)
        {
            if (tlv[pos] == (byte)type)
            {
                lengths.Add(tlv[pos + 1]);
            }

            pos += 2 + tlv[pos + 1];
        }

        Assert.Equal([255, totalLength - 255], lengths);
    }

    [Fact]
    public async Task Transient_refused_with_470_maps_to_TransientRefused()
    {
        var accessory = NewAccessory(allowTransient: false);
        await using var link = await OpenAsync(accessory);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(link.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.TransientRefused, ex.Failure);
        Assert.Equal(470, ex.StatusCode);
        Assert.IsType<RtspStatusException>(ex.InnerException);
    }

    [Fact]
    public async Task Transient_TLV_error_in_M2_maps_to_TransientRefused()
    {
        var accessory = NewAccessory();
        await using var link = await OpenAsync(accessory, (path, request, response) =>
            request?.State == 1 ? new HapAccessoryResponse(200, new Tlv8Document().AddState(2).Add(TlvType.Error, (byte)TlvError.Unavailable).Encode(), null) : response);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(link.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.TransientRefused, ex.Failure);
        Assert.Equal(TlvError.Unavailable, ex.TlvError);
    }

    [Fact]
    public async Task Transient_authentication_error_in_M4_maps_to_TransientRefused()
    {
        // shairport-sync with --password uses the password for the transient SRP too, so M3 gets Error 2.
        var accessory = NewAccessory();
        await using var link = await OpenAsync(accessory, (path, request, response) =>
            request?.State == 3 ? new HapAccessoryResponse(200, new Tlv8Document().AddState(4).Add(TlvType.Error, (byte)TlvError.Authentication).Encode(), null) : response);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(link.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.TransientRefused, ex.Failure);
        Assert.Equal(TlvError.Authentication, ex.TlvError);
    }

    [Fact]
    public async Task Transient_M4_proof_mismatch_is_refused()
    {
        var accessory = NewAccessory();
        await using var link = await OpenAsync(accessory, (path, request, response) =>
        {
            if (request?.State != 3)
            {
                return response;
            }

            var proof = Tlv8Document.Decode(response.Body).Get(TlvType.Proof)!;
            proof[10] ^= 1;
            return response with { Body = new Tlv8Document().AddState(4).Add(TlvType.Proof, proof).Encode() };
        });
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(link.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.TransientRefused, ex.Failure);
    }

    [Theory]
    [InlineData(TlvError.Busy)]
    [InlineData(TlvError.BackOff)]
    [InlineData(TlvError.MaxTries)]
    public async Task Busy_backoff_and_max_tries_are_retryable(TlvError error)
    {
        var accessory = NewAccessory();
        await using var link = await OpenAsync(accessory, (path, request, response) =>
            request?.State == 1
                ? new HapAccessoryResponse(200, new Tlv8Document().AddState(2).Add(TlvType.Error, (byte)error).Add(TlvType.RetryDelay, [30, 0]).Encode(), null)
                : response);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(link.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.Retryable, ex.Failure);
        Assert.Equal(error, ex.TlvError);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public async Task Malformed_or_out_of_state_responses_are_protocol_errors()
    {
        var accessory = NewAccessory();
        await using var truncated = await OpenAsync(accessory, (path, request, response) =>
            request?.State == 1 ? response with { Body = [0x06, 0x05, 0x02] } : response);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(truncated.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.Protocol, ex.Failure);

        await using var wrongState = await OpenAsync(accessory.ForConnection(), (path, request, response) =>
            request?.State == 1 ? response with { Body = new Tlv8Document().AddState(4).Encode() } : response);
        ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(wrongState.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.Protocol, ex.Failure);

        await using var shortSalt = await OpenAsync(accessory.ForConnection(), (path, request, response) =>
        {
            if (request?.State != 1)
            {
                return response;
            }

            var doc = Tlv8Document.Decode(response.Body);
            return response with { Body = new Tlv8Document().AddState(2).Add(TlvType.Salt, new byte[8]).Add(TlvType.PublicKey, doc.Get(TlvType.PublicKey)!).Encode() };
        });
        ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(shortSalt.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.Protocol, ex.Failure);
    }

    [Fact]
    public async Task Pin_start_404_is_tolerated_but_other_errors_are_not()
    {
        var accessory = NewAccessory();
        await using var notFound = await OpenAsync(accessory, (path, request, response) =>
            path == HapPairingEndpoints.PinStart ? new HapAccessoryResponse(404, [], null) : response);
        var session = await NewClient().PairTransientAsync(notFound.Connection, CancellationToken.None);
        Assert.Equal(PairingKind.Transient, session.Kind);

        await using var forbidden = await OpenAsync(accessory.ForConnection(), (path, request, response) =>
            path == HapPairingEndpoints.PinStart ? new HapAccessoryResponse(403, [], null) : response);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().PairTransientAsync(forbidden.Connection, CancellationToken.None));
        Assert.Equal(PairingFailure.Protocol, ex.Failure);
        Assert.Equal(403, ex.StatusCode);
    }

    // ---------------------------------------------------------------- regular pair-setup (3.6) and pair-verify (3.7)

    [Fact]
    public async Task Pin_pairing_then_pair_verify_on_a_new_connection()
    {
        var accessory = NewAccessory(pin: "4711");
        var client = NewClient();
        PairingCredentials credentials;
        await using (var setup = await OpenAsync(accessory))
        {
            credentials = await PairWithPinAsync(accessory, setup, "4711", client);
            Assert.Null(setup.AccessorySession); // regular pair-setup does not start encryption
            Assert.Equal(["/pair-pin-start", "/pair-setup", "/pair-setup", "/pair-setup"], setup.Requests.Select(r => r.Uri));
            Assert.All(setup.Requests, r => Assert.Equal("3", r.Header("X-Apple-HKP")));
            Assert.All(setup.Requests, r => Assert.Equal("application/octet-stream", r.Header("Content-Type")));
            Assert.Equal(Hex("000100060101"), setup.Requests[1].Body); // Method 0, State 1, no Flags
            AssertFragmented(setup.Requests[2].Body, TlvType.PublicKey, Tlv8Document.Decode(setup.Requests[2].Body).Get(TlvType.PublicKey)!.Length);
        }

        Assert.Equal(accessory.AccessoryId, credentials.AccessoryId);
        Assert.Equal(accessory.AccessoryPublicKey, credentials.AccessoryPublicKey);
        Assert.True(accessory.KnownControllers.TryGet(credentials.Controller.Id, out var registered));
        Assert.Equal(credentials.Controller.KeyPair.PublicKey, registered);

        await using var verify = await OpenAsync(accessory.ForConnection());
        var session = await client.VerifyAsync(verify.Connection, credentials, CancellationToken.None);
        Assert.Equal(PairingKind.Verified, session.Kind);
        Assert.Equal(32, session.SharedSecret.Length);
        AssertKeysMirror(session, verify.AccessorySession);
        Assert.Equal(["/pair-verify", "/pair-verify"], verify.Requests.Select(r => r.Uri));
        Assert.All(verify.Requests, r => Assert.Equal("3", r.Header("X-Apple-HKP")));
        Assert.All(verify.Requests, r => Assert.Equal("application/octet-stream", r.Header("Content-Type")));
    }

    [Fact]
    public async Task Pair_verify_on_the_same_connection_after_pin_pairing()
    {
        // owntone does PAIR_SETUP then PAIR_VERIFY back to back (SPEC-control.md 3.6 step 8).
        var accessory = NewAccessory();
        var client = NewClient();
        await using var link = await OpenAsync(accessory);
        var credentials = await PairWithPinAsync(accessory, link, "1234", client);
        var session = await client.VerifyAsync(link.Connection, credentials, CancellationToken.None);
        AssertKeysMirror(session, link.AccessorySession);
    }

    [Fact]
    public async Task Pair_verify_with_fixed_ephemeral_keys_matches_the_pinned_hkdf_answers()
    {
        var accessory = new HapAccessoryPairing(NewAccessory().Options with
        {
            EphemeralKeyFactory = () => X25519KeyPair.FromPrivateKey(Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb")),
        });
        var client = NewClient(ephemeral: () => X25519KeyPair.FromPrivateKey(Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")));
        await using var setup = await OpenAsync(accessory);
        var credentials = await PairWithPinAsync(accessory, setup, "1234", client);

        await using var link = await OpenAsync(accessory.ForConnection());
        var session = await client.VerifyAsync(link.Connection, credentials, CancellationToken.None);
        Assert.Equal(Hex(VerifyShared), session.SharedSecret); // RFC 7748 6.1
        Assert.Equal(Hex(VerifyControlWrite), session.ControlKeys.WriteKey);
        Assert.Equal(Hex(VerifyControlRead), session.ControlKeys.ReadKey);
        Assert.Equal(Hex(VerifyControlRead), HapAccessoryPairing.ControlKeysFor(link.AccessorySession!).WriteKey);
    }

    [Fact]
    public async Task Wrong_pin_is_an_authentication_failure_with_TLV_error_2()
    {
        var accessory = NewAccessory(pin: "1234");
        await using var link = await OpenAsync(accessory);
        var client = NewClient();
        var started = await client.StartPinPairingAsync(link.Connection, Controller(), TlvMethod.PairSetup, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.FinishPinPairingAsync(started, "9999", CancellationToken.None));
        Assert.Equal(PairingFailure.Authentication, ex.Failure);
        Assert.Equal(TlvError.Authentication, ex.TlvError);
        Assert.Equal(4, Tlv8Document.Decode(link.ResponseBodies[^1]).State);
        Assert.Equal(0, accessory.KnownControllers.Count);

        // The state is single-use.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FinishPinPairingAsync(started, "1234", CancellationToken.None));

        // A fresh start on the same connection succeeds with the right PIN.
        var again = await client.StartPinPairingAsync(link.Connection, Controller(), TlvMethod.PairSetup, CancellationToken.None);
        var credentials = await client.FinishPinPairingAsync(again, "1234", CancellationToken.None);
        Assert.Equal(accessory.AccessoryId, credentials.AccessoryId);
    }

    [Fact]
    public async Task Tampered_M6_signature_fails_the_pairing()
    {
        // Deterministic SRP (vector a, b, salt with PIN 3939) so the test knows K and can re-encrypt a tampered M6.
        var sessionKey = Hex(SrpVector.GetProperty("K").GetString()!);
        var encryptKey = HapKdf.Derive(sessionKey, HapKeyInfo.PairSetupEncryptSalt, HapKeyInfo.PairSetupEncryptInfo);
        var nonce = ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairSetupM6Nonce);
        var accessory = NewAccessory(pin: "3939", deterministicSrp: true);
        await using var link = await OpenAsync(accessory, (path, request, response) =>
        {
            if (request?.State != 5)
            {
                return response;
            }

            var sub = Tlv8Document.Decode(ChaCha20Poly1305Aead.Decrypt(encryptKey, nonce, Tlv8Document.Decode(response.Body).Get(TlvType.EncryptedData)!, [])!);
            var signature = sub.Get(TlvType.Signature)!;
            signature[0] ^= 0x01;
            var tampered = new Tlv8Document()
                .Add(TlvType.Identifier, sub.Get(TlvType.Identifier)!)
                .Add(TlvType.PublicKey, sub.Get(TlvType.PublicKey)!)
                .Add(TlvType.Signature, signature);
            var encrypted = ChaCha20Poly1305Aead.Encrypt(encryptKey, nonce, tampered.Encode(), []);
            return response with { Body = new Tlv8Document().AddState(6).Add(TlvType.EncryptedData, encrypted).Encode() };
        });

        var client = NewClient(VectorA);
        var started = await client.StartPinPairingAsync(link.Connection, Controller(), TlvMethod.PairSetup, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.FinishPinPairingAsync(started, "3939", CancellationToken.None));
        Assert.Equal(PairingFailure.Protocol, ex.Failure);
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Undecryptable_M6_fails_the_pairing()
    {
        var accessory = NewAccessory();
        await using var link = await OpenAsync(accessory, (path, request, response) =>
        {
            if (request?.State != 5)
            {
                return response;
            }

            var encrypted = Tlv8Document.Decode(response.Body).Get(TlvType.EncryptedData)!;
            encrypted[^1] ^= 0x80;
            return response with { Body = new Tlv8Document().AddState(6).Add(TlvType.EncryptedData, encrypted).Encode() };
        });
        var ex = await Assert.ThrowsAsync<PairingException>(() => PairWithPinAsync(accessory, link, "1234"));
        Assert.Equal(PairingFailure.Protocol, ex.Failure);
    }

    [Fact]
    public async Task Pair_verify_by_an_unknown_controller_is_rejected()
    {
        var accessory = NewAccessory();
        var stranger = new PairingCredentials(ControllerIdentity.Generate(), accessory.AccessoryId, accessory.AccessoryPublicKey);
        await using var link = await OpenAsync(accessory);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().VerifyAsync(link.Connection, stranger, CancellationToken.None));
        Assert.Equal(PairingFailure.CredentialsRejected, ex.Failure);
        Assert.Equal(TlvError.Authentication, ex.TlvError);
        Assert.Null(link.AccessorySession);
    }

    [Fact]
    public async Task Pair_verify_answered_with_470_is_rejected()
    {
        var accessory = NewAccessory();
        await using var setup = await OpenAsync(accessory);
        var credentials = await PairWithPinAsync(accessory, setup, "1234");
        await using var link = await OpenAsync(accessory.ForConnection(), (path, request, response) =>
            path == HapPairingEndpoints.PairVerify ? new HapAccessoryResponse(470, [], null) : response);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().VerifyAsync(link.Connection, credentials, CancellationToken.None));
        Assert.Equal(PairingFailure.CredentialsRejected, ex.Failure);
        Assert.Equal(470, ex.StatusCode);
    }

    [Fact]
    public async Task Pair_verify_against_a_reset_receiver_is_rejected()
    {
        // The receiver was reset: same id, new long-term key. Our stored LTPK no longer verifies its M2 signature.
        var accessory = NewAccessory();
        await using var setup = await OpenAsync(accessory);
        var credentials = await PairWithPinAsync(accessory, setup, "1234");
        var reset = new HapAccessoryPairing(accessory.Options with { Identity = new ControllerIdentity(accessory.AccessoryId, Ed25519KeyPair.Generate().Seed.ToArray()) });
        reset.KnownControllers.Set(credentials.Controller.Id, credentials.Controller.KeyPair.PublicKey);

        await using var link = await OpenAsync(reset);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().VerifyAsync(link.Connection, credentials, CancellationToken.None));
        Assert.Equal(PairingFailure.CredentialsRejected, ex.Failure);

        // A different accessory id is rejected as well.
        await using var other = await OpenAsync(accessory.ForConnection());
        ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().VerifyAsync(other.Connection, credentials with { AccessoryId = "11:22:33:44:55:66" }, CancellationToken.None));
        Assert.Equal(PairingFailure.CredentialsRejected, ex.Failure);
    }

    [Fact]
    public async Task Pair_verify_with_a_corrupted_stored_key_is_rejected_without_contacting_the_receiver()
    {
        // A truncated pairings row must take the CredentialsRejected path so the caller deletes it (SPEC-control.md 3.8).
        var accessory = NewAccessory();
        var corrupted = new PairingCredentials(Controller(), accessory.AccessoryId, accessory.AccessoryPublicKey.AsSpan(0, 31).ToArray());
        await using var link = await OpenAsync(accessory);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().VerifyAsync(link.Connection, corrupted, CancellationToken.None));
        Assert.Equal(PairingFailure.CredentialsRejected, ex.Failure);
        Assert.Empty(link.Requests);
    }

    [Fact]
    public async Task Pair_verify_M4_with_an_empty_body_completes()
    {
        // pyatv ignores the M4 body [pyatv:hap.py:136-138]; a 2xx M4 without TLV carries no Error and is accepted.
        var accessory = NewAccessory();
        await using var setup = await OpenAsync(accessory);
        var credentials = await PairWithPinAsync(accessory, setup, "1234");
        await using var link = await OpenAsync(accessory.ForConnection(), (path, request, response) =>
            path == HapPairingEndpoints.PairVerify && request?.State == 3 ? response with { Body = [] } : response);
        var session = await NewClient().VerifyAsync(link.Connection, credentials, CancellationToken.None);
        Assert.Equal(PairingKind.Verified, session.Kind);
        AssertKeysMirror(session, link.AccessorySession);
    }

    [Fact]
    public async Task Pair_verify_M4_with_a_wrong_state_is_a_protocol_failure()
    {
        var accessory = NewAccessory();
        await using var setup = await OpenAsync(accessory);
        var credentials = await PairWithPinAsync(accessory, setup, "1234");
        await using var link = await OpenAsync(accessory.ForConnection(), (path, request, response) =>
            path == HapPairingEndpoints.PairVerify && request?.State == 3 ? response with { Body = new Tlv8Document().AddState(6).Encode() } : response);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().VerifyAsync(link.Connection, credentials, CancellationToken.None));
        Assert.Equal(PairingFailure.Protocol, ex.Failure);
    }

    [Fact]
    public async Task M1_method_is_selectable_for_probe_P6()
    {
        // Default accessory behaves like pair_ap: Method 1 → 470.
        var strict = NewAccessory();
        await using var link = await OpenAsync(strict);
        var ex = await Assert.ThrowsAsync<PairingException>(() => NewClient().StartPinPairingAsync(link.Connection, Controller(), TlvMethod.PairSetupWithAuth, CancellationToken.None));
        Assert.Equal(PairingFailure.Protocol, ex.Failure);
        Assert.Equal(470, ex.StatusCode);
        Assert.Equal(Hex("000101060101"), link.Requests[1].Body);

        var lenient = NewAccessory(allowMethod1: true);
        await using var link2 = await OpenAsync(lenient);
        var client = NewClient();
        var started = await client.StartPinPairingAsync(link2.Connection, Controller(), TlvMethod.PairSetupWithAuth, CancellationToken.None);
        Assert.Equal(TlvMethod.PairSetupWithAuth, started.Method);
        var credentials = await client.FinishPinPairingAsync(started, "1234", CancellationToken.None);
        Assert.Equal(lenient.AccessoryId, credentials.AccessoryId);
    }
}

/// <summary>The pairing procedures over the in-memory <see cref="InMemoryRtspConnection"/>.</summary>
public sealed class InMemoryPairingProcedureTests : PairingProcedureTests
{
    private protected override Task<IPairingLink> OpenAsync(HapAccessoryPairing accessory, ResponseTransform? transform = null) =>
        Task.FromResult<IPairingLink>(new Link(new InMemoryRtspConnection(accessory, transform)));

    private sealed class Link(InMemoryRtspConnection connection) : IPairingLink
    {
        public IRtspConnection Connection => connection;

        public IReadOnlyList<RecordedRequest> Requests => connection.Requests;

        public IReadOnlyList<byte[]> ResponseBodies => connection.ResponseBodies;

        public PairingSession? AccessorySession => connection.AccessorySession;

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
