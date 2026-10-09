using System.Collections.Concurrent;
using System.Threading.Channels;
using Bridge.Service.Output;
using Bridge.Service.Spotify;
using Librespot.Core;
using Librespot.Metadata;
using Librespot.Metadata.Audio;
using Librespot.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ArtistRole = Spotify.Metadata.ArtistWithRole.Types.ArtistRole;

namespace Bridge.Tests.Service;

/// <summary>
/// The SPEC-audio.md 8.4 mapping with a stubbed librespot event stream (WORK-PLAN.md bridge-service "Verify", no Spotify
/// login): which events flush, when the now-playing goes out and with which position, how the volume becomes G.
/// </summary>
public sealed class PlayerEventMapperTests
{
    private static readonly SpotifyUri TrackA = SpotifyUri.FromUri("spotify:track:4uLU6hMCjMI75M1A2tKUQC");
    private static readonly SpotifyUri TrackB = SpotifyUri.FromUri("spotify:track:7GhIk7Il098yCjg4BQjzvb");

    [Fact]
    public async Task Track_start_sends_text_artwork_and_position_without_a_flush()
    {
        var output = new RecordingOutput();
        var events = await RunAsync(output, new object[] {
            new PlayerEvent.PlayRequestIdChanged(1),
            new PlayerEvent.Loading(1, TrackA, 0),
            new PlayerEvent.TrackChanged(Item(TrackA, "Never Gonna Give You Up", "Rick Astley", "Whenever You Need Somebody", 213_000)),
            new PlayerEvent.Playing(1, TrackA, 0),
        });

        Assert.Equal(["now playing"], output.Calls.Select(c => c.Kind));
        var nowPlaying = output.Calls[0].NowPlaying!;
        Assert.Equal("Never Gonna Give You Up", nowPlaying.Metadata.Title);
        Assert.Equal("Rick Astley", nowPlaying.Metadata.Artist);
        Assert.Equal("Whenever You Need Somebody", nowPlaying.Metadata.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(213_000), nowPlaying.Metadata.Duration);
        Assert.Equal(3, nowPlaying.Metadata.TrackNumber);
        Assert.Equal("image/jpeg", nowPlaying.ArtworkContentType);
        Assert.Equal(StubArtwork.Bytes, nowPlaying.Artwork!.Value.ToArray());
        Assert.Equal(TimeSpan.Zero, nowPlaying.Elapsed);
        Assert.True(events.Playback.Playing);
        Assert.Equal("Never Gonna Give You Up", events.Playback.Title);
        Assert.Equal(StubArtwork.Url, events.Playback.ArtworkUrl);
    }

    [Fact]
    public async Task Frames_written_after_the_track_change_move_the_elapsed_time()
    {
        var output = new RecordingOutput();
        await RunAsync(output, new object[] {
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            Step(() => output.Frames += 44100), // one second of the track reached the sink before the Playing event
            new PlayerEvent.Playing(1, TrackA, 30_000),
        });

        // SPEC-audio.md 7: the elapsed time is that of the next sample written: 30 s + the second already written.
        Assert.Equal(TimeSpan.FromSeconds(31), Assert.Single(output.Calls).NowPlaying!.Elapsed);
    }

    [Fact]
    public async Task Seek_while_playing_flushes_then_resends_the_position()
    {
        var output = new RecordingOutput();
        await RunAsync(output, new object[] {
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            new PlayerEvent.Playing(1, TrackA, 0),
            new PlayerEvent.Seeked(1, TrackA, 90_000),
        });

        Assert.Equal(["now playing", "flush", "now playing"], output.Calls.Select(c => c.Kind));
        Assert.Equal(TimeSpan.FromSeconds(90), output.Calls[2].NowPlaying!.Elapsed);
    }

    [Fact]
    public async Task Seek_while_paused_only_resends_the_position()
    {
        var output = new RecordingOutput();
        var mapper = await RunAsync(output, new object[] {
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            new PlayerEvent.Playing(1, TrackA, 0),
            new PlayerEvent.Paused(1, TrackA, 10_000),
            new PlayerEvent.Seeked(1, TrackA, 120_000),
        });

        Assert.Equal(["now playing", "now playing"], output.Calls.Select(c => c.Kind));
        Assert.Equal(TimeSpan.FromSeconds(120), output.Calls[1].NowPlaying!.Elapsed);
        Assert.False(mapper.Playback.Playing);
        Assert.Equal(120_000, mapper.Playback.PositionMs);
    }

