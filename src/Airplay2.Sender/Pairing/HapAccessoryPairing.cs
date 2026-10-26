using System.Diagnostics.CodeAnalysis;
using System.Text;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tlv8;

namespace Airplay2.Sender.Pairing;

/// <summary>Options of the accessory side.</summary>
/// <param name="Identity">The accessory's pairing identifier (TXT <c>pi</c>) and long-term Ed25519 key (<c>pk</c>).</param>
/// <param name="Pin">The PIN for regular pair-setup (the fake receiver "shows" it); transient pair-setup always uses 3939.</param>
/// <param name="AllowTransient">Answer transient M1 with M2 (true) or with HTTP 470 (false, SPEC-control.md 3.9).</param>
public sealed record HapAccessoryOptions(ControllerIdentity Identity, string Pin, bool AllowTransient = true)
{
    /// <summary>
    /// Accept <c>Method = 1</c> (PairSetupWithAuth) in M1 like plain pair-setup. Off by default: pair_ap rejects every
    /// method other than 0 with 470 [sps:pair_ap/pair_homekit.c:2074-2078]; airplay2-receiver treats method 1 as an MFi
    /// setup [ap2r:ap2/pairing/hap.py:521-530]. Open question C-7.6 / probe P6.
    /// </summary>
    public bool AllowPairSetupWithAuth { get; init; }

    /// <summary>Largest number of controllers the registry accepts; M5 beyond it answers <c>MaxPeers</c> (HAP 5.6.6.1 step 6 [ap2r:ap2/pairing/hap.py:1111-1117]).</summary>
    public int MaxControllers { get; init; } = 16;

    /// <summary>Test hook: the 16-byte SRP salt (random when null).</summary>
    internal byte[]? SrpSalt { get; init; }

    /// <summary>Test hook: the SRP private value <c>b</c> (32 random bytes when null).</summary>
    internal byte[]? SrpPrivateKey { get; init; }

    /// <summary>Test hook: the ephemeral X25519 key pair of pair-verify (random when null).</summary>
    internal Func<X25519KeyPair>? EphemeralKeyFactory { get; init; }
}

/// <summary>What the accessory wants sent back for one pairing POST.</summary>
/// <param name="Status">HTTP status (200, or 470 for a refused transient M1).</param>
/// <param name="Body">The TLV8 response body (empty on 470).</param>
/// <param name="Session">Non-null when this response completes a procedure: the shared secret the channel keys derive from. Encryption starts on the <b>next</b> message (SPEC-control.md 2.5).</param>
public sealed record HapAccessoryResponse(int Status, byte[] Body, PairingSession? Session);

/// <summary>
/// The accessory (receiver) side of the three HAP procedures of SPEC-control.md 3: transient pair-setup M1-M4, regular
/// pair-setup M1-M6 (SRP server with a configurable PIN, Ed25519 identity, controller registry), and pair-verify M1-M4.
/// Owned by the <c>pairing</c> package together with <see cref="HapPairingClient"/> so that client and accessory are
/// tested against each other; <c>Airplay2.FakeReceiver</c> (InternalsVisibleTo) wraps it in its pairing handlers and
/// adds nothing of its own. One instance per receiver; one procedure state at a time per connection: use
/// <see cref="ForConnection"/> to get a per-connection view that shares <see cref="Options"/> and <see cref="KnownControllers"/>.
/// </summary>
/// <remarks>
/// Response conventions follow pair_ap's server side as shairport-sync uses it: a malformed request, a missing item or a
/// message out of sequence is answered with HTTP 470 and no body [sps:rtsp.c:1716-1722, 1801-1807]; an authentication
/// failure (wrong PIN, bad signature, unknown controller) with 200 and <c>State</c> + <c>Error = 2</c>
/// [sps:pair_ap/pair_homekit.c:1992-2013 (<c>server_auth_failed_response</c>)].
/// </remarks>
internal sealed class HapAccessoryPairing
{
    private const int SaltSize = 16;

    private readonly Lock _gate = new();

