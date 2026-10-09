// Port of connect/src/state/context.rs.
using Librespot.Core;
using Microsoft.Extensions.Logging;
using Librespot.Protocol;
using Context = Spotify.Player.Proto.Context;
using ContextIndex = Spotify.Connectstate.ContextIndex;
using ContextPage = Spotify.Player.Proto.ContextPage;
using ContextTrack = Spotify.Player.Proto.ContextTrack;
using PlayerRestrictions = Spotify.Player.Proto.Restrictions;
using ProvidedTrack = Spotify.Connectstate.ProvidedTrack;

namespace Librespot.Connect;

/// <summary>A resolved context: its tracks, metadata, restrictions and fill-up index (upstream <c>StateContext</c>).</summary>
internal sealed class StateContext
{
    /// <summary>The tracks of the context, possibly shuffled.</summary>
    public required ShuffleVec<ProvidedTrack> Tracks { get; init; }

    /// <summary>The context metadata (also carries the custom shuffle seed and initial track).</summary>
    public required Dictionary<string, string> Metadata { get; init; }

    /// <summary>The context restrictions, if any.</summary>
    public PlayerRestrictions? Restrictions { get; set; }

    /// <summary>is used to keep track which tracks are already loaded into the next_tracks</summary>
    public required ContextIndex Index { get; init; }
}

/// <summary>Which context is meant (upstream <c>ContextType</c>).</summary>
internal enum ContextType
{
    /// <summary>The context that was requested.</summary>
    Default,

    /// <summary>The autoplay continuation of the context.</summary>
    Autoplay,
}

/// <summary>
/// How a context is reset (upstream enum <c>ResetContext</c>; renamed because <see cref="ConnectState.ResetContext"/>
/// is the method that consumes it).
/// </summary>
internal abstract record ResetContextKind
{
    private ResetContextKind()
    {
    }

    /// <summary>Drops the default and the autoplay context.</summary>
    public sealed record Completely : ResetContextKind;

    /// <summary>Keeps the contexts but resets their indices.</summary>
    public sealed record DefaultIndex : ResetContextKind;

    /// <summary>Resets completely when <paramref name="Uri"/> differs from the current context uri.</summary>
    public sealed record WhenDifferent(string Uri) : ResetContextKind;
}

internal sealed partial class ConnectState
{
    private const string LocalFilesIdentifier = "spotify:local-files";
    private const string SearchIdentifier = "spotify:search";

    /// <summary>
    /// Extracts the spotify uri from a given page_url: just extracts "spotify/album/5LFzwirfFwBKXJQGfwmiMY" and
    /// replaces the slashes with colons.
    /// </summary>
    /// <remarks>
    /// Expected <paramref name="pageUrl"/> should look something like the following:
    /// <c>hm://artistplaycontext/v1/page/spotify/album/5LFzwirfFwBKXJQGfwmiMY/km_artist</c>
    /// </remarks>
    internal static string PageUrlToUri(string pageUrl)
    {
        string[] split;
        if (pageUrl.StartsWith("hm://", StringComparison.Ordinal))
        {
            split = pageUrl["hm://".Length..].Split('/');
        }
        else
        {
            Log.LogWarning("page_url didn't start with hm://. got page_url: {PageUrl}", pageUrl);
            split = pageUrl.Split('/');
        }

        return string.Join(':', split.SkipWhile(s => s != "spotify").Take(3));
    }

    /// <summary>The index of the first track in <paramref name="ctx"/> matching <paramref name="f"/>.</summary>
    /// <exception cref="StateException">No track matches (<c>CanNotFindTrackInContext</c>).</exception>
    public static int FindIndexInContext(StateContext ctx, Func<ProvidedTrack, bool> f) =>
        TryFindIndexInContext(ctx, f) ?? throw StateException.CanNotFindTrackInContext(null, ctx.Tracks.Count);

    /// <summary><see cref="FindIndexInContext"/> without the error (upstream <c>find_index_in_context(..).ok()</c>).</summary>
    public static int? TryFindIndexInContext(StateContext ctx, Func<ProvidedTrack, bool> f)
    {
        var index = ctx.Tracks.FindIndex(t => f(t));
        return index >= 0 ? index : null;
    }

    /// <summary>The context of the given type.</summary>
    /// <exception cref="StateException">The context is not available (<c>NoContext</c>).</exception>
    public StateContext GetContext(ContextType ty) => TryGetContext(ty) ?? throw StateException.NoContext(ty);

    /// <summary>The context of the given type, or null (upstream <c>get_context(..).ok()</c>).</summary>
    public StateContext? TryGetContext(ContextType ty) => ty switch
    {
        ContextType.Default => _context,
        ContextType.Autoplay => _autoplayContext,
        _ => null,
    };

