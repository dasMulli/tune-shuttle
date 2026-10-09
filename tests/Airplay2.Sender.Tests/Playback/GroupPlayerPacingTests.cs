using System.Collections.Concurrent;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Playback;

/// <summary>
/// The pacer and the ring without a network (SPEC-audio.md 8.2, 8.3): deadline accuracy over 30 s, the NTP time handed
/// to the transport, silence on underrun, the silence timeout, Write's backpressure, pause and flush marks.
/// </summary>
public sealed class GroupPlayerPacingTests(ITestOutputHelper output)
{
    private const long PacketNanos = 352L * 1_000_000_000 / 44100;
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The full 30 s accuracy run of the Verify list; tagged so a loaded CI runner can exclude it (<c>Category!=Timing</c>).</summary>
    [Fact]
    [Trait("Category", "Timing")]
    public Task Pacer_stays_within_two_milliseconds_of_its_deadlines_over_30_seconds() => AssertPacerAccuracyAsync(30);

    [Fact]
    public Task Pacer_stays_within_two_milliseconds_of_its_deadlines_over_5_seconds() => AssertPacerAccuracyAsync(5);

    [Fact]
    public async Task A_stall_beyond_the_threshold_skips_the_missed_timestamps_and_keeps_the_sequence_and_anchor()
    {
        var transport = new RecordingTransport();
        await using var player = NewPlayer(transport, new GroupPlayerOptions { StallThreshold = TimeSpan.FromMilliseconds(200) });
        var stalled = 0;
        player.PacketObserver = (_, _) =>
        {
            if (transport.Sends.Count == 20 && Interlocked.Exchange(ref stalled, 1) == 0)
            {
                Thread.Sleep(600); // the pacer thread itself freezes (GC pause, suspended VM)
            }
        };

        await player.StartAsync(None);
        await WaitAsync(() => transport.Sends.Count >= 50);
        var sends = transport.Sends.ToArray();

        // Sequence numbers stay contiguous; exactly one timestamp jump of whole packets, right after the stalled packet.
        for (var i = 1; i < sends.Length; i++)
        {
            Assert.Equal(unchecked((ushort)(sends[i - 1].Sequence + 1)), sends[i].Sequence);
        }

        var jumps = Enumerable.Range(1, sends.Length - 1).Where(i => unchecked(sends[i].Timestamp - sends[i - 1].Timestamp) != 352u).ToArray();
        var j = Assert.Single(jumps);
        Assert.Equal(20, j);
        var skipped = unchecked(sends[j].Timestamp - sends[j - 1].Timestamp) - 352u;
        Assert.Equal(0u, skipped % 352u);
        Assert.InRange(skipped / 352u, 50u, 100u); // ~600 ms = ~75 packets' worth of timestamps
        Assert.Equal(1, player.Stalls);

        // The anchor is kept: the sync time of every packet still follows its timestamp exactly, across the jump too.
        for (var i = 1; i < sends.Length; i++)
        {
            var frames = unchecked(sends[i].Timestamp - sends[i - 1].Timestamp);
            var nanos = NtpTimestamp.ToUnixNanos(sends[i].Ntp) - NtpTimestamp.ToUnixNanos(sends[i - 1].Ntp);
            Assert.Equal(frames * 1e9 / 44100, nanos, 2.0);
        }

        Assert.True(player.Timeline.IsAnchored);
    }

