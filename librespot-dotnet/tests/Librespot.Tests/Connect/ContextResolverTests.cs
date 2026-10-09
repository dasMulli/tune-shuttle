using Librespot.Connect;
using Librespot.Core;
using Xunit;
using Context = Spotify.Player.Proto.Context;
using ContextPage = Spotify.Player.Proto.ContextPage;
using TransferState = Spotify.Player.Proto.Transfer.TransferState;

namespace Librespot.Tests.Connect;

/// <summary>Tests for connect/src/context_resolver.rs (upstream has no unit tests; network paths are not exercised).</summary>
public class ContextResolverTests
{
    private const string AlbumUri = "spotify:album:0000000000000000000abcde";

    private static ContextResolver NewResolver() => new(new Session(SessionConfig.Default(), null));

    [Fact]
    public void Add_ignores_duplicates()
    {
        var resolver = NewResolver();
        resolver.Add(ResolveContext.FromUri(AlbumUri, "", ContextType.Default, ContextAction.Replace));
        resolver.Add(ResolveContext.FromUri(AlbumUri, "", ContextType.Default, ContextAction.Replace));

        Assert.True(resolver.HasNext);
        resolver.RemoveUsedAndInvalid();
        Assert.False(resolver.HasNext);
    }

    [Fact]
    public void Invalid_entries_are_skipped_and_removed()
    {
        var resolver = NewResolver();
        resolver.Add(ResolveContext.FromUri("spotify:search:abc", "", ContextType.Default, ContextAction.Replace));
        resolver.Add(ResolveContext.FromUri(AlbumUri, "", ContextType.Default, ContextAction.Replace));
        resolver.Add(ResolveContext.FromUri(AlbumUri, "", ContextType.Autoplay, ContextAction.Replace));

        resolver.RemoveUsedAndInvalid();

        // only the autoplay entry is left
        var state = StateTests.NewState();
        var context = StateTests.MakeContext("spotify:station:x", 2);
        Assert.Null(resolver.ApplyNextContext(state, context));
        Assert.NotNull(state.TryGetContext(ContextType.Autoplay));

        resolver.RemoveUsedAndInvalid();
        Assert.False(resolver.HasNext);
    }

    [Fact]
    public void Fallback_is_used_when_uri_is_invalid()
    {
        var resolve = ResolveContext.FromUri("", AlbumUri, ContextType.Default, ContextAction.Replace);
        Assert.Equal(AlbumUri, resolve.ResolveUri());
        Assert.Equal("", resolve.ContextUri());
        Assert.Equal($"resolve_uri: <Some(\"{AlbumUri}\")>, context_uri: <>, update: <Default>", resolve.ToString());

        var fromContext = ResolveContext.FromContext(
            new Context { Uri = "spotify:search:x", Pages = { ContextPage.FromUris([StateTests.TrackUri(1)]) } },
            ContextType.Default,
            ContextAction.Replace);
        Assert.Equal(StateTests.TrackUri(1), fromContext.ResolveUri());
        Assert.Equal("spotify:search:x", fromContext.ContextUri());

        var none = ResolveContext.FromUri("spotify:search:x", "", ContextType.Autoplay, ContextAction.Append);
        Assert.Null(none.ResolveUri());
        Assert.Equal("resolve_uri: <None>, context_uri: <spotify:search:x>, update: <Autoplay>", none.ToString());
    }

    [Fact]
    public void Unavailable_contexts_are_not_added_again()
    {
        var resolver = NewResolver();
        var resolve = ResolveContext.FromUri(AlbumUri, "", ContextType.Default, ContextAction.Replace);
        resolver.Add(resolve);
        resolver.MarkNextUnavailable();
        resolver.RemoveUsedAndInvalid();
        Assert.False(resolver.HasNext);

        resolver.Add(ResolveContext.FromUri(AlbumUri, "", ContextType.Default, ContextAction.Replace));
        Assert.False(resolver.HasNext);
    }

    [Fact]
    public void Apply_replace_returns_remaining_pages_and_try_finish_sets_up_playback()
    {
        var resolver = NewResolver();
        resolver.Add(ResolveContext.FromUri(AlbumUri, "", ContextType.Default, ContextAction.Replace));

        var context = StateTests.MakeContext(AlbumUri, 4);
        context.Pages.Add(new ContextPage { PageUrl = "hm://artistplaycontext/v1/page/spotify/album/5LFzwirfFwBKXJQGfwmiMY/km_artist" });

        var state = StateTests.NewState();
        var remaining = resolver.ApplyNextContext(state, context);

        var append = Assert.Single(remaining!);
        Assert.Equal("spotify:album:5LFzwirfFwBKXJQGfwmiMY", append.ContextUri());
        Assert.Equal(ContextAction.Append, append.Action);
        Assert.Equal(ContextType.Default, append.Update);

        TransferState? transfer = null;
        Assert.True(resolver.TryFinish(state, ref transfer));
        Assert.Equal(StateTests.TrackUri(0), state.Player.Track.Uri);
        Assert.Equal(3, state.Player.NextTracks.Count);

        // the appended page is applied with ContextAction.Append
        resolver.RemoveUsedAndInvalid();
        resolver.AddList(remaining!);
        var page = StateTests.MakeContext(AlbumUri, 2, 4);
        Assert.Null(resolver.ApplyNextContext(state, page));
        Assert.Equal(6, state.GetContext(ContextType.Default).Tracks.Count);
    }

    [Fact]
    public void Apply_append_with_multiple_pages_fails()
    {
        var resolver = NewResolver();
        resolver.Add(ResolveContext.FromUri(AlbumUri, "", ContextType.Default, ContextAction.Append));

        var context = StateTests.MakeContext(AlbumUri, 1);
        context.Pages.Add(new ContextPage());

        var e = Assert.Throws<ContextResolverException>(() => resolver.ApplyNextContext(StateTests.NewState(), context));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
        Assert.Equal("tried appending context with 2 pages", e.Detail);
    }

    [Fact]
    public void Apply_without_next_fails()
    {
        var e = Assert.Throws<ContextResolverException>(() => NewResolver().ApplyNextContext(StateTests.NewState(), new Context()));
        Assert.Equal(ContextResolverException.Reason.NoNext, e.Cause);
    }

    [Fact]
    public void Try_finish_waits_for_the_last_of_its_kind()
    {
        var resolver = NewResolver();
        resolver.Add(ResolveContext.FromUri(AlbumUri, "", ContextType.Default, ContextAction.Replace));
        resolver.Add(ResolveContext.FromUri("spotify:album:other", "", ContextType.Default, ContextAction.Replace));

        TransferState? transfer = null;
        Assert.False(resolver.TryFinish(StateTests.NewState(), ref transfer));
    }

    [Fact]
    public async Task Autoplay_for_podcasts_is_not_allowed()
    {
        var resolver = NewResolver();
        resolver.Add(ResolveContext.FromUri("spotify:show:abc", "", ContextType.Autoplay, ContextAction.Replace));

        var e = await Assert.ThrowsAsync<ContextResolverException>(() => resolver.GetNextContextAsync(() => []));
        Assert.Equal(ContextResolverException.Reason.NotAllowedContext, e.Cause);
        Assert.Equal("tried resolving not allowed context: \"spotify:show:abc\"", e.Detail);
    }

    [Fact]
    public async Task Get_next_context_without_next_fails()
    {
        var e = await Assert.ThrowsAsync<ContextResolverException>(() => NewResolver().GetNextContextAsync(() => []));
        Assert.Equal(ContextResolverException.Reason.NoNext, e.Cause);
    }
}
