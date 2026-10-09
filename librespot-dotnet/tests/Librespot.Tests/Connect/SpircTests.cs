using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Librespot.Connect;
using Librespot.Core;
using Librespot.Core.Dealer;
using Librespot.Playback;
using Librespot.Playback.Mixer;
using Xunit;
using ClusterUpdate = Spotify.Connectstate.ClusterUpdate;
using Context = Spotify.Player.Proto.Context;
using ContextPage = Spotify.Player.Proto.ContextPage;
using ContextPlayerOptionOverrides = Spotify.Player.Proto.ContextPlayerOptionOverrides;
using LogoutCommand = Spotify.Connectstate.LogoutCommand;
using PlaylistModificationInfo = Spotify.Playlist4.Proto.PlaylistModificationInfo;
using SessionUpdate = Socialconnect.SessionUpdate;
using SetVolumeCommand = Spotify.Connectstate.SetVolumeCommand;
using SocialSession = Socialconnect.Session;
using UserAttributesMutation = Spotify.UserAttributes.Proto.UserAttributesMutation;
using UserAttributesUpdate = Spotify.ExplicitContent.Proto.UserAttributesUpdate;

namespace Librespot.Tests.Connect;

/// <summary>
/// Tests for connect/src/spirc.rs. Upstream has no unit tests; these cover the pieces that run without a network:
/// volume handling, the play command conversion, request routing with a fake dealer reply channel and the player
/// event state machine with a recording player.
/// </summary>
public class SpircTests
{
    private static string TrackUri(int i) => $"spotify:track:{i.ToString("D22", CultureInfo.InvariantCulture)}";

    private sealed class FakeMixer : IMixer
    {
        public ushort Volume { get; private set; }

        public void SetVolume(ushort volume) => Volume = volume;
    }

    /// <summary>Records every player call as a short string.</summary>
    private sealed class RecordingPlayer : ISpircPlayer
    {
        public List<string> Calls { get; } = [];

        public void Load(SpotifyUri trackId, bool startPlaying, uint positionMs) =>
            Calls.Add($"load {trackId.ToUri()} {startPlaying} {positionMs}");

        public void Preload(SpotifyUri trackId) => Calls.Add($"preload {trackId.ToUri()}");

        public void Play() => Calls.Add("play");

        public void Pause() => Calls.Add("pause");

        public void Stop() => Calls.Add("stop");

        public void Seek(uint positionMs) => Calls.Add($"seek {positionMs}");

        public void EmitVolumeChangedEvent(ushort volume) => Calls.Add($"volume {volume}");

        public void EmitFilterExplicitContentChangedEvent(bool filter) => Calls.Add($"filter {filter}");

        public void EmitSessionConnectedEvent(string connectionId, string userName) => Calls.Add("connected");

        public void EmitSessionDisconnectedEvent(string connectionId, string userName) => Calls.Add("disconnected");

        public void EmitSessionClientChangedEvent(string clientId, string clientName, string clientBrandName, string clientModelName) =>
            Calls.Add("client");

        public void EmitShuffleChangedEvent(bool shuffle) => Calls.Add($"shuffle {shuffle}");

        public void EmitRepeatChangedEvent(bool context, bool track) => Calls.Add($"repeat {context} {track}");

        public void EmitAutoPlayChangedEvent(bool autoPlay) => Calls.Add($"autoplay {autoPlay}");

        public void EmitSetQueueEvent(string contextUri, QueueTrack? currentTrack, IReadOnlyList<QueueTrack> nextTracks, IReadOnlyList<QueueTrack> prevTracks) =>
            Calls.Add($"set_queue {contextUri} {currentTrack?.Uri} {nextTracks.Count} {prevTracks.Count}");
    }

    private static IAsyncEnumerator<Result<T>> Empty<T>() =>
        Channel.CreateUnbounded<Result<T>>().Reader.ReadAllAsync().GetAsyncEnumerator();

    private static SpircStreams EmptyStreams() => new(
        Empty<string>(),
        Empty<ClusterUpdate>(),
        Empty<SetVolumeCommand>(),
        Empty<LogoutCommand>(),
        Empty<PlaylistModificationInfo>(),
        Empty<FallbackWrapper<SessionUpdate>>(),
        Channel.CreateUnbounded<RequestReply>().Reader,
        Empty<UserAttributesUpdate>(),
        Empty<UserAttributesMutation>());

