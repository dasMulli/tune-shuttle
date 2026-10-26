using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Airplay2.Receiver.Codecs;
using Airplay2.Receiver.Discovery;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Streams;
using Airplay2.Receiver.Timing;
using Airplay2.Sender;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Receiver.Tests;

/// <summary>
/// The product <see cref="AirplayReceiver"/> end to end on loopback (SPEC-receiver.md 11.1, 13 wave 2 <c>receiver-facade</c>):
/// our own <c>Airplay2.Sender</c> (<see cref="AirplaySender"/> with its group player, or a bare <see cref="ReceiverSession"/>
/// plus a raw block writer) drives the receiver; a capturing <see cref="IReceiverAudioOutput"/> records what reaches the
/// output. PTP runs between the sender's grandmaster and the receiver's slave on two private <see cref="PtpPort"/>s bound to
/// 127.0.0.1 port 0 (never 319/320); the RTSP listener binds port 0; identities are synthetic; mDNS (when used) goes to a
/// private group with hop limit 1.
/// </summary>
public sealed class AirplayReceiverLoopbackTests(ITestOutputHelper output)
{
    private const int Packet = 352;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private static readonly SenderIdentity SenderId = new("loopback phone", "02:00:00:00:0C:01", "02:00:00:00:0C:01", Model: "TestPhone1,1");

    /// <summary>A private mDNS group on its own port (the advertiser tests use 53541).</summary>
    private static readonly MdnsEndpoint PrivateMdns = new(IPAddress.Parse("239.255.0.251"), IPAddress.Parse("ff05::1:fb"), 53543, HopLimit: 1);

    [Fact]
    public async Task Start_advertises_answers_info_renames_and_stop_closes_the_listener()
    {
        var identity = ReceiverIdentity.Create();
        var sink = new CapturingOutput();
        var options = Options(identity) with { Advertise = true, Mdns = PrivateMdns, MdnsHostname = "ap2-facade-loopback-" + identity.DeviceIdCompact[^4..].ToLowerInvariant() };
        var receiver = new AirplayReceiver(options, sink, NullLoggerFactory.Instance);
        Assert.Equal(ReceiverStatus.Off, receiver.Status);
        Assert.Equal(0, receiver.RtspPort);
        Assert.Null(receiver.Statistics);

        await receiver.StartAsync(None);
        Assert.Equal(ReceiverStatus.Advertising, receiver.Status);
        Assert.NotEqual(0, receiver.RtspPort);
        Assert.False(receiver.AacAvailable); // the test factory loads nothing: the ALAC-only variant (R3, R4)
        Assert.Equal(ReceiverFeatures.AlacOnly, receiver.Features);
        if (receiver.Advertisement is { } advertisement)
        {
            Assert.Equal(options.Name, advertisement.Name);
            Assert.Equal(receiver.RtspPort, advertisement.Port);
            Assert.Contains("cn=0,1", advertisement.RaopTxt);
        }
        else
        {
            output.WriteLine("mDNS responder could not bind; the advertisement part is skipped");
        }

        // GET /info on an unpaired connection (3.2): the R3 ALAC-only formats, no bit 17, the name.
        await using (var session = await ConnectAsync(receiver, pair: false))
        {
            var info = await session.GetInfoAsync(withQualifier: false, None);
            Assert.Equal(options.Name, info.GetString("name"));
            Assert.Equal(identity.DeviceId, info.GetString("deviceID"));
            Assert.Equal(0x4L, info.GetInt64("statusFlags"));
            Assert.Equal(unchecked((long)ReceiverFeatures.AlacOnly), info.GetInt64("features"));
            Assert.Equal(0x40000L, info.GetDictionary("supportedFormats")!.GetInt64("bufferStream"));
        }

        // Rename and password without a restart (3.3): /info follows, the TXT flags gain the password bit.
        await receiver.UpdateSettingsAsync("Renamed Receiver", "loopback", None);
        Assert.Equal("Renamed Receiver", receiver.Name);
        if (receiver.Advertisement is { } renamed)
        {
            Assert.Equal("Renamed Receiver", renamed.Name);
            Assert.Contains("flags=0x84", renamed.AirplayTxt);
        }

        await using (var session = await ConnectAsync(receiver, pair: false))
        {
            var info = await session.GetInfoAsync(withQualifier: true, None);
            Assert.Equal("Renamed Receiver", info.GetString("name"));
            Assert.Equal(0x84L, info.GetInt64("statusFlags"));
        }

        var port = receiver.RtspPort;
        await receiver.StopAsync(None);
        Assert.Equal(ReceiverStatus.Off, receiver.Status);
        Assert.Equal(0, receiver.RtspPort);
        Assert.Null(receiver.Advertisement);
        await AssertRefusedAsync(port);

        // enabled → true again (3.3): a fresh listener.
        await receiver.StartAsync(None);
        Assert.Equal(ReceiverStatus.Advertising, receiver.Status);
        await receiver.DisposeAsync();
        Assert.Equal(ReceiverStatus.Off, receiver.Status);
        Assert.True(receiver.Events.Completion.IsCompleted);
    }

