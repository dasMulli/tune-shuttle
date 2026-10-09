using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Sender.Session;

/// <summary>Options of <see cref="SessionProbes"/>.</summary>
public sealed record SessionProbeOptions
{
    /// <summary>Who we say we are in the session SETUP.</summary>
    public SenderIdentity Sender { get; init; } = new("airplay2-streamer probe", "AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF");

    /// <summary>
    /// Control-connection options: 25 s connect, and a 30 s request timeout instead of D6's 10 s, because the SpeakerA MOTIV
    /// HOME on Wi-Fi answered pair-setup M2/M4 and RECORD after up to ~10 s and once not within 10 s (probe runs
    /// 2026-10-09, SPEC-control.md 2.1 note).
    /// </summary>
    public RtspConnectionOptions Connection { get; init; } = new() { RequestTimeout = TimeSpan.FromSeconds(30) };

    /// <summary>Quirks; <see cref="ReceiverQuirks.ForReceiver"/> when null.</summary>
    public ReceiverQuirks? Quirks { get; init; }

    /// <summary>How long P1 waits after the stream SETUP for 0xD2 timing requests (WORK-PLAN.md section 4: 2 s).</summary>
    public TimeSpan TimingWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The volume the probes set (dB). No audio is sent by any probe here; -24 dB is level 20, the quiet test level.</summary>
    public double VolumeDb { get; init; } = -24.0;

    /// <summary>Upper bound for the TEARDOWN sequence that ends every probe session.</summary>
    public TimeSpan TeardownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The receiver address to use; the first IPv4 address of the receiver when null.</summary>
    public IPAddress? Address { get; init; }
}

/// <summary>The outcome of one probe: a one-line summary and the timestamped observations (also logged at Information).</summary>
/// <param name="Probe">The probe id (<c>P1</c> ... <c>P21</c>, SPEC.md section 4).</param>
/// <param name="Summary">One line for the probe table in SPEC.md.</param>
/// <param name="Observations">What was sent and seen, in order, with the time since the probe started.</param>
public record ProbeReport(string Probe, string Summary, IReadOnlyList<string> Observations);

/// <summary>Probe P1 (control part, decisive for SPEC.md D1): does the receiver accept an NTP session and send timing requests?</summary>
/// <param name="Summary">One line for SPEC.md.</param>
/// <param name="Observations">Timestamped observations.</param>
/// <param name="NtpSetupStatus">Status of the NTP session SETUP (200; shairport-sync 5.5.2 also answers 200, with an empty body and no <c>eventPort</c>, SPEC-control.md 4.2 note).</param>
/// <param name="NtpEventPort">The <c>eventPort</c> of the NTP SETUP answer, 0 when refused.</param>
/// <param name="TimingRepliesSent">0xD2 requests answered by our timing responder within the wait after the stream SETUP.</param>
/// <param name="FirstTimingReplyAfter">Time from the stream SETUP answer to the first answered timing request.</param>
/// <param name="PtpSetupStatus">Status of the PTP session SETUP with a minimal <c>timingPeerInfo</c>.</param>
/// <param name="PtpTimingPeerAddresses">The receiver's <c>timingPeerInfo.Addresses</c> in the PTP answer.</param>
/// <param name="Decision">NTP when the NTP SETUP was accepted with an <c>eventPort</c> and timing requests arrived; PTP otherwise.</param>
public sealed record TimingProtocolProbeReport(
    string Summary,
    IReadOnlyList<string> Observations,
    int NtpSetupStatus,
    int NtpEventPort,
    long TimingRepliesSent,
    TimeSpan? FirstTimingReplyAfter,
    int PtpSetupStatus,
    IReadOnlyList<IPAddress> PtpTimingPeerAddresses,
    TimingProtocol Decision) : ProbeReport("P1", Summary, Observations);

/// <summary>
/// The control-plane probes of SPEC.md section 4 owned by <c>session-streams</c> (WORK-PLAN.md), as reusable methods the
/// CLI's <c>probe</c> command and the live tests call: P1 (control part), P3, P4, P5, P8, P9, P10, P13, P15 (control part)
/// and P21. None of them sends audio; every session they open is ended with the D7 TEARDOWN and closed, also on failure.
/// </summary>
public sealed class SessionProbes
{
    private static readonly string[] FeedbackBodies = ["none", "{}", "{streams: []}"];

    private readonly DiscoveredReceiver _receiver;
    private readonly IPairingStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SessionProbeOptions _options;
    private readonly IMasterClock _clock = new MasterClock();

    /// <summary>Creates the probes for one receiver.</summary>
    /// <param name="receiver">The receiver (live rules: only the Test Speaker ABCD).</param>
    /// <param name="store">Pairing store: stored credentials are verified; otherwise transient pairing is used.</param>
    /// <param name="loggerFactory">Loggers for the connection, pairing and session (the wire log is at Debug).</param>
    /// <param name="options">Options.</param>
    public SessionProbes(DiscoveredReceiver receiver, IPairingStore store, ILoggerFactory? loggerFactory = null, SessionProbeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(store);
        _receiver = receiver;
        _store = store;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<SessionProbes>();
        _options = options ?? new SessionProbeOptions();
    }

