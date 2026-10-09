// Port of connect/src/context_resolver.rs.
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using AutoplayContextRequest = Spotify.Player.Proto.AutoplayContextRequest;
using Context = Spotify.Player.Proto.Context;
using TransferState = Spotify.Player.Proto.Transfer.TransferState;

namespace Librespot.Connect;

/// <summary>What to resolve: a uri, or an already known context (upstream <c>Resolve</c>).</summary>
/// <remarks>
/// Record equality compares the protobuf <see cref="Context"/> structurally, which matches upstream's byte-wise
/// <c>Hash</c>/<c>Eq</c> for the purposes of the resolver queue and the unavailable map.
/// </remarks>
internal abstract record Resolve
{
    private Resolve()
    {
    }

    /// <summary>Resolve the context of a uri.</summary>
    public sealed record Uri(string Value) : Resolve;

    /// <summary>Resolve a given context.</summary>
    public sealed record Context(Spotify.Player.Proto.Context Value) : Resolve;
}

/// <summary>How a resolved context is applied (upstream <c>ContextAction</c>).</summary>
internal enum ContextAction
{
    /// <summary>Append the single page to the default context.</summary>
    Append,

    /// <summary>Replace the context.</summary>
    Replace,
}

/// <summary>A queued context resolution (upstream <c>ResolveContext</c>).</summary>
internal sealed record ResolveContext
{
    private ResolveContext(Resolve resolve, string? fallback, ContextType update, ContextAction action)
    {
        Resolve = resolve;
        Fallback = fallback;
        Update = update;
        Action = action;
    }

    /// <summary>What to resolve.</summary>
    public Resolve Resolve { get; }

    /// <summary>The uri used when <see cref="Resolve"/> yields no valid uri.</summary>
    public string? Fallback { get; }

    /// <summary>Which context is updated.</summary>
    public ContextType Update { get; }

    /// <summary>How the result is applied.</summary>
    public ContextAction Action { get; }

    /// <summary>Upstream <c>append_context</c>: appends the page at <paramref name="uri"/> to the default context.</summary>
    internal static ResolveContext AppendContext(string uri) =>
        new(new Resolve.Uri(uri), null, ContextType.Default, ContextAction.Append);

    /// <summary>Upstream <c>from_uri</c>; an empty <paramref name="fallback"/> means no fallback.</summary>
    public static ResolveContext FromUri(string uri, string fallback, ContextType update, ContextAction action) =>
        new(new Resolve.Uri(uri), fallback.Length != 0 ? fallback : null, update, action);

    /// <summary>Upstream <c>from_context</c>.</summary>
    public static ResolveContext FromContext(Context context, ContextType update, ContextAction action) =>
        new(new Resolve.Context(context), null, update, action);

    /// <summary>the uri which should be used to resolve the context, might not be the context uri</summary>
    internal string? ResolveUri()
    {
        // it's important to call this always, or at least for every ResolveContext
        // otherwise we might not even check if we need to fallback and just use the fallback uri
        var uri = Resolve switch
        {
            Resolve.Uri u => ConnectState.ValidResolveUri(u.Value),
            Resolve.Context c => ConnectState.FindValidUri(c.Value.HasUri ? c.Value.Uri : null, c.Value.Pages.FirstOrDefault()),
            _ => null,
        };

        return uri ?? Fallback;
    }

    /// <summary>the actual context uri</summary>
    internal string ContextUri() => Resolve switch
    {
        Resolve.Uri u => u.Value,
        Resolve.Context c => c.Value.HasUri ? c.Value.Uri : "",
        _ => "",
    };

    /// <summary>Upstream <c>Display</c>.</summary>
    public override string ToString() =>
        $"resolve_uri: <{RustDebug.Option(ResolveUri())}>, context_uri: <{ContextUri()}>, update: <{Update}>";
}

/// <summary>Upstream <c>ContextResolverError</c> (kind FailedPrecondition).</summary>
internal sealed class ContextResolverException : LibrespotException
{
    /// <summary>The upstream variants.</summary>
    public enum Reason
    {
        /// <summary>No next context to resolve.</summary>
        NoNext,
        /// <summary>Tried appending a context with an unexpected number of pages.</summary>
        UnexpectedPagesSize,
        /// <summary>Tried resolving a context that is not allowed.</summary>
        NotAllowedContext,
    }

