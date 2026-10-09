using System.Diagnostics;
using System.Globalization;
using System.Net;
using Airplay2.Sender.Alac;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Session;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Streams;

/// <summary>
/// Live tests of the buffered stream (WORK-PLAN.md buffered-stream "Verify", section 4 rules). They return early unless
/// <c>AIRPLAY_LIVE=1</c> and write the decrypted wire exchange to <c>artifacts/live/&lt;date&gt;-&lt;probe&gt;.log</c>.
/// <list type="bullet">
/// <item><b>ShairportLive</b>: shairport-sync 5.5.2 in the Docker VM, run by
/// <c>docker/shairport-sync/run-ptp-live-tests-in-vm.ps1 -Filter "FullyQualifiedName~BufferedLiveTests&amp;Category=ShairportLive"</c>
/// (bridge network: our grandmaster needs UDP 319/320, which nqptp owns in the VM's host namespace). The dummy backend
/// plays the stream; acceptance is read from <c>docker logs sps-test</c>.</item>
/// <item><b>SpeakerALive</b>: probe P16 on the Test Speaker ABCD: <b>3 s of the quiet 440 Hz tone at -24 dBFS, level 15</b>,
/// per run, only with <c>AIRPLAY_AUDIO_I_KNOW=1</c> (the test-run form of the CLI's <c>--i-know</c>), always ending in
/// TEARDOWN. One format per run: <c>SpeakerA_P16_alac_44100_s16_0xFACE</c> and <c>SpeakerA_P16_alac_48000_s24_0x15000000</c>.</item>
/// </list>
/// </summary>
public sealed class BufferedLiveTests(ITestOutputHelper output)
{
    private const double MaxToneSeconds = 3.0;
    private const int Level = 15;

    private static readonly string[] SpeakerAAirplay =
    [
        "acl=0", "deviceid=02:1A:2B:3C:4D:5E", "features=0x445F8A00,0x1C340", "rsf=0x0", "fv=p20.1.1.36.0x8b4c480", "flags=0x404",
        "model=Test Speaker ABCD", "manufacturer=Lautsprecher SpeakerA GmbH", "serialNumber=00000000-0000-0000-0000-000000000000",
        "protovers=1.1", "srcvers=366.0", "pi=02:1A:2B:3C:4D:5E", "gid=02:1A:2B:3C:4D:5E", "gcgl=0",
        "pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
    ];

    /// <summary>
    /// shairport-sync plays a buffered stream (type 103, ALAC 44.1/16 with SSRC 0x0000FACE) timed by our grandmaster: PTP
    /// session SETUP, SETPEERS, stream SETUP 103 (TCP data port, <c>audioBufferSize</c>), an explicit anchor 1.5 s ahead and
    /// 3 s of audio pushed ahead of real time through the run-ahead window; then an immediate flush (rate 0 +
    /// FLUSHBUFFERED), a new anchor and 1 s more; TEARDOWN. shairport-sync's log must show the ALAC/44100 decoding chain
    /// and frames played into the dummy backend.
    /// </summary>
    [Fact]
    [Trait("Category", "ShairportLive")]
    public async Task Shairport_plays_a_run_ahead_buffered_stream_with_flush_and_reanchor()
    {
        if (!Live())
        {
            return;
        }

        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SPS_HOST") ?? "127.0.0.1");
        var port = int.Parse(Environment.GetEnvironmentVariable("SPS_PORT") ?? "7000", CultureInfo.InvariantCulture);
        using var log = new SessionLiveLog("buffered-shairport", output);
        var receiver = new DiscoveredReceiver("SPS", "shairport-sync", [host], port, AirplayTxtRecord.Parse(["features=0x405FCA00,0x1C340", "flags=0x4", "model=ShairportSync"]), null, DateTimeOffset.UtcNow);
        await using var rig = await Rig.StartAsync(log, BufferedAudioFormat.Alac44100S16);
        await using var session = await rig.ConnectAsync(receiver, new RtspConnectionOptions());
        try
        {
            var stream = await rig.SetupAsync(session, volumeDb: -30.0);
            await Task.Delay(1500); // nqptp takes our clock as master; shairport-sync needs 400 ms of mastership

            var pcm = PlaybackProbes.Tone(TimeSpan.FromSeconds(3));
            var first = await rig.PushAsync(pcm, sequence: 1000, timestamp: 0x1000_0000, leadSeconds: 1.5);
            log.Write($"pushed 3 s: {first.Packets} packets in {first.Elapsed.TotalMilliseconds:0} ms; {rig.Transport.BytesWritten} bytes written");
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            Report(log, rig, await session.FeedbackAsync(CancellationToken.None), "feedback while playing");

            await rig.Transport.FlushAsync(CancellationToken.None);
            log.Write($"flushed at wire sequence {rig.Transport.NextWireSequence}");
            var second = await rig.PushAsync(PlaybackProbes.Tone(TimeSpan.FromSeconds(1), 660), first.NextSequence, first.NextTimestamp, leadSeconds: 0.5);
            log.Write($"pushed 1 s more: {second.Packets} packets");
            await Task.Delay(TimeSpan.FromSeconds(2));
            Report(log, rig, await session.FeedbackAsync(CancellationToken.None), "feedback after the flush");

            var status = Assert.Single(rig.Transport.ReceiverStates);
            Assert.True(status.DataConnected, "the TCP data connection is not up");
            Assert.False(status.Failed);
            Assert.Equal(0, rig.Transport.ControlErrors);
            Assert.Equal(0, rig.Transport.PacketsDropped);
            Assert.True(rig.Transport.AnchorsSent >= 3); // rate 1, rate 0 (flush), rate 1
            Assert.Equal(1, rig.Transport.FlushesSent);
            Assert.True(stream.AudioBufferSize > 0);
        }
        finally
        {
            await rig.TeardownAsync(session);
        }
    }

