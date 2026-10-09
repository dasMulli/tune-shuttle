// Tests for src/main.rs (Program.cs + Setup.cs). Upstream has no unit tests for main.rs; these cover the option
// handling of `get_setup` (values, defaults, environment fallbacks and the exact upstream error output),
// `parse_file_size`, `device_id` and the `RUST_LOG`-style log filter.
using System.Net;
using Librespot.Cli;
using Librespot.Core;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Librespot.Tests.Cli;

public class MainTests
{
    private sealed record Result(Setup? Setup, int? ExitCode, string Stdout, string Stderr);

    private static Result Run(string[] args, Dictionary<string, string>? env = null)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        try
        {
            var setup = Setup.Get(new SetupContext
            {
                Program = "librespot",
                Args = args,
                Environment = env ?? [],
                Stdout = stdout,
                Stderr = stderr,
            });
            return new Result(setup, null, stdout.ToString(), stderr.ToString());
        }
        catch (CliExitException e)
        {
            return new Result(null, e.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }

    private static Setup Ok(params string[] args)
    {
        var r = Run(args);
        Assert.Null(r.ExitCode);
        return r.Setup!;
    }

    private static string Exit1(params string[] args)
    {
        var r = Run(args);
        Assert.Equal(1, r.ExitCode);
        return r.Stdout;
    }

    [Fact]
    public void HelpOption()
    {
        foreach (var args in new[] { new[] { "-h" }, ["--help"], ["-n", "x", "--help"] })
        {
            var r = Run(args);
            Assert.Equal(0, r.ExitCode);
            Assert.Equal(Program.Usage("librespot", CliOptions.Build()) + "\n", r.Stdout);
        }
    }

    [Fact]
    public void HelpText()
    {
        var usage = Program.Usage("librespot", CliOptions.Build());
        var expectedStart =
            $"{Program.GetVersionString()}\n\n" +
            "An open source client library for Spotify, with support for Spotify Connect\n\n" +
            "https://github.com/librespot-org/librespot\n\n" +
            "Usage: librespot [<Options>]\n\n" +
            "Options:\n" +
            "    -h, --help          Print this help menu.\n" +
            "    -V, --version       Display librespot version string.\n";
        Assert.StartsWith(expectedStart, usage);
    }

    [Fact]
    public void HelpFromEnvironment()
    {
        var r = Run([], new() { ["LIBRESPOT_HELP"] = "" });
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Usage: librespot [<Options>]", r.Stdout);
    }

    [Fact]
    public void VersionFlag()
    {
        var r = Run(["-V"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(Program.GetVersionString() + "\n", r.Stdout);
        Assert.Matches(@"^librespot 0\.8\.0 \w+ \(Built on .+, Build ID: .+, Profile: (debug|release)\)$", Program.GetVersionString());
    }

    [Fact]
    public void ParseErrorPrintsErrorAndUsage()
    {
        var r = Run(["--nope"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("Error parsing command line options: Unrecognized option: 'nope'\n", r.Stderr);
        Assert.Equal("\n" + Program.Usage("librespot", CliOptions.Build()) + "\n", r.Stdout);
    }

    [Fact]
    public void Defaults()
    {
        var s = Ok();
        Assert.Equal(AudioFormat.S16, s.Format);
        Assert.Same(AudioBackends.Find(null), s.Backend);
        Assert.Null(s.Device);
        Assert.NotNull(s.Cache); // upstream `Cache::new(None, None, None, None)` succeeds
        Assert.Null(s.Credentials);
        Assert.False(s.EnableOAuth);
        Assert.False(s.EnableDeviceAuth);
        Assert.Equal((ushort)5588, s.OAuthPort);
        Assert.Equal((ushort)0, s.ZeroconfPort);
        Assert.Empty(s.ZeroconfIp);
        Assert.NotNull(s.ZeroconfBackend);
        Assert.Null(s.PlayerEventProgram);
        Assert.False(s.EmitSinkEvents);

        Assert.Equal("librespot", s.ConnectConfig.Name);
        Assert.Equal(DeviceType.Speaker, s.ConnectConfig.DeviceType);
        Assert.False(s.ConnectConfig.IsGroup);
        Assert.Equal((ushort)32767, s.ConnectConfig.InitialVolume);
        Assert.False(s.ConnectConfig.DisableVolume);
        Assert.Equal((ushort)64, s.ConnectConfig.VolumeSteps);
        Assert.False(s.ConnectConfig.EmitSetQueueEvents);

        Assert.Equal(Program.DeviceId("librespot"), s.SessionConfig.DeviceId);
        Assert.Null(s.SessionConfig.Proxy);
        Assert.Null(s.SessionConfig.ApPort);
        Assert.Null(s.SessionConfig.Autoplay);

        Assert.Equal(new VolumeCtrl.Log(60.0), s.MixerConfig.VolumeCtrl);
        Assert.Equal("default", s.MixerConfig.Device);
        Assert.Equal("PCM", s.MixerConfig.Control);
        Assert.Equal(0u, s.MixerConfig.Index);

        var p = s.PlayerConfig;
        var d = new PlayerConfig();
        Assert.Equal(Bitrate.Bitrate160, p.Bitrate);
        Assert.True(p.Gapless);
        Assert.False(p.Passthrough);
        Assert.False(p.Normalisation);
        Assert.Equal(d.NormalisationAttackCf, p.NormalisationAttackCf);
        Assert.Equal(d.NormalisationReleaseCf, p.NormalisationReleaseCf);
        Assert.Equal("tpdf", p.Ditherer!().Name);
        Assert.Null(p.PositionUpdateInterval);
        Assert.Empty(p.LocalFileDirectories);
    }

    [Fact]
    public void DeviceIdIsSha1Hex()
    {
        // echo -n librespot | sha1sum
        Assert.Equal("7c28ab8a5c9512e4266ac7cb756312c82ee43d7e", Program.DeviceId("librespot"));
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", Program.DeviceId(""));
    }

    [Fact]
    public void NameFromCommandLineAndEnvironment()
    {
        Assert.Equal("Kitchen", Ok("--name", "Kitchen").ConnectConfig.Name);
        Assert.Equal(Program.DeviceId("Kitchen"), Ok("-n", "Kitchen").SessionConfig.DeviceId);

        var env = new Dictionary<string, string> { ["LIBRESPOT_NAME"] = "Env" };
        Assert.Equal("Env", Run([], env).Setup!.ConnectConfig.Name);
        Assert.Equal("Cli", Run(["-n", "Cli"], env).Setup!.ConnectConfig.Name);
    }

    [Fact]
    public void EnvironmentOnlyMapsLongNames()
    {
        var env = new Dictionary<string, string>
        {
            ["LIBRESPOT_N"] = "short",            // single character: ignored
            ["LIBRESPOT_DISABLE_GAPLESS"] = "",   // flag
            ["LIBRESPOT_DEVICE_TYPE"] = "tv",     // '_' -> '-'
            ["LIBRESPOT_LIBRESPOT_GROUP"] = "1",  // prefix stripped repeatedly
            ["OTHER_NAME"] = "x",
        };
        var s = Run([], env).Setup!;
        Assert.Equal("librespot", s.ConnectConfig.Name);
        Assert.False(s.PlayerConfig.Gapless);
        Assert.Equal(DeviceType.Tv, s.ConnectConfig.DeviceType);
        Assert.True(s.ConnectConfig.IsGroup);
    }

    [Fact]
    public void EmptyStrings()
    {
        Assert.Equal(1, Run(["--name="]).ExitCode);
        Assert.Equal(1, Run(["--device="]).ExitCode);
        Assert.Equal(1, Run(["--access-token="]).ExitCode);
        Assert.Equal(1, Run(["--username="]).ExitCode);
    }

    [Fact]
    public void AccessToken()
    {
        var s = Ok("-k", "token");
        Assert.Equal(Credentials.WithAccessToken("token"), s.Credentials);
    }

    [Fact]
    public void PasswordIsRejected()
    {
        Assert.Equal(1, Run(["-u", "user", "-p", "pass"]).ExitCode);
        Assert.Null(Ok("-u", "user").Credentials);
    }

    [Fact]
    public void BackendOption()
    {
        Assert.Same(AudioBackends.Find("pipe"), Ok("--backend", "pipe").Backend);

        var r = Run(["-B", "nope"]);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("Available backends: \n- naudio (default)\n- pipe\n- subprocess\n", r.Stdout);

        r = Run(["-B", "?"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("Available backends: \n- naudio (default)\n- pipe\n- subprocess\n", r.Stdout);
    }

    [Fact]
    public void FormatOption()
    {
        Assert.Equal(AudioFormat.S24_3, Ok("--format", "s24_3").Format);
        Assert.Equal("Valid `--format` / `-f` values: F64, F32, S32, S24, S24_3, S16\nDefault: S16\n", Exit1("-f", "S8"));
    }

    [Fact]
    public void DitherOption()
    {
        Assert.Null(Ok("-f", "F32").PlayerConfig.Ditherer);
        Assert.Null(Ok("-f", "S32").PlayerConfig.Ditherer);
        Assert.Equal("tpdf", Ok("-f", "S24").PlayerConfig.Ditherer!().Name);
        Assert.Null(Ok("--dither", "none").PlayerConfig.Ditherer);
        Assert.Null(Ok("-f", "F64", "--dither", "none").PlayerConfig.Ditherer);
        Assert.Equal("gpdf", Ok("-D", "gpdf").PlayerConfig.Ditherer!().Name);
        Assert.Equal("tpdf_hp", Ok("-f", "S32", "-D", "tpdf_hp").PlayerConfig.Ditherer!().Name);

        // Dithering is not available with float formats.
        Assert.Equal(string.Empty, Exit1("-f", "F32", "-D", "tpdf"));

        Assert.Equal(
            "Valid `--dither` / `-D` values: none, gpdf, tpdf, tpdf_hp for formats S16, S24, S24_3, S32, none for formats F32, F64\n" +
            "Default: tpdf for formats S16, S24, S24_3 and none for formats S32, F32, F64\n",
            Exit1("-D", "rpdf"));
    }

    [Fact]
    public void BitrateOption()
    {
        Assert.Equal(Bitrate.Bitrate320, Ok("-b", "320").PlayerConfig.Bitrate);
        Assert.Equal("Valid `--bitrate` / `-b` values: 96, 160, 320\nDefault: 160\n", Exit1("--bitrate", "999"));
    }

    [Fact]
    public void InitialVolumeOption()
    {
        Assert.Equal((ushort)65535, Ok("--initial-volume", "100").ConnectConfig.InitialVolume);
        Assert.Equal((ushort)0, Ok("-R", "0").ConnectConfig.InitialVolume);
        Assert.Equal((ushort)32767, Ok("-R", "+50").ConnectConfig.InitialVolume);
        Assert.Equal((ushort)655, Ok("-R", "1").ConnectConfig.InitialVolume);

        const string invalid = "Valid `--initial-volume` / `-R` values: 0 - 100\nDefault: 32767\n";
        Assert.Equal(invalid, Exit1("-R", "101"));
        Assert.Equal(invalid, Exit1("-R", "-1"));
        Assert.Equal(invalid, Exit1("-R", " 5"));
        Assert.Equal(invalid, Exit1("-R", "5.0"));
    }

    [Fact]
    public void VolumeControlOption()
    {
        Assert.Equal(new VolumeCtrl.Cubic(30.0), Ok("-E", "cubic", "-e", "30").MixerConfig.VolumeCtrl);
        Assert.Equal(new VolumeCtrl.Log(42.5), Ok("--volume-range", "42.5").MixerConfig.VolumeCtrl);
        Assert.IsType<VolumeCtrl.Linear>(Ok("-E", "LINEAR").MixerConfig.VolumeCtrl);

        var s = Ok("--volume-ctrl", "fixed");
        Assert.IsType<VolumeCtrl.Fixed>(s.MixerConfig.VolumeCtrl);
        Assert.True(s.ConnectConfig.DisableVolume);

        Assert.Equal("Valid `--volume-ctrl` / `-E` values: cubic, fixed, linear, log\nDefault: log\n", Exit1("-E", "loud"));
        Assert.Equal("Valid `--volume-range` / `-e` values: 0 - 100\nDefault: 60\n", Exit1("-e", "100.1"));
        Assert.Equal("Valid `--volume-range` / `-e` values: 0 - 100\nDefault: 60\n", Exit1("-e", "nan"));
    }

    [Fact]
    public void VolumeStepsOption()
    {
        Assert.Equal((ushort)100, Ok("--volume-steps", "100").ConnectConfig.VolumeSteps);
        Assert.Equal(
            "Valid `--volume-steps` values: a positive whole number <= 65535\nDefault: 64\n",
            Exit1("--volume-steps", "65536"));
    }

    [Fact]
    public void DeviceTypeAndGroupOption()
    {
        var s = Ok("-F", "AVR", "--group");
        Assert.Equal(DeviceType.Avr, s.ConnectConfig.DeviceType);
        Assert.True(s.ConnectConfig.IsGroup);

        Assert.Equal(
            "Valid `--device-type` / `-F` values: computer, tablet, smartphone, speaker, tv, avr, stb, audiodongle, " +
            "gameconsole, castaudio, castvideo, automobile, smartwatch, chromebook, carthing\nDefault: Speaker\n",
            Exit1("-F", "toaster"));
    }

    [Fact]
    public void NormalisationOption()
    {
        // Ignored without --enable-volume-normalisation.
        var s = Ok("--normalisation-pregain", "3");
        Assert.False(s.PlayerConfig.Normalisation);
        Assert.Equal(0.0, s.PlayerConfig.NormalisationPregainDb);

        s = Ok("-N", "-X", "basic", "-W", "album", "-Y", "-3.5", "-Z", "-1", "-U", "10", "-y", "200", "-w", "2");
        var p = s.PlayerConfig;
        Assert.True(p.Normalisation);
        Assert.Equal(NormalisationMethod.Basic, p.NormalisationMethod);
        Assert.Equal(NormalisationType.Album, p.NormalisationType);
        Assert.Equal(-3.5, p.NormalisationPregainDb);
        Assert.Equal(-1.0, p.NormalisationThresholdDbfs);
        Assert.Equal(Player.DurationToCoefficient(TimeSpan.FromMilliseconds(10)), p.NormalisationAttackCf);
        Assert.Equal(Player.DurationToCoefficient(TimeSpan.FromMilliseconds(200)), p.NormalisationReleaseCf);
        Assert.Equal(2.0, p.NormalisationKneeDb);

        Assert.Equal("Valid `--normalisation-method` / `-X` values: basic, dynamic\nDefault: Dynamic\n", Exit1("-N", "-X", "x"));
        Assert.Equal("Valid `--normalisation-gain-type` / `-W` values: track, album, auto\nDefault: Auto\n", Exit1("-N", "-W", "x"));
        Assert.Equal("Valid `--normalisation-pregain` / `-Y` values: -10 - 10\nDefault: 0\n", Exit1("-N", "-Y", "11"));
        Assert.Equal("Valid `--normalisation-threshold` / `-Z` values: -10 - 0\nDefault: -2\n", Exit1("-N", "-Z", "1"));
        Assert.Equal("Valid `--normalisation-attack` / `-U` values: 1 - 500\nDefault: 5\n", Exit1("-N", "-U", "0"));
        Assert.Equal("Valid `--normalisation-release` / `-y` values: 1 - 1000\nDefault: 100\n", Exit1("-N", "-y", "1001"));
        Assert.Equal("Valid `--normalisation-knee` / `-w` values: 0 - 10\nDefault: 5\n", Exit1("-N", "-w", "-1"));
    }

    [Fact]
    public void PortsOption()
    {
        Assert.Equal((ushort)1234, Ok("-z", "1234").ZeroconfPort);
        Assert.Equal("Valid `--zeroconf-port` / `-z` values: 1 - 65535\n", Exit1("-z", "0"));

        Assert.Equal((ushort)4070, Ok("--ap-port", "4070").SessionConfig.ApPort);
        Assert.Equal("Valid `--ap-port` / `-a` values: 1 - 65535\n", Exit1("-a", "65536"));

        Assert.Equal((ushort)8080, Ok("-K", "8080").OAuthPort);
        Assert.Null(Ok("-K", "0").OAuthPort);
        Assert.Equal("Valid `--oauth-port` / `-K` values: 1 - 65535\n", Exit1("-K", "x"));
    }

    [Fact]
    public void DisableDiscoveryOption()
    {
        // Credentials are required if discovery and oauth login are disabled.
        Assert.Equal(1, Run(["-O"]).ExitCode);

        var s = Ok("-O", "-k", "token", "-z", "1234", "--zeroconf-backend", "nope");
        Assert.Null(s.ZeroconfBackend);
        Assert.Equal((ushort)0, s.ZeroconfPort);

        Assert.Null(Ok("-O", "--enable-oauth").ZeroconfBackend);
        Assert.Null(Ok("-O", "--enable-device-auth").ZeroconfBackend);
    }

    [Fact]
    public void OAuthAndDeviceAuthAreExclusive()
    {
        Assert.Equal(1, Run(["-j", "--enable-device-auth"]).ExitCode);
        Assert.True(Ok("-j").EnableOAuth);
        Assert.True(Ok("--enable-device-auth").EnableDeviceAuth);
    }

    [Fact]
    public void ZeroconfBackendOption()
    {
        Assert.NotNull(Ok("--zeroconf-backend", "libmdns").ZeroconfBackend);
        Assert.Equal("Valid `--zeroconf-backend` values: libmdns\nDefault: libmdns\n", Exit1("--zeroconf-backend", "avahi"));
        Assert.Equal("Valid `--zeroconf-backend` values: libmdns\nDefault: libmdns\n", Exit1("--zeroconf-backend", "bonjour"));
    }

    [Fact]
    public void ZeroconfInterfaceOption()
    {
        var s = Ok("-i", "198.51.100.2, ::1");
        Assert.Equal([IPAddress.Parse("198.51.100.2"), IPAddress.IPv6Loopback], s.ZeroconfIp);

        Assert.Equal("Valid `--zeroconf-interface` / `-i` values: IPv4 and IPv6 addresses\n", Exit1("-i", "1.2"));
        Assert.Equal("Valid `--zeroconf-interface` / `-i` values: IPv4 and IPv6 addresses\n", Exit1("-i", "10.0.0.1,01.2.3.4"));
        Assert.Equal("Valid `--zeroconf-interface` / `-i` values: IPv4 and IPv6 addresses\n", Exit1("-i", "fe80::1%3"));
    }

    [Fact]
    public void AutoplayOption()
    {
        Assert.True(Ok("-A", "on").SessionConfig.Autoplay);
        Assert.False(Ok("--autoplay", "off").SessionConfig.Autoplay);
        Assert.Equal("Valid `--autoplay` / `-A` values: on, off\n", Exit1("-A", "ON"));
    }

    [Fact]
    public void ProxyOption()
    {
        Assert.Equal(new Uri("http://proxy:8080"), Ok("--proxy", "http://proxy:8080").SessionConfig.Proxy);
        Assert.Equal(new Uri("https://proxy"), Ok("-x", "https://proxy").SessionConfig.Proxy);
        Assert.Equal(1, Run(["-x", "proxy:8080:x"]).ExitCode);
        Assert.Equal(1, Run(["-x", "not a url"]).ExitCode);

        var env = new Dictionary<string, string> { ["http_proxy"] = "http://env:3128" };
        Assert.Equal(new Uri("http://env:3128"), Run([], env).Setup!.SessionConfig.Proxy);
        Assert.Equal(new Uri("http://cli:1"), Run(["-x", "http://cli:1"], env).Setup!.SessionConfig.Proxy);
    }

    [Fact]
    public void FlagsOption()
    {
        var s = Ok("-g", "-P", "-Q", "-o", "notify-send x");
        Assert.False(s.PlayerConfig.Gapless);
        Assert.True(s.PlayerConfig.Passthrough);
        Assert.True(s.EmitSinkEvents);
        Assert.Equal("notify-send x", s.PlayerEventProgram);
    }

    [Fact]
    public void LocalFileDirectoriesOption()
    {
        Assert.Equal(["a", "b"], Ok("-l", "a", "--local-file-dir", "b").PlayerConfig.LocalFileDirectories);

        // Command line only: no environment variable fallback upstream.
        var env = new Dictionary<string, string> { ["LIBRESPOT_LOCAL_FILE_DIR"] = "c" };
        Assert.Empty(Run([], env).Setup!.PlayerConfig.LocalFileDirectories);
    }

    [Fact]
    public void TmpAndCacheDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "librespot-cli-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tmp = Path.Combine(root, "tmp");
            var cache = Path.Combine(root, "cache");

            var s = Ok("--tmp", tmp, "-c", cache, "-M", "1K");
            Assert.Equal(tmp, s.SessionConfig.TmpDir);
            Assert.True(Directory.Exists(tmp));
            Assert.NotNull(s.Cache);
            Assert.True(Directory.Exists(Path.Combine(cache, "files")));
            Assert.Null(s.Credentials);

            Assert.Equal(string.Empty, Exit1("-c", cache, "-M", "1X"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("1", 1UL)]
    [InlineData("0", 0UL)]
    [InlineData("1K", 1000UL)]
    [InlineData("1k", 1000UL)]
    [InlineData("1Ki", 1024UL)]
    [InlineData("2ki", 2048UL)]
    [InlineData("1KI", 1024UL)]
    [InlineData("1.5M", 1500000UL)]
    [InlineData("16G", 16000000000UL)]
    [InlineData("16Gi", 17179869184UL)]
    [InlineData("1T", 1000000000000UL)]
    [InlineData("1P", 1000000000000000UL)]
    [InlineData("1E", 1000000000000000000UL)]
    [InlineData("1e3", 1000UL)]
    [InlineData("-5K", 0UL)]
    [InlineData("0.5", 0UL)]
    public void ParseFileSize(string input, ulong expected)
    {
        Assert.Equal(expected, Program.ParseFileSize(input));
    }

    [Theory]
    [InlineData("", ParseFileSizeException.Reason.EmptyInput, "empty argument")]
    [InlineData("i", ParseFileSizeException.Reason.InvalidSuffix, "invalid suffix")]
    [InlineData("5i", ParseFileSizeException.Reason.InvalidSuffix, "invalid suffix")]
    [InlineData("5X", ParseFileSizeException.Reason.InvalidSuffix, "invalid suffix")]
    [InlineData("K", ParseFileSizeException.Reason.InvalidNumber, "invalid number: cannot parse float from empty string")]
    [InlineData("abcK", ParseFileSizeException.Reason.InvalidNumber, "invalid number: invalid float literal")]
    [InlineData(" 5K", ParseFileSizeException.Reason.InvalidNumber, "invalid number: invalid float literal")]
    [InlineData("infK", ParseFileSizeException.Reason.NotFinite, "non-finite number specified")]
    [InlineData("NaNM", ParseFileSizeException.Reason.NotFinite, "non-finite number specified")]
    public void ParseFileSizeErrors(string input, ParseFileSizeException.Reason reason, string message)
    {
        var e = Assert.Throws<ParseFileSizeException>(() => Program.ParseFileSize(input));
        Assert.Equal(reason, e.Cause);
        Assert.Equal(message, e.Message);
    }

    [Fact]
    public void CoefficientToMillisRoundTrips()
    {
        Assert.Equal(5UL, Program.CoefficientToMillis(Player.DurationToCoefficient(TimeSpan.FromMilliseconds(5))));
        Assert.Equal(100UL, Program.CoefficientToMillis(Player.DurationToCoefficient(TimeSpan.FromMilliseconds(100))));
        Assert.Equal(500UL, Program.CoefficientToMillis(Player.DurationToCoefficient(TimeSpan.FromMilliseconds(500))));
    }

    [Fact]
    public void LogFilterDefaults()
    {
        var info = LogFilter.Parse("libmdns=info,librespot=info");
        Assert.True(info.IsEnabled("Librespot.Core.Session", LogLevel.Information));
        Assert.False(info.IsEnabled("Librespot.Core.Session", LogLevel.Debug));
        Assert.False(info.IsEnabled("Microsoft.Extensions.Hosting", LogLevel.Error));

        var verbose = LogFilter.Parse("libmdns=info,librespot=trace");
        Assert.True(verbose.IsEnabled("Librespot.Playback.Player", LogLevel.Trace));
        Assert.False(verbose.IsEnabled("Librespot.Discovery.Mdns.MdnsResponder", LogLevel.Debug));
        Assert.True(verbose.IsEnabled("Librespot.Discovery.Mdns.MdnsResponder", LogLevel.Information));

        var quiet = LogFilter.Parse("libmdns=warn,librespot=warn");
        Assert.False(quiet.IsEnabled("Librespot.Cli.Program", LogLevel.Information));
        Assert.True(quiet.IsEnabled("Librespot.Cli.Program", LogLevel.Warning));
    }

    [Fact]
    public void LogFilterSpecs()
    {
        // No directive: errors only.
        var empty = LogFilter.Parse("");
        Assert.True(empty.IsEnabled("Anything", LogLevel.Error));
        Assert.False(empty.IsEnabled("Anything", LogLevel.Warning));

        // Global level plus a more specific override.
        var f = LogFilter.Parse("warn,librespot_core::session=trace");
        Assert.True(f.IsEnabled("Librespot.Core.Session", LogLevel.Trace));
        Assert.False(f.IsEnabled("Librespot.Core.Cache", LogLevel.Information));
        Assert.True(f.IsEnabled("Other", LogLevel.Warning));

        // A bare target enables everything for it; "off" disables.
        var g = LogFilter.Parse("librespot_playback, librespot_core=off");
        Assert.True(g.IsEnabled("Librespot.Playback.Player", LogLevel.Trace));
        Assert.False(g.IsEnabled("Librespot.Core.Session", LogLevel.Critical));
        Assert.False(g.IsEnabled("Other", LogLevel.Critical));

        // .NET categories are used verbatim.
        var h = LogFilter.Parse("Librespot.Connect.SpircTask=DEBUG");
        Assert.True(h.IsEnabled("Librespot.Connect.SpircTask", LogLevel.Debug));

        // Invalid levels are reported and ignored.
        var warnings = new StringWriter { NewLine = "\n" };
        var i = LogFilter.Parse("librespot=loud", warnings);
        Assert.Equal("warning: invalid logging spec 'loud', ignoring it\n", warnings.ToString());
        Assert.True(i.IsEnabled("Librespot.Core.Session", LogLevel.Error));
        Assert.False(i.IsEnabled("Librespot.Core.Session", LogLevel.Warning));
    }

    [Theory]
    [InlineData("librespot", "Librespot")]
    [InlineData("librespot_core::session", "Librespot.Core.Session")]
    [InlineData("librespot_playback::audio_backend", "Librespot.Playback.AudioBackend")]
    [InlineData("librespot_core::spotify_id", "Librespot.Core.SpotifyId")]
    [InlineData("libmdns", "Librespot.Discovery.Mdns")]
    [InlineData("Librespot.Core", "Librespot.Core")]
    public void LogTargetMapping(string target, string category)
    {
        Assert.Equal(category, LogFilter.MapTarget(target));
    }
}