    /// <summary>
    /// P1, control part (SPEC.md D1 gate, WORK-PLAN.md section 4): after pairing, a session SETUP with
    /// <c>timingProtocol: NTP</c> and our timing port; on 200 + <c>eventPort</c>: event channel, RECORD, stream SETUP, then
    /// count the 0xD2 timing requests our responder answers within <see cref="SessionProbeOptions.TimingWait"/>; TEARDOWN.
    /// Then, on a new connection, a session SETUP with <c>timingProtocol: PTP</c> and a minimal <c>timingPeerInfo</c>; the
    /// answer is recorded; TEARDOWN. No audio.
    /// </summary>
    public async Task<TimingProtocolProbeReport> TimingProtocolAsync(CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P1", _logger);
        var ntpStatus = 0;
        var ntpEventPort = 0;
        long replies = 0;
        TimeSpan? firstReply = null;

        await using (var timing = new TimingResponder(_clock, _loggerFactory.CreateLogger<TimingResponder>()))
        using (var control = BindUdp())
        {
            await timing.StartAsync(cancellationToken).ConfigureAwait(false);
            log.Add($"timing responder on UDP {timing.Port}, control port UDP {LocalPort(control)}");
            await using var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    var setup = await session.SetupSessionAsync(SessionRequest(TimingProtocol.Ntp, timing.Port), cancellationToken).ConfigureAwait(false);
                    ntpStatus = RtspStatus.Ok;
                    ntpEventPort = setup.EventPort;
                    log.Add($"NTP session SETUP: 200, eventPort {setup.EventPort}, timingPort {setup.TimingPort}");
                }
                catch (RtspStatusException ex)
                {
                    ntpStatus = ex.StatusCode;
                    log.Add($"NTP session SETUP refused: {ex.StatusCode} {ex.Response.ReasonPhrase}");
                }
                catch (InvalidDataException ex)
                {
                    // 2xx without eventPort: shairport-sync's actual refusal of NTP.
                    ntpStatus = RtspStatus.Ok;
                    log.Note("NTP session SETUP answered without eventPort: " + ex.Message);
                }

                if (ntpStatus == RtspStatus.Ok)
                {
                    var channel = await session.ConnectEventChannelAsync(cancellationToken).ConfigureAwait(false);
                    log.Add($"event channel connected to port {channel.RemoteEndPoint.Port}");
                    var latency = await session.RecordAsync(cancellationToken).ConfigureAwait(false);
                    log.Add($"RECORD: 200, Audio-Latency {latency}");
                    var before = timing.RepliesSent;
                    var stream = await session.SetupStreamAsync(StreamRequest(session, LocalPort(control)), cancellationToken).ConfigureAwait(false);
                    var streamAnswered = Stopwatch.GetTimestamp();
                    log.Add($"stream SETUP: 200, dataPort {stream.DataPort}, controlPort {stream.ControlPort}, streamID {Show(stream.StreamId)}; timing replies so far {before}");
                    var deadline = streamAnswered + (long)(_options.TimingWait.TotalSeconds * Stopwatch.Frequency);
                    while (Stopwatch.GetTimestamp() < deadline)
                    {
                        if (firstReply is null && timing.RepliesSent > 0)
                        {
                            firstReply = Stopwatch.GetElapsedTime(streamAnswered);
                        }

                        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    }

                    replies = timing.RepliesSent;
                    log.Add($"timing requests answered within {_options.TimingWait.TotalSeconds:0.#} s of the stream SETUP: {replies} (first after {(firstReply is { } f ? $"{f.TotalMilliseconds:0} ms" : "-")}); ignored datagrams {timing.PacketsIgnored}");
                    log.Add($"event channel: {channel.RequestsAnswered} request(s) answered, largest frame {channel.LargestFrameReceived} B");
                }
            }
            finally
            {
                await EndAsync(session, log).ConfigureAwait(false);
            }
        }

        // PTP: record the answer only (no PTP master runs here).
        var ptpStatus = 0;
        IReadOnlyList<IPAddress> ptpPeers = [];
        await using (var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var clockId = RandomClockId();
                var setup = await session.SetupSessionAsync(SessionRequest(TimingProtocol.Ptp, 0, clockId), cancellationToken).ConfigureAwait(false);
                ptpStatus = RtspStatus.Ok;
                ptpPeers = setup.TimingPeerAddresses;
                log.Add($"PTP session SETUP (ClockID {clockId:X16}): 200, eventPort {setup.EventPort}, timingPeerInfo addresses [{string.Join(", ", ptpPeers)}], keys [{string.Join(", ", setup.Raw.Keys)}]");
            }
            catch (RtspStatusException ex)
            {
                ptpStatus = ex.StatusCode;
                log.Add($"PTP session SETUP refused: {ex.StatusCode} {ex.Response.ReasonPhrase}");
            }
            catch (InvalidDataException ex)
            {
                ptpStatus = RtspStatus.Ok;
                log.Note("PTP session SETUP answered without eventPort: " + ex.Message);
            }
            finally
            {
                await EndAsync(session, log).ConfigureAwait(false);
            }
        }

        var decision = ntpStatus == RtspStatus.Ok && ntpEventPort > 0 && replies > 0 ? TimingProtocol.Ntp : TimingProtocol.Ptp;
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"NTP session SETUP {ntpStatus}{(ntpEventPort > 0 ? $" with eventPort {ntpEventPort}" : string.Empty)}, {replies} timing request(s) answered within {_options.TimingWait.TotalSeconds:0.#} s of the stream SETUP{(firstReply is { } fr ? $" (first after {fr.TotalMilliseconds:0} ms)" : string.Empty)}; PTP session SETUP {ptpStatus}; decision: {decision}");
        log.Note(summary);
        return new TimingProtocolProbeReport(summary, log.Lines, ntpStatus, ntpEventPort, replies, firstReply, ptpStatus, ptpPeers, decision);
    }

    /// <summary>
    /// P3 (C-7.3): both start-up orders on two connections, no audio. Order A (D5): session SETUP, event channel, RECORD,
    /// stream SETUP, volume, GET_PARAMETER. Order B (pyatv): session SETUP, event channel, volume, GET_PARAMETER, stream
    /// SETUP, RECORD, GET_PARAMETER. Records statuses, <c>Audio-Latency</c> and the read-back volumes.
    /// </summary>
    public async Task<ProbeReport> StartupOrderAsync(TimingProtocol timingProtocol, CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P3", _logger);
        var results = new List<string>();
        foreach (var order in new[] { "A (D5: RECORD before stream SETUP, volume last)", "B (pyatv: volume before stream SETUP, RECORD last)" })
        {
            log.Add($"order {order}");
            await using var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false);
            await using var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false);
            var steps = new List<string>();
            try
            {
                await StepAsync(steps, log, "session SETUP", () => session.SetupSessionAsync(SessionRequest(timingProtocol, sockets.Timing.Port), cancellationToken)).ConfigureAwait(false);
                await StepAsync(steps, log, "event channel", () => session.ConnectEventChannelAsync(cancellationToken)).ConfigureAwait(false);
                if (order.StartsWith('A'))
                {
                    await StepAsync(steps, log, "RECORD", async () => $"Audio-Latency {await session.RecordAsync(cancellationToken).ConfigureAwait(false)}").ConfigureAwait(false);
                    await StepAsync(steps, log, "stream SETUP", () => session.SetupStreamAsync(StreamRequest(session, sockets.ControlPort), cancellationToken)).ConfigureAwait(false);
                    await StepAsync(steps, log, string.Create(CultureInfo.InvariantCulture, $"volume {_options.VolumeDb}"), () => session.SetVolumeAsync(_options.VolumeDb, cancellationToken)).ConfigureAwait(false);
                    await StepAsync(steps, log, "GET_PARAMETER volume", async () => Show(await session.TryGetVolumeAsync(cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
                }
                else
                {
                    var early = _options.VolumeDb - 1.5;
                    await StepAsync(steps, log, string.Create(CultureInfo.InvariantCulture, $"volume {early} before stream SETUP"), () => session.SetVolumeAsync(early, cancellationToken)).ConfigureAwait(false);
                    await StepAsync(steps, log, "GET_PARAMETER volume", async () => Show(await session.TryGetVolumeAsync(cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
                    await StepAsync(steps, log, "stream SETUP", () => session.SetupStreamAsync(StreamRequest(session, sockets.ControlPort), cancellationToken)).ConfigureAwait(false);
                    await StepAsync(steps, log, "RECORD", async () => $"Audio-Latency {await session.RecordAsync(cancellationToken).ConfigureAwait(false)}").ConfigureAwait(false);
                    await StepAsync(steps, log, "GET_PARAMETER volume after stream SETUP", async () => Show(await session.TryGetVolumeAsync(cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
                }
            }
            finally
            {
                await EndAsync(session, log).ConfigureAwait(false);
            }

            results.Add($"{order[0]}: {string.Join("; ", steps)}");
        }

        return log.Report(string.Join(" | ", results));
    }

    /// <summary>
    /// P4 (C-7.4, A-Q5): keeps a session (no audio) for <paramref name="duration"/>, logs every event-channel message, polls
    /// <c>GET_PARAMETER volume</c> every second and sends feedback every 2 s; the user presses the receiver's volume buttons
    /// meanwhile. Reports which channel showed the changes.
    /// </summary>
    public async Task<ProbeReport> DeviceVolumeAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P4", _logger);
        var changes = 0;
        var events = 0;
        await using var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false);
        await using var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false);
        Task reader = Task.CompletedTask;
        try
        {
            var channel = await StartSessionAsync(session, sockets, TimingProtocol.Ntp, log, cancellationToken).ConfigureAwait(false);
            reader = DrainEventsAsync(channel, log, () => Interlocked.Increment(ref events), cancellationToken);
            await session.SetupStreamAsync(StreamRequest(session, sockets.ControlPort), cancellationToken).ConfigureAwait(false);
            await session.SetVolumeAsync(_options.VolumeDb, cancellationToken).ConfigureAwait(false);
            var last = await session.TryGetVolumeAsync(cancellationToken).ConfigureAwait(false);
            log.Add($"start volume {Show(last)}; press the receiver's volume buttons now ({duration.TotalSeconds:0} s)");
            var started = Stopwatch.GetTimestamp();
            var tick = 0;
            while (Stopwatch.GetElapsedTime(started) < duration)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                var volume = await session.TryGetVolumeAsync(cancellationToken).ConfigureAwait(false);
                if (volume != last)
                {
                    changes++;
                    log.Add($"GET_PARAMETER volume changed {Show(last)} -> {Show(volume)}");
                    last = volume;
                }

                if (++tick % 2 == 0)
                {
                    var body = await session.FeedbackAsync(cancellationToken).ConfigureAwait(false);
                    if (body is not null && body.Count > 0)
                    {
                        log.Add($"feedback body: {Describe(body)}");
                    }
                }
            }
        }
        finally
        {
            // TEARDOWN closes the event channel, which completes its message stream and ends the reader.
            await EndAsync(session, log).ConfigureAwait(false);
            await reader.ConfigureAwait(false);
        }

        return log.Report($"{changes} volume change(s) seen by GET_PARAMETER polling, {events} event-channel message(s) in {duration.TotalSeconds:0} s");
    }

    /// <summary>
    /// P5 (C-7.5): which <c>streamID</c> the stream SETUP returns; per-stream then session TEARDOWN (statuses,
    /// <c>Connection: close</c>); then a second session on a new connection must start without 451/453.
    /// </summary>
    public async Task<ProbeReport> StreamIdAndTeardownAsync(CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P5", _logger);
        string first;
        await using (var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false))
        await using (var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false))
        {
            StreamSetupResult? stream = null;
            try
            {
                await StartSessionAsync(session, sockets, TimingProtocol.Ntp, log, cancellationToken).ConfigureAwait(false);
                stream = await session.SetupStreamAsync(StreamRequest(session, sockets.ControlPort), cancellationToken).ConfigureAwait(false);
                log.Add($"stream SETUP answer: {Describe(stream.Raw)}");
            }
            finally
            {
                var teardown = await EndAsync(session, log).ConfigureAwait(false);
                first = string.Create(
                    CultureInfo.InvariantCulture,
                    $"streamID {Show(stream?.StreamId)}; per-stream TEARDOWN {(teardown?.StreamResponses is { Count: > 0 } s ? string.Join(",", s.Select(r => r.StatusCode)) : "not sent")}; session TEARDOWN {Show(teardown?.SessionResponse?.StatusCode)}{(teardown?.ConnectionClose == true ? " with Connection: close" : " without Connection: close")}");
                log.Note(first);
                var closed = await Task.WhenAny(session.Connection.Closed, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false) == session.Connection.Closed;
                log.Note(closed ? $"receiver closed the control connection: {await session.Connection.Closed.ConfigureAwait(false)}" : "control connection still open 2 s after the session TEARDOWN");
            }
        }

        string second;
        await using (var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false))
        await using (var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await session.SetupSessionAsync(SessionRequest(TimingProtocol.Ntp, sockets.Timing.Port), cancellationToken).ConfigureAwait(false);
                second = "next session SETUP 200";
            }
            catch (RtspStatusException ex)
            {
                second = $"next session SETUP {ex.StatusCode}";
            }
            finally
            {
                await EndAsync(session, log).ConfigureAwait(false);
            }

            log.Note(second);
        }

        return log.Report($"{first}; {second}");
    }

    /// <summary>
    /// P8 (C-7.8, A-Q13): feedback answers for no body, <c>{}</c> and <c>{streams: []}</c> (three each, 2 s apart); then
    /// no feedback for <paramref name="idle"/> while watching both sockets, to time an idle drop (the full 10-minute runs
    /// are a parameter of the caller).
    /// </summary>
    public async Task<ProbeReport> FeedbackAsync(TimeSpan idle, CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P8", _logger);
        var answers = new List<string>();
        string idleOutcome = "idle phase skipped";
        await using var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false);
        await using var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await StartSessionAsync(session, sockets, TimingProtocol.Ntp, log, cancellationToken).ConfigureAwait(false);
            await session.SetupStreamAsync(StreamRequest(session, sockets.ControlPort), cancellationToken).ConfigureAwait(false);
            foreach (var kind in FeedbackBodies)
            {
                for (var i = 0; i < 3; i++)
                {
                    PlistDictionary? body = kind switch
                    {
                        "{}" => new PlistDictionary(),
                        "{streams: []}" => new PlistDictionary { ["streams"] = new List<object?>() },
                        _ => null,
                    };
                    string outcome;
                    try
                    {
                        var answer = await session.FeedbackAsync(body, cancellationToken).ConfigureAwait(false);
                        outcome = $"200 {(answer is null ? "(empty)" : Describe(answer))}";
                    }
                    catch (RtspStatusException ex)
                    {
                        outcome = ex.StatusCode.ToString(CultureInfo.InvariantCulture);
                    }

                    log.Add($"feedback body {kind}: {outcome}");
                    if (i == 0)
                    {
                        answers.Add($"{kind} -> {outcome}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }

            if (idle > TimeSpan.Zero)
            {
                var started = Stopwatch.GetTimestamp();
                var finished = await Task.WhenAny(session.Connection.Closed, channel.Closed, Task.Delay(idle, cancellationToken)).ConfigureAwait(false);
                idleOutcome = finished == session.Connection.Closed
                    ? $"control connection closed after {Stopwatch.GetElapsedTime(started).TotalSeconds:0} s idle: {await session.Connection.Closed.ConfigureAwait(false)}"
                    : finished == channel.Closed
                        ? $"event channel closed after {Stopwatch.GetElapsedTime(started).TotalSeconds:0} s idle: {await channel.Closed.ConfigureAwait(false)}"
                        : $"still connected after {idle.TotalSeconds:0} s without feedback";
                log.Note(idleOutcome);
            }
        }
        finally
        {
            await EndAsync(session, log).ConfigureAwait(false);
        }

        return log.Report($"{string.Join("; ", answers)}; {idleOutcome}");
    }

    /// <summary>
    /// P9 (C-7.9): sends DMAP <c>mlit{minm, asar, asal, astm}</c>, the artwork and a progress item for a session without
    /// audio; whether the receiver's app shows them is checked by the user.
    /// </summary>
    public async Task<ProbeReport> MetadataAsync(TrackMetadata metadata, ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var log = new ProbeLog("P9", _logger);
        var steps = new List<string>();
        await using var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false);
        await using var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await StartSessionAsync(session, sockets, TimingProtocol.Ntp, log, cancellationToken).ConfigureAwait(false);
            await session.SetupStreamAsync(StreamRequest(session, sockets.ControlPort), cancellationToken).ConfigureAwait(false);
            await session.SetVolumeAsync(_options.VolumeDb, cancellationToken).ConfigureAwait(false);
            const uint start = 77175;
            var length = (uint)Math.Min(uint.MaxValue - start, (metadata.Duration ?? TimeSpan.FromMinutes(3)).TotalSeconds * 44100);
            var uri = session.Connection.SessionUri;
            await StepAsync(steps, log, "DMAP", () => session.Connection.SendExpectOkAsync(SessionRequests.Metadata(uri, metadata, start), cancellationToken)).ConfigureAwait(false);

            // Variants: pyatv's minimal item (minm, asal, asar only), with rtptime alone and with seq=;rtptime= as pyatv
            // sends it [pyatv:pyatv/support/rtsp.py:202-226].
            var minimal = metadata with { Duration = null, TrackNumber = null, DiscNumber = null, Genre = null };
            await StepAsync(steps, log, "DMAP minimal", () => session.Connection.SendExpectOkAsync(SessionRequests.Metadata(uri, minimal, start), cancellationToken)).ConfigureAwait(false);
            var withSeq = RtspRequest.WithBody(
                "SET_PARAMETER",
                uri,
                RtspContentTypes.DmapTagged,
                DmapEncoder.EncodeListingItem(minimal),
                new Dictionary<string, string> { [RtspHeaders.RtpInfo] = string.Create(CultureInfo.InvariantCulture, $"seq=0;rtptime={start}") });
            await StepAsync(steps, log, "DMAP minimal with seq", () => session.Connection.SendExpectOkAsync(withSeq, cancellationToken)).ConfigureAwait(false);
            if (!jpeg.IsEmpty)
            {
                await StepAsync(steps, log, $"artwork {jpeg.Length} B", () => session.Connection.SendExpectOkAsync(SessionRequests.Artwork(uri, jpeg, RtspContentTypes.Jpeg, start), cancellationToken)).ConfigureAwait(false);
            }

            var progress = new ProgressInfo(start - 15360, start, start + length, start);
            await StepAsync(steps, log, "progress", () => session.Connection.SendExpectOkAsync(SessionRequests.Progress(uri, progress), cancellationToken)).ConfigureAwait(false);
            log.Note("check the receiver's companion app now");
        }
        finally
        {
            await EndAsync(session, log).ConfigureAwait(false);
        }

        return log.Report(string.Join("; ", steps));
    }

    /// <summary>P10 (C-7.10): does the encrypted <c>GET /info</c> carry <c>initialVolume</c>; what does <c>GET_PARAMETER volume</c> say?</summary>
    public async Task<ProbeReport> InitialVolumeAsync(CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P10", _logger);
        string infoVolume;
        string afterSession = "-";
        string afterStream = "-";
        await using var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false);
        await using var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false);
        try
        {
            var info = await session.GetInfoAsync(withQualifier: false, cancellationToken).ConfigureAwait(false);
            infoVolume = info.GetDouble("initialVolume") is { } v ? v.ToString(CultureInfo.InvariantCulture) : "absent";
            log.Add($"encrypted GET /info: {info.Count} keys, initialVolume {infoVolume}");
            var stage1 = await session.GetInfoAsync(withQualifier: true, cancellationToken).ConfigureAwait(false);
            log.Add($"encrypted GET /info stage 1: {stage1.Count} keys, initialVolume {Show(stage1.GetDouble("initialVolume"))}");
            await session.SetupSessionAsync(SessionRequest(TimingProtocol.Ntp, sockets.Timing.Port), cancellationToken).ConfigureAwait(false);
            afterSession = await TryReadVolumeAsync(session).ConfigureAwait(false);
            log.Add($"GET_PARAMETER volume after the session SETUP: {afterSession}");
            await session.ConnectEventChannelAsync(cancellationToken).ConfigureAwait(false);
            await session.RecordAsync(cancellationToken).ConfigureAwait(false);
            await session.SetupStreamAsync(StreamRequest(session, sockets.ControlPort), cancellationToken).ConfigureAwait(false);
            afterStream = await TryReadVolumeAsync(session).ConfigureAwait(false);
            log.Add($"GET_PARAMETER volume after the stream SETUP: {afterStream}");
        }
        finally
        {
            await EndAsync(session, log).ConfigureAwait(false);
        }

        return log.Report($"paired GET /info initialVolume {infoVolume}; GET_PARAMETER volume after session SETUP {afterSession}, after stream SETUP {afterStream}");

        async Task<string> TryReadVolumeAsync(ReceiverSession s)
        {
            try
            {
                return Show(await s.TryGetVolumeAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (RtspStatusException ex)
            {
                return $"status {ex.StatusCode}";
            }
        }
    }

    /// <summary>
    /// P13 (C-7.13): when does the first event-channel message arrive relative to the session SETUP and RECORD, and does
    /// the receiver close the channel when our answer is delayed by <paramref name="answerDelay"/>? Waits
    /// <paramref name="answerDelay"/> plus 10 s for a close.
    /// </summary>
    public async Task<ProbeReport> EventTimingAsync(TimeSpan answerDelay, CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P13", _logger);
        var sessionOptions = new ReceiverSessionOptions { EventChannel = new EventChannelOptions { AnswerDelay = answerDelay } };
        string outcome;
        await using var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false);
        await using var session = await OpenSessionAsync(log, sessionOptions, cancellationToken).ConfigureAwait(false);
        try
        {
            var started = Stopwatch.GetTimestamp();
            await session.SetupSessionAsync(SessionRequest(TimingProtocol.Ntp, sockets.Timing.Port), cancellationToken).ConfigureAwait(false);
            var setupAt = Stopwatch.GetElapsedTime(started);
            var channel = await session.ConnectEventChannelAsync(cancellationToken).ConfigureAwait(false);
            var connectedAt = Stopwatch.GetElapsedTime(started);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var beforeRecord = channel.FirstRequestAfter;
            await session.RecordAsync(cancellationToken).ConfigureAwait(false);
            var recordAt = Stopwatch.GetElapsedTime(started);
            log.Add($"session SETUP answered at {setupAt.TotalMilliseconds:0} ms, event channel at {connectedAt.TotalMilliseconds:0} ms, RECORD answered at {recordAt.TotalMilliseconds:0} ms; first event request {(beforeRecord is { } b ? $"{b.TotalMilliseconds:0} ms after connect (before RECORD)" : "not before RECORD")}");
            var wait = answerDelay + TimeSpan.FromSeconds(10);
            var closed = await Task.WhenAny(channel.Closed, Task.Delay(wait, cancellationToken)).ConfigureAwait(false) == channel.Closed;
            var first = channel.FirstRequestAfter;
            var closeReason = closed ? await channel.Closed.ConfigureAwait(false) : null;
            outcome = string.Create(
                CultureInfo.InvariantCulture,
                $"first event request {(first is { } f ? $"{f.TotalMilliseconds:0} ms after the event connect ({(beforeRecord is not null ? "before" : "after")} RECORD)" : "never")}; answer delay {answerDelay.TotalSeconds:0} s: {(closed ? $"channel closed ({closeReason}) at {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s" : $"channel still open after {wait.TotalSeconds:0} s")}, {channel.RequestsAnswered} answered");
            log.Note(outcome);
        }
        finally
        {
            await EndAsync(session, log).ConfigureAwait(false);
        }

        return log.Report(outcome);
    }

    /// <summary>
    /// P15 (A-Q4), control part: stream SETUP with <c>shk</c> = the first 32 bytes of the shared secret (D4) and, on a
    /// second session, 32 random bytes; both must be accepted (the audio part that checks playback is wave 4).
    /// </summary>
    public async Task<ProbeReport> StreamKeyAsync(CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P15", _logger);
        var results = new List<string>();
        foreach (var random in new[] { false, true })
        {
            await using var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false);
            await using var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false);
            try
            {
                await StartSessionAsync(session, sockets, TimingProtocol.Ntp, log, cancellationToken).ConfigureAwait(false);
                var key = random ? RandomNumberGenerator.GetBytes(32) : session.Pairing!.DefaultStreamKey;
                var request = StreamRequest(session, sockets.ControlPort) with { StreamKey = key };
                var name = random ? "random shk" : "shk = SS[0..31]";
                try
                {
                    var stream = await session.SetupStreamAsync(request, cancellationToken).ConfigureAwait(false);
                    results.Add($"{name}: 200 (dataPort {stream.DataPort})");
                }
                catch (RtspStatusException ex)
                {
                    results.Add($"{name}: {ex.StatusCode}");
                }

                log.Note(results[^1]);
            }
            finally
            {
                await EndAsync(session, log).ConfigureAwait(false);
            }
        }

        return log.Report(string.Join("; ", results));
    }

    /// <summary>
    /// P21 (C-7.15): on a throw-away connection, after pairing, <c>GET /info</c> as one HAP frame of
    /// <paramref name="frameSize"/> bytes (default 2048; the library never sends frames above 1024); then, in a normal
    /// session, the largest frame sizes the receiver sends on the control connection and the event channel.
    /// </summary>
    public async Task<ProbeReport> LargeFrameAsync(int frameSize, CancellationToken cancellationToken)
    {
        var log = new ProbeLog("P21", _logger);
        string oversized;
        var endpoint = Endpoint();
        await using (var raw = await OversizedFrameProbeConnection.ConnectAsync(endpoint, _options.Connection.ConnectTimeout, _options.Connection.RequestTimeout, _loggerFactory.CreateLogger("P21"), cancellationToken).ConfigureAwait(false))
        {
            var pairing = new HapPairingClient(_loggerFactory.CreateLogger<HapPairingClient>());
            var stored = await _store.GetAsync(_receiver.Id, cancellationToken).ConfigureAwait(false);
            var paired = stored is not null
                ? await pairing.VerifyAsync(raw, stored, cancellationToken).ConfigureAwait(false)
                : await pairing.PairTransientAsync(raw, cancellationToken).ConfigureAwait(false);
            raw.EnableEncryption(paired.ControlKeys);
            log.Add($"throw-away connection paired ({paired.Kind})");
            var (response, failure) = await raw.SendAsSingleFrameAsync(RtspRequest.Create("GET", "/info"), frameSize, cancellationToken).ConfigureAwait(false);
            oversized = response is not null
                ? $"GET /info as one {frameSize}-byte frame: {response.StatusCode} ({response.Body.Length}-byte body, largest frame received {raw.LargestReceivedFrame} B)"
                : $"GET /info as one {frameSize}-byte frame: no answer ({failure})";
            log.Note(oversized);
        }

        string normal;
        await using var sockets = await ProbeSockets.StartAsync(_clock, _loggerFactory, cancellationToken).ConfigureAwait(false);
        await using var session = await OpenSessionAsync(log, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await session.GetInfoAsync(withQualifier: true, cancellationToken).ConfigureAwait(false);
            var channel = await StartSessionAsync(session, sockets, TimingProtocol.Ntp, log, cancellationToken).ConfigureAwait(false);
            await session.SetupStreamAsync(StreamRequest(session, sockets.ControlPort), cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var control = session.Connection is RtspConnection rtsp ? $"{rtsp.ReceivedFrames} frames, largest {rtsp.LargestReceivedFrame} B" : "n/a";
            normal = $"normal session: control connection {control}; event channel {channel.FramesReceived} frames, largest {channel.LargestFrameReceived} B";
            log.Note(normal);
        }
        finally
        {
            await EndAsync(session, log).ConfigureAwait(false);
        }

        return log.Report($"{oversized}; {normal}");
    }

    /// <summary>A small valid 16x16 single-colour baseline JPEG (634 bytes) for P9 when the caller has no artwork.</summary>
    public static byte[] SampleJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/" +
        "2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAQABADASIAAhEBAxEB/8QA" +
        "HwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkK" +
        "FhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXG" +
        "x8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
        "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOE" +
        "hYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDweiiiv3M/" +
        "LD//2Q==");

    private async Task<ReceiverSession> OpenSessionAsync(ProbeLog log, ReceiverSessionOptions? sessionOptions, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint();
        var connection = await RtspConnection.ConnectAsync(endpoint, _options.Connection, _loggerFactory.CreateLogger<RtspConnection>(), cancellationToken).ConfigureAwait(false);
        var session = new ReceiverSession(
            _receiver,
            connection,
            new HapPairingClient(_loggerFactory.CreateLogger<HapPairingClient>()),
            _options.Quirks ?? ReceiverQuirks.ForReceiver(_receiver),
            _loggerFactory.CreateLogger<ReceiverSession>(),
            sessionOptions);
        try
        {
            var info = await session.GetInfoAsync(withQualifier: false, cancellationToken).ConfigureAwait(false);
            var paired = await session.PairAsync(_store, pinPrompt: null, cancellationToken).ConfigureAwait(false);
            log.Add($"connected to {endpoint} from {connection.LocalAddress} ({info.GetString("model") ?? "?"}), paired {paired.Kind}");
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<EventChannel> StartSessionAsync(ReceiverSession session, ProbeSockets sockets, TimingProtocol protocol, ProbeLog log, CancellationToken cancellationToken)
    {
        var setup = await session.SetupSessionAsync(SessionRequest(protocol, sockets.Timing.Port), cancellationToken).ConfigureAwait(false);
        var channel = await session.ConnectEventChannelAsync(cancellationToken).ConfigureAwait(false);
        var latency = await session.RecordAsync(cancellationToken).ConfigureAwait(false);
        log.Add($"session SETUP ({protocol}) eventPort {setup.EventPort}, event channel connected, RECORD Audio-Latency {latency}");
        return channel;
    }

    private async Task<TeardownResult?> EndAsync(ReceiverSession session, ProbeLog log)
    {
        if (session.Connection.Closed.IsCompleted && session.EventChannel is null)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(_options.TeardownTimeout);
        try
        {
            var result = await session.TeardownWithResultAsync(timeout.Token).ConfigureAwait(false);
            if (result.SessionResponse is not null)
            {
                log.Add($"TEARDOWN: {result.StreamResponses.Count} per-stream, session {result.SessionResponse.StatusCode}{(result.ConnectionClose ? " (Connection: close)" : string.Empty)}");
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or RtspStatusException)
        {
            log.Add($"TEARDOWN failed: {ex.Message}");
            return null;
        }
    }

    private async Task DrainEventsAsync(EventChannel channel, ProbeLog log, Action counted, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in channel.Messages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                counted();
                log.Note(message switch
                {
                    ReceiverEventMessage.UpdateInfo u => $"event: updateInfo, initialVolume {Show(u.Value.GetDouble("initialVolume"))}, {u.Value.Count} keys",
                    ReceiverEventMessage.MediaRemoteCommand c => $"event: sendMediaRemoteCommand {c.Command}",
                    ReceiverEventMessage.Unknown u => $"event: {u.Request.Method} {u.Request.Uri} type {u.Type ?? "(none)"} {(u.Body is null ? string.Empty : Describe(u.Body))}",
                    ReceiverEventMessage.Closed c => $"event channel closed: {c.Reason}",
                    _ => message.ToString(),
                });
            }
        }
        catch (OperationCanceledException)
        {
            // probe cancelled
        }
    }

    private SessionSetupRequest SessionRequest(TimingProtocol protocol, int timingPort, ulong? clockId = null) => new(
        _options.Sender,
        protocol,
        timingPort,
        Guid.NewGuid().ToString().ToUpperInvariant(),
        GroupUuid: null,
        GroupContainsGroupLeader: false,
        IsMultiSelectAirPlay: true,
        TimingPeerAddresses: null,
        PtpClockId: protocol == TimingProtocol.Ptp ? clockId ?? RandomClockId() : null);

    /// <summary>A PTP clock id in the shape <c>0xFFFF &lt;&lt; 48 | 48-bit seed</c> (SPEC-audio.md 4.4).</summary>
    private static ulong RandomClockId()
    {
        Span<byte> seed = stackalloc byte[8];
        RandomNumberGenerator.Fill(seed);
        return (0xFFFFUL << 48) | (BitConverter.ToUInt64(seed) & 0xFFFF_FFFF_FFFFUL);
    }

    private static StreamSetupRequest StreamRequest(ReceiverSession session, int controlPort) =>
        new(StreamType.Realtime, session.Pairing?.DefaultStreamKey ?? throw new InvalidOperationException("Not paired."), controlPort);

    private IPEndPoint Endpoint()
    {
        var address = _options.Address
            ?? _receiver.AddressOf(AddressFamily.InterNetwork)
            ?? _receiver.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"{_receiver.Name} has no address.");
        return new IPEndPoint(address, _receiver.Port);
    }

    private static async Task StepAsync(List<string> steps, ProbeLog log, string name, Func<Task> step)
    {
        string outcome;
        try
        {
            await step().ConfigureAwait(false);
            outcome = "200";
        }
        catch (RtspStatusException ex)
        {
            outcome = ex.StatusCode.ToString(CultureInfo.InvariantCulture);
        }

        steps.Add($"{name} {outcome}");
        log.Add($"{name}: {outcome}");
    }

    private static async Task StepAsync<T>(List<string> steps, ProbeLog log, string name, Func<Task<T>> step)
    {
        string outcome;
        try
        {
            var value = await step().ConfigureAwait(false);
            outcome = value switch
            {
                string s => $"200 ({s})",
                SessionSetupResult r => $"200 (eventPort {r.EventPort})",
                StreamSetupResult r => $"200 (streamID {Show(r.StreamId)})",
                _ => "200",
            };
        }
        catch (RtspStatusException ex)
        {
            outcome = ex.StatusCode.ToString(CultureInfo.InvariantCulture);
        }

        steps.Add($"{name} {outcome}");
        log.Add($"{name}: {outcome}");
    }

    private static Socket BindUdp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return socket;
    }

    private static int LocalPort(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private static string Show<T>(T? value)
        where T : struct => value is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture)! : "none";

    private static string Describe(PlistDictionary plist) => DescribeValue(plist);

    private static string DescribeValue(object? value) => value switch
    {
        null => "null",
        PlistDictionary d => "{" + string.Join(", ", d.Select(kv => $"{kv.Key}: {DescribeValue(kv.Value)}")) + "}",
        List<object?> l => "[" + string.Join(", ", l.Select(DescribeValue)) + "]",
        byte[] b => $"<{b.Length} bytes>",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>The UDP sockets a probe announces: a timing responder (answers 0xD2) and a bound control port.</summary>
    private sealed class ProbeSockets : IAsyncDisposable
    {
        private readonly Socket _control;

        private ProbeSockets(TimingResponder timing, Socket control)
        {
            Timing = timing;
            _control = control;
        }

        public TimingResponder Timing { get; }

        public int ControlPort => LocalPort(_control);

        public static async Task<ProbeSockets> StartAsync(IMasterClock clock, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
        {
            var timing = new TimingResponder(clock, loggerFactory.CreateLogger<TimingResponder>());
            try
            {
                await timing.StartAsync(cancellationToken).ConfigureAwait(false);
                return new ProbeSockets(timing, BindUdp());
            }
            catch
            {
                await timing.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _control.Dispose();
            await Timing.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Timestamped observations of one probe, mirrored to the logger at Information.</summary>
    private sealed class ProbeLog(string probe, ILogger logger)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

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
            return new ProbeReport(probe, summary, Lines);
        }
    }
}