    [Fact]
    public async Task A_taken_rtsp_port_is_an_error_status()
    {
        using var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        blocker.Listen(1);
        var port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
        await using var receiver = new AirplayReceiver(Options(ReceiverIdentity.Create()) with { RtspPort = port }, new CapturingOutput());

        await Assert.ThrowsAnyAsync<SocketException>(() => receiver.StartAsync(None));
        Assert.Equal(ReceiverStatus.Error, receiver.Status);
        Assert.Equal(0, receiver.RtspPort);
    }

    private async Task RealtimeToneAsync()
    {
        await using var rig = await PtpRig.StartAsync(StreamType.Realtime, new OutputLoggerFactory(output));
        var tone = Tone(Packet * 376); // about 3 s
        var player = await rig.Sender.CreateGroupPlayerAsync([rig.Describe()], new GroupPlayerOptions { VolumePollInterval = null, Sender = SenderId }, None);
        try
        {
            await player.AddReceiverAsync(rig.Describe(), 40, None);
            var connected = await rig.Events.WaitForAsync<ReceiverEvent.SenderConnected>();
            Assert.Equal(SenderId.Name, connected.Sender.Name);
            var started = await rig.Events.WaitForAsync<ReceiverEvent.SenderStreamStarted>();
            Assert.Equal(StreamType.Realtime, started.StreamType);
            Assert.Equal("ALAC/44100/16/2", started.Format);
            Assert.Equal(ReceiverStatus.Streaming, rig.Receiver.Status);
            Assert.Equal(SenderId.Name, rig.Receiver.Sender?.Name);

            // The member's level reaches the output as the master level (R9, 5.5).
            var volume = await rig.Events.WaitForAsync<ReceiverEvent.SenderVolume>();
            Assert.Contains($"level {volume.Level}", rig.Output.Calls);

            await player.StartAsync(None);
            var writing = Task.Run(() => player.Write(tone));
            await rig.Output.WaitForToneAsync(tone, output);
            await writing;

            AssertTone(rig.Output.Samples(), tone, mutedFrames: 0);
            Assert.Contains("resume", rig.Output.Calls); // the first frame released while paused resumed the output (5.1)

            // The player's now-playing: DMAP, artwork and progress with one rtptime become one output call (R10, 5.5), its
            // elapsed read from the next frame the buffer releases (the player defines elapsed as the next sample written).
            var artwork = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 };
            await player.SetNowPlayingAsync(new TrackMetadata("Tone", "Loopback", "Facade", TimeSpan.FromSeconds(30)), artwork, "image/png", TimeSpan.FromSeconds(4), None);
            var nowPlaying = await rig.Output.WaitForNowPlayingAsync(1);
            Assert.Equal(("Tone", "Loopback", "Facade"), (nowPlaying.Metadata.Title, nowPlaying.Metadata.Artist, nowPlaying.Metadata.Album));
            Assert.Equal(artwork, nowPlaying.Artwork);
            Assert.Equal("image/png", nowPlaying.ContentType);
            Assert.InRange(nowPlaying.Elapsed.TotalSeconds, 2.0, 4.5);
            await Task.Delay(700);
            Assert.Single(rig.Output.NowPlaying);
            var stats = rig.Receiver.Statistics!;
            Assert.Equal(StreamType.Realtime, stats.StreamType);
            Assert.Equal(0, stats.LateFrames);
            Assert.Equal(0, stats.MissingFrames);
            Assert.True(rig.Receiver.PtpStatistics is { PairsSeen: > 0 }, "the session's PTP slave follows the sender's grandmaster");
            var periodic = await rig.Events.WaitForAsync<ReceiverEvent.Statistics>();
            Assert.Equal(StreamType.Realtime, periodic.Playout.StreamType);
            Assert.NotNull(periodic.Ptp);
        }
        finally
        {
            await player.DisposeAsync(); // TEARDOWN {streams}, then {}
        }

