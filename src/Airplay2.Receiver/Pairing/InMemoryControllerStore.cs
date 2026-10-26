using System.Collections.Concurrent;

namespace Airplay2.Receiver.Pairing;

/// <summary>
/// An <see cref="IControllerStore"/> that keeps the controllers in memory (SPEC-receiver.md 4.2): for tests, the fake
/// receiver and a receiver without persistence. Thread-safe; every operation completes synchronously; keys are copied
/// in and out.
/// </summary>
public sealed class InMemoryControllerStore : IControllerStore
{
    private readonly ConcurrentDictionary<string, byte[]> _controllers = new(StringComparer.Ordinal);

    /// <summary>The number of stored controllers.</summary>
    public int Count => _controllers.Count;

    /// <summary>True when <paramref name="controllerId"/> is stored.</summary>
    public bool Contains(string controllerId) => _controllers.ContainsKey(controllerId);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, byte[]> snapshot = _controllers.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public Task SaveAsync(string controllerId, byte[] publicKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controllerId);
        ArgumentNullException.ThrowIfNull(publicKey);
        cancellationToken.ThrowIfCancellationRequested();
        _controllers[controllerId] = publicKey.ToArray();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string controllerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controllerId);
        cancellationToken.ThrowIfCancellationRequested();
        _controllers.TryRemove(controllerId, out _);
        return Task.CompletedTask;
    }
}
