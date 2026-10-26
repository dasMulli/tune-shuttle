using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Sender;
using Airplay2.Sender.Playback;
using Bridge.Service.Output;
using Librespot.Playback;
using Librespot.Playback.Decoder;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests.Service;

/// <summary>
/// <see cref="AirplaySink"/> driven like librespot drives it (synthetic
/// <c>AudioPacket.Samples</c> decoded by the fakes): f64 samples in, the same S16 the converter produces out of two fake
/// receivers' ALAC decoders; Stop flushes, Start re-anchors; without a player the audio is discarded.
/// </summary>
public sealed class AirplaySinkTests
{
    private const int PacketFrames = 1024; // what a librespot Vorbis packet roughly carries

    [Fact]
    public async Task Samples_written_to_the_sink_arrive_bit_exact_at_two_fake_receivers()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Sink A" });
        await using var b = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Sink B" });
        await using var sender = new AirplaySender(new AirplaySenderOptions { LocalAddress = IPAddress.Loopback }, new MemoryPairingStore(), NullLoggerFactory.Instance);
        var receivers = new[] { a.Describe(), b.Describe() };
        await using var player = await sender.CreateGroupPlayerAsync(receivers, new GroupPlayerOptions { VolumePollInterval = null }, CancellationToken.None);
        await player.AddReceiverAsync(receivers[0], 15, CancellationToken.None);
        await player.AddReceiverAsync(receivers[1], 15, CancellationToken.None);
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance);
        await sink.AttachAsync(player, CancellationToken.None);

        // librespot's converter without a ditherer is deterministic: the expected wire PCM is its output.
        var converter = new Converter(null);
        var packets = Enumerable.Range(0, 40).Select(Synthetic).ToList();
        var expected = packets.SelectMany(p => converter.F64ToS16(p.Data)).ToArray();

        var writer = new Thread(() =>
        {
            sink.Start();
            foreach (var packet in packets)
            {
                sink.Write(packet, converter);
            }
        }) { IsBackground = true, Name = "librespot player (test)" };
        writer.Start();

        var expectedFrames = expected.Length / 2;
        var audioA = await CollectAsync(a, expectedFrames);
        var audioB = await CollectAsync(b, expectedFrames);
        Assert.True(writer.Join(TimeSpan.FromSeconds(20)));

        Assert.True(audioA[0].Marker, "the first packet after the start carries the marker bit (SPEC-audio.md 8.4)");
        Assert.Equal(expected, audioA.SelectMany(p => p.Pcm).Take(expected.Length).ToArray());
        Assert.Equal(expected, audioB.SelectMany(p => p.Pcm).Take(expected.Length).ToArray());
        Assert.Equal(expectedFrames, sink.FramesWritten);
        Assert.Equal(0, sink.FramesDiscarded);
        Assert.True(sink.IsRunning);

        // Stop (librespot pause): every receiver is flushed; Start re-anchors with a new marker packet.
        sink.Stop();
        Assert.False(sink.IsRunning);
        await a.WaitAsync<FakeReceiverEvent.Flush>();
        await b.WaitAsync<FakeReceiverEvent.Flush>();
        var markersBefore = a.Count<FakeReceiverEvent.AudioPacket>(p => p.Marker);
        var resume = new Thread(() =>
        {
            sink.Start();
            for (var i = 0; i < 20; i++)
            {
                sink.Write(Synthetic(100 + i), converter);
            }
        }) { IsBackground = true };
        resume.Start();
        await a.WaitAsync<FakeReceiverEvent.AudioPacket>(p => p.Marker, skip: markersBefore);
        Assert.True(resume.Join(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Without_a_player_the_audio_is_discarded_and_start_stop_do_nothing()
    {
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance);
        var converter = new Converter(null);
        sink.Start();
        sink.Write(Synthetic(0), converter);
        sink.Write(new AudioPacket.Raw([1, 2, 3, 4]), converter); // passthrough cannot be forwarded: ignored
        sink.Stop();
        Assert.Equal(PacketFrames, sink.FramesDiscarded);
        Assert.Equal(0, sink.FramesWritten);
        Assert.Throws<Librespot.Playback.AudioBackend.SinkException>(() => new AirplaySink(AudioFormat.F32, NullLogger<AirplaySink>.Instance));
    }

    [Fact]
    public async Task Frames_of_a_non_owner_are_dropped_and_after_a_release_the_next_write_claims()
    {
        // SPEC-receiver.md 8.2: the per-write gate; the first Spotify write while nobody owns the output claims it.
        var sources = new SourceSwitch();
        var player = new StubPlayer();
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance) { Sources = sources, PaceDroppedFrames = false, Player = player };
        var converter = new Converter(null);
        await sources.ClaimAsync(AudioSource.Airplay, null, CancellationToken.None);

        sink.Write(Synthetic(0), converter);
        sink.Write(Synthetic(1), converter);
        Assert.Equal(2 * PacketFrames, sink.FramesDropped);
        Assert.Equal(0, sink.FramesWritten);
        Assert.Equal(0, player.FramesWritten);
        Assert.Equal(AudioSource.Airplay, sources.Owner);

        sources.Release(AudioSource.Airplay);
        Assert.Equal(AudioSource.None, sources.Owner);
        sink.Write(Synthetic(2), converter);
        Assert.Equal(AudioSource.Spotify, sources.Owner);
        Assert.Equal(PacketFrames, sink.FramesWritten);
        Assert.Equal(PacketFrames, player.FramesWritten);
        Assert.Equal(2 * PacketFrames, sink.FramesDropped);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Dropped_frames_are_paced_at_real_time()
    {
        var sources = new SourceSwitch();
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance) { Sources = sources, Player = new StubPlayer() };
        var converter = new Converter(null);
        await sources.ClaimAsync(AudioSource.Airplay, null, CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            sink.Write(Synthetic(i), converter); // 10 x 1024 frames = 232 ms of audio
        }

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(200), $"dropping took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_start_while_airplay_owns_is_dropped_and_replayed_when_spotify_claims()
    {
        var player = new StubPlayer();
        AirplaySink? sinkRef = null;
        var sources = new SourceSwitch(new RuntimeState(), null, NullLogger<SourceSwitch>.Instance, new SourceSwitchHooks
        {
            StartSpotifyIfRunningAsync = ct => sinkRef!.StartIfRunning().WaitAsync(ct),
        });
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance) { Sources = sources, Player = player, PaceDroppedFrames = false };
        sinkRef = sink;
        await sources.ClaimAsync(AudioSource.Airplay, null, CancellationToken.None);

        sink.Start(); // waits for its command: the start was handled (dropped) when it returns
        Assert.Equal(0, player.Starts);
        sink.Stop();
        await sink.LastPauseCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, player.Pauses); // AirPlay's player is not paused by Spotify
        sink.Start();
        Assert.Equal(0, player.Starts);

        await sources.ClaimAsync(AudioSource.Spotify, null, CancellationToken.None);
        Assert.Equal(1, player.Starts); // replayed once, by the new owner
        Assert.Equal(AudioSource.Spotify, sources.Owner);
    }

    [Fact]
    public async Task Eviction_pauses_spotify_and_completes_after_the_players_pause()
    {
        // SPEC-receiver.md 8.2: TryCommand("pause") → librespot's Stop() → the queued PauseAsync; the claim awaits it.
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new StubPlayer { PauseGate = pause.Task };
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance) { Player = player };
        sink.Start();
        Assert.Equal(1, player.Starts);

        var requested = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eviction = sink.EvictAsync(
            () =>
            {
                Interlocked.Increment(ref requested);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    sink.Stop(); // Spirc pauses the player thread a moment later
                    stopped.TrySetResult();
                });
                return true;
            },
            CancellationToken.None);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); // not a fixed delay: a starved pool may run the item late
        Assert.Equal(1, requested);
        Assert.False(eviction.IsCompleted);
        Assert.False(sink.IsRunning);

        pause.SetResult();
        await eviction.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, player.Pauses);
        Assert.True(sink.LastPauseCompleted.IsCompleted);

        // Not running: nothing to request, completes at once.
        await sink.EvictAsync(() => throw new InvalidOperationException("not called"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Eviction_without_spotify_stops_the_sink_itself()
    {
        var player = new StubPlayer();
        await using var sink = new AirplaySink(AudioFormat.S16, NullLogger<AirplaySink>.Instance) { Player = player };
        sink.Start();

        await sink.EvictAsync(() => false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(sink.IsRunning);
        Assert.Equal(1, player.Pauses);
        await sink.StartIfRunning().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, player.Starts); // not running: StartIfRunning starts nothing
    }

    /// <summary>A 1024-frame stereo packet of two different tones, distinct per index.</summary>
    private static AudioPacket.Samples Synthetic(int index)
    {
        var samples = new double[PacketFrames * 2];
        for (var i = 0; i < PacketFrames; i++)
        {
            var t = (index * PacketFrames) + i;
            samples[2 * i] = 0.4 * Math.Sin(2 * Math.PI * 440 * t / 44100.0);
            samples[(2 * i) + 1] = 0.3 * Math.Sin(2 * Math.PI * 660 * t / 44100.0);
        }

        return new AudioPacket.Samples(samples);
    }

    /// <summary>A player that counts what the sink asks of it.</summary>
    private sealed class StubPlayer : IGroupPlayer
    {
        private int _starts;
        private int _pauses;
        private long _frames;

        public Task PauseGate { get; init; } = Task.CompletedTask;

        public int Starts => Volatile.Read(ref _starts);

        public int Pauses => Volatile.Read(ref _pauses);

        public long FramesWritten => Interlocked.Read(ref _frames);

        public System.Threading.Channels.ChannelReader<GroupPlayerEvent> Events { get; } = System.Threading.Channels.Channel.CreateUnbounded<GroupPlayerEvent>().Reader;

        public GroupVolumeModel Volume => throw new NotSupportedException();

        public IReadOnlyDictionary<string, GroupMemberState> Members { get; } = new Dictionary<string, GroupMemberState>();

        public int QueuedFrames => 0;

        public Task AddReceiverAsync(Airplay2.Sender.Discovery.DiscoveredReceiver receiver, int level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveReceiverAsync(string receiverId, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Write(ReadOnlySpan<short> interleavedPcm) => Interlocked.Add(ref _frames, interleavedPcm.Length / 2);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            return Task.CompletedTask;
        }

        public async Task PauseAsync(CancellationToken cancellationToken)
        {
            await PauseGate;
            Interlocked.Increment(ref _pauses);
        }

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetReceiverLevelAsync(string receiverId, int level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetNowPlayingAsync(Airplay2.Sender.Session.TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<List<FakeReceiverEvent.AudioPacket>> CollectAsync(RecordedFake fake, int frames)
    {
        var packets = (frames + 351) / 352;
        await fake.WaitAsync<FakeReceiverEvent.AudioPacket>(skip: packets - 1, timeout: TimeSpan.FromSeconds(30));
        return fake.Events.OfType<FakeReceiverEvent.AudioPacket>().Take(packets).ToList();
    }
}
