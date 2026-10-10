using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Sender.Playback;

/// <summary>Options of <see cref="PlaybackProbes"/>.</summary>
public sealed record PlaybackProbeOptions
{
    /// <summary>The live-test ceiling for the level (docs/DEVELOPMENT.md, "Live-device rules": level &lt;= 20).</summary>
    public const int MaxLevel = 20;

    /// <summary>The live-test ceiling for audio per run (docs/DEVELOPMENT.md, "Live-device rules": a 3-second quiet tone).</summary>
    public static readonly TimeSpan MaxAudio = TimeSpan.FromSeconds(3);

    /// <summary>Who we say we are in the session SETUP.</summary>
    public SenderIdentity Sender { get; init; } = new("airplay2-streamer probe", "AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF");

    /// <summary>Control-connection options (30 s per request: the srcvers-366 speaker on Wi-Fi needed up to ~10 s, SPEC.md section 4).</summary>
    public RtspConnectionOptions Connection { get; init; } = new() { RequestTimeout = TimeSpan.FromSeconds(30) };

    /// <summary>Quirks; <see cref="ReceiverQuirks.ForReceiver"/> when null.</summary>
    public ReceiverQuirks? Quirks { get; init; }

    /// <summary>The receiver level 0..<see cref="MaxLevel"/> (default 15: -25.5 dB on the wire).</summary>
    public int Level { get; init; } = 15;

    /// <summary>Tone frequency.</summary>
    public double ToneFrequency { get; init; } = 440.0;

    /// <summary>Tone amplitude in dB full scale (docs/DEVELOPMENT.md, "Live-device rules": -24 dBFS).</summary>
    public double ToneDbfs { get; init; } = -24.0;

    /// <summary>The receiver address; its first IPv4 address when null.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>The PTP grandmaster for a PTP-timed realtime stream; null runs NTP (the srcvers-366 speaker, SPEC.md P1).</summary>
    public IPtpMaster? Ptp { get; init; }
}

/// <summary>
/// The audio probes of SPEC.md section 4 owned by <c>audio-pipeline-group-sync</c>: P1 (audio confirmation), P7, P12,
/// P14, P15 (audio part), P18 and P19, as reusable methods for the CLI's <c>probe</c> command and the live tests. Each run
/// opens one session through the real <see cref="GroupPlayer"/> and <see cref="RealtimeStreamTransport"/>, sends at most
/// <see cref="PlaybackProbeOptions.MaxAudio"/> of a quiet tone at a level of at most <see cref="PlaybackProbeOptions.MaxLevel"/>
/// (enforced; the guard of docs/DEVELOPMENT.md, "Live-device rules", is the caller's <c>--i-know</c>), and always ends with the D7 TEARDOWN.
/// </summary>
/// <remarks>
/// What a probe can observe without a microphone: whether the receiver keeps sending timing requests, whether it asks for
/// withheld packets (it parses our RTP stream), the answers to FLUSH, DMAP and feedback while audio flows, its advertised
/// <c>gid</c>, and what it publishes about latency. Whether the tone was <b>audible</b> needs a person in the room (P1),
/// and the output delay of P7 needs a microphone; both are reported as such.
/// </remarks>
public sealed class PlaybackProbes
{
    private readonly DiscoveredReceiver _receiver;
    private readonly IPairingStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly PlaybackProbeOptions _options;

    /// <summary>Creates the probes for one receiver (live rules: only the live receiver, AIRPLAY_LIVE_RECEIVER).</summary>
    public PlaybackProbes(DiscoveredReceiver receiver, IPairingStore store, ILoggerFactory? loggerFactory = null, PlaybackProbeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(store);
        _receiver = receiver;
        _store = store;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PlaybackProbes>();
        _options = options ?? new PlaybackProbeOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(_options.Level, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.Level, PlaybackProbeOptions.MaxLevel, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.ToneDbfs, -12.0, nameof(options));
    }