    /// <summary>The uri of the context that is played.</summary>
    public string ContextUri => Player.ContextUri;

    private bool DifferentContextUri(string uri) =>
        // search identifier is always different
        ContextUri != uri || uri.StartsWith(SearchIdentifier, StringComparison.Ordinal);

    /// <summary>Resets the contexts as described by <paramref name="resetAs"/>.</summary>
    public void ResetContext(ResetContextKind resetAs)
    {
        if (resetAs is ResetContextKind.WhenDifferent whenDifferent && DifferentContextUri(whenDifferent.Uri))
        {
            resetAs = new ResetContextKind.Completely();
        }

        if (_context is { } ctx)
        {
            ctx.Metadata.RemoveShuffleSeed();
            ctx.Metadata.RemoveInitialTrack();
            ctx.Tracks.Unshuffle();
        }

        switch (resetAs)
        {
            case ResetContextKind.WhenDifferent:
                Log.LogDebug("context didn't change, no reset");
                break;
            case ResetContextKind.Completely:
                _context = null;
                _autoplayContext = null;

                Player.ContextUri = "";
                Player.ContextUrl = "";
                break;
            case ResetContextKind.DefaultIndex:
                foreach (var context in new[] { _context, _autoplayContext })
                {
                    if (context is null)
                    {
                        continue;
                    }

                    context.Index.Track = 0;
                    context.Index.Page = 0;
                }

                break;
        }

        _fillUpContext = ContextType.Default;
        SetActiveContext(ContextType.Default);
        UpdateRestrictions();
    }

    /// <summary>The uri if it can be resolved: not empty and not a search uri.</summary>
    public static string? ValidResolveUri(string uri) =>
        uri.Length == 0 || uri.StartsWith(SearchIdentifier, StringComparison.Ordinal) ? null : uri;

    /// <summary>The context uri if it is valid, otherwise the uri of the first track of the first page.</summary>
    public static string? FindValidUri(string? contextUri, ContextPage? firstPage)
    {
        if (contextUri is not null && ValidResolveUri(contextUri) is { } valid)
        {
            return valid;
        }

        return firstPage?.Tracks.FirstOrDefault() is { HasUri: true } track ? track.Uri : null;
    }

    /// <summary>Switches the active context and copies its restrictions and metadata into the player state.</summary>
    public void SetActiveContext(ContextType newContext)
    {
        ActiveContext = newContext;

        var player = Player;

        player.ContextMetadata.Clear();
        player.ContextRestrictions = new Spotify.Connectstate.Restrictions();
        player.Restrictions = new Spotify.Connectstate.Restrictions();

        var ctx = TryGetContext(newContext);
        if (ctx is null)
        {
            Log.LogWarning("couldn't load context info because: {Why}", StateException.NoContext(newContext).Detail);
            return;
        }

        if (ctx.Restrictions is { } restrictions)
        {
            player.Restrictions = restrictions.ToConnectState();
        }

        foreach (var (key, value) in ctx.Metadata)
        {
            player.ContextMetadata[key] = value;
        }
    }

