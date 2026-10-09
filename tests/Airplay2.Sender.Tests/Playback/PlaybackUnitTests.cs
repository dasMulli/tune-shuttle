using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Xunit;

namespace Airplay2.Sender.Tests.Playback;

/// <summary>The pure parts of the audio pipeline: volume model (SPEC-audio.md 6.3), timeline (1.5), metadata rtptimes (7), PCM ring (8.2).</summary>
public sealed class PlaybackUnitTests
{
    [Fact]
    public void Master_level_scales_every_receiver_and_preserves_ratios()
    {
        var model = new GroupVolumeModel(50);
        model.SetReceiverLevel("a", 40);
        model.SetReceiverLevel("b", 20);

        var changed = model.SetMasterLevel(25);
        Assert.Equal(25, model.MasterLevel);
        Assert.Equal(new (string, int)[] { ("a", 20), ("b", 10) }, changed.OrderBy(c => c.ReceiverId).Select(c => (c.ReceiverId, c.Level)).ToArray());
        Assert.Equal(20, model.Levels["a"]);
        Assert.Equal(10, model.Levels["b"]);

        model.SetMasterLevel(50);
        Assert.Equal(40, model.Levels["a"]);
        Assert.Equal(20, model.Levels["b"]);
    }

    [Fact]
    public void Receivers_clamp_at_100_and_come_back_to_their_relation()
    {
        var model = new GroupVolumeModel(50);
        model.SetReceiverLevel("loud", 80);
        model.SetReceiverLevel("quiet", 40);

        var up = model.SetMasterLevel(100);
        Assert.Equal(100, model.Levels["loud"]); // 160 clamps
        Assert.Equal(80, model.Levels["quiet"]);
        Assert.Equal(2, up.Count);

        model.SetMasterLevel(50);
        Assert.Equal(80, model.Levels["loud"]); // the weight survived the clamp
        Assert.Equal(40, model.Levels["quiet"]);
    }

    [Fact]
    public void Master_zero_mutes_everyone_without_losing_the_relation()
    {
        var model = new GroupVolumeModel(60);
        model.SetReceiverLevel("a", 30);
        model.SetReceiverLevel("b", 60);
        model.SetMasterLevel(0);
        Assert.All(model.Levels.Values, l => Assert.Equal(0, l));
        model.SetMasterLevel(60);
        Assert.Equal(30, model.Levels["a"]);
        Assert.Equal(60, model.Levels["b"]);
    }

    [Fact]
    public void A_receiver_reported_change_leaves_the_others_and_G_alone()
    {
        var model = new GroupVolumeModel(50);
        model.SetReceiverLevel("a", 40);
        model.SetReceiverLevel("b", 20);
        model.ReceiverReported("a", 70);
        Assert.Equal(70, model.Levels["a"]);
        Assert.Equal(20, model.Levels["b"]);
        Assert.Equal(50, model.MasterLevel);

        // From now on a moves with G from its new level.
        model.SetMasterLevel(25);
        Assert.Equal(35, model.Levels["a"]);
        Assert.Equal(10, model.Levels["b"]);

        // Unknown receivers are ignored; removal forgets.
        model.ReceiverReported("ghost", 10);
        Assert.False(model.Levels.ContainsKey("ghost"));
        model.RemoveReceiver("b");
        Assert.Equal(["a"], model.Levels.Keys);
    }

    [Fact]
    public void Levels_are_clamped_and_unchanged_receivers_are_not_reported()
    {
        var model = new GroupVolumeModel(100);
        model.SetReceiverLevel("a", 150);
        model.SetReceiverLevel("b", -3);
        Assert.Equal(100, model.Levels["a"]);
        Assert.Equal(0, model.Levels["b"]);
        Assert.Empty(model.SetMasterLevel(100));
        Assert.Equal(new (string, int)[] { ("a", 50) }, model.SetMasterLevel(50).Select(c => (c.ReceiverId, c.Level)).ToArray()); // b stays 0
    }

    [Theory]
    [InlineData(0, -144.0)]
    [InlineData(15, -25.5)]
    [InlineData(100, 0.0)]
    public void Wire_levels_round_trip(int level, double db)
    {
        Assert.Equal(db, VolumeMapping.LevelToDb(level), 6);
        Assert.Equal(level, VolumeMapping.DbToLevel(VolumeMapping.LevelToDb(level)));
    }

    [Fact]
    public void Timeline_deadlines_are_exact_and_never_drift()
    {
        var timeline = new Timeline(new MasterClock(), 77175, 65535, 0xFFFF_FF00u);
        timeline.Anchor(1_000_000);
        Assert.Equal(1_000_000, timeline.NextDeadline);
        for (var i = 0; i < 11_025; i++)
        {
            timeline.Advance(); // 11 025 packets = 3 880 800 frames: exactly 88 s, through the 32-bit timestamp wrap
        }

        Assert.Equal(1_000_000 + 88_000_000_000L, timeline.NextDeadline);
        Assert.Equal(unchecked((ushort)(65535 + 11_025)), timeline.NextSequence);
        Assert.Equal(unchecked(0xFFFF_FF00u + 3_880_800u), timeline.NextTimestamp);
    }

