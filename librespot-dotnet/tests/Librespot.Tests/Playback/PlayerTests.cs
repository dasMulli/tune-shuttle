// Tests for playback/src/player.rs (upstream has no #[cfg(test)] module; these cover the plan's verify list).
using System.Buffers.Binary;
using System.Threading.Channels;
using Librespot.Audio.Fetch;
using Librespot.Core;
using Librespot.Metadata.Audio;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Decoder;
using Librespot.Playback.Mixer;
using Xunit;

namespace Librespot.Tests.Playback;

public class PlayerTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private static readonly SpotifyUri TrackA = new SpotifyUri.Track(SpotifyId.FromBase62("4uLU6hMCjMI75M1A2tKUQC"));
    private static readonly SpotifyUri TrackB = new SpotifyUri.Track(SpotifyId.FromBase62("6rqhFgbbKwnb9MLmUQDhG6"));

    // ---- NormalisationData.parse_from_ogg -----------------------------------------------------------------------

    [Fact]
    public void ParseFromOgg_reads_four_le_floats_at_offset_144()
    {
        var file = new byte[0xa7 + 32];
        // Bytes before the block must be ignored.
        Array.Fill(file, (byte)0xAA, 0, 144);
        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(144), -6.5f);
        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(148), 0.95f);
        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(152), -7.25f);
        BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(156), 0.99f);

        // Raw little-endian layout of the block, checked byte for byte: -6.5f = 0xC0D00000.
        Assert.Equal(new byte[] { 0x00, 0x00, 0xD0, 0xC0 }, file[144..148]);

        using var stream = new MemoryStream(file);
        var data = NormalisationData.ParseFromOgg(stream);

        Assert.Equal(new NormalisationData(-6.5, (double)0.95f, -7.25, (double)0.99f), data);
        Assert.Equal(160, stream.Position);
    }

    [Fact]
    public void ParseFromOgg_short_file_throws()
    {
        using var stream = new MemoryStream(new byte[150]);
        Assert.Throws<EndOfStreamException>(() => NormalisationData.ParseFromOgg(stream));
    }

    // ---- NormalisationData.get_factor ---------------------------------------------------------------------------

    [Fact]
    public void GetFactor_is_one_when_normalisation_is_off()
    {
        var data = new NormalisationData(-10.0, 0.5, -12.0, 0.4);
        Assert.Equal(1.0, NormalisationData.GetFactor(new PlayerConfig { Normalisation = false }, data));
    }

    public static IEnumerable<object[]> FactorCases =>
    [
        // method, type, pregain, data (tg, tp, ag, ap), expected
        // Basic: factor = min(10^((gain + pregain) / 20), 1 / peak), capped at 1.0.
        [NormalisationMethod.Basic, NormalisationType.Track, 0.0, new[] { -3.0, 0.5, -9.0, 0.5 }, Math.Pow(10, -3.0 / 20)],
        [NormalisationMethod.Basic, NormalisationType.Album, 0.0, new[] { -3.0, 0.5, -9.0, 0.5 }, Math.Pow(10, -9.0 / 20)],
        [NormalisationMethod.Basic, NormalisationType.Track, 1.0, new[] { -3.0, 0.5, -9.0, 0.5 }, Math.Pow(10, -2.0 / 20)],
        // Peak limits the gain: min(10^(0/20)=1.0, 1/2.0=0.5) = 0.5.
        [NormalisationMethod.Basic, NormalisationType.Track, 0.0, new[] { 0.0, 2.0, 0.0, 1.0 }, 0.5],
        // Positive gain would exceed 0 dBFS: min(1.995, 1.25) = 1.25 > 1 -> 1.0.
        [NormalisationMethod.Basic, NormalisationType.Track, 0.0, new[] { 6.0, 0.8, 0.0, 1.0 }, 1.0],
        // Dynamic: factor = 10^((gain + pregain) / 20), no cap (the limiter handles peaks).
        [NormalisationMethod.Dynamic, NormalisationType.Track, 0.0, new[] { 3.0, 1.0, 0.0, 1.0 }, Math.Pow(10, 3.0 / 20)],
        [NormalisationMethod.Dynamic, NormalisationType.Track, -1.0, new[] { -5.0, 1.0, 0.0, 1.0 }, Math.Pow(10, -6.0 / 20)],
        // Auto behaves like Track inside get_factor (the player resolves Auto before calling it).
        [NormalisationMethod.Dynamic, NormalisationType.Auto, 0.0, new[] { -4.0, 1.0, -8.0, 1.0 }, Math.Pow(10, -4.0 / 20)],
    ];

    [Theory]
    [MemberData(nameof(FactorCases))]
    public void GetFactor_matches_replaygain_rules(NormalisationMethod method, NormalisationType type, double pregain, double[] d, double expected)
    {
        var config = new PlayerConfig
        {
            Normalisation = true,
            NormalisationMethod = method,
            NormalisationType = type,
            NormalisationPregainDb = pregain,
        };

        var factor = NormalisationData.GetFactor(config, new NormalisationData(d[0], d[1], d[2], d[3]));

        Assert.Equal(expected, factor, 12);
    }

    // ---- sample processing (handle_packet) ----------------------------------------------------------------------

    [Fact]
    public void Volume_only_scales_samples_without_normalisation()
    {
        var player = NewInternal(new PlayerConfig(), new FixedVolume(0.5));
        var data = new[] { 1.0, -0.5, 0.25, 0.0 };

        player.ApplyVolumeAndNormalisation(data, 1.0);

        Assert.Equal(new[] { 0.5, -0.25, 0.125, 0.0 }, data);
    }

    [Fact]
    public void Basic_normalisation_multiplies_factor_and_volume()
    {
        var config = new PlayerConfig { Normalisation = true, NormalisationMethod = NormalisationMethod.Basic };
        var player = NewInternal(config, new FixedVolume(0.5));
        var data = new[] { 0.8, -0.8 };

        player.ApplyVolumeAndNormalisation(data, 0.5);

        Assert.Equal(new[] { 0.2, -0.2 }, data);
    }

    [Fact]
    public void Basic_normalisation_leaves_samples_alone_at_unity()
    {
        var config = new PlayerConfig { Normalisation = true, NormalisationMethod = NormalisationMethod.Basic };
        var player = NewInternal(config, new NoOpVolume());
        var data = new[] { 0.8, -0.8 };

        player.ApplyVolumeAndNormalisation(data, 1.0);

        Assert.Equal(new[] { 0.8, -0.8 }, data);
    }

    [Fact]
    public void Dynamic_limiter_passes_quiet_signal()
    {
        // Threshold -2 dBFS, knee 5 dB: a 0.5 (-6.02 dBFS) signal has bias -4.02 dB, knee boundary -8.04 < -5,
        // so the limiter gain is 0 dB and the samples pass unchanged (plus f64::MIN_POSITIVE).
        var config = new PlayerConfig { Normalisation = true, NormalisationMethod = NormalisationMethod.Dynamic };
        var player = NewInternal(config, new NoOpVolume());
        var data = Enumerable.Repeat(0.5, 2000).ToArray();

        player.ApplyVolumeAndNormalisation(data, 1.0);

        Assert.All(data, s => Assert.Equal(0.5, s, 15));
    }

    [Fact]
    public void Dynamic_limiter_converges_to_soft_knee_gain_for_constant_full_scale_signal()
    {
        // Constant amplitude 1.0 (0 dBFS), threshold -2 dBFS, knee 5 dB:
        //   bias = 0 - (-2) = 2 dB, knee boundary = 4 dB, |4| <= 5 -> limiter = (4 + 5)^2 / (8 * 5) = 2.025 dB.
        // The integrator holds 2.025 (max(limiter, smoothed)), the peak detector converges to it, and the output
        // converges to 10^(-2.025 / 20) = 0.792045...
        var config = new PlayerConfig { Normalisation = true, NormalisationMethod = NormalisationMethod.Dynamic };
        var player = NewInternal(config, new NoOpVolume());
        var data = Enumerable.Repeat(1.0, 2 * 44100).ToArray(); // 1 s of stereo, >> the 5 ms attack

        player.ApplyVolumeAndNormalisation(data, 1.0);

        var expectedSteadyState = Math.Pow(10, -2.025 / 20);
        Assert.Equal(0.792045, expectedSteadyState, 6);
        Assert.Equal(expectedSteadyState, data[^1], 9);
        Assert.Equal(expectedSteadyState, data[^2], 9);

        // First sample: peak = (1 - attack_cf) * 2.025 dB, applied to the left channel only.
        var attackCf = config.NormalisationAttackCf;
        var firstPeak = (1.0 - attackCf) * 2.025;
        Assert.Equal(Math.Pow(10, -firstPeak / 20), data[0], 12);

        // The gain reduction is monotonic while the peak detector charges.
        for (var i = 2; i < data.Length; i++)
        {
            Assert.True(data[i] <= data[i - 2] + 1e-15);
        }
    }

    [Fact]
    public void Dynamic_limiter_applies_normalisation_factor_and_volume()
    {
        // factor 0.5 makes a 1.0 signal quiet (-6.02 dBFS): only the gain stage and the volume apply.
        var config = new PlayerConfig { Normalisation = true, NormalisationMethod = NormalisationMethod.Dynamic };
        var player = NewInternal(config, new FixedVolume(0.5));
        var data = Enumerable.Repeat(1.0, 100).ToArray();

        player.ApplyVolumeAndNormalisation(data, 0.5);

        Assert.All(data, s => Assert.Equal(0.25, s, 15));
    }

    // ---- Subfile -------------------------------------------------------------------------------------------------

    [Fact]
    public void Subfile_offsets_reads_and_seeks()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        using var subfile = new Subfile(new MemoryStream(bytes), 10, 100);

        Assert.Equal(0, subfile.Position);
        Assert.Equal(10, subfile.ReadByte());
        Assert.Equal(1, subfile.Position);

        Assert.Equal(5, subfile.Seek(5, SeekOrigin.Begin));
        Assert.Equal(15, subfile.ReadByte());

        Assert.Equal(8, subfile.Seek(2, SeekOrigin.Current));
        Assert.Equal(18, subfile.ReadByte());

        // SeekFrom::End is relative to the underlying stream's end.
        Assert.Equal(80, subfile.Seek(-10, SeekOrigin.End));
        Assert.Equal(90, subfile.ReadByte());

        // Upstream's guard: length - offset < self.offset.
        Assert.Throws<IOException>(() => subfile.Seek(95, SeekOrigin.End));

        // byte_len is the full length passed in.
        Assert.Equal(100, subfile.Length);
    }

    // ---- PlayerTrackLoader helpers --------------------------------------------------------------------------------

    [Theory]
    [InlineData(AudioFileFormat.OggVorbis96, 12 * 1024)]
    [InlineData(AudioFileFormat.OggVorbis160, 20 * 1024)]
    [InlineData(AudioFileFormat.OggVorbis320, 40 * 1024)]
    [InlineData(AudioFileFormat.Mp3256, 32 * 1024)]
    [InlineData(AudioFileFormat.FlacFlac, 112 * 1024)]
    [InlineData(AudioFileFormat.XheAac12, 1536)]
    [InlineData(AudioFileFormat.FlacFlac24Bit, 3 * 1024)]
    public void StreamDataRate_matches_upstream_table(AudioFileFormat format, int expected)
    {
        Assert.Equal(expected, PlayerTrackLoader.StreamDataRate(format));
    }

    [Fact]
    public void Formats_prefer_requested_bitrate_then_fall_back()
    {
        Assert.Equal(AudioFileFormat.OggVorbis96, PlayerTrackLoader.FormatsFor(Bitrate.Bitrate96)[0]);
        Assert.Equal(AudioFileFormat.OggVorbis160, PlayerTrackLoader.FormatsFor(Bitrate.Bitrate160)[0]);
        Assert.Equal(
            [
                AudioFileFormat.OggVorbis320, AudioFileFormat.Mp3320, AudioFileFormat.Mp3256, AudioFileFormat.OggVorbis160,
                AudioFileFormat.Mp3160, AudioFileFormat.OggVorbis96, AudioFileFormat.Mp396,
            ],
            PlayerTrackLoader.FormatsFor(Bitrate.Bitrate320));
    }

    [Fact]
    public void PositionChanged_has_no_play_request_id_like_upstream()
    {
        Assert.Null(new PlayerEvent.PositionChanged(3, TrackA, 0).GetPlayRequestId());
        Assert.Equal(3ul, new PlayerEvent.Seeked(3, TrackA, 0).GetPlayRequestId());
    }

    // ---- state machine -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Load_and_play_runs_to_end_of_track()
    {
        var sink = new FakeSink();
        var loader = new FakeLoaderFactory(packets: 5);
        await using var session = NewSession();
        using var player = Player.Create(new PlayerConfig(), session, new NoOpVolume(), () => sink, loader.Create, TimeProvider.System);
        using var events = player.GetPlayerEventChannel();

        player.Load(TrackA, startPlaying: true, positionMs: 0);

        var seen = await ReadUntilAsync<PlayerEvent.EndOfTrack>(events.Reader);

        Assert.Equal(new PlayerEvent.PlayRequestIdChanged(0), seen[0]);
        Assert.Equal(new PlayerEvent.Loading(0, TrackA, 0), seen[1]);
        Assert.IsType<PlayerEvent.TrackChanged>(seen[2]);
        Assert.Equal(new PlayerEvent.Playing(0, TrackA, 0), seen[3]);
        Assert.Contains(new PlayerEvent.TimeToPreloadNextTrack(0, TrackA), seen);
        Assert.Equal(new PlayerEvent.EndOfTrack(0, TrackA), seen[^1]);

        Assert.Equal(5, sink.Writes);
        Assert.True(sink.Starts >= 1);
    }

    [Fact]
    public async Task Paused_load_then_seek_play_pause_stop()
    {
        var sink = new FakeSink { WriteDelay = TimeSpan.FromMilliseconds(2) };
        var loader = new FakeLoaderFactory(packets: 100_000);
        await using var session = NewSession();
        var player = Player.Create(new PlayerConfig(), session, new NoOpVolume(), () => sink, loader.Create, TimeProvider.System);
        using var events = player.GetPlayerEventChannel();

        player.Load(TrackA, startPlaying: false, positionMs: 0);
        await ReadUntilAsync<PlayerEvent.Paused>(events.Reader);
        Assert.Equal(0, sink.Writes);

        player.Seek(500);
        var seeked = await ReadUntilAsync<PlayerEvent.Seeked>(events.Reader);
        Assert.Equal(new PlayerEvent.Seeked(0, TrackA, 500), seeked[^1]);

        player.Play();
        var playing = await ReadUntilAsync<PlayerEvent.Playing>(events.Reader);
        Assert.Equal(new PlayerEvent.Playing(0, TrackA, 500), playing[^1]);

        player.Pause();
        var paused = await ReadUntilAsync<PlayerEvent.Paused>(events.Reader);
        Assert.True(((PlayerEvent.Paused)paused[^1]).PositionMs >= 500);
        Assert.True(sink.Stops >= 1);

        player.Stop();
        var stopped = await ReadUntilAsync<PlayerEvent.Stopped>(events.Reader);
        Assert.Equal(new PlayerEvent.Stopped(0, TrackA), stopped[^1]);

        // Stopping drops the decoder (upstream: the state, and with it the decoder, is replaced).
        Assert.True(loader.Decoders.Single().Disposed);

        player.Dispose();
        Assert.True(player.IsInvalid);
        Assert.True(sink.Disposed);

        // The event channel completes when the player shuts down.
        while (await events.Reader.WaitToReadAsync())
        {
            events.Reader.TryRead(out _);
        }
    }

    [Fact]
    public async Task Failed_load_reports_unavailable()
    {
        var loader = new FakeLoaderFactory(packets: 5) { Fail = true };
        await using var session = NewSession();
        using var player = Player.Create(new PlayerConfig(), session, new NoOpVolume(), () => new FakeSink(), loader.Create, TimeProvider.System);
        using var events = player.GetPlayerEventChannel();

        player.Load(TrackA, startPlaying: true, positionMs: 0);

        var seen = await ReadUntilAsync<PlayerEvent.Unavailable>(events.Reader);
        Assert.Equal(new PlayerEvent.Unavailable(0, TrackA), seen[^1]);
    }

    [Fact]
    public async Task Preloaded_track_is_used_by_load()
    {
        var sink = new FakeSink { WriteDelay = TimeSpan.FromMilliseconds(2) };
        var loader = new FakeLoaderFactory(packets: 100_000);
        await using var session = NewSession();
        using var player = Player.Create(new PlayerConfig(), session, new NoOpVolume(), () => sink, loader.Create, TimeProvider.System);
        using var events = player.GetPlayerEventChannel();

        player.Load(TrackA, startPlaying: false, positionMs: 0);
        await ReadUntilAsync<PlayerEvent.Paused>(events.Reader);

        player.Preload(TrackB);
        var preloading = await ReadUntilAsync<PlayerEvent.Preloading>(events.Reader);
        Assert.Equal(new PlayerEvent.Preloading(TrackB), preloading[^1]);

        player.Load(TrackB, startPlaying: false, positionMs: 0);
        var seen = await ReadUntilAsync<PlayerEvent.Paused>(events.Reader);

        // The second play request id comes from the generator; no Loading event for a preloaded track.
        Assert.Equal(new PlayerEvent.PlayRequestIdChanged(1), seen[0]);
        Assert.DoesNotContain(seen, e => e is PlayerEvent.Loading);
        Assert.Equal(new PlayerEvent.Paused(1, TrackB, 0), seen[^1]);
        Assert.Equal([TrackA, TrackB], loader.Loaded);

        // Track A's decoder was dropped when B took over.
        Assert.True(loader.Decoders[0].Disposed);
        Assert.False(loader.Decoders[1].Disposed);
    }

    [Fact]
    public async Task Emit_commands_produce_events_and_await_end_of_track_returns_on_stop()
    {
        await using var session = NewSession();
        using var player = Player.Create(new PlayerConfig(), session, new NoOpVolume(), () => new FakeSink(), new FakeLoaderFactory(5).Create, TimeProvider.System);
        using var events = player.GetPlayerEventChannel();

        player.EmitVolumeChangedEvent(1234);
        player.EmitShuffleChangedEvent(true);
        player.EmitSetQueueEvent("spotify:album:x", null, [new QueueTrack("spotify:track:a", "context")], []);

        var seen = await ReadUntilAsync<PlayerEvent.SetQueue>(events.Reader);
        Assert.Equal(new PlayerEvent.VolumeChanged(1234), seen[0]);
        Assert.Equal(new PlayerEvent.ShuffleChanged(true), seen[1]);

        var waiter = player.AwaitEndOfTrackAsync();
        player.Load(TrackA, startPlaying: false, positionMs: 0);
        await ReadUntilAsync<PlayerEvent.Paused>(events.Reader);
        player.Stop();
        await waiter.WaitAsync(EventTimeout);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static Session NewSession() => new(SessionConfig.Default(), null);

    private static PlayerInternal NewInternal(PlayerConfig config, IVolumeGetter volume) =>
        new(
            NewSession(),
            config,
            Channel.CreateUnbounded<PlayerCommand>().Reader,
            new FakeSink(),
            volume,
            new Converter(null),
            1.0 / (8.0 * config.NormalisationKneeDb),
            0,
            new LocalFileLookup(),
            (s, c, l) => new FakeLoaderFactory(1).Create(s, c, l),
            TimeProvider.System);

    private static async Task<List<PlayerEvent>> ReadUntilAsync<T>(ChannelReader<PlayerEvent> reader)
        where T : PlayerEvent
    {
        using var cts = new CancellationTokenSource(EventTimeout);
        var seen = new List<PlayerEvent>();
        while (true)
        {
            var e = await reader.ReadAsync(cts.Token);
            seen.Add(e);
            if (e is T)
            {
                return seen;
            }
        }
    }

    private sealed class FixedVolume(double factor) : IVolumeGetter
    {
        public double AttenuationFactor() => factor;
    }

    private sealed class FakeSink : ISink
    {
        private int _starts;
        private int _stops;
        private int _writes;

        public TimeSpan WriteDelay { get; init; }

        public int Starts => Volatile.Read(ref _starts);

        public int Stops => Volatile.Read(ref _stops);

        public int Writes => Volatile.Read(ref _writes);

        public bool Disposed { get; private set; }

        public void Start() => Interlocked.Increment(ref _starts);

        public void Stop() => Interlocked.Increment(ref _stops);

        public void Write(AudioPacket packet, Converter converter)
        {
            Interlocked.Increment(ref _writes);
            if (WriteDelay > TimeSpan.Zero)
            {
                Thread.Sleep(WriteDelay); // runs on the player thread, paces playback like a real device
            }
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Yields <c>packets</c> packets of 100 ms each (10 stereo samples of 0.1).</summary>
    private sealed class FakeDecoder(int packets) : IAudioDecoder
    {
        private uint _positionMs;
        private int _remaining = packets;

        public bool Disposed { get; private set; }

        public uint Seek(uint positionMs)
        {
            _positionMs = positionMs;
            return positionMs;
        }

        public (AudioPacketPosition Position, AudioPacket Packet)? NextPacket()
        {
            if (_remaining-- <= 0)
            {
                return null;
            }

            var position = _positionMs;
            _positionMs += 100;
            return (new AudioPacketPosition(position, false), new AudioPacket.Samples(Enumerable.Repeat(0.1, 20).ToArray()));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeLoaderFactory(int packets) : ITrackLoader
    {
        private readonly object _lock = new();

        public bool Fail { get; init; }

        public List<FakeDecoder> Decoders { get; } = [];

        public List<SpotifyUri> Loaded { get; } = [];

        public ITrackLoader Create(Session session, PlayerConfig config, LocalFileLookup lookup) => this;

        public async Task<PlayerLoadedTrackData?> LoadTrackAsync(SpotifyUri trackUri, uint positionMs, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Fail)
            {
                return null;
            }

            var decoder = new FakeDecoder(packets);
            lock (_lock)
            {
                Decoders.Add(decoder);
                Loaded.Add(trackUri);
            }

            return new PlayerLoadedTrackData
            {
                Decoder = decoder,
                NormalisationData = NormalisationData.Default,
                StreamLoaderController = StreamLoaderController.FromLocalFile(1000),
                AudioItem = new AudioItem
                {
                    TrackId = trackUri,
                    Uri = trackUri.ToUri(),
                    Files = new AudioFiles(),
                    Name = "test",
                    DurationMs = (uint)packets * 100,
                    UniqueFields = new UniqueFields.Local(null, null, null, null, null, "test.mp3"),
                },
                BytesPerSecond = 1000,
                DurationMs = (uint)packets * 100,
                StreamPositionMs = decoder.Seek(positionMs),
                IsExplicit = false,
            };
        }
    }
}
