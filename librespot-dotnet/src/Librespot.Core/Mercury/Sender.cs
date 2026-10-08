// Port of core/src/mercury/sender.rs (contract).
// TODO(port: core-session): upstream MercurySender is a futures::Sink<Vec<u8>> that issues one SEND request per
// item and keeps the pending response futures (poll_ready / start_send / poll_flush / poll_close). The port
// exposes the same operations as async methods.
namespace Librespot.Core.Mercury;

/// <summary>Upstream <c>MercurySender</c>: sends repeated SEND requests to one uri, tracking the pending responses.</summary>
public sealed class MercurySender
{
    internal MercurySender(MercuryManager mercury, string uri)
    {
        Mercury = mercury;
        Uri = uri;
    }

    internal MercuryManager Mercury { get; }

    public string Uri { get; }

    /// <summary>Upstream <c>start_send(item)</c>: issues a SEND and queues the pending response.</summary>
    public void Send(byte[] item) => throw new NotImplementedException("TODO(port: core-session)");

    /// <summary>Upstream <c>poll_flush</c>: awaits every pending response.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException("TODO(port: core-session)");

    /// <summary>Upstream <c>poll_close</c>: flushes.</summary>
    public Task CloseAsync(CancellationToken cancellationToken = default) => FlushAsync(cancellationToken);
}
