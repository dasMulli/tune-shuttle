using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Streams;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Receiver.Tests.Streams;

/// <summary>
/// The product's realtime reader on loopback (SPEC-receiver.md 5.1, 11.1, 13 wave 2): our own
/// <see cref="RealtimeStreamTransport"/> under PTP (its <see cref="PtpMaster"/> on a test <see cref="PtpPort"/>, the
/// receiver's <see cref="PtpSlave"/> on a second one, both on port 0) streams an ALAC tone into a
/// <see cref="RealtimeStreamReceiver"/> feeding a started <see cref="PlayoutBuffer"/> with a recording
/// <see cref="IReceiverAudioOutput"/>. The buffer's <c>Lpipe</c> is shorter than the sender's 1.75 s lead, so <c>D</c> is 0
/// and frames are released 0.5 s after they arrive: time enough for retransmissions on loopback.
/// </summary>
public sealed class RealtimeStreamLoopbackTests
{
    private const int L = RealtimeStreamConstants.DefaultLatencyFrames;
    private const int Frames = RealtimeStreamConstants.FramesPerPacket;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Every_frame_of_a_tone_arrives_once_and_in_order()
    {
        await using var h = await Harness.StartAsync();
        var tone = Tone(Frames * 376, 0); // about 3 s
        const uint start = 4_294_900_000; // the timestamp wraps mid-stream
        await h.StreamAsync(tone, sequence: 1000, timestamp: start);

        await h.Output.WaitForAsync(tone.Length);
        Assert.Equal(tone.Length / 2 / Frames, h.Observer.Sequences.Count);
        Assert.Equal(Enumerable.Range(0, 376).Select(i => (ushort)(1000 + i)), h.Observer.Sequences);
        AssertContains(h.Output.Samples(), tone);

        var stats = h.Receiver.Statistics;
        Assert.Equal(376, stats.PacketsDecoded);
        Assert.Equal(0, stats.RetransmitRequestsSent);
        Assert.Equal(0, stats.DuplicatePackets + stats.EarlyPackets + stats.LatePackets + stats.BadPackets + stats.PacketsLost);
        Assert.True(stats.Syncs >= 3);
        Assert.Equal(1, stats.AnchorsApplied); // periodic syncs refresh the buffer's mapping, they do not re-anchor
        Assert.Single(h.Observer.Anchors);
        Assert.Equal(h.Master.ClockId, h.Observer.Anchors[0].ClockId);
        Assert.Equal(unchecked(start - L), h.Observer.Anchors[0].NowFrame);
        Assert.Equal(0, h.Buffer.Statistics.MissingFrames);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
        Assert.True(h.Output.Resumes >= 1); // the first released frame resumed the output (5.1)
    }

    [Fact]
    public async Task Every_seventh_packet_withheld_is_requested_with_0xD5_and_recovered()
    {
        await using var h = await Harness.StartAsync();
        h.Transport.WithholdEveryNthPacket = 7;
        var tone = Tone(Frames * 200, 1);
        await h.StreamAsync(tone, sequence: 65500, timestamp: 0); // the sequence wraps mid-stream

        await h.Output.WaitForAsync(tone.Length);
        AssertContains(h.Output.Samples(), tone);
        var withheld = h.Transport.PacketsWithheld;
        Assert.True(withheld >= 25);
        var stats = h.Receiver.Statistics;
        Assert.Equal(200, stats.PacketsDecoded);
        Assert.Equal(withheld, stats.PacketsRecovered);
        Assert.True(stats.RetransmitRequestsSent >= withheld);
        Assert.Equal(withheld, h.Observer.Answered.Count);
        Assert.All(h.Observer.Answered, a => Assert.True(a.Wrapped)); // our sender answers with 0xD6 on the control port
        Assert.Equal(0, stats.PacketsLost);
        Assert.Equal(0, h.Buffer.Statistics.MissingFrames);
        Assert.True(h.Control.RetransmitRequestsReceived >= withheld);
        Assert.Equal(0, h.Control.UnmatchedRequests);
    }

