using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Cli;

/// <summary>Parameters of one probe run (the CLI options of <c>probe</c>); null means the probe's default.</summary>
/// <param name="Seconds">Duration where a probe has one (P4 session, P8 idle time, P13 answer delay, P17 capture, tone length of the audio probes).</param>
/// <param name="Level">Receiver level for the audio probes (0..20, default 15).</param>
/// <param name="Latency">P7: sender latency L in frames (77175).</param>
/// <param name="SyncFlags">P7: sync packet flags (7).</param>
/// <param name="WithholdEvery">P1 audio part / P14: withhold every n-th packet (50).</param>
public sealed record ProbeParameters(double? Seconds = null, int? Level = null, int? Latency = null, ushort? SyncFlags = null, int? WithholdEvery = null);

/// <summary>What a probe id does (SPEC.md section 4).</summary>
public enum ProbeKind
{
    /// <summary>Control plane only; never sends audio.</summary>
    ControlOnly,

    /// <summary>A control part that always runs and an audio part that needs <c>--i-know</c> (P1, P15).</summary>
    ControlThenAudio,

    /// <summary>Sends audio; refused without <c>--i-know</c> (P7, P12, P14, P18, P19).</summary>
    Audio,

    /// <summary>Not available in this build: owned by a later package or needs a person with a microphone (P16, P20).</summary>
    Unavailable,
}

/// <summary>
/// <c>airplay2 probe &lt;P1..P21&gt; &lt;name&gt;</c> (SPEC.md section 4): dispatches to the probe methods of the owning
/// packages (<see cref="SessionProbes"/>, <see cref="PlaybackProbes"/>) and implements the pairing probes P2, P6, P11 and
/// the PTP capture P17 here. Every run appends its complete log (the decrypted wire exchange at Debug) to
/// <c>artifacts/live/&lt;date&gt;-&lt;id&gt;.log</c> and prints a one-line result for the SPEC.md probe table.
/// </summary>
public sealed partial class ProbeCommand(CliServices services)
{
    /// <summary>Probe ids that send audio (refused without <c>--i-know</c>).</summary>
    public static readonly IReadOnlySet<string> AudioProbes = new HashSet<string>(StringComparer.Ordinal) { "P7", "P12", "P14", "P18", "P19" };

    /// <summary>Probe ids with a control part and an audio part (the audio part needs <c>--i-know</c>).</summary>
    public static readonly IReadOnlySet<string> MixedProbes = new HashSet<string>(StringComparer.Ordinal) { "P1", "P15" };

    /// <summary>P6: pause between the two M1s so the first, abandoned SRP session is gone.</summary>
    public static readonly TimeSpan MethodByteGap = TimeSpan.FromSeconds(5);

    private CliServices Services { get; } = services;

    /// <summary>Normalises <c>p1</c>/<c>P01</c> to <c>P1</c>; null for anything outside P1..P21.</summary>
    public static string? NormalizeId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var match = ProbeIdPattern().Match(id.Trim());
        if (!match.Success)
        {
            return null;
        }

        var n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return n is >= 1 and <= 21 ? "P" + n.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The kind of a normalised probe id.</summary>
    public static ProbeKind KindOf(string id) => id switch
    {
        _ when AudioProbes.Contains(id) => ProbeKind.Audio,
        _ when MixedProbes.Contains(id) => ProbeKind.ControlThenAudio,
        "P16" or "P20" => ProbeKind.Unavailable,
        _ => ProbeKind.ControlOnly,
    };