    // pair-setup state of this connection
    private SetupStep _setupStep;
    private SrpServer? _srp;
    private bool _transient;
    private byte[]? _setupSessionKey;

    // pair-verify state of this connection
    private bool _verifyAwaitingM3;
    private byte[]? _verifyShared;
    private byte[]? _controllerEphemeral;
    private byte[]? _accessoryEphemeral;

    /// <summary>Creates an accessory with the given identity and PIN.</summary>
    public HapAccessoryPairing(HapAccessoryOptions options)
        : this(options, new HapControllerRegistry())
    {
    }

    /// <summary>
    /// Creates an accessory whose paired controllers live in <paramref name="knownControllers"/>: the receiver seeds it
    /// from its controller store and persists changes through the registry's change callback (SPEC-receiver.md 4.2).
    /// </summary>
    public HapAccessoryPairing(HapAccessoryOptions options, HapControllerRegistry knownControllers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(knownControllers);
        ArgumentNullException.ThrowIfNull(options.Identity);
        ArgumentException.ThrowIfNullOrEmpty(options.Pin);
        Options = options;
        KnownControllers = knownControllers;
    }

    private enum SetupStep
    {
        Idle,
        AwaitM3,
        AwaitM5,
    }

    /// <summary>The options.</summary>
    public HapAccessoryOptions Options { get; }

    /// <summary>
    /// Controllers that completed regular pair-setup: pairing identifier to Ed25519 LTPK; pair-verify M3 is checked against it.
    /// Shared by every <see cref="ForConnection"/> view; thread-safe on its own.
    /// </summary>
    public HapControllerRegistry KnownControllers { get; }

    /// <summary>The accessory's pairing identifier as sent in M6 and pair-verify M2.</summary>
    public string AccessoryId => Options.Identity.Id;

    /// <summary>The accessory's Ed25519 long-term public key (what a controller stores from M6).</summary>
    public byte[] AccessoryPublicKey => Options.Identity.KeyPair.PublicKey;

    /// <summary>
    /// A fresh per-connection procedure state for the same receiver: same options and the same controller registry, so a
    /// controller paired on one connection can pair-verify on the next (pyatv pairs and verifies on separate connections
    /// [pyatv:pyatv/protocols/airplay/pairing.py:522-526]).
    /// </summary>
    public HapAccessoryPairing ForConnection() => new(Options, KnownControllers);

    /// <summary>
    /// Dispatches a pairing POST by path: <c>/pair-pin-start</c>, <c>/pair-setup</c>, <c>/pair-verify</c>; a body that is
    /// not valid TLV8 is answered with 470, an unknown path with 404.
    /// </summary>
    public HapAccessoryResponse HandleRequest(string path, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path == HapPairingEndpoints.PinStart)
        {
            return HandlePinStart();
        }

        if (path is not (HapPairingEndpoints.PairSetup or HapPairingEndpoints.PairVerify))
        {
            return new HapAccessoryResponse(RtspStatus.NotFound, [], null);
        }

        Tlv8Document request;
        try
        {
            request = Tlv8Document.Decode(body);
        }
        catch (FormatException)
        {
            return Refuse();
        }

