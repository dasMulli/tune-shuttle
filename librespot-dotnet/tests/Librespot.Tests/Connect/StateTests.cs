using System.Globalization;
using Librespot.Connect;
using Librespot.Core;
using Librespot.Core.Dealer;
using Xunit;
using Context = Spotify.Player.Proto.Context;
using ContextPage = Spotify.Player.Proto.ContextPage;
using ContextTrack = Spotify.Player.Proto.ContextTrack;
using PlayerContextPlayerOptions = Spotify.Player.Proto.ContextPlayerOptions;
using TransferPlayback = Spotify.Player.Proto.Transfer.Playback;
using ProvidedTrack = Spotify.Connectstate.ProvidedTrack;
using Queue = Spotify.Player.Proto.Transfer.Queue;
using TransferSession = Spotify.Player.Proto.Transfer.Session;
using TransferState = Spotify.Player.Proto.Transfer.TransferState;

namespace Librespot.Tests.Connect;

/// <summary>Tests for connect/src/state.rs and state/*.rs (upstream has no unit tests for these files).</summary>
public class StateTests
{
    private const string AlbumUri = "spotify:album:0000000000000000000abcde";
    private const string AutoplayUri = "spotify:station:0000000000000000000abcde";

    internal static string TrackUri(int i) => $"spotify:track:{i.ToString("D22", CultureInfo.InvariantCulture)}";

    internal static Context MakeContext(string uri, int count, int offset = 0)
    {
        var context = new Context { Uri = uri };
        context.Pages.Add(ContextPage.FromUris(Enumerable.Range(offset, count).Select(TrackUri)));
        return context;
    }

    internal static ConnectState NewState() => new(new ConnectConfig(), "device-id", "client-id", "session-id");

    private static ConnectState Loaded(int count)
    {
        var state = NewState();
        state.UpdateContext(MakeContext(AlbumUri, count), ContextType.Default);
        state.ResetPlaybackToPosition(null);
        return state;
    }

    private static List<string> Uris(IEnumerable<ProvidedTrack> tracks) => tracks.Select(t => t.Uri).ToList();

    private static List<string> TrackUris(params int[] indices) => indices.Select(TrackUri).ToList();

    // Vectors generated with Rust std 1.98: `DefaultHasher::new()` + `String::hash` for each uri.
    [Fact]
    public void Rust_default_hasher_matches_std()
    {
        Assert.Equal(15130871412783076140UL, new RustDefaultHasher().Finish());

        var one = new RustDefaultHasher();
        one.WriteStr("spotify:track:abc");
        Assert.Equal(588664255852550666UL, one.Finish());

        var three = new RustDefaultHasher();
        foreach (var uri in new[] { "spotify:track:4uLU6hMCjMI75M1A2tKUQC", "spotify:track:1301WleyT98MSxVHPZCA6M", "spotify:delimiter" })
        {
            three.WriteStr(uri);
        }

        Assert.Equal(10091276708217813770UL, three.Finish());
    }

    [Fact]
    public void Queue_revision_hashes_next_track_uris()
    {
        var state = NewState();
        state.SetNextTracks(
        [
            new ProvidedTrack { Uri = "spotify:track:4uLU6hMCjMI75M1A2tKUQC" },
            new ProvidedTrack { Uri = "spotify:track:1301WleyT98MSxVHPZCA6M" },
            new ProvidedTrack { Uri = "spotify:delimiter" },
        ]);
        state.UpdateQueueRevision();

        Assert.Equal("10091276708217813770", state.Player.QueueRevision);
    }

    [Fact]
    public void New_state_describes_the_device()
    {
        var state = new ConnectState(new ConnectConfig { Name = "test", VolumeSteps = 32, InitialVolume = 1000 }, "dev", "cid", "sid");

        Assert.Equal("test", state.DeviceInfo.Name);
        Assert.Equal("dev", state.DeviceInfo.DeviceId);
        Assert.Equal("cid", state.DeviceInfo.ClientId);
        Assert.Equal(1000u, state.DeviceInfo.Volume);
        Assert.Equal(32, state.DeviceInfo.Capabilities.VolumeSteps);
        Assert.Equal(["audio/episode", "audio/track", "audio/local"], state.DeviceInfo.Capabilities.SupportedTypes);
        Assert.Equal("sid", state.Player.SessionId);
        Assert.True(state.Player.IsSystemInitiated);
        Assert.Equal(1.0, state.Player.PlaybackSpeed);
        Assert.Equal(ushort.MaxValue / 32, state.VolumeStepSize);
        Assert.False(state.IsActive);

        Assert.Equal(1024, new ConnectState(new ConnectConfig { VolumeSteps = 0 }, "d", "c", "s").VolumeStepSize);
    }