    /// <summary>
    /// Replaces the context of type <paramref name="ty"/> with the first page of <paramref name="context"/>; the
    /// tracks of further pages are appended. Returns the uris of pages that still have to be resolved, or null.
    /// </summary>
    public List<string>? UpdateContext(Context context, ContextType ty)
    {
        var contextUri = context.HasUri ? context.Uri : null;

        if (context.Pages.All(p => p.Tracks.Count == 0))
        {
            Log.LogError("context didn't have any tracks: {Context}", context);
            throw StateException.ContextHasNoTracks();
        }
        else if (contextUri is not null && contextUri.StartsWith(LocalFilesIdentifier, StringComparison.Ordinal))
        {
            throw StateException.UnsupportedLocalPlayback();
        }

        var nextContexts = new List<ContextPage>();
        ContextPage? firstPage = null;
        foreach (var page in context.Pages)
        {
            if (firstPage is null && page.Tracks.Count > 0)
            {
                firstPage = page;
            }
            else
            {
                nextContexts.Add(page);
            }
        }

        var firstContextPage = firstPage ?? throw StateException.ContextHasNoTracks();

        Log.LogDebug(
            "updated context {Type} to <{Uri}> ({Count} tracks)",
            ty,
            RustDebug.Option(contextUri),
            firstContextPage.Tracks.Count);

        switch (ty)
        {
            case ContextType.Default:
            {
                var newContext = StateContextFromPage(
                    firstContextPage,
                    new Dictionary<string, string>(context.Metadata),
                    context.Restrictions,
                    contextUri,
                    0,
                    null);

                // when we update the same context, we should try to preserve the previous position
                // otherwise we might load the entire context twice, unless it's the search context
                if (!ContextUri.StartsWith(SearchIdentifier, StringComparison.Ordinal)
                    && contextUri is not null
                    && contextUri == ContextUri)
                {
                    if (FindLastIndexInNewContext(newContext) is { } newIndex)
                    {
                        if (!newIndex.Found)
                        {
                            Player.Index = null;
                        }

                        newContext.Index.Track = newIndex.Index;

                        // enforce reloading the context
                        if (TryGetContext(ContextType.Autoplay) is { } autoplayCtx)
                        {
                            autoplayCtx.Index.Track = 0;
                        }

                        ClearNextTracks();
                    }
                }

                _context = newContext;

                if (!(context.HasUrl && context.Url.Contains(SearchIdentifier, StringComparison.Ordinal)))
                {
                    Player.ContextUrl = context.HasUrl ? context.Url : "";
                }
                else
                {
                    Player.ContextUrl = "";
                }

                Player.ContextUri = contextUri ?? "";
                break;
            }
            case ContextType.Autoplay:
                _autoplayContext = StateContextFromPage(
                    firstContextPage,
                    new Dictionary<string, string>(context.Metadata),
                    context.Restrictions,
                    contextUri,
                    null,
                    Provider.Autoplay);
                break;
        }

        if (nextContexts.Count == 0)
        {
            return null;
        }

        // load remaining contexts
        var remaining = new List<string>();
        foreach (var page in nextContexts)
        {
            if (page.Tracks.Count > 0)
            {
                try
                {
                    FillContextFromPage(page);
                }
                catch (LibrespotException)
                {
                    // upstream: `self.fill_context_from_page(page).ok()?` drops the error silently
                }
            }
            else if (page is { HasPageUrl: true, PageUrl.Length: > 0 })
            {
                remaining.Add(PageUrlToUri(page.PageUrl));
            }
            else
            {
                Log.LogWarning("unhandled context page: {Page}", page);
            }
        }

        return remaining;
    }

    private int? FindFirstPrevTrackIndex(StateContext ctx)
    {
        var prevTracks = PrevTracks;
        for (var i = prevTracks.Count - 1; i >= 0; i--)
        {
            var prevTrack = prevTracks[i];
            if (TryFindIndexInContext(ctx, t => prevTrack.Uri == t.Uri) is { } idx)
            {
                return idx;
            }
        }

        return null;
    }

    /// <summary>
    /// Upstream returns <c>Option&lt;Result&lt;u32, u32&gt;&gt;</c>: null when there is no previous context,
    /// otherwise the index and whether it was found (false = fallback index).
    /// </summary>
    private (uint Index, bool Found)? FindLastIndexInNewContext(StateContext newContext)
    {
        var ctx = _context;
        if (ctx is null)
        {
            return null;
        }

        var isQueuedItem = CurrentTrack(t => t.IsQueue() || t.Metadata.IsFromQueue());

        int? newIndex;
        if (ctx.Index.Track >= SpotifyMaxNextTracksSize)
        {
            newIndex = (int)ctx.Index.Track - SpotifyMaxNextTracksSize;
        }
        else if (isQueuedItem)
        {
            newIndex = FindFirstPrevTrackIndex(newContext);
        }
        else
        {
            newIndex = TryFindIndexInContext(newContext, current => CurrentTrack(t => t.Uri == current.Uri));
        }

        if (newIndex is { } i)
        {
            return ((uint)i + 1, true);
        }

        Log.LogInformation("couldn't distinguish index from current or previous tracks in the updated context");
        var fallbackIndex = Player.Index?.Track ?? 0;
        Log.LogInformation("falling back to index {FallbackIndex}", fallbackIndex);
        return (fallbackIndex, false);
    }

    private StateContext StateContextFromPage(
        ContextPage page,
        Dictionary<string, string> metadata,
        PlayerRestrictions? restrictions,
        string? newContextUri,
        int? contextLength,
        Provider? provider)
    {
        newContextUri ??= ContextUri;

        var tracks = new List<ProvidedTrack>(page.Tracks.Count);
        for (var i = 0; i < page.Tracks.Count; i++)
        {
            var track = page.Tracks[i];
            try
            {
                tracks.Add(ContextToProvidedTrack(track, newContextUri, contextLength + i, page.Metadata, provider));
            }
            catch (LibrespotException why)
            {
                Log.LogError("couldn't convert {Track} into ProvidedTrack: {Why}", track, why.Message);
            }
        }

        return new StateContext
        {
            Tracks = new ShuffleVec<ProvidedTrack>(tracks),
            Restrictions = restrictions,
            Metadata = metadata,
            Index = new ContextIndex(),
        };
    }

