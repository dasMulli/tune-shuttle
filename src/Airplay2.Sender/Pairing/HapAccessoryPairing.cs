using Airplay2.Sender.Crypto;
using Airplay2.Sender.Tlv8;

namespace Airplay2.Sender.Pairing;

/// <summary>Options of the accessory side.</summary>
/// <param name="Identity">The accessory's pairing identifier (TXT <c>pi</c>) and long-term Ed25519 key (<c>pk</c>).</param>
/// <param name="Pin">The PIN for regular pair-setup (the fake receiver "shows" it); transient pair-setup always uses 3939.</param>
/// <param name="AllowTransient">Answer transient M1 with M2 (true) or with HTTP 470 (false, SPEC-control.md 3.9).</param>
public sealed record HapAccessoryOptions(ControllerIdentity Identity, string Pin, bool AllowTransient = true);

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
/// adds nothing of its own. One instance per receiver; one procedure state at a time per connection.
/// </summary>
internal sealed class HapAccessoryPairing
{
    /// <summary>Creates an accessory with the given identity and PIN.</summary>
    public HapAccessoryPairing(HapAccessoryOptions options)
    {
        Options = options;
    }

    /// <summary>The options.</summary>
    public HapAccessoryOptions Options { get; }

    /// <summary>Controllers that completed regular pair-setup: pairing identifier to Ed25519 LTPK; pair-verify M3 is checked against it.</summary>
    public Dictionary<string, byte[]> KnownControllers { get; } = new(StringComparer.Ordinal);

    /// <summary><c>POST /pair-setup</c>: handles M1, M3 and M5 and returns M2, M4 or M6 (or a TLV <see cref="TlvError"/> item).</summary>
    public HapAccessoryResponse HandlePairSetup(Tlv8Document request) => throw new NotImplementedException("TODO(impl: pairing)");

    /// <summary><c>POST /pair-verify</c>: handles M1 and M3 and returns M2 or M4.</summary>
    public HapAccessoryResponse HandlePairVerify(Tlv8Document request) => throw new NotImplementedException("TODO(impl: pairing)");

    /// <summary>The keys the accessory uses on the control channel after a completed procedure (the sender's keys swapped).</summary>
    public static HapChannelKeys ControlKeysFor(PairingSession session) => session.ControlKeys.Swapped();
}
