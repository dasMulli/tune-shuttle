// Port of core/src/dealer/manager.rs.
using System.Threading.Channels;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core.Dealer;

/// <summary>Upstream <c>manager::Reply</c>: the answer a request handler gives to a dealer request.</summary>
public enum Reply
{
    /// <summary>Reply <c>success: true</c>.</summary>
    Success,

    /// <summary>Reply <c>success: false</c>.</summary>
    Failure,

    /// <summary>Do not reply at all.</summary>
    Unanswered,
}

/// <summary>
/// Upstream <c>RequestReply = (Request, mpsc::UnboundedSender&lt;Reply&gt;)</c>. Write exactly one <see cref="Reply"/>
/// to <see cref="ReplyTo"/>; completing <see cref="ReplyTo"/> without writing (upstream: dropping the sender) replies
/// <see cref="Reply.Failure"/>.
/// </summary>
public sealed record RequestReply(Request Request, ChannelWriter<Reply> ReplyTo);

/// <summary>
/// Upstream <c>Result&lt;T, Error&gt;</c> as a stream element (see <see cref="DealerManager.ListenFor{T}"/>):
/// exactly one of <see cref="Value"/> / <see cref="Error"/> is set.
/// </summary>
public readonly record struct Result<T>(T? Value, LibrespotException? Error)
{
    /// <summary>True when <see cref="Value"/> is set.</summary>
    public bool IsOk => Error is null;

    /// <summary>A successful result.</summary>
    public static Result<T> Ok(T value) => new(value, null);

    /// <summary>A failed result.</summary>
    public static Result<T> Err(LibrespotException error) => new(default, error);

    /// <summary>Returns the value or throws the error (upstream <c>?</c>).</summary>
    public T Unwrap() => Error is null ? Value! : throw Error;
}

/// <summary>Upstream <c>DealerError</c> (kind FailedPrecondition, private upstream).</summary>
internal sealed class DealerException : LibrespotException
{
    public enum Reason
    {
        BuilderNotAvailable,
        LaunchFailure,
        CouldNotSetDealer,
    }

    public Reason Cause { get; }

    private DealerException(Reason cause, string detail, Exception? inner = null)
        : base(ErrorKind.FailedPrecondition, detail, inner)
    {
        Cause = cause;
    }

    public static DealerException BuilderNotAvailable() => new(Reason.BuilderNotAvailable, "Builder wasn't available");

    public static DealerException LaunchFailure(LibrespotException e) =>
        new(Reason.LaunchFailure, $"Websocket couldn't be started because: {e.Message}", e);

    public static DealerException CouldNotSetDealer() => new(Reason.CouldNotSetDealer, "Failed to set dealer");
}

/// <summary>
/// Upstream <c>DealerRequestHandler</c>: forwards each request to a channel as a <see cref="RequestReply"/> and
/// answers the dealer once the consumer replied.
/// </summary>
internal sealed class DealerRequestHandler : IRequestHandler
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<DealerRequestHandler>();

    private readonly Channel<RequestReply> _channel;

    private DealerRequestHandler(Channel<RequestReply> channel)
    {
        _channel = channel;
    }

    /// <summary>Upstream <c>DealerRequestHandler::new()</c>.</summary>
    public static (DealerRequestHandler Handler, ChannelReader<RequestReply> Receiver) Create()
    {
        var channel = Channel.CreateUnbounded<RequestReply>();
        return (new DealerRequestHandler(channel), channel.Reader);
    }

    public void HandleRequest(Request request, Responder responder)
    {
        var reply = Channel.CreateUnbounded<Reply>();

        if (!_channel.Writer.TryWrite(new RequestReply(request, reply.Writer)))
        {
            Log.LogError("failed sending dealer request {Why}", "channel closed");
            responder.Send(new Response(false));
            return;
        }

        _ = ReplyAsync(reply.Reader, responder);
    }

    private static async Task ReplyAsync(ChannelReader<Reply> rx, Responder responder)
    {
        using (responder)
        {
            Reply reply;
            try
            {
                reply = await rx.ReadAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is ChannelClosedException or OperationCanceledException)
            {
                // upstream: `rx.recv().await.unwrap_or(Reply::Failure)` - the sender was dropped (completed; a
                // writer completed with an OperationCanceledException surfaces as that instead)
                reply = Reply.Failure;
            }

            Log.LogDebug("replying to ws request: {Reply}", reply);
            switch (reply)
            {
                case Reply.Unanswered:
                    responder.ForceUnanswered();
                    break;
                default:
                    responder.Send(new Response(reply == Reply.Success));
                    break;
            }
        }
    }

    /// <summary>
    /// The dealer dropped the handler: the request stream ends. Requests still queued are answered with
    /// <see cref="Reply.Failure"/> so their waiting reply tasks finish (deviation: upstream notices a dropped receiver
    /// on the next send; a <see cref="ChannelReader{T}"/> cannot tell that its consumer is gone, so this is the only
    /// point at which unconsumed requests can be released).
    /// </summary>
    public void Release()
    {
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var pending))
        {
            pending.ReplyTo.TryComplete();
        }
    }
}

