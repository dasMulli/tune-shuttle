using System.Text;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Xunit;
using TestReceiver = Airplay2.FakeReceiver.FakeReceiver;

namespace Airplay2.Sender.Tests.Playback;

/// <summary>
/// <see cref="GroupPlayer"/> over the real transport with two in-process fake receivers in one group:
/// D5 start-up, identical packets under per-receiver keys, sync relation and cadence,
/// mid-stream join, flush/pause/resume, retransmits from the history, PTP syncs, progress bodies, volume and events.
/// </summary>
public sealed class GroupPlayerFakeReceiverTests
{
    private const int L = RealtimeStreamConstants.DefaultLatencyFrames;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Two_receivers_play_identical_audio_under_their_own_keys_and_decode_to_the_input()
    {
        await using var h = await GroupHarness.StartAsync();
        var a = await h.NewFakeAsync();
        var b = await h.NewFakeAsync();
        await h.AddAsync(a, 40);
        await h.AddAsync(b, 60);
        Assert.All(h.Player.Members.Values, s => Assert.Equal(GroupMemberState.Connected, s));

        var input = TestPcm.Sweep(352 * 100);
        h.Player.Write(input); // below the high-water mark: returns at once (pre-fill before start)
        await h.Player.StartAsync(None);

        var eventsA = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.Sync>().Count() >= 2 && e.OfType<FakeReceiverEvent.AudioPacket>().Count() >= 100);
        var eventsB = await CollectAsync(b, e => e.OfType<FakeReceiverEvent.Sync>().Count() >= 2 && e.OfType<FakeReceiverEvent.AudioPacket>().Count() >= 100);
        await h.WaitForAsync(() => h.Player.Members.Values.All(s => s == GroupMemberState.Streaming));