    [Fact]
    public async Task Natural_track_transition_keeps_the_timeline_and_a_manual_skip_flushes()
    {
        var output = new RecordingOutput();
        await RunAsync(output, new object[] {
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            new PlayerEvent.Playing(1, TrackA, 0),

            // A ends: spirc loads B (gapless, no sink stop): continuous timeline, metadata only.
            new PlayerEvent.EndOfTrack(1, TrackA),
            new PlayerEvent.PlayRequestIdChanged(2),
            new PlayerEvent.Loading(2, TrackB, 0),
            new PlayerEvent.TrackChanged(Item(TrackB, "B", "X", "Y", 180_000)),
            new PlayerEvent.Playing(2, TrackB, 0),

            // The user skips while B plays: the queued audio of B must go.
            new PlayerEvent.PlayRequestIdChanged(3),
            new PlayerEvent.Loading(3, TrackA, 0),
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            new PlayerEvent.Playing(3, TrackA, 0),
        });

        Assert.Equal(["now playing", "now playing", "flush", "now playing"], output.Calls.Select(c => c.Kind));
        Assert.Equal(["A", "B", "A"], output.Calls.Where(c => c.NowPlaying is not null).Select(c => c.NowPlaying!.Metadata.Title));
    }

    [Fact]
    public async Task Skip_to_a_preloaded_track_flushes_without_a_loading_event()
    {
        // librespot's HandleCommandLoad serves a preloaded track (Spirc preloads 30 s before every track end) or the same
        // track again without a Loading event: PlayRequestIdChanged -> TrackChanged -> Playing.
        var output = new RecordingOutput();
        await RunAsync(output, new object[] {
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            new PlayerEvent.Playing(1, TrackA, 0),
            new PlayerEvent.PlayRequestIdChanged(2),
            new PlayerEvent.TrackChanged(Item(TrackB, "B", "X", "Y", 180_000)),
            new PlayerEvent.Playing(2, TrackB, 0),

            // The same track again (restart from the beginning): also a skip.
            new PlayerEvent.PlayRequestIdChanged(3),
            new PlayerEvent.TrackChanged(Item(TrackB, "B", "X", "Y", 180_000)),
            new PlayerEvent.Playing(3, TrackB, 0),
        });

        Assert.Equal(["now playing", "flush", "now playing", "flush", "now playing"], output.Calls.Select(c => c.Kind));
        Assert.Equal(["A", "B", "B"], output.Calls.Where(c => c.NowPlaying is not null).Select(c => c.NowPlaying!.Metadata.Title));
    }

    [Fact]
    public async Task Preloaded_track_after_the_end_of_track_needs_no_flush()
    {
        var output = new RecordingOutput();
        await RunAsync(output, new object[] {
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            new PlayerEvent.Playing(1, TrackA, 0),
            new PlayerEvent.EndOfTrack(1, TrackA),
            new PlayerEvent.PlayRequestIdChanged(2),
            new PlayerEvent.TrackChanged(Item(TrackB, "B", "X", "Y", 180_000)),
            new PlayerEvent.Playing(2, TrackB, 0),
        });

        Assert.Equal(["now playing", "now playing"], output.Calls.Select(c => c.Kind));
    }

    [Fact]
    public async Task Skip_while_paused_needs_no_flush()
    {
        var output = new RecordingOutput();
        await RunAsync(output, new object[] {
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            new PlayerEvent.Playing(1, TrackA, 0),
            new PlayerEvent.Paused(1, TrackA, 5_000),
            new PlayerEvent.Loading(2, TrackB, 0),
            new PlayerEvent.TrackChanged(Item(TrackB, "B", "X", "Y", 180_000)),
            new PlayerEvent.Playing(2, TrackB, 0),
        });

        Assert.DoesNotContain(output.Calls, c => c.Kind == "flush");
    }

    [Fact]
    public async Task Spotify_volume_becomes_the_master_level_and_coalesces()
    {
        var output = new RecordingOutput();
        var mapper = await RunAsync(output, new object[] {
            new PlayerEvent.VolumeChanged(PlayerEventMapper.ToSpotifyVolume(30)),
            new PlayerEvent.VolumeChanged(32768),
        });

        var levels = output.Calls.Where(c => c.Kind == "volume").Select(c => c.Level).ToList();
        Assert.NotEmpty(levels);
        Assert.Equal(50, levels[^1]); // 32768 / 65535 -> 50
        Assert.Equal(50, mapper.Playback.SpotifyVolume);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(99)]
    [InlineData(100)]
    public void Level_and_spotify_volume_round_trip(int level)
    {
        Assert.Equal(level, PlayerEventMapper.ToLevel(PlayerEventMapper.ToSpotifyVolume(level)));
    }

    [Fact]
    public async Task Stopped_clears_the_track()
    {
        var output = new RecordingOutput();
        var mapper = await RunAsync(output, new object[] {
            new PlayerEvent.TrackChanged(Item(TrackA, "A", "X", "Y", 200_000)),
            new PlayerEvent.Playing(1, TrackA, 0),
            new PlayerEvent.Stopped(1, TrackA),
        });

        Assert.False(mapper.Playback.Playing);
        Assert.Null(mapper.Playback.Title);
        Assert.Equal(0, mapper.Playback.PositionMs);
    }

