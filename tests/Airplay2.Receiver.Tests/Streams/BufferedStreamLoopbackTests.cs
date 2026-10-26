using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Airplay2.Receiver.Codecs;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Session;
using Airplay2.Receiver.Streams;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Receiver.Tests.Streams;

/// <summary>
/// The buffered stream receiver on loopback (SPEC-receiver.md 5.2, 11.1): our sender's <see cref="BufferedStreamTransport"/>
/// streams an ALAC tone (SSRC <c>0x0000FACE</c>) with immediate and deferred <c>FLUSHBUFFERED</c> (4.7) forwarded by a
/// substitute control session; a test-only raw block writer replays the AAC vector (SSRC <c>0x16000000</c>), unknown SSRCs
/// and a stream large enough to fill the undecoded queue. The playout side is a recorder (the stream's
/// <c>PlayoutBuffer</c> is tested on its own), so the frames compared are exactly what the reader decodes.
/// </summary>
public class BufferedStreamLoopbackTests(ITestOutputHelper output)
{
    private const int Frames = BufferedBlock.AlacFramesPerBlock;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Alac_tone_through_the_transport_arrives_in_order_with_the_first_block_muted()
    {
        await using var rig = await TransportRig.StartAsync();
        const int count = 50;
        for (var i = 0; i < count; i++)
        {
            rig.Send(i, (uint)(i * Frames));
        }

        await WaitUntilAsync(() => rig.Target.Count >= count, "50 decoded blocks");
        var blocks = rig.Target.Snapshot();
        Assert.Equal(Enumerable.Range(0, count).Select(i => (uint)i), blocks.Select(b => b.Sequence));
        Assert.Equal(Enumerable.Range(0, count).Select(i => (uint)(i * Frames)), blocks.Select(b => b.Timestamp));
        Assert.All(blocks[0].Pcm, s => Assert.Equal(0, s));
        for (var i = 1; i < count; i++)
        {
            Assert.Equal(Tone(i * Frames, Frames), blocks[i].Pcm); // ALAC is lossless
        }

        var stats = rig.Receiver.Statistics;
        Assert.Equal(1, stats.Connections);
        Assert.Equal(count, stats.BlocksDecoded);
        Assert.Equal(1, stats.MutedBlocks);
        Assert.Equal(0, stats.BadBlocks + stats.LateBlocks + stats.FlushedBlocks + stats.UnknownSsrcBlocks);
        Assert.Equal("ALAC", stats.Decoder);
        Assert.Equal("ALAC", rig.Target.Decoder);
    }

    [Fact]
    public async Task Immediate_flush_drops_the_queued_blocks_and_mutes_the_first_after_the_seek()
    {
        await using var rig = await TransportRig.StartAsync();
        rig.Target.Room = false; // a full playout buffer: blocks stay undecoded
        for (var i = 0; i < 20; i++)
        {
            rig.Send(i, (uint)(i * Frames));
        }

        await WaitUntilAsync(() => rig.Receiver.Statistics.BlocksReceived == 20, "20 queued blocks");

        // Seek: SETRATEANCHORTIME rate 0, FLUSHBUFFERED {flushUntilSeq: 20} (4.7 immediate form).
        await rig.Transport.FlushReceiverAsync(TransportRig.Id, 20, 1_000_000, CancellationToken.None);
        Assert.Equal([(20u, (uint?)null)], rig.Flushes);
        Assert.Equal(20, rig.Receiver.Statistics.FlushedBlocks);
        Assert.Equal(0, rig.Receiver.Statistics.QueuedBytes);

        rig.Target.Room = true;
        for (var i = 20; i < 30; i++)
        {
            rig.Send(i, (uint)(1_000_000 + ((i - 20) * Frames)));
        }

        await WaitUntilAsync(() => rig.Target.Count >= 10, "10 blocks after the seek");
        await Task.Delay(100);
        var blocks = rig.Target.Snapshot();
        Assert.Equal(Enumerable.Range(20, 10).Select(i => (uint)i), blocks.Select(b => b.Sequence));
        Assert.All(blocks[0].Pcm, s => Assert.Equal(0, s));
        for (var i = 1; i < blocks.Count; i++)
        {
            Assert.Equal(Tone(1_000_000 + (i * Frames), Frames), blocks[i].Pcm);
        }

        Assert.Equal(1, rig.Receiver.Statistics.MutedBlocks);
    }