    [Fact]
    public async Task A_flush_followed_by_new_audio_re_anchors_on_the_next_sync()
    {
        await using var h = await Harness.StartAsync();
        var before = Tone(Frames * 60, 2);
        var after = Tone(Frames * 120, 3);
        var next = await h.StreamAsync(before, sequence: 10, timestamp: 50_000);
        await h.Output.WaitForAsync(before.Length);

        // FLUSH (SPEC-receiver.md 4.5, 5.5): the stream's receiver and buffer are flushed; the sender resyncs, so the
        // next packet carries the marker and comes after a 0x90 sync; its timeline jumps 10 s ahead.
        h.Receiver.Flush(h, null);
        h.Buffer.Flush(null);
        h.Transport.Resync("receiver");
        var jumped = 50_000u + (uint)(60 * Frames) + (10 * 44100);
        await h.StreamAsync(after, (ushort)(next + 60), jumped);
        await h.Output.WaitForAsync(before.Length + after.Length);

        Assert.Equal(2, h.Receiver.Statistics.AnchorsApplied);
        Assert.Equal(2, h.Observer.Anchors.Count);
        Assert.Equal(unchecked(jumped - L), h.Observer.Anchors[1].NowFrame);
        Assert.True(h.Observer.Anchors[1].First);
        AssertContains(h.Output.Samples(), after);
        Assert.Equal(0, h.Receiver.Statistics.EarlyPackets + h.Receiver.Statistics.LatePackets);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
        Assert.True(h.Output.Flushes >= 1);
    }

    [Fact]
    public async Task Packets_more_than_two_seconds_early_or_already_late_are_counted_and_dropped()
    {
        await using var h = await Harness.StartAsync();
        var tone = Tone(Frames * 40, 4);
        const uint start = 900_000;
        var next = await h.StreamAsync(tone, sequence: 300, timestamp: start);
        var nextTimestamp = start + (uint)tone.Length / 2;

        // Right behind the sender's last packet, under the same shk: one 5 s ahead (a corrupt timestamp), one 3 s behind
        // (its release is long past), then one on time.
        using var builder = new RtpAudioPacketBuilder(h.Key, 0);
        var alac = new AlacEncoder().Encode(Tone(Frames, 5));
        await h.InjectAsync(builder, next, nextTimestamp + (5 * 44100), alac);
        await h.InjectAsync(builder, (ushort)(next + 1), nextTimestamp - (3 * 44100), alac);
        await h.InjectAsync(builder, (ushort)(next + 2), nextTimestamp + (2 * Frames), alac);

        await WaitUntilAsync(() => h.Receiver.Statistics.PacketsDecoded >= 41, "the on-time packet");
        var stats = h.Receiver.Statistics;
        Assert.Equal(1, stats.EarlyPackets);
        Assert.Equal(1, stats.LatePackets);
        Assert.Equal(41, stats.PacketsDecoded);
        Assert.Equal(new[] { (next, false), ((ushort)(next + 1), true) }, h.Observer.Rejected);
        Assert.DoesNotContain(next, h.Observer.Sequences);
        Assert.Contains((ushort)(next + 2), h.Observer.Sequences);
    }