    /// <summary>Which upstream variant this is.</summary>
    public Reason Cause { get; }

    private ContextResolverException(Reason cause, string detail) : base(ErrorKind.FailedPrecondition, detail)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>NoNext</c>.</summary>
    public static ContextResolverException NoNext() => new(Reason.NoNext, "no next context to resolve");

    /// <summary>Upstream <c>UnexpectedPagesSize(usize)</c>.</summary>
    public static ContextResolverException UnexpectedPagesSize(int size) =>
        new(Reason.UnexpectedPagesSize, $"tried appending context with {size} pages");

    /// <summary>Upstream <c>NotAllowedContext(String)</c>.</summary>
    public static ContextResolverException NotAllowedContext(string uri) =>
        new(Reason.NotAllowedContext, $"tried resolving not allowed context: \"{uri}\"");
}

/// <summary>Queues contexts to resolve and applies them to the <see cref="ConnectState"/> (upstream <c>ContextResolver</c>).</summary>
internal sealed class ContextResolver
{
    // time after which an unavailable context is retried
    private static readonly TimeSpan RetryUnavailable = TimeSpan.FromSeconds(3600);

    private static readonly ILogger Log = LibrespotLog.CreateLogger<ContextResolver>();

    private readonly Session _session;
    private readonly List<ResolveContext> _queue = [];
    private readonly Dictionary<ResolveContext, long> _unavailableContexts = new();

    /// <summary>Upstream <c>ContextResolver::new</c>.</summary>
    public ContextResolver(Session session)
    {
        _session = session;
    }

    /// <summary>Queues <paramref name="resolve"/> unless it is already queued or was unavailable within the last hour.</summary>
    public void Add(ResolveContext resolve)
    {
        TimeSpan? lastTry = _unavailableContexts.TryGetValue(resolve, out var since)
            ? Stopwatch.GetElapsedTime(since)
            : null;

        if (lastTry is { } elapsed && elapsed > RetryUnavailable)
        {
            _unavailableContexts.Remove(resolve);
            Log.LogDebug(
                "context was requested {Seconds}s ago, trying again to resolve the requested context",
                (ulong)elapsed.TotalSeconds);
            lastTry = null;
        }

        if (lastTry is not null)
        {
            Log.LogDebug("tried loading unavailable context: {Resolve}", resolve);
            return;
        }
        else if (_queue.Contains(resolve))
        {
            Log.LogDebug("update for {Resolve} is already added", resolve);
            return;
        }
        else
        {
            Log.LogTrace("added {Uri} to resolver queue", resolve.ResolveUri() ?? resolve.ContextUri());
        }

        _queue.Add(resolve);
    }

    /// <summary>Queues every item of <paramref name="resolve"/>.</summary>
    public void AddList(IEnumerable<ResolveContext> resolve)
    {
        foreach (var item in resolve)
        {
            Add(item);
        }
    }

    /// <summary>Removes the next context and every invalid one queued before it.</summary>
    public void RemoveUsedAndInvalid()
    {
        if (FindNext() is { } next)
        {
            _queue.RemoveRange(0, next.Index); // remove invalid
        }

        if (_queue.Count > 0)
        {
            _queue.RemoveAt(0); // remove used
        }
    }

    /// <summary>Empties the queue.</summary>
    public void Clear() => _queue.Clear();

    private (ResolveContext Next, string Uri, int Index)? FindNext()
    {
        for (var idx = 0; idx < _queue.Count; idx++)
        {
            var next = _queue[idx];
            if (next.ResolveUri() is { } uri)
            {
                return (next, uri, idx);
            }

            Log.LogWarning("skipped {Index} because of invalid resolve_uri: {Next}", idx, next);
        }

        return null;
    }

    /// <summary>Whether a valid context is queued.</summary>
    public bool HasNext => FindNext() is not null;