    /// <summary>
    /// A 16-bit stereo sine at 44.1 kHz (<c>tone:&lt;s&gt;</c> of the CLI: 440 Hz at -24 dBFS by default) with 10 ms fades so
    /// the start and end do not click.
    /// </summary>
    public static short[] Tone(TimeSpan duration, double frequency = 440.0, double dbfs = -24.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks, nameof(duration));
        var frames = (int)NowPlayingTracker.ToFrames(duration);
        var amplitude = short.MaxValue * Math.Pow(10, dbfs / 20.0);
        var fade = RealtimeStreamConstants.SampleRate / 100;
        var pcm = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)fade);
            var sample = (short)Math.Round(amplitude * envelope * Math.Sin(2 * Math.PI * frequency * i / RealtimeStreamConstants.SampleRate));
            pcm[2 * i] = sample;
            pcm[(2 * i) + 1] = sample;
        }

        return pcm;
    }

    /// <summary>
    /// P1 (audio confirmation, D1) combined with P14 (A-Q6), P9's audio half, P12's <c>gid</c> check and P7's header dump:
    /// a <paramref name="duration"/> tone (at most 3 s) after the initial <c>0x90</c> sync under the transport's timing
    /// protocol, with every <paramref name="withholdEvery"/>-th packet withheld (0: none) so the receiver must ask for it;
    /// DMAP, artwork and progress sent while audio flows; the receiver's mDNS <c>gid</c> compared with our
    /// <c>groupUUID</c>; the session SETUP answer searched for latency keys.
    /// </summary>
    public Task<ProbeReport> AudioConfirmationAsync(TimeSpan duration, int withholdEvery, CancellationToken cancellationToken) =>
        RunAsync("P1", new GroupPlayerOptions(), transport => transport.WithholdEveryNthPacket = withholdEvery, async (run, ct) =>
        {
            var tone = run.Tone(duration);
            await run.Player.StartAsync(ct).ConfigureAwait(false);

            // The track starts with the next frame written (SPEC-audio.md 7): announce it, then queue it.
            var nowPlaying = run.Player.SetNowPlayingAsync(new TrackMetadata("airplay2-streamer probe tone", "Probe", "Probes", duration), SessionProbes.SampleJpeg(), RtspContentTypes.Jpeg, TimeSpan.Zero, ct);
            var writing = run.WriteAsync(tone);
            await nowPlaying.ConfigureAwait(false);
            run.Log.Add($"DMAP/artwork/progress sent while audio flows (statuses in the session log)");
            await run.ObserveGroupIdAsync(ct).ConfigureAwait(false);
            await writing.ConfigureAwait(false);
            await run.DrainAsync(ct).ConfigureAwait(false);
            return run.Summary($"tone {duration.TotalSeconds:0.0} s, withheld every {withholdEvery}");
        }, cancellationToken);

    /// <summary>P14 (A-Q6, C-7.14) alone: withhold every <paramref name="withholdEvery"/>-th packet of a tone and count 0xD5 requests and re-requests.</summary>
    public Task<ProbeReport> RetransmitAsync(TimeSpan duration, int withholdEvery, CancellationToken cancellationToken) =>
        RunAsync("P14", new GroupPlayerOptions(), transport => transport.WithholdEveryNthPacket = withholdEvery, async (run, ct) =>
        {
            await run.Player.StartAsync(ct).ConfigureAwait(false);
            await run.WriteAsync(run.Tone(duration)).ConfigureAwait(false);
            await run.DrainAsync(ct).ConfigureAwait(false);
            return run.Summary($"withheld every {withholdEvery}");
        }, cancellationToken);

    /// <summary>
    /// P7 (A-Q1, A-Q2, A-Q9): a click track (one 10 ms click per second) with sender latency <paramref name="latencyFrames"/>
    /// and sync flags <paramref name="syncFlags"/>; logs the session SETUP and RECORD latency fields. The delay itself must
    /// be measured with a microphone against a locally played click (not automated).
    /// </summary>
    public Task<ProbeReport> LatencyAsync(int latencyFrames, ushort syncFlags, TimeSpan duration, CancellationToken cancellationToken) =>
        RunAsync("P7", new GroupPlayerOptions { LatencyFrames = latencyFrames }, transport => transport.SyncFlagsOverride = syncFlags, async (run, ct) =>
        {
            await run.Player.StartAsync(ct).ConfigureAwait(false);
            run.Log.Add($"click track: L = {latencyFrames}, sync flags {syncFlags}; first click due {latencyFrames / 44100.0:0.000} s after its sync");
            await run.WriteAsync(run.Clicks(duration)).ConfigureAwait(false);
            await run.DrainAsync(ct).ConfigureAwait(false);
            return run.Summary($"L {latencyFrames}, flags {syncFlags} (delay: measure with a microphone)");
        }, cancellationToken, latencyFrames);

    /// <summary>
    /// P12 (C-7.12): one session with <c>groupUUID</c> = <paramref name="groupUuid"/> and the given booleans; reports the
    /// receiver's advertised <c>gid</c>/<c>gcgl</c> before and during the session. A second member needs a second allowed
    /// receiver (final integration).
    /// </summary>
    public Task<ProbeReport> GroupFieldsAsync(string groupUuid, bool groupContainsGroupLeader, bool isMultiSelectAirPlay, TimeSpan duration, CancellationToken cancellationToken) =>
        RunAsync("P12", new GroupPlayerOptions { GroupUuid = groupUuid, GroupContainsGroupLeader = groupContainsGroupLeader, IsMultiSelectAirPlay = isMultiSelectAirPlay }, null, async (run, ct) =>
        {
            await run.Player.StartAsync(ct).ConfigureAwait(false);
            var writing = run.WriteAsync(run.Tone(duration));
            await run.ObserveGroupIdAsync(ct).ConfigureAwait(false);
            await writing.ConfigureAwait(false);
            await run.DrainAsync(ct).ConfigureAwait(false);
            return run.Summary($"groupUUID {groupUuid}, gcgl {groupContainsGroupLeader}, multi-select {isMultiSelectAirPlay}");
        }, cancellationToken);

    /// <summary>P15 (A-Q4), audio part: a tone with <c>shk</c> = the shared secret (D4) or, with <paramref name="randomKey"/>, 32 random bytes.</summary>
    public Task<ProbeReport> StreamKeyAsync(bool randomKey, TimeSpan duration, CancellationToken cancellationToken) =>
        RunAsync("P15", new GroupPlayerOptions { RandomStreamKey = randomKey }, null, async (run, ct) =>
        {
            await run.Player.StartAsync(ct).ConfigureAwait(false);
            await run.WriteAsync(run.Tone(duration)).ConfigureAwait(false);
            await run.DrainAsync(ct).ConfigureAwait(false);
            return run.Summary(randomKey ? "random shk" : "shk = SS[0..31]");
        }, cancellationToken);

    /// <summary>
    /// P18 (A-Q10): a tone during which periodic syncs stop for <paramref name="gap"/> after the first second. Within the
    /// 3-second live budget the gap is at most about 2 s; the 3/6/10 s gaps of the probe need silence packets, which carry
    /// no sound, so the gap may run past the tone.
    /// </summary>
    public Task<ProbeReport> SyncGapAsync(TimeSpan duration, TimeSpan gap, CancellationToken cancellationToken) =>
        RunAsync("P18", new GroupPlayerOptions(), null, async (run, ct) =>
        {
            await run.Player.StartAsync(ct).ConfigureAwait(false);
            var writing = run.WriteAsync(run.Tone(duration));
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            run.Transport.PeriodicSyncsSuspended = true;
            run.Log.Add($"periodic syncs stopped for {gap.TotalSeconds:0.0} s");
            await Task.Delay(gap, ct).ConfigureAwait(false);
            run.Transport.PeriodicSyncsSuspended = false;
            run.Log.Add($"periodic syncs resumed");
            await writing.ConfigureAwait(false);
            await run.DrainAsync(ct).ConfigureAwait(false);
            return run.Summary($"syncs stopped {gap.TotalSeconds:0.0} s");
        }, cancellationToken);

    /// <summary>
    /// P19 (A-Q11): <paramref name="first"/> of tone, then <c>PauseAsync</c> (FLUSH with the next seq/rtptime, pacer stopped)
    /// for <paramref name="pause"/>, then <c>StartAsync</c> (new <c>0x90</c> sync + marker, no new RECORD) and
    /// <paramref name="second"/> of tone; then a seek-style <c>FlushAsync</c> while the stream runs. Tone total at most 3 s.
    /// </summary>
    public Task<ProbeReport> PauseResumeAsync(TimeSpan first, TimeSpan pause, TimeSpan second, CancellationToken cancellationToken) =>
        RunAsync("P19", new GroupPlayerOptions(), null, async (run, ct) =>
        {
            await run.Player.StartAsync(ct).ConfigureAwait(false);
            await run.WriteAsync(run.Tone(first)).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
            var before = run.Transport.PacketsSent;
            await run.Player.PauseAsync(ct).ConfigureAwait(false);
            run.Log.Add($"paused: FLUSH sent, {run.Transport.PacketsSent - before} packets meanwhile, member state {run.State}");
            await Task.Delay(pause, ct).ConfigureAwait(false);
            var syncs = run.Transport.SyncsSent;
            await run.Player.StartAsync(ct).ConfigureAwait(false);
            await run.WriteAsync(run.Tone(second)).ConfigureAwait(false);
            run.Log.Add($"resumed without RECORD: {run.Transport.SyncsSent - syncs} sync(s) since, member state {run.State}");
            await run.Player.FlushAsync(ct).ConfigureAwait(false);
            run.Log.Add($"seek-style FLUSH while running: member state {run.State}");
            await run.DrainAsync(ct).ConfigureAwait(false);
            return run.Summary($"pause {pause.TotalSeconds:0.0} s, resume, flush");
        }, cancellationToken);

    private async Task<ProbeReport> RunAsync(
        string probe,
        GroupPlayerOptions playerOptions,
        Action<RealtimeStreamTransport>? configure,
        Func<ProbeRun, CancellationToken, Task<string>> script,
        CancellationToken cancellationToken,
        int latencyFrames = RealtimeStreamConstants.DefaultLatencyFrames)
    {
        var log = new ProbeLogLines(probe, _logger);
        var clock = new MasterClock();
        await using var timing = new TimingResponder(clock, _loggerFactory.CreateLogger<TimingResponder>());
        await using var control = new ControlPort(_loggerFactory.CreateLogger<ControlPort>());
        var transport = new RealtimeStreamTransport(clock, timing, control, latencyFrames, _options.Ptp, _loggerFactory.CreateLogger<RealtimeStreamTransport>());
        configure?.Invoke(transport);
        var sessions = new ConcurrentQueue<ReceiverSession>();
        var options = playerOptions with
        {
            Sender = _options.Sender,
            LatencyFrames = latencyFrames,
            VolumePollInterval = TimeSpan.FromSeconds(1),
            SilenceTimeout = TimeSpan.FromSeconds(30),
        };
        var player = new GroupPlayer(transport, (receiver, ct) => OpenSessionAsync(receiver, sessions, log, ct), options, _loggerFactory.CreateLogger<GroupPlayer>());
        var run = new ProbeRun(this, player, transport, log);
        var events = CollectEventsAsync(player, log, cancellationToken);
        string summary;
        try
        {
            await player.AddReceiverAsync(_receiver, _options.Level, cancellationToken).ConfigureAwait(false);
            if (sessions.TryPeek(out var session))
            {
                var raw = session.SessionSetup?.Raw;
                var latencyKeys = raw is null ? "none" : string.Join(", ", raw.Keys.Where(k => k.Contains("atency", StringComparison.OrdinalIgnoreCase)));
                log.Add($"session SETUP answer keys [{(raw is null ? string.Empty : string.Join(", ", raw.Keys))}], latency keys [{latencyKeys}]");
            }

            log.Add($"member joined: {run.State}, {timing.RepliesSent} timing replies so far");
            summary = await script(run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or RtspStatusException or PairingException or InvalidDataException or InvalidOperationException)
        {
            summary = $"failed: {ex.GetType().Name}: {ex.Message}";
            log.Note(summary);
        }
        finally
        {
            log.Add($"totals: {transport.PacketsSent} packets sent, {transport.PacketsWithheld} withheld, {transport.SyncsSent} syncs, {timing.RepliesSent} timing replies, {control.RetransmitRequestsReceived} retransmit requests, {control.PacketsRetransmitted} re-sent, {control.PacketsMissing} not in history, {control.UnmatchedRequests} unmatched, {player.SilencePackets} silence packets, {player.LateWakeups} late wakeups, {transport.SendErrors} send errors");

            // TEARDOWN of every member (D7), also on failure or cancellation.
            await player.DisposeAsync().ConfigureAwait(false);
            await AwaitQuietly(events).ConfigureAwait(false);
        }

        return log.Report(summary);
    }

    private async Task<IReceiverSession> OpenSessionAsync(DiscoveredReceiver receiver, ConcurrentQueue<ReceiverSession> sessions, ProbeLogLines log, CancellationToken cancellationToken)
    {
        var address = _options.Address
            ?? receiver.AddressOf(AddressFamily.InterNetwork)
            ?? receiver.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"{receiver.Name} has no address.");
        var connection = await RtspConnection.ConnectAsync(new IPEndPoint(address, receiver.Port), _options.Connection, _loggerFactory.CreateLogger<RtspConnection>(), cancellationToken).ConfigureAwait(false);
        var session = new ReceiverSession(receiver, connection, new HapPairingClient(_loggerFactory.CreateLogger<HapPairingClient>()), _options.Quirks ?? ReceiverQuirks.ForReceiver(receiver), _loggerFactory.CreateLogger<ReceiverSession>());
        try
        {
            await session.GetInfoAsync(withQualifier: false, cancellationToken).ConfigureAwait(false);
            var pairing = await session.PairAsync(_store, pinPrompt: null, cancellationToken).ConfigureAwait(false);
            log.Add($"paired ({pairing.Kind}) with {receiver.Name} at {address}");
            sessions.Enqueue(session);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CollectEventsAsync(GroupPlayer player, ProbeLogLines log, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var e in player.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (e)
                {
                    case GroupPlayerEvent.MemberStateChanged s:
                        log.Add($"member {s.State}{(s.Reason is null ? string.Empty : ": " + s.Reason)}");
                        break;
                    case GroupPlayerEvent.ReceiverVolumeReported v:
                        log.Add($"receiver reports level {v.Level}");
                        break;
                    case GroupPlayerEvent.RemoteCommand c:
                        log.Add($"remote command {c.Command}");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // probe cancelled
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // logged by the probe
        }
    }

    /// <summary>One running probe: the player, the transport, the log and the audio budget.</summary>
    private sealed class ProbeRun(PlaybackProbes owner, GroupPlayer player, RealtimeStreamTransport transport, ProbeLogLines log)
    {
        private long _framesBudget = NowPlayingTracker.ToFrames(PlaybackProbeOptions.MaxAudio);

        public GroupPlayer Player { get; } = player;

        public RealtimeStreamTransport Transport { get; } = transport;

        public ProbeLogLines Log { get; } = log;

        public string State => Player.Members.Count == 0 ? "none" : string.Join("/", Player.Members.Values);

        public short[] Tone(TimeSpan duration) => PlaybackProbes.Tone(Clamp(duration), owner._options.ToneFrequency, owner._options.ToneDbfs);

        /// <summary>One 10 ms 1 kHz burst at the start of every second.</summary>
        public short[] Clicks(TimeSpan duration)
        {
            var pcm = PlaybackProbes.Tone(Clamp(duration), 1000.0, owner._options.ToneDbfs);
            var second = RealtimeStreamConstants.SampleRate;
            for (var i = 0; i < pcm.Length / 2; i++)
            {
                if (i % second >= second / 100)
                {
                    pcm[2 * i] = 0;
                    pcm[(2 * i) + 1] = 0;
                }
            }

            return pcm;
        }

        /// <summary>Writes on a producer thread (Write blocks by design, SPEC-audio.md 8.2), within the run's audio budget.</summary>
        public Task WriteAsync(short[] pcm)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Player.Write(pcm);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "probe producer",
            };
            thread.Start();
            return done.Task;
        }

        /// <summary>Waits until everything queued was sent and played (ring empty, then L plus a margin).</summary>
        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10);
            while (Player.QueuedFrames > 0 && Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds((Player.Options.LatencyFrames / 44100.0) + 0.75), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>P12: the receiver's advertised gid/gcgl (mDNS, allowed at any time) compared with our groupUUID.</summary>
        public async Task ObserveGroupIdAsync(CancellationToken cancellationToken)
        {
            try
            {
                await using var browser = new MdnsReceiverBrowser(new ReceiverBrowserOptions(), owner._loggerFactory.CreateLogger<MdnsReceiverBrowser>());
                var found = await browser.FindAsync(owner._receiver.Id, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    Log.Add($"mDNS: {owner._receiver.Name} not seen within 2 s");
                }
                else
                {
                    Log.Add($"mDNS during the session: gid {found.Airplay.GroupId ?? "none"} (our groupUUID {Player.Options.GroupUuid}), gcgl {found.Airplay.GroupContainsGroupLeader}");
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
            {
                Log.Add($"mDNS browse failed: {ex.Message}");
            }
        }

        public string Summary(string what)
        {
            var transportLine = string.Create(
                CultureInfo.InvariantCulture,
                $"{what}: {Transport.PacketsSent} packets, {Transport.PacketsWithheld} withheld, {Transport.Control.RetransmitRequestsReceived} retransmit requests, {Transport.SyncsSent} syncs, {Transport.Timing.RepliesSent} timing replies, member {State}");
            return transportLine;
        }

        private TimeSpan Clamp(TimeSpan duration)
        {
            var frames = Math.Min(NowPlayingTracker.ToFrames(duration), _framesBudget);
            _framesBudget -= frames;
            if (frames < NowPlayingTracker.ToFrames(duration))
            {
                Log.Add($"tone shortened to the 3 s live budget");
            }

            return TimeSpan.FromSeconds(frames / (double)RealtimeStreamConstants.SampleRate);
        }
    }

    /// <summary>Timestamped observations, mirrored to the logger at Information.</summary>
    private sealed class ProbeLogLines(string probe, ILogger logger)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<string> _lines = new();

        public void Add(FormattableString line) => Note(FormattableString.Invariant(line));

        public void Note(string line)
        {
            var stamped = string.Create(CultureInfo.InvariantCulture, $"{Stopwatch.GetElapsedTime(_started).TotalSeconds,7:0.000} s {line}");
            _lines.Enqueue(stamped);
            logger.LogInformation("{Probe}: {Observation}", probe, line);
        }

        public ProbeReport Report(string summary)
        {
            Note("result: " + summary);
            return new ProbeReport(probe, summary, [.. _lines]);
        }
    }
}
