using Airplay2.Sender.Crypto;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Pairing;

/// <summary>The sender's long-term HomeKit controller identity: one per installation (SPEC-control.md 3.6).</summary>
/// <param name="Id">Pairing identifier sent as TLV <c>Identifier</c> (pyatv: a UUID string).</param>
/// <param name="Ed25519Seed">32-byte Ed25519 seed (LTSK).</param>
public sealed record ControllerIdentity(string Id, byte[] Ed25519Seed)
{
    /// <summary>Generates a fresh identity.</summary>
    public static ControllerIdentity Generate() => new(Guid.NewGuid().ToString().ToUpperInvariant(), Ed25519KeyPair.Generate().Seed.ToArray());

    /// <summary>The key pair.</summary>
    public Ed25519KeyPair KeyPair => Ed25519KeyPair.FromSeed(Ed25519Seed);
}

/// <summary>A persisted regular (PIN/password) pairing with one receiver (SPEC-control.md 3.8, <c>pairings</c> table).</summary>
/// <param name="Controller">Our identity used for this pairing.</param>
/// <param name="AccessoryId">The receiver's pairing identifier from M6.</param>
/// <param name="AccessoryPublicKey">The receiver's Ed25519 LTPK from M6 (32 bytes).</param>
public sealed record PairingCredentials(ControllerIdentity Controller, string AccessoryId, byte[] AccessoryPublicKey);

/// <summary>Which procedure produced a session.</summary>
public enum PairingKind
{
    /// <summary>Transient pair-setup; shared secret = 64-byte SRP session key.</summary>
    Transient,

    /// <summary>Pair-verify with persisted credentials; shared secret = 32-byte X25519 secret.</summary>
    Verified,
}

/// <summary>The result of pairing on one connection: the shared secret all channel keys derive from.</summary>
public sealed record PairingSession(PairingKind Kind, byte[] SharedSecret)
{
    /// <summary>Control-channel keys (SPEC-control.md 2.5).</summary>
    public HapChannelKeys ControlKeys => HapChannelKeys.ForControl(SharedSecret);

    /// <summary>Event-channel keys (SPEC-control.md 4.3).</summary>
    public HapChannelKeys EventKeys => HapChannelKeys.ForEvents(SharedSecret);

    /// <summary>Default <c>shk</c>: the first 32 bytes of the shared secret (SPEC.md decision D4).</summary>
    public byte[] DefaultStreamKey => SharedSecret.AsSpan(0, 32).ToArray();
}

/// <summary>Why pairing failed.</summary>
public enum PairingFailure
{
    /// <summary>HTTP 470 or TLV Error on transient M1: the receiver wants the PIN flow.</summary>
    TransientRefused,

    /// <summary>Wrong PIN / password (TLV Error 2 on M4).</summary>
    Authentication,

    /// <summary>TLV BackOff / MaxTries / Busy: retry later.</summary>
    Retryable,

    /// <summary>Pair-verify rejected: persisted credentials are stale; delete and re-pair.</summary>
    CredentialsRejected,

    /// <summary>Proof mismatch, bad signature or malformed TLV.</summary>
    Protocol,
}

/// <summary>Thrown by <see cref="IPairingClient"/>.</summary>
public sealed class PairingException(PairingFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The failure class.</summary>
    public PairingFailure Failure { get; } = failure;

    /// <summary>The TLV error code when the receiver sent one.</summary>
    public TlvError? TlvError { get; init; }
}

/// <summary>State between M2 and M3 of a PIN pairing while the web app asks the user for the PIN.</summary>
public sealed class PinPairingInProgress
{
    internal PinPairingInProgress(IRtspConnection connection, ControllerIdentity controller, byte[] salt, byte[] serverPublicKey)
    {
        Connection = connection;
        Controller = controller;
        Salt = salt;
        ServerPublicKey = serverPublicKey;
    }

    /// <summary>The connection the pairing runs on; must stay open until <see cref="IPairingClient.FinishPinPairingAsync"/>.</summary>
    public IRtspConnection Connection { get; }

