using Airplay2.Receiver.Pairing;
using Bridge.Service.Store;

namespace Bridge.Service.Airplay;

/// <summary>
/// The receiver's <see cref="IControllerStore"/> over the bridge store (SPEC-receiver.md 4.2, 8.4): registered senders
/// (HAP pairing identifier and Ed25519 LTPK) live in the <c>airplay_controllers</c> table, so password-mode pair-verify
/// survives restarts.
/// </summary>
public sealed class BridgeControllerStore(IBridgeStore store) : IControllerStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await store.GetAirplayControllersAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.ControllerId, r => r.PublicKey, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public Task SaveAsync(string controllerId, byte[] publicKey, CancellationToken cancellationToken) =>
        store.SaveAirplayControllerAsync(controllerId, publicKey, cancellationToken);

    /// <inheritdoc />
    public Task RemoveAsync(string controllerId, CancellationToken cancellationToken) =>
        store.RemoveAirplayControllerAsync(controllerId, cancellationToken);
}