    private async Task AssertPacerAccuracyAsync(int seconds)
    {
        var transport = new RecordingTransport();
        await using var player = NewPlayer(transport);
        var errors = new ConcurrentQueue<long>();
        var deadlines = new ConcurrentQueue<long>();
        player.PacketObserver = (deadline, sent) =>
        {
            errors.Enqueue(sent - deadline);
            deadlines.Enqueue(deadline);
        };

        await player.StartAsync(None);
        var producer = WriteOnThread(player, TestPcm.Sweep(44100 * seconds));
        await Task.Delay(TimeSpan.FromSeconds(seconds + 0.5));
        await player.PauseAsync(None);

        var e = errors.ToArray();
        var sorted = e.Order().ToArray();
        var within = e.Count(x => Math.Abs(x) <= 2_000_000);
        output.WriteLine($"{e.Length} packets; error median {sorted[sorted.Length / 2] / 1e3:0} us, p99 {sorted[(int)(sorted.Length * 0.99)] / 1e3:0} us, max {sorted[^1] / 1e3:0} us; within 2 ms {within * 100.0 / e.Length:0.00} %; late wakeups {player.LateWakeups}");

        // N + 0.5 s of deadlines, no drift: the deadlines are exactly 352/44100 s apart and the packet count matches the clock.
        var expected = (int)((seconds + 0.5) * 44100 / 352);
        Assert.InRange(e.Length, expected - 6, expected + 4);
        var d = deadlines.ToArray();
        for (var i = 1; i < d.Length; i++)
        {
            Assert.InRange(d[i] - d[i - 1], PacketNanos - 1, PacketNanos + 1);
        }

        Assert.True(within >= e.Length * 0.99, $"only {within} of {e.Length} packets within 2 ms");
        Assert.InRange(sorted[sorted.Length / 2], -2_000_000, 2_000_000);
        Assert.All(e, x => Assert.True(x >= 0, "never early")); // sleeps end at or after the deadline

        // The transport got the deadlines as NTP times: consecutive values one packet apart, no rounding drift.
        var ntp = transport.Sends.Select(s => NtpTimestamp.ToUnixNanos(s.Ntp)).ToArray();
        Assert.Equal(PacketNanos * (ntp.Length - 1), ntp[^1] - ntp[0], (double)ntp.Length);
        await AwaitQuietly(producer);
    }

    [Fact]
    public async Task Underrun_sends_silence_with_continuous_timestamps()
    {
        var transport = new RecordingTransport(decode: true);
        await using var player = NewPlayer(transport);
        var input = TestPcm.Sweep(352 * 10);
        await player.StartAsync(None);
        player.Write(input);
        await WaitAsync(() => transport.Sends.Count >= 60);

        var sends = transport.Sends.ToArray();
        for (var i = 1; i < sends.Length; i++)
        {
            Assert.Equal(unchecked((ushort)(sends[i - 1].Sequence + 1)), sends[i].Sequence);
            Assert.Equal(unchecked(sends[i - 1].Timestamp + 352u), sends[i].Timestamp);
        }

        // The pre-fill timed out with nothing buffered, so the stream started with silence; the input follows in order; then silence again.
        var pcm = sends.SelectMany(s => s.Pcm!).ToArray();
        var start = Array.FindIndex(pcm, x => x != 0) & ~1; // first frame with sound (the sweep's left channel starts at 0)
        Assert.True(start >= 0 && start % (352 * 2) == 0, $"input starts at sample {start}");
        Assert.Equal(input, pcm.AsSpan(start, input.Length).ToArray());
        Assert.All(sends[^5..], s => Assert.All(s.Pcm!, x => Assert.Equal(0, x)));
        Assert.True(player.SilencePackets >= 40);
    }

    [Fact]
    public async Task Silence_timeout_stops_the_pacer_and_audio_restarts_it_on_the_same_timeline()
    {
        var transport = new RecordingTransport();
        await using var player = NewPlayer(transport, new GroupPlayerOptions { SilenceTimeout = TimeSpan.FromMilliseconds(300) });
        await player.StartAsync(None);
        await WaitAsync(() => transport.Sends.Count >= 30);
        await Task.Delay(400);
        var stopped = transport.Sends.Count;
        await Task.Delay(300);
        Assert.Equal(stopped, transport.Sends.Count);
        Assert.InRange(stopped, 37, 60); // ~0.3 s of silence = 38 packets
        Assert.False(player.Timeline.IsAnchored);

        var last = transport.Sends.Last();
        player.Write(TestPcm.Sweep(352 * 30));
        await WaitAsync(() => transport.Sends.Count > stopped + 20);
        var resumed = transport.Sends.ElementAt(stopped);
        Assert.Equal(unchecked((ushort)(last.Sequence + 1)), resumed.Sequence);
        Assert.Equal(unchecked(last.Timestamp + 352u), resumed.Timestamp); // monotonic, re-anchored
        Assert.True(player.IsStarted);
    }