    [Fact]
    public async Task A_gap_the_sender_never_answers_is_requested_three_times_then_given_up()
    {
        await using var run = await UnansweredGapAsync();
        var (receiver, observer) = (run.Receiver, run.Observer);
        await WaitUntilAsync(() => receiver.Statistics.PacketsLost == 2, "the gap given up");
        await Task.Delay(150);
        Assert.Equal(3, receiver.Statistics.RetransmitRequestsSent);
        Assert.Equal(6, receiver.Statistics.PacketsRequested);
        run.Sender.Blocking = false;
        Assert.Equal(0, run.Sender.Available); // no fourth request

        // The given-up packets arriving now are dropped, not played out of order.
        await run.SendAsync(2);
        await run.SendAsync(5);
        await WaitUntilAsync(() => observer.Sequences.Count == 4, "packet 5");
        Assert.Equal(new ushort[] { 0, 1, 4, 5 }, observer.Sequences);
        Assert.Equal(1, receiver.Statistics.DuplicatePackets);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task A_gap_is_requested_within_30_ms_and_repeated_no_sooner_than_30_ms_later()
    {
        await using var run = await UnansweredGapAsync();
        Assert.True(run.Times[0] < TimeSpan.FromMilliseconds(30), $"first request after {run.Times[0].TotalMilliseconds} ms");
        Assert.True(run.Times[1] - run.Times[0] >= TimeSpan.FromMilliseconds(30));
        Assert.True(run.Times[2] - run.Times[1] >= TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task A_sender_timeline_that_moves_between_periodic_syncs_is_followed_without_late_frames()
    {
        await using var h = await Harness.StartAsync();
        var before = Tone(Frames * 125, 8); // 1 s
        var after = Tone(Frames * 250, 9); // 2 s
        const uint start = 300_000;
        var next = await h.StreamAsync(before, sequence: 2000, timestamp: start);

        // A pacing slip on the sender: 1 s of wall time passes with no audio, then the timeline continues where it was
        // (no marker, no FLUSH) and the next periodic 0xD7 carries the moved (now_frame, T) relation. Under the old
        // anchor every following frame would be 1 s behind its release time, well past LateThreshold.
        await Task.Delay(TimeSpan.FromSeconds(1));
        var resume = start + (uint)(before.Length / 2);
        h.Transport.SendSync(resume, h.Clock.NtpNow, first: false);
        await h.StreamAsync(after, next, resume);
        await h.Output.WaitForAsync(before.Length + after.Length);

        AssertContains(h.Output.Samples(), after);
        var stats = h.Receiver.Statistics;
        Assert.Equal(1, stats.AnchorsApplied); // no re-anchor was signalled: the buffer followed the periodic sync
        Assert.Equal(0, stats.LatePackets + stats.EarlyPackets + stats.PacketsLost);
        Assert.Equal(375, stats.PacketsDecoded);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
    }

    [Fact]
    public async Task Before_the_first_anchor_a_corrupt_timestamp_is_dropped_and_frames_are_enqueued_with_their_sequence()
    {
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new RecordingTarget();
        var observer = new Observer();
        await using var receiver = new RealtimeStreamReceiver(new RealtimeStreamReceiverOptions { Name = "test" }, target, observer, NullLogger.Instance);
        receiver.Start();
        var key = RandomNumberGenerator.GetBytes(32);
        receiver.BeginStream(this, key, (IPEndPoint)sender.LocalEndPoint!);
        var data = new IPEndPoint(IPAddress.Loopback, receiver.DataPort);
        using var builder = new RtpAudioPacketBuilder(key, 0);
        var pcm = Tone(Frames, 7);
        var alac = new AlacEncoder().Encode(pcm);
        var packet = new byte[RealtimeStreamConstants.MaxPacketSize];
        async Task SendAsync(ushort sequence, uint timestamp)
        {
            var n = builder.Build(packet, sequence, timestamp, sequence == 7, alac);
            await sender.SendToAsync(packet.AsMemory(0, n), SocketFlags.None, data);
        }

        await SendAsync(7, 1000);
        await SendAsync(8, 1000 + Frames + (3 * 44100)); // 3 s ahead of what packet 7 predicts
        await SendAsync(9, 1000 + (2 * Frames));
        await WaitUntilAsync(() => target.Enqueued.Count == 2, "two enqueued packets");
        Assert.Equal(new[] { (1000u, 7u), (1000u + (2 * Frames), 9u) }, target.Enqueued.Select(e => (e.Timestamp, e.Sequence)));
        Assert.Equal(pcm, target.Enqueued[0].Pcm);
        Assert.Equal(1, receiver.Statistics.EarlyPackets);
        Assert.Empty(target.Anchors);

        // Every 0xD7 sync reaches the target (the buffer tells a refresh from a re-anchor); the first one and one flagged
        // first (0x90) are the receiver's re-anchors.
        var sync = new byte[PtpSyncPacket.Size];
        var control = new IPEndPoint(IPAddress.Loopback, receiver.ControlPort);
        var now = target.Clock.NowNanoseconds;
        foreach (var (first, frame) in new[] { (false, 1000u), (false, 45100u), (true, 90_000u) })
        {
            new PtpSyncPacket(first, frame, now, frame + L, 0x1234).Write(sync);
            await sender.SendToAsync(sync, SocketFlags.None, control);
        }

        await WaitUntilAsync(() => receiver.Statistics.Syncs == 3, "three syncs");
        await WaitUntilAsync(() => target.Anchors.Count == 3, "three anchors");
        Assert.Equal(new[] { 1000u, 45100u, 90_000u }, target.Anchors.Select(a => a.Rtp));
        Assert.All(target.Anchors, a => Assert.Equal(0x1234UL, a.Timeline));
        Assert.Equal(2, receiver.Statistics.AnchorsApplied);
        Assert.Equal(new[] { 1000u, 90_000u }, observer.Anchors.Select(a => a.NowFrame));
    }

    /// <summary>
    /// A receiver without playout target whose sender (a bare socket playing the sender's control port) never answers:
    /// packets 0 and 1, then 4 (2 and 3 missing); returns once the three 0xD5 requests arrived, with their times after 4 was sent.
    /// </summary>
    private static async Task<UnansweredGap> UnansweredGapAsync()
    {
        var run = new UnansweredGap();
        await run.StartAsync();
        await run.SendAsync(0);
        await run.SendAsync(1);
        await WaitUntilAsync(() => run.Observer.Sequences.Count == 2, "two packets");
        var sent = Stopwatch.GetTimestamp();
        await run.SendAsync(4);

        var buffer = new byte[64];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 3; i++)
        {
            var got = await run.Sender.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            run.Times.Add(Stopwatch.GetElapsedTime(sent));
            Assert.Equal(RetransmitRequest.Size, got.ReceivedBytes); // the 8-byte form (SPEC-audio.md 2.4)
            Assert.True(RetransmitRequest.TryParse(buffer.AsSpan(0, got.ReceivedBytes), out var request));
            Assert.Equal(new RetransmitRequest(2, 2), request);
            Assert.Equal(run.Receiver.ControlPort, ((IPEndPoint)got.RemoteEndPoint).Port);
        }

        return run;
    }

    private static short[] Tone(int frames, int seed)
    {
        var pcm = new short[frames * 2];
        var frequency = 440.0 + (seed * 110);
        for (var i = 0; i < frames; i++)
        {
            var value = (short)(8000 * Math.Sin(2 * Math.PI * frequency * i / 44100.0));
            pcm[2 * i] = value;
            pcm[(2 * i) + 1] = (short)-value;
        }

        return pcm;
    }

    /// <summary>The output holds <paramref name="expected"/> as one contiguous run (lossless ALAC: sample-exact).</summary>
    private static void AssertContains(short[] recorded, short[] expected)
    {
        var span = recorded.AsSpan();
        for (var start = 0; start + expected.Length <= span.Length; start += 2)
        {
            if (span[start] == expected[0] && span[start + 1] == expected[1] && span.Slice(start, expected.Length).SequenceEqual(expected))
            {
                return;
            }
        }

        Assert.Fail($"the {expected.Length / 2} expected frames are not in the {recorded.Length / 2} recorded ones as one run");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Patience.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                Assert.Fail($"timed out waiting for {what}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>The bare receiver and silent sender socket of <see cref="UnansweredGapAsync"/>.</summary>
    private sealed class UnansweredGap : IAsyncDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        private readonly RtpAudioPacketBuilder _builder;
        private readonly byte[] _alac = new AlacEncoder().Encode(Tone(Frames, 6));
        private readonly byte[] _packet = new byte[RealtimeStreamConstants.MaxPacketSize];

        public UnansweredGap()
        {
            _builder = new RtpAudioPacketBuilder(_key, 0);
            Sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Receiver = new RealtimeStreamReceiver(
                new RealtimeStreamReceiverOptions { Name = "test", RetransmitInterval = TimeSpan.FromMilliseconds(40) },
                Observer,
                NullLogger.Instance);
        }

        public Socket Sender { get; } = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        public Observer Observer { get; } = new();

        public RealtimeStreamReceiver Receiver { get; }

        public List<TimeSpan> Times { get; } = [];

        public Task StartAsync()
        {
            Receiver.Start();
            Receiver.BeginStream(this, _key, (IPEndPoint)Sender.LocalEndPoint!);
            return Task.CompletedTask;
        }

        public async Task SendAsync(ushort sequence)
        {
            var n = _builder.Build(_packet, sequence, (uint)(sequence * Frames), sequence == 0, _alac);
            await Sender.SendToAsync(_packet.AsMemory(0, n), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, Receiver.DataPort));
        }

        public async ValueTask DisposeAsync()
        {
            await Receiver.DisposeAsync();
            _builder.Dispose();
            Sender.Dispose();
        }
    }

    /// <summary>Our sender, its grandmaster, the receiver's slave, the product receiver, its buffer and a recording output, all on loopback.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly AlacEncoder _encoder = new();
        private readonly List<IAsyncDisposable> _disposables = [];
        private readonly Socket _injector = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        private Harness(MasterClock clock)
        {
            Clock = clock;
            Local = MonotonicClock.FromMasterClock(clock);
        }

        public MasterClock Clock { get; }

        public IMonotonicClock Local { get; }

        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);

        public PtpMaster Master { get; private set; } = null!;

        public ControlPort Control { get; private set; } = null!;

        public RealtimeStreamTransport Transport { get; private set; } = null!;

        public PlayoutBuffer Buffer { get; private set; } = null!;

        public RealtimeStreamReceiver Receiver { get; private set; } = null!;

        public RecordingOutput Output { get; } = new();

        public Observer Observer { get; } = new();

        public static async Task<Harness> StartAsync()
        {
            var clock = new MasterClock();
            var h = new Harness(clock);
            var portOptions = new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 };
            var masterPort = h.Track(new PtpPort(portOptions));
            var slavePort = h.Track(new PtpPort(portOptions));
            h.Master = h.Track(new PtpMaster(clock, masterPort, NullLogger<PtpMaster>.Instance));
            var timing = h.Track(new TimingResponder(clock, bindAddress: IPAddress.Loopback));
            h.Control = h.Track(new ControlPort(bindAddress: IPAddress.Loopback));
            h.Transport = h.Track(new RealtimeStreamTransport(clock, timing, h.Control, L, h.Master));
            await h.Transport.StartAsync(CancellationToken.None); // starts the control port and the grandmaster

            var slave = h.Track(new PtpSlave(new PtpSlaveOptions
            {
                Port = slavePort,
                SenderAddresses = [IPAddress.Loopback],
                PeerEventPort = masterPort.EventPort,
                PeerGeneralPort = masterPort.GeneralPort,
                LocalClock = h.Local,
            }));
            await slave.StartAsync(CancellationToken.None);
            h.Master.AddPeer(IPAddress.Loopback, slavePort.EventPort, slavePort.GeneralPort);
            await WaitUntilAsync(() => slave.Statistics.PairsSeen >= 4, "the slave following the master");

            // Lpipe 1.25 s < the sender's 1.75 s lead: D = 0, frames released 0.5 s after arrival.
            var playout = new PlayoutOptions { Lpipe = PlayoutOptions.SampleRate * 5 / 4 };
            h.Buffer = h.Track(new PlayoutBuffer(h.Output, h.Local, StreamType.Realtime, slave, playout, NullLogger<PlayoutBuffer>.Instance));
            await h.Buffer.StartAsync(CancellationToken.None);
            h.Receiver = h.Track(new RealtimeStreamReceiver(new RealtimeStreamReceiverOptions { Name = "product", Clock = clock }, h.Buffer, h.Observer, NullLogger.Instance));
            h.Receiver.Start();
            h.Receiver.BeginStream(h, h.Key, new IPEndPoint(IPAddress.Loopback, h.Control.Port));
            h.Transport.AddReceiver(
                "receiver",
                new ReceiverStreamEndpoint(new IPEndPoint(IPAddress.Loopback, h.Receiver.DataPort), new IPEndPoint(IPAddress.Loopback, h.Receiver.ControlPort), h.Key, 0, RetransmitOnDataPort: false));
            h._injector.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return h;
        }