        return path == HapPairingEndpoints.PairSetup ? HandlePairSetup(request) : HandlePairVerify(request);
    }

    /// <summary><c>POST /pair-pin-start</c>: 200 with an empty body [sps:rtsp.c:1751-1771; ap2r:ap2-receiver.py:987-994].</summary>
    public HapAccessoryResponse HandlePinStart() => new(RtspStatus.Ok, [], null);

    /// <summary><c>POST /pair-setup</c>: handles M1, M3 and M5 and returns M2, M4 or M6 (or a TLV <see cref="TlvError"/> item).</summary>
    public HapAccessoryResponse HandlePairSetup(Tlv8Document request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return request.State switch
            {
                1 => SetupM1(request),
                3 => SetupM3(request),
                5 => SetupM5(request),
                _ => ResetSetupAndRefuse(),
            };
        }
    }

    /// <summary><c>POST /pair-verify</c>: handles M1 and M3 and returns M2 or M4.</summary>
    public HapAccessoryResponse HandlePairVerify(Tlv8Document request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            return request.State switch
            {
                1 => VerifyM1(request),
                3 => VerifyM3(request),
                _ => ResetVerifyAndRefuse(),
            };
        }
    }

    /// <summary>The keys the accessory uses on the control channel after a completed procedure (the sender's keys swapped).</summary>
    public static HapChannelKeys ControlKeysFor(PairingSession session) => session.ControlKeys.Swapped();

    /// <summary>The keys the accessory uses on the event channel (the sender's event keys swapped, SPEC-control.md 4.3).</summary>
    public static HapChannelKeys EventKeysFor(PairingSession session) => session.EventKeys.Swapped();

    private HapAccessoryResponse SetupM1(Tlv8Document request)
    {
        ResetSetup();

        // Method must be PairSetup (0); pair_ap refuses anything else [sps:pair_ap/pair_homekit.c:2074-2078].
        var method = request.Get(TlvType.Method);
        var methodOk = method is [(byte)TlvMethod.PairSetup] || (Options.AllowPairSetupWithAuth && method is [(byte)TlvMethod.PairSetupWithAuth]);
        if (!methodOk)
        {
            return Refuse();
        }

        // Flags: one byte 0x10 from pyatv and pair_ap; airplay2-receiver reads it as a big-endian integer [ap2r:ap2/pairing/hap.py:513-516].
        ulong flags = 0;
        if (request.Get(TlvType.Flags) is { Length: > 0 and <= 8 } flagBytes)
        {
            foreach (var b in flagBytes)
            {
                flags = (flags << 8) | b;
            }
        }

        var transient = (flags & (ulong)TlvFlags.Transient) != 0;
        if (transient && !Options.AllowTransient)
        {
            // "HTTP 470 on transient M1 → PIN flow" (SPEC-control.md 3.9).
            return Refuse();
        }

        var salt = Options.SrpSalt ?? RandomSalt();
        var password = transient ? Srp6a.TransientPin : Options.Pin;
        _srp = Options.SrpPrivateKey is { } b2
            ? new SrpServer(Srp6a.PairSetupUsername, password, salt, b2)
            : new SrpServer(Srp6a.PairSetupUsername, password, salt);
        _transient = transient;
        _setupStep = SetupStep.AwaitM3;

        // M2: State, Salt (16), PublicKey (B, fragmented above 255 bytes) [sps:pair_ap/pair_homekit.c:2271-2307].
        var m2 = new Tlv8Document()
            .AddState(2)
            .Add(TlvType.Salt, salt)
            .Add(TlvType.PublicKey, _srp.PublicKey);
        return Ok(m2);
    }

    private HapAccessoryResponse SetupM3(Tlv8Document request)
    {
        if (_setupStep != SetupStep.AwaitM3 || _srp is null)
        {
            return ResetSetupAndRefuse();
        }

        // A at most 384 bytes, proof exactly 64 [sps:pair_ap/pair_homekit.c:2122-2132].
        var a = request.Get(TlvType.PublicKey);
        var proof = request.Get(TlvType.Proof);
        if (a is null || a.Length is 0 or > Srp6a.GroupSize || proof is not { Length: Srp6a.HashSize })
        {
            return ResetSetupAndRefuse();
        }

        var result = _srp.ProcessClientProof(a, proof);
        if (result is not { } ok)
        {
            // Wrong PIN: State 4 + Error Authentication [sps:pair_ap/pair_homekit.c:2150-2155, 2320-2321].
            ResetSetup();
            return AuthenticationFailed(4);
        }

        var m4 = new Tlv8Document().AddState(4).Add(TlvType.Proof, ok.ServerProof);
        if (_transient)
        {
            // Transient: done after M4; SS = K (64 bytes) [sps:pair_ap/pair_homekit.c:2336-2350; sps:rtsp.c:1810-1833].
            ResetSetup();
            return new HapAccessoryResponse(RtspStatus.Ok, m4.Encode(), new PairingSession(PairingKind.Transient, ok.SessionKey));
        }

        _setupSessionKey = ok.SessionKey;
        _setupStep = SetupStep.AwaitM5;
        return Ok(m4);
    }

    private HapAccessoryResponse SetupM5(Tlv8Document request)
    {
        if (_setupStep != SetupStep.AwaitM5 || _setupSessionKey is not { } sessionKey)
        {
            return ResetSetupAndRefuse();
        }

        ResetSetup();
        var encrypted = request.Get(TlvType.EncryptedData);
        if (encrypted is null || encrypted.Length < ChaCha20Poly1305Aead.TagSize)
        {
            return Refuse();
        }

        var encryptKey = HapKdf.Derive(sessionKey, HapKeyInfo.PairSetupEncryptSalt, HapKeyInfo.PairSetupEncryptInfo);
        var plain = ChaCha20Poly1305Aead.Decrypt(encryptKey, ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairSetupM5Nonce), encrypted, []);
        if (plain is null)
        {
            // HAP 5.6.6.1: a decryption failure is answered with Error Authentication [ap2r:ap2/pairing/hap.py:1040-1058].
            return AuthenticationFailed(6);
        }

        Tlv8Document sub;
        try
        {
            sub = Tlv8Document.Decode(plain);
        }
        catch (FormatException)
        {
            return Refuse();
        }

        var controllerId = sub.Get(TlvType.Identifier);
        var controllerKey = sub.Get(TlvType.PublicKey);
        var signature = sub.Get(TlvType.Signature);
        if (controllerId is not { Length: > 0 } || controllerKey is not { Length: Ed25519KeyPair.KeySize } || signature is not { Length: Ed25519KeyPair.SignatureSize })
        {
            return Refuse();
        }

        // iOSDeviceInfo = iOSDeviceX || id || LTPK, Ed25519-verified with the LTPK [sps:pair_ap/pair_homekit.c:2246-2254; ap2r:ap2/pairing/hap.py:1061-1104].
        var deviceX = HapKdf.Derive(sessionKey, HapKeyInfo.PairSetupControllerSignSalt, HapKeyInfo.PairSetupControllerSignInfo);
        if (!Ed25519KeyPair.Verify(controllerKey, HapPairingClient.Concat(deviceX, controllerId, controllerKey), signature))
        {
            return AuthenticationFailed(6);
        }

        if (!KnownControllers.TryAddOrUpdate(Encoding.UTF8.GetString(controllerId), controllerKey, Options.MaxControllers))
        {
            return Error(6, TlvError.MaxPeers);
        }

        // M6: EncryptedData(Identifier || PublicKey || Signature(AccessoryX || accessoryId || accessoryLTPK))
        // [sps:pair_ap/pair_homekit.c:2364-2460; ap2r:ap2/pairing/hap.py:1119-1174].
        var identity = Options.Identity.KeyPair;
        var accessoryId = Encoding.UTF8.GetBytes(AccessoryId);
        var accessoryX = HapKdf.Derive(sessionKey, HapKeyInfo.PairSetupAccessorySignSalt, HapKeyInfo.PairSetupAccessorySignInfo);
        var accessorySignature = identity.Sign(HapPairingClient.Concat(accessoryX, accessoryId, identity.PublicKey));
        var m6Sub = new Tlv8Document()
            .Add(TlvType.Identifier, accessoryId)
            .Add(TlvType.PublicKey, identity.PublicKey)
            .Add(TlvType.Signature, accessorySignature);
        var m6Encrypted = ChaCha20Poly1305Aead.Encrypt(encryptKey, ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairSetupM6Nonce), m6Sub.Encode(), []);

        // Regular pair-setup does not start encryption; pair-verify follows (SPEC-control.md 3.6 step 8).
        return Ok(new Tlv8Document().AddState(6).Add(TlvType.EncryptedData, m6Encrypted));
    }

    private HapAccessoryResponse VerifyM1(Tlv8Document request)
    {
        ResetVerify();
        var controllerEphemeral = request.Get(TlvType.PublicKey);
        if (controllerEphemeral is not { Length: X25519KeyPair.KeySize })
        {
            return Refuse();
        }

        var ephemeral = Options.EphemeralKeyFactory?.Invoke() ?? X25519KeyPair.Generate();
        byte[] shared;
        try
        {
            shared = ephemeral.Agree(controllerEphemeral);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Refuse();
        }

        // M2: PublicKey(E_acc), EncryptedData(Identifier || Signature(E_acc || accessoryId || E_ctrl))
        // [sps:pair_ap/pair_homekit.c:2628-2706; ap2r:ap2/pairing/hap.py:1176-1249].
        var accessoryId = Encoding.UTF8.GetBytes(AccessoryId);
        var signature = Options.Identity.KeyPair.Sign(HapPairingClient.Concat(ephemeral.PublicKey, accessoryId, controllerEphemeral));
        var sub = new Tlv8Document()
            .Add(TlvType.Identifier, accessoryId)
            .Add(TlvType.Signature, signature);
        var verifyKey = HapKdf.Derive(shared, HapKeyInfo.PairVerifyEncryptSalt, HapKeyInfo.PairVerifyEncryptInfo);
        var encrypted = ChaCha20Poly1305Aead.Encrypt(verifyKey, ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairVerifyM2Nonce), sub.Encode(), []);

        _verifyShared = shared;
        _controllerEphemeral = controllerEphemeral;
        _accessoryEphemeral = ephemeral.PublicKey;
        _verifyAwaitingM3 = true;

        return Ok(new Tlv8Document()
            .AddState(2)
            .Add(TlvType.PublicKey, ephemeral.PublicKey)
            .Add(TlvType.EncryptedData, encrypted));
    }

    private HapAccessoryResponse VerifyM3(Tlv8Document request)
    {
        if (!_verifyAwaitingM3 || _verifyShared is not { } shared || _controllerEphemeral is not { } controllerEphemeral || _accessoryEphemeral is not { } accessoryEphemeral)
        {
            return ResetVerifyAndRefuse();
        }

        ResetVerify();
        var encrypted = request.Get(TlvType.EncryptedData);
        if (encrypted is null || encrypted.Length < ChaCha20Poly1305Aead.TagSize)
        {
            return Refuse();
        }

        var verifyKey = HapKdf.Derive(shared, HapKeyInfo.PairVerifyEncryptSalt, HapKeyInfo.PairVerifyEncryptInfo);
        var plain = ChaCha20Poly1305Aead.Decrypt(verifyKey, ChaCha20Poly1305Aead.NonceFromLabel(HapKeyInfo.PairVerifyM3Nonce), encrypted, []);
        if (plain is null)
        {
            return AuthenticationFailed(4);
        }

        Tlv8Document sub;
        try
        {
            sub = Tlv8Document.Decode(plain);
        }
        catch (FormatException)
        {
            return Refuse();
        }

        var controllerId = sub.Get(TlvType.Identifier);
        var signature = sub.Get(TlvType.Signature);
        if (controllerId is not { Length: > 0 } || signature is not { Length: Ed25519KeyPair.SignatureSize })
        {
            return Refuse();
        }

        // Unknown controller or bad signature: State 4 + Error Authentication [ap2r:ap2/pairing/hap.py:1251-1343;
        // sps:pair_ap/pair_homekit.c:2598-2613].
        if (!KnownControllers.TryGet(Encoding.UTF8.GetString(controllerId), out var controllerKey)
            || !Ed25519KeyPair.Verify(controllerKey, HapPairingClient.Concat(controllerEphemeral, controllerId, accessoryEphemeral), signature))
        {
            return AuthenticationFailed(4);
        }

        // M4: State only; SS = the X25519 secret (32 bytes) [sps:pair_ap/pair_homekit.c:2708-2745].
        return new HapAccessoryResponse(RtspStatus.Ok, new Tlv8Document().AddState(4).Encode(), new PairingSession(PairingKind.Verified, shared));
    }

    private void ResetSetup()
    {
        _setupStep = SetupStep.Idle;
        _srp = null;
        _transient = false;
        _setupSessionKey = null;
    }

    private void ResetVerify()
    {
        _verifyAwaitingM3 = false;
        _verifyShared = null;
        _controllerEphemeral = null;
        _accessoryEphemeral = null;
    }

    private HapAccessoryResponse ResetSetupAndRefuse()
    {
        ResetSetup();
        return Refuse();
    }

    private HapAccessoryResponse ResetVerifyAndRefuse()
    {
        ResetVerify();
        return Refuse();
    }

    private static byte[] RandomSalt()
    {
        var salt = new byte[SaltSize];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        return salt;
    }

    private static HapAccessoryResponse Ok(Tlv8Document document) => new(RtspStatus.Ok, document.Encode(), null);

    private static HapAccessoryResponse Refuse() => new(RtspStatus.ConnectionAuthorizationRequired, [], null);

    private static HapAccessoryResponse AuthenticationFailed(byte state) => Error(state, TlvError.Authentication);

    private static HapAccessoryResponse Error(byte state, TlvError error) =>
        Ok(new Tlv8Document().AddState(state).Add(TlvType.Error, (byte)error));
}