    [Fact]
    public async Task Write_blocks_above_the_high_water_mark_and_is_paced_by_the_clock()
    {
        var transport = new RecordingTransport();
        await using var player = NewPlayer(transport, new GroupPlayerOptions { RingHighWaterFrames = 4410, PrefillFrames = 352 });

        // Nothing to play to: discarded at once.
        player.Write(TestPcm.Sweep(44100));
        Assert.Equal(44100, player.FramesDiscarded);
        Assert.Throws<ArgumentException>(() => player.Write(new short[3]));

        await player.StartAsync(None);
        var started = DateTime.UtcNow;
        var producer = WriteOnThread(player, TestPcm.Sweep(44100));
        await Task.Delay(300);
        Assert.False(producer.IsCompleted);
        Assert.InRange(player.QueuedFrames, 0, 4410 + 4096);
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task Pause_stops_at_a_packet_boundary_and_flush_drops_only_frames_written_before_it()
    {
        var transport = new RecordingTransport();
        await using var player = NewPlayer(transport);
        await player.StartAsync(None);
        player.Write(TestPcm.Sweep(352 * 20));
        await WaitAsync(() => transport.Sends.Count >= 5);
        await player.PauseAsync(None);
        var count = transport.Sends.Count;
        Assert.Equal(0, player.QueuedFrames);
        Assert.False(player.IsStarted);
        await Task.Delay(100);
        Assert.Equal(count, transport.Sends.Count);
        Assert.Equal(transport.Sends.Last().Timestamp + 352u, player.Timeline.NextTimestamp);

        // While paused with no members, writes are discarded; FlushAsync while paused keeps the player paused.
        await player.FlushAsync(None);
        Assert.False(player.IsStarted);
        Assert.Equal(count, transport.Sends.Count);
        await player.StartAsync(None);
        await WaitAsync(() => transport.Sends.Count > count + 3);
        await player.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => player.PauseAsync(None).GetAwaiter().GetResult());
    }

    private static GroupPlayer NewPlayer(RecordingTransport transport, GroupPlayerOptions? options = null) =>
        new(transport, (_, _) => throw new InvalidOperationException("no receivers in pacing tests"), options ?? new GroupPlayerOptions(), NullLogger<GroupPlayer>.Instance);

    private static Task WriteOnThread(GroupPlayer player, short[] pcm)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                player.Write(pcm);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        { IsBackground = true }.Start();
        return done.Task;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "condition not met in time");
            await Task.Delay(10);
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException)
        {
        }
    }

    /// <summary>An <see cref="IStreamTransport"/> that records what the pacer hands over (and optionally decodes it).</summary>
    private sealed class RecordingTransport(bool decode = false) : IStreamTransport
    {
        private readonly AlacDecoder _decoder = new(AlacMagicCookie.AirPlay);

        public ConcurrentQueue<(ushort Sequence, uint Timestamp, ulong Ntp, short[]? Pcm)> Sends { get; } = new();

        public ConcurrentQueue<string> Calls { get; } = new();

        public IMasterClock Clock { get; } = new MasterClock();

        public StreamType Type => StreamType.Realtime;

        public TimingProtocol TimingProtocol => TimingProtocol.Ntp;

        public int ControlPort => 6001;

        public int TimingPort => 6002;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void AddReceiver(string receiverId, ReceiverStreamEndpoint endpoint) => Calls.Enqueue("add " + receiverId);

        public void RemoveReceiver(string receiverId) => Calls.Enqueue("remove " + receiverId);

        public void Resync(string receiverId) => Calls.Enqueue("resync " + receiverId);

        public void Send(ushort sequence, uint timestamp, ReadOnlySpan<byte> alacFrame, ulong ntpNow)
        {
            short[]? pcm = null;
            if (decode)
            {
                pcm = new short[352 * 2];
                var bits = new BitBuffer(alacFrame.ToArray(), alacFrame.Length);
                Assert.Equal(352, _decoder.Decode(ref bits, pcm, 352, 2));
            }

            Sends.Enqueue((sequence, timestamp, ntpNow, pcm));
        }

        public void SendSync(uint nextTimestamp, ulong ntpNow, bool first) => Calls.Enqueue("sync");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