    /// <summary>
    /// The pacer-driven path on shairport-sync: the group player over <see cref="BufferedStreamTransport"/> with
    /// <see cref="BufferedStreamTransport.WrapSessionFactory"/> (D5 sequence, PTP, SETPEERS, stream SETUP 103, anchor with the
    /// first packet), 3 s of tone in real time, a pause (rate 0 + FLUSHBUFFERED), resume with 1 s more, dispose (TEARDOWN).
    /// </summary>
    [Fact]
    [Trait("Category", "ShairportLive")]
    public async Task Shairport_plays_the_buffered_stream_through_the_group_player()
    {
        if (!Live())
        {
            return;
        }

        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SPS_HOST") ?? "127.0.0.1");
        var port = int.Parse(Environment.GetEnvironmentVariable("SPS_PORT") ?? "7000", CultureInfo.InvariantCulture);
        using var log = new SessionLiveLog("buffered-shairport-player", output);
        var receiver = new DiscoveredReceiver("SPS", "shairport-sync", [host], port, AirplayTxtRecord.Parse(["features=0x405FCA00,0x1C340", "flags=0x4", "model=ShairportSync"]), null, DateTimeOffset.UtcNow);
        var clock = new MasterClock();
        await using var master = new PtpMaster(clock, log.CreateLogger<PtpMaster>(), PtpOptions());
        await using var control = new ControlPort(log.CreateLogger<ControlPort>());
        var transport = new BufferedStreamTransport(clock, master, control, new BufferedStreamOptions(), log.CreateLogger<BufferedStreamTransport>());

        async Task<IReceiverSession> Connect(DiscoveredReceiver r, CancellationToken cancellationToken)
        {
            var connection = await RtspConnection.ConnectAsync(new IPEndPoint(host, port), new RtspConnectionOptions(), log.CreateLogger("rtsp"), cancellationToken);
            var session = new ReceiverSession(r, connection, new HapPairingClient(log.CreateLogger<HapPairingClient>()), ReceiverQuirks.ForReceiver(r), log.CreateLogger<ReceiverSession>());
            await session.GetInfoAsync(false, cancellationToken);
            await session.PairAsync(new InMemoryPairingStore(), null, cancellationToken);
            return session;
        }

        await using (var player = new GroupPlayer(transport, transport.WrapSessionFactory(Connect), new GroupPlayerOptions { Sender = SessionTestSupport.Sender with { Name = "airplay2-streamer buffered test" } }, log.CreateLogger<GroupPlayer>()))
        {
            await player.AddReceiverAsync(receiver, 10, CancellationToken.None);
            await Task.Delay(1500);
            var writing = WriteOnThreadAsync(player, PlaybackProbes.Tone(TimeSpan.FromSeconds(3)));
            await player.StartAsync(CancellationToken.None);
            await writing;
            await Task.Delay(TimeSpan.FromSeconds(3.5));
            await player.PauseAsync(CancellationToken.None);
            log.Write($"paused: {player.Members.Values.Single()}, {transport.FlushesSent} flushes, {transport.AnchorsSent} anchors");
            writing = WriteOnThreadAsync(player, PlaybackProbes.Tone(TimeSpan.FromSeconds(1), 660));
            await player.StartAsync(CancellationToken.None);
            await writing;
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            var status = Assert.Single(transport.ReceiverStates);
            log.Write($"transport: {transport.PacketsSent} packets, {transport.BytesWritten} bytes, {transport.PacketsDropped} dropped, {transport.AnchorsSent} anchors, {transport.FlushesSent} flushes, {transport.ControlErrors} control errors; {status}; grandmaster {master.GetStatistics()}");
            Assert.True(status.DataConnected);
            Assert.Equal(0, transport.ControlErrors);
            Assert.True(transport.AnchorsSent >= 3);
            Assert.Equal(1, transport.FlushesSent);
            Assert.Equal(GroupMemberState.Streaming, player.Members.Values.Single());
        }

        Assert.Empty(master.Peers); // TEARDOWN released the grandmaster peer
    }

