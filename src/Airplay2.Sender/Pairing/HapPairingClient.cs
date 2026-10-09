using System.Security.Cryptography;
using System.Text;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Pairing;

/// <summary>Endpoints and <c>X-Apple-HKP</c> values of the pairing POSTs (SPEC-control.md 2.2, 3.1).</summary>
public static class HapPairingEndpoints
{
    /// <summary><c>POST /pair-pin-start</c>: empty body; makes a PIN receiver display its PIN.</summary>
    public const string PinStart = "/pair-pin-start";

    /// <summary><c>POST /pair-setup</c>: M1/M3/M5 of transient and regular pair-setup.</summary>
    public const string PairSetup = "/pair-setup";

    /// <summary><c>POST /pair-verify</c>: M1/M3 of pair-verify.</summary>
    public const string PairVerify = "/pair-verify";

    /// <summary><c>X-Apple-HKP</c> for regular (HomeKit) pair-setup and pair-verify [pyatv:hap.py:22; owntone:airplay.c:2835-2836].</summary>
    public const string HkpRegular = "3";

    /// <summary><c>X-Apple-HKP</c> for transient pair-setup [pyatv:hap_transient.py:26].</summary>
    public const string HkpTransient = "4";

    /// <summary>The HTTP verb of every pairing request.</summary>
    public const string Method = "POST";
}

/// <summary>
/// The controller (sender) side of the three HomeKit procedures of SPEC-control.md 3 over an <see cref="IRtspConnection"/>:
/// transient pair-setup M1-M4 (3.5), regular pair-setup M1-M6 split into start/finish for the web app (3.6) and
/// pair-verify M1-M4 (3.7), with the error mapping of 3.9. The client never retries on its own and never switches the
/// connection to encryption: the caller (session-streams) decides on fallbacks (470 → PIN flow, rejected credentials →
/// delete and re-pair) and calls <see cref="IRtspConnection.EnableEncryption"/> with <see cref="PairingSession.ControlKeys"/>
/// after the plaintext M4 response was read (2.5). Transport failures (<see cref="IOException"/>, <see cref="TimeoutException"/>
/// from the connection's request timeout, cancellation) propagate unchanged; everything the receiver says or fails to
/// say correctly becomes a <see cref="PairingException"/>.
/// </summary>
public sealed class HapPairingClient(ILogger<HapPairingClient> logger) : IPairingClient
{
    private static readonly IReadOnlyDictionary<string, string> TransientHeaders = PairingHeaders(HapPairingEndpoints.HkpTransient);
    private static readonly IReadOnlyDictionary<string, string> RegularHeaders = PairingHeaders(HapPairingEndpoints.HkpRegular);

    // Clamp for a receiver-supplied RetryDelay so a bogus value cannot overflow TimeSpan.
    private const ulong MaxRetryDelaySeconds = 24 * 60 * 60;

    private ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Test hook: supplies the 32-byte SRP private value <c>a</c> (random when null).</summary>
    internal Func<byte[]>? SrpPrivateKeyFactory { get; init; }

    /// <summary>Test hook: supplies the ephemeral X25519 key pair of pair-verify (random when null).</summary>
    internal Func<X25519KeyPair>? EphemeralKeyFactory { get; init; }

