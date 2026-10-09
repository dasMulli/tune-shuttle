// Port of core/src/mercury/sender.rs.
namespace Librespot.Core.Mercury;

/// <summary>
/// Upstream <c>MercurySender</c>: sends repeated SEND requests to one uri and keeps the pending responses, so the
/// caller can <see cref="FlushAsync"/> them later.
/// </summary>
/// <remarks>Not thread-safe (upstream methods take <c>&amp;mut self</c>). <see cref="Clone"/> starts with no pending responses.</remarks>
public sealed class MercurySender
{
    private readonly Queue<Task<MercuryResponse>> _pending = new();
    private Task<MercuryResponse>? _bufferedFuture;

    internal MercurySender(MercuryManager mercury, string uri)
    {
        Mercury = mercury;
        Uri = uri;
    }

    internal MercuryManager Mercury { get; }

    /// <summary>The uri every request is sent to.</summary>
    public string Uri { get; }

    /// <summary>Upstream <c>is_flushed()</c>: true when no response is pending.</summary>
    public bool IsFlushed => _bufferedFuture is null && _pending.Count == 0;

    /// <summary>Upstream <c>send(item)</c>: issues a SEND request now and queues its pending response.</summary>
    /// <exception cref="LibrespotException">The session is not connected.</exception>
    public void Send(byte[] item)
    {
        var task = Mercury.Send(Uri, item);
        _pending.Enqueue(task);
    }

    /// <summary>Upstream <c>flush()</c>: awaits every pending response in order; the first failure is thrown.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_bufferedFuture is null)
        {
            _bufferedFuture = PopFront();
        }

        while (_bufferedFuture is { } future)
        {
            await future.WaitAsync(cancellationToken).ConfigureAwait(false);
            _bufferedFuture = PopFront();
        }
    }

    /// <summary>Flushes (kept from the skeleton's Sink-shaped contract; same as <see cref="FlushAsync"/>).</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default) => FlushAsync(cancellationToken);

    /// <summary>Upstream <c>impl Clone</c>: same manager and uri, no pending responses.</summary>
    public MercurySender Clone() => new(Mercury, Uri);

    private Task<MercuryResponse>? PopFront() => _pending.TryDequeue(out var next) ? next : null;
}