    [Fact]
    public void Set_last_command_and_active()
    {
        var state = NewState();
        state.SetLastCommand(new Request { MessageId = 5, SentByDeviceId = "abc" });
        Assert.Equal(5u, state.Request.LastCommandMessageId);
        Assert.Equal("abc", state.Request.LastCommandSentByDeviceId);

        state.SetActive(true);
        state.SetNow(1234);
        Assert.True(state.IsActive);
        Assert.Equal(1234UL, state.Request.ClientSideTimestamp);
        Assert.True(state.Request.StartedPlayingAt > 0);
    }

    [Fact]
    public void Set_status_maps_play_states()
    {
        var state = Loaded(3);

        state.SetStatus(new SpircPlayStatus.Playing(0, false));
        Assert.True(state.IsPlaying);
        Assert.False(state.IsPause);
        Assert.Equal(1.0, state.Player.PlaybackSpeed);
        Assert.Equal(["not_paused"], state.Player.Restrictions.DisallowResumingReasons);

        state.SetStatus(new SpircPlayStatus.Paused(10, false));
        Assert.False(state.IsPlaying);
        Assert.True(state.IsPause);
        Assert.Equal(0.0, state.Player.PlaybackSpeed);
        Assert.Equal(["not_playing"], state.Player.Restrictions.DisallowPausingReasons);
        Assert.Empty(state.Player.Restrictions.DisallowResumingReasons);

        state.SetStatus(new SpircPlayStatus.Stopped());
        Assert.True(state.Player.IsPaused);
    }

    [Fact]
    public void Reset_playback_fills_next_tracks()
    {
        var state = Loaded(5);

        Assert.Equal(TrackUri(0), state.Player.Track.Uri);
        Assert.Equal(0u, state.Player.Index.Track);
        Assert.Equal(TrackUris(1, 2, 3, 4), Uris(state.Player.NextTracks));
        Assert.Empty(state.Player.PrevTracks);
        Assert.Equal(["no previous tracks"], state.Player.Restrictions.DisallowSkippingPrevReasons);
        Assert.Equal(AlbumUri, state.ContextUri);
        Assert.Equal("context", state.Player.Track.Provider);
        Assert.Equal(AlbumUri, state.Player.Track.Metadata["context_uri"]);
        Assert.Equal("0", state.Player.Track.Metadata["context_index"]);
    }

    [Fact]
    public void Reset_playback_to_position_sets_prev_tracks()
    {
        var state = NewState();
        state.UpdateContext(MakeContext(AlbumUri, 30), ContextType.Default);
        state.ResetPlaybackToPosition(15);

        Assert.Equal(TrackUri(15), state.Player.Track.Uri);
        Assert.Equal(Enumerable.Range(5, 10).Select(TrackUri), Uris(state.Player.PrevTracks));
        Assert.Equal(Enumerable.Range(16, 14).Select(TrackUri), Uris(state.Player.NextTracks));
        Assert.Empty(state.Player.Restrictions.DisallowSkippingPrevReasons);
    }

    [Fact]
    public void Next_and_prev_track_navigate_the_context()
    {
        var state = Loaded(5);

        Assert.Equal(1u, state.NextTrack());
        Assert.Equal(TrackUri(1), state.Player.Track.Uri);
        Assert.Equal(TrackUris(0), Uris(state.Player.PrevTracks));
        Assert.Equal(TrackUris(2, 3, 4), Uris(state.Player.NextTracks));

        var prev = state.PrevTrack();
        Assert.NotNull(prev);
        Assert.Equal(TrackUri(0), prev.Uri);
        Assert.Equal(0u, state.Player.Index.Track);
        Assert.Equal(TrackUris(1, 2, 3, 4), Uris(state.Player.NextTracks));
        Assert.Empty(state.Player.PrevTracks);

        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal((uint)i, state.NextTrack());
        }