    /// <inheritdoc />
    /// <remarks>
    /// Error mapping (SPEC-control.md 3.5, 3.9, SPEC.md D2): HTTP 470 on pin-start or M1, a TLV <c>Error</c> in M2 or M4,
    /// or an M4 proof that does not match PIN 3939 → <see cref="PairingFailure.TransientRefused"/> (shairport-sync with a
    /// password answers transient M3 with <c>Error = 2</c> because its SRP verifier uses the password [sps:rtsp.c:1793-1794;
    /// sps:pair_ap/pair_homekit.c:2015-2023]); <c>BackOff</c>, <c>MaxTries</c> and <c>Busy</c> → <see cref="PairingFailure.Retryable"/>
    /// instead (3.9: "retryable later"), because falling back to the PIN flow would not help a busy receiver.
    /// </remarks>
    public async Task<PairingSession> PairTransientAsync(IRtspConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Logger.LogDebug("Transient pair-setup with {Receiver}", connection.RemoteEndPoint);

        await PinStartAsync(connection, Procedure.Transient, cancellationToken).ConfigureAwait(false);

        // M1: Method = PairSetup, State = 1, Flags = transient [pyatv:hap_transient.py:51-60; sps:pair_homekit.c:1272-1280].
        var m1 = new Tlv8Document()
            .AddMethod(TlvMethod.PairSetup)
            .AddState(1)
            .Add(TlvType.Flags, (byte)TlvFlags.Transient);
        var m2 = await ExchangeAsync(connection, HapPairingEndpoints.PairSetup, Procedure.Transient, "M1", m1, 2, cancellationToken).ConfigureAwait(false);
        var (salt, serverPublicKey) = ReadChallenge(m2, Procedure.Transient);

        var srp = CreateSrpClient(Srp6a.TransientPin);
        var srpSession = ProcessChallenge(srp, salt, serverPublicKey, Procedure.Transient);

        // M3: State = 3, PublicKey = A, Proof = M1 [pyatv:hap_transient.py:73-80; sps:pair_homekit.c:1314-1322].
        var m3 = new Tlv8Document()
            .AddState(3)
            .Add(TlvType.PublicKey, srp.PublicKey)
            .Add(TlvType.Proof, srpSession.ClientProof);
        var m4 = await ExchangeAsync(connection, HapPairingEndpoints.PairSetup, Procedure.Transient, "M3", m3, 4, cancellationToken).ConfigureAwait(false);

        // M4: verify M2 (pyatv only checks it implicitly; pair_ap enforces it [sps:pair_homekit.c:1503-1520]).
        var proof = Require(m4, TlvType.Proof, Procedure.Transient, "M4");
        if (!srpSession.VerifyServerProof(proof))
        {
            Logger.LogWarning("Transient pair-setup with {Receiver}: M4 proof does not match PIN 3939", connection.RemoteEndPoint);
            throw new PairingException(PairingFailure.TransientRefused, "Transient pair-setup: the receiver's M4 proof does not match PIN 3939; it is not in transient mode.");
        }

        Logger.LogInformation("Transient pair-setup with {Receiver} complete", connection.RemoteEndPoint);
        return new PairingSession(PairingKind.Transient, srpSession.SessionKey);
    }

    /// <inheritdoc />
    /// <remarks>
    /// pin-start answered with 404 or 501 is logged and skipped (a password receiver, flags bit 7, shows nothing anyway);
    /// any other non-2xx, a TLV <c>Error</c> in M2 or a malformed M2 fails with <see cref="PairingException"/>.
    /// </remarks>
    public async Task<PinPairingInProgress> StartPinPairingAsync(IRtspConnection connection, ControllerIdentity controller, TlvMethod method, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(controller);
        if (method is not (TlvMethod.PairSetup or TlvMethod.PairSetupWithAuth))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Pair-setup M1 uses Method 0 (PairSetup) or 1 (PairSetupWithAuth).");
        }

        Logger.LogDebug("PIN pair-setup with {Receiver}: pin-start, M1 method {Method}", connection.RemoteEndPoint, method);
        await PinStartAsync(connection, Procedure.Regular, cancellationToken).ConfigureAwait(false);

        // M1: Method, State = 1, no Flags [pyatv:hap.py:54-57; sps:pair_homekit.c:1272-1276]; method 1 only for probe P6 / bit 51.
        var m1 = new Tlv8Document().AddMethod(method).AddState(1);
        var m2 = await ExchangeAsync(connection, HapPairingEndpoints.PairSetup, Procedure.Regular, "M1", m1, 2, cancellationToken).ConfigureAwait(false);
        var (salt, serverPublicKey) = ReadChallenge(m2, Procedure.Regular);