    /// <summary>Our identity.</summary>
    public ControllerIdentity Controller { get; }

    /// <summary>SRP salt from M2.</summary>
    public byte[] Salt { get; }

    /// <summary>SRP B from M2.</summary>
    public byte[] ServerPublicKey { get; }
}

/// <summary>HomeKit pairing procedures over an <see cref="IRtspConnection"/> (SPEC-control.md 3).</summary>
public interface IPairingClient
{
    /// <summary>
    /// Transient pair-setup (3.5): <c>POST /pair-pin-start</c> (HKP 4), M1 with Flags 0x10, M3 with PIN 3939, verify M2 proof.
    /// Throws <see cref="PairingException"/> with <see cref="PairingFailure.TransientRefused"/> on 470 / TLV Error in M2.
    /// Does not enable encryption; the caller does that with <see cref="PairingSession.ControlKeys"/> after this returns.
    /// </summary>
    Task<PairingSession> PairTransientAsync(IRtspConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// Regular pair-setup, steps 1-3 (3.6): <c>POST /pair-pin-start</c> (HKP 3; the receiver shows its PIN) and M1/M2.
    /// <paramref name="method"/> selects the M1 method byte (open question C-7.6).
    /// </summary>
    Task<PinPairingInProgress> StartPinPairingAsync(IRtspConnection connection, ControllerIdentity controller, TlvMethod method, CancellationToken cancellationToken);

    /// <summary>
    /// Regular pair-setup, steps 4-7: M3/M4 with the user's PIN (or the receiver password), M5/M6 with our LTPK and signature,
    /// M6 accessory signature verified (enforced; pyatv skips it). Returns credentials to persist. The connection is not encrypted yet.
    /// </summary>
    Task<PairingCredentials> FinishPinPairingAsync(PinPairingInProgress pairing, string pin, CancellationToken cancellationToken);

    /// <summary>
    /// Pair-verify (3.7) with persisted credentials. Throws <see cref="PairingFailure.CredentialsRejected"/> on TLV Error or 470,
    /// after which the caller deletes the stored pairing and shows the PIN flow again.
    /// </summary>
    Task<PairingSession> VerifyAsync(IRtspConnection connection, PairingCredentials credentials, CancellationToken cancellationToken);
}

/// <summary>Persistence of regular pairings and the controller identity; the bridge backs it with SQLite, the CLI with a JSON file.</summary>
public interface IPairingStore
{
    /// <summary>Loads or creates the installation's controller identity.</summary>
    Task<ControllerIdentity> GetControllerIdentityAsync(CancellationToken cancellationToken);

    /// <summary>Loads the pairing for a receiver id, or null.</summary>
    Task<PairingCredentials?> GetAsync(string receiverId, CancellationToken cancellationToken);

    /// <summary>Stores a pairing.</summary>
    Task SaveAsync(string receiverId, PairingCredentials credentials, CancellationToken cancellationToken);

    /// <summary>Deletes a pairing (after a pair-verify failure or on user request).</summary>
    Task DeleteAsync(string receiverId, CancellationToken cancellationToken);
}

/// <summary>Implementation of the three HAP procedures with <see cref="SrpClient"/>, <see cref="X25519KeyPair"/>, <see cref="Ed25519KeyPair"/> and <see cref="Tlv8Document"/>.</summary>
public sealed class HapPairingClient(ILogger<HapPairingClient> logger) : IPairingClient
{
    private ILogger Logger { get; } = logger;

    /// <inheritdoc />
    public Task<PairingSession> PairTransientAsync(IRtspConnection connection, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: pairing)");

    /// <inheritdoc />
    public Task<PinPairingInProgress> StartPinPairingAsync(IRtspConnection connection, ControllerIdentity controller, TlvMethod method, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: pairing)");

    /// <inheritdoc />
    public Task<PairingCredentials> FinishPinPairingAsync(PinPairingInProgress pairing, string pin, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: pairing)");

    /// <inheritdoc />
    public Task<PairingSession> VerifyAsync(IRtspConnection connection, PairingCredentials credentials, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: pairing)");
}
