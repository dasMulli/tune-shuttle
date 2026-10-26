using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Session;
using Xunit;

namespace Airplay2.Receiver.Tests.Playout;

/// <summary>
/// <see cref="PlayoutBuffer"/> on a fake clock with a recording output (SPEC-receiver.md 11.1, R6, 5.3-5.5): the release
/// rule <c>T - Lpipe + D</c> and the learnt offset <c>D</c>, the anchor-only mapping, late and missing frames, drift
/// correction, pause/resume, flush and splice, the 5.4 queue cap and the resume before the first frame. The tests drive
/// <see cref="PlayoutBuffer.PumpAsync"/> in 1 ms steps (the scheduling quantum) instead of the release thread.
/// </summary>
public class PlayoutBufferTests
{
    private const long Ms = 1_000_000;
    private const long Second = 1_000 * Ms;
    private const long Start = 1_000 * Second;
    private const long PtpOffset = 5 * Second; // local = sender + 5 s
    private const int Packet = PlayoutOptions.FramesPerPacket;
    private static readonly long Lpipe = new PlayoutOptions().Lpipe * Second / PlayoutOptions.SampleRate; // 2.2 s

    [Fact]
    public void Lpipe_is_the_sum_of_latency_receiver_lead_and_prefill()
    {
        Assert.Equal(97_020, new PlayoutOptions().Lpipe);
        Assert.Equal(2_200 * Ms, Lpipe);
    }