        Logger.LogInformation("PIN pair-setup with {Receiver} started; waiting for the PIN", connection.RemoteEndPoint);
        return new PinPairingInProgress(connection, controller, salt, serverPublicKey, method);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A state can be finished once (the receiver's SRP session accepts one M3); a second call throws
    /// <see cref="InvalidOperationException"/>. TLV <c>Error = 2</c> or a mismatching proof in M4 →
    /// <see cref="PairingFailure.Authentication"/>; <c>BackOff</c>/<c>MaxTries</c>/<c>Busy</c> → <see cref="PairingFailure.Retryable"/>;
    /// a missing item, an undecryptable M6 or an invalid M6 signature → <see cref="PairingFailure.Protocol"/>.
    /// </remarks>
    public async Task<PairingCredentials> FinishPinPairingAsync(PinPairingInProgress pairing, string pin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentException.ThrowIfNullOrEmpty(pin);
        if (!pairing.TryConsume())
        {
            throw new InvalidOperationException("This PIN pairing was already finished or failed; start a new one.");
        }

        var connection = pairing.Connection;
        var controller = pairing.Controller;

        // The PIN is used verbatim as the SRP password (SPEC-control.md 3.3).
        var srp = CreateSrpClient(pin);
        var srpSession = ProcessChallenge(srp, pairing.Salt, pairing.ServerPublicKey, Procedure.Regular);

        // M3/M4.
        var m3 = new Tlv8Document()
            .AddState(3)
            .Add(TlvType.PublicKey, srp.PublicKey)
            .Add(TlvType.Proof, srpSession.ClientProof);
        var m4 = await ExchangeAsync(connection, HapPairingEndpoints.PairSetup, Procedure.Regular, "M3", m3, 4, cancellationToken).ConfigureAwait(false);
        var proof = Require(m4, TlvType.Proof, Procedure.Regular, "M4");
        if (!srpSession.VerifyServerProof(proof))
        {
            Logger.LogWarning("PIN pair-setup with {Receiver}: M4 proof mismatch", connection.RemoteEndPoint);
            throw new PairingException(PairingFailure.Authentication, "PIN pair-setup: the receiver's M4 proof does not match; wrong PIN?");
        }

        var sessionKey = srpSession.SessionKey;
        var encryptKey = HapKdf.Derive(sessionKey, HapKeyInfo.PairSetupEncryptSalt, HapKeyInfo.PairSetupEncryptInfo);

        // M5: EncryptedData(Identifier || PublicKey || Signature(iOSDeviceX || ourId || LTPK)) [pyatv:hap_srp.py:165-205;
        // sps:pair_homekit.c:1342-1442]. pyatv's optional Name (0x11) item is not sent (SPEC-control.md 3.6 step 6).
        var keyPair = controller.KeyPair;
        var controllerId = Encoding.UTF8.GetBytes(controller.Id);
        var deviceX = HapKdf.Derive(sessionKey, HapKeyInfo.PairSetupControllerSignSalt, HapKeyInfo.PairSetupControllerSignInfo);
        var signature = keyPair.Sign(Concat(deviceX, controllerId, keyPair.PublicKey));
        var subTlv = new Tlv8Document()
            .Add(TlvType.Identifier, controllerId)
            .Add(TlvType.PublicKey, keyPair.PublicKey)
            .Add(TlvType.Signature, signature);
        var encrypted = ChaCha20Poly1305Aead.Encrypt(encryptKey, ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairSetupM5Nonce), subTlv.Encode(), []);
        var m5 = new Tlv8Document().AddState(5).Add(TlvType.EncryptedData, encrypted);
        var m6 = await ExchangeAsync(connection, HapPairingEndpoints.PairSetup, Procedure.Regular, "M5", m5, 6, cancellationToken).ConfigureAwait(false);

        // M6: decrypt, then verify the accessory signature; enforced here, skipped by pyatv [pyatv:hap_srp.py:229],
        // enforced by pair_ap [sps:pair_homekit.c:1547-1665] (SPEC.md D2, C7).
        var m6Encrypted = Require(m6, TlvType.EncryptedData, Procedure.Regular, "M6");
        var m6Plain = ChaCha20Poly1305Aead.Decrypt(encryptKey, ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairSetupM6Nonce), m6Encrypted, [])
            ?? throw new PairingException(PairingFailure.Protocol, "PIN pair-setup: M6 EncryptedData failed authentication.");
        var m6Sub = DecodeTlv(m6Plain, Procedure.Regular, "M6 sub-TLV");
        var accessoryIdBytes = Require(m6Sub, TlvType.Identifier, Procedure.Regular, "M6");
        var accessoryPublicKey = Require(m6Sub, TlvType.PublicKey, Procedure.Regular, "M6");
        var accessorySignature = Require(m6Sub, TlvType.Signature, Procedure.Regular, "M6");
        if (accessoryPublicKey.Length != Ed25519KeyPair.KeySize || accessorySignature.Length != Ed25519KeyPair.SignatureSize || accessoryIdBytes.Length == 0)
        {
            throw new PairingException(PairingFailure.Protocol, "PIN pair-setup: M6 carries an invalid identifier, public key or signature size.");
        }

