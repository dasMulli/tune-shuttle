using System.Globalization;
using System.Net.Http.Json;
using Bridge.Contracts;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Bridge.Tests.Service;

/// <summary>
/// The bridge against the Test Speaker ABCD (WORK-PLAN.md section 4: from wave 3 pairing, SETUP, event channel, volume and
/// TEARDOWN are allowed; audio only as the CLI's quiet tone, which is not the bridge's). The bridge finds the speaker by
/// mDNS, connects it as the single output (transient pairing, session and stream SETUP, volume at level 15), changes the
/// level and disconnects it (TEARDOWN) without ever starting the pacer, so no audio packet is sent. Returns early unless
/// <c>AIRPLAY_LIVE=1</c>; the decrypted wire exchange goes to <c>artifacts/live/&lt;date&gt;-bridge-connect.log</c>.
/// </summary>
public sealed class BridgeLiveTests(ITestOutputHelper output)
{
    /// <summary>The only receiver the live tests may contact (WORK-PLAN.md section 4).</summary>
    public const string SpeakerAName = "Test Speaker ABCD";

    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task Bridge_connects_levels_and_disconnects_the_SpeakerA_without_audio()
    {
        if (Environment.GetEnvironmentVariable("AIRPLAY_LIVE") != "1")
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        using var log = new LiveLogProvider("bridge-connect", output);
        await using var factory = new BridgeFactory(useMdns: true, extraLogging: log);
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(100);

        ReceiverDto? SpeakerA = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (SpeakerA is null && DateTime.UtcNow < deadline)
        {
            var receivers = await client.GetJsonAsync<List<ReceiverDto>>(ApiRoutes.Receivers);
            SpeakerA = receivers.FirstOrDefault(r => r.Name == SpeakerAName && r.Online);
            if (SpeakerA is null)
            {
                await Task.Delay(500);
            }
        }

        if (SpeakerA is null)
        {
            log.Write("The SpeakerA is not on the network; live steps skipped.");
            return;
        }

        log.Write($"found {SpeakerA.Name} {SpeakerA.Id} at {SpeakerA.Address}:{SpeakerA.Port}, pairing {SpeakerA.Pairing}, features {SpeakerA.Capabilities.Features}");

        // The level is set before connecting, so the first SET_PARAMETER volume already is the quiet one.
        await client.SendJsonAsync<ReceiverDto>(HttpMethod.Put, Http.Receiver(ApiRoutes.ReceiverVolume, SpeakerA.Id), new VolumeRequest(15));
        try
        {
            var connected = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Post, Http.Receiver(ApiRoutes.ReceiverConnect, SpeakerA.Id));
            log.Write($"connect: state {connected.State}, error {connected.Error ?? "none"}");
            Assert.Equal(ReceiverConnectionState.Connected, connected.State);
            var outputDto = await client.GetJsonAsync<OutputDto>(ApiRoutes.Output);
            Assert.Equal(SpeakerA.Id, outputDto.ReceiverId);

            await Task.Delay(TimeSpan.FromSeconds(5)); // feedback and the volume poll run meanwhile; no audio (Spotify is off)
            var quieter = await client.SendJsonAsync<ReceiverDto>(HttpMethod.Put, Http.Receiver(ApiRoutes.ReceiverVolume, SpeakerA.Id), new VolumeRequest(12));
            Assert.Equal(12, quieter.Level);
            await Task.Delay(TimeSpan.FromSeconds(2));
            var still = await client.GetJsonAsync<ReceiverDto>(Http.Receiver(ApiRoutes.Receivers + "/{id}", SpeakerA.Id));
            log.Write($"after 7 s: state {still.State}, level {still.Level}");
            Assert.Equal(ReceiverConnectionState.Connected, still.State);

            var diagnostics = await client.GetJsonAsync<DiagnosticsDto>(ApiRoutes.Diagnostics);
            log.Write($"diagnostics: timing {diagnostics.TimingPort}, control {diagnostics.ControlPort}, packets {diagnostics.PacketsSent}");
            Assert.Equal(0, diagnostics.PacketsSent);
        }
        finally
        {
            // Always TEARDOWN what was set up (section 4).
            using var response = await client.PostAsync(Http.Receiver(ApiRoutes.ReceiverDisconnect, SpeakerA.Id), null);
            log.Write($"disconnect: {(int)response.StatusCode} {(await response.Content.ReadFromJsonAsync<ReceiverDto>(Http.Json))?.State}");
        }
    }

    /// <summary>Writes every log line of the bridge (Debug and above) to the live log file and the test output.</summary>
    private sealed class LiveLogProvider : ILoggerProvider
    {
        private readonly StreamWriter _file;
        private readonly ITestOutputHelper _output;
        private readonly Lock _gate = new();

        public LiveLogProvider(string probe, ITestOutputHelper output)
        {
            _output = output;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "AirplayBridge.sln")))
            {
                root = root.Parent;
            }

            var directory = Path.Combine(root?.FullName ?? Path.GetTempPath(), "artifacts", "live");
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
                    // the test already finished (a late background line)
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
            lock (_gate)
            {
                _file.Dispose();
            }
        }

        private sealed class Logger(LiveLogProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner.Write($"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception.Message)}");
                }
            }
        }
    }
}