    [Fact]
    public async Task Splice_drops_from_the_from_block_up_to_the_until_block_without_a_flush()
    {
        await using var rig = await TransportRig.StartAsync();
        rig.Target.Room = false;
        for (var i = 0; i < 10; i++)
        {
            rig.Send(i, (uint)(i * Frames));
        }

        await WaitUntilAsync(() => rig.Receiver.Statistics.BlocksReceived == 10, "10 queued blocks");

        // Deferred form whose from block has arrived: 5, 6, 7 are dropped from the queue.
        await rig.Transport.FlushRangeAsync(5, 5 * Frames, 8, 8 * Frames, CancellationToken.None);
        Assert.Equal([(8u, (uint?)5u)], rig.Flushes);
        Assert.Equal(3, rig.Receiver.Statistics.FlushedBlocks);

        // A second splice registered before its from block arrives: 15, 16, 17 are dropped on arrival.
        await rig.Transport.FlushRangeAsync(15, 15 * Frames, 18, 18 * Frames, CancellationToken.None);
        rig.Target.Room = true;
        for (var i = 10; i < 21; i++)
        {
            rig.Send(i, (uint)(i * Frames));
        }

        uint[] expected = [0, 1, 2, 3, 4, 8, 9, 10, 11, 12, 13, 14, 18, 19, 20];
        await WaitUntilAsync(() => rig.Target.Count >= expected.Length, "the spliced stream");
        await Task.Delay(100);
        var blocks = rig.Target.Snapshot();
        Assert.Equal(expected, blocks.Select(b => b.Sequence));
        foreach (var block in blocks)
        {
            var muted = block.Sequence is 0 or 8 or 18; // start and the block after each gap
            Assert.Equal(muted ? new short[Frames * 2] : Tone((int)block.Timestamp, Frames), block.Pcm);
        }

        var stats = rig.Receiver.Statistics;
        Assert.Equal(6, stats.FlushedBlocks);
        Assert.Equal(3, stats.MutedBlocks);
        Assert.DoesNotContain(rig.Session.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IReceiverSession.SetRateAnchorTimeAsync) && (int)c.GetArguments()[0]! == 0);
    }

    [Fact]
    public async Task Late_blocks_are_dropped_before_decrypting()
    {
        await using var rig = await TransportRig.StartAsync();
        rig.Target.Late = ts => ts < 10 * Frames;
        for (var i = 0; i < 20; i++)
        {
            rig.Send(i, (uint)(i * Frames));
        }

        await WaitUntilAsync(() => rig.Target.Count >= 10, "the blocks that are not late");
        await Task.Delay(100);
        var blocks = rig.Target.Snapshot();
        Assert.Equal(Enumerable.Range(10, 10).Select(i => (uint)i), blocks.Select(b => b.Sequence));
        Assert.All(blocks[0].Pcm, s => Assert.Equal(0, s));
        Assert.Equal(Tone(11 * Frames, Frames), blocks[1].Pcm);
        Assert.Equal(10, rig.Receiver.Statistics.LateBlocks);
        Assert.Equal(0, rig.Receiver.Statistics.BadBlocks);
    }

    [Fact]
    public async Task Aac_vector_blocks_decode_to_the_expected_rms()
    {
        var factory = new FfmpegAacDecoderFactory(LibraryDirectory());
        if (!factory.TryCreate(out var decoder, out var reason) || !factory.TryCreate(out var fallbackDecoder, out reason))
        {
            output.WriteLine($"skipped: libavcodec not found ({reason}); run tools/get-ffmpeg.ps1 or set AIRPLAY_FFMPEG_PATH");
            return;
        }

        var vector = AacVector.Load();
        foreach (var (ssrc, audioFormat, dec) in new[] { (BufferedBlock.SsrcAac, (ulong)ReceiverStreamFormats.Alac, decoder), (0x0BADF00Du, (ulong)ReceiverStreamFormats.AacLc, fallbackDecoder) })
        {
            var target = new RecordingTarget();
            await using var receiver = new BufferedStreamReceiver(target, dec, null, NullLogger.Instance);
            await receiver.StartAsync(RawBlockWriter.Key, audioFormat, CancellationToken.None);
            await using (var writer = await RawBlockWriter.ConnectAsync(receiver.DataPort))
            {
                for (var i = 0; i < vector.Blocks.Count; i++)
                {
                    await writer.WriteAsync(ssrc, (uint)i, (uint)(i * BufferedBlock.AacFramesPerBlock), vector.Blocks[i]);
                }

                await WaitUntilAsync(() => receiver.Statistics.BlocksDecoded + receiver.Statistics.BadBlocks >= vector.Blocks.Count, "the AAC vector");
            }

            var pcm = target.Snapshot().SelectMany(b => b.Pcm).ToList();
            var rms = Rms(pcm);
            output.WriteLine($"SSRC 0x{ssrc:X8}: {vector.Blocks.Count} blocks -> {pcm.Count / 2} frames, RMS {rms:F1} (expected {vector.ExpectedRms})");
            Assert.Equal(0, receiver.Statistics.BadBlocks);
            Assert.Equal(1, receiver.Statistics.MutedBlocks);
            Assert.Equal("AAC-LC (libavcodec)", receiver.Statistics.Decoder);
            Assert.Equal(ssrc == BufferedBlock.SsrcAac ? 0 : vector.Blocks.Count, receiver.Statistics.UnknownSsrcBlocks);
            Assert.InRange(pcm.Count / 2, vector.ExpectedFrames - (2 * BufferedBlock.AacFramesPerBlock), vector.ExpectedFrames + BufferedBlock.AacFramesPerBlock);
            Assert.InRange(rms, vector.ExpectedRms * 0.9, vector.ExpectedRms * 1.1);
        }
    }

    [Fact]
    public async Task Aac_blocks_without_a_decoder_are_dropped_and_counted()
    {
        var target = new RecordingTarget();
        await using var receiver = new BufferedStreamReceiver(target, null, null, NullLogger.Instance);
        await receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.Alac, CancellationToken.None);
        await using var writer = await RawBlockWriter.ConnectAsync(receiver.DataPort);
        for (var i = 0; i < 5; i++)
        {
            await writer.WriteAsync(BufferedBlock.SsrcAac, (uint)i, (uint)(i * 1024), new byte[200]);
        }

        await WaitUntilAsync(() => receiver.Statistics.BadBlocks == 5, "5 dropped AAC blocks");
        Assert.Equal(0, target.Count);
    }

    [Fact]
    public async Task Unknown_ssrc_falls_back_to_the_setup_format_or_is_dropped_without_one()
    {
        var encoder = new AlacEncoder();
        var frames = Enumerable.Range(0, 6).Select(i => encoder.Encode(Tone(i * Frames, Frames))).ToList();

        var target = new RecordingTarget();
        await using (var receiver = new BufferedStreamReceiver(target, null, null, NullLogger.Instance))
        {
            await receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.Alac, CancellationToken.None);
            await using var writer = await RawBlockWriter.ConnectAsync(receiver.DataPort);
            for (var i = 0; i < frames.Count; i++)
            {
                await writer.WriteAsync(0x12345678, (uint)i, (uint)(i * Frames), frames[i]);
            }

            await WaitUntilAsync(() => target.Count >= frames.Count, "ALAC by the SETUP format");
            var blocks = target.Snapshot();
            for (var i = 1; i < blocks.Count; i++)
            {
                Assert.Equal(Tone(i * Frames, Frames), blocks[i].Pcm);
            }

            Assert.Equal(frames.Count, receiver.Statistics.UnknownSsrcBlocks);
        }

        var none = new RecordingTarget();
        await using (var receiver = new BufferedStreamReceiver(none, null, null, NullLogger.Instance))
        {
            await receiver.StartAsync(RawBlockWriter.Key, 0x200000, CancellationToken.None); // not one of R3's
            await using var writer = await RawBlockWriter.ConnectAsync(receiver.DataPort);
            for (var i = 0; i < frames.Count; i++)
            {
                await writer.WriteAsync(0x12345678, (uint)i, (uint)(i * Frames), frames[i]);
            }

            await WaitUntilAsync(() => receiver.Statistics.BadBlocks == frames.Count, "dropped unknown blocks");
            Assert.Equal(0, none.Count);
        }
    }

    [Fact]
    public async Task A_full_undecoded_queue_pauses_reading_until_the_slow_consumer_takes_more()
    {
        const int cap = 128 * 1024;
        const int count = 2000;
        var noise = new short[Frames * 2];
        var random = new Random(7);
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (short)random.Next(-12000, 12000);
        }

        var frame = new AlacEncoder().Encode(noise);
        var target = new RecordingTarget { Room = false };
        await using var receiver = new BufferedStreamReceiver(target, null, new BufferedStreamReceiverOptions { AudioBufferSize = cap }, NullLogger.Instance);
        await receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.Alac, CancellationToken.None);
        await using var writer = await RawBlockWriter.ConnectAsync(receiver.DataPort);
        var total = (long)count * BufferedAudioPacket.PacketLength(frame.Length);
        var writing = Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
            {
                await writer.WriteAsync(BufferedBlock.SsrcAlac, (uint)i, (uint)(i * Frames), frame);
            }
        });

        await WaitUntilAsync(() => receiver.Statistics.ReadPauses >= 1, "reading paused");
        await Task.Delay(300);
        var first = receiver.Statistics;
        await Task.Delay(300);
        var second = receiver.Statistics;
        output.WriteLine($"paused: {second.BytesReceived} of {total} bytes read, {second.QueuedBytes} queued, writer done: {writing.IsCompleted}");
        Assert.InRange(second.QueuedBytes, 1, cap);
        Assert.Equal(first.BytesReceived, second.BytesReceived); // the socket is not read while the queue is full
        Assert.True(second.BytesReceived < total / 2);
        Assert.Equal(0, target.Count);

        target.Room = true;
        await writing.WaitAsync(Wait);
        await WaitUntilAsync(() => target.Count == count, "every block after the consumer caught up");
        var stats = receiver.Statistics;
        Assert.Equal(total, stats.BytesReceived);
        Assert.Equal(count, stats.BlocksDecoded);
        Assert.Equal(1, stats.MutedBlocks);
        Assert.Equal(noise, target.Snapshot()[^1].Pcm);
    }

    [Fact]
    public async Task Into_a_playout_buffer_blocks_wait_undecoded_beyond_the_decode_ahead()
    {
        var sink = Substitute.For<IReceiverAudioOutput>();
        await using var buffer = new PlayoutBuffer(sink, MonotonicClock.CreateStopwatch(), StreamType.Buffered);
        await buffer.StartAsync(CancellationToken.None);
        await using var receiver = new BufferedStreamReceiver(buffer, null, new BufferedStreamReceiverOptions { DecodeAhead = TimeSpan.FromMilliseconds(500) }, NullLogger.Instance);
        await receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.Alac, CancellationToken.None);
        var encoder = new AlacEncoder();
        await using var writer = await RawBlockWriter.ConnectAsync(receiver.DataPort);
        const int count = 200; // 1.6 s; a buffered stream holds everything until its first SETRATEANCHORTIME (4.7)
        for (var i = 0; i < count; i++)
        {
            await writer.WriteAsync(BufferedBlock.SsrcAlac, (uint)i, (uint)(i * Frames), encoder.Encode(Tone(i * Frames, Frames)));
        }

        await WaitUntilAsync(() => receiver.Statistics.BlocksReceived == count && buffer.Statistics.BufferedMs >= 500, "the decode-ahead filled");
        await Task.Delay(200);
        var stats = receiver.Statistics;
        output.WriteLine($"decoded {stats.BlocksDecoded} of {count}, {buffer.Statistics.BufferedMs} ms in the buffer, {stats.QueuedBytes} bytes undecoded");
        Assert.InRange(buffer.Statistics.BufferedMs, 500, 500 + 8);
        Assert.InRange(stats.BlocksDecoded, 60, 70);
        Assert.True(stats.QueuedBytes > 0);
        Assert.Equal(0, stats.LateBlocks); // no anchor yet: nothing is late
        Assert.Equal("ALAC", buffer.Statistics.Decoder);
    }

    [Fact]
    public async Task A_reconnected_data_connection_is_accepted_and_its_first_block_muted()
    {
        var encoder = new AlacEncoder();
        var target = new RecordingTarget();
        await using var receiver = new BufferedStreamReceiver(target, null, null, NullLogger.Instance);
        await receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.Alac, CancellationToken.None);
        await using (var first = await RawBlockWriter.ConnectAsync(receiver.DataPort))
        {
            for (var i = 0; i < 3; i++)
            {
                await first.WriteAsync(BufferedBlock.SsrcAlac, (uint)i, (uint)(i * Frames), encoder.Encode(Tone(i * Frames, Frames)));
            }

            await WaitUntilAsync(() => target.Count == 3, "the first connection's blocks");
        }

        // The next connection continues the sequence; the reconnect alone is the discontinuity (5.2).
        await using var second = await RawBlockWriter.ConnectAsync(receiver.DataPort);
        await WaitUntilAsync(() => receiver.Statistics.Connections == 2, "the second accept");
        for (var i = 3; i < 6; i++)
        {
            await second.WriteAsync(BufferedBlock.SsrcAlac, (uint)i, (uint)(i * Frames), encoder.Encode(Tone(i * Frames, Frames)));
        }

        await WaitUntilAsync(() => target.Count == 6, "the second connection's blocks");
        var blocks = target.Snapshot();
        Assert.All(blocks[3].Pcm, s => Assert.Equal(0, s));
        Assert.Equal(Tone(4 * Frames, Frames), blocks[4].Pcm);
        Assert.Equal(Tone(5 * Frames, Frames), blocks[5].Pcm);
        var stats = receiver.Statistics;
        Assert.Equal(2, stats.Connections);
        Assert.Equal(2, stats.MutedBlocks);
    }

    [Fact]
    public async Task Aac_decoder_is_reset_before_the_first_block_after_a_start_a_flush_and_a_gap()
    {
        var decoder = new CountingAacDecoder();
        var target = new RecordingTarget();
        var receiver = new BufferedStreamReceiver(target, decoder, null, NullLogger.Instance);
        await receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.AacLc, CancellationToken.None);
        var writer = await RawBlockWriter.ConnectAsync(receiver.DataPort);
        var aac = BufferedBlock.AacFramesPerBlock;
        for (var i = 0; i < 4; i++)
        {
            await writer.WriteAsync(BufferedBlock.SsrcAac, (uint)i, (uint)(i * aac), new byte[100]);
        }

        await WaitUntilAsync(() => target.Count == 4, "the first AAC blocks");
        Assert.Equal(1, decoder.Resets);
        Assert.Equal(4, decoder.Decodes);
        Assert.Equal("AAC-LC (libavcodec)", target.Decoder);

        receiver.Flush(null);
        for (var i = 4; i < 8; i++)
        {
            await writer.WriteAsync(BufferedBlock.SsrcAac, (uint)i, (uint)(i * aac), new byte[100]);
        }

        await WaitUntilAsync(() => target.Count == 8, "the AAC blocks after the flush");
        Assert.Equal(2, decoder.Resets);

        for (var i = 9; i < 11; i++) // sequence 8 is missing
        {
            await writer.WriteAsync(BufferedBlock.SsrcAac, (uint)i, (uint)(i * aac), new byte[100]);
        }

        await WaitUntilAsync(() => target.Count == 10, "the AAC blocks after the gap");
        Assert.Equal(3, decoder.Resets);
        Assert.Equal(3, receiver.Statistics.MutedBlocks);
        Assert.Equal(10, decoder.Decodes);

        await writer.DisposeAsync();
        await receiver.DisposeAsync();
        Assert.True(decoder.Disposed);
    }

    [Fact]
    public async Task Factory_buffered_stream_holds_its_frames_until_the_anchor_and_creates_an_aac_decoder_under_an_alac_setup()
    {
        var sink = Substitute.For<IReceiverAudioOutput>();
        var aacFactory = Substitute.For<IAacDecoderFactory>();
        aacFactory.TryCreate(out Arg.Any<IAacDecoder?>(), out Arg.Any<string?>()).Returns(call =>
        {
            call[0] = new CountingAacDecoder();
            call[1] = null;
            return true;
        });
        var factory = new ReceiverStreamFactory(sink, MonotonicClock.CreateStopwatch(), aacDecoders: aacFactory);
        var request = new ReceiverStreamRequest(
            StreamType.Buffered,
            RawBlockWriter.Key,
            ReceiverStreamFormats.Alac,
            ReceiverStreamFormats.AlacName,
            new IPEndPoint(IPAddress.Loopback, 9),
            IPAddress.Loopback,
            new Airplay2.Sender.Plist.PlistDictionary());
        await using var stream = await factory.CreateAsync(request, null, CancellationToken.None);
        aacFactory.ReceivedWithAnyArgs(1).TryCreate(out _, out _);
        Assert.Equal(StreamType.Buffered, stream.Type);
        Assert.InRange(stream.DataPort, 1, 65535);

        var encoder = new AlacEncoder();
        await using var writer = await RawBlockWriter.ConnectAsync(stream.DataPort);
        for (var i = 0; i < 40; i++)
        {
            await writer.WriteAsync(BufferedBlock.SsrcAlac, (uint)i, (uint)(i * Frames), encoder.Encode(Tone(i * Frames, Frames)));
        }

        // Well past the anchor-only lead (PlayoutOptions.AnchorLead): a buffered stream still waits for SETRATEANCHORTIME (4.7).
        await Task.Delay(1000);
        await sink.DidNotReceiveWithAnyArgs().WriteAsync(default, default);
    }

    [Fact]
    public async Task Start_binds_both_ports_and_dispose_closes_them()
    {
        var receiver = new BufferedStreamReceiver(new RecordingTarget(), null, null, NullLogger.Instance);
        Assert.Equal(0, receiver.DataPort);
        await Assert.ThrowsAsync<ArgumentException>(() => receiver.StartAsync(new byte[16], ReceiverStreamFormats.Alac, CancellationToken.None));
        await receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.Alac, CancellationToken.None);
        var dataPort = receiver.DataPort;
        Assert.InRange(dataPort, 1, 65535);
        Assert.InRange(receiver.ControlPort, 1, 65535);
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.Alac, CancellationToken.None));
        await receiver.DisposeAsync();

        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, dataPort));
    }

    internal static short[] Tone(int startFrame, int frames)
    {
        var pcm = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)Math.Round(8000 * Math.Sin(2 * Math.PI * 440 * (startFrame + i) / 44100.0));
            pcm[2 * i] = sample;
            pcm[(2 * i) + 1] = sample;
        }

        return pcm;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static double Rms(IReadOnlyCollection<short> samples) =>
        samples.Count == 0 ? 0 : Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Count);

    private static string? LibraryDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("AIRPLAY_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")))
            {
                var candidate = Path.Combine(dir.FullName, "artifacts", "ffmpeg");
                return Directory.Exists(candidate) ? candidate : null;
            }
        }

        return null;
    }

    /// <summary>An AAC decoder stand-in that counts resets and decodes and returns 1024 silent frames per block.</summary>
    private sealed class CountingAacDecoder : IAacDecoder
    {
        private int _resets;
        private int _decodes;

        public int Resets => Volatile.Read(ref _resets);

        public int Decodes => Volatile.Read(ref _decodes);

        public bool Disposed { get; private set; }

        public bool TryDecode(ReadOnlySpan<byte> adtsBlock, Span<short> pcmOut, out int frames)
        {
            Interlocked.Increment(ref _decodes);
            frames = BufferedBlock.AacFramesPerBlock;
            pcmOut[..(frames * 2)].Fill(100);
            return true;
        }

        public void Reset() => Interlocked.Increment(ref _resets);

        public void Dispose() => Disposed = true;
    }

    /// <summary>The recorder on the playout side: decoded blocks in arrival order, a switchable "buffer full" and lateness.</summary>
    private sealed class RecordingTarget : IBufferedPlayoutTarget
    {
        private readonly ConcurrentQueue<(uint Timestamp, uint Sequence, short[] Pcm)> _blocks = new();

        public volatile bool Room = true;

        public Func<uint, bool> Late { get; set; } = _ => false;

        public string? Decoder { get; private set; }

        public int Count => _blocks.Count;

        public List<(uint Timestamp, uint Sequence, short[] Pcm)> Snapshot() => [.. _blocks];

        public bool IsLate(uint rtpTimestamp) => Late(rtpTimestamp);

        public bool WantsMore(TimeSpan decodeAhead) => Room;

        public void ReportDecoder(string decoder) => Decoder = decoder;

        public void Enqueue(uint rtpTimestamp, uint sequence, ReadOnlyMemory<short> frames) => _blocks.Enqueue((rtpTimestamp, sequence, frames.ToArray()));
    }

    /// <summary>
    /// Our sender's buffered transport aimed at the receiver: substitute PTP master and control port (no UDP 319/320), a
    /// substitute control session whose FLUSHBUFFERED is forwarded to the receiver as the control plane does (4.7).
    /// </summary>
    private sealed class TransportRig : IAsyncDisposable
    {
        public const string Id = "loopback-buffered";

        private readonly MasterClock _clock = new();
        private readonly AlacEncoder _encoder = new();
        private readonly ConcurrentQueue<(uint Until, uint? From)> _flushes = new();

        private TransportRig()
        {
            var ptp = Substitute.For<IPtpMaster>();
            ptp.ClockId.Returns(0x0200_00FF_FE00_0B01UL);
            var control = Substitute.For<IControlPort>();
            control.Port.Returns(6001);
            Transport = new BufferedStreamTransport(_clock, ptp, control, new BufferedStreamOptions { AnchorAfterAudio = false }, null);
            Receiver = new BufferedStreamReceiver(Target, null, new BufferedStreamReceiverOptions { Name = "loopback" }, NullLogger.Instance);
            Session = Substitute.For<IReceiverSession>();
            Session.FlushBufferedAsync(Arg.Any<uint>(), Arg.Any<uint>(), Arg.Any<uint?>(), Arg.Any<uint?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var until = call.ArgAt<uint>(0);
                var from = call.ArgAt<uint?>(2);
                _flushes.Enqueue((until, from));
                if (from is { } f)
                {
                    Receiver.Splice(f, until);
                }
                else
                {
                    Receiver.Flush(until);
                }

                return Task.CompletedTask;
            });
        }

        public RecordingTarget Target { get; } = new();

        public BufferedStreamReceiver Receiver { get; }

        public BufferedStreamTransport Transport { get; }

        public IReceiverSession Session { get; }

        public IReadOnlyList<(uint Until, uint? From)> Flushes => [.. _flushes];

        public static async Task<TransportRig> StartAsync()
        {
            var rig = new TransportRig();
            await rig.Receiver.StartAsync(RawBlockWriter.Key, ReceiverStreamFormats.Alac, CancellationToken.None);
            await rig.Transport.StartAsync(CancellationToken.None);
            rig.Transport.AttachSession(Id, rig.Session);
            rig.Transport.AddReceiver(Id, new ReceiverStreamEndpoint(
                new IPEndPoint(IPAddress.Loopback, rig.Receiver.DataPort),
                new IPEndPoint(IPAddress.Loopback, rig.Receiver.ControlPort),
                RawBlockWriter.Key,
                0,
                false));
            return rig;
        }

        public void Send(int sequence, uint timestamp) =>
            Transport.Send((ushort)sequence, timestamp, _encoder.Encode(Tone((int)timestamp, Frames)), _clock.NtpNow);

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            await Receiver.DisposeAsync();
        }
    }

    /// <summary>A test-only sender of raw blocks (any SSRC and payload) on one data connection, framed as SPEC-audio.md 4.2.</summary>
    private sealed class RawBlockWriter : IAsyncDisposable
    {
        public static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Dictionary<uint, RtpAudioPacketBuilder> _builders = [];

        private RawBlockWriter(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<RawBlockWriter> ConnectAsync(int port)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return new RawBlockWriter(client);
        }

        public async Task WriteAsync(uint ssrc, uint sequence, uint timestamp, byte[] payload)
        {
            if (!_builders.TryGetValue(ssrc, out var builder))
            {
                builder = new RtpAudioPacketBuilder(Key, ssrc);
                _builders[ssrc] = builder;
            }

            var packet = new byte[BufferedAudioPacket.PacketLength(payload.Length)];
            BufferedAudioPacket.Build(builder, packet, sequence, timestamp, payload);
            await _stream.WriteAsync(packet);
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _client.Dispose();
            foreach (var builder in _builders.Values)
            {
                builder.Dispose();
            }
        }
    }

    private sealed record AacVector(List<byte[]> Blocks, int ExpectedFrames, double ExpectedRms)
    {
        public static AacVector Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "aac-44100-stereo-1024.json");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            var blocks = root.GetProperty("blocks_base64").EnumerateArray().Select(e => Convert.FromBase64String(e.GetString()!)).ToList();
            return new AacVector(blocks, root.GetProperty("expected_frames").GetInt32(), root.GetProperty("expected_rms_s16").GetDouble());
        }
    }
}