        var accessoryX = HapKdf.Derive(sessionKey, HapKeyInfo.PairSetupAccessorySignSalt, HapKeyInfo.PairSetupAccessorySignInfo);
        if (!Ed25519KeyPair.Verify(accessoryPublicKey, Concat(accessoryX, accessoryIdBytes, accessoryPublicKey), accessorySignature))
        {
            Logger.LogWarning("PIN pair-setup with {Receiver}: M6 accessory signature invalid", connection.RemoteEndPoint);
            throw new PairingException(PairingFailure.Protocol, "PIN pair-setup: the receiver's M6 signature does not verify.");
        }

        var accessoryId = Encoding.UTF8.GetString(accessoryIdBytes);
        Logger.LogInformation("PIN pair-setup with {Receiver} complete; accessory id {AccessoryId}", connection.RemoteEndPoint, accessoryId);
        return new PairingCredentials(controller, accessoryId, accessoryPublicKey);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="PairingFailure.CredentialsRejected"/> on HTTP 470, a TLV <c>Error</c> in M2/M4 (except the retryable
    /// <c>BackOff</c>/<c>MaxTries</c>/<c>Busy</c>), an accessory identifier that differs from the stored one, or an M2
    /// signature that does not verify with the stored LTPK (the receiver was reset: SPEC-control.md 3.8 "a receiver's
    /// <c>pk</c> changing indicates a reset"), and a stored LTPK that is not 32 bytes (a corrupted pairing row, rejected
    /// before anything is sent). A malformed or undecryptable M2 → <see cref="PairingFailure.Protocol"/>. An M4 with
    /// <c>State = 4</c>, or a 2xx M4 with an empty body (pyatv ignores the M4 body [pyatv:hap.py:136-138]), completes the
    /// procedure; a TLV <c>Error</c> or another <c>State</c> in M4 fails it.
    /// </remarks>
    public async Task<PairingSession> VerifyAsync(IRtspConnection connection, PairingCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credentials);
        if (credentials.AccessoryPublicKey is not { Length: Ed25519KeyPair.KeySize })
        {
            // A corrupted or truncated stored pairing: the caller drops it and re-pairs like any rejected pairing (SPEC-control.md 3.8).
            Logger.LogWarning(
                "Pair-verify with {Receiver}: the stored accessory public key has {Length} bytes, expected 32",
                connection.RemoteEndPoint,
                credentials.AccessoryPublicKey?.Length ?? 0);
            throw new PairingException(PairingFailure.CredentialsRejected, "Pair-verify: the stored accessory public key is not 32 bytes.");
        }

        Logger.LogDebug("Pair-verify with {Receiver} as {ControllerId}", connection.RemoteEndPoint, credentials.Controller.Id);

        // M1: State = 1, PublicKey = ephemeral X25519 [pyatv:hap.py:114-121; sps:pair_homekit.c:1750-1751].
        var ephemeral = EphemeralKeyFactory?.Invoke() ?? X25519KeyPair.Generate();
        var m1 = new Tlv8Document().AddState(1).Add(TlvType.PublicKey, ephemeral.PublicKey);
        var m2 = await ExchangeAsync(connection, HapPairingEndpoints.PairVerify, Procedure.Verify, "M1", m1, 2, cancellationToken).ConfigureAwait(false);

        var accessoryEphemeral = Require(m2, TlvType.PublicKey, Procedure.Verify, "M2");
        var m2Encrypted = Require(m2, TlvType.EncryptedData, Procedure.Verify, "M2");
        if (accessoryEphemeral.Length != X25519KeyPair.KeySize)
        {
            throw new PairingException(PairingFailure.Protocol, $"Pair-verify: M2 public key has {accessoryEphemeral.Length} bytes, expected 32.");
        }

        byte[] shared;
        try
        {
            shared = ephemeral.Agree(accessoryEphemeral);
        }
        catch (CryptographicException ex)
        {
            throw new PairingException(PairingFailure.Protocol, "Pair-verify: the receiver's ephemeral key is a low-order point.", ex);
        }

        var verifyKey = HapKdf.Derive(shared, HapKeyInfo.PairVerifyEncryptSalt, HapKeyInfo.PairVerifyEncryptInfo);
        var m2Plain = ChaCha20Poly1305Aead.Decrypt(verifyKey, ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairVerifyM2Nonce), m2Encrypted, [])
            ?? throw new PairingException(PairingFailure.Protocol, "Pair-verify: M2 EncryptedData failed authentication.");
        var m2Sub = DecodeTlv(m2Plain, Procedure.Verify, "M2 sub-TLV");
        var accessoryIdBytes = Require(m2Sub, TlvType.Identifier, Procedure.Verify, "M2");
        var accessorySignature = Require(m2Sub, TlvType.Signature, Procedure.Verify, "M2");