    /// <summary>Channel-backed volume and request streams (the others stay empty) to drive the select loop.</summary>
    private sealed class LoopChannels
    {
        public Channel<Result<SetVolumeCommand>> Volume { get; } = Channel.CreateUnbounded<Result<SetVolumeCommand>>();

        public Channel<RequestReply> Requests { get; } = Channel.CreateUnbounded<RequestReply>();

        public SpircStreams Streams() => new(
            Empty<string>(),
            Empty<ClusterUpdate>(),
            Volume.Reader.ReadAllAsync().GetAsyncEnumerator(),
            Empty<LogoutCommand>(),
            Empty<PlaylistModificationInfo>(),
            Empty<FallbackWrapper<SessionUpdate>>(),
            Requests.Reader,
            Empty<UserAttributesUpdate>(),
            Empty<UserAttributesMutation>());
    }

    private sealed record Harness(SpircTask Task, Session Session, RecordingPlayer Player, FakeMixer Mixer);

    private static Harness NewTask(
        ConnectConfig? config = null,
        bool emitSetQueueEvents = false,
        SpircStreams? streams = null,
        SessionConfig? sessionConfig = null)
    {
        var session = new Session(sessionConfig ?? SessionConfig.Default(), null);
        var player = new RecordingPlayer();
        var mixer = new FakeMixer();
        var state = ConnectState.New(config ?? new ConnectConfig(), session);
        var task = new SpircTask(
            0,
            session,
            player,
            mixer,
            state,
            emitSetQueueEvents,
            streams ?? EmptyStreams(),
            Channel.CreateUnbounded<SpircCommand>(),
            null);
        return new Harness(task, session, player, mixer);
    }

    private static async Task<Reply> SendRequestAsync(SpircTask task, Command command)
    {
        var replies = Channel.CreateUnbounded<Reply>();
        var request = new Request { MessageId = 7, SentByDeviceId = "remote", Command = command };
        await task.HandleConnectStateRequestAsync(new RequestReply(request, replies.Writer));
        Assert.True(replies.Reader.TryRead(out var reply));
        return reply;
    }

    private static Task LoadTracksAsync(SpircTask task, int count, bool startPlaying = true, PlayingTrack? playingTrack = null) =>
        task.HandleLoadAsync(
            LoadRequest.FromTracks(
                Enumerable.Range(0, count).Select(TrackUri).ToList(),
                new LoadRequestOptions { StartPlaying = startPlaying, PlayingTrack = playingTrack }),
            null,
            null);

    // ---- dealer helpers -------------------------------------------------------------------------------------------

    [Fact]
    public void Extract_connection_id_reads_the_header()
    {
        var message = new Message
        {
            Uri = "hm://pusher/v1/connections/abc",
            Headers = new Dictionary<string, string> { ["Spotify-Connection-Id"] = "connection-id" },
        };

        Assert.Equal("connection-id", Spirc.ExtractConnectionId(message));
    }

    [Fact]
    public void Extract_connection_id_without_header_is_invalid_uri()
    {
        var message = new Message { Uri = "hm://pusher/v1/connections/abc" };

        var e = Assert.Throws<SpircException>(() => Spirc.ExtractConnectionId(message));
        Assert.Equal(SpircException.Reason.InvalidUri, e.Cause);
        Assert.Equal(ErrorKind.Aborted, e.Kind);
        Assert.Equal("hm://pusher/v1/connections/abc", e.Uri);
        Assert.Equal("message pushed for another URI", e.Detail);
    }

    [Fact]
    public void Spirc_error_kinds_match_upstream()
    {
        Assert.Equal(ErrorKind.Unavailable, SpircException.NoData().Kind);
        Assert.Equal("response payload empty", SpircException.NoData().Detail);
        Assert.Equal(ErrorKind.Unavailable, SpircException.NoUri("context").Kind);
        Assert.Equal("context had no uri", SpircException.NoUri("context").Detail);
        Assert.Equal(ErrorKind.Aborted, SpircException.FailedDealerSetup().Kind);
        Assert.Equal(ErrorKind.Unimplemented, SpircException.UnknownEndpoint(JsonDocument.Parse("{}").RootElement).Kind);
    }

