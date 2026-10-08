// Port of core/src/mercury/mod.rs (MercuryManager; types.rs -> Types.cs, sender.rs -> Sender.cs).
// TODO(port: core-session): 4-byte sequence, pending map (MercuryPending: partial-response reassembly),
// subscriptions Dictionary<string, List<ChannelWriter<MercuryResponse>>>, dispatch of MercuryReq/Sub/Unsub/Event,
// shutdown. Consumed via contract by core-spclient (TokenProvider: GetAsync) and metadata (GetAsync for
// MercuryRequest-backed types).
using System.Threading.Channels;

namespace Librespot.Core.Mercury;

/// <summary>Hermes/Mercury request-response and pub-sub over the AP connection.</summary>
public sealed class MercuryManager : SessionComponent
{
    internal MercuryManager(Session session) : base(session)
    {
    }

    public Task<MercuryResponse> GetAsync(string uri, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-session)");

    public Task<MercuryResponse> SendAsync(string uri, byte[] data, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-session)");

    public Task<MercuryResponse> RequestAsync(MercuryRequest request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-session)");

    /// <summary>Upstream <c>sender(uri)</c>.</summary>
    public MercurySender Sender(string uri) => new(this, uri);

    /// <summary>Upstream <c>subscribe(uri)</c>: SUB request, then a stream of events for the uri.</summary>
    public Task<ChannelReader<MercuryResponse>> SubscribeAsync(string uri, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-session)");

    /// <summary>Upstream <c>listen_for(uri)</c>: a stream of events for the uri without sending SUB.</summary>
    public ChannelReader<MercuryResponse> ListenFor(string uri) =>
        throw new NotImplementedException("TODO(port: core-session)");

    internal void Dispatch(PacketType cmd, ReadOnlyMemory<byte> data) =>
        throw new NotImplementedException("TODO(port: core-session)");

    /// <summary>Fails all pending requests and closes subscriptions (called from <see cref="Session.Shutdown"/>).</summary>
    public void Shutdown()
    {
        // TODO(port: core-session): no-op until the manager is ported so Session.Shutdown works.
    }
}