        // Identity checks [pyatv:hap_srp.py:84-109 ("incorrect device response"); sps:pair_homekit.c:1842-1947].
        var expectedId = Encoding.UTF8.GetBytes(credentials.AccessoryId);
        if (!accessoryIdBytes.AsSpan().SequenceEqual(expectedId))
        {
            Logger.LogWarning(
                "Pair-verify with {Receiver}: accessory id {Actual} differs from the stored {Expected}",
                connection.RemoteEndPoint,
                Encoding.UTF8.GetString(accessoryIdBytes),
                credentials.AccessoryId);
            throw new PairingException(PairingFailure.CredentialsRejected, "Pair-verify: the receiver's identifier differs from the stored pairing.");
        }

        if (!Ed25519KeyPair.Verify(credentials.AccessoryPublicKey, Concat(accessoryEphemeral, accessoryIdBytes, ephemeral.PublicKey), accessorySignature))
        {
            Logger.LogWarning("Pair-verify with {Receiver}: M2 signature does not verify with the stored key", connection.RemoteEndPoint);
            throw new PairingException(PairingFailure.CredentialsRejected, "Pair-verify: the receiver's M2 signature does not verify with the stored public key.");
        }

        // M3: EncryptedData(Identifier(ourId) || Signature(E || ourId || E_acc)) [pyatv:hap_srp.py:111-124; sps:pair_homekit.c:1771-1840].
        var controllerId = Encoding.UTF8.GetBytes(credentials.Controller.Id);
        var signature = credentials.Controller.KeyPair.Sign(Concat(ephemeral.PublicKey, controllerId, accessoryEphemeral));
        var subTlv = new Tlv8Document()
            .Add(TlvType.Identifier, controllerId)
            .Add(TlvType.Signature, signature);
        var encrypted = ChaCha20Poly1305Aead.Encrypt(verifyKey, ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairVerifyM3Nonce), subTlv.Encode(), []);
        var m3 = new Tlv8Document().AddState(3).Add(TlvType.EncryptedData, encrypted);

        // M4: State = 4 and nothing else; an Error is checked here (pyatv does not [pyatv:hap.py:136-138]). A 2xx M4 with an
        // empty body carries no Error and is accepted with a warning rather than being stricter than every reference sender.
        _ = await ExchangeAsync(connection, HapPairingEndpoints.PairVerify, Procedure.Verify, "M3", m3, 4, cancellationToken, allowEmptyBody: true).ConfigureAwait(false);

        Logger.LogInformation("Pair-verify with {Receiver} complete", connection.RemoteEndPoint);
        return new PairingSession(PairingKind.Verified, shared);
    }

    private enum Procedure
    {
        Transient,
        Regular,
        Verify,
    }

    private static IReadOnlyDictionary<string, string> PairingHeaders(string hkp) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Headers on every pairing POST [pyatv:hap.py:20-25; pyatv:hap_transient.py:23-28]; User-Agent and the identity
        // headers come from the codec, Content-Type/Content-Length from RtspRequest.
        [RtspHeaders.Connection] = "keep-alive",
        [RtspHeaders.XAppleHkp] = hkp,
    };

    private SrpClient CreateSrpClient(string password)
    {
        var a = SrpPrivateKeyFactory?.Invoke();
        return a is null
            ? new SrpClient(Srp6a.PairSetupUsername, password)
            : new SrpClient(Srp6a.PairSetupUsername, password, a);
    }

    private static SrpClientSession ProcessChallenge(SrpClient srp, byte[] salt, byte[] serverPublicKey, Procedure procedure)
    {
        try
        {
            return srp.ProcessChallenge(salt, serverPublicKey);
        }
        catch (CryptographicException ex)
        {
            // B mod N == 0 or u == 0 (RFC 5054 2.5.4, sps:pair_homekit.c:506-516).
            throw new PairingException(PairingFailure.Protocol, $"{Name(procedure)}: invalid SRP challenge in M2.", ex);
        }
    }

    private static (byte[] Salt, byte[] ServerPublicKey) ReadChallenge(Tlv8Document m2, Procedure procedure)
    {
        var salt = Require(m2, TlvType.Salt, procedure, "M2");
        var serverPublicKey = Require(m2, TlvType.PublicKey, procedure, "M2");

        // The client rejects salts other than 16 bytes [sps:pair_homekit.c:1465-1470]; B is at most the group size.
        if (salt.Length != 16)
        {
            throw new PairingException(PairingFailure.Protocol, $"{Name(procedure)}: M2 salt has {salt.Length} bytes, expected 16.");
        }

        if (serverPublicKey.Length is 0 or > Srp6a.GroupSize)
        {
            throw new PairingException(PairingFailure.Protocol, $"{Name(procedure)}: M2 public key has {serverPublicKey.Length} bytes.");
        }

        return (salt, serverPublicKey);
    }

    private async Task PinStartAsync(IRtspConnection connection, Procedure procedure, CancellationToken cancellationToken)
    {
        // POST /pair-pin-start: empty body, Content-Type octet-stream + Content-Length 0 (RtspRequest rule), sent before
        // both the transient and the regular flow as pyatv does [pyatv:hap.py:52; pyatv:hap_transient.py:49].
        var request = RtspRequest.WithEmptyBody(HapPairingEndpoints.Method, HapPairingEndpoints.PinStart, RtspContentTypes.OctetStream, HeadersFor(procedure));
        var response = await connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("POST {Uri} -> {Status}", HapPairingEndpoints.PinStart, response.StatusCode);
        if (response.IsSuccess)
        {
            return;
        }

        if (response.StatusCode is RtspStatus.NotFound or RtspStatus.NotImplemented)
        {
            // Not every receiver implements pin-start (it only makes a display show the PIN); M1 decides.
            Logger.LogWarning("{Receiver} answered pin-start with {Status}; continuing with M1", connection.RemoteEndPoint, response.StatusCode);
            return;
        }

        throw StatusFailure(procedure, "pin-start", request, response);
    }

    private async Task<Tlv8Document> ExchangeAsync(
        IRtspConnection connection,
        string uri,
        Procedure procedure,
        string step,
        Tlv8Document body,
        byte expectedState,
        CancellationToken cancellationToken,
        bool allowEmptyBody = false)
    {
        var encoded = body.Encode();
        var request = RtspRequest.WithBody(HapPairingEndpoints.Method, uri, RtspContentTypes.OctetStream, encoded, HeadersFor(procedure));
        Logger.LogDebug("POST {Uri} {Step} ({Length} bytes)", uri, step, encoded.Length);
        var response = await connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("POST {Uri} {Step} -> {Status} ({Length} bytes)", uri, step, response.StatusCode, response.Body.Length);
        if (!response.IsSuccess)
        {
            throw StatusFailure(procedure, step, request, response);
        }

        if (allowEmptyBody && response.Body.IsEmpty)
        {
            Logger.LogWarning("{Procedure} {Step}: {Receiver} answered {Status} with an empty body; accepting it as State {State}", Name(procedure), step, connection.RemoteEndPoint, response.StatusCode, expectedState);
            return new Tlv8Document().AddState(expectedState);
        }

        var document = DecodeTlv(response.Body.Span, procedure, $"{step} response");
        if (document.Error is { } error)
        {
            throw TlvFailure(procedure, step, error, document);
        }

        if (document.State != expectedState)
        {
            throw new PairingException(PairingFailure.Protocol, $"{Name(procedure)}: expected State {expectedState} in the {step} response, got {document.State}.");
        }

        return document;
    }

    private static IReadOnlyDictionary<string, string> HeadersFor(Procedure procedure) =>
        procedure == Procedure.Transient ? TransientHeaders : RegularHeaders;

    private PairingException StatusFailure(Procedure procedure, string step, RtspRequest request, RtspResponse response)
    {
        // 470 "Connection Authorization Required" [sps:rtsp.c:813,1721,1806; owntone:airplay.c:3435-3445].
        var failure = (procedure, response.StatusCode) switch
        {
            (Procedure.Transient, RtspStatus.ConnectionAuthorizationRequired) => PairingFailure.TransientRefused,
            (Procedure.Verify, RtspStatus.ConnectionAuthorizationRequired) => PairingFailure.CredentialsRejected,
            _ => PairingFailure.Protocol,
        };
        Logger.LogWarning("{Procedure} {Step}: receiver answered {Status} {Reason} -> {Failure}", Name(procedure), step, response.StatusCode, response.ReasonPhrase, failure);
        return new PairingException(failure, $"{Name(procedure)} {step}: the receiver answered {response.StatusCode} {response.ReasonPhrase}.", new RtspStatusException(request, response))
        {
            StatusCode = response.StatusCode,
        };
    }

    private PairingException TlvFailure(Procedure procedure, string step, TlvError error, Tlv8Document document)
    {
        // SPEC-control.md 3.9: BackOff (3), MaxTries (5) and Busy (7) are retryable later [sps:pair_homekit.c:860-877].
        var failure = error switch
        {
            TlvError.BackOff or TlvError.MaxTries or TlvError.Busy => PairingFailure.Retryable,
            _ => procedure switch
            {
                Procedure.Transient => PairingFailure.TransientRefused,
                Procedure.Verify => PairingFailure.CredentialsRejected,
                _ => error == TlvError.Authentication ? PairingFailure.Authentication : PairingFailure.Protocol,
            },
        };

        TimeSpan? retryAfter = null;
        if (document.Get(TlvType.RetryDelay) is { Length: > 0 and <= 8 } delay)
        {
            // Seconds, little-endian (SPEC-control.md 3.2).
            ulong seconds = 0;
            for (var i = delay.Length - 1; i >= 0; i--)
            {
                seconds = (seconds << 8) | delay[i];
            }

            retryAfter = TimeSpan.FromSeconds((double)Math.Min(seconds, MaxRetryDelaySeconds));
        }

        Logger.LogWarning("{Procedure} {Step}: receiver sent TLV error {Error} -> {Failure}", Name(procedure), step, error, failure);
        return new PairingException(failure, $"{Name(procedure)} {step}: the receiver answered with TLV error {error} ({(byte)error}).")
        {
            TlvError = error,
            RetryAfter = retryAfter,
        };
    }

    private static Tlv8Document DecodeTlv(ReadOnlySpan<byte> data, Procedure procedure, string what)
    {
        try
        {
            return Tlv8Document.Decode(data);
        }
        catch (FormatException ex)
        {
            throw new PairingException(PairingFailure.Protocol, $"{Name(procedure)}: malformed TLV8 in the {what}.", ex);
        }
    }

    private static byte[] Require(Tlv8Document document, TlvType type, Procedure procedure, string step) =>
        document.Get(type) ?? throw new PairingException(PairingFailure.Protocol, $"{Name(procedure)}: {step} has no {type} item.");

    private static string Name(Procedure procedure) => procedure switch
    {
        Procedure.Transient => "Transient pair-setup",
        Procedure.Regular => "PIN pair-setup",
        _ => "Pair-verify",
    };

    internal static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var output = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(output);
        b.CopyTo(output.AsSpan(a.Length));
        c.CopyTo(output.AsSpan(a.Length + b.Length));
        return output;
    }
}
