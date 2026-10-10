using System.Globalization;
using System.Net;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Pairing;

/// <summary>
/// Live pairing tests (docs/DEVELOPMENT.md, "Live-device rules"). They return early unless <c>AIRPLAY_LIVE=1</c>; the default
/// <c>dotnet test</c> therefore never touches a receiver. ShairportLive runs inside the Docker VM against the
/// shairport-sync container (<c>SPS_HOST</c>/<c>SPS_PORT</c>); SpeakerALive targets the Test Speaker ABCD only
/// (<c>SpeakerA_HOST</c>, default 192.0.2.26) and is for the stage that allows pairing on it (wave 3, session-streams):
/// probes P2 (transient pairing with flags bit 10), P6 (M1 method byte) and P11 (<c>X-Apple-HKP</c> required?).
/// Every live test logs the wire exchange to <c>artifacts/live/&lt;date&gt;-&lt;probe&gt;.log</c>.
/// </summary>
public sealed class PairingLiveTests(ITestOutputHelper output)
{
    private static bool LiveEnabled => Environment.GetEnvironmentVariable("AIRPLAY_LIVE") == "1";

    [Fact]
    [Trait("Category", "ShairportLive")]
    public async Task Shairport_transient_pairing_then_encrypted_get_info()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        var host = Environment.GetEnvironmentVariable("SPS_HOST") ?? "127.0.0.1";
        var port = int.Parse(Environment.GetEnvironmentVariable("SPS_PORT") ?? "7000", CultureInfo.InvariantCulture);
        using var log = new LiveLog("shairport-pairing", output);
        await using var connection = await RtspConnection.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), port), new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None);
        var client = new HapPairingClient(log.CreateLogger<HapPairingClient>());
        var session = await client.PairTransientAsync(connection, CancellationToken.None);
        Assert.Equal(64, session.SharedSecret.Length);
        connection.EnableEncryption(session.ControlKeys);
        var info = await connection.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        log.Write($"encrypted GET /info -> {info.StatusCode}, {info.Body.Length} bytes");
        Assert.NotEmpty(info.PlistBody());
    }

    /// <summary>
    /// A password-protected shairport-sync (<c>--password</c>, the 470/PIN path of TEST-RECEIVER.md): transient pairing is
    /// refused (pair_ap uses the password for the transient SRP as well, so M4 carries <c>Error = 2</c>), PIN pair-setup with
    /// the password succeeds with a verified M6 signature, pair-verify completes on the same and on a new connection, and a
    /// wrong password is an authentication failure. shairport-sync 5.5.2 does <b>not</b> switch the connection to
    /// encryption after pair-verify (the cipher set-up in <c>handle_pair_verify</c> is commented out [sps:rtsp.c:1725-1741];
    /// only transient pair-setup M4 activates it [sps:rtsp.c:1810-1833]); observed 2026-10-09: an encrypted request after
    /// verify is answered with nothing ("can't find an RTSP header"). The test therefore checks that the connection
    /// still serves plaintext after verify, which documents the receiver; Apple receivers encrypt (SPEC-control.md 3.7).
    /// Needs <c>SPS_PIN_HOST</c>, <c>SPS_PIN_PORT</c> and <c>SPS_PASSWORD</c> (set by <c>run-live-tests-in-vm.ps1</c> from the
    /// <c>sps-pin</c> service of compose.yaml); returns early without them and says SKIPPED in its output.
    /// </summary>
    [Fact]
    [Trait("Category", "ShairportLive")]
    public async Task Shairport_password_pin_pairing_then_verify()
    {
        var host = Environment.GetEnvironmentVariable("SPS_PIN_HOST");
        var password = Environment.GetEnvironmentVariable("SPS_PASSWORD");
        if (!LiveEnabled || host is null || password is null)
        {
            // xunit 2.9 has no dynamic skip: the early return is reported as passed. run-live-tests-in-vm.ps1 starts the
            // password receiver (compose profile "pin") and sets both variables, and warns when it cannot.
            output.WriteLine($"SKIPPED (returned early, reported as passed): {(LiveEnabled ? "SPS_PIN_HOST or SPS_PASSWORD not set" : "AIRPLAY_LIVE not set")}");
            return;
        }

        var endpoint = new IPEndPoint(IPAddress.Parse(host), int.Parse(Environment.GetEnvironmentVariable("SPS_PIN_PORT") ?? "7000", CultureInfo.InvariantCulture));
        using var log = new LiveLog("shairport-pin-pairing", output);
        var client = new HapPairingClient(log.CreateLogger<HapPairingClient>());
        var controller = ControllerIdentity.Generate();

        await using (var transient = await RtspConnection.ConnectAsync(endpoint, new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None))
        {
            var refused = await Assert.ThrowsAsync<PairingException>(() => client.PairTransientAsync(transient, CancellationToken.None));
            log.Write($"transient against a password receiver: {refused.Failure} TLV {refused.TlvError} status {refused.StatusCode}");
            Assert.Equal(PairingFailure.TransientRefused, refused.Failure);
        }

        await using (var wrong = await RtspConnection.ConnectAsync(endpoint, new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None))
        {
            var started = await client.StartPinPairingAsync(wrong, controller, TlvMethod.PairSetup, CancellationToken.None);
            var rejected = await Assert.ThrowsAsync<PairingException>(() => client.FinishPinPairingAsync(started, password + "0", CancellationToken.None));
            log.Write($"wrong password: {rejected.Failure} TLV {rejected.TlvError}");
            Assert.Equal(PairingFailure.Authentication, rejected.Failure);
        }

        PairingCredentials credentials;
        await using (var setup = await RtspConnection.ConnectAsync(endpoint, new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None))
        {
            var started = await client.StartPinPairingAsync(setup, controller, TlvMethod.PairSetup, CancellationToken.None);
            credentials = await client.FinishPinPairingAsync(started, password, CancellationToken.None);
            log.Write($"PIN pairing ok: accessory id {credentials.AccessoryId}, LTPK {Convert.ToHexString(credentials.AccessoryPublicKey)}");
            var session = await client.VerifyAsync(setup, credentials, CancellationToken.None);
            Assert.Equal(32, session.SharedSecret.Length);
            var info = await setup.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
            log.Write($"verify on the pairing connection ok; plaintext GET /info after verify -> {info.StatusCode} (shairport keeps plaintext)");
        }

        await using (var reconnect = await RtspConnection.ConnectAsync(endpoint, new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None))
        {
            var session = await client.VerifyAsync(reconnect, credentials, CancellationToken.None);
            Assert.Equal(PairingKind.Verified, session.Kind);
            var info = await reconnect.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
            log.Write($"verify on a new connection ok; plaintext GET /info after verify -> {info.StatusCode} (shairport keeps plaintext)");
        }
    }

    /// <summary>P2 + P11 (header present): transient M1-M4 against the SpeakerA, then an encrypted <c>GET /info</c>.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task SpeakerA_P2_transient_pairing_then_encrypted_get_info()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        using var log = new LiveLog("P2", output);
        await using var connection = await ConnectSpeakerAAsync(log);
        var client = new HapPairingClient(log.CreateLogger<HapPairingClient>());
        PairingSession session;
        try
        {
            session = await client.PairTransientAsync(connection, CancellationToken.None);
        }
        catch (PairingException ex)
        {
            log.Write($"P2 result: transient pairing refused: {ex.Failure}, status {ex.StatusCode}, TLV {ex.TlvError}: {ex.Message}");
            throw;
        }

        connection.EnableEncryption(session.ControlKeys);
        var info = await connection.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
        var plist = info.PlistBody();
        log.Write($"P2 result: transient pairing accepted; encrypted GET /info -> {info.StatusCode}, {info.Body.Length} bytes, keys: {string.Join(",", plist.Keys)}");
    }

    /// <summary>P11: transient M1 without <c>X-Apple-HKP</c> on its own connection; records whether M2 still arrives.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task SpeakerA_P11_transient_M1_without_hkp_header()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        using var log = new LiveLog("P11", output);
        await using var connection = await ConnectSpeakerAAsync(log);
        var m1 = new Tlv8Document().AddMethod(TlvMethod.PairSetup).AddState(1).Add(TlvType.Flags, (byte)TlvFlags.Transient).Encode();
        var noHkp = new Dictionary<string, string> { [RtspHeaders.Connection] = "keep-alive" };
        await connection.SendAsync(RtspRequest.WithEmptyBody("POST", HapPairingEndpoints.PinStart, RtspContentTypes.OctetStream, noHkp), CancellationToken.None);
        var response = await connection.SendAsync(RtspRequest.WithBody("POST", HapPairingEndpoints.PairSetup, RtspContentTypes.OctetStream, m1, noHkp), CancellationToken.None);
        log.Write($"P11 result: M1 without X-Apple-HKP -> {response.StatusCode} {response.ReasonPhrase}, {Describe(response)}");
    }

    /// <summary>P6: M1 with method 0 and with method 1 on two connections (no PIN entry follows); compares the two M2.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task SpeakerA_P6_m1_method_byte()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        using var log = new LiveLog("P6", output);
        foreach (var method in new[] { TlvMethod.PairSetup, TlvMethod.PairSetupWithAuth })
        {
            await using var connection = await ConnectSpeakerAAsync(log);
            var m1 = new Tlv8Document().AddMethod(method).AddState(1).Encode();
            var headers = new Dictionary<string, string> { [RtspHeaders.Connection] = "keep-alive", [RtspHeaders.XAppleHkp] = HapPairingEndpoints.HkpRegular };
            var response = await connection.SendAsync(RtspRequest.WithBody("POST", HapPairingEndpoints.PairSetup, RtspContentTypes.OctetStream, m1, headers), CancellationToken.None);
            log.Write($"P6 result: M1 method {(byte)method} -> {response.StatusCode} {response.ReasonPhrase}, {Describe(response)}");
        }
    }

    private static string Describe(RtspResponse response)
    {
        try
        {
            var doc = Tlv8Document.Decode(response.Body.Span);
            return string.Join(" ", doc.Items.Select(i => $"{i.Type}({i.Value.Length})")) + (doc.Error is { } e ? $" Error={e}" : string.Empty);
        }
        catch (FormatException)
        {
            return $"{response.Body.Length} bytes, not TLV8";
        }
    }

    private static async Task<RtspConnection> ConnectSpeakerAAsync(LiveLog log)
    {
        // Only the Test Speaker ABCD may be contacted (live-device rules); never the LG TV or a HomePod.
        var host = Environment.GetEnvironmentVariable("SpeakerA_HOST") ?? "192.0.2.26";
        return await RtspConnection.ConnectAsync(new IPEndPoint(IPAddress.Parse(host), 7000), new RtspConnectionOptions(), log.CreateLogger("rtsp"), CancellationToken.None);
    }

    /// <summary>Writes every log line (incl. the connection's Debug wire log) to the test output and to <c>artifacts/live</c>.</summary>
    private sealed class LiveLog : ILoggerFactory
    {
        private readonly StreamWriter _file;
        private readonly ITestOutputHelper _output;
        private readonly Lock _gate = new();

        public LiveLog(string probe, ITestOutputHelper output)
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
                _output.WriteLine(line);
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public ILogger<T> CreateLogger<T>() => new Logger<T>(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose() => _file.Dispose();

        private static string FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? AppContext.BaseDirectory;
        }

        private sealed class Logger(LiveLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Write($"{DateTimeOffset.Now:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception.Message)}");
        }
    }
}