    [Fact]
    public void Timeline_deadlines_keep_increasing_past_two_to_the_32_frames_since_the_anchor()
    {
        // 27 h of uninterrupted playback: the 32-bit frame distance to the anchor wraps, the deadline must not.
        var timeline = new Timeline(new MasterClock(), 77175, 0, 0x8000_0000u);
        timeline.Anchor(5_000);
        var frames = 0L;
        var previous = timeline.NextDeadline;
        for (var i = 0; i < 5; i++)
        {
            timeline.Skip(0x7FFF_FF00u); // stands in for ~6.1 M packets (the pacer's stall recovery uses the same path)
            frames += 0x7FFF_FF00L;
            for (var k = 0; k < 1000; k++)
            {
                timeline.Advance();
                frames += 352;
            }

            var deadline = timeline.NextDeadline;
            Assert.True(deadline > previous, $"deadline went backwards after {frames} frames");
            Assert.Equal(5_000 + (long)((Int128)frames * 1_000_000_000 / 44100), deadline);
            Assert.Equal(unchecked(0x8000_0000u + (uint)frames), timeline.NextTimestamp);

            // A timestamp just behind the next one (what the syncs and the packet history describe) maps just before it.
            Assert.Equal(5_000 + (long)((Int128)(frames - 352) * 1_000_000_000 / 44100), timeline.DeadlineOf(unchecked(timeline.NextTimestamp - 352u)));
            previous = deadline;
        }

        Assert.True(frames > 1L << 33);
    }

    [Fact]
    public void Timeline_now_frame_skip_and_speaker_frame()
    {
        var timeline = new Timeline(new MasterClock(), 77175, 10, 100_000);
        Assert.Equal(100_000u - 77175u, timeline.NowFrame);
        Assert.Equal(timeline.NowFrame, timeline.FrameAtSpeaker(123)); // not anchored: where playback resumes
        Assert.Throws<InvalidOperationException>(() => timeline.NextDeadline);

        timeline.Anchor(0);
        Assert.Equal(100_000u - 77175u + 44100u, timeline.FrameAtSpeaker(1_000_000_000));
        var (seq, ts) = timeline.Advance();
        Assert.Equal((ushort)10, seq);
        Assert.Equal(100_000u, ts);

        // Skip moves the timestamp (and so the deadline) but not the sequence or the anchor.
        timeline.Skip(44100);
        Assert.Equal((ushort)11, timeline.NextSequence);
        Assert.Equal(100_000u + 352u + 44100u, timeline.NextTimestamp);
        Assert.Equal(1_000_000_000L + (352L * 1_000_000_000 / 44100), timeline.NextDeadline);
        timeline.ClearAnchor();
        Assert.False(timeline.IsAnchored);
    }

    [Fact]
    public void Progress_for_a_fresh_start_uses_the_startup_lead()
    {
        var tracker = new NowPlayingTracker();
        var track = tracker.SetTrack(new TrackMetadata("t", "a", "b", TimeSpan.FromSeconds(180)), null, null, writeTimestamp: 1_000_000, TimeSpan.Zero);
        Assert.Equal(1_000_000u, track.Start);
        var progress = NowPlayingTracker.Progress(track, speakerFrame: 1_000_000 - 77175, NowPlayingTracker.StartupDisplayLead);
        Assert.Equal(new ProgressInfo(1_000_000 - 15360, 1_000_000, 1_000_000 + (180 * 44100), RtpTime: 1_000_000), progress);
    }

    [Fact]
    public void Progress_for_a_track_switch_and_a_mid_track_position()
    {
        var tracker = new NowPlayingTracker();
        var track = tracker.SetTrack(new TrackMetadata("t", "a", "b", TimeSpan.FromSeconds(60)), null, null, writeTimestamp: 5_000_000, TimeSpan.FromSeconds(10));
        Assert.Equal(5_000_000u - 441_000u, track.Start);
        var speaker = 5_000_000u - 77175u;
        var progress = NowPlayingTracker.Progress(track, speaker, NowPlayingTracker.SwitchDisplayLead);
        Assert.Equal(track.Start - 30720u, progress.Start);
        Assert.Equal(speaker, progress.Current); // already past the start
        Assert.Equal(track.Start + (60u * 44100u), progress.End);
        Assert.Equal(track.Start, progress.RtpTime);
    }

    [Fact]
    public void Progress_with_unknown_duration_wraps_and_shifts()
    {
        var tracker = new NowPlayingTracker();
        var track = tracker.SetTrack(new TrackMetadata("t", null, null, null), null, null, writeTimestamp: 100, TimeSpan.FromSeconds(1));
        Assert.Equal(unchecked(100u - 44100u), track.Start); // modulo 2^32
        var progress = NowPlayingTracker.Progress(track, speakerFrame: 50, NowPlayingTracker.StartupDisplayLead);
        Assert.Equal(50u, progress.Current); // 50 is after the wrapped start
        Assert.Equal(progress.Current, progress.End);
        tracker.Shift(-1000);
        Assert.Equal(unchecked(100u - 44100u - 1000u), tracker.Track!.Start);
    }

    [Fact]
    public void Pcm_ring_wraps_marks_and_drops()
    {
        var ring = new PcmRing(10);
        var input = Enumerable.Range(1, 16).Select(i => (short)i).ToArray(); // 8 frames
        Assert.Equal(8, ring.Write(input));
        var output = new short[12];
        Assert.Equal(6, ring.Read(output));
        Assert.Equal(input[..12], output);
        Assert.Equal(8, ring.Write(input)); // wraps: 2 + 8 = 10 frames
        Assert.Equal(0, ring.Write(input));
        Assert.Equal(10, ring.Count);
        var mark = ring.WrittenFrames - 3;
        Assert.Equal(7, ring.DropUntil(mark));
        Assert.Equal(3, ring.Read(output));
        Assert.Equal(input[10..16], output[..6]);
        Assert.Equal(0, ring.Clear());
    }

    [Fact]
    public void Probe_tone_is_quiet_and_faded()
    {
        var tone = PlaybackProbes.Tone(TimeSpan.FromSeconds(1));
        Assert.Equal(44100 * 2, tone.Length);
        Assert.Equal(0, tone[0]);
        var peak = tone.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, 2000, 2070); // -24 dBFS = 2063
    }
}