    [Fact]
    public async Task Buffered_lead_releases_each_frame_at_T_minus_Lpipe_and_never_earlier()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 1000, playTime: Start + 3 * Second);
        h.Buffer.Resume();
        for (var k = 0; k < 50; k++)
        {
            h.Enqueue(1000 + (uint)(k * Packet), (uint)k);
        }

        await h.RunUntilAsync(Start + 3 * Second);

        var writes = h.Output.Writes;
        Assert.Equal(50, writes.Count);
        for (var k = 0; k < writes.Count; k++)
        {
            var release = Start + 3 * Second + FramesNs(k * Packet) - Lpipe;
            Assert.InRange(writes[k].Time, release, release + Ms);
        }

        Assert.Equal(TimeSpan.Zero, h.Buffer.Statistics.StreamOffset);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
        Assert.Equal(new[] { "resume", "write" }, h.Output.Kinds.Distinct());
        Assert.Single(h.Output.Kinds, k => k == "resume");
    }

    [Fact]
    public async Task Realtime_lead_of_1_75_s_plays_D_late_and_drops_nothing()
    {
        var h = new Harness(StreamType.Realtime, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + 1_750 * Ms);

        // Each packet arrives 1.75 s before its play time (SPEC-receiver.md 5.4).
        var arrivals = Enumerable.Range(0, 100).Select(k => (Time: Start + FramesNs(k * Packet), Rtp: (uint)(k * Packet), Seq: (uint)k)).ToList();
        await h.RunUntilAsync(Start + 2 * Second, now =>
        {
            foreach (var a in arrivals.Where(a => a.Time <= now).ToList())
            {
                h.Enqueue(a.Rtp, a.Seq);
                arrivals.Remove(a);
            }
        });

        var stats = h.Buffer.Statistics;
        Assert.Equal(100, h.Output.Writes.Count);
        Assert.Equal(0, stats.LateFrames);
        Assert.Equal(0, stats.MissingFrames);
        Assert.InRange(stats.StreamOffset.TotalMilliseconds, 449, 451);
        Assert.Equal("resume", h.Output.Kinds[0]);
        for (var k = 0; k < 100; k++)
        {
            // T - Lpipe + D = arrival time.
            var release = Start + FramesNs(k * Packet);
            Assert.InRange(h.Output.Writes[k].Time, release, release + Ms);
        }
    }

    [Fact]
    public void Without_PTP_the_first_anchor_plays_AnchorLead_from_now_and_later_anchors_keep_the_offset()
    {
        foreach (var slave in new IPtpSlave?[] { null, new FakeSlave(null) })
        {
            var h = new Harness(StreamType.Buffered, slave);
            h.Buffer.SetAnchor(0, 123 * (ulong)Second, 0);
            Assert.True(h.Buffer.TryGetPlayTime(0, out var first));
            Assert.Equal(Start + 300 * Ms, first);

            h.Clock.Now += 5 * Second + 17 * Ms;
            h.Buffer.SetAnchor(5 * 44100, 128 * (ulong)Second, 0);
            Assert.True(h.Buffer.TryGetPlayTime(5 * 44100, out var later));
            Assert.Equal(Start + 300 * Ms + 5 * Second, later);
        }
    }

    [Fact]
    public async Task Realtime_before_the_first_sync_is_timed_anchor_only_by_its_first_frame()
    {
        var h = new Harness(StreamType.Realtime, null);
        h.Enqueue(7000, 1);
        await h.PumpAsync();
        Assert.True(h.Buffer.TryGetPlayTime(7000, out var t));
        Assert.Equal(Start + 300 * Ms, t);
        Assert.Equal(new[] { "resume", "write" }, h.Output.Kinds);
        Assert.Equal(Lpipe - 300 * Ms, h.Buffer.Statistics.StreamOffset.Ticks * 100);
    }

    [Fact]
    public async Task Buffered_stream_waits_for_its_anchor()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.Enqueue(0, 0);
        await h.RunUntilAsync(Start + 3 * Second);
        Assert.Empty(h.Output.Writes);
        Assert.False(h.Buffer.TryGetPlayTime(0, out _));
    }

    [Fact]
    public async Task Past_due_frames_release_at_once_and_frames_later_than_LateThreshold_are_dropped_and_counted()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe + Second); // D = 0, first release at Start + 1 s
        h.Buffer.Resume();
        for (var k = 0; k < 60; k++)
        {
            h.Enqueue((uint)(k * Packet), (uint)k);
        }

        // The output's queue is full: nothing is written although frames fall due (5.4).
        h.Output.FixedQueuedFrames = 100_000;
        await h.RunUntilAsync(Start + Second + 300 * Ms);
        Assert.Empty(h.Output.Writes);

        // 300 ms later: frames due more than 200 ms ago are late, the rest are past due and go out at once.
        h.Output.FixedQueuedFrames = 0;
        await h.PumpAsync();
        var lateFrames = h.Buffer.Statistics.LateFrames;
        var lateBlocks = (int)(lateFrames / Packet);
        Assert.Equal(0, lateFrames % Packet);
        Assert.Equal(Enumerable.Range(0, 60).Count(k => Start + Second + FramesNs(k * Packet) + 200 * Ms < Start + Second + 300 * Ms), lateBlocks);
        var burst = h.Output.Writes.Where(w => w.Time == Start + Second + 300 * Ms).ToList();
        var pastDue = Enumerable.Range(lateBlocks, 60 - lateBlocks).Count(k => Start + Second + FramesNs(k * Packet) <= Start + Second + 300 * Ms);
        Assert.Equal(pastDue, burst.Count);
        Assert.Equal(13, lateBlocks);
        Assert.Equal(Harness.Marker((uint)(lateBlocks * Packet)), burst[0].FirstSample);
        Assert.Equal(0, h.Buffer.Statistics.MissingFrames);
    }

    [Fact]
    public async Task A_frame_arriving_after_its_slot_was_released_is_discarded_and_not_counted_late()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe);
        h.Buffer.Resume();
        h.Enqueue(0, 0);
        h.Enqueue(Packet, 1);
        await h.RunUntilAsync(Start + 50 * Ms);
        Assert.Equal(2, h.Output.Writes.Count);

        h.Enqueue(Packet, 1); // a duplicate retransmission
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
        Assert.Equal(Packet, h.Buffer.RejectedFrames);
        Assert.Equal(0, h.Buffer.Statistics.BufferedMs);
    }

    [Fact]
    public async Task A_missing_sequence_becomes_silence_of_the_right_length()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe);
        h.Buffer.Resume();
        h.Enqueue(0, 0);
        h.Enqueue(Packet, 1);
        h.Enqueue(3 * Packet, 3); // 2 is missing
        h.Enqueue(4 * Packet, 4, frames: 1024);
        h.Enqueue((4 * Packet) + (2 * 1024), 6, frames: 1024); // a 1024-frame block (5) is missing
        await h.RunUntilAsync(Start + Second);

        var writes = h.Output.Writes;
        Assert.Equal(Packet + Packet + Packet + Packet + 1024 + 1024 + 1024, writes.Sum(w => w.Frames));
        Assert.Equal(Packet, writes[2].Frames);
        Assert.True(writes[2].Silent);
        Assert.InRange(writes[2].Time, Start + FramesNs(2 * Packet), Start + FramesNs(2 * Packet) + Ms);
        Assert.False(writes[3].Silent);
        var gap = writes.Skip(5).TakeWhile(w => w.Silent).ToList();
        Assert.Equal(1024, gap.Sum(w => w.Frames));
        Assert.Equal(Packet + 1024, h.Buffer.Statistics.MissingFrames);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
    }

    [Fact]
    public async Task Pause_holds_frames_and_resume_with_a_new_anchor_re_times_them()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe);
        h.Buffer.Resume();
        for (var k = 0; k < 200; k++)
        {
            h.Enqueue((uint)(k * Packet), (uint)k);
        }

        await h.RunUntilAsync(Start + 100 * Ms);
        var before = h.Output.Writes.Count;
        h.Buffer.Pause();
        await h.RunUntilAsync(Start + 5 * Second);
        Assert.Equal(before, h.Output.Writes.Count);
        Assert.Equal("pause", h.Output.Kinds[^1]);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
        var next = h.Buffer.Statistics.NextTimestamp!.Value;
        Assert.Equal((uint)(before * Packet), next);

        // rate 1 with a new anchor: the held head plays 3 s from now, so it is released 0.8 s from now.
        var resumeAt = h.Clock.Now;
        h.AnchorAt(rtp: next, playTime: resumeAt + 3 * Second);
        h.Buffer.Resume();
        await h.RunUntilAsync(resumeAt + 900 * Ms);
        var after = h.Output.Writes.Skip(before).ToList();
        Assert.Equal("resume", h.Output.Kinds[h.Output.Kinds.LastIndexOf("pause") + 1]);
        var expected = resumeAt + 3 * Second - Lpipe;
        Assert.InRange(after[0].Time, expected, expected + Ms);
        Assert.Equal(Harness.Marker(next), after[0].FirstSample);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
    }

    [Fact]
    public async Task Flush_drops_everything_flushes_the_output_and_the_next_frame_resumes()
    {
        var h = new Harness(StreamType.Realtime, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + 1_750 * Ms);
        for (var k = 0; k < 10; k++)
        {
            h.Enqueue((uint)(k * Packet), (uint)k);
        }

        await h.PumpAsync();
        var written = h.Output.Writes.Count;
        Assert.True(written >= 1);
        h.Buffer.Flush(null);
        await h.PumpAsync();
        Assert.Equal(0, h.Buffer.Statistics.BufferedMs);
        Assert.Null(h.Buffer.Statistics.NextTimestamp);
        Assert.Equal("flush", h.Output.Kinds[^1]);
        Assert.Equal(TimeSpan.Zero, h.Buffer.Statistics.StreamOffset); // D is learnt again

        h.Clock.Now += 500 * Ms;
        h.AnchorAt(rtp: 90_000, playTime: h.Clock.Now + 1_750 * Ms);
        h.Enqueue(90_000, 500);
        await h.PumpAsync();
        Assert.Equal(new[] { "flush", "resume", "write" }, h.Output.Kinds.TakeLast(3));
        Assert.Equal(Harness.Marker(90_000), h.Output.Writes[^1].FirstSample);
        Assert.Equal(0, h.Buffer.Statistics.MissingFrames);
    }

    [Fact]
    public async Task Immediate_FLUSHBUFFERED_drops_below_until_flushes_and_pauses_until_the_next_rate_1()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe + Second);
        h.Buffer.Resume();
        for (var k = 0; k < 10; k++)
        {
            h.Enqueue((uint)(k * Packet), (uint)k);
        }

        h.Buffer.Flush(6);
        await h.RunUntilAsync(Start + 3 * Second);
        Assert.Equal(new[] { "resume", "flush", "pause" }, h.Output.Kinds);
        Assert.Equal(6u * Packet, h.Buffer.Statistics.NextTimestamp);

        h.Enqueue(5 * Packet, 5); // in flight below flushUntilSeq: discarded
        Assert.Equal(6u * Packet, h.Buffer.Statistics.NextTimestamp);
        Assert.Equal(4 * Packet * 1000 / 44100, h.Buffer.Statistics.BufferedMs);

        h.AnchorAt(rtp: 6 * Packet, playTime: h.Clock.Now + 3 * Second);
        h.Buffer.Resume();
        await h.RunUntilAsync(h.Clock.Now + 900 * Ms);
        Assert.Equal(4, h.Output.Writes.Count);
        Assert.Equal(Harness.Marker(6 * Packet), h.Output.Writes[0].FirstSample);
    }

    [Fact]
    public async Task Immediate_flush_modular_compare_handles_the_23_bit_wrap()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe + Second);
        uint[] seqs = [0x7FFFFE, 0x7FFFFF, 0, 1];
        for (var k = 0; k < seqs.Length; k++)
        {
            h.Enqueue((uint)(k * Packet), seqs[k]);
        }

        h.Buffer.Flush(0);
        await h.PumpAsync();
        Assert.Equal(2u * Packet, h.Buffer.Statistics.NextTimestamp);
    }

    [Fact]
    public async Task Deferred_FLUSHBUFFERED_splices_the_range_without_touching_the_output()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe);
        h.Buffer.Resume();
        for (var k = 0; k < 8; k++)
        {
            h.Enqueue((uint)(k * Packet), (uint)k);
        }

        // Blocks 4..6 (and 7..9 not yet arrived) belong to the old next track; the new one starts at block 10 with
        // block 4's timestamp.
        h.Buffer.Splice(4, 10);
        h.Enqueue(8 * Packet, 8); // still in the range: discarded
        for (var k = 0; k < 4; k++)
        {
            h.Enqueue((uint)((4 + k) * Packet), (uint)(10 + k), marker: 10_000);
        }

        await h.RunUntilAsync(Start + Second);
        Assert.DoesNotContain("flush", h.Output.Kinds);
        Assert.DoesNotContain("pause", h.Output.Kinds);
        var writes = h.Output.Writes;
        Assert.Equal(8, writes.Count);
        Assert.Equal(Enumerable.Range(0, 4).Select(i => Harness.Marker((uint)(i * Packet))), writes.Take(4).Select(w => w.FirstSample));
        Assert.All(writes.Skip(4), w => Assert.True(w.FirstSample >= 10_000));
        Assert.Equal(0, h.Buffer.Statistics.MissingFrames);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
    }

    [Fact]
    public async Task Drift_of_plus_30_ms_triggers_single_frame_drops_until_within_20_ms()
    {
        var slave = new FakeSlave(PtpOffset);
        var h = new Harness(StreamType.Buffered, slave);
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe);
        h.Buffer.Resume();
        var blocks = (int)(25.0 * 44100 / Packet);
        for (var k = 0; k < blocks; k++)
        {
            h.Enqueue((uint)(k * Packet), (uint)k);
        }

        // At 2 s the sender's clock is found 30 ms further ahead: frames fall due 30 ms earlier than written time says.
        await h.RunUntilAsync(Start + 2 * Second);
        slave.Offset = PtpOffset - (30 * Ms);
        await h.RunUntilAsync(Start + 9_900 * Ms);
        Assert.Equal(0, h.Buffer.Statistics.DroppedForDrift);

        await h.RunUntilAsync(Start + 18 * Second);
        var dropped = h.Buffer.Statistics.DroppedForDrift;

        // 30 ms = 1323 frames ahead; drops stop once within 20 ms = 882 frames: about 441 single frames.
        Assert.InRange(dropped, 400, 445);
        Assert.Equal(0, h.Buffer.Statistics.RepeatedForDrift);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
        Assert.Contains(h.Output.Writes, w => w.Frames == Packet - 1);

        await h.RunUntilAsync(Start + 22 * Second);
        Assert.Equal(dropped, h.Buffer.Statistics.DroppedForDrift);
    }

    [Fact]
    public async Task Drift_behind_repeats_single_frames()
    {
        var slave = new FakeSlave(PtpOffset);
        var h = new Harness(StreamType.Buffered, slave);
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe);
        h.Buffer.Resume();
        for (var k = 0; k < (int)(16.0 * 44100 / Packet); k++)
        {
            h.Enqueue((uint)(k * Packet), (uint)k);
        }

        await h.RunUntilAsync(Start + 2 * Second);
        slave.Offset = PtpOffset + (25 * Ms);
        await h.RunUntilAsync(Start + 15 * Second);
        // 25 ms = 1102 frames behind; repeats stop within 20 ms = 882 frames: about 220, plus up to one quantum (44 frames) of measuring jitter.
        Assert.InRange(h.Buffer.Statistics.RepeatedForDrift, 200, 270);
        Assert.Equal(0, h.Buffer.Statistics.DroppedForDrift);
        Assert.Contains(h.Output.Writes, w => w.Frames == Packet + 1);
    }

    [Fact]
    public async Task Writes_never_exceed_PrefillFrames_plus_352_queued_in_the_output()
    {
        var options = new PlayoutOptions { PrefillFrames = 1000 };
        var slave = new FakeSlave(PtpOffset);
        var h = new Harness(StreamType.Buffered, slave, options);
        h.Output.Drain = true;
        h.AnchorAt(rtp: 0, playTime: Start + options.Lpipe * Second / 44100);
        h.Buffer.Resume();
        for (var k = 0; k < 200; k++)
        {
            h.Enqueue((uint)(k * Packet), (uint)k);
        }

        await h.RunUntilAsync(Start + 300 * Ms);
        slave.Offset = PtpOffset - (150 * Ms); // 150 ms of frames fall due at once
        await h.RunUntilAsync(Start + 1_500 * Ms);

        Assert.All(h.Output.Writes, w => Assert.True(w.QueuedBefore + w.Frames <= 1000 + Packet, $"{w.QueuedBefore} + {w.Frames}"));
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
        Assert.Contains(h.Output.Writes, w => w.QueuedBefore > 600);

        // A fixed queue at the cap blocks a due frame; one block less lets it through.
        h.Output.Drain = false;
        h.Output.FixedQueuedFrames = 1000 + 1;
        var count = h.Output.Writes.Count;
        await h.RunUntilAsync(h.Clock.Now + 20 * Ms);
        Assert.Equal(count, h.Output.Writes.Count);
        h.Output.FixedQueuedFrames = 1000;
        await h.PumpAsync();
        Assert.True(h.Output.Writes.Count > count);
    }

    [Fact]
    public async Task The_first_PTP_estimate_after_anchor_only_timing_is_a_re_anchor_and_a_lost_estimate_keeps_the_PTP_timeline()
    {
        var slave = new FakeSlave(null);
        var h = new Harness(StreamType.Realtime, slave);

        // The sender's first sync says rtp 0 plays 1.75 s from now (its PTP time); without an estimate it plays anchor-only.
        h.Buffer.SetAnchor(0, (ulong)(Start + 1_750 * Ms - PtpOffset), 0);
        var next = 0;
        var arrivals = new List<long>();
        void Arrive(long now)
        {
            while (Start + FramesNs(next * Packet) <= now)
            {
                h.Enqueue((uint)(next * Packet), (uint)next);
                arrivals.Add(Start + FramesNs(next * Packet));
                next++;
            }
        }

        await h.RunUntilAsync(Start + Second, Arrive);
        Assert.Equal(TimeSpan.FromTicks((Lpipe - 300 * Ms) / 100), h.Buffer.Statistics.StreamOffset);

        // The slave's first estimate: T moves by 1.45 s; D is learnt again so the stream keeps the arrival cadence.
        slave.Offset = PtpOffset;
        await h.RunUntilAsync(Start + 2 * Second, Arrive);
        Assert.InRange(h.Buffer.Statistics.StreamOffset.TotalMilliseconds, 449, 452);

        // The estimate is lost again: the last PTP offset continues the timeline, nothing is re-learnt.
        slave.Offset = null;
        await h.RunUntilAsync(Start + 4 * Second, Arrive);

        var stats = h.Buffer.Statistics;
        Assert.Equal(0, stats.LateFrames);
        Assert.Equal(0, stats.MissingFrames);
        Assert.InRange(stats.StreamOffset.TotalMilliseconds, 449, 452);
        var writes = h.Output.Writes;
        Assert.Equal(next, writes.Count);
        for (var k = 0; k < writes.Count; k++)
        {
            Assert.InRange(writes[k].Time, arrivals[k], arrivals[k] + 2 * Ms);
        }

        Assert.Single(h.Output.Kinds, k => k == "resume");
        Assert.DoesNotContain("pause", h.Output.Kinds);
    }

    [Fact]
    public async Task A_buffered_stream_is_not_paused_for_silence()
    {
        var h = new Harness(StreamType.Buffered, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + Lpipe);
        h.Buffer.Resume();
        h.Enqueue(0, 0);
        await h.RunUntilAsync(Start + 5 * Second); // the data connection stalls for 5 s
        Assert.Equal(new[] { "resume", "write" }, h.Output.Kinds);
    }

    [Fact]
    public async Task ResumeAsync_precedes_the_first_frame_released_while_paused_and_a_silent_stream_pauses_after_2_s()
    {
        var h = new Harness(StreamType.Realtime, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + 1_750 * Ms);
        h.Enqueue(0, 0);
        await h.RunUntilAsync(Start + 1_900 * Ms);
        Assert.Equal(new[] { "resume", "write" }, h.Output.Kinds);

        await h.RunUntilAsync(Start + 2_100 * Ms);
        Assert.Equal(new[] { "resume", "write", "pause" }, h.Output.Kinds);

        // The sender starts again: the next frame resumes the output first.
        var rtp = (uint)(3 * 44100);
        h.Enqueue(rtp, 400);
        await h.RunUntilAsync(Start + 3 * Second);
        Assert.Equal(new[] { "resume", "write", "pause", "resume", "write" }, h.Output.Kinds);
        Assert.Equal(0, h.Buffer.Statistics.MissingFrames);
    }

    [Fact]
    public async Task Periodic_syncs_that_agree_with_the_mapping_keep_D()
    {
        var h = new Harness(StreamType.Realtime, new FakeSlave(PtpOffset));
        h.AnchorAt(rtp: 0, playTime: Start + 1_750 * Ms);
        var next = 0;
        await h.RunUntilAsync(Start + 5 * Second, now =>
        {
            while (Start + FramesNs(next * Packet) <= now)
            {
                h.Enqueue((uint)(next * Packet), (uint)next);
                next++;
            }

            if ((now - Start) % Second == 0 && now > Start)
            {
                // 0xD7 every second with 2 ms of jitter.
                var rtp = (uint)((now - Start) * 44100 / Second);
                h.AnchorAt(rtp, now + 1_750 * Ms + 2 * Ms);
            }
        });

        Assert.InRange(h.Buffer.Statistics.StreamOffset.TotalMilliseconds, 449, 451);
        Assert.Equal(0, h.Buffer.Statistics.LateFrames);
        Assert.Equal(next, h.Output.Writes.Count);
        Assert.Single(h.Output.Kinds, k => k == "resume");
    }

    [Fact]
    public void Statistics_report_buffered_audio_the_next_timestamp_and_the_stream()
    {
        var h = new Harness(StreamType.Buffered, null);
        h.Buffer.Decoder = "ALAC";
        Assert.Equal(PlayoutStatistics.Empty with { StreamType = StreamType.Buffered, Decoder = "ALAC" }, h.Buffer.Statistics);

        h.Enqueue(4410, 0, frames: 4410);
        h.Enqueue(8820, 1, frames: 4410);
        var stats = h.Buffer.Statistics;
        Assert.Equal(200, stats.BufferedMs);
        Assert.Equal(4410u, stats.NextTimestamp);
        Assert.Equal(StreamType.Buffered, stats.StreamType);
        Assert.Equal("ALAC", stats.Decoder);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Release_thread_writes_on_the_real_clock_and_stops_on_dispose()
    {
        var output = new RecordingOutput(MonotonicClock.CreateStopwatch());
        var buffer = new PlayoutBuffer(output, output.Clock, StreamType.Realtime);
        await buffer.StartAsync(CancellationToken.None);
        for (var k = 0; k < 20; k++)
        {
            buffer.Enqueue((uint)(k * Packet), (uint)k, new short[Packet * 2]);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (output.Writes.Count < 20 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(20, output.Writes.Count);
        // Measured from the second write: the first write of a fresh process pays the JIT for the whole release path
        // (about one packet in a Debug build), which delays it until the second frame is due as well. The pacing under
        // test is the steady state after that.
        var span = output.Writes[^1].Time - output.Writes[1].Time;
        Assert.InRange(span, FramesNs(18 * Packet) - (2 * Ms), FramesNs(18 * Packet) + (30 * Ms));
        await buffer.DisposeAsync();
        buffer.Enqueue(0, 0, new short[2]); // after dispose: ignored
    }

    private static long FramesNs(long frames) => frames * Second / PlayoutOptions.SampleRate;

    private sealed class Harness
    {
        public Harness(StreamType type, IPtpSlave? slave, PlayoutOptions? options = null)
        {
            Clock = new FakeClock { Now = Start };
            Slave = slave;
            Output = new RecordingOutput(Clock);
            Buffer = new PlayoutBuffer(Output, Clock, type, slave, options);
        }

        public FakeClock Clock { get; }

        public IPtpSlave? Slave { get; }

        public RecordingOutput Output { get; }

        public PlayoutBuffer Buffer { get; }

        /// <summary>The first sample of a block marks its RTP timestamp so the tests can follow blocks.</summary>
        public static short Marker(uint rtp) => (short)((rtp / Packet % 9000) + 1);

        public void Enqueue(uint rtp, uint seq, int frames = Packet, short marker = 0)
        {
            var samples = new short[frames * 2];
            Array.Fill(samples, (short)3);
            samples[0] = marker != 0 ? (short)(marker + (short)seq) : Marker(rtp);
            Buffer.Enqueue(rtp, seq, samples);
        }

        /// <summary>An anchor whose sender time maps (through the fake slave's offset, or anchor-only) to <paramref name="playTime"/>.</summary>
        public void AnchorAt(uint rtp, long playTime)
        {
            var offset = Slave is FakeSlave { Offset: { } o } ? o : PtpOffset;
            Buffer.SetAnchor(rtp, (ulong)(playTime - offset), 0);
        }

        public async Task PumpAsync() => await Buffer.PumpAsync(CancellationToken.None);

        public async Task RunUntilAsync(long until, Action<long>? beforeEachStep = null)
        {
            while (true)
            {
                beforeEachStep?.Invoke(Clock.Now);
                await Buffer.PumpAsync(CancellationToken.None);
                if (Clock.Now >= until)
                {
                    return;
                }

                Clock.Now += Ms;
            }
        }
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public long Now { get; set; }

        public ulong NowNanoseconds => (ulong)Now;
    }

    private sealed class FakeSlave(long? offset) : IPtpSlave
    {
        public long? Offset { get; set; } = offset;

        public ulong ClockId => 0x0200_00FF_FE00_0001UL;

        public ulong MasterClockId => 0;

        public IReadOnlyCollection<PtpSlaveSample> Samples => [];

        public PtpSlaveStatistics Statistics => new(Offset, 0, null, 0);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool TryMapToLocal(ulong senderNanos, out ulong localNanos)
        {
            localNanos = Offset is { } o ? (ulong)((long)senderNanos + o) : 0;
            return Offset is not null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Write(long Time, int Frames, short FirstSample, bool Silent, int QueuedBefore);

    private sealed class RecordingOutput(IMonotonicClock clock) : IReceiverAudioOutput
    {
        private readonly object _gate = new();
        private readonly List<Write> _writes = [];
        private readonly List<string> _kinds = [];
        private long _written;
        private long? _drainStart;

        public IMonotonicClock Clock { get; } = clock;

        public int? FixedQueuedFrames { get; set; }

        /// <summary>Models the pacer: what was written drains at 44.1 kHz from the first write.</summary>
        public bool Drain { get; set; }

        public List<Write> Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public List<string> Kinds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _kinds];
                }
            }
        }

        public int QueuedFrames
        {
            get
            {
                if (FixedQueuedFrames is { } f)
                {
                    return f;
                }

                if (!Drain || _drainStart is null)
                {
                    return 0;
                }

                var drained = ((long)Clock.NowNanoseconds - _drainStart.Value) * 44100 / Second;
                return (int)Math.Max(0, _written - drained);
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken)
        {
            var queued = QueuedFrames;
            var span = frames.Span;
            var silent = true;
            foreach (var s in span)
            {
                if (s != 0)
                {
                    silent = false;
                    break;
                }
            }

            lock (_gate)
            {
                if (Drain)
                {
                    if (_drainStart is null)
                    {
                        _drainStart = (long)Clock.NowNanoseconds;
                    }
                    else
                    {
                        var drained = ((long)Clock.NowNanoseconds - _drainStart.Value) * 44100 / Second;
                        if (_written < drained)
                        {
                            // An underrun: the queue restarts empty.
                            _drainStart = (long)Clock.NowNanoseconds;
                            _written = 0;
                        }
                    }

                    _written += frames.Length / 2;
                }

                _writes.Add(new Write((long)Clock.NowNanoseconds, frames.Length / 2, span.Length > 0 ? span[0] : (short)0, silent, queued));
                _kinds.Add("write");
            }

            return ValueTask.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Record("pause");

        public Task ResumeAsync(CancellationToken cancellationToken) => Record("resume");

        public Task FlushAsync(CancellationToken cancellationToken) => Record("flush");

        public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken) => Record("level");

        public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken) => Record("nowplaying");

        public Task ReleaseAsync(CancellationToken cancellationToken) => Record("release");

        private Task Record(string kind)
        {
            lock (_gate)
            {
                _kinds.Add(kind);
            }

            return Task.CompletedTask;
        }
    }
}