    // ---- handle ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Handle_sends_commands_and_rejects_unsupported_queue_uris()
    {
        var channel = Channel.CreateUnbounded<SpircCommand>();
        var spirc = new Spirc(channel.Writer);

        spirc.VolumeUp();
        spirc.AddToQueue(SpotifyUri.FromUri(TrackUri(1)));
        var artist = SpotifyUri.FromUri("spotify:artist:4uLU6hMCjMI75M1A2tKUQC");
        var e = Assert.Throws<LibrespotException>(() => spirc.AddToQueue(artist));
        Assert.Equal(ErrorKind.InvalidArgument, e.Kind);

        Assert.True(channel.Reader.TryRead(out var first));
        Assert.IsType<SpircCommand.VolumeUp>(first);
        Assert.True(channel.Reader.TryRead(out var second));
        Assert.Equal(TrackUri(1), Assert.IsType<SpircCommand.AddToQueue>(second).Uri.ToUri());
        Assert.False(channel.Reader.TryRead(out _));

        channel.Writer.TryComplete();
        Assert.Equal(ErrorKind.Internal, Assert.Throws<LibrespotException>(spirc.Shutdown).Kind);
    }

    // ---- volume ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Saturating_volume_math()
    {
        Assert.Equal(ushort.MaxValue, SpircTask.SaturatingAdd(65000, 1023));
        Assert.Equal((ushort)2046, SpircTask.SaturatingAdd(1023, 1023));
        Assert.Equal((ushort)0, SpircTask.SaturatingSub(100, 1023));
        Assert.Equal((ushort)1023, SpircTask.SaturatingSub(2046, 1023));
    }

    [Fact]
    public void Initial_volume_is_applied_to_the_mixer_without_a_remote_update()
    {
        var h = NewTask(new ConnectConfig { InitialVolume = 12345 });

        h.Task.ApplyInitialVolume();

        Assert.Equal((ushort)12345, h.Mixer.Volume);
        Assert.Equal(12345u, h.Task.ConnectState.DeviceInfo.Volume);
        Assert.False(h.Task.UpdateVolume);
        // not active: no volume event
        Assert.Empty(h.Player.Calls);
    }

    [Fact]
    public void Set_volume_updates_mixer_and_schedules_remote_update()
    {
        var h = NewTask();
        h.Task.ApplyInitialVolume();

        h.Task.SetVolume(1000);
        Assert.Equal((ushort)1000, h.Mixer.Volume);
        Assert.Equal(1000u, h.Task.ConnectState.DeviceInfo.Volume);
        Assert.True(h.Task.UpdateVolume);

        h.Task.HandleActivate();
        h.Player.Calls.Clear();
        h.Task.SetVolume(2000);
        Assert.Equal(new[] { "volume 2000" }, h.Player.Calls);
    }

    [Fact]
    public void Volume_step_size_follows_volume_steps()
    {
        Assert.Equal((ushort)1023, NewTask(new ConnectConfig { VolumeSteps = 64 }).Task.ConnectState.VolumeStepSize);
        Assert.Equal((ushort)1024, NewTask(new ConnectConfig { VolumeSteps = 0 }).Task.ConnectState.VolumeStepSize);
    }

    // ---- play command conversion ----------------------------------------------------------------------------------

    [Fact]
    public void Playing_track_from_skip_to_prefers_uri_then_uid_then_index()
    {
        Assert.Equal(
            new PlayingTrack.Uri("u"),
            SpircTask.PlayingTrackFromSkipTo(new SkipTo { TrackUri = "u", TrackUid = "id", TrackIndex = 0 }));
        Assert.Equal(new PlayingTrack.Uid("id"), SpircTask.PlayingTrackFromSkipTo(new SkipTo { TrackUid = "id", TrackIndex = 3 }));
        Assert.Equal(new PlayingTrack.Index(3), SpircTask.PlayingTrackFromSkipTo(new SkipTo { TrackIndex = 3 }));
        Assert.Null(SpircTask.PlayingTrackFromSkipTo(new SkipTo()));
    }