        /// <summary>Sends <paramref name="pcm"/> in 352-frame packets at the real-time pace, each with <c>T</c> = its due time; returns the next sequence.</summary>
        public async Task<ushort> StreamAsync(short[] pcm, ushort sequence, uint timestamp)
        {
            var packets = pcm.Length / 2 / Frames;
            var start = Stopwatch.GetTimestamp();
            var packetTicks = Stopwatch.Frequency * Frames / 44100.0;
            for (var k = 0; k < packets; k++)
            {
                var due = start + (long)(k * packetTicks);
                var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due);
                if (wait > TimeSpan.FromMilliseconds(2))
                {
                    await Task.Delay(wait);
                }

                var alac = _encoder.Encode(pcm.AsSpan(k * Frames * 2, Frames * 2));
                Transport.Send(unchecked((ushort)(sequence + k)), unchecked(timestamp + (uint)(k * Frames)), alac, Clock.NtpNow);
            }

            return unchecked((ushort)(sequence + packets));
        }

        public async Task InjectAsync(RtpAudioPacketBuilder builder, ushort sequence, uint timestamp, byte[] alac)
        {
            var packet = new byte[RealtimeStreamConstants.MaxPacketSize];
            var n = builder.Build(packet, sequence, timestamp, false, alac);
            await _injector.SendToAsync(packet.AsMemory(0, n), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, Receiver.DataPort));
        }

        public async ValueTask DisposeAsync()
        {
            _injector.Dispose();
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                await _disposables[i].DisposeAsync();
            }
        }

        private T Track<T>(T disposable)
            where T : IAsyncDisposable
        {
            _disposables.Add(disposable);
            return disposable;
        }
    }

    /// <summary>Records what the receiver reports (thread-safe snapshots).</summary>
    private sealed class Observer : IRealtimeStreamObserver
    {
        private readonly Lock _lock = new();
        private readonly List<ushort> _sequences = [];
        private readonly List<PtpSyncPacket> _anchors = [];
        private readonly List<(ushort Sequence, bool Wrapped)> _answered = [];
        private readonly List<(ushort Sequence, bool Late)> _rejected = [];

        public IReadOnlyList<ushort> Sequences => Snapshot(_sequences);

        public IReadOnlyList<PtpSyncPacket> Anchors => Snapshot(_anchors);

        public IReadOnlyList<(ushort Sequence, bool Wrapped)> Answered => Snapshot(_answered);

        public IReadOnlyList<(ushort Sequence, bool Late)> Rejected => Snapshot(_rejected);

        public void OnAudioPacket(ushort sequence, uint timestamp, bool marker, ReadOnlySpan<short> pcm) => Add(_sequences, sequence);

        public void OnAnchor(PtpSyncPacket sync) => Add(_anchors, sync);

        public void OnRetransmitAnswered(ushort sequence, bool wrapped) => Add(_answered, (sequence, wrapped));

        public void OnPacketRejected(ushort sequence, uint timestamp, bool late) => Add(_rejected, (sequence, late));

        private void Add<T>(List<T> list, T item)
        {
            lock (_lock)
            {
                list.Add(item);
            }
        }

        private List<T> Snapshot<T>(List<T> list)
        {
            lock (_lock)
            {
                return [.. list];
            }
        }
    }

    /// <summary>A playout target that records (the seam the product fills with <see cref="PlayoutBuffer"/>).</summary>
    private sealed class RecordingTarget : IRealtimePlayoutTarget
    {
        private readonly Lock _lock = new();
        private readonly List<(uint Timestamp, uint Sequence, short[] Pcm)> _enqueued = [];
        private readonly List<(uint Rtp, ulong Sender, ulong Timeline)> _anchors = [];

        public IMonotonicClock Clock { get; } = MonotonicClock.CreateStopwatch();

        public PlayoutOptions Options { get; } = new();

        public IReadOnlyList<(uint Timestamp, uint Sequence, short[] Pcm)> Enqueued
        {
            get
            {
                lock (_lock)
                {
                    return [.. _enqueued];
                }
            }
        }

        public IReadOnlyList<(uint Rtp, ulong Sender, ulong Timeline)> Anchors
        {
            get
            {
                lock (_lock)
                {
                    return [.. _anchors];
                }
            }
        }

        public void Enqueue(uint rtpTimestamp, uint sequence, ReadOnlyMemory<short> frames)
        {
            lock (_lock)
            {
                _enqueued.Add((rtpTimestamp, sequence, frames.ToArray()));
            }
        }

        public void SetAnchor(uint rtpTime, ulong senderTimeNs, ulong timelineId)
        {
            lock (_lock)
            {
                _anchors.Add((rtpTime, senderTimeNs, timelineId));
            }
        }

        public bool IsLate(uint rtpTimestamp) => false;

        public bool TryGetReleaseTime(uint rtpTimestamp, out long releaseNanos)
        {
            releaseNanos = 0;
            return false; // no mapping: the receiver keeps its pre-anchor test
        }
    }

    /// <summary>An <see cref="IReceiverAudioOutput"/> that keeps every written sample and counts the other calls.</summary>
    private sealed class RecordingOutput : IReceiverAudioOutput
    {
        private readonly Lock _lock = new();
        private readonly List<short> _samples = [];
        private int _resumes;
        private int _flushes;

        public int QueuedFrames => 0;

        public int Resumes => Volatile.Read(ref _resumes);

        public int Flushes => Volatile.Read(ref _flushes);

        public short[] Samples()
        {
            lock (_lock)
            {
                return [.. _samples];
            }
        }

        public async Task WaitForAsync(int samples)
        {
            await WaitUntilAsync(
                () =>
                {
                    lock (_lock)
                    {
                        return _samples.Count >= samples;
                    }
                },
                $"{samples / 2} frames in the output");
        }

        public ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _samples.AddRange(frames.Span);
            }

            return ValueTask.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resumes);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushes);
            return Task.CompletedTask;
        }

        public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