        var audioA = eventsA.OfType<FakeReceiverEvent.AudioPacket>().Take(100).ToList();
        var audioB = eventsB.OfType<FakeReceiverEvent.AudioPacket>().Take(100).ToList();
        Assert.True(audioA[0].Marker);
        Assert.All(audioA.Skip(1), p => Assert.False(p.Marker));
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(unchecked((ushort)(audioA[0].Sequence + i)), audioA[i].Sequence);
            Assert.Equal(unchecked(audioA[0].Timestamp + (uint)(352 * i)), audioA[i].Timestamp);
            Assert.Equal(audioA[i].Sequence, audioB[i].Sequence);
            Assert.Equal(audioA[i].Timestamp, audioB[i].Timestamp);
            Assert.Equal(audioA[i].Marker, audioB[i].Marker);
            Assert.Equal(audioA[i].Pcm, audioB[i].Pcm);
        }

        Assert.Equal(input, audioA.SelectMany(p => p.Pcm).ToArray());
        Assert.DoesNotContain(eventsA.Concat(eventsB), e => e is FakeReceiverEvent.BadAudioPacket);

        // Per-receiver keys: each stream SETUP carried that receiver's own shk (its pairing's shared secret, D4).
        var keyA = StreamKey(eventsA);
        var keyB = StreamKey(eventsB);
        Assert.NotEqual(keyA, keyB);

        // D5 bodies: one groupUUID, NTP with our timing port, the levels after the stream SETUP.
        var setupA = eventsA.OfType<FakeReceiverEvent.SessionSetup>().Single().Body;
        var setupB = eventsB.OfType<FakeReceiverEvent.SessionSetup>().Single().Body;
        Assert.Equal(h.Player.Options.GroupUuid, setupA.GetString("groupUUID"));
        Assert.Equal(setupA.GetString("groupUUID"), setupB.GetString("groupUUID"));
        Assert.Equal("NTP", setupA.GetString("timingProtocol"));
        Assert.Equal(h.Timing.Port, setupA.GetInt64("timingPort"));
        Assert.Equal(VolumeMapping.LevelToDb(40), eventsA.OfType<FakeReceiverEvent.Volume>().First().Db, 6);
        Assert.Equal(VolumeMapping.LevelToDb(60), eventsB.OfType<FakeReceiverEvent.Volume>().First().Db, 6);
        Assert.Equal(["GET /info", "POST /pair-pin-start", "POST /pair-setup", "POST /pair-setup", "SETUP", "RECORD", "SETUP", "SET_PARAMETER"], eventsA.OfType<FakeReceiverEvent.Request>().Take(8).Select(Line).ToArray());

        // Syncs: 0x90 first, now_frame = next - L, the first one names the marker packet; the periodic one ~1 s later.
        foreach (var events in new[] { eventsA, eventsB })
        {
            var syncs = events.OfType<FakeReceiverEvent.Sync>().ToList();
            Assert.True(syncs[0].First);
            Assert.False(syncs[1].First);
            Assert.Equal(audioA[0].Timestamp, syncs[0].NextFrame);
            Assert.All(syncs, s => Assert.Equal(unchecked(s.NextFrame - (uint)L), s.NowFrame));
            var frames = unchecked(syncs[1].NowFrame - syncs[0].NowFrame);
            Assert.InRange(frames, 44100u, 44100u + 351u);
            var seconds = (NtpTimestamp.ToUnixNanos(syncs[1].NtpTime) - NtpTimestamp.ToUnixNanos(syncs[0].NtpTime)) / 1e9;
            Assert.Equal(frames, seconds * 44100, 0.5);
        }

        Assert.True(h.Timing.RepliesSent > 0);
    }

    [Fact]
    public async Task A_receiver_joining_mid_stream_gets_0x90_and_a_marker_on_the_shared_timeline()
    {
        await using var h = await GroupHarness.StartAsync();
        var a = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var b = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        await h.AddAsync(a);
        var writing = h.WriteAsync(TestPcm.Sweep(44100 * 3));
        await h.Player.StartAsync(None);
        await h.WaitForAsync(() => h.Player.PacketsSent > 80);
        await h.AddAsync(b);

        var eventsB = await CollectAsync(b, e => e.OfType<FakeReceiverEvent.AudioPacket>().Count() >= 40);
        var eventsA = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.AudioPacket>().Count() >= 160);
        var firstB = eventsB.OfType<FakeReceiverEvent.AudioPacket>().First();
        var firstA = eventsA.OfType<FakeReceiverEvent.AudioPacket>().First();
        Assert.True(firstB.Marker);
        Assert.True(unchecked((short)(firstB.Sequence - firstA.Sequence)) > 50); // no catch-up: the next packet of the timeline
        var syncB = eventsB.OfType<FakeReceiverEvent.Sync>().First();
        Assert.True(syncB.First);
        Assert.Equal(firstB.Timestamp, syncB.NextFrame);

        // Same (now_frame, T) relation as a's syncs: both receivers play frame x at the same instant.
        var syncA = eventsA.OfType<FakeReceiverEvent.Sync>().First();
        var seconds = (NtpTimestamp.ToUnixNanos(syncB.NtpTime) - NtpTimestamp.ToUnixNanos(syncA.NtpTime)) / 1e9;
        Assert.Equal(unchecked(syncB.NowFrame - syncA.NowFrame), seconds * 44100, 0.5);

        // b's packets are a's packets.
        var sameInA = eventsA.OfType<FakeReceiverEvent.AudioPacket>().Single(p => p.Sequence == firstB.Sequence);
        Assert.Equal(sameInA.Pcm, firstB.Pcm);
        Assert.Equal(sameInA.Timestamp, firstB.Timestamp);
        Assert.False(writing.IsFaulted); // the producer is still paced (blocked above the high-water mark); dispose releases it
    }

    [Fact]
    public async Task Pause_flushes_and_resume_reanchors_with_0x90_and_marker_at_the_announced_values()
    {
        await using var h = await GroupHarness.StartAsync(new GroupPlayerOptions { VolumePollInterval = null });
        var a = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        await h.AddAsync(a, 30);
        h.Player.Write(TestPcm.Sweep(352 * 60));
        await h.Player.StartAsync(None);
        await CollectAsync(a, e => e.OfType<FakeReceiverEvent.AudioPacket>().Count() >= 30);

        await h.Player.PauseAsync(None);
        var next = (h.Player.Timeline.NextSequence, h.Player.Timeline.NextTimestamp);
        Assert.Equal(GroupMemberState.Flushed, h.Player.Members[a.DeviceId]);
        Assert.Equal(0, h.Player.QueuedFrames);
        var paused = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.Flush>().Any() && e.OfType<FakeReceiverEvent.Volume>().Any());
        var flush = paused.OfType<FakeReceiverEvent.Flush>().Single();
        Assert.Equal(next, (flush.Sequence, flush.Timestamp));
        Assert.Equal(VolumeMapping.LevelToDb(30), paused.OfType<FakeReceiverEvent.Volume>().Last().Db, 6); // re-sent after FLUSH
        var sentWhilePaused = h.Player.PacketsSent;
        await Task.Delay(200);
        Assert.Equal(sentWhilePaused, h.Player.PacketsSent);

        h.Player.Write(TestPcm.Sweep(352 * 30));
        await h.Player.StartAsync(None);
        var resumed = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.AudioPacket>().Any() && e.OfType<FakeReceiverEvent.Sync>().Any());
        var sync = resumed.OfType<FakeReceiverEvent.Sync>().First();
        var marker = resumed.OfType<FakeReceiverEvent.AudioPacket>().First();
        Assert.True(sync.First);
        Assert.Equal(flush.Timestamp, sync.NextFrame);
        Assert.True(marker.Marker);
        Assert.Equal(flush.Sequence, marker.Sequence);
        Assert.Equal(flush.Timestamp, marker.Timestamp);
        await h.WaitForAsync(() => h.Player.Members[a.DeviceId] == GroupMemberState.Streaming);

        // A seek-style flush while running: FLUSH with the next values, then 0x90 + marker again, timestamps still monotonic.
        await h.Player.FlushAsync(None);
        var seek = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.Flush>().Any() && e.OfType<FakeReceiverEvent.AudioPacket>().Any(p => p.Marker));
        var flush2 = seek.OfType<FakeReceiverEvent.Flush>().Single();
        Assert.True(unchecked((int)(flush2.Timestamp - flush.Timestamp)) > 0);
        var marker2 = seek.OfType<FakeReceiverEvent.AudioPacket>().First(p => p.Marker);
        Assert.Equal((flush2.Sequence, flush2.Timestamp), (marker2.Sequence, marker2.Timestamp));
        Assert.DoesNotContain(seek.OfType<FakeReceiverEvent.AudioPacket>(), p => unchecked((int)(p.Timestamp - flush2.Timestamp)) < 0 && seek.IndexOf(p) > seek.IndexOf(flush2));
    }

    [Fact]
    public async Task Silence_timeout_flushes_every_member_and_returning_audio_reanchors_each_with_0x90_and_marker()
    {
        await using var h = await GroupHarness.StartAsync(new GroupPlayerOptions { VolumePollInterval = null, SilenceTimeout = TimeSpan.FromMilliseconds(300) });
        var a = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var b = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        await h.AddAsync(a, 30);
        await h.AddAsync(b, 70);
        h.Player.Write(TestPcm.Sweep(352 * 20));
        await h.Player.StartAsync(None);

        // SPEC-audio.md 8.3: after 300 ms of silence every member gets FLUSH with the same next values, then its volume again.
        var flushes = new List<FakeReceiverEvent.Flush>();
        foreach (var (fake, level) in new[] { (a, 30), (b, 70) })
        {
            var events = await CollectAsync(fake, e => e.SkipWhile(x => x is not FakeReceiverEvent.Flush).OfType<FakeReceiverEvent.Volume>().Any());
            flushes.Add(events.OfType<FakeReceiverEvent.Flush>().Single());
            Assert.Equal(VolumeMapping.LevelToDb(level), events.SkipWhile(x => x is not FakeReceiverEvent.Flush).OfType<FakeReceiverEvent.Volume>().First().Db, 6);
        }

        Assert.Equal((flushes[0].Sequence, flushes[0].Timestamp), (flushes[1].Sequence, flushes[1].Timestamp));
        Assert.Equal((h.Player.Timeline.NextSequence, h.Player.Timeline.NextTimestamp), (flushes[0].Sequence, flushes[0].Timestamp));
        await h.WaitForAsync(() => h.Player.Members.Values.All(s => s == GroupMemberState.Flushed));
        Assert.False(h.Player.Timeline.IsAnchored);
        Assert.True(h.Player.IsStarted);
        var sent = h.Player.PacketsSent;
        await Task.Delay(200);
        Assert.Equal(sent, h.Player.PacketsSent); // nothing is sent until audio returns

        // Audio returns: a new anchor, and each member gets a 0x90 sync and a marker packet at the announced values.
        h.Player.Write(TestPcm.Sweep(352 * 30));
        foreach (var fake in new[] { a, b })
        {
            var resumed = await CollectAsync(fake, e => e.OfType<FakeReceiverEvent.Sync>().Any(s => s.First) && e.OfType<FakeReceiverEvent.AudioPacket>().Any(p => p.Marker));
            var sync = resumed.OfType<FakeReceiverEvent.Sync>().First(s => s.First);
            var marker = resumed.OfType<FakeReceiverEvent.AudioPacket>().First(p => p.Marker);
            Assert.Equal(flushes[0].Timestamp, sync.NextFrame);
            Assert.Equal(unchecked(sync.NextFrame - (uint)L), sync.NowFrame);
            Assert.Equal((flushes[0].Sequence, flushes[0].Timestamp), (marker.Sequence, marker.Timestamp));
        }

        await h.WaitForAsync(() => h.Player.Members.Values.All(s => s == GroupMemberState.Streaming));
    }

    [Fact]
    public async Task A_failed_pacer_faults_the_player_instead_of_freezing_the_producer()
    {
        await using var h = await GroupHarness.StartAsync(new GroupPlayerOptions { VolumePollInterval = null, RingHighWaterFrames = 4410 });
        var a = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        await h.AddAsync(a);
        h.Player.PacketObserver = (_, _) =>
        {
            if (h.Player.PacketsSent >= 10)
            {
                throw new InvalidOperationException("test: a bug on the pacer thread");
            }
        };

        await h.Player.StartAsync(None);
        var writing = h.WriteAsync(TestPcm.Sweep(44100 * 5)); // far above the high-water mark: blocks while the pacer runs
        await h.WaitForAsync(() => h.Player.IsFaulted);
        await writing.WaitAsync(TimeSpan.FromSeconds(5)); // released and discarded, not blocked forever
        Assert.False(h.Player.IsStarted);
        Assert.True(h.Player.FramesDiscarded > 0);
        Assert.Equal(0, h.Player.QueuedFrames);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Player.StartAsync(None));
        h.Player.Write(TestPcm.Sweep(44100)); // returns at once
    }

    [Fact]
    public async Task Dropped_packets_are_answered_from_the_history()
    {
        await using var h = await GroupHarness.StartAsync();
        var a = await h.NewFakeAsync(new FakeReceiverOptions { DropEveryNthPacket = 10, SendTimingRequests = false });
        var b = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        await h.AddAsync(a);
        await h.AddAsync(b);
        var input = TestPcm.Sweep(352 * 80);
        h.Player.Write(input);
        await h.Player.StartAsync(None);
        var events = await CollectAsync(a, e =>
        {
            var audio = e.OfType<FakeReceiverEvent.AudioPacket>().ToList();
            var start = audio.FirstOrDefault(p => p.Marker);
            return start is not null && Enumerable.Range(0, 80).All(i => audio.Any(p => p.Sequence == unchecked((ushort)(start.Sequence + i))));
        });
        var answered = events.OfType<FakeReceiverEvent.RetransmitAnswered>().ToList();
        Assert.NotEmpty(answered);
        Assert.All(answered, x => Assert.True(x.Wrapped));
        var bySequence = events.OfType<FakeReceiverEvent.AudioPacket>().GroupBy(p => p.Sequence).ToDictionary(g => g.Key, g => g.First());
        var first = events.OfType<FakeReceiverEvent.AudioPacket>().First(p => p.Marker).Sequence;
        Assert.Equal(input, Enumerable.Range(0, 80).SelectMany(i => bySequence[unchecked((ushort)(first + i))].Pcm).ToArray());
        Assert.True(h.Control.RetransmitRequestsReceived > 0);
        Assert.Equal(0, h.Control.UnmatchedRequests); // two receivers on 127.0.0.1 told apart by port
    }

    [Fact]
    public async Task Under_ptp_the_group_sends_0xD7_syncs_setpeers_and_registers_the_timing_peer()
    {
        var ptp = new StubPtpMaster(0xFFFF_0000_ABCD_EF01UL);
        await using var h = await GroupHarness.StartAsync(new GroupPlayerOptions { VolumePollInterval = null }, ptp);
        var a = await h.NewFakeAsync();
        await h.AddAsync(a);
        Assert.Contains("add 127.0.0.1", ptp.Calls);
        h.Player.Write(TestPcm.Sweep(352 * 20));
        await h.Player.StartAsync(None);
        var events = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.PtpSync>().Count() >= 2 && e.OfType<FakeReceiverEvent.AudioPacket>().Any());

        var setup = events.OfType<FakeReceiverEvent.SessionSetup>().Single().Body;
        Assert.Equal("PTP", setup.GetString("timingProtocol"));
        Assert.Equal(unchecked((long)ptp.ClockId), setup.GetDictionary("timingPeerInfo")!.GetInt64("ClockID"));
        Assert.Equal(["127.0.0.1", "127.0.0.1"], events.OfType<FakeReceiverEvent.Peers>().Single().Addresses.ToArray());
        var requests = events.OfType<FakeReceiverEvent.Request>().Select(Line).ToList();
        Assert.True(requests.IndexOf("RECORD") < requests.IndexOf("SETPEERS"));
        Assert.True(requests.IndexOf("SETPEERS") < requests.LastIndexOf("SETUP"));

        var syncs = events.OfType<FakeReceiverEvent.PtpSync>().ToList();
        Assert.DoesNotContain(events, e => e is FakeReceiverEvent.Sync);
        Assert.True(syncs[0].First);
        Assert.False(syncs[1].First);
        Assert.All(syncs, s => Assert.Equal(ptp.ClockId, s.ClockId));
        Assert.All(syncs, s => Assert.Equal(unchecked(s.NextFrame - (uint)L), s.NowFrame));
        Assert.Equal(events.OfType<FakeReceiverEvent.AudioPacket>().First().Timestamp, syncs[0].NextFrame);
        var frames = unchecked(syncs[1].NowFrame - syncs[0].NowFrame);
        Assert.Equal(frames, (syncs[1].PtpTimeNanos - syncs[0].PtpTimeNanos) * 44100 / 1e9, 0.5);

        await h.Player.RemoveReceiverAsync(a.DeviceId, None);
        Assert.Contains("remove 127.0.0.1", ptp.Calls);
    }

    [Fact]
    public async Task Progress_bodies_follow_spec_audio_7_for_a_fresh_start_a_track_switch_and_a_mid_track_join()
    {
        await using var h = await GroupHarness.StartAsync(new GroupPlayerOptions { VolumePollInterval = null });
        var a = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var b = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        await h.AddAsync(a);
        await CollectAsync(a, e => e.OfType<FakeReceiverEvent.Volume>().Any());

        // Fresh start: before any audio, the track starts at the next timestamp; display 15 360 early; current = start.
        var start1 = h.Player.Timeline.NextTimestamp;
        var jpeg = SessionProbes.SampleJpeg();
        await h.Player.SetNowPlayingAsync(new TrackMetadata("One", "Artist", "Album", TimeSpan.FromSeconds(180)), jpeg, RtspContentTypes.Jpeg, TimeSpan.Zero, None);
        var fresh = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.Progress>().Any());
        Assert.Equal(start1, fresh.OfType<FakeReceiverEvent.Metadata>().Single().RtpTime);
        Assert.Equal("One", fresh.OfType<FakeReceiverEvent.Metadata>().Single().Items["minm"]);
        Assert.Equal(start1, fresh.OfType<FakeReceiverEvent.Artwork>().Single().RtpTime);
        Assert.Equal(new FakeReceiverEvent.Progress(start1 - 15360, start1, start1 + (180 * 44100)), fresh.OfType<FakeReceiverEvent.Progress>().Single());
        Assert.Equal($"rtptime={start1}", ProgressRequest(fresh).Headers["RTP-Info"]);

        h.Player.Write(TestPcm.Sweep(352 * 40));
        await h.Player.StartAsync(None);
        var firstPacket = (await CollectAsync(a, e => e.OfType<FakeReceiverEvent.AudioPacket>().Any())).OfType<FakeReceiverEvent.AudioPacket>().First();
        Assert.Equal(start1, firstPacket.Timestamp); // the metadata rtptime is the track's first sample
        await h.WaitForAsync(() => h.Player.QueuedFrames == 0);

        // Track switch 20 s into the track (the bridge reports the position of what it writes next): display 30 720 early,
        // current = the frame at the speakers (already past the start), end = start + duration.
        var before = h.Player.Timeline.NextTimestamp;
        await h.Player.SetNowPlayingAsync(new TrackMetadata("Two", "Artist", "Album", TimeSpan.FromSeconds(60)), null, null, TimeSpan.FromSeconds(20), None);
        var after = h.Player.Timeline.NextTimestamp;
        var switched = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.Progress>().Any());
        var start2 = switched.OfType<FakeReceiverEvent.Metadata>().Single().RtpTime!.Value;
        Assert.InRange(unchecked(start2 + 882_000 - before), 0u, unchecked(after - before));
        Assert.Empty(switched.OfType<FakeReceiverEvent.Artwork>());
        var progress2 = switched.OfType<FakeReceiverEvent.Progress>().Single();
        Assert.Equal(start2 - 30720, progress2.Start);
        Assert.Equal(start2 + (60 * 44100), progress2.End);
        var expectedCurrent = unchecked(start2 + 882_000 - (uint)L);
        Assert.InRange(unchecked((int)(progress2.Current - expectedCurrent)), -2 * 352, 2 * 352);
        Assert.Equal($"rtptime={start2}", ProgressRequest(switched).Headers["RTP-Info"]);

        // Mid-track join: b gets the current track with the startup lead and the current speaker frame.
        await h.AddAsync(b);
        var joined = await CollectAsync(b, e => e.OfType<FakeReceiverEvent.Progress>().Any());
        Assert.Equal(start2, joined.OfType<FakeReceiverEvent.Metadata>().Single().RtpTime);
        var progressB = joined.OfType<FakeReceiverEvent.Progress>().Single();
        Assert.Equal(start2 - 15360, progressB.Start);
        Assert.Equal(start2 + (60 * 44100), progressB.End);
        Assert.True(unchecked((int)(progressB.Current - progress2.Current)) >= 0);
        Assert.True(unchecked((int)(progressB.Current - start2)) > 0);
    }

    [Fact]
    public async Task Volume_scaling_device_reports_remote_commands_and_coalescing()
    {
        await using var h = await GroupHarness.StartAsync(new GroupPlayerOptions { VolumePollInterval = null });
        var a = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var b = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        await h.AddAsync(a, 40);
        await h.AddAsync(b, 80);
        await CollectAsync(a, e => e.OfType<FakeReceiverEvent.Volume>().Any());
        await CollectAsync(b, e => e.OfType<FakeReceiverEvent.Volume>().Any());

        await h.Player.SetMasterLevelAsync(25, None); // G 50 -> 25 halves every r_i
        Assert.Equal(VolumeMapping.LevelToDb(20), (await NextAsync<FakeReceiverEvent.Volume>(a)).Db, 6);
        Assert.Equal(VolumeMapping.LevelToDb(40), (await NextAsync<FakeReceiverEvent.Volume>(b)).Db, 6);
        Assert.Equal(20, h.Player.Volume.Levels[a.DeviceId]);

        // The receiver changes its own volume (updateInfo on the event channel): only its r_i changes.
        await a.ChangeVolumeAsync(-15.0, None);
        var reported = await NextPlayerEventAsync<GroupPlayerEvent.ReceiverVolumeReported>(h.Player);
        Assert.Equal(new GroupPlayerEvent.ReceiverVolumeReported(a.DeviceId, 50), reported);
        Assert.Equal(50, h.Player.Volume.Levels[a.DeviceId]);
        Assert.Equal(40, h.Player.Volume.Levels[b.DeviceId]);
        Assert.Equal(25, h.Player.Volume.MasterLevel);

        await b.SendRemoteCommandAsync("nitm", None);
        Assert.Equal(new GroupPlayerEvent.RemoteCommand(b.DeviceId, "nitm"), await NextPlayerEventAsync<GroupPlayerEvent.RemoteCommand>(h.Player));

        // Twenty quick changes collapse into a few requests, the last one with the final level (D10: at most 10/s).
        var sets = Enumerable.Range(1, 20).Select(i => h.Player.SetReceiverLevelAsync(b.DeviceId, i, None)).ToArray();
        await Task.WhenAll(sets);
        var volumes = (await FakeReceiverHelpers.DrainAsync(b, TimeSpan.FromMilliseconds(400))).OfType<FakeReceiverEvent.Volume>().ToList();
        Assert.InRange(volumes.Count, 1, 5);
        Assert.Equal(VolumeMapping.LevelToDb(20), volumes[^1].Db, 6);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Player.SetReceiverLevelAsync("nobody", 5, None));
    }

    [Fact]
    public async Task A_lost_receiver_leaves_the_group_playing_and_removal_tears_down()
    {
        await using var h = await GroupHarness.StartAsync(new GroupPlayerOptions { VolumePollInterval = null });
        var a = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var b = await h.NewFakeAsync(new FakeReceiverOptions { SendTimingRequests = false });
        await h.AddAsync(a);
        await h.AddAsync(b);
        await h.Player.StartAsync(None);
        await CollectAsync(b, e => e.OfType<FakeReceiverEvent.AudioPacket>().Count() >= 5);

        await b.KickAsync(None);
        await h.WaitForAsync(() => h.Player.Members[b.DeviceId] == GroupMemberState.Lost);
        Assert.False(h.Transport.ReceiverStates.ContainsKey(b.DeviceId));
        var sent = h.Player.PacketsSent;
        await h.WaitForAsync(() => h.Player.PacketsSent > sent + 20); // the group plays on (silence here)
        Assert.NotEmpty((await CollectAsync(a, e => e.OfType<FakeReceiverEvent.AudioPacket>().Count() >= 5)).OfType<FakeReceiverEvent.AudioPacket>());

        await h.Player.RemoveReceiverAsync(a.DeviceId, None);
        var teardown = await CollectAsync(a, e => e.OfType<FakeReceiverEvent.Teardown>().Any(t => t.Session));
        Assert.Contains(teardown, e => e is FakeReceiverEvent.Teardown { Session: false }); // per-stream first (D7)
        Assert.False(h.Player.Members.ContainsKey(a.DeviceId));
        Assert.False(h.Player.Volume.Levels.ContainsKey(a.DeviceId));
        await h.Player.RemoveReceiverAsync(b.DeviceId, None);
        Assert.Empty(h.Player.Members);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await h.AddAsync(a);
            await h.AddAsync(a);
        });
    }

    private static string Line(FakeReceiverEvent.Request request) => request.Uri.StartsWith("rtsp://", StringComparison.Ordinal) ? request.Method : $"{request.Method} {request.Uri}";

    private static byte[] StreamKey(List<FakeReceiverEvent> events) =>
        ((PlistDictionary)events.OfType<FakeReceiverEvent.StreamSetup>().Single().Body.GetArray("streams")![0]!).GetData("shk")!;

    private static FakeReceiverEvent.Request ProgressRequest(List<FakeReceiverEvent> events) =>
        events.OfType<FakeReceiverEvent.Request>().Last(r => r.Method == "SET_PARAMETER" && Encoding.ASCII.GetString(r.Body).StartsWith("progress:", StringComparison.Ordinal));

    /// <summary>Reads events until <paramref name="done"/> holds for everything read so far.</summary>
    private static async Task<List<FakeReceiverEvent>> CollectAsync(TestReceiver fake, Func<List<FakeReceiverEvent>, bool> done, TimeSpan? timeout = null)
    {
        var list = new List<FakeReceiverEvent>();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            while (!done(list))
            {
                list.Add(await fake.Events.ReadAsync(cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Condition not met; {list.Count} events: {string.Join(", ", list.GroupBy(e => e.GetType().Name).Select(g => $"{g.Key} x{g.Count()}"))}; requests: {string.Join(", ", list.OfType<FakeReceiverEvent.Request>().Select(Line))}");
        }

        return list;
    }

    private static async Task<T> NextAsync<T>(TestReceiver fake)
        where T : FakeReceiverEvent
    {
        var events = await CollectAsync(fake, e => e.OfType<T>().Any());
        return events.OfType<T>().First();
    }

    private static async Task<T> NextPlayerEventAsync<T>(GroupPlayer player)
        where T : GroupPlayerEvent
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (await player.Events.ReadAsync(cts.Token) is T typed)
            {
                return typed;
            }
        }
    }
}

/// <summary>Draining without failing on silence.</summary>
internal static class FakeReceiverHelpers
{
    public static Task<List<FakeReceiverEvent>> DrainAsync(TestReceiver fake, TimeSpan quiet) => Fake.FakeReceiverHarness.DrainAsync(fake, quiet);
}