    [Fact]
    public void Play_command_with_context_uri_becomes_uri_load_request()
    {
        var context = new Context { Uri = "spotify:album:0000000000000000000abcde" };
        context.Pages.Add(ContextPage.FromUris([TrackUri(1)]));
        context.Pages.Add(ContextPage.FromUris([TrackUri(2)]));
        var play = new PlayCommand
        {
            Context = context,
            Options = new PlayOptions
            {
                SeekTo = 1500,
                SkipTo = new SkipTo { TrackUid = "uid", TrackIndex = 4 },
                PlayerOptionsOverride = new ContextPlayerOptionOverrides { ShufflingContext = true },
            },
        };

        var (request, page, fallbackIndex) = SpircTask.LoadRequestFromPlay(play);

        Assert.Equal(new PlayContext.Uri("spotify:album:0000000000000000000abcde"), request.Context);
        Assert.True(request.Options.StartPlaying);
        Assert.Equal(1500u, request.Options.SeekTo);
        Assert.Equal(new PlayingTrack.Uid("uid"), request.Options.PlayingTrack);
        var options = Assert.IsType<LoadContextOptions.WithOptions>(request.Options.ContextOptions);
        Assert.True(options.Options.Shuffle);
        Assert.False(options.Options.Repeat);
        Assert.Equal(4, fallbackIndex);

        // the last page is popped from the command's context
        Assert.NotNull(page);
        Assert.Equal(TrackUri(2), page.Tracks[0].Uri);
        Assert.Single(context.Pages);
    }

    [Fact]
    public void Play_command_without_uri_loads_the_tracks_of_all_pages()
    {
        var context = new Context();
        context.Pages.Add(ContextPage.FromUris([TrackUri(1), TrackUri(2)]));
        context.Pages.Add(ContextPage.FromUris([TrackUri(3)]));

        var (request, _, fallbackIndex) = SpircTask.LoadRequestFromPlay(new PlayCommand { Context = context });

        var tracks = Assert.IsType<PlayContext.Tracks>(request.Context);
        Assert.Equal(new[] { TrackUri(1), TrackUri(2), TrackUri(3) }, tracks.Uris);
        Assert.Equal(0u, request.Options.SeekTo);
        Assert.Null(request.Options.PlayingTrack);
        Assert.Null(request.Options.ContextOptions);
        Assert.Null(fallbackIndex);
    }

    [Fact]
    public void Play_command_without_uri_or_pages_has_no_uri()
    {
        var e = Assert.Throws<SpircException>(() => SpircTask.LoadRequestFromPlay(new PlayCommand { Context = new Context() }));
        Assert.Equal(SpircException.Reason.NoUri, e.Cause);
        Assert.Equal("context had no uri", e.Detail);
    }

    // ---- load -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Load_tracks_starts_the_requested_index()
    {
        var h = NewTask(emitSetQueueEvents: true);

        await LoadTracksAsync(h.Task, 3, playingTrack: new PlayingTrack.Index(1));

        Assert.Contains($"load {TrackUri(1)} True 0", h.Player.Calls);
        Assert.Contains(h.Player.Calls, c => c.StartsWith("set_queue spotify:web-api", StringComparison.Ordinal));
        Assert.Equal(new SpircPlayStatus.LoadingPlay(0), h.Task.PlayStatus);
        Assert.Equal("spotify:web-api", h.Task.ConnectState.ContextUri);
        Assert.Equal(TrackUri(1), h.Task.ConnectState.CurrentTrack(t => t.Uri));
    }

    [Fact]
    public async Task Load_tracks_with_unknown_uri_uses_the_first_track()
    {
        var h = NewTask();

        await LoadTracksAsync(h.Task, 2, startPlaying: false, playingTrack: new PlayingTrack.Uri(TrackUri(9)));

        Assert.Equal(new[] { $"load {TrackUri(0)} False 0" }, h.Player.Calls);
        Assert.Equal(new SpircPlayStatus.LoadingPause(0), h.Task.PlayStatus);
    }

    // ---- requests -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pause_request_pauses_playback_and_replies_success()
    {
        var h = NewTask();
        h.Task.PlayStatus = new SpircPlayStatus.Playing(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000, true);

        var reply = await SendRequestAsync(h.Task, new Command.Pause(new PauseCommand()));

        Assert.Equal(Reply.Success, reply);
        Assert.Equal(new[] { "pause" }, h.Player.Calls);
        var paused = Assert.IsType<SpircPlayStatus.Paused>(h.Task.PlayStatus);
        Assert.InRange(paused.PositionMs, 4900u, 6000u);
        Assert.True(paused.PreloadingOfNextTrackTriggered);
        Assert.True(h.Task.UpdateState);
        Assert.Equal(7u, h.Task.ConnectState.Request.LastCommandMessageId);
        Assert.Equal("remote", h.Task.ConnectState.Request.LastCommandSentByDeviceId);
    }

