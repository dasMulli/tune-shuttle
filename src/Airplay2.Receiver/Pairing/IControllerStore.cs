namespace Airplay2.Receiver.Pairing;

/// <summary>
/// Where the receiver keeps the controllers that completed regular pair-setup (pairing identifier and Ed25519 long-term
/// public key), so password-mode pair-verify keeps working across restarts (SPEC-receiver.md R7, 4.2). The bridge backs
/// it with the <c>airplay_controllers</c> table (SPEC-receiver.md 8.4); tests and the fake receiver use
/// <see cref="InMemoryControllerStore"/>.
/// </summary>
public interface IControllerStore
{
    /// <summary>Every stored controller: pairing identifier to its 32-byte Ed25519 long-term public key.</summary>
    Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Adds or replaces a controller.</summary>
    Task SaveAsync(string controllerId, byte[] publicKey, CancellationToken cancellationToken);

    /// <summary>Removes a controller; removing an unknown one is not an error.</summary>
    Task RemoveAsync(string controllerId, CancellationToken cancellationToken);
}