/// <summary>
/// The accessory's registry of paired controllers (pairing identifier to Ed25519 LTPK), shared by every per-connection
/// view of one <see cref="HapAccessoryPairing"/>. All members are thread-safe; keys are copied in and out so callers
/// cannot alter a stored key.
/// </summary>
internal sealed class HapControllerRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, byte[]> _controllers = new(StringComparer.Ordinal);
    private readonly Action<string, byte[]?>? _onChanged;

    /// <summary>Creates an empty registry.</summary>
    /// <param name="onChanged">
    /// Called after a controller was added or replaced (with its new key) or removed (with null), outside the registry's
    /// lock; the receiver writes the change through to its controller store (SPEC-receiver.md 4.2).
    /// </param>
    public HapControllerRegistry(Action<string, byte[]?>? onChanged = null)
    {
        _onChanged = onChanged;
    }

    /// <summary>The number of paired controllers.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _controllers.Count;
            }
        }
    }

    /// <summary>Looks up a controller's long-term public key.</summary>
    public bool TryGet(string controllerId, [NotNullWhen(true)] out byte[]? publicKey)
    {
        ArgumentNullException.ThrowIfNull(controllerId);
        lock (_lock)
        {
            if (_controllers.TryGetValue(controllerId, out var stored))
            {
                publicKey = stored.ToArray();
                return true;
            }
        }

        publicKey = null;
        return false;
    }

    /// <summary>
    /// Adds or replaces a controller unless that would grow the registry beyond <paramref name="maxControllers"/>
    /// (a known controller can always re-pair); false when the registry is full (HAP <c>MaxPeers</c>).
    /// </summary>
    public bool TryAddOrUpdate(string controllerId, ReadOnlySpan<byte> publicKey, int maxControllers)
    {
        ArgumentNullException.ThrowIfNull(controllerId);
        lock (_lock)
        {
            if (!_controllers.ContainsKey(controllerId) && _controllers.Count >= maxControllers)
            {
                return false;
            }

            _controllers[controllerId] = publicKey.ToArray();
        }

        _onChanged?.Invoke(controllerId, publicKey.ToArray());
        return true;
    }

    /// <summary>Adds or replaces a controller regardless of the limit (seeding a receiver in tests).</summary>
    public void Set(string controllerId, ReadOnlySpan<byte> publicKey) => TryAddOrUpdate(controllerId, publicKey, int.MaxValue);

    /// <summary>Removes a controller; false when it was not paired.</summary>
    public bool Remove(string controllerId)
    {
        ArgumentNullException.ThrowIfNull(controllerId);
        bool removed;
        lock (_lock)
        {
            removed = _controllers.Remove(controllerId);
        }

        if (removed)
        {
            _onChanged?.Invoke(controllerId, null);
        }

        return removed;
    }
}