    [Fact]
    public async Task Unknown_and_empty_transfer_requests_reply_failure()
    {
        var h = NewTask();

        Assert.Equal(Reply.Failure, await SendRequestAsync(h.Task, new Command.Unknown(JsonDocument.Parse("{\"endpoint\":\"x\"}").RootElement)));
        Assert.Equal(Reply.Failure, await SendRequestAsync(h.Task, new Command.Transfer(new TransferCommand())));
        Assert.False(h.Task.UpdateState);
    }

    [Fact]
    public async Task Play_request_with_tracks_activates_and_loads()
    {
        var h = NewTask();
        var context = new Context();
        context.Pages.Add(ContextPage.FromUris([TrackUri(1), TrackUri(2)]));

        var reply = await SendRequestAsync(h.Task, new Command.Play(new PlayCommand
        {
            Context = context,
            PlayOrigin = new Spotify.Connectstate.PlayOrigin { FeatureIdentifier = "test" },
        }));

        Assert.Equal(Reply.Success, reply);
        Assert.True(h.Task.ConnectState.IsActive);
        Assert.Contains("connected", h.Player.Calls);
        Assert.Contains($"load {TrackUri(1)} True 0", h.Player.Calls);
        Assert.Equal("test", h.Task.ConnectState.Player.PlayOrigin.FeatureIdentifier);
        Assert.True(h.Task.UpdateState);
    }

    [Fact]
    public async Task Update_context_for_another_context_is_ignored()
    {
        var h = NewTask();

        await SendRequestAsync(h.Task, new Command.UpdateContext(new UpdateContextCommand
        {
            Context = new Context { Uri = "spotify:album:0000000000000000000abcde" },
        }));
        Assert.False(h.Task.ContextResolver.HasNext);

        var context = new Context();
        context.Pages.Add(ContextPage.FromUris([TrackUri(1)]));
        await SendRequestAsync(h.Task, new Command.UpdateContext(new UpdateContextCommand { Context = context }));
        Assert.True(h.Task.ContextResolver.HasNext);

        // update_context does not schedule a state update
        Assert.False(h.Task.UpdateState);
    }

    [Fact]
    public async Task Set_options_request_updates_repeat_and_shuffle()
    {
        var h = NewTask();
        await LoadTracksAsync(h.Task, 3);
        h.Player.Calls.Clear();

        var reply = await SendRequestAsync(h.Task, new Command.SetOptions(new SetOptionsCommand { RepeatingTrack = true }));

        Assert.Equal(Reply.Success, reply);
        Assert.True(h.Task.ConnectState.RepeatTrack);
        Assert.Equal(new[] { "repeat False True" }, h.Player.Calls);
    }

    // ---- player events --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Player_events_of_another_request_are_ignored()
    {
        var h = NewTask();
        await LoadTracksAsync(h.Task, 2);
        var track = SpotifyUri.FromUri(TrackUri(0));

        h.Task.HandlePlayerEvent(new PlayerEvent.PlayRequestIdChanged(5));
        h.Task.HandlePlayerEvent(new PlayerEvent.Playing(4, track, 1000));

        Assert.Equal(new SpircPlayStatus.LoadingPlay(0), h.Task.PlayStatus);
        Assert.False(h.Task.UpdateState);

        h.Task.HandlePlayerEvent(new PlayerEvent.Playing(5, track, 1000));

        Assert.IsType<SpircPlayStatus.Playing>(h.Task.PlayStatus);
        Assert.Equal(1000, h.Task.ConnectState.Player.PositionAsOfTimestamp);
        Assert.True(h.Task.UpdateState);
    }

    [Fact]
    public async Task End_of_track_loads_the_next_track()
    {
        var h = NewTask();
        await LoadTracksAsync(h.Task, 3);
        h.Task.HandlePlayerEvent(new PlayerEvent.PlayRequestIdChanged(1));
        h.Player.Calls.Clear();

        h.Task.HandlePlayerEvent(new PlayerEvent.EndOfTrack(1, SpotifyUri.FromUri(TrackUri(0))));

        Assert.Equal(new[] { $"load {TrackUri(1)} True 0" }, h.Player.Calls);
        Assert.Equal(TrackUri(1), h.Task.ConnectState.CurrentTrack(t => t.Uri));
    }

