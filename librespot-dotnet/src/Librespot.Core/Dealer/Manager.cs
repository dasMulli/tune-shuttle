// Port of core/src/dealer/manager.rs (contract). The websocket dealer itself (dealer/mod.rs -> Dealer.cs), the
// JSON protocol (dealer/protocol.rs -> Protocol.cs, protocol/request.rs -> Protocol/Request.cs) and the
// handler/subscriber maps (dealer/maps.rs -> Maps.cs) belong to package `core-dealer`; this file only fixes the
// manager-level contract that connect/spirc codes against.
// TODO(port: core-dealer)
using System.Threading.Channels;

namespace Librespot.Core.Dealer;

/// <summary>Upstream <c>manager::Reply</c>.</summary>
public enum Reply
{
    Success,
    Failure,
    Unanswered,
}

/// <summary>Upstream <c>RequestReply = (Request, mpsc::UnboundedSender&lt;Reply&gt;)</c>.</summary>
public sealed record RequestReply(Request Request, ChannelWriter<Reply> ReplyTo);

/// <summary>
/// Manages the dealer websocket connection and hands out subscriptions and request handlers.
/// Upstream <c>DealerManager</c>. The URL is <c>wss://{host}:{port}/?access_token={token}</c> with host/port
/// from <c>ApResolver.ResolveAsync("dealer")</c> and the token from <c>Session.Login5.AuthTokenAsync()</c>
/// (package core-spclient, forward dependency via contract).
/// </summary>
public sealed class DealerManager : SessionComponent
{
    internal DealerManager(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>start()</c>: launches the websocket in the background (reconnects with backoff).</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-dealer)");

    /// <summary>
    /// Upstream <c>add_listen_for(url)</c>: subscribes to the uri (on the builder before <see cref="StartAsync"/>,
    /// on the live dealer afterwards) and returns the raw message stream (upstream <c>Subscription</c>).
    /// </summary>
    public IAsyncEnumerable<Message> AddListenFor(string url) =>
        throw new NotImplementedException("TODO(port: core-dealer)");

    /// <summary>
    /// Upstream <c>listen_for(uri, parse)</c>: <see cref="AddListenFor"/> mapped through <paramref name="parse"/>
    /// (spirc passes <see cref="Message.FromRaw{M}"/>, <see cref="Message.TryFromJson{M}"/> or its own
    /// <c>extract_connection_id</c>). A parse failure is yielded as the exception element, not thrown from the
    /// enumerator, so one bad message does not end the subscription (upstream <c>Stream&lt;Result&lt;T&gt;&gt;</c>).
    /// </summary>
    public IAsyncEnumerable<Result<T>> ListenFor<T>(string uri, Func<Message, T> parse) =>
        throw new NotImplementedException("TODO(port: core-dealer)");

    /// <summary>Upstream <c>add_handle_for(url)</c>: registers a request handler and returns its receiver.</summary>
    public ChannelReader<RequestReply> AddHandleFor(string url) =>
        throw new NotImplementedException("TODO(port: core-dealer)");

    /// <summary>Upstream <c>handle_for(uri)</c>: same as <see cref="AddHandleFor"/> (upstream only wraps it in a stream).</summary>
    public ChannelReader<RequestReply> HandleFor(string uri) =>
        throw new NotImplementedException("TODO(port: core-dealer)");

    /// <summary>Upstream <c>handles(uri)</c>.</summary>
    public bool Handles(string uri) =>
        throw new NotImplementedException("TODO(port: core-dealer)");

    /// <summary>Upstream <c>close()</c>: shuts the websocket down and awaits the background tasks.</summary>
    public Task CloseAsync() =>
        throw new NotImplementedException("TODO(port: core-dealer)");
}

/// <summary>
/// Upstream <c>Result&lt;T, Error&gt;</c> as a stream element (see <see cref="DealerManager.ListenFor{T}"/>):
/// exactly one of <see cref="Value"/> / <see cref="Error"/> is set.
/// </summary>
public readonly record struct Result<T>(T? Value, LibrespotException? Error)
{
    public bool IsOk => Error is null;

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Err(LibrespotException error) => new(default, error);

    /// <summary>Returns the value or throws the error (upstream <c>?</c>).</summary>
    public T Unwrap() => Error is null ? Value! : throw Error;
}