    /// <summary>
    /// Whether <paramref name="track"/> is the initial track of the shuffled active context, which is already
    /// played and has to be skipped in the first iteration.
    /// </summary>
    public bool IsSkipTrack(ProvidedTrack track, uint? iteration)
    {
        var ctx = TryGetContext(ActiveContext);
        if (ctx is null)
        {
            return false;
        }

        if (ctx.Metadata.GetInitialTrack() is not { } uri || uri != track.Uri)
        {
            return false;
        }

        return iteration is null or 0;
    }

    /// <summary>
    /// Merges metadata and uids of <paramref name="newPage"/> into the matching tracks of the default context.
    /// Returns false when there is no default context or no page (upstream <c>Option&lt;()&gt;</c>).
    /// </summary>
    public bool MergeContext(ContextPage? newPage)
    {
        var currentContext = TryGetContext(ContextType.Default);
        if (currentContext is null || newPage is null)
        {
            return false;
        }

        foreach (var newTrack in newPage.Tracks)
        {
            if (!newTrack.HasUri || newTrack.Uri.Length == 0)
            {
                continue;
            }

            var newTrackUri = newTrack.Uri;
            if (TryFindIndexInContext(currentContext, t => t.Uri == newTrackUri) is { } position)
            {
                var contextTrack = currentContext.Tracks[position];

                foreach (var (key, value) in newTrack.Metadata)
                {
                    contextTrack.Metadata[key] = value;
                }

                // the uid provided from another context might be actual uid of an item
                if (newTrack.HasUid)
                {
                    contextTrack.Uid = newTrack.Uid;
                }
            }
        }

        return true;
    }

    /// <summary>Sets the track index of the context of type <paramref name="ty"/>.</summary>
    internal void UpdateContextIndex(ContextType ty, int newIndex)
    {
        var context = GetContext(ty);

        context.Index.Track = (uint)newIndex;
    }

    /// <summary>Converts a context track into a provided track for the player state.</summary>
    /// <exception cref="StateException">The track has neither a valid uri nor a gid.</exception>
    /// <exception cref="LibrespotException">The uri or gid cannot be parsed.</exception>
    public ProvidedTrack ContextToProvidedTrack(
        ContextTrack ctxTrack,
        string? contextUri,
        int? contextIndex,
        IDictionary<string, string>? pageMetadata,
        Provider? provider)
    {
        var trackUri = ctxTrack.HasUri ? ctxTrack.Uri : null;

        SpotifyUri id;
        if (trackUri is not null && trackUri.Contains('?'))
        {
            throw StateException.InvalidTrackUri(trackUri);
        }
        else if (trackUri is { Length: > 0 })
        {
            id = SpotifyUri.FromUri(trackUri);
        }
        else if (ctxTrack is { HasGid: true, Gid.IsEmpty: false })
        {
            id = new SpotifyUri.Track(SpotifyId.FromRaw(ctxTrack.Gid.Span));
        }
        else
        {
            throw StateException.InvalidTrackUri(null);
        }

        var uri = id.ToUri().Replace("unknown", "track", StringComparison.Ordinal);

        var actualProvider = _unavailableUri.Contains(uri) ? Provider.Unavailable : provider ?? Provider.Context;

        // assumption: the uid is used as unique-id of any item
        //  - queue resorting is done by each client and orients itself by the given uid
        //  - if no uid is present, resorting doesn't work or behaves not as intended
        var uid = ctxTrack is { HasUid: true, Uid.Length: > 0 }
            ? ctxTrack.Uid
            // so providing a unique id should allow to resort the queue
            : Guid.NewGuid().ToString("N");

        var track = new ProvidedTrack
        {
            Uri = uri,
            Uid = uid,
            Provider = actualProvider.ToDisplayString(),
        };

        if (pageMetadata is not null)
        {
            foreach (var (k, v) in pageMetadata)
            {
                track.Metadata[k] = v;
            }
        }

        foreach (var (k, v) in ctxTrack.Metadata)
        {
            track.Metadata[k] = v;
        }

        if (contextUri is not null)
        {
            track.Metadata.SetEntityUri(contextUri);
            track.Metadata.SetContextUri(contextUri);
        }

        if (contextIndex is { } index)
        {
            track.Metadata.SetContextIndex(index);
        }

        if (actualProvider == Provider.Autoplay)
        {
            track.Metadata.SetFromAutoplay(true);
        }

        return track;
    }

    /// <summary>Appends the tracks of <paramref name="page"/> to the default context.</summary>
    /// <exception cref="StateException">There is no default context.</exception>
    public void FillContextFromPage(ContextPage page)
    {
        var ctxLen = _context?.Tracks.Count;
        var context = StateContextFromPage(page, new Dictionary<string, string>(), null, null, ctxLen, null);

        var ctx = _context ?? throw StateException.NoContext(ContextType.Default);

        foreach (var t in context.Tracks)
        {
            ctx.Tracks.Add(t);
        }
    }
}
