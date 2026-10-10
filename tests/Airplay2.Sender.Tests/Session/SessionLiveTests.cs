using System.Globalization;
using System.Net;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Session;

/// <summary>
/// Live session tests and probes (docs/DEVELOPMENT.md, "Live-device rules"): pairing, session SETUP, event channel, RECORD,
/// stream SETUP, volume, feedback and TEARDOWN on the Test Speaker ABCD (<c>SpeakerA_HOST</c>, default 192.0.2.26),
/// <b>never audio</b>; and the PTP session path on shairport-sync inside the Docker VM (<c>SPS_HOST</c>/<c>SPS_PORT</c>).
/// They return early unless <c>AIRPLAY_LIVE=1</c>. Every run writes the decrypted wire exchange (Debug) to
/// <c>artifacts/live/&lt;date&gt;-&lt;probe&gt;.log</c>.
/// </summary>
public sealed class SessionLiveTests(ITestOutputHelper output)
{
    private static readonly string[] SpeakerAAirplay =
    [
        "acl=0", "deviceid=02:1A:2B:3C:4D:5E", "features=0x445F8A00,0x1C340", "rsf=0x0", "fv=p20.1.1.36.0x8b4c480", "flags=0x404",
        "model=Test Speaker ABCD", "manufacturer=Lautsprecher SpeakerA GmbH", "serialNumber=00000000-0000-0000-0000-000000000000",
        "protovers=1.1", "srcvers=366.0", "pi=02:1A:2B:3C:4D:5E", "gid=02:1A:2B:3C:4D:5E", "gcgl=0",
        "pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
    ];

    private static readonly string[] SpeakerARaop =
    [
        "cn=0,1", "da=true", "et=0,4", "ft=0x445F8A00,0x1C340", "fv=p20.1.1.36.0x8b4c480", "md=0,1,2", "am=Test Speaker ABCD",
        "sf=0x404", "tp=UDP", "vn=65537", "vs=366.0", "pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
    ];

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("AIRPLAY_LIVE") == "1";

    /// <summary>The session start-up of D5 without audio: pairing, session SETUP (NTP), event channel, RECORD, stream SETUP, volume, GET_PARAMETER, feedback, TEARDOWN.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task SpeakerA_session_startup_without_audio()
    {
        if (!Live())
        {
            return;
        }

        using var log = new SessionLiveLog("session-startup", output);
        var receiver = SpeakerA();
        await using var connection = await RtspConnection.ConnectAsync(new IPEndPoint(receiver.Addresses[0], receiver.Port), new RtspConnectionOptions { RequestTimeout = TimeSpan.FromSeconds(30) }, log.CreateLogger("rtsp"), CancellationToken.None);
        await using var session = new ReceiverSession(receiver, connection, new HapPairingClient(log.CreateLogger<HapPairingClient>()), ReceiverQuirks.ForReceiver(receiver), log.CreateLogger<ReceiverSession>());
        await using var timing = new TimingResponder(new MasterClock(), log.CreateLogger<TimingResponder>());
        await timing.StartAsync(CancellationToken.None);
        using var control = new System.Net.Sockets.UdpClient(0);
        try
        {
            await session.GetInfoAsync(false, CancellationToken.None);
            var paired = await session.PairAsync(new InMemoryPairingStore(), null, CancellationToken.None);
            log.Write($"paired: {paired.Kind}");
            var setup = await session.SetupSessionAsync(
                new SessionSetupRequest(SessionTestSupport.Sender with { Name = "airplay2-streamer live test" }, TimingProtocol.Ntp, timing.Port, Guid.NewGuid().ToString().ToUpperInvariant(), null, false, true, null, null),
                CancellationToken.None);
            log.Write($"session SETUP: eventPort {setup.EventPort}, keys {string.Join(",", setup.Raw.Keys)}");
            var channel = await session.ConnectEventChannelAsync(CancellationToken.None);
            var latency = await session.RecordAsync(CancellationToken.None);
            log.Write($"RECORD: Audio-Latency {latency}");
            var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, paired.DefaultStreamKey, ((IPEndPoint)control.Client.LocalEndPoint!).Port), CancellationToken.None);
            log.Write($"stream SETUP: data {stream.DataPort}, control {stream.ControlPort}, streamID {stream.StreamId?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            var before = await session.TryGetVolumeAsync(CancellationToken.None);
            await session.SetVolumeAsync(-24.0, CancellationToken.None);
            var after = await session.TryGetVolumeAsync(CancellationToken.None);
            log.Write($"GET_PARAMETER volume before {before?.ToString(CultureInfo.InvariantCulture) ?? "none"}, after setting -24: {after?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            for (var i = 0; i < 2; i++)
            {
                var body = await session.FeedbackAsync(CancellationToken.None);
                log.Write($"feedback: {(body is null ? "empty" : string.Join(",", body.Keys))}");
                await Task.Delay(2000);
            }

            while (channel.Messages.TryRead(out var message))
            {
                log.Write($"event: {message.GetType().Name}");
            }

            log.Write($"timing requests answered: {timing.RepliesSent}; event requests answered {channel.RequestsAnswered}, largest event frame {channel.LargestFrameReceived} B; largest control frame {connection.LargestReceivedFrame} B");
        }
        finally
        {
            var result = await session.TeardownWithResultAsync(CancellationToken.None);
            log.Write($"TEARDOWN: {result.StreamResponses.Count} per-stream, session {result.SessionResponse?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "-"}, Connection: close {result.ConnectionClose}");
        }
    }