/// <summary>
/// Manages the dealer websocket connection and hands out subscriptions and request handlers.
/// Upstream <c>DealerManager</c>. The URL is <c>wss://{host}:{port}/?access_token={token}</c> with host/port
/// from <c>ApResolver.ResolveAsync("dealer")</c> and the token from <c>Session.Login5.AuthTokenAsync()</c>.
/// </summary>
public sealed class DealerManager : SessionComponent
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<DealerManager>();

    // upstream DealerManagerInner: builder: OnceLock<Builder> = OnceLock::from(Builder::new()), dealer: OnceLock<Dealer>
    private Builder? _builder = new();
    private Dealer? _dealer;

    internal DealerManager(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>get_url(session)</c>: resolves the dealer and builds the websocket url with a fresh token.</summary>
    private static async Task<Uri> GetUrlAsync(Session session, CancellationToken cancellationToken)
    {
        var (host, port) = await session.ApResolver.ResolveAsync("dealer", cancellationToken).ConfigureAwait(false);
        var token = (await session.Login5.AuthTokenAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
        var url = $"wss://{host}:{port}/?access_token={token}";
        try
        {
            return new Uri(url);
        }
        catch (UriFormatException e)
        {
            throw LibrespotException.From(e);
        }
    }

    /// <summary>
    /// Upstream <c>add_listen_for(url)</c>: subscribes to the uri (on the builder before <see cref="StartAsync"/>,
    /// on the live dealer afterwards) and returns the raw message stream (upstream <c>Subscription</c>). The
    /// subscription is registered immediately; the stream is meant to be enumerated once and ends when the dealer
    /// is closed. Throws when the uri is invalid or while the dealer is being started.
    /// </summary>
    public IAsyncEnumerable<Message> AddListenFor(string url)
    {
        lock (Lock)
        {
            if (_dealer is not null)
            {
                return _dealer.Subscribe(url);
            }

            if (_builder is not null)
            {
                return _builder.Subscribe(url);
            }

            throw DealerException.BuilderNotAvailable();
        }
    }

    /// <summary>
    /// Upstream <c>listen_for(uri, parse)</c>: <see cref="AddListenFor"/> mapped through <paramref name="parse"/>
    /// (spirc passes <see cref="Message.FromRaw{M}"/>, <see cref="Message.TryFromJson{M}"/> or its own
    /// <c>extract_connection_id</c>). A parse failure is yielded as the exception element, not thrown from the
    /// enumerator, so one bad message does not end the subscription (upstream <c>Stream&lt;Result&lt;T&gt;&gt;</c>).
    /// </summary>
    public IAsyncEnumerable<Result<T>> ListenFor<T>(string uri, Func<Message, T> parse) =>
        new MappedStream<T>(AddListenFor(uri), parse);

    /// <summary>
    /// Upstream <c>subscription.map(parse)</c>. Not a compiler-generated iterator: it creates the source enumerator
    /// as soon as it is itself created, so disposing it before the first <c>MoveNextAsync</c> still disposes (and so
    /// unregisters) the underlying subscription.
    /// </summary>
    private sealed class MappedStream<T>(IAsyncEnumerable<Message> source, Func<Message, T> parse)
        : IAsyncEnumerable<Result<T>>
    {
        public IAsyncEnumerator<Result<T>> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(source.GetAsyncEnumerator(cancellationToken), parse);

        private sealed class Enumerator(IAsyncEnumerator<Message> inner, Func<Message, T> parse)
            : IAsyncEnumerator<Result<T>>
        {
            public Result<T> Current { get; private set; }

            public async ValueTask<bool> MoveNextAsync()
            {
                if (!await inner.MoveNextAsync().ConfigureAwait(false))
                {
                    return false;
                }

                try
                {
                    Current = Result<T>.Ok(parse(inner.Current));
                }
                catch (Exception e)
                {
                    Current = Result<T>.Err(LibrespotException.From(e));
                }

                return true;
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// <summary>
    /// Upstream <c>add_handle_for(url)</c>: registers a request handler and returns its receiver. The receiver
    /// completes when the dealer is closed.
    /// </summary>
    public ChannelReader<RequestReply> AddHandleFor(string url)
    {
        var (handler, receiver) = DealerRequestHandler.Create();
        lock (Lock)
        {
            if (_dealer is not null)
            {
                _dealer.AddHandler(url, handler);
                return receiver;
            }

            if (_builder is not null)
            {
                _builder.AddHandler(url, handler);
                return receiver;
            }

            throw DealerException.BuilderNotAvailable();
        }
    }

    /// <summary>Upstream <c>handle_for(uri)</c>: same as <see cref="AddHandleFor"/> (upstream only wraps it in a stream).</summary>
    public ChannelReader<RequestReply> HandleFor(string uri) => AddHandleFor(uri);

    /// <summary>Upstream <c>handles(uri)</c>.</summary>
    public bool Handles(string uri)
    {
        lock (Lock)
        {
            if (_dealer is not null)
            {
                return _dealer.Handles(uri);
            }

            return _builder?.Handles(uri) ?? false;
        }
    }

    /// <summary>
    /// Upstream <c>start()</c>: connects the websocket (failing if the first attempt fails) and keeps it alive in the
    /// background, reconnecting every 10 s after a failure. <paramref name="cancellationToken"/> only cancels the
    /// first connection attempt.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Log.LogDebug("Launching dealer");

        var session = Session;
        // the url has to be a function that can retrieve a new url,
        // otherwise when we later try to reconnect with the initial url/token
        // and the token is expired we will just get 401 error
        GetDealerUrl getUrl = ct => GetUrlAsync(session, ct);

        Builder builder;
        lock (Lock)
        {
            builder = _builder ?? throw DealerException.BuilderNotAvailable();
            _builder = null;
        }

        Dealer dealer;
        try
        {
            // upstream passes no proxy to the dealer
            dealer = await builder.LaunchAsync(getUrl, null, cancellationToken).ConfigureAwait(false);
        }
        catch (LibrespotException e)
        {
            throw DealerException.LaunchFailure(e);
        }

        bool set;
        lock (Lock)
        {
            set = _dealer is null;
            if (set)
            {
                _dealer = dealer;
            }
        }

        if (!set)
        {
            // upstream: the rejected Dealer is dropped
            await dealer.CloseAsync().ConfigureAwait(false);
            throw DealerException.CouldNotSetDealer();
        }
    }

    /// <summary>Upstream <c>close()</c>: shuts the websocket down and awaits the background tasks.</summary>
    public async Task CloseAsync()
    {
        Dealer? dealer;
        lock (Lock)
        {
            dealer = _dealer;
            _dealer = null;
        }

        if (dealer is not null)
        {
            await dealer.CloseAsync().ConfigureAwait(false);
        }
    }
}