        var gone = await rig.Events.WaitForAsync<ReceiverEvent.SenderGone>();
        Assert.Equal("TEARDOWN", gone.Reason);
        Assert.Contains("release", rig.Output.Calls);
        await WaitUntilAsync(() => rig.Receiver.Status == ReceiverStatus.Advertising, "status back to advertising");
        Assert.Null(rig.Receiver.Sender);
        Assert.Null(rig.Receiver.Statistics);
    }

    [Fact]
    public Task Buffered_alac_from_the_group_player_pauses_seeks_and_splices() => Bounded(BufferedAlacAsync);

    [Fact]
    public Task Realtime_tone_from_the_group_player_under_ptp_reaches_the_output_and_teardown_releases_it() => Bounded(RealtimeToneAsync);

    /// <summary>Runs a group-player test with an overall bound, so a hang fails the test with its log instead of stalling the run.</summary>
    private static async Task Bounded(Func<Task> test)
    {
        var run = test();
        await run.WaitAsync(TimeSpan.FromSeconds(90));
    }

    private async Task BufferedAlacAsync()
    {
        await using var rig = await PtpRig.StartAsync(StreamType.Buffered, new OutputLoggerFactory(output));
        var first = Tone(Packet * 250, seed: 1); // about 2 s
        var player = await rig.Sender.CreateGroupPlayerAsync([rig.Describe()], new GroupPlayerOptions { VolumePollInterval = null, Sender = SenderId }, None);
        try
        {
            await player.AddReceiverAsync(rig.Describe(), 50, None);
            var started = await rig.Events.WaitForAsync<ReceiverEvent.SenderStreamStarted>();
            Assert.Equal(StreamType.Buffered, started.StreamType);
            Assert.Equal("ALAC/44100/16/2", started.Format);

            // The ring holds 1 s: the pacer must run while the rest is written (GroupPlayer.Write blocks on a full ring).
            await player.StartAsync(None);
            await Task.Run(() => player.Write(first));
            await rig.Events.WaitForAsync<ReceiverEvent.SenderResumed>(); // SETRATEANCHORTIME rate 1 (4.7)
            await rig.Output.WaitForToneAsync(first, output);
            AssertTone(rig.Output.Samples(), first, mutedFrames: Packet); // the first block after a start is muted (5.2)
            Assert.Equal(StreamType.Buffered, rig.Receiver.Statistics!.StreamType);

            // Pause: rate 0 and an immediate FLUSHBUFFERED (4.7, 5.5): the output is paused and flushed.
            var callsBefore = rig.Output.Calls.Count;
            await player.PauseAsync(None);
            await rig.Events.WaitForAsync<ReceiverEvent.SenderPaused>();
            var flush = await rig.Events.WaitForAsync<ReceiverEvent.SenderFlush>();
            Assert.False(flush.Deferred);
            await WaitUntilAsync(() => rig.Output.Calls.Skip(callsBefore).Contains("flush"), "the output flushed");
            Assert.Contains("pause", rig.Output.Calls.Skip(callsBefore));
            Assert.Equal(ReceiverStatus.Streaming, rig.Receiver.Status);

            // Seek: new audio after the flush plays from its own start (its first block muted), after a resume.
            rig.Output.Clear();
            var second = Tone(Packet * 120, seed: 2);
            await player.StartAsync(None);
            await Task.Run(() => player.Write(second));
            await rig.Output.WaitForToneAsync(second, output);
            AssertTone(rig.Output.Samples(), second, mutedFrames: Packet);
            Assert.Contains("resume", rig.Output.Calls);

            // Splice (gapless track change): a deferred FLUSHBUFFERED over blocks 40-59 of the next track, registered before they
            // are sent (4.7). The player is paused first so no silence packets move the sequence; after the resume the output
            // gets blocks 0-39 and 61-99 (60 is muted as the first after the gap) and is neither flushed nor paused by the splice.
            var transport = Assert.IsType<BufferedStreamTransport>(((GroupPlayer)player).Transport);
            await player.PauseAsync(None);
            var third = Tone(Packet * 100, seed: 3);
            player.Write(third); // fits the ring
            var next = unchecked((ushort)transport.NextWireSequence);
            await transport.FlushRangeAsync(unchecked((ushort)(next + 40)), 0, unchecked((ushort)(next + 60)), 0, None);
            var splice = await rig.Events.WaitForAsync<ReceiverEvent.SenderFlush>(f => f.Deferred);
            Assert.True(splice.Deferred);
            rig.Output.Clear();
            await player.StartAsync(None);
            await WaitUntilAsync(() => Contains(rig.Output.Samples(), third, 95), "the audio after the splice");
            var recorded = rig.Output.Samples();
            Assert.True(Contains(recorded, third, 10), "block 10 is played");
            Assert.True(Contains(recorded, third, 39), "block 39 is played");
            Assert.False(Contains(recorded, third, 40), "block 40 is spliced out");
            Assert.False(Contains(recorded, third, 59), "block 59 is spliced out");
            Assert.False(Contains(recorded, third, 60), "block 60, the first after the gap, is muted (a discontinuity, 5.2)");
            Assert.True(Contains(recorded, third, 61), "block 61 is played");
            Assert.Equal(["resume"], rig.Output.Calls.Where(c => c is "flush" or "pause" or "resume"));
        }
        finally
        {
            await player.DisposeAsync();
        }

        await rig.Events.WaitForAsync<ReceiverEvent.SenderGone>();
        Assert.Contains("release", rig.Output.Calls);
    }

    [Fact]
    public async Task Buffered_aac_from_a_raw_block_writer_is_decoded_in_anchor_only_timing()
    {
        var ffmpeg = FfmpegDirectory();
        var factory = new FfmpegAacDecoderFactory(ffmpeg);
        if (!factory.TryCreate(out var probe, out var reason))
        {
            output.WriteLine($"skipped: libavcodec not found ({reason}); run tools/get-ffmpeg.ps1 or set AIRPLAY_FFMPEG_PATH");
            return;
        }

        probe.Dispose();
        var sink = new CapturingOutput();
        await using var receiver = new AirplayReceiver(Options(ReceiverIdentity.Create()) with { AacDecoderFactory = factory }, sink);
        await receiver.StartAsync(None);
        Assert.True(receiver.AacAvailable);
        Assert.Equal(ReceiverFeatures.Default, receiver.Features);
        await using var events = EventLog.Start(receiver);

        await using var session = await ConnectAsync(receiver);
        await session.SetupSessionAsync(SessionSetup(), None);
        var stream = await session.SetupStreamAsync(
            new StreamSetupRequest(StreamType.Buffered, session.Pairing!.DefaultStreamKey, 6001, CompressionType: 8, AudioFormat: 0x400000),
            None);
        Assert.Equal(StreamType.Buffered, stream.Type);
        Assert.Equal(8388608L, stream.AudioBufferSize);
        var started = await events.WaitForAsync<ReceiverEvent.SenderStreamStarted>();
        Assert.Equal("AAC-LC/44100/2", started.Format);
        Assert.Null(receiver.PtpStatistics); // no PtpPort: anchor-only (R5, R6)

        var vector = AacVector.Load();
        await using (var writer = await RawBlockWriter.ConnectAsync(stream.DataPort, session.Pairing.DefaultStreamKey))
        {
            for (var i = 0; i < vector.Blocks.Count; i++)
            {
                await writer.WriteAsync(BufferedBlock.SsrcAac, (uint)i, (uint)(i * BufferedBlock.AacFramesPerBlock), vector.Blocks[i]);
            }

            // The anchor maps the first block to now + AnchorLead (R6, anchor-only).
            await session.SetRateAnchorTimeAsync(1, 0, 0, 0, 0, None);
            await WaitUntilAsync(() => sink.FrameCount >= vector.ExpectedFrames - BufferedBlock.AacFramesPerBlock, "the decoded AAC tone");
        }

        await Task.Delay(300);
        var pcm = sink.Samples();
        output.WriteLine($"{sink.FrameCount} frames (expected {vector.ExpectedFrames}), RMS {Rms(pcm):F1} (expected {vector.ExpectedRms})");
        Assert.InRange(sink.FrameCount, vector.ExpectedFrames - BufferedBlock.AacFramesPerBlock, vector.ExpectedFrames + BufferedBlock.AacFramesPerBlock);
        Assert.InRange(Rms(pcm), vector.ExpectedRms * 0.95, vector.ExpectedRms * 1.05);
        Assert.Equal("resume", sink.Calls.First(c => c is "resume" or "pause"));
        Assert.Contains("AAC", receiver.Statistics!.Decoder ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Volume_dmap_artwork_and_progress_become_one_now_playing_call_and_a_master_level()
    {
        var sink = new CapturingOutput();
        await using var receiver = new AirplayReceiver(Options(ReceiverIdentity.Create()), sink);
        await receiver.StartAsync(None);
        await using var events = EventLog.Start(receiver);
        await using var session = await ConnectAsync(receiver);
        await session.SetupSessionAsync(SessionSetup(), None);
        Assert.Equal(ReceiverStatus.Connected, receiver.Status);
        await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, session.Pairing!.DefaultStreamKey, 6001), None);

        // R9: -15 dB is level 50; GET_PARAMETER answers the sender's level while no output level is known.
        await session.SetVolumeAsync(-15.0, None);
        var volume = await events.WaitForAsync<ReceiverEvent.SenderVolume>();
        Assert.Equal((50, -15.0), (volume.Level, volume.Db));
        Assert.Contains("level 50", sink.Calls);
        Assert.Equal(-15.0, await session.GetVolumeAsync(None), 3);

        // DMAP, artwork and progress with one rtptime: one call once all three arrived (5.5).
        const uint key = 88_200;
        var metadata = new TrackMetadata("Loopback Title", "Loopback Artist", "Loopback Album", TimeSpan.FromSeconds(180));
        var artwork = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
        await session.SetMetadataAsync(metadata, key, None);
        await session.SetArtworkAsync(artwork, "image/jpeg", key, None);
        await session.SetProgressAsync(new ProgressInfo(key, key + (10 * 44100), key + (180 * 44100), key), None);
        var call = await sink.WaitForNowPlayingAsync(1);
        Assert.Equal(("Loopback Title", "Loopback Artist", "Loopback Album"), (call.Metadata.Title, call.Metadata.Artist, call.Metadata.Album));
        Assert.Equal(artwork, call.Artwork);
        Assert.Equal("image/jpeg", call.ContentType);
        Assert.InRange(call.Elapsed.TotalSeconds, 9.9, 10.1); // the buffer is empty: the sender's (current - start) / 44100
        Assert.Single(events.All.OfType<ReceiverEvent.SenderMetadata>());
        Assert.Single(events.All.OfType<ReceiverEvent.SenderArtwork>());
        Assert.Single(events.All.OfType<ReceiverEvent.SenderProgress>());

        // A new track with DMAP only: sent 500 ms after its first item, without waiting for the rest.
        const uint next = 9_000_000;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await session.SetMetadataAsync(metadata with { Title = "Second Title" }, next, None);
        var second = await sink.WaitForNowPlayingAsync(2);
        Assert.Equal("Second Title", second.Metadata.Title);
        Assert.Null(second.Artwork);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400), $"sent after {watch.Elapsed.TotalMilliseconds} ms");
        await Task.Delay(700);
        Assert.Equal(2, sink.NowPlaying.Count);

        await session.TeardownAsync(None);
        await events.WaitForAsync<ReceiverEvent.SenderGone>();
    }

    [Fact]
    public async Task Teardown_of_the_stream_keeps_the_session_and_teardown_of_the_session_ends_it()
    {
        var sink = new CapturingOutput();
        await using var receiver = new AirplayReceiver(Options(ReceiverIdentity.Create()), sink);
        await receiver.StartAsync(None);
        await using var events = EventLog.Start(receiver);
        await using var session = await ConnectAsync(receiver);
        await session.SetupSessionAsync(SessionSetup(), None);
        var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, session.Pairing!.DefaultStreamKey, 6001), None);
        Assert.Equal(ReceiverStatus.Streaming, receiver.Status);
        Assert.NotNull(receiver.Statistics);

        // {streams: [{streamID}]}: the stream ends (output released, bit 17 cleared), the session stays (4.9).
        var answer = await session.Connection.SendAsync(SessionRequests.TeardownStream(session.Connection.SessionUri, stream.StreamId!.Value, StreamType.Realtime), None);
        Assert.Equal(200, answer.StatusCode);
        Assert.Contains("release", sink.Calls);
        Assert.Equal(ReceiverStatus.Connected, receiver.Status);
        Assert.Null(receiver.Statistics);
        Assert.Empty(events.All.OfType<ReceiverEvent.SenderGone>());
        Assert.Equal(0x4L, (await session.GetInfoAsync(withQualifier: false, None)).GetInt64("statusFlags"));

        // {}: the session ends; Connection: close.
        var end = await session.Connection.SendAsync(SessionRequests.TeardownSession(session.Connection.SessionUri), None);
        Assert.Equal(200, end.StatusCode);
        Assert.Equal("close", end.Header(RtspHeaders.Connection));
        Assert.Equal("TEARDOWN", (await events.WaitForAsync<ReceiverEvent.SenderGone>()).Reason);
        Assert.Equal(ReceiverStatus.Advertising, receiver.Status);
        Assert.Null(receiver.Sender);
    }

    [Fact]
    public async Task A_body_less_teardown_ends_the_session_like_an_empty_one()
    {
        var sink = new CapturingOutput();
        await using var receiver = new AirplayReceiver(Options(ReceiverIdentity.Create()), sink);
        await receiver.StartAsync(None);
        await using var events = EventLog.Start(receiver);
        await using var session = await ConnectAsync(receiver);
        await session.SetupSessionAsync(SessionSetup(), None);
        await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, session.Pairing!.DefaultStreamKey, 6001), None);

        var answer = await session.Connection.SendAsync(RtspRequest.Create("TEARDOWN", session.Connection.SessionUri), None);
        Assert.Equal(200, answer.StatusCode);
        Assert.Equal("TEARDOWN without a body", (await events.WaitForAsync<ReceiverEvent.SenderGone>()).Reason);
        Assert.Contains("release", sink.Calls);
        Assert.Equal(ReceiverStatus.Advertising, receiver.Status);
        await session.Connection.Closed.WaitAsync(Patience);
    }

    [Fact]
    public async Task EndSessionAsync_evicts_the_sender_after_releasing_the_output_and_a_second_sender_evicts_the_first()
    {
        var sink = new CapturingOutput();
        await using var receiver = new AirplayReceiver(Options(ReceiverIdentity.Create()), sink);
        await receiver.StartAsync(None);
        await using var events = EventLog.Start(receiver);

        // The host's eviction (R8: Spotify claimed the output).
        await using (var first = await ConnectAsync(receiver))
        {
            await first.SetupSessionAsync(SessionSetup(), None);
            await first.SetupStreamAsync(new StreamSetupRequest(StreamType.Buffered, first.Pairing!.DefaultStreamKey, 6001), None);
            Assert.Equal(ReceiverStatus.Streaming, receiver.Status);

            await receiver.EndSessionAsync("spotify took over", None);
            Assert.Contains("release", sink.Calls); // completed after ReleaseAsync
            Assert.Equal("spotify took over", (await events.WaitForAsync<ReceiverEvent.SenderGone>()).Reason);
            Assert.Equal(ReceiverStatus.Advertising, receiver.Status);
            await first.Connection.Closed.WaitAsync(Patience);
            await receiver.EndSessionAsync("again", None); // a no-op without a session
            Assert.Single(events.All.OfType<ReceiverEvent.SenderGone>());
        }

        // A second sender's stream SETUP ends the first sender's session (R8, 4.3).
        events.Clear();
        sink.Clear();
        await using var a = await ConnectAsync(receiver);
        await a.SetupSessionAsync(SessionSetup(), None);
        await a.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, a.Pairing!.DefaultStreamKey, 6001), None);
        await using var b = await ConnectAsync(receiver);
        await b.SetupSessionAsync(SessionSetup(SenderId with { Name = "second phone", DeviceId = "02:00:00:00:0C:02" }), None);
        Assert.Equal(ReceiverStatus.Streaming, receiver.Status);
        Assert.Equal(SenderId.Name, receiver.Sender?.Name); // the streaming sender stays the one shown until the claim
        await b.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, b.Pairing!.DefaultStreamKey, 6001), None);

        var gone = await events.WaitForAsync<ReceiverEvent.SenderGone>();
        Assert.StartsWith("another sender", gone.Reason, StringComparison.Ordinal);
        await a.Connection.Closed.WaitAsync(Patience);
        Assert.Equal(ReceiverStatus.Streaming, receiver.Status);
        Assert.Equal("second phone", receiver.Sender?.Name);
        Assert.Equal(2, events.All.OfType<ReceiverEvent.SenderStreamStarted>().Count());

        // StopAsync ends the remaining session and closes the listener.
        var port = receiver.RtspPort;
        await receiver.StopAsync(None);
        Assert.Equal("receiver stopped", (await events.WaitForAsync<ReceiverEvent.SenderGone>(g => g.Reason == "receiver stopped")).Reason);
        await b.Connection.Closed.WaitAsync(Patience);
        await AssertRefusedAsync(port);
    }

    private static AirplayReceiverOptions Options(ReceiverIdentity identity) => new()
    {
        Name = "Facade Loopback",
        Identity = identity,
        BindAddress = IPAddress.Loopback,
        RtspPort = 0,
        Advertise = false,
        AacDecoderFactory = new NoAacDecoders(),
        StatisticsInterval = TimeSpan.FromMilliseconds(500),
    };

    private static SessionSetupRequest SessionSetup(SenderIdentity? sender = null) =>
        new(sender ?? SenderId, TimingProtocol.Ptp, 0, Guid.NewGuid().ToString().ToUpperInvariant(), null, GroupContainsGroupLeader: false, IsMultiSelectAirPlay: false, [IPAddress.Loopback], 0x0200_00FF_FE00_0C01UL);

    private static DiscoveredReceiver Describe(AirplayReceiver receiver)
    {
        var advertisement = ReceiverTxtRecords.Advertisement(receiver.Options with { Name = receiver.Name }, receiver.RtspPort, receiver.Features);
        return new DiscoveredReceiver(
            receiver.Options.Identity.DeviceId,
            receiver.Name,
            [IPAddress.Loopback],
            receiver.RtspPort,
            AirplayTxtRecord.Parse(advertisement.AirplayTxt),
            RaopTxtRecord.Parse(advertisement.RaopTxt),
            DateTimeOffset.UtcNow);
    }

    private static async Task<ReceiverSession> ConnectAsync(AirplayReceiver receiver, bool pair = true)
    {
        var connection = await RtspConnection.ConnectAsync(
            new IPEndPoint(IPAddress.Loopback, receiver.RtspPort),
            new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(10) },
            NullLogger.Instance,
            None);
        var session = new ReceiverSession(Describe(receiver), connection, new HapPairingClient(NullLogger<HapPairingClient>.Instance), ReceiverQuirks.Default, NullLogger<ReceiverSession>.Instance);
        if (pair)
        {
            await session.PairAsync(new MemoryPairingStore(), pinPrompt: null, None);
        }

        return session;
    }

    private static async Task AssertRefusedAsync(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var error = await Assert.ThrowsAsync<SocketException>(async () => await probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)));
        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
    }

    /// <summary>A stereo tone with a rising amplitude, so every 352-frame window occurs once.</summary>
    private static short[] Tone(int frames, int seed = 0)
    {
        var pcm = new short[frames * 2];
        var frequency = 440.0 + (seed * 110.0);
        for (var i = 0; i < frames; i++)
        {
            var amplitude = 3000.0 + (6000.0 * i / frames);
            var value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * i / 44100.0));
            pcm[2 * i] = value;
            pcm[(2 * i) + 1] = (short)-value;
        }

        return pcm;
    }

    /// <summary>True when block <paramref name="block"/> (352 frames) of <paramref name="tone"/> occurs in <paramref name="recorded"/>.</summary>
    private static bool Contains(short[] recorded, short[] tone, int block) =>
        recorded.AsSpan().IndexOf(tone.AsSpan(block * Packet * 2, Packet * 2)) >= 0;

    /// <summary>Where <paramref name="tone"/> (from its second block on) starts in <paramref name="recorded"/>, in samples, or -1.</summary>
    private static int FindTone(short[] recorded, short[] tone)
    {
        var window = tone.AsSpan(2 * Packet * 2, Packet * 2);
        var at = recorded.AsSpan().IndexOf(window);
        return at < 0 ? -1 : at - (2 * Packet * 2);
    }

    /// <summary>
    /// SPEC-receiver.md 11.1: the recorded output carries the tone sample for sample (except <paramref name="mutedFrames"/> at its
    /// start, the muted first block of 5.2), its length within one packet of the tone's and its RMS within 5 %.
    /// </summary>
    private static void AssertTone(short[] recorded, short[] tone, int mutedFrames)
    {
        var start = FindTone(recorded, tone);
        Assert.True(start >= 0, "the tone is in the output");
        var available = Math.Min(tone.Length, recorded.Length - start);
        Assert.True(available >= tone.Length - (Packet * 2), $"{available / 2} of {tone.Length / 2} frames recorded");
        var played = recorded.AsSpan(start, available);
        Assert.True(played[(mutedFrames * 2)..].SequenceEqual(tone.AsSpan(mutedFrames * 2, available - (mutedFrames * 2))), "the recorded tone equals the sent one");
        var expected = Rms(tone.AsSpan(0, available));
        Assert.InRange(Rms(played), expected * 0.95, expected * 1.05);
    }

    private static double Rms(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in pcm)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / pcm.Length);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
        }
    }

    private static string? FfmpegDirectory()
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

    /// <summary>
    /// Our sender (<see cref="AirplaySender"/>, PTP grandmaster on a private loopback <see cref="PtpPort"/>) and the product
    /// receiver (its <see cref="PtpSlave"/> on a second private port), each told the other's ports (SPEC-receiver.md 11.1).
    /// </summary>
    private sealed class PtpRig : IAsyncDisposable
    {
        private PtpRig(AirplaySender sender, AirplayReceiver receiver, CapturingOutput sink, EventLog events, PtpPort senderPort, PtpPort receiverPort)
        {
            Sender = sender;
            Receiver = receiver;
            Output = sink;
            Events = events;
            SenderPort = senderPort;
            ReceiverPort = receiverPort;
        }

        public AirplaySender Sender { get; }

        public AirplayReceiver Receiver { get; }

        public CapturingOutput Output { get; }

        public EventLog Events { get; }

        public PtpPort SenderPort { get; }

        public PtpPort ReceiverPort { get; }

        public static async Task<PtpRig> StartAsync(StreamType preferred, ILoggerFactory logs)
        {
            var portOptions = new PtpPortOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 };
            var senderPort = new PtpPort(portOptions);
            var receiverPort = new PtpPort(portOptions);
            await senderPort.EnsureBoundAsync(None);
            await receiverPort.EnsureBoundAsync(None);
            var identity = ReceiverIdentity.Create();
            var sink = new CapturingOutput();
            var receiver = new AirplayReceiver(
                Options(identity) with
                {
                    PtpPort = receiverPort,
                    PtpSlave = new PtpSlaveOptions { PeerEventPort = senderPort.EventPort, PeerGeneralPort = senderPort.GeneralPort },
                },
                sink,
                logs);
            await receiver.StartAsync(None);
            var sender = new AirplaySender(
                new AirplaySenderOptions
                {
                    LocalAddress = IPAddress.Loopback,
                    Identity = SenderId,
                    PreferredStream = preferred,
                    DefaultTimingProtocol = TimingProtocol.Ptp,
                    Quirks = new Dictionary<string, ReceiverQuirks> { [identity.DeviceId] = new() { TimingProtocol = TimingProtocol.Ptp } },
                    PtpPort = senderPort,
                    Ptp = new PtpMasterOptions { PeerEventPort = receiverPort.EventPort, PeerGeneralPort = receiverPort.GeneralPort },
                },
                new MemoryPairingStore(),
                logs);
            return new PtpRig(sender, receiver, sink, EventLog.Start(receiver), senderPort, receiverPort);
        }

        public DiscoveredReceiver Describe() => AirplayReceiverLoopbackTests.Describe(Receiver);

        public async ValueTask DisposeAsync()
        {
            await Sender.DisposeAsync();
            await Receiver.DisposeAsync();
            await Events.DisposeAsync();
            await SenderPort.DisposeAsync();
            await ReceiverPort.DisposeAsync();
        }
    }

    /// <summary>Drains <see cref="AirplayReceiver.Events"/> into a list.</summary>
    private sealed class EventLog : IAsyncDisposable
    {
        private readonly ConcurrentQueue<ReceiverEvent> _events = new();
        private readonly CancellationTokenSource _stop = new();
        private Task _reader = Task.CompletedTask;

        public IReadOnlyList<ReceiverEvent> All => [.. _events];

        public static EventLog Start(AirplayReceiver receiver)
        {
            var log = new EventLog();
            log._reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in receiver.Events.ReadAllAsync(log._stop.Token))
                    {
                        log._events.Enqueue(e);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
            return log;
        }

        public void Clear() => _events.Clear();

        public async Task<T> WaitForAsync<T>(Func<T, bool>? match = null)
            where T : ReceiverEvent
        {
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline)
            {
                if (_events.OfType<T>().FirstOrDefault(e => match?.Invoke(e) ?? true) is { } found)
                {
                    return found;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"Timed out waiting for {typeof(T).Name}; seen: {string.Join(", ", _events.Select(e => e.GetType().Name))}.");
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _reader;
            _stop.Dispose();
        }
    }

    /// <summary>An <see cref="IReceiverAudioOutput"/> that records the frames and the calls (SPEC-receiver.md 5.5).</summary>
    private sealed class CapturingOutput : IReceiverAudioOutput
    {
        private readonly Lock _lock = new();
        private readonly List<short> _samples = [];
        private readonly List<string> _calls = [];
        private readonly List<NowPlayingCall> _nowPlaying = [];

        public int QueuedFrames => 0;

        public int FrameCount
        {
            get
            {
                lock (_lock)
                {
                    return _samples.Count / 2;
                }
            }
        }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_lock)
                {
                    return [.. _calls];
                }
            }
        }

        public IReadOnlyList<NowPlayingCall> NowPlaying
        {
            get
            {
                lock (_lock)
                {
                    return [.. _nowPlaying];
                }
            }
        }

        public short[] Samples()
        {
            lock (_lock)
            {
                return [.. _samples];
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _samples.Clear();
                _calls.Clear();
            }
        }

        public async Task WaitForToneAsync(short[] tone, ITestOutputHelper log)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < deadline)
            {
                var recorded = Samples();
                var start = FindTone(recorded, tone);
                if (start >= 0 && recorded.Length - start >= tone.Length)
                {
                    return;
                }

                await Task.Delay(50);
            }

            log.WriteLine($"recorded {FrameCount} frames; calls: {string.Join(", ", Calls)}");
            throw new TimeoutException("Timed out waiting for the tone in the output.");
        }

        public async Task<NowPlayingCall> WaitForNowPlayingAsync(int count)
        {
            await WaitUntilAsync(() => NowPlaying.Count >= count, $"{count} now-playing call(s)");
            return NowPlaying[count - 1];
        }

        public ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _samples.AddRange(frames.Span);
            }

            return ValueTask.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Record("pause");

        public Task ResumeAsync(CancellationToken cancellationToken) => Record("resume");

        public Task FlushAsync(CancellationToken cancellationToken) => Record("flush");

        public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken) => Record(FormattableString.Invariant($"level {level}"));

        public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _nowPlaying.Add(new NowPlayingCall(metadata, artwork?.ToArray(), artworkContentType, elapsed));
                _calls.Add("nowplaying");
            }

            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CancellationToken cancellationToken) => Record("release");

        private Task Record(string call)
        {
            lock (_lock)
            {
                _calls.Add(call);
            }

            return Task.CompletedTask;
        }
    }

    private sealed record NowPlayingCall(TrackMetadata Metadata, byte[]? Artwork, string? ContentType, TimeSpan Elapsed);

    /// <summary>Writes Information and above to the test output (diagnostics when a loopback test fails).</summary>
    private sealed class OutputLoggerFactory(ITestOutputHelper sink) : ILoggerFactory, ILoggerProvider
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

        public void Dispose()
        {
        }

        private sealed class Logger(OutputLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                try
                {
                    owner.Write(FormattableString.Invariant($"{owner._clock.Elapsed.TotalSeconds,8:F3} {logLevel,-11} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception.GetType().Name + ": " + exception.Message)}"));
                }
                catch (InvalidOperationException)
                {
                    // the test has finished
                }
            }
        }

        private void Write(string line) => sink.WriteLine(line);
    }

    /// <summary>Our sender's pairing store in memory.</summary>
    private sealed class MemoryPairingStore : IPairingStore
    {
        private readonly ConcurrentDictionary<string, PairingCredentials> _pairings = new();
        private readonly ControllerIdentity _controller = ControllerIdentity.Generate();

        public Task<ControllerIdentity> GetControllerIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(_controller);

        public Task<PairingCredentials?> GetAsync(string receiverId, CancellationToken cancellationToken) =>
            Task.FromResult(_pairings.TryGetValue(receiverId, out var p) ? p : null);

        public Task SaveAsync(string receiverId, PairingCredentials credentials, CancellationToken cancellationToken)
        {
            _pairings[receiverId] = credentials;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string receiverId, CancellationToken cancellationToken)
        {
            _pairings.TryRemove(receiverId, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>An AAC factory that never loads (the ALAC-only variant of R3, without touching the default library search).</summary>
    private sealed class NoAacDecoders : IAacDecoderFactory
    {
        public bool TryCreate([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IAacDecoder? decoder, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? reason)
        {
            decoder = null;
            reason = "disabled in this test";
            return false;
        }
    }

    /// <summary>A test-only sender of raw buffered blocks (any SSRC) on one data connection, framed as SPEC-audio.md 4.2.</summary>
    private sealed class RawBlockWriter : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly byte[] _key;
        private readonly Dictionary<uint, RtpAudioPacketBuilder> _builders = [];

        private RawBlockWriter(TcpClient client, byte[] key)
        {
            _client = client;
            _stream = client.GetStream();
            _key = key;
        }

        public static async Task<RawBlockWriter> ConnectAsync(int port, byte[] key)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return new RawBlockWriter(client, key);
        }

        public async Task WriteAsync(uint ssrc, uint sequence, uint timestamp, byte[] payload)
        {
            if (!_builders.TryGetValue(ssrc, out var builder))
            {
                builder = new RtpAudioPacketBuilder(_key, ssrc);
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