    /// <summary>Runs probe <paramref name="id"/> against <paramref name="name"/>.</summary>
    public async Task<int> RunAsync(string id, string name, bool iKnow, ProbeParameters parameters, CliLogOptions logOptions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(logOptions);
        var probe = NormalizeId(id);
        if (probe is null)
        {
            await Services.Error.WriteLineAsync($"Unknown probe id '{id}': use P1 .. P21 (SPEC.md section 4).").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var kind = KindOf(probe);
        if (kind == ProbeKind.Audio && !iKnow)
        {
            // Before any file, store or socket (WORK-PLAN.md section 4).
            await Services.Error.WriteLineAsync($"Refusing {probe} without --i-know: it sends audio (live rule: only the Test Speaker ABCD, at most 3 s of a quiet tone, level <= 20, user's go-ahead). Nothing was sent.").ConfigureAwait(false);
            return ExitCodes.AudioRefused;
        }

        if (kind == ProbeKind.Unavailable)
        {
            await Services.Error.WriteLineAsync(probe == "P16"
                ? "P16 (buffered stream format codes) belongs to the buffered-stream package and is not available from this CLI."
                : "P20 (mixed group latency) needs the HomePod owner's permission and a microphone measurement; it is part of final integration, not an automated probe.").ConfigureAwait(false);
            return ExitCodes.Unavailable;
        }

        if (parameters.Level is { } level && level is < 0 or > PlaybackProbeOptions.MaxLevel)
        {
            await Services.Error.WriteLineAsync($"--level must be 0..{PlaybackProbeOptions.MaxLevel} for probes (WORK-PLAN.md section 4).").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        // The receiver is resolved before the log file exists (console logging only), so a mistyped name leaves no
        // near-empty log in artifacts/live and touches no pairing store.
        var commands = new CliCommands(Services);
        DiscoveredReceiver? receiver;
        using (var lookupLogging = Services.CreateLogging(logOptions with { LogFile = null }))
        {
            await using var receivers = Services.CreateDirectory(lookupLogging);
            receiver = await commands.ResolveAsync(receivers, name, ct).ConfigureAwait(false);
        }

        if (receiver is null)
        {
            return ExitCodes.Failure;
        }

        var directory = Services.LiveLogDirectory ?? CliLogging.LiveLogDirectory();
        var logFile = logOptions.LogFile ?? Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}-{probe}.log");
        using var logging = Services.CreateLogging(logOptions with { LogFile = logFile });
        var logger = logging.CreateLogger("airplay2.probe");
        await Services.Out.WriteLineAsync($"{probe}: logging the wire exchange to {logFile}").ConfigureAwait(false);
        logger.LogInformation("{Probe} against {Receiver} [{Id}] at {Addresses}", probe, receiver.Name, receiver.Id, string.Join(", ", receiver.Addresses));

        var store = Services.CreatePairingStore(logging);
        var controller = await store.GetControllerIdentityAsync(ct).ConfigureAwait(false);
        var identity = AirplaySender.DeriveIdentity("airplay2-streamer probe", controller);

        // The sender supplies the quirk overlay and connection options of AirplaySenderOptions, and the PTP grandmaster.
        await using var sender = new AirplaySender(Services.SenderOptions with { Identity = identity }, store, logging);
        var reports = new List<ProbeReport>();
        var failed = false;
        try
        {
            await RunProbeAsync(probe, receiver, sender, iKnow, parameters, logging, reports, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Services.Error.WriteLineAsync($"{probe} interrupted.").ConfigureAwait(false);
            return ExitCodes.Interrupted;
        }
        catch (Exception ex) when (CliCommands.IsReceiverError(ex) || ex is PairingException or InvalidOperationException)
        {
            logger.LogError("{Probe} failed: {Error}", probe, ex.Message);
            reports.Add(new ProbeReport(probe, $"failed: {ex.GetType().Name}: {ex.Message}", []));
            failed = true;
        }

        foreach (var report in reports)
        {
            await Services.Out.WriteLineAsync($"RESULT {report.Probe}: {report.Summary}").ConfigureAwait(false);
            logger.LogInformation("RESULT {Probe}: {Summary}", report.Probe, report.Summary);
            failed |= PlaybackProbeFailed(report);
        }

        return failed || reports.Count == 0 ? ExitCodes.Failure : ExitCodes.Ok;
    }

    /// <summary>
    /// <see cref="PlaybackProbes"/> report a failure only in the summary (it catches the error so its TEARDOWN log stays in
    /// the report, and writes <c>failed: &lt;type&gt;: &lt;message&gt;</c>); <see cref="SessionProbes"/> and the probes
    /// here throw instead, which <see cref="RunAsync"/> records explicitly.
    /// </summary>
    private static bool PlaybackProbeFailed(ProbeReport report) => report.Summary.StartsWith("failed:", StringComparison.Ordinal);

    private static TimeSpan Seconds(ProbeParameters p, double fallback) => TimeSpan.FromSeconds(p.Seconds ?? fallback);

    private static TimeSpan AudioSeconds(ProbeParameters p, double fallback) =>
        TimeSpan.FromSeconds(Math.Min(p.Seconds ?? fallback, PlaybackProbeOptions.MaxAudio.TotalSeconds));

    private static IPAddress AddressOf(DiscoveredReceiver receiver) =>
        receiver.AddressOf(AddressFamily.InterNetwork) ?? receiver.Addresses[0];

    [GeneratedRegex("^[Pp]0*([0-9]{1,2})$")]
    private static partial Regex ProbeIdPattern();

    private async Task RunProbeAsync(string probe, DiscoveredReceiver receiver, AirplaySender sender, bool iKnow, ProbeParameters p, ILoggerFactory logging, List<ProbeReport> reports, CancellationToken ct)
    {
        var store = sender.PairingStore;
        var identity = sender.Options.Identity;

        // The receiver's quirks and timing protocol as play/volume see them (AirplaySenderOptions.Quirks over the model defaults, D1).
        var quirks = sender.QuirksFor(receiver);
        var timing = sender.TimingProtocolFor(receiver);
        var connection = Services.SenderOptions.Connection;
        var session = new SessionProbes(receiver, store, logging, new SessionProbeOptions { Sender = identity, Quirks = quirks, Connection = connection });

        // A PTP-timed receiver (shairport-sync, P1 quirk) gets its audio under the sender's grandmaster (0xD7 syncs,
        // SETPEERS through the group player); only when UDP 319/320 cannot be bound do the audio probes not run.
        var kind = KindOf(probe);
        IPtpMaster? ptp = null;
        string? noAudio = null;
        if (timing == TimingProtocol.Ptp && (kind == ProbeKind.Audio || (kind == ProbeKind.ControlThenAudio && iKnow)))
        {
            try
            {
                ptp = await sender.EnsurePtpMasterAsync(ct).ConfigureAwait(false);
            }
            catch (PtpMasterException ex)
            {
                noAudio = $"{receiver.Name} is timed by PTP and our grandmaster cannot bind its ports ({ex.Message})";
            }
        }

        if (kind == ProbeKind.Audio && noAudio is not null)
        {
            throw new InvalidOperationException($"{noAudio}; {probe} sends no audio without it.");
        }

        var runAudioPart = iKnow && noAudio is null;
        if (iKnow && noAudio is not null)
        {
            await Services.Out.WriteLineAsync($"{probe} audio part skipped: {noAudio}.").ConfigureAwait(false);
        }

        PlaybackProbes Playback() => new(receiver, store, logging, new PlaybackProbeOptions { Sender = identity, Level = p.Level ?? 15, Quirks = quirks, Connection = connection, Ptp = ptp });

        switch (probe)
        {
            case "P1":
                var control = await session.TimingProtocolAsync(ct).ConfigureAwait(false);
                reports.Add(control with { Summary = $"control part: {control.Summary}" });
                if (runAudioPart)
                {
                    reports.Add(await Playback().AudioConfirmationAsync(AudioSeconds(p, 3), p.WithholdEvery ?? 50, ct).ConfigureAwait(false));
                }
                else if (!iKnow)
                {
                    await Services.Out.WriteLineAsync("P1 audio part skipped: it needs --i-know (3 s quiet tone).").ConfigureAwait(false);
                }

                break;
            case "P2":
                reports.Add(await TransientPairingAsync(receiver, logging, ct).ConfigureAwait(false));
                break;
            case "P3":
                reports.Add(await session.StartupOrderAsync(timing, ct).ConfigureAwait(false));
                break;
            case "P4":
                reports.Add(await session.DeviceVolumeAsync(Seconds(p, 10), ct).ConfigureAwait(false));
                break;
            case "P5":
                reports.Add(await session.StreamIdAndTeardownAsync(ct).ConfigureAwait(false));
                break;
            case "P6":
                reports.Add(await MethodByteAsync(receiver, logging, ct).ConfigureAwait(false));
                break;
            case "P7":
                reports.Add(await Playback().LatencyAsync(p.Latency ?? RealtimeStreamConstants.DefaultLatencyFrames, p.SyncFlags ?? 7, AudioSeconds(p, 3), ct).ConfigureAwait(false));
                break;
            case "P8":
                reports.Add(await session.FeedbackAsync(Seconds(p, 0), ct).ConfigureAwait(false));
                break;
            case "P9":
                reports.Add(await session.MetadataAsync(new TrackMetadata("airplay2-streamer probe", "Probe Artist", "Probe Album", TimeSpan.FromMinutes(3)), SessionProbes.SampleJpeg(), ct).ConfigureAwait(false));
                break;
            case "P10":
                reports.Add(await session.InitialVolumeAsync(ct).ConfigureAwait(false));
                break;
            case "P11":
                reports.Add(await HkpHeaderAsync(receiver, logging, ct).ConfigureAwait(false));
                break;
            case "P12":
                reports.Add(await Playback().GroupFieldsAsync(Guid.NewGuid().ToString().ToUpperInvariant(), false, true, AudioSeconds(p, 2), ct).ConfigureAwait(false));
                break;
            case "P13":
                reports.Add(await session.EventTimingAsync(Seconds(p, 5), ct).ConfigureAwait(false));
                break;
            case "P14":
                reports.Add(await Playback().RetransmitAsync(AudioSeconds(p, 3), p.WithholdEvery ?? 50, ct).ConfigureAwait(false));
                break;
            case "P15":
                reports.Add(await session.StreamKeyAsync(ct).ConfigureAwait(false));
                if (runAudioPart)
                {
                    reports.Add(await Playback().StreamKeyAsync(randomKey: true, AudioSeconds(p, 2), ct).ConfigureAwait(false));
                }
                else if (!iKnow)
                {
                    await Services.Out.WriteLineAsync("P15 audio part skipped: it needs --i-know (2 s quiet tone with a random shk).").ConfigureAwait(false);
                }

                break;
            case "P17":
                reports.Add(await PtpCaptureAsync(receiver, sender, Seconds(p, 20), logging, ct).ConfigureAwait(false));
                break;
            case "P18":
                var tone = AudioSeconds(p, 3);
                var gap = TimeSpan.FromSeconds(Math.Max(0.5, tone.TotalSeconds - 1));
                reports.Add(await Playback().SyncGapAsync(tone, gap, ct).ConfigureAwait(false));
                break;
            case "P19":
                reports.Add(await Playback().PauseResumeAsync(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.2), ct).ConfigureAwait(false));
                break;
            case "P21":
                reports.Add(await session.LargeFrameAsync(2048, ct).ConfigureAwait(false));
                break;
            default:
                throw new InvalidOperationException($"{probe} has no implementation.");
        }

        foreach (var report in reports)
        {
            foreach (var line in report.Observations)
            {
                await Services.Out.WriteLineAsync("  " + line).ConfigureAwait(false);
            }
        }
    }

    /// <summary>P2 (C-7.2): transient M1-M4 (<c>/pair-pin-start</c> HKP 4, Flags 0x10, PIN 3939), then an encrypted <c>GET /info</c>; nothing stored.</summary>
    private async Task<ProbeReport> TransientPairingAsync(DiscoveredReceiver receiver, ILoggerFactory logging, CancellationToken ct)
    {
        var log = new Observations("P2", logging.CreateLogger("airplay2.probe"));
        await using var connection = await ConnectAsync(receiver, logging, log, ct).ConfigureAwait(false);
        var client = new HapPairingClient(logging.CreateLogger<HapPairingClient>());
        try
        {
            var session = await client.PairTransientAsync(connection, ct).ConfigureAwait(false);
            connection.EnableEncryption(session.ControlKeys);
            var info = await connection.SendExpectOkAsync(AirplaySender.InfoRequest(withQualifier: false), ct).ConfigureAwait(false);
            var plist = info.PlistBody();
            log.Add($"transient M1-M4 accepted; encrypted GET /info {info.StatusCode}, {info.Body.Length} bytes, keys [{string.Join(", ", plist.Keys)}]");
            return log.Report("transient pairing accepted (flags bit 10 set); encrypted GET /info 200");
        }
        catch (PairingException ex)
        {
            log.Add($"transient pairing refused: {ex.Failure}, status {ex.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}, TLV {ex.TlvError?.ToString() ?? "-"}: {ex.Message}");
            return log.Report($"transient pairing refused ({ex.Failure}): the PIN flow is the default for this receiver");
        }
    }

    /// <summary>P6 (C-7.6): M1 with method 0 (<c>PairSetup</c>) and 1 (<c>PairSetupWithAuth</c>) on two connections, no PIN entry; compares the M2s.</summary>
    private async Task<ProbeReport> MethodByteAsync(DiscoveredReceiver receiver, ILoggerFactory logging, CancellationToken ct)
    {
        var log = new Observations("P6", logging.CreateLogger("airplay2.probe"));
        var results = new List<string>();
        var first = true;
        foreach (var method in new[] { TlvMethod.PairSetup, TlvMethod.PairSetupWithAuth })
        {
            if (!first)
            {
                // An accessory runs one pair-setup at a time and answers a second M1 with BackOff while the first SRP
                // session is still open (HAP: kTLVError_Busy/BackOff); give it time to drop the abandoned one.
                await Task.Delay(MethodByteGap, ct).ConfigureAwait(false);
            }

            first = false;
            await using var connection = await ConnectAsync(receiver, logging, log, ct).ConfigureAwait(false);
            var m1 = new Tlv8Document().AddMethod(method).AddState(1).Encode();
            var headers = new Dictionary<string, string> { [RtspHeaders.Connection] = "keep-alive", [RtspHeaders.XAppleHkp] = HapPairingEndpoints.HkpRegular };
            var response = await connection.SendAsync(RtspRequest.WithBody(HapPairingEndpoints.Method, HapPairingEndpoints.PairSetup, RtspContentTypes.OctetStream, m1, headers), ct).ConfigureAwait(false);
            var described = DescribeTlv(response);
            log.Add($"M1 method {(byte)method} ({method}) -> {response.StatusCode} {response.ReasonPhrase}, {described}");
            results.Add($"method {(byte)method}: {response.StatusCode} {described}");
        }

        return log.Report(string.Join("; ", results));
    }

    /// <summary>P11 (C-7.11): <c>/pair-pin-start</c> and transient M1 without <c>X-Apple-HKP</c> on one connection; does M2 still arrive?</summary>
    private async Task<ProbeReport> HkpHeaderAsync(DiscoveredReceiver receiver, ILoggerFactory logging, CancellationToken ct)
    {
        var log = new Observations("P11", logging.CreateLogger("airplay2.probe"));
        await using var connection = await ConnectAsync(receiver, logging, log, ct).ConfigureAwait(false);
        var noHkp = new Dictionary<string, string> { [RtspHeaders.Connection] = "keep-alive" };
        var start = await connection.SendAsync(RtspRequest.WithEmptyBody(HapPairingEndpoints.Method, HapPairingEndpoints.PinStart, RtspContentTypes.OctetStream, noHkp), ct).ConfigureAwait(false);
        log.Add($"POST /pair-pin-start without X-Apple-HKP -> {start.StatusCode} {start.ReasonPhrase}");
        var m1 = new Tlv8Document().AddMethod(TlvMethod.PairSetup).AddState(1).Add(TlvType.Flags, (byte)TlvFlags.Transient).Encode();
        var response = await connection.SendAsync(RtspRequest.WithBody(HapPairingEndpoints.Method, HapPairingEndpoints.PairSetup, RtspContentTypes.OctetStream, m1, noHkp), ct).ConfigureAwait(false);
        var described = DescribeTlv(response);
        log.Add($"transient M1 without X-Apple-HKP -> {response.StatusCode} {response.ReasonPhrase}, {described}");
        return log.Report($"M1 without X-Apple-HKP: {response.StatusCode} {described}");
    }

    /// <summary>
    /// P17 (A-Q8) during a PTP control session (no audio): our grandmaster runs, the receiver becomes its peer through
    /// SETPEERS, and for <paramref name="duration"/> the messages its PTP slave sends are counted (feedback every 2 s).
    /// </summary>
    /// <remarks>
    /// The grandmaster is a dedicated one on the sender's clock rather than <see cref="AirplaySender.EnsurePtpMasterAsync"/>:
    /// libairptp drops silent peers after 15 s, and the capture must keep the receiver for the whole run whatever
    /// <see cref="AirplaySenderOptions.Ptp"/> says (SPEC-audio.md 4.4 note). P17 sends no audio, so the facade's master is
    /// never started in the same run and the ports are free.
    /// </remarks>
    private static async Task<ProbeReport> PtpCaptureAsync(DiscoveredReceiver receiver, AirplaySender sender, TimeSpan duration, ILoggerFactory logging, CancellationToken ct)
    {
        var log = new Observations("P17", logging.CreateLogger("airplay2.probe"));
        var identity = sender.Options.Identity;
        await sender.StartAsync(ct).ConfigureAwait(false);
        await using var master = new PtpMaster(sender.Clock, logging.CreateLogger<PtpMaster>(), sender.Options.Ptp with { PeerTimeout = Timeout.InfiniteTimeSpan });
        await master.StartAsync(ct).ConfigureAwait(false);
        log.Add($"grandmaster {master.ClockId:X16} on UDP {master.EventPort}/{master.GeneralPort}");
        var session = await sender.ConnectAsync(receiver, pinPrompt: null, ct).ConfigureAwait(false);
        IPAddress? peer = null;
        try
        {
            var setup = await session.SetupSessionAsync(
                new SessionSetupRequest(identity, TimingProtocol.Ptp, 0, Guid.NewGuid().ToString().ToUpperInvariant(), null, false, true, null, master.ClockId),
                ct).ConfigureAwait(false);
            log.Add($"PTP session SETUP: eventPort {setup.EventPort}, timingPeerInfo [{string.Join(", ", setup.TimingPeerAddresses)}]");
            await session.ConnectEventChannelAsync(ct).ConfigureAwait(false);
            await session.RecordAsync(ct).ConfigureAwait(false);
            peer = await PtpSessionWiring.AttachAsync(session, master, setup, null, ct).ConfigureAwait(false);
            log.Add($"SETPEERS sent; PTP peer {peer}");
            var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, session.Pairing!.DefaultStreamKey, sender.Control.Port), ct).ConfigureAwait(false);
            log.Add($"stream SETUP: data {stream.DataPort}, control {stream.ControlPort} (no audio is sent)");
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < duration)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var feedback = await session.FeedbackAsync(ct).ConfigureAwait(false);
                var status = master.Peers.FirstOrDefault();
                log.Add($"t={watch.Elapsed.TotalSeconds:0.0}s feedback {(feedback is null ? "empty" : "ok")}; peer received {status?.MessagesReceived ?? 0}, last seen {status?.SinceLastSeen.TotalSeconds ?? -1:0.0}s ago");
            }
        }
        finally
        {
            await CliCommands.TeardownQuietlyAsync(session, logging.CreateLogger("airplay2.probe")).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            if (peer is not null)
            {
                master.RemovePeer(peer);
            }
        }

        log.Note("grandmaster " + master.GetStatistics());
        foreach (var status in master.Peers)
        {
            log.Note($"peer {status.Address}: {status.MessagesReceived} messages, identity {status.PortIdentity?.ToString() ?? "none"}");
        }

        while (master.Events.TryRead(out var e))
        {
            log.Note("event " + e);
        }

        var received = master.Peers.Sum(x => x.MessagesReceived);
        return log.Report(string.Create(CultureInfo.InvariantCulture, $"{received} PTP messages from the receiver in {duration.TotalSeconds:0} s; {master.GetStatistics()}"));
    }

    private async Task<RtspConnection> ConnectAsync(DiscoveredReceiver receiver, ILoggerFactory logging, Observations log, CancellationToken ct)
    {
        var endpoint = new IPEndPoint(AddressOf(receiver), receiver.Port);
        var connection = await RtspConnection.ConnectAsync(endpoint, Services.SenderOptions.Connection, logging.CreateLogger<RtspConnection>(), ct).ConfigureAwait(false);
        log.Add($"connected to {endpoint} from {connection.LocalAddress}");
        return connection;
    }

    private static string DescribeTlv(RtspResponse response)
    {
        try
        {
            var doc = Tlv8Document.Decode(response.Body.Span);
            return string.Join(" ", doc.Items.Select(i => i.Value.Length is > 0 and <= 4 ? $"{i.Type}=0x{Convert.ToHexString(i.Value)}" : $"{i.Type}({i.Value.Length})")) + (doc.Error is { } e ? $" Error={e}" : string.Empty);
        }
        catch (FormatException)
        {
            return $"{response.Body.Length} bytes, not TLV8";
        }
    }

    /// <summary>Timestamped observations of the probes implemented here, mirrored to the log.</summary>
    private sealed class Observations(string probe, ILogger logger)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<string> _lines = new();

        public void Add(FormattableString line) => Note(FormattableString.Invariant(line));

        public void Note(string line)
        {
            _lines.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{Stopwatch.GetElapsedTime(_started).TotalSeconds,7:0.000} s {line}"));
            logger.LogInformation("{Probe}: {Observation}", probe, line);
        }

        public ProbeReport Report(string summary)
        {
            Note("result: " + summary);
            return new ProbeReport(probe, summary, [.. _lines]);
        }
    }
}