    /// <summary>P1, control part: the D1 gate.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P1_timing_protocol() => RunProbeAsync("P1-timing-protocol", async p => await p.TimingProtocolAsync(CancellationToken.None));

    /// <summary>P3: both start-up orders.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P3_startup_order() => RunProbeAsync("P3-startup-order", p => p.StartupOrderAsync(TimingProtocol.Ntp, CancellationToken.None));

    /// <summary>P4: device-side volume (press the buttons during the run; <c>P4_SECONDS</c>, default 10).</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P4_device_volume() => RunProbeAsync("P4-device-volume", p => p.DeviceVolumeAsync(TimeSpan.FromSeconds(Seconds("P4_SECONDS", 10)), CancellationToken.None));

    /// <summary>P5: streamID and both TEARDOWN forms.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P5_stream_id_and_teardown() => RunProbeAsync("P5-teardown", p => p.StreamIdAndTeardownAsync(CancellationToken.None));

    /// <summary>P8: feedback bodies; idle drop timing with <c>P8_IDLE_SECONDS</c> (default 0).</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P8_feedback() => RunProbeAsync("P8-feedback", p => p.FeedbackAsync(TimeSpan.FromSeconds(Seconds("P8_IDLE_SECONDS", 0)), CancellationToken.None));

    /// <summary>P9: metadata and artwork (check the SpeakerA app).</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P9_metadata() => RunProbeAsync("P9-metadata", p => p.MetadataAsync(new TrackMetadata("airplay2-streamer probe", "Probe Artist", "Probe Album", TimeSpan.FromMinutes(3)), SessionProbes.SampleJpeg(), CancellationToken.None));

    /// <summary>P10: initialVolume once paired, GET_PARAMETER volume.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P10_initial_volume() => RunProbeAsync("P10-initial-volume", p => p.InitialVolumeAsync(CancellationToken.None));

    /// <summary>P13: event timing; answer delay from <c>P13_DELAY_SECONDS</c> (default 5).</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P13_event_timing() => RunProbeAsync("P13-event-timing", p => p.EventTimingAsync(TimeSpan.FromSeconds(Seconds("P13_DELAY_SECONDS", 5)), CancellationToken.None));

    /// <summary>P15, control part: both stream keys accepted.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P15_stream_key() => RunProbeAsync("P15-stream-key", p => p.StreamKeyAsync(CancellationToken.None));

    /// <summary>P21: one 2048-byte frame; received frame sizes.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P21_large_frame() => RunProbeAsync("P21-large-frame", p => p.LargeFrameAsync(2048, CancellationToken.None));