    /// <summary>Fetches the next context: the context itself, or its autoplay continuation.</summary>
    /// <param name="recentTrackUri">Supplies the recently played uris for autoplay requests.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ContextResolverException">Nothing queued, or autoplay for a podcast.</exception>
    public async Task<Context> GetNextContextAsync(Func<List<string>> recentTrackUri, CancellationToken cancellationToken = default)
    {
        var (next, resolveUri, _) = FindNext() ?? throw ContextResolverException.NoNext();

        switch (next.Update)
        {
            case ContextType.Default:
            {
                var ctx = await _session.SpClient.GetContextAsync(resolveUri, cancellationToken).ConfigureAwait(false);
                ctx.Uri = next.ContextUri();
                ctx.Url = $"context://{ctx.Uri}";

                return ctx;
            }
            case ContextType.Autoplay:
            {
                if (resolveUri.Contains("spotify:show:", StringComparison.Ordinal)
                    || resolveUri.Contains("spotify:episode:", StringComparison.Ordinal))
                {
                    // autoplay is not supported for podcasts
                    throw ContextResolverException.NotAllowedContext(resolveUri);
                }

                var request = new AutoplayContextRequest { ContextUri = resolveUri };
                request.RecentTrackUri.Add(recentTrackUri());

                return await _session.SpClient.GetAutoplayContextAsync(request, cancellationToken).ConfigureAwait(false);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(next.Update), next.Update, null);
        }
    }

    /// <summary>Remembers the next context as unavailable, so it is not retried within the next hour.</summary>
    public void MarkNextUnavailable()
    {
        if (FindNext() is { } next)
        {
            _unavailableContexts[next.Next] = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Applies a resolved <paramref name="context"/> to <paramref name="state"/> as the next queued item describes.
    /// Returns further pages that have to be resolved, or null.
    /// </summary>
    public List<ResolveContext>? ApplyNextContext(ConnectState state, Context context)
    {
        var (next, _, _) = FindNext() ?? throw ContextResolverException.NoNext();

        List<string>? remaining;
        switch (next.Action)
        {
            case ContextAction.Append when context.Pages.Count == 1:
            {
                var page = context.Pages[0];
                context.Pages.RemoveAt(0);
                state.FillContextFromPage(page);
                remaining = null;
                break;
            }
            case ContextAction.Replace:
            {
                // upstream merges the given context even when the update failed, then propagates the error
                ExceptionDispatchInfo? failure = null;
                remaining = null;
                try
                {
                    remaining = state.UpdateContext(context, next.Update);
                }
                catch (LibrespotException e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }

                if (next.Resolve is Resolve.Context resolveContext)
                {
                    state.MergeContext(resolveContext.Value.Pages.LastOrDefault()?.Clone());
                }

                failure?.Throw();
                break;
            }
            default:
                Log.LogWarning("unexpected page size: {Context}", context);
                throw ContextResolverException.UnexpectedPagesSize(context.Pages.Count);
        }

        return remaining?.Select(ResolveContext.AppendContext).ToList();
    }

    /// <summary>
    /// When the next item is the last queued one of its kind, finishes the state setup (transfer, shuffle or
    /// playback position). Returns whether the state was finished.
    /// </summary>
    public bool TryFinish(ConnectState state, ref TransferState? transferState)
    {
        if (FindNext() is not { } found)
        {
            return false;
        }

        var next = found.Next;

        // when there is only one update type, we are the last of our kind, so we should update the state
        if (_queue.Count(resolve => resolve.Update == next.Update) != 1)
        {
            return false;
        }

        switch (next.Update, state.ActiveContext)
        {
            case (ContextType.Default, ContextType.Default) or (ContextType.Autoplay, _):
                Log.LogDebug("last item of type <{Update}>, finishing state setup", next.Update);
                break;
            case (ContextType.Default, _):
                Log.LogDebug("skipped finishing default, because it isn't the active context");
                return false;
        }

        var activeCtx = state.TryGetContext(state.ActiveContext);
        try
        {
            if (transferState is { } transfer)
            {
                transferState = null;
                state.FinishTransfer(transfer);
            }
            else if (state.ShufflingContext && next.Update == ContextType.Default)
            {
                state.ShuffleNew();
            }
            else if (activeCtx is { Index.Track: 0 })
            {
                // has context, and context is not touched
                // when the index is not zero, the next index was already evaluated elsewhere
                var idx = ConnectState.TryFindIndexInContext(activeCtx, t => state.CurrentTrack(c => t.Uri == c.Uri));

                state.ResetPlaybackToPosition(idx);
            }
            else
            {
                state.FillUpNextTracks();
            }
        }
        catch (LibrespotException why)
        {
            Log.LogError("setup of state failed: {Why}, last used resolve {Next}", why.Message, next);
        }

        state.UpdateRestrictions();
        state.UpdateQueueRevision();

        return true;
    }
}