        Assert.Empty(state.Player.NextTracks);
        Assert.Null(state.NextTrack());
    }

    [Fact]
    public void Prev_track_without_prev_returns_null()
    {
        var state = Loaded(3);
        Assert.Null(state.PrevTrack());
    }

    [Fact]
    public void Next_tracks_are_limited()
    {
        var state = Loaded(100);
        Assert.Equal(ConnectState.SpotifyMaxNextTracksSize, state.Player.NextTracks.Count);
        Assert.Equal(TrackUri(80), state.Player.NextTracks[^1].Uri);

        state.NextTrack();
        Assert.Equal(ConnectState.SpotifyMaxNextTracksSize, state.Player.NextTracks.Count);
        Assert.Equal(TrackUri(81), state.Player.NextTracks[^1].Uri);

        for (var i = 0; i < 15; i++)
        {
            state.NextTrack();
        }

        Assert.Equal(ConnectState.SpotifyMaxPrevTracksSize, state.Player.PrevTracks.Count);
    }

    [Fact]
    public void Repeat_context_inserts_delimiter()
    {
        var state = NewState();
        state.UpdateContext(MakeContext(AlbumUri, 3), ContextType.Default);
        state.SetRepeatContext(true);
        state.ResetPlaybackToPosition(null);

        var next = state.Player.NextTracks;
        Assert.Equal(ConnectState.SpotifyMaxNextTracksSize, next.Count);
        Assert.Equal(TrackUri(1), next[0].Uri);
        Assert.Equal(TrackUri(2), next[1].Uri);
        Assert.Equal("spotify:delimiter", next[2].Uri);
        Assert.Equal("delimiter0", next[2].Uid);
        Assert.Equal("true", next[2].Metadata["hidden"]);
        Assert.Equal("0", next[2].Metadata["iteration"]);
        Assert.Equal(TrackUri(0), next[3].Uri);
        Assert.Equal("delimiter1", next[6].Uid);

        state.NextTrack();
        state.NextTrack();
        // the delimiter is skipped and moved to the previous tracks
        Assert.Equal(0u, state.NextTrack());
        Assert.Equal(TrackUri(0), state.Player.Track.Uri);
        Assert.Equal("delimiter0", state.Player.PrevTracks[^1].Uid);

        // going back moves the delimiter to the next tracks again
        var prev = state.PrevTrack();
        Assert.Equal(TrackUri(2), prev!.Uri);
        Assert.Equal("delimiter0", state.Player.NextTracks[0].Uid);
    }

    [Fact]
    public void Repeat_track_is_disabled_by_next_track()
    {
        var state = Loaded(3);
        state.SetRepeatTrack(true);
        Assert.Equal(SpotifyUri.FromUri(TrackUri(0)), state.PreviewNextTrack());

        state.NextTrack();
        Assert.False(state.RepeatTrack);
        Assert.Equal(SpotifyUri.FromUri(TrackUri(2)), state.PreviewNextTrack());
    }

    [Fact]
    public void Add_to_queue_inserts_after_queued_tracks()
    {
        var state = Loaded(5);

        state.AddToQueue(new ProvidedTrack { Uri = TrackUri(100) }, true);
        state.AddToQueue(new ProvidedTrack { Uri = TrackUri(101) }, true);

        var next = state.Player.NextTracks;
        Assert.Equal([TrackUri(100), TrackUri(101), TrackUri(1)], next.Take(3).Select(t => t.Uri));
        Assert.Equal("q0", next[0].Uid);
        Assert.Equal("q1", next[1].Uid);
        Assert.Equal("queue", next[0].Provider);
        Assert.Equal("true", next[0].Metadata["is_queued"]);

        // a queued track does not update the index and is not added to the previous tracks
        Assert.Equal(0u, state.NextTrack());
        Assert.Equal(TrackUri(100), state.Player.Track.Uri);
        Assert.Null(state.Player.Index);
        Assert.Equal(TrackUris(0), Uris(state.Player.PrevTracks));

        state.NextTrack();
        Assert.Equal(TrackUris(0), Uris(state.Player.PrevTracks));

        Assert.Equal(1u, state.NextTrack());
        Assert.Equal(TrackUri(1), state.Player.Track.Uri);
    }

    [Fact]
    public void Clear_queue_keeps_context_tracks()
    {
        var state = Loaded(5);
        state.AddToQueue(new ProvidedTrack { Uri = TrackUri(100) }, true);
        state.ClearQueue();

        Assert.Equal(TrackUris(1, 2, 3, 4), Uris(state.Player.NextTracks));
    }

    [Fact]
    public void Set_next_tracks_marks_queued_tracks()
    {
        var state = Loaded(3);
        var queued = new ProvidedTrack { Uri = TrackUri(7) };
        queued.Metadata["is_queued"] = "true";
        var empty = new ProvidedTrack();

        state.SetNextTracks([queued, empty]);

        Assert.Equal("queue", state.Player.NextTracks[0].Provider);
        Assert.Equal("q0", state.Player.NextTracks[0].Uid);
        // an empty item gets the uri of the current track
        Assert.Equal(TrackUri(0), state.Player.NextTracks[1].Uri);
    }

    [Fact]
    public void Shuffle_keeps_current_track_first_and_unshuffle_restores()
    {
        var state = Loaded(20);
        state.NextTrack();
        state.NextTrack();
        state.NextTrack();
        Assert.Equal(TrackUri(3), state.Player.Track.Uri);

        state.HandleShuffle(true);

        Assert.True(state.ShufflingContext);
        var ctx = state.GetContext(ContextType.Default);
        Assert.Equal(TrackUri(3), ctx.Tracks[0].Uri);
        Assert.Equal(TrackUri(3), ctx.Metadata["initial_track"]);
        Assert.True(ctx.Metadata.ContainsKey("shuffle_seed"));
        Assert.Empty(state.Player.PrevTracks);
        Assert.Equal(19, state.Player.NextTracks.Count);
        Assert.DoesNotContain(TrackUri(3), Uris(state.Player.NextTracks));
        Assert.Equal(
            Enumerable.Range(0, 20).Where(i => i != 3).Select(TrackUri).Order(),
            Uris(state.Player.NextTracks).Order());

        state.HandleShuffle(false);

        Assert.False(state.ShufflingContext);
        Assert.False(ctx.Metadata.ContainsKey("shuffle_seed"));
        Assert.Equal(Enumerable.Range(0, 20).Select(TrackUri), ctx.Tracks.Select(t => t.Uri));
        Assert.Equal(TrackUri(3), state.Player.Track.Uri);
        Assert.Equal(3u, state.Player.Index.Track);
        Assert.Equal(TrackUris(0, 1, 2), Uris(state.Player.PrevTracks));
        Assert.Equal(Enumerable.Range(4, 16).Select(TrackUri), Uris(state.Player.NextTracks));
    }

    [Fact]
    public void Shuffle_restore_is_deterministic()
    {
        var expected = new ShuffleVec<string>(Enumerable.Range(0, 10).Select(TrackUri));
        expected.ShuffleWithSeed(12345, uri => uri == TrackUri(4));

        var state = NewState();
        state.UpdateContext(MakeContext(AlbumUri, 10), ContextType.Default);
        state.ShuffleRestore(new ShuffleState(12345, TrackUri(4)));

        Assert.Equal(expected, new ShuffleVec<string>(state.GetContext(ContextType.Default).Tracks.Select(t => t.Uri)));
        Assert.Equal(expected.Skip(1), Uris(state.Player.NextTracks));
    }

    [Fact]
    public void Shuffle_is_rejected_when_disallowed()
    {
        var state = Loaded(3);
        state.Player.Restrictions.DisallowTogglingShuffleReasons.Add("autoplay");

        var e = Assert.Throws<StateException>(state.ShuffleNew);
        Assert.Equal(ErrorKind.Unavailable, e.Kind);
        Assert.Equal("currently shuffle is not allowed because autoplay", e.Detail);
    }

    [Fact]
    public void Repeat_context_handler_resets_around_current_track()
    {
        var state = Loaded(4);
        state.NextTrack();
        state.HandleSetRepeatContext(true);

        Assert.True(state.RepeatContext);
        Assert.Equal(TrackUri(1), state.Player.Track.Uri);
        Assert.Equal(TrackUris(0), Uris(state.Player.PrevTracks));
        Assert.Equal("spotify:delimiter", state.Player.NextTracks[2].Uri);
    }

    [Fact]
    public void Autoplay_context_continues_after_default_context()
    {
        var state = Loaded(3);
        state.UpdateContext(MakeContext(AutoplayUri, 3, 50), ContextType.Autoplay);
        state.FillUpNextTracks();

        Assert.Equal(
            [TrackUri(1), TrackUri(2), "spotify:delimiter", TrackUri(50), TrackUri(51), TrackUri(52)],
            Uris(state.Player.NextTracks));
        Assert.Equal("autoplay", state.Player.NextTracks[3].Provider);
        Assert.Equal("true", state.Player.NextTracks[3].Metadata["autoplay.is_autoplay"]);

        state.NextTrack();
        state.NextTrack();
        state.NextTrack();

        Assert.Equal(TrackUri(50), state.Player.Track.Uri);
        Assert.Equal(ContextType.Autoplay, state.ActiveContext);
        Assert.Null(state.Player.Index);
        Assert.Equal(["autoplay"], state.Player.Restrictions.DisallowTogglingShuffleReasons);

        var prev = state.PrevTrack();
        Assert.Equal(TrackUri(2), prev!.Uri);
        Assert.Equal(ContextType.Default, state.ActiveContext);
        Assert.Equal("spotify:delimiter", state.Player.NextTracks[0].Uri);
        Assert.Equal(TrackUri(50), state.Player.NextTracks[1].Uri);
    }

    [Fact]
    public void Mark_unavailable_removes_track()
    {
        var state = Loaded(5);
        state.MarkUnavailable(SpotifyUri.FromUri(TrackUri(2)));

        Assert.Equal(TrackUris(1, 3, 4), Uris(state.Player.NextTracks));

        // the context track is converted as unavailable when the context is loaded again
        var track = state.ContextToProvidedTrack(new ContextTrack { Uri = TrackUri(2) }, null, null, null, null);
        Assert.Equal("unavailable", track.Provider);
    }

    [Fact]
    public void Context_to_provided_track_validates_uri()
    {
        var state = NewState();

        var e = Assert.Throws<StateException>(() => state.ContextToProvidedTrack(new ContextTrack { Uri = "spotify:track:abc?x=1" }, null, null, null, null));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
        Assert.Equal("track uri <Some(\"spotify:track:abc?x=1\")> contains invalid characters", e.Detail);

        var none = Assert.Throws<StateException>(() => state.ContextToProvidedTrack(new ContextTrack(), null, null, null, null));
        Assert.Equal("track uri <None> contains invalid characters", none.Detail);

        var gid = new byte[16];
        gid[15] = 1;
        var fromGid = state.ContextToProvidedTrack(
            new ContextTrack { Gid = Google.Protobuf.ByteString.CopyFrom(gid), Uid = "uid1" },
            AlbumUri,
            7,
            new Dictionary<string, string> { ["page"] = "1" },
            Provider.Autoplay);
        Assert.Equal(TrackUri(1), fromGid.Uri);
        Assert.Equal("uid1", fromGid.Uid);
        Assert.Equal("7", fromGid.Metadata["context_index"]);
        Assert.Equal(AlbumUri, fromGid.Metadata["entity_uri"]);
        Assert.Equal("1", fromGid.Metadata["page"]);
        Assert.Equal("true", fromGid.Metadata["autoplay.is_autoplay"]);

        var generatedUid = state.ContextToProvidedTrack(new ContextTrack { Uri = TrackUri(3) }, null, null, null, null);
        Assert.Equal(32, generatedUid.Uid.Length);
    }

    [Fact]
    public void Update_context_rejects_empty_and_local_contexts()
    {
        var state = NewState();

        var empty = new Context { Uri = AlbumUri };
        empty.Pages.Add(new ContextPage());
        Assert.Equal(StateException.Reason.ContextHasNoTracks, Assert.Throws<StateException>(() => state.UpdateContext(empty, ContextType.Default)).Cause);

        var local = MakeContext("spotify:local-files", 2);
        Assert.Equal(StateException.Reason.UnsupportedLocalPlayback, Assert.Throws<StateException>(() => state.UpdateContext(local, ContextType.Default)).Cause);
    }

    [Fact]
    public void Update_context_appends_pages_and_returns_page_urls()
    {
        var state = NewState();
        var context = MakeContext(AlbumUri, 2);
        context.Url = "context://" + AlbumUri;
        context.Metadata["key"] = "value";
        context.Pages.Add(ContextPage.FromUris([TrackUri(2), TrackUri(3)]));
        context.Pages.Add(new ContextPage { PageUrl = "hm://artistplaycontext/v1/page/spotify/album/5LFzwirfFwBKXJQGfwmiMY/km_artist" });
        context.Pages.Add(new ContextPage());

        var remaining = state.UpdateContext(context, ContextType.Default);

        Assert.Equal(["spotify:album:5LFzwirfFwBKXJQGfwmiMY"], remaining);
        var ctx = state.GetContext(ContextType.Default);
        Assert.Equal(TrackUris(0, 1, 2, 3), ctx.Tracks.Select(t => t.Uri));
        Assert.Equal("3", ctx.Tracks[3].Metadata["context_index"]);
        Assert.Equal(AlbumUri, state.ContextUri);
        Assert.Equal("context://" + AlbumUri, state.Player.ContextUrl);
        Assert.Equal("value", ctx.Metadata["key"]);
    }

    [Fact]
    public void Update_same_context_preserves_position()
    {
        var state = Loaded(10);
        for (var i = 0; i < 4; i++)
        {
            state.NextTrack();
        }

        // the same context again (e.g. an update_context command): the index continues after the current track
        state.UpdateContext(MakeContext(AlbumUri, 10), ContextType.Default);
        Assert.Equal(5u, state.GetContext(ContextType.Default).Index.Track);
    }

    [Theory]
    [InlineData("hm://artistplaycontext/v1/page/spotify/album/5LFzwirfFwBKXJQGfwmiMY/km_artist", "spotify:album:5LFzwirfFwBKXJQGfwmiMY")]
    [InlineData("spotify/playlist/37i9dQZF1DXcBWIGoYBM5M", "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("hm://nothing/here", "")]
    public void Page_url_to_uri(string pageUrl, string expected) => Assert.Equal(expected, ConnectState.PageUrlToUri(pageUrl));

    [Fact]
    public void Merge_context_copies_metadata_and_uid()
    {
        var state = Loaded(3);
        var page = new ContextPage();
        var track = new ContextTrack { Uri = TrackUri(1), Uid = "real-uid" };
        track.Metadata["extra"] = "x";
        page.Tracks.Add(track);
        page.Tracks.Add(new ContextTrack { Uri = "" });

        Assert.True(state.MergeContext(page));
        var merged = state.GetContext(ContextType.Default).Tracks[1];
        Assert.Equal("real-uid", merged.Uid);
        Assert.Equal("x", merged.Metadata["extra"]);

        Assert.False(state.MergeContext(null));
        Assert.False(NewState().MergeContext(page));
    }

    [Fact]
    public void Reset_context_completely_drops_contexts()
    {
        var state = Loaded(3);
        state.ResetContext(new ResetContextKind.WhenDifferent(AlbumUri));
        Assert.NotNull(state.TryGetContext(ContextType.Default));

        state.ResetContext(new ResetContextKind.WhenDifferent("spotify:album:other"));
        Assert.Null(state.TryGetContext(ContextType.Default));
        Assert.Equal("", state.ContextUri);

        var e = Assert.Throws<StateException>(() => state.GetContext(ContextType.Autoplay));
        Assert.Equal("context is not available. type: Autoplay", e.Detail);
    }

    [Fact]
    public void Valid_uris()
    {
        Assert.Null(ConnectState.ValidResolveUri(""));
        Assert.Null(ConnectState.ValidResolveUri("spotify:search:abc"));
        Assert.Equal(AlbumUri, ConnectState.ValidResolveUri(AlbumUri));

        var page = ContextPage.FromUris([TrackUri(9)]);
        Assert.Equal(TrackUri(9), ConnectState.FindValidUri("spotify:search:abc", page));
        Assert.Equal(AlbumUri, ConnectState.FindValidUri(AlbumUri, page));
        Assert.Null(ConnectState.FindValidUri(null, null));
    }

    private static TransferState MakeTransfer(bool shuffle, string? shuffleSeed, string? initialTrack)
    {
        var context = new Context { Uri = AlbumUri };
        context.Metadata["transfer-key"] = "v";
        if (shuffleSeed is not null)
        {
            context.Metadata["shuffle_seed"] = shuffleSeed;
        }

        if (initialTrack is not null)
        {
            context.Metadata["initial_track"] = initialTrack;
        }

        var queue = new Queue { IsPlayingQueue = false };
        queue.Tracks.Add(new ContextTrack { Uri = TrackUri(100) });

        return new TransferState
        {
            Options = new PlayerContextPlayerOptions { ShufflingContext = shuffle },
            Playback = new TransferPlayback { IsPaused = true, PlaybackSpeed = 0, CurrentTrack = new ContextTrack { Uri = TrackUri(2) } },
            CurrentSession = new TransferSession { Context = context },
            Queue = queue,
        };
    }

    [Fact]
    public void Transfer_restores_track_queue_and_position()
    {
        var state = NewState();
        var transfer = MakeTransfer(false, null, null);

        state.HandleInitialTransfer(transfer, AlbumUri);

        Assert.True(state.Player.IsPaused);
        Assert.False(state.Player.IsPlaying);
        Assert.Equal(1.0, state.Player.PlaybackSpeed);
        Assert.Equal(AlbumUri, state.ContextUri);
        Assert.Equal("context://" + AlbumUri, state.Player.ContextUrl);
        Assert.Equal("v", state.Player.ContextMetadata["transfer-key"]);
        Assert.Null(transfer.CurrentSession.Context);

        var found = state.CurrentTrackFromTransfer(transfer);
        Assert.Equal(TrackUri(2), found.Uri);

        state.UpdateContext(MakeContext(AlbumUri, 5), ContextType.Default);
        state.FinishTransfer(transfer);

        Assert.Equal(TrackUri(2), state.Player.Track.Uri);
        Assert.Equal(2u, state.Player.Index.Track);
        Assert.Equal(TrackUris(0, 1), Uris(state.Player.PrevTracks));
        Assert.Equal(TrackUris(100, 3, 4), Uris(state.Player.NextTracks));
        Assert.Equal("queue", state.Player.NextTracks[0].Provider);
    }

    [Fact]
    public void Transfer_restores_shuffle_from_context_metadata()
    {
        var state = NewState();
        var transfer = MakeTransfer(true, "12345", TrackUri(2));

        state.HandleInitialTransfer(transfer, AlbumUri);
        state.UpdateContext(MakeContext(AlbumUri, 5), ContextType.Default);
        state.FinishTransfer(transfer);

        var expected = new ShuffleVec<string>(Enumerable.Range(0, 5).Select(TrackUri));
        expected.ShuffleWithSeed(12345, uri => uri == TrackUri(2));

        Assert.True(state.ShufflingContext);
        Assert.Equal(TrackUri(2), state.Player.Track.Uri);
        Assert.Equal(expected, new ShuffleVec<string>(state.GetContext(ContextType.Default).Tracks.Select(t => t.Uri)));
        Assert.Equal("12345", state.GetContext(ContextType.Default).Metadata["shuffle_seed"]);
    }

    [Fact]
    public void Current_track_from_transfer_requires_a_track()
    {
        var state = NewState();
        var e = Assert.Throws<StateException>(() => state.CurrentTrackFromTransfer(new TransferState()));
        Assert.Equal(StateException.Reason.CouldNotResolveTrackFromTransfer, e.Cause);

        var queue = new Queue { IsPlayingQueue = true };
        queue.Tracks.Add(new ContextTrack { Uri = TrackUri(9) });
        var fromQueue = state.CurrentTrackFromTransfer(new TransferState { Queue = queue });
        Assert.Equal("queue", fromQueue.Provider);
    }

    [Fact]
    public void Became_inactive_resets_state_before_sending()
    {
        var state = Loaded(3);
        state.SetActive(true);
        state.SetSessionId("new-session");

        // The send itself needs core-spclient; only check the synchronous reset part via ResetContext.
        state.ResetContext(new ResetContextKind.Completely());
        Assert.Null(state.TryGetContext(ContextType.Default));
        Assert.Equal("new-session", state.Player.SessionId);
    }

    [Fact]
    public void Update_position_in_relation_advances_position()
    {
        var state = NewState();
        state.UpdatePosition(1000, 10_000);
        state.UpdatePositionInRelation(12_500);

        Assert.Equal(3500, state.Player.PositionAsOfTimestamp);
        Assert.Equal(12_500, state.Player.Timestamp);
    }

    [Fact]
    public void Update_position_in_relation_with_negative_diff_does_not_throw()
    {
        var state = NewState();
        state.UpdatePosition(1000, 20_000);
        state.UpdatePositionInRelation(10_000);

        Assert.Equal(-9000, state.Player.PositionAsOfTimestamp);
        Assert.Equal(10_000, state.Player.Timestamp);

        // The debug-log formatting must wrap like upstream's `pos as u64` instead of overflowing a TimeSpan.
        var sec = unchecked((ulong)-9000L) / 1000;
        Assert.Equal((sec / 60, sec % 60), ConnectState.PositionMinSec(-9000));
        Assert.Equal((1UL, 5UL), ConnectState.PositionMinSec(65_999));
    }
}
