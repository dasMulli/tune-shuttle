// Tests for src/player_event_handler.rs (PlayerEventHandler.cs): the environment variable table per event.
// Upstream has no unit tests for this file.
using System.Threading.Channels;
using Librespot.Cli;
using Librespot.Core;
using Librespot.Metadata;
using Librespot.Metadata.Audio;
using Librespot.Playback;
using Xunit;

namespace Librespot.Tests.Cli;

public class PlayerEventHandlerTests
{
    private const string TrackBase62 = "4uLU6hMCjMI75M1A2tKUQC";

    private static readonly SpotifyUri Track = new SpotifyUri.Track(SpotifyId.FromBase62(TrackBase62));

    private static AudioItem Item(UniqueFields uniqueFields) => new()
    {
        TrackId = Track,
        Uri = "spotify:track:" + TrackBase62,
        Files = new AudioFiles(),
        Name = "Song",
        Covers = [new CoverImage("https://i/1", default, 64, 64), new CoverImage("https://i/2", default, 640, 640)],
        Language = ["en", "de"],
        DurationMs = 215000,
        IsExplicit = true,
        UniqueFields = uniqueFields,
    };

    [Fact]
    public void PlaybackEvents()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "playing", ["TRACK_ID"] = TrackBase62, ["POSITION_MS"] = "1234" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.Playing(1, Track, 1234)));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "paused", ["TRACK_ID"] = TrackBase62, ["POSITION_MS"] = "0" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.Paused(1, Track, 0)));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "seeked", ["TRACK_ID"] = TrackBase62, ["POSITION_MS"] = "5" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.Seeked(1, Track, 5)));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "position_correction", ["TRACK_ID"] = TrackBase62, ["POSITION_MS"] = "7" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.PositionCorrection(1, Track, 7)));

        (PlayerEvent Event, string Name)[] trackOnly =
        [
            (new PlayerEvent.Stopped(1, Track), "stopped"),
            (new PlayerEvent.Loading(1, Track, 0), "loading"),
            (new PlayerEvent.Preloading(Track), "preloading"),
            (new PlayerEvent.TimeToPreloadNextTrack(1, Track), "preload_next"),
            (new PlayerEvent.EndOfTrack(1, Track), "end_of_track"),
            (new PlayerEvent.Unavailable(1, Track), "unavailable"),
        ];
        foreach (var (e, name) in trackOnly)
        {
            Assert.Equal(
                new Dictionary<string, string> { ["PLAYER_EVENT"] = name, ["TRACK_ID"] = TrackBase62 },
                PlayerEventHandler.GetEnvVars(e));
        }
    }

    [Fact]
    public void StateEvents()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "play_request_id_changed", ["PLAY_REQUEST_ID"] = "18446744073709551615" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.PlayRequestIdChanged(ulong.MaxValue)));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "volume_changed", ["VOLUME"] = "65535" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.VolumeChanged(65535)));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "shuffle_changed", ["SHUFFLE"] = "true" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.ShuffleChanged(true)));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "repeat_changed", ["REPEAT"] = "false", ["REPEAT_TRACK"] = "true" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.RepeatChanged(false, true)));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "auto_play_changed", ["AUTO_PLAY"] = "false" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.AutoPlayChanged(false)));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "filter_explicit_content_changed", ["FILTER"] = "true" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.FilterExplicitContentChanged(true)));
    }

    [Fact]
    public void SessionEvents()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "session_connected", ["CONNECTION_ID"] = "c", ["USER_NAME"] = "u" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.SessionConnected("c", "u")));
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "session_disconnected", ["CONNECTION_ID"] = "c", ["USER_NAME"] = "u" },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.SessionDisconnected("c", "u")));
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PLAYER_EVENT"] = "session_client_changed",
                ["CLIENT_ID"] = "id",
                ["CLIENT_NAME"] = "name",
                ["CLIENT_BRAND_NAME"] = "brand",
                ["CLIENT_MODEL_NAME"] = "model",
            },
            PlayerEventHandler.GetEnvVars(new PlayerEvent.SessionClientChanged("id", "name", "brand", "model")));
    }

    [Fact]
    public void SetQueue()
    {
        var withCurrent = PlayerEventHandler.GetEnvVars(new PlayerEvent.SetQueue(
            "spotify:album:x",
            new QueueTrack("spotify:track:a", "context"),
            [new QueueTrack("spotify:track:b", "queue"), new QueueTrack("spotify:track:c", "context")],
            []));
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PLAYER_EVENT"] = "set_queue",
                ["CONTEXT_URI"] = "spotify:album:x",
                ["CURRENT_TRACK"] = "spotify:track:a\tcontext",
                ["NEXT_TRACKS"] = "spotify:track:b\tqueue\nspotify:track:c\tcontext",
                ["PREV_TRACKS"] = "",
            },
            withCurrent);

        var withoutCurrent = PlayerEventHandler.GetEnvVars(new PlayerEvent.SetQueue("ctx", null, [], [new QueueTrack("u", "p")]));
        Assert.False(withoutCurrent.ContainsKey("CURRENT_TRACK"));
        Assert.Equal("u\tp", withoutCurrent["PREV_TRACKS"]);
    }

    [Fact]
    public void TrackChangedTrack()
    {
        var artists = new ArtistsWithRole(
        [
            new ArtistWithRole(new SpotifyUri.Artist(SpotifyId.FromBase62(TrackBase62)), "A1", default),
            new ArtistWithRole(new SpotifyUri.Artist(SpotifyId.FromBase62(TrackBase62)), "A2", default),
        ]);
        var env = PlayerEventHandler.GetEnvVars(new PlayerEvent.TrackChanged(
            Item(new UniqueFields.Track(artists, "Album", ["AA1", "AA2"], 42, 3, 1))));

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["PLAYER_EVENT"] = "track_changed",
                ["TRACK_ID"] = TrackBase62,
                ["URI"] = "spotify:track:" + TrackBase62,
                ["NAME"] = "Song",
                ["COVERS"] = "https://i/1\nhttps://i/2",
                ["LANGUAGE"] = "en\nde",
                ["DURATION_MS"] = "215000",
                ["IS_EXPLICIT"] = "true",
                ["ITEM_TYPE"] = "Track",
                ["ARTISTS"] = "A1\nA2",
                ["ALBUM_ARTISTS"] = "AA1\nAA2",
                ["ALBUM"] = "Album",
                ["POPULARITY"] = "42",
                ["NUMBER"] = "3",
                ["DISC_NUMBER"] = "1",
            },
            env);
    }

    [Fact]
    public void TrackChangedLocal()
    {
        var env = PlayerEventHandler.GetEnvVars(new PlayerEvent.TrackChanged(
            Item(new UniqueFields.Local("Artist", null, null, 7, null, "/music/a.mp3"))));

        Assert.Equal("Track", env["ITEM_TYPE"]);
        Assert.Equal("Artist", env["ARTISTS"]);
        Assert.Equal("", env["ALBUM_ARTISTS"]);
        Assert.Equal("", env["ALBUM"]);
        Assert.Equal("7", env["NUMBER"]);
        Assert.Equal("", env["DISC_NUMBER"]);
        Assert.Equal("/music/a.mp3", env["LOCAL_FILE_PATH"]);
        Assert.False(env.ContainsKey("POPULARITY"));
    }

    [Fact]
    public void TrackChangedEpisode()
    {
        var env = PlayerEventHandler.GetEnvVars(new PlayerEvent.TrackChanged(
            Item(new UniqueFields.Episode("Desc", Date.FromTimestampMs(1_700_000_000_999), "Show"))));

        Assert.Equal("Episode", env["ITEM_TYPE"]);
        Assert.Equal("Desc", env["DESCRIPTION"]);
        Assert.Equal("1700000000", env["PUBLISH_TIME"]);
        Assert.Equal("Show", env["SHOW_NAME"]);
        Assert.False(env.ContainsKey("ARTISTS"));
    }

    [Fact]
    public void IgnoredEvents()
    {
        Assert.Empty(PlayerEventHandler.GetEnvVars(new PlayerEvent.PositionChanged(1, Track, 1)));
    }

    [Fact]
    public void SinkEvents()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["PLAYER_EVENT"] = "sink", ["SINK_STATUS"] = "running" },
            PlayerEventHandler.GetSinkEnvVars(SinkStatus.Running));
        Assert.Equal("temporarily_closed", PlayerEventHandler.GetSinkEnvVars(SinkStatus.TemporarilyClosed)["SINK_STATUS"]);
        Assert.Equal("closed", PlayerEventHandler.GetSinkEnvVars(SinkStatus.Closed)["SINK_STATUS"]);
    }

    [Fact]
    public void RunProgramFailures()
    {
        // Upstream panics on `v.remove(0)` for an empty program.
        Assert.Throws<InvalidOperationException>(() => PlayerEventHandler.RunProgramOnSinkEvents(SinkStatus.Running, "   "));

        // A program that cannot be started is logged, not thrown.
        PlayerEventHandler.RunProgramOnSinkEvents(SinkStatus.Running, "librespot-no-such-program-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void DebugFormatting()
    {
        Assert.Equal("\"a\\\"b\\\\c\\nd\\te\\u{1}\"", PlayerEventHandler.RustDebugString("a\"b\\c\nd\te\u0001"));
        Assert.Equal(
            "{\n    \"PLAYER_EVENT\": \"sink\",\n    \"SINK_STATUS\": \"closed\",\n}",
            PlayerEventHandler.FormatEnvVars(PlayerEventHandler.GetSinkEnvVars(SinkStatus.Closed)));
    }

    [Fact]
    public async Task ThreadEndsWhenChannelCompletes()
    {
        var channel = Channel.CreateUnbounded<PlayerEvent>();
        var handler = new PlayerEventHandler(new PlayerEventChannel(channel.Reader, channel.Writer), "librespot-unused-program");

        // Ignored events do not run the program.
        Assert.True(channel.Writer.TryWrite(new PlayerEvent.PositionChanged(1, Track, 1)));
        channel.Writer.TryComplete();

        var dispose = Task.Run(handler.Dispose);
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