    [Fact]
    public async Task End_of_track_while_repeating_track_reloads_the_track()
    {
        var h = NewTask();
        await LoadTracksAsync(h.Task, 3);
        h.Task.ConnectState.SetRepeatTrack(true);
        h.Task.HandlePlayerEvent(new PlayerEvent.PlayRequestIdChanged(1));
        h.Player.Calls.Clear();

        h.Task.HandlePlayerEvent(new PlayerEvent.EndOfTrack(1, SpotifyUri.FromUri(TrackUri(0))));

        Assert.Equal(new[] { $"load {TrackUri(0)} True 0" }, h.Player.Calls);
    }

    [Fact]
    public async Task Time_to_preload_preloads_the_next_track()
    {
        var h = NewTask();
        await LoadTracksAsync(h.Task, 3);
        h.Task.HandlePlayerEvent(new PlayerEvent.PlayRequestIdChanged(1));
        h.Task.HandlePlayerEvent(new PlayerEvent.Playing(1, SpotifyUri.FromUri(TrackUri(0)), 0));
        h.Player.Calls.Clear();

        h.Task.HandlePlayerEvent(new PlayerEvent.TimeToPreloadNextTrack(1, SpotifyUri.FromUri(TrackUri(0))));

        Assert.Equal(new[] { $"preload {TrackUri(1)}" }, h.Player.Calls);
        Assert.True(Assert.IsType<SpircPlayStatus.Playing>(h.Task.PlayStatus).PreloadingOfNextTrackTriggered);
    }

    // ---- session and attribute updates ----------------------------------------------------------------------------

    [Fact]
    public void Session_update_for_this_device_adopts_the_session_id()
    {
        var h = NewTask();
        var update = new SessionUpdate
        {
            Session = new SocialSession { SessionId = "new-session", HostActiveDeviceId = h.Session.DeviceId },
        };

        h.Task.HandleSessionUpdate(new FallbackWrapper<SessionUpdate>.Inner(update));

        Assert.Equal("new-session", h.Session.SessionId);
        Assert.Equal("new-session", h.Task.ConnectState.Player.SessionId);
    }

    [Fact]
    public void Session_update_for_another_device_and_fallbacks_are_ignored()
    {
        var h = NewTask();
        var sessionId = h.Session.SessionId;

        h.Task.HandleSessionUpdate(new FallbackWrapper<SessionUpdate>.Inner(new SessionUpdate
        {
            Session = new SocialSession { SessionId = "other", HostActiveDeviceId = "other-device" },
        }));
        h.Task.HandleSessionUpdate(new FallbackWrapper<SessionUpdate>.Fallback(
            JsonDocument.Parse("{\"reason\":\"WIFI_BROADCAST_CHANGED\"}").RootElement));

        Assert.Equal(sessionId, h.Session.SessionId);
    }

    [Fact]
    public void Autoplay_mutation_toggles_the_attribute_and_emits_an_event()
    {
        var h = NewTask();
        h.Session.SetUserAttributes(new UserAttributes { ["autoplay"] = "0", ["filter-explicit-content"] = "0" });

        var mutation = new UserAttributesMutation();
        mutation.Fields.Add(new Spotify.UserAttributes.Proto.MutatedField { Name = "autoplay" });
        mutation.Fields.Add(new Spotify.UserAttributes.Proto.MutatedField { Name = "filter-explicit-content" });
        mutation.Fields.Add(new Spotify.UserAttributes.Proto.MutatedField { Name = "unknown" });
        h.Task.HandleUserAttributesMutation(mutation);

        Assert.Equal("1", h.Session.GetUserAttribute("autoplay"));
        Assert.Equal("1", h.Session.GetUserAttribute("filter-explicit-content"));
        Assert.Equal(new[] { "autoplay True", "filter True" }, h.Player.Calls);
    }

    // ---- select loop ----------------------------------------------------------------------------------------------

    private static readonly TimeSpan IterationTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Loop_volume_item_sets_the_volume_and_continues()
    {
        var channels = new LoopChannels();
        var h = NewTask(streams: channels.Streams());
        var sources = h.Task.CreateLoopSources();

        channels.Volume.Writer.TryWrite(Result<SetVolumeCommand>.Ok(new SetVolumeCommand { Volume = 1234 }));

        Assert.True(await h.Task.RunIterationAsync(sources).WaitAsync(IterationTimeout));
        Assert.Equal((ushort)1234, h.Mixer.Volume);
        Assert.True(h.Task.UpdateVolume);
    }