    /// <summary>P16 (A-Q7), run 1: buffered ALAC 44.1 kHz/16-bit with shairport-sync's made-up SSRC <c>0x0000FACE</c>.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P16_alac_44100_s16_0xFACE() => SpeakerAP16Async("P16-buffered-alac44-face", BufferedAudioFormat.Alac44100S16);

    /// <summary>P16 (A-Q7), run 2: buffered ALAC 48 kHz/24-bit with Apple's SSRC <c>0x15000000</c> (uncompressed ALAC frames).</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P16_alac_48000_s24_0x15000000() => SpeakerAP16Async("P16-buffered-alac48-24", BufferedAudioFormat.Alac48000S24);

    /// <summary>
    /// P16 prerequisite on the SpeakerA, <b>no audio</b> (control plane only, WORK-PLAN.md section 4 wave 3 allowances): which
    /// SETRATEANCHORTIME body does it accept? Variants on one buffered session: the session package's body (a fraction above
    /// 2^63 becomes a 16-byte bplist integer), the fraction and timeline id as signed 8-byte integers (what airplay2-receiver's
    /// <c>&amp; 0xffffffffffffffff</c> masks suggest senders send), with <c>networkTimeFlags: 0</c>, and rate 0 alone.
    /// Every answer is logged; nothing is written to the data connection.
    /// </summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task SpeakerA_P16_anchor_body_probe_without_audio()
    {
        if (!Live())
        {
            return;
        }

        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SpeakerA_HOST") ?? "192.0.2.26");
        var receiver = new DiscoveredReceiver("02:1A:2B:3C:4D:5E", "Test Speaker ABCD", [host], 7000, AirplayTxtRecord.Parse(SpeakerAAirplay), null, DateTimeOffset.UtcNow);
        using var log = new SessionLiveLog("P16-anchor-body", output);
        await using var rig = await Rig.StartAsync(log, BufferedAudioFormat.Alac44100S16, PtpOptions() with { PeerTimeout = Timeout.InfiniteTimeSpan });
        await using var session = await rig.ConnectAsync(receiver, new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(30) });
        try
        {
            var connectData = Environment.GetEnvironmentVariable("P16_CONNECT_DATA") == "1";
            await rig.SetupAsync(session, VolumeMapping.LevelToDb(Level), connectData);
            log.Write($"data connection {(connectData ? "open (no packets are sent)" : "not opened")}");
            await Task.Delay(TimeSpan.FromSeconds(Number("P16_WAIT_SECONDS", 3)));
            var uri = session.Connection.SessionUri;
            var clockId = rig.Master.ClockId;
            var variants = Environment.GetEnvironmentVariable("P16_ANCHOR_OFFSETS_MS") is { Length: > 0 } offsets
                ? offsets.Split(',').Select(o => long.Parse(o, CultureInfo.InvariantCulture)).Select(o => ($"rate 1 at now {o:+0;-0} ms, int64 fields", AnchorAt(rig.Clock.PtpNowNanos, o, clockId)))
                : AnchorVariants(rig.Clock.PtpNowNanos + 2_000_000_000UL, clockId);
            foreach (var (name, body) in variants)
            {
                var watch = Stopwatch.StartNew();
                var response = await session.Connection.SendAsync(RtspRequest.WithPlist("SETRATEANCHORTIME", uri, body), CancellationToken.None);
                log.Write($"RESULT P16 anchor body '{name}': {response.StatusCode} {response.ReasonPhrase} after {watch.Elapsed.TotalMilliseconds:0} ms, body {response.Body.Length} bytes");
                await Task.Delay(500);
            }

            log.Write($"grandmaster {rig.Master.GetStatistics()}");
        }
        finally
        {
            await rig.TeardownAsync(session);
        }
    }

    private static PlistDictionary AnchorAt(ulong nowNanos, long offsetMs, ulong clockId)
    {
        var (seconds, fraction) = NetworkTime.FromNanoseconds((ulong)((long)nowNanos + (offsetMs * 1_000_000L)));
        return new PlistDictionary
        {
            ["rate"] = 1L,
            ["rtpTime"] = 0x2000_0000L,
            ["networkTimeSecs"] = (long)seconds,
            ["networkTimeFrac"] = unchecked((long)fraction),
            ["networkTimeTimelineID"] = unchecked((long)clockId),
        };
    }

    private static IEnumerable<(string Name, PlistDictionary Body)> AnchorVariants(ulong ptpNanos, ulong clockId)
    {
        var (seconds, fraction) = NetworkTime.FromNanoseconds(ptpNanos);
        if (fraction <= long.MaxValue)
        {
            fraction |= 0x8000_0000_0000_0000UL; // force the >2^63 case the first run hit (a shift of < 1 s does not matter here)
        }

        PlistDictionary Body(object frac, object timeline, bool flags, long rate = 1)
        {
            var body = new PlistDictionary
            {
                ["rate"] = rate,
                ["rtpTime"] = 0x2000_0000L,
                ["networkTimeSecs"] = (long)seconds,
                ["networkTimeFrac"] = frac,
                ["networkTimeTimelineID"] = timeline,
            };
            if (flags)
            {
                body["networkTimeFlags"] = 0L;
            }

            return body;
        }

        yield return ("rate 1, frac uint64 (16-byte int), timeline int64", Body(fraction, unchecked((long)clockId), false));
        yield return ("rate 1, frac int64, timeline int64", Body(unchecked((long)fraction), unchecked((long)clockId), false));
        yield return ("rate 1, frac int64, timeline int64, networkTimeFlags 0", Body(unchecked((long)fraction), unchecked((long)clockId), true));
        yield return ("rate 1, frac uint64 (16-byte), timeline uint64 (16-byte), networkTimeFlags 0", Body(fraction, clockId, true));
        yield return ("rate 0, frac int64, timeline int64, networkTimeFlags 0", Body(unchecked((long)fraction), unchecked((long)clockId), true, 0));
        yield return ("rate 0 only", new PlistDictionary { ["rate"] = 0L });
    }

    /// <summary>
    /// One P16 run on the SpeakerA: pairing, PTP session SETUP, event channel, RECORD, SETPEERS, stream SETUP 103 with the
    /// format, volume at level 15, an explicit anchor 2 s ahead, 3 s of the quiet tone pushed through the window, feedback
    /// while it plays, TEARDOWN. What the receiver does with the stream is logged: the SETUP answer, whether it accepts and
    /// keeps the TCP connection, its anchor/feedback answers, the PTP exchange. Audibility needs a listener.
    /// </summary>
    private async Task SpeakerAP16Async(string probe, BufferedAudioFormat format)
    {
        if (!Live())
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("AIRPLAY_AUDIO_I_KNOW") != "1")
        {
            output.WriteLine("AIRPLAY_AUDIO_I_KNOW not set: audio probes need the explicit acknowledgement (WORK-PLAN.md section 4)");
            return;
        }

        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SpeakerA_HOST") ?? "192.0.2.26");
        var receiver = new DiscoveredReceiver("02:1A:2B:3C:4D:5E", "Test Speaker ABCD", [host], 7000, AirplayTxtRecord.Parse(SpeakerAAirplay), null, DateTimeOffset.UtcNow);
        using var log = new SessionLiveLog(probe, output);
        log.Write($"P16 format {format.Name}: SSRC 0x{format.Ssrc:X8}, audioFormat 0x{format.AudioFormat:X}, sr {format.SampleRate}, spf {format.FramesPerPacket}");
        await using var rig = await Rig.StartAsync(log, format, PtpOptions() with { PeerTimeout = Timeout.InfiniteTimeSpan });
        await using var session = await rig.ConnectAsync(receiver, new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(30) });
        try
        {
            await rig.SetupAsync(session, VolumeMapping.LevelToDb(Level));
            await Task.Delay(2000); // the SpeakerA's slave yields to our Announce after ~1 s (SPEC-audio.md 4.4 note)

            var seconds = Math.Min(MaxToneSeconds, 3.0);
            PushResult pushed;
            try
            {
                pushed = format.BitDepth == 24
                    ? await rig.PushFramesAsync(Tone24(seconds, format.SampleRate), format, sequence: 2000, timestamp: 0x2000_0000, leadSeconds: 2.0)
                    : await rig.PushAsync(PlaybackProbes.Tone(TimeSpan.FromSeconds(seconds)), sequence: 2000, timestamp: 0x2000_0000, leadSeconds: 2.0);
            }
            catch (RtspStatusException ex)
            {
                // The probe's answer, not a test failure: the receiver refuses to play this format (2026-10-09: 400 for both ALAC forms).
                log.Write($"RESULT P16 {format.Name}: SETRATEANCHORTIME rate 1 refused ({ex.Response.StatusCode}) with {rig.Transport.BytesWritten} bytes of audio written");
                return;
            }

            log.Write($"pushed {seconds} s of tone: {pushed.Packets} packets in {pushed.Elapsed.TotalMilliseconds:0} ms; {rig.Transport.BytesWritten} bytes written");
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(seconds + 2.5))
            {
                await Task.Delay(1000);
                Report(log, rig, await session.FeedbackAsync(CancellationToken.None), $"t={watch.Elapsed.TotalSeconds:0.0}s feedback");
            }

            await rig.Transport.PauseAsync(CancellationToken.None);
            log.Write($"RESULT P16 {format.Name}: transport {rig.Transport.PacketsSent} packets, {rig.Transport.BytesWritten} bytes, {rig.Transport.PacketsDropped} dropped, {rig.Transport.AnchorsSent} anchors answered, {rig.Transport.ControlErrors} control errors; {Assert.Single(rig.Transport.ReceiverStates)}; grandmaster {rig.Master.GetStatistics()}");
        }
        finally
        {
            await rig.TeardownAsync(session);
            while (rig.Transport.Events.TryRead(out var e))
            {
                log.Write($"RESULT P16 event {e}");
            }
        }
    }

    private static void Report(SessionLiveLog log, Rig rig, PlistDictionary? feedback, string what)
    {
        var streams = feedback?.GetArray("streams")?.OfType<PlistDictionary>().Select(s => $"{{type {s.GetInt64("type")}, sr {s.GetDouble("sr") ?? s.GetInt64("sr")}}}") ?? [];
        log.Write($"{what}: {(feedback is null ? "empty" : "[" + string.Join(", ", streams) + "]")}; {Assert.Single(rig.Transport.ReceiverStates)}");
    }

    private static Task WriteOnThreadAsync(GroupPlayer player, short[] pcm)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
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
        { IsBackground = true };
        thread.Start();
        return done.Task;
    }

    /// <summary>A 24-bit stereo 440 Hz tone at -24 dBFS with 10 ms fades, interleaved.</summary>
    private static int[] Tone24(double seconds, int sampleRate)
    {
        var frames = (int)(seconds * sampleRate);
        var amplitude = 8_388_607 * Math.Pow(10, -24 / 20.0);
        var fade = sampleRate / 100;
        var samples = new int[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)fade);
            var value = (int)Math.Round(amplitude * envelope * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            samples[2 * i] = value;
            samples[(2 * i) + 1] = value;
        }

        return samples;
    }

    private static PtpMasterOptions PtpOptions() => new()
    {
        EventPort = (int)Number("PTP_EVENT_PORT", PtpMessages.EventPort),
        GeneralPort = (int)Number("PTP_GENERAL_PORT", PtpMessages.GeneralPort),
    };

    private static double Number(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private bool Live()
    {
        if (Environment.GetEnvironmentVariable("AIRPLAY_LIVE") != "1")
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return false;
        }

        return true;
    }

    /// <summary>A grandmaster, a control port and a buffered transport for one live receiver, plus the D5 steps around them.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly SessionLiveLog _log;

        private Rig(SessionLiveLog log, MasterClock clock, PtpMaster master, ControlPort control, BufferedStreamTransport transport)
        {
            _log = log;
            Clock = clock;
            Master = master;
            Control = control;
            Transport = transport;
        }

        public MasterClock Clock { get; }

        public PtpMaster Master { get; }

        public ControlPort Control { get; }

        public BufferedStreamTransport Transport { get; }

        public static async Task<Rig> StartAsync(SessionLiveLog log, BufferedAudioFormat format, PtpMasterOptions? ptp = null)
        {
            var clock = new MasterClock();
            var master = new PtpMaster(clock, log.CreateLogger<PtpMaster>(), ptp ?? PtpOptions());
            var control = new ControlPort(log.CreateLogger<ControlPort>());
            var transport = new BufferedStreamTransport(clock, master, control, new BufferedStreamOptions { Format = format }, log.CreateLogger<BufferedStreamTransport>());
            await transport.StartAsync(CancellationToken.None);
            log.Write($"grandmaster {master.ClockId:X16} on UDP {master.EventPort}/{master.GeneralPort}; control port {transport.ControlPort}");
            return new Rig(log, clock, master, control, transport);
        }

        public async Task<BufferedReceiverSession> ConnectAsync(DiscoveredReceiver receiver, RtspConnectionOptions options)
        {
            var endpoint = new IPEndPoint(receiver.Addresses[0], receiver.Port);
            var connection = await RtspConnection.ConnectAsync(endpoint, options, _log.CreateLogger("rtsp"), CancellationToken.None);
            var inner = new ReceiverSession(receiver, connection, new HapPairingClient(_log.CreateLogger<HapPairingClient>()), ReceiverQuirks.ForReceiver(receiver), _log.CreateLogger<ReceiverSession>());
            var session = new BufferedReceiverSession(Transport, inner, receiver.Id);
            await session.GetInfoAsync(false, CancellationToken.None);
            var paired = await session.PairAsync(new InMemoryPairingStore(), null, CancellationToken.None);
            _log.Write($"paired: {paired.Kind}");
            return session;
        }

        /// <summary>D5 for the buffered stream: session SETUP (PTP), event channel, RECORD, SETPEERS, stream SETUP 103, volume, data connection.</summary>
        public async Task<StreamSetupResult> SetupAsync(BufferedReceiverSession session, double volumeDb, bool connectData = true)
        {
            var setup = await session.SetupSessionAsync(
                new SessionSetupRequest(SessionTestSupport.Sender with { Name = "airplay2-streamer buffered test" }, TimingProtocol.Ptp, 0, Guid.NewGuid().ToString().ToUpperInvariant(), null, false, true, null, Master.ClockId),
                CancellationToken.None);
            _log.Write($"PTP session SETUP: eventPort {setup.EventPort}, timingPeerInfo [{string.Join(", ", setup.TimingPeerAddresses)}], peer {session.TimingPeer}, keys [{string.Join(", ", setup.Raw.Keys)}]");
            await session.ConnectEventChannelAsync(CancellationToken.None);
            await session.RecordAsync(CancellationToken.None);
            await session.SetPeersAsync([session.TimingPeer!, session.Connection.LocalAddress], CancellationToken.None);
            var key = session.Pairing!.DefaultStreamKey;
            var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Buffered, key, Transport.ControlPort), CancellationToken.None);
            _log.Write($"stream SETUP 103: dataPort {stream.DataPort}, controlPort {stream.ControlPort}, audioBufferSize {stream.AudioBufferSize?.ToString(CultureInfo.InvariantCulture) ?? "none"}, streamID {stream.StreamId?.ToString(CultureInfo.InvariantCulture) ?? "none"}, keys [{string.Join(", ", stream.Raw.Keys)}]");
            await session.SetVolumeAsync(volumeDb, CancellationToken.None);
            if (!connectData)
            {
                return stream;
            }

            var address = session.Connection.RemoteEndPoint.Address;
            Transport.AddReceiver(session.ReceiverId, new ReceiverStreamEndpoint(new IPEndPoint(address, stream.DataPort), new IPEndPoint(address, stream.ControlPort), key, 0, false));
            return stream;
        }

        /// <summary>Anchors <paramref name="timestamp"/> at now + <paramref name="leadSeconds"/> and pushes 16-bit PCM ahead through the window.</summary>
        public async Task<PushResult> PushAsync(short[] pcm, ushort sequence, uint timestamp, double leadSeconds)
        {
            var encoder = new AlacEncoder();
            var frames = new List<byte[]>();
            for (var offset = 0; offset < pcm.Length; offset += 352 * 2)
            {
                var chunk = new short[352 * 2];
                pcm.AsSpan(offset, Math.Min(352 * 2, pcm.Length - offset)).CopyTo(chunk);
                frames.Add(encoder.Encode(chunk));
            }

            return await PushEncodedAsync(frames, 352, sequence, timestamp, leadSeconds);
        }

        /// <summary>The same for 24-bit samples as uncompressed ALAC frames.</summary>
        public async Task<PushResult> PushFramesAsync(int[] samples, BufferedAudioFormat format, ushort sequence, uint timestamp, double leadSeconds)
        {
            var frames = new List<byte[]>();
            var perPacket = format.FramesPerPacket * 2;
            for (var offset = 0; offset < samples.Length; offset += perPacket)
            {
                var chunk = new int[perPacket];
                samples.AsSpan(offset, Math.Min(perPacket, samples.Length - offset)).CopyTo(chunk);
                var frame = new byte[AlacEscapeFrame.Size24(format.FramesPerPacket, partial: false)];
                AlacEscapeFrame.WriteStereo24(chunk, format.FramesPerPacket, frame);
                frames.Add(frame);
            }

            return await PushEncodedAsync(frames, format.FramesPerPacket, sequence, timestamp, leadSeconds);
        }

        public async Task TeardownAsync(BufferedReceiverSession session)
        {
            Transport.RemoveReceiver(session.ReceiverId);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var result = await ((ReceiverSession)session.Inner).TeardownWithResultAsync(cts.Token);
                _log.Write($"TEARDOWN: session {result.SessionResponse?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or RtspStatusException)
            {
                _log.Write($"TEARDOWN failed: {ex.Message}");
            }

            // The grandmaster peer is released when the BufferedReceiverSession is disposed.
        }

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            await Control.DisposeAsync();
            await Master.DisposeAsync();
        }

        private async Task<PushResult> PushEncodedAsync(List<byte[]> frames, int framesPerPacket, ushort sequence, uint timestamp, double leadSeconds)
        {
            var anchorTime = Clock.PtpNowNanos + (ulong)(leadSeconds * 1e9);
            var anchorAt = timestamp;
            var anchoring = Transport.AnchorAsync(timestamp, anchorTime, CancellationToken.None); // goes out after the first audio
            var watch = Stopwatch.StartNew();
            foreach (var frame in frames)
            {
                await Transport.WaitForWindowAsync(BufferedAudioPacket.PacketLength(frame.Length), CancellationToken.None);
                Transport.Send(sequence++, timestamp, frame, 0);
                timestamp += (uint)framesPerPacket;
            }

            var pushed = watch.Elapsed;
            try
            {
                await anchoring;
                _log.Write($"anchored rtpTime {anchorAt} at PTP {anchorTime} (now + {leadSeconds} s): answered after {watch.Elapsed.TotalMilliseconds:0} ms");
            }
            catch (Exception ex) when (ex is RtspStatusException or IOException or TimeoutException)
            {
                _log.Write($"RESULT anchor of rtpTime {anchorAt} failed after {watch.Elapsed.TotalMilliseconds:0} ms: {ex.Message}");
                throw;
            }

            return new PushResult(frames.Count, pushed, sequence, timestamp);
        }
    }

    private sealed record PushResult(int Packets, TimeSpan Elapsed, ushort NextSequence, uint NextTimestamp);
}