    /// <summary>
    /// shairport-sync in the Docker VM: transient pairing, PTP session SETUP (5.5.2 refuses NTP with 200 and an empty body), event channel with
    /// <c>updateInfo</c> on connect, RECORD, stream SETUP (no streamID), volume set/get with the leading-CRLF reply,
    /// feedback, TEARDOWN.
    /// </summary>
    [Fact]
    [Trait("Category", "ShairportLive")]
    public async Task Shairport_ptp_session_startup()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SPS_HOST") ?? "127.0.0.1");
        var port = int.Parse(Environment.GetEnvironmentVariable("SPS_PORT") ?? "7000", CultureInfo.InvariantCulture);
        using var log = new SessionLiveLog("shairport-session", output);
        // TXT as shairport-sync 5.5.2 advertises it (model=ShairportSync: txtAirPlay in artifacts/live/2026-10-09-rtsp-get-info-shairport.log).
        var receiver = new DiscoveredReceiver("SPS", "shairport-sync", [host], port, AirplayTxtRecord.Parse(["features=0x405FCA00,0x1C340", "flags=0x4", "model=ShairportSync"]), null, DateTimeOffset.UtcNow);
        var quirks = ReceiverQuirks.ForReceiver(receiver);
        Assert.Equal(TimingProtocol.Ptp, quirks.TimingProtocol);
        var connection = await RtspConnection.ConnectAsync(new IPEndPoint(host, port), new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None);
        await using var session = new ReceiverSession(receiver, connection, new HapPairingClient(log.CreateLogger<HapPairingClient>()), quirks, log.CreateLogger<ReceiverSession>());
        try
        {
            await session.GetInfoAsync(false, CancellationToken.None);
            var paired = await session.PairAsync(new InMemoryPairingStore(), null, CancellationToken.None);
            // shairport-sync 5.5.2 refuses NTP with "200 OK" and an empty body (not 501 as SPEC-control.md 4.2 expected).
            var ntp = await Assert.ThrowsAsync<InvalidDataException>(() => session.SetupSessionAsync(SessionTestSupport.NtpSession(), CancellationToken.None));
            log.Write($"NTP session SETUP: {ntp.Message}");
            var setup = await session.SetupSessionAsync(SessionTestSupport.NtpSession() with { TimingProtocol = TimingProtocol.Ptp, TimingPort = 0, PtpClockId = 0xFFFF_0000_1234_5678UL }, CancellationToken.None);
            log.Write($"PTP session SETUP: eventPort {setup.EventPort}, timingPeerInfo [{string.Join(", ", setup.TimingPeerAddresses)}]");
            var channel = await session.ConnectEventChannelAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = await channel.Messages.ReadAsync(cts.Token);
            log.Write($"first event: {first.GetType().Name}");
            Assert.IsType<ReceiverEventMessage.UpdateInfo>(first);
            Assert.Equal(0, await session.RecordAsync(CancellationToken.None));
            using var control = new System.Net.Sockets.UdpClient(0);
            var stream = await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, paired.DefaultStreamKey, ((IPEndPoint)control.Client.LocalEndPoint!).Port), CancellationToken.None);
            log.Write($"stream SETUP: data {stream.DataPort}, control {stream.ControlPort}, streamID {stream.StreamId?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            await session.SetVolumeAsync(-20.0, CancellationToken.None);
            var volume = await session.GetVolumeAsync(CancellationToken.None);
            log.Write($"GET_PARAMETER volume: {volume.ToString(CultureInfo.InvariantCulture)}");
            Assert.Equal(-20.0, volume, 3);
            var feedback = await session.FeedbackAsync(CancellationToken.None);
            log.Write($"feedback: {(feedback is null ? "empty" : string.Join(",", feedback.Keys))}");
        }
        finally
        {
            var result = await session.TeardownWithResultAsync(CancellationToken.None);
            log.Write($"TEARDOWN: {result.StreamResponses.Count} per-stream, session {result.SessionResponse?.StatusCode.ToString(CultureInfo.InvariantCulture) ?? "-"}, Connection: close {result.ConnectionClose}");
        }
    }

    private async Task RunProbeAsync(string name, Func<SessionProbes, Task<ProbeReport>> probe)
    {
        if (!Live())
        {
            return;
        }

        using var log = new SessionLiveLog(name, output);
        var probes = new SessionProbes(SpeakerA(), new InMemoryPairingStore(), log, new SessionProbeOptions { Sender = SessionTestSupport.Sender with { Name = "airplay2-streamer probe" } });
        var report = await probe(probes);
        log.Write($"RESULT {report.Probe}: {report.Summary}");
    }

    private bool Live()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return false;
        }

        return true;
    }

    private static DiscoveredReceiver SpeakerA()
    {
        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SpeakerA_HOST") ?? "192.0.2.26");
        return new DiscoveredReceiver("02:1A:2B:3C:4D:5E", "Test Speaker ABCD", [host], 7000, AirplayTxtRecord.Parse(SpeakerAAirplay), RaopTxtRecord.Parse(SpeakerARaop), DateTimeOffset.UtcNow);
    }

    private static double Seconds(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

/// <summary>Writes every log line (Debug included: the decrypted wire exchange) to <c>artifacts/live/&lt;date&gt;-&lt;probe&gt;.log</c> and the test output.</summary>
internal sealed class SessionLiveLog : ILoggerFactory
{
    private readonly StreamWriter _file;
    private readonly ITestOutputHelper _output;
    private readonly Lock _gate = new();

    public SessionLiveLog(string probe, ITestOutputHelper output)
    {
        _output = output;
        var directory = Path.Combine(FindRepositoryRoot(), "artifacts", "live");
        Directory.CreateDirectory(directory);
        _file = new StreamWriter(Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}-{probe}.log"), append: true) { AutoFlush = true };
        Write($"--- {probe} {DateTimeOffset.Now:O}");
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            _file.WriteLine(line);
            try
            {
                _output.WriteLine(line);
            }
            catch (InvalidOperationException)
            {
                // the test already finished (a late background log line)
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public ILogger<T> CreateLogger<T>() => new Logger<T>(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file.Dispose();
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class Logger(SessionLiveLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                owner.Write($"{DateTimeOffset.Now:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception.Message)}");
            }
            catch (ObjectDisposedException)
            {
                // the log was closed by the test before a late background line
            }
        }
    }
}
