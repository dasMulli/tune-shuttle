using System.Buffers.Binary;
using System.Net;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Cli;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tests.Playback;
using Airplay2.Sender.Tests.Sender;
using Airplay2.Sender.Tests.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TestReceiver = Airplay2.FakeReceiver.FakeReceiver;

namespace Airplay2.Sender.Tests.Cli;

/// <summary>
/// The <c>airplay2</c> CLI (WORK-PLAN.md sender-api-cli "Verify"): argument parsing and the <c>--i-know</c> guard (exit 2,
/// no socket opened: the directory, store and sender factories are never called), the probe id rules, the WAV reader and
/// the tone source, and the commands end to end against fake receivers with a mocked directory.
/// </summary>
public sealed class CliTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Theory]
    [InlineData("play", "tone:3", "--to", "Test Speaker ABCD")]
    [InlineData("play", "tone:3", "--to", "Test Speaker ABCD", "--level", "15")]
    [InlineData("play", "does-not-exist.wav", "--to", "A", "--to", "B")]
    [InlineData("probe", "P14", "Test Speaker ABCD")]
    [InlineData("probe", "p7", "Test Speaker ABCD")]
    [InlineData("probe", "P12", "Test Speaker ABCD")]
    [InlineData("probe", "P18", "Test Speaker ABCD")]
    [InlineData("probe", "P19", "Test Speaker ABCD")]
    public async Task Audio_without_i_know_exits_2_and_touches_nothing(params string[] args)
    {
        var cli = new CountingServices();
        var exit = await Program.RunAsync(args, cli.Services, None);
        Assert.Equal(ExitCodes.AudioRefused, exit);
        Assert.Contains("--i-know", cli.ErrorText);
        Assert.Equal(0, cli.DirectoriesCreated);
        Assert.Equal(0, cli.StoresCreated);
        Assert.Equal(0, cli.LoggingCreatedWithFile);
    }

    [Fact]
    public async Task Play_requires_to_and_a_file()
    {
        var cli = new CountingServices();
        Assert.NotEqual(0, await Program.RunAsync(["play", "tone:3", "--i-know"], cli.Services, None));
        Assert.NotEqual(0, await Program.RunAsync(["play", "--to", "x", "--i-know"], cli.Services, None));
        Assert.Equal(0, cli.DirectoriesCreated);
    }

    [Fact]
    public void Play_defaults_level_15_and_latency_77175_and_accepts_repeated_to()
    {
        var root = Program.BuildRootCommand(new CountingServices().Services);
        var parse = root.Parse(["play", "tone:3", "--to", "A", "--to", "B C", "--i-know"]);
        Assert.Empty(parse.Errors);
        Assert.Equal(15, parse.GetValue<int>("--level"));
        Assert.Equal(77175, parse.GetValue<int>("--latency"));
        Assert.True(parse.GetValue<bool>("--i-know"));
        Assert.Equal(["A", "B C"], parse.GetValue<string[]>("--to")!);

        var probe = root.Parse(["probe", "P1", "Test Speaker ABCD", "--i-know", "--seconds", "2"]);
        Assert.Empty(probe.Errors);
        Assert.Equal("P1", probe.GetValue<string>("id"));
        Assert.Equal(2.0, probe.GetValue<double?>("--seconds"));

        Assert.Empty(root.Parse(["-v", "discover", "--seconds", "3"]).Errors);
        Assert.Empty(root.Parse(["discover", "-v"]).Errors);
        Assert.Empty(root.Parse(["volume", "Test Speaker ABCD"]).Errors);
        Assert.Empty(root.Parse(["volume", "Test Speaker ABCD", "20"]).Errors);
        Assert.NotEmpty(root.Parse(["volume", "Test Speaker ABCD", "loud"]).Errors);
    }

    [Theory]
    [InlineData("P1", "P1", ProbeKind.ControlThenAudio)]
    [InlineData("p01", "P1", ProbeKind.ControlThenAudio)]
    [InlineData("P15", "P15", ProbeKind.ControlThenAudio)]
    [InlineData("P2", "P2", ProbeKind.ControlOnly)]
    [InlineData("P21", "P21", ProbeKind.ControlOnly)]
    [InlineData("P17", "P17", ProbeKind.ControlOnly)]
    [InlineData("P14", "P14", ProbeKind.Audio)]
    [InlineData("P16", "P16", ProbeKind.Unavailable)]
    [InlineData("P20", "P20", ProbeKind.Unavailable)]
    public void Probe_ids_normalise_and_classify(string id, string normalised, ProbeKind kind)
    {
        Assert.Equal(normalised, ProbeCommand.NormalizeId(id));
        Assert.Equal(kind, ProbeCommand.KindOf(normalised));
    }

    [Theory]
    [InlineData("P0")]
    [InlineData("P22")]
    [InlineData("Q1")]
    [InlineData("1")]
    public async Task Unknown_probe_ids_fail_without_touching_anything(string id)
    {
        Assert.Null(ProbeCommand.NormalizeId(id));
        var cli = new CountingServices();
        Assert.Equal(ExitCodes.Failure, await Program.RunAsync(["probe", id, "x", "--i-know"], cli.Services, None));
        Assert.Equal(0, cli.DirectoriesCreated);
    }

    [Fact]
    public async Task Unavailable_probes_exit_3_without_touching_anything()
    {
        var cli = new CountingServices();
        Assert.Equal(ExitCodes.Unavailable, await Program.RunAsync(["probe", "P16", "x", "--i-know"], cli.Services, None));
        Assert.Equal(ExitCodes.Unavailable, await Program.RunAsync(["probe", "P20", "x"], cli.Services, None));
        Assert.Equal(0, cli.DirectoriesCreated);
        Assert.Equal(0, cli.StoresCreated);
    }

    [Fact]
    public async Task Play_rejects_bad_levels_tones_and_files_before_resolving_receivers()
    {
        var cli = new CountingServices();
        Assert.Equal(ExitCodes.Failure, await Program.RunAsync(["play", "tone:3", "--to", "x", "--level", "101", "--i-know"], cli.Services, None));
        Assert.Equal(ExitCodes.Failure, await Program.RunAsync(["play", "tone:0", "--to", "x", "--i-know"], cli.Services, None));
        Assert.Equal(ExitCodes.Failure, await Program.RunAsync(["play", "tone:abc", "--to", "x", "--i-know"], cli.Services, None));
        Assert.Equal(ExitCodes.Failure, await Program.RunAsync(["play", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"), "--to", "x", "--i-know"], cli.Services, None));
        Assert.Equal(0, cli.DirectoriesCreated);
    }

    [Fact]
    public async Task Play_with_an_unknown_receiver_fails_before_any_session()
    {
        var cli = new CountingServices();
        Assert.Equal(ExitCodes.Failure, await Program.RunAsync(["play", "tone:1", "--to", "nobody", "--i-know"], cli.Services, None));
        Assert.Equal(1, cli.DirectoriesCreated);
        Assert.Equal(0, cli.StoresCreated);
        Assert.Contains("not found", cli.ErrorText);
    }

    [Fact]
    public void Tone_source_is_the_quiet_test_tone()
    {
        using var tone = PcmSource.Open("tone:3");
        Assert.Equal(3 * 44100, tone.TotalFrames);
        var buffer = new short[44100 * 2 * 4];
        var frames = tone.Read(buffer);
        Assert.Equal(3 * 44100, frames);
        Assert.Equal(0, tone.Read(buffer));
        var peak = buffer.Take(frames * 2).Max(s => Math.Abs((int)s));
        Assert.InRange(peak, 2000, 2070); // -24 dBFS = 32767 * 0.0631 = 2068
        Assert.Throws<FormatException>(() => PcmSource.Open("tone:-1"));
        Assert.Throws<FormatException>(() => PcmSource.Open("tone:9999"));
    }

    [Fact]
    public void Wav_reader_round_trips_and_reads_in_odd_chunks()
    {
        var pcm = TestPcm.Sweep(10_001);
        using var stream = new MemoryStream();
        WavReader.Write(stream, pcm);
        Assert.Equal(44 + (pcm.Length * 2), stream.Length);
        stream.Position = 0;
        using var reader = WavReader.Open(stream, "x");
        Assert.Equal(10_001, reader.TotalFrames);
        var all = new List<short>();
        var buffer = new short[2 * 333];
        int frames;
        while ((frames = reader.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, frames * 2));
        }

        Assert.Equal(pcm, all);
    }

    [Fact]
    public void Wav_reader_skips_unknown_chunks_accepts_extensible_pcm_and_streamed_sizes()
    {
        var pcm = TestPcm.Sweep(1000);
        var bytes = BuildWav(pcm, extensible: true, extraChunk: true, dataSize: uint.MaxValue);
        using var reader = WavReader.Open(new MemoryStream(bytes), "x");
        Assert.Equal(-1, reader.TotalFrames);
        var buffer = new short[pcm.Length + 64];
        var frames = reader.Read(buffer);
        Assert.Equal(1000, frames);
        Assert.Equal(pcm, buffer[..pcm.Length]);
    }

    [Theory]
    [InlineData(48000, 2, 16)]
    [InlineData(44100, 1, 16)]
    [InlineData(44100, 2, 24)]
    public void Wav_reader_refuses_other_formats(int rate, int channels, int bits)
    {
        var bytes = BuildWav(TestPcm.Sweep(10), rate: rate, channels: channels, bits: bits);
        var ex = Assert.Throws<FormatException>(() => WavReader.Open(new MemoryStream(bytes), "x"));
        Assert.Contains("44100", ex.Message);
        Assert.Throws<FormatException>(() => WavReader.Open(new MemoryStream("RIFF\0\0\0\0AVI "u8.ToArray()), "x"));
    }

    [Fact]
    public async Task Play_end_to_end_plays_a_wav_to_two_fakes_through_the_cli()
    {
        await using var a = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var b = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake B" });
        var pcm = TestPcm.Sweep(44100);
        var path = Path.Combine(Path.GetTempPath(), $"airplay2-cli-{Guid.NewGuid():N}.wav");
        await using (var file = File.Create(path))
        {
            WavReader.Write(file, pcm);
        }

        try
        {
            var cli = new CountingServices([SessionTestSupport.Describe(a), SessionTestSupport.Describe(b)]);
            var run = Program.RunAsync(["play", path, "--to", "Fake A", "--to", "fake b", "--i-know", "--no-metadata", "--level", "12"], cli.Services, None);
            var packets = (pcm.Length / 2 + 351) / 352;
            var eventsA = await AirplaySenderTests.CollectAudioAsync(a, packets);
            var eventsB = await AirplaySenderTests.CollectAudioAsync(b, packets);
            Assert.Equal(ExitCodes.Ok, await run.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(pcm, eventsA.OfType<FakeReceiverEvent.AudioPacket>().SelectMany(p => p.Pcm).Take(pcm.Length).ToArray());
            Assert.Equal(pcm, eventsB.OfType<FakeReceiverEvent.AudioPacket>().SelectMany(p => p.Pcm).Take(pcm.Length).ToArray());
            Assert.Equal(VolumeMapping.LevelToDb(12), eventsA.OfType<FakeReceiverEvent.Volume>().First().Db, 6);
            // The end of the file is not turned into a pause: no FLUSH between the last packet and the TEARDOWN.
            Assert.DoesNotContain(await UntilTeardownAsync(a), e => e is FakeReceiverEvent.Flush);
            Assert.DoesNotContain(await UntilTeardownAsync(b), e => e is FakeReceiverEvent.Flush);
            Assert.Contains("Playing", cli.OutText);
            Assert.Contains("Done after", cli.OutText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<List<FakeReceiverEvent>> UntilTeardownAsync(TestReceiver fake)
    {
        var seen = new List<FakeReceiverEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var e = await fake.Events.ReadAsync(cts.Token);
            seen.Add(e);
            if (e is FakeReceiverEvent.Teardown)
            {
                return seen;
            }
        }
    }

    [Fact]
    public async Task Volume_sets_the_level_in_a_session_without_audio_and_tears_down()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake A", SendTimingRequests = false });
        var cli = new CountingServices([SessionTestSupport.Describe(fake)]);
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["volume", "Fake A", "30"], cli.Services, None));
        var events = await Fake.FakeReceiverHarness.DrainAsync(fake, TimeSpan.FromMilliseconds(300));
        Assert.Equal(VolumeMapping.LevelToDb(30), events.OfType<FakeReceiverEvent.Volume>().Single().Db, 6);
        Assert.DoesNotContain(events, e => e is FakeReceiverEvent.AudioPacket);
        Assert.Contains(events, e => e is FakeReceiverEvent.Teardown { Session: true });
        Assert.Contains("set level 30", cli.OutText);
    }

    [Fact]
    public async Task Info_pair_and_discover_work_against_a_fake()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        var cli = new CountingServices([SessionTestSupport.Describe(fake)]);
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["info", "127.0.0.1", "--port", fake.ControlEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)], cli.Services, None));
        Assert.Contains("model:", cli.OutText);
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["info", "Fake A", "--qualifier"], cli.Services, None));
        Assert.Contains("txtAirPlay", cli.OutText);
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["pair", "Fake A"], cli.Services, None));
        Assert.Contains("Transient", cli.OutText);
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["discover", "--seconds", "1"], cli.Services, None));
        Assert.Contains("pairing   Transient", cli.OutText);
        Assert.True(cli.OutText.Contains("timing    NTP", StringComparison.Ordinal), cli.OutText);
    }

    [Fact]
    public async Task Pair_runs_the_pin_flow_with_the_given_pin_and_stores_the_pairing()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Pin Fake", StatusFlags = 0x404 | 0x200, AllowTransientPairing = false, Pin = "2468" });
        var cli = new CountingServices([SessionTestSupport.Describe(fake)]);
        Assert.Equal(ExitCodes.Failure, await Program.RunAsync(["pair", "Pin Fake", "--pin", "1111"], cli.Services, None));
        Assert.Empty(cli.Store.Pairings);
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["pair", "Pin Fake", "--pin", "2468"], cli.Services, None));
        Assert.Single(cli.Store.Pairings);
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["pair", "Pin Fake"], cli.Services, None)); // verifies the stored pairing
        Assert.Contains("stored pairing verified", cli.OutText);
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["pair", "Pin Fake", "--forget"], cli.Services, None));
        Assert.Empty(cli.Store.Pairings);
    }

    [Fact]
    public async Task A_control_probe_runs_against_a_fake_and_writes_its_log()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake A", SendTimingRequests = false });
        var logs = Path.Combine(Path.GetTempPath(), $"airplay2-probe-{Guid.NewGuid():N}");
        var cli = new CountingServices([SessionTestSupport.Describe(fake)]) { LiveLogDirectory = logs };
        try
        {
            Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["probe", "P10", "Fake A"], cli.Services, None));
            Assert.Contains("RESULT P10:", cli.OutText);
            var file = Assert.Single(Directory.GetFiles(logs));
            Assert.EndsWith("-P10.log", file);
            Assert.Contains("GET /info", await File.ReadAllTextAsync(file));
        }
        finally
        {
            if (Directory.Exists(logs))
            {
                Directory.Delete(logs, recursive: true);
            }
        }
    }

    [Fact]
    public void Ctrl_c_waits_for_the_teardowns_instead_of_the_2_s_default()
    {
        var configuration = Program.CreateInvocationConfiguration(new CountingServices().Services);
        Assert.NotNull(configuration.ProcessTerminationTimeout);
        Assert.True(configuration.ProcessTerminationTimeout >= TimeSpan.FromSeconds(30), "covers the 10 s TEARDOWN bound per member and a slow receiver");
    }

    [Fact]
    public async Task Interrupting_play_tears_down_and_exits_130()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        var cli = new CountingServices([SessionTestSupport.Describe(fake)]);
        using var cts = new CancellationTokenSource();
        var run = Program.RunAsync(["play", "tone:3", "--to", "Fake A", "--i-know", "--no-metadata"], cli.Services, cts.Token);
        await AirplaySenderTests.CollectAudioAsync(fake, 10);
        await cts.CancelAsync();
        Assert.Equal(ExitCodes.Interrupted, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        await AirplaySenderTests.FirstAsync<FakeReceiverEvent.Teardown>(fake);
        Assert.Contains("Interrupted", cli.ErrorText);
    }

    [Fact]
    public async Task Volume_on_a_ptp_receiver_announces_the_running_grandmaster_and_sends_setpeers_before_the_stream_setup()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake A", SendTimingRequests = false });
        var receiver = SessionTestSupport.Describe(fake);
        var cli = new CountingServices([receiver])
        {
            SenderOptions = new AirplaySenderOptions
            {
                LocalAddress = IPAddress.Loopback,
                Quirks = new Dictionary<string, ReceiverQuirks> { [receiver.Id] = new() { TimingProtocol = TimingProtocol.Ptp } },
                Ptp = new PtpMasterOptions { BindAddress = IPAddress.Loopback, EventPort = 0, GeneralPort = 0 },
            },
        };
        Assert.Equal(ExitCodes.Ok, await Program.RunAsync(["volume", "Fake A", "25"], cli.Services, None));
        var events = await Fake.FakeReceiverHarness.DrainAsync(fake, TimeSpan.FromMilliseconds(300));
        var setup = events.OfType<FakeReceiverEvent.SessionSetup>().Single().Body;
        Assert.Equal("PTP", setup.GetString("timingProtocol"));
        Assert.NotEqual(0, setup.GetDictionary("timingPeerInfo")!.GetInt64("ClockID"));
        var peers = events.FindIndex(e => e is FakeReceiverEvent.Peers);
        var stream = events.FindIndex(e => e is FakeReceiverEvent.StreamSetup);
        Assert.True(peers >= 0 && peers < stream, "D5: SETPEERS (PTP only) before the stream SETUP");
        Assert.Equal(VolumeMapping.LevelToDb(25), events.OfType<FakeReceiverEvent.Volume>().Single().Db, 6);
        Assert.Contains(events, e => e is FakeReceiverEvent.Teardown { Session: true });
    }

    [Fact]
    public async Task A_probe_against_an_unknown_receiver_leaves_no_log_file_and_no_store()
    {
        var logs = Path.Combine(Path.GetTempPath(), $"airplay2-probe-{Guid.NewGuid():N}");
        var cli = new CountingServices() { LiveLogDirectory = logs };
        Assert.Equal(ExitCodes.Failure, await Program.RunAsync(["probe", "P10", "nobody"], cli.Services, None));
        Assert.Contains("not found", cli.ErrorText);
        Assert.False(Directory.Exists(logs));
        Assert.Equal(0, cli.LoggingCreatedWithFile);
        Assert.Equal(0, cli.StoresCreated);
    }

    [Fact]
    public async Task Discover_shows_the_configured_quirk_timing()
    {
        await using var fake = await SessionTestSupport.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        var receiver = SessionTestSupport.Describe(fake);
        var options = new AirplaySenderOptions { Quirks = new Dictionary<string, ReceiverQuirks> { [receiver.Id] = new() { TimingProtocol = TimingProtocol.Ptp } } };
        Assert.Contains("timing    PTP (receiver quirk", CliCommands.Describe(receiver, options), StringComparison.Ordinal);
        Assert.Contains("timing    NTP", CliCommands.Describe(receiver), StringComparison.Ordinal);
    }

    private static byte[] BuildWav(short[] pcm, int rate = 44100, int channels = 2, int bits = 16, bool extensible = false, bool extraChunk = false, uint? dataSize = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(0u);
        w.Write("WAVE"u8);
        if (extraChunk)
        {
            w.Write("LIST"u8);
            w.Write(3u);
            w.Write(new byte[] { 1, 2, 3, 0 }); // odd size + pad byte
        }

        w.Write("fmt "u8);
        w.Write(extensible ? 40u : 16u);
        w.Write((ushort)(extensible ? 0xFFFE : 1));
        w.Write((ushort)channels);
        w.Write((uint)rate);
        w.Write((uint)(rate * channels * bits / 8));
        w.Write((ushort)(channels * bits / 8));
        w.Write((ushort)bits);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)bits);
            w.Write(3u);
            w.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        }

        w.Write("data"u8);
        w.Write(dataSize ?? (uint)(pcm.Length * 2));
        Span<byte> b = stackalloc byte[2];
        foreach (var s in pcm)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b, s);
            w.Write(b);
        }

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>CLI services over fakes: counts every factory call so a refused command can be shown to have touched nothing.</summary>
    private sealed class CountingServices
    {
        private readonly StringWriter _out = new();
        private readonly StringWriter _error = new();
        private readonly List<DiscoveredReceiver> _receivers;

        public CountingServices(IEnumerable<DiscoveredReceiver>? receivers = null)
        {
            _receivers = [.. receivers ?? []];
        }

        public InMemoryPairingStore Store { get; } = new();

        public int DirectoriesCreated { get; private set; }

        public int StoresCreated { get; private set; }

        public int LoggingCreatedWithFile { get; private set; }

        public string? LiveLogDirectory { get; init; }

        public AirplaySenderOptions SenderOptions { get; init; } = new() { LocalAddress = IPAddress.Loopback };

        public string OutText => _out.ToString();

        public string ErrorText => _error.ToString();

        public CliServices Services => new()
        {
            Out = _out,
            Error = _error,
            Prompt = (_, _) => Task.FromResult<string?>(null),
            CreateLogging = options =>
            {
                if (options.LogFile is not null)
                {
                    LoggingCreatedWithFile++;
                    return new TeeLoggerFactory(NullLoggerFactory.Instance, options.LogFile);
                }

                return NullLoggerFactory.Instance;
            },
            CreateDirectory = _ =>
            {
                DirectoriesCreated++;
                return new StaticReceiverDirectory(_receivers);
            },
            CreatePairingStore = _ =>
            {
                StoresCreated++;
                return Store;
            },
            SenderOptions = SenderOptions,
            LookupTimeout = TimeSpan.FromMilliseconds(100),
            LiveLogDirectory = LiveLogDirectory,
        };
    }
}