    [Fact]
    public void Episodes_show_the_show_as_artist_and_album()
    {
        var item = Item(TrackA, "Folge 12", "ignored", "ignored", 3_600_000) with
        {
            UniqueFields = new UniqueFields.Episode("desc", Date.FromTimestampMs(0), "Der Podcast"),
        };
        var meta = PlayerEventMapper.ToMetadata(item);
        Assert.Equal(("Folge 12", "Der Podcast", "Der Podcast"), (meta.Title, meta.Artist, meta.Album));
    }

    /// <summary>Feeds the events in order (the <c>Step</c> markers run code in between) and waits for every background send.</summary>
    private static async Task<PlayerEventMapper> RunAsync(RecordingOutput output, IReadOnlyList<object> events)
    {
        var mapper = new PlayerEventMapper(output, new StubArtwork(), new RuntimeState(), hub: null, NullLogger.Instance);
        var channel = Channel.CreateUnbounded<PlayerEvent>();
        var running = mapper.RunAsync(channel.Reader, CancellationToken.None);
        foreach (var item in events)
        {
            if (item is Action step)
            {
                // Let the mapper handle everything before this point, then change the world.
                await output.IdleAsync(channel);
                step();
                continue;
            }

            await channel.Writer.WriteAsync((PlayerEvent)item);
        }

        await output.IdleAsync(channel);
        channel.Writer.Complete();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        return mapper;
    }

    private static object Step(Action action) => action;

    private static AudioItem Item(SpotifyUri uri, string title, string artist, string album, uint durationMs) => new()
    {
        TrackId = uri,
        Uri = uri.ToUri(),
        Files = new AudioFiles(),
        Name = title,
        DurationMs = durationMs,
        Covers = [new CoverImage(StubArtwork.Url, global::Spotify.Metadata.Image.Types.Size.Default, 300, 300)],
        UniqueFields = new UniqueFields.Track(
            new ArtistsWithRole([new ArtistWithRole(SpotifyUri.FromUri("spotify:artist:0gxyHStUsqpMadRV0Di1Qt"), artist, ArtistRole.MainArtist)]),
            album,
            [artist],
            50,
            3,
            1),
    };

    private sealed class StubArtwork : IArtworkLoader
    {
        public const string Url = "https://i.scdn.co/image/test";

        public static readonly byte[] Bytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

        public string? UrlOf(AudioItem item) => item.Covers.FirstOrDefault()?.Url;

        public Task<Artwork?> LoadAsync(AudioItem item, CancellationToken cancellationToken) =>
            Task.FromResult<Artwork?>(new Artwork(Bytes, "image/jpeg"));
    }

    /// <summary>Records what the mapper asks of the output, in order.</summary>
    private sealed class RecordingOutput : IPlaybackOutput
    {
        private readonly ConcurrentQueue<Call> _calls = new();
        private int _busy;

        public long Frames { get; set; }

        public IReadOnlyList<Call> Calls => _calls.ToArray();

        public long FramesWritten => Frames;

        public ChannelReader<string> RemoteCommands { get; } = Channel.CreateUnbounded<string>().Reader;

        public ChannelReader<int> SpotifyVolumeUpdates { get; } = Channel.CreateUnbounded<int>().Reader;

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _busy);
            await Task.Delay(20, cancellationToken); // a FLUSH round trip
            _calls.Enqueue(new Call("flush", null, 0));
            Interlocked.Decrement(ref _busy);
        }

        public Task SetNowPlayingAsync(NowPlaying nowPlaying, CancellationToken cancellationToken)
        {
            _calls.Enqueue(new Call("now playing", nowPlaying, 0));
            return Task.CompletedTask;
        }

        public Task SetSpotifyVolumeAsync(int level, CancellationToken cancellationToken)
        {
            _calls.Enqueue(new Call("volume", null, level));
            return Task.CompletedTask;
        }

        public Task<int?> CurrentLevelAsync(CancellationToken cancellationToken) => Task.FromResult<int?>(50);

        /// <summary>Waits until the mapper read every queued event and its background sends settled.</summary>
        public async Task IdleAsync(Channel<PlayerEvent> channel)
        {
            var stable = 0;
            var lastCount = -1;
            for (var i = 0; i < 400 && stable < 5; i++)
            {
                await Task.Delay(10);
                var count = _calls.Count;
                stable = channel.Reader.Count == 0 && Volatile.Read(ref _busy) == 0 && count == lastCount ? stable + 1 : 0;
                lastCount = count;
            }
        }
    }

    private sealed record Call(string Kind, NowPlaying? NowPlaying, int Level);
}