    [Fact]
    public async Task Loop_parse_error_is_logged_and_the_stream_is_polled_again()
    {
        var channels = new LoopChannels();
        var h = NewTask(streams: channels.Streams());
        var sources = h.Task.CreateLoopSources();

        channels.Volume.Writer.TryWrite(Result<SetVolumeCommand>.Err(LibrespotException.Internal("bad volume")));
        Assert.True(await h.Task.RunIterationAsync(sources).WaitAsync(IterationTimeout));
        Assert.Equal((ushort)0, h.Mixer.Volume);
        Assert.False(h.Task.UpdateVolume);

        // the same source keeps working in the next iteration
        channels.Volume.Writer.TryWrite(Result<SetVolumeCommand>.Ok(new SetVolumeCommand { Volume = 42 }));
        Assert.True(await h.Task.RunIterationAsync(sources).WaitAsync(IterationTimeout));
        Assert.Equal((ushort)42, h.Mixer.Volume);
    }

    [Fact]
    public async Task Loop_breaks_when_a_stream_ended()
    {
        var channels = new LoopChannels();
        var h = NewTask(streams: channels.Streams());
        var sources = h.Task.CreateLoopSources();

        channels.Volume.Writer.Complete();

        Assert.False(await h.Task.RunIterationAsync(sources).WaitAsync(IterationTimeout));
    }

    [Fact]
    public async Task Loop_answers_dealer_requests()
    {
        var channels = new LoopChannels();
        var h = NewTask(streams: channels.Streams());
        var sources = h.Task.CreateLoopSources();
        h.Task.PlayStatus = new SpircPlayStatus.Playing(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000, true);
        var replies = Channel.CreateUnbounded<Reply>();

        channels.Requests.Writer.TryWrite(new RequestReply(
            new Request { MessageId = 1, SentByDeviceId = "remote", Command = new Command.Pause(new PauseCommand()) },
            replies.Writer));
        Assert.True(await h.Task.RunIterationAsync(sources).WaitAsync(IterationTimeout));
        Assert.True(replies.Reader.TryRead(out var success));
        Assert.Equal(Reply.Success, success);
        Assert.Equal(new[] { "pause" }, h.Player.Calls);

        channels.Requests.Writer.TryWrite(new RequestReply(
            new Request
            {
                MessageId = 2,
                SentByDeviceId = "remote",
                Command = new Command.Unknown(JsonDocument.Parse("{\"endpoint\":\"x\"}").RootElement),
            },
            replies.Writer));
        Assert.True(await h.Task.RunIterationAsync(sources).WaitAsync(IterationTimeout));
        Assert.True(replies.Reader.TryRead(out var failure));
        Assert.Equal(Reply.Failure, failure);
    }

    [Fact]
    public async Task Loop_runs_the_delayed_volume_update_when_nothing_else_is_ready()
    {
        var channels = new LoopChannels();
        // the remote volume update fails fast: the proxy refuses the connection and there are no credentials
        var h = NewTask(
            streams: channels.Streams(),
            sessionConfig: SessionConfig.Default() with { Proxy = new Uri("http://127.0.0.1:1") });
        var sources = h.Task.CreateLoopSources();
        h.Task.SetVolume(1000);
        Assert.True(h.Task.UpdateVolume);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var iteration = h.Task.RunIterationAsync(sources);
        while (h.Task.UpdateVolume && !iteration.IsCompleted && started.Elapsed < IterationTimeout)
        {
            await Task.Delay(10);
        }

        var elapsed = started.Elapsed;
        Assert.False(h.Task.UpdateVolume);
        Assert.True(elapsed >= SpircTask.VolumeUpdateDelay - TimeSpan.FromMilliseconds(50), $"selected after {elapsed}");
        Assert.True(await iteration.WaitAsync(IterationTimeout));
    }

    [Fact]
    public async Task Loop_cancellation_breaks_but_ready_items_win()
    {
        var channels = new LoopChannels();
        var h = NewTask(streams: channels.Streams());
        var sources = h.Task.CreateLoopSources();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        channels.Volume.Writer.TryWrite(Result<SetVolumeCommand>.Ok(new SetVolumeCommand { Volume = 7 }));
        Assert.True(await h.Task.RunIterationAsync(sources, cancellation.Token).WaitAsync(IterationTimeout));
        Assert.Equal((ushort)7, h.Mixer.Volume);

        // the volume timer is enabled now, but the cancellation is ready first
        Assert.False(await h.Task.RunIterationAsync(sources, cancellation.Token).WaitAsync(IterationTimeout));
        Assert.True(h.Task.UpdateVolume);
    }
}
