using Bridge.Service;
using Bridge.Service.Airplay;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Bridge.Tests.Integration;

/// <summary>
/// Release consistency: the pieces a release is made of agree with each other. The shipped
/// <c>appsettings.json</c> states the same defaults as <see cref="BridgeOptions"/> (so the README's configuration table
/// holds whether a key is present or not), <c>build.ps1</c> publishes the bridge where
/// <c>install/windows/install-service.ps1</c> looks for it, and the web port is the same everywhere.
/// </summary>
public sealed class ReleaseConsistencyTests(ITestOutputHelper output)
{
    [Fact]
    public void Appsettings_states_the_BridgeOptions_defaults()
    {
        if (Root() is not { } root)
        {
            output.WriteLine("repository not found; skipped");
            return;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src", "Bridge.Service", "appsettings.json"), optional: false)
            .Build();
        var shipped = new BridgeOptions();
        configuration.GetSection("Bridge").Bind(shipped);
        var defaults = new BridgeOptions();

        Assert.Equal(defaults.DeviceName, shipped.DeviceName);
        Assert.Equal(defaults.LatencyFrames, shipped.LatencyFrames);
        Assert.Equal(77175, shipped.LatencyFrames); // SPEC.md D1
        Assert.Equal(defaults.PreferredStream, shipped.PreferredStream);
        Assert.Equal("Realtime", shipped.PreferredStream); // SPEC.md section 4, P16: buffered does not play on the srcvers-366 speaker
        Assert.Equal(defaults.PollReceiverVolumeSeconds, shipped.PollReceiverVolumeSeconds);
        Assert.Equal(defaults.ZeroconfPort, shipped.ZeroconfPort);
        Assert.Equal(defaults.SenderPorts.Timing, shipped.SenderPorts.Timing);
        Assert.Equal(defaults.SenderPorts.Control, shipped.SenderPorts.Control);
        Assert.Equal(defaults.EnableSpotify, shipped.EnableSpotify);
        Assert.Equal(defaults.BrowseReceivers, shipped.BrowseReceivers);
        Assert.Equal(defaults.FileLogging, shipped.FileLogging);
        Assert.Equal(defaults.LogRetentionDays, shipped.LogRetentionDays);
        Assert.Equal(defaults.ConnectTimeout, shipped.ConnectTimeout);
        Assert.Equal(defaults.ReconnectDelay, shipped.ReconnectDelay);
        Assert.Equal(defaults.MaxReconnectDelay, shipped.MaxReconnectDelay);
        Assert.Equal(0, shipped.FakeReceivers); // development switch, never on in a release
        Assert.Equal(defaults.Airplay.Enabled, shipped.Airplay.Enabled); // SPEC-receiver.md 8.5
        Assert.Equal(7000, shipped.Airplay.RtspPort); // SPEC-receiver.md R13
        Assert.Equal(319, shipped.Airplay.PtpEventPort);
        Assert.Equal(320, shipped.Airplay.PtpGeneralPort);
        Assert.Equal(defaults.Airplay.AnchorLeadMs, shipped.Airplay.AnchorLeadMs);
        Assert.Equal(defaults.Airplay.SessionTimeoutSeconds, shipped.Airplay.SessionTimeoutSeconds);
        Assert.Null(shipped.Airplay.FfmpegPath);
        Assert.True(string.IsNullOrEmpty(configuration["DataDirectory"]));
    }

    [Fact]
    public void Build_script_publishes_where_the_install_script_looks_and_the_web_port_is_8080_everywhere()
    {
        if (Root() is not { } root || !File.Exists(Path.Combine(root, "build.ps1")))
        {
            output.WriteLine("repository or build.ps1 not found; skipped");
            return;
        }

        var build = File.ReadAllText(Path.Combine(root, "build.ps1"));
        var install = File.ReadAllText(Path.Combine(root, "install", "windows", "install-service.ps1"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "docker", "Dockerfile"));
        var appsettings = File.ReadAllText(Path.Combine(root, "src", "Bridge.Service", "appsettings.json"));

        // build.ps1: <Output>/bridge with Output defaulting to <repo>/artifacts; install-service.ps1: <repo>\artifacts\bridge.
        Assert.Contains("Join-Path $PSScriptRoot \"artifacts\"", build, StringComparison.Ordinal);
        Assert.Contains("Join-Path $Output \"bridge\"", build, StringComparison.Ordinal);
        Assert.Contains("$PSScriptRoot\\..\\..\\artifacts\\bridge", install, StringComparison.Ordinal);
        Assert.Contains("bridge.exe", install, StringComparison.Ordinal);
        Assert.Contains("bridge.exe", build, StringComparison.Ordinal);

        Assert.Contains("[int]$WebPort = 8080", install, StringComparison.Ordinal);
        Assert.Contains("EXPOSE 8080/tcp", dockerfile, StringComparison.Ordinal);
        Assert.Contains("http://0.0.0.0:8080", dockerfile, StringComparison.Ordinal);
        Assert.Contains("http://0.0.0.0:8080", appsettings, StringComparison.Ordinal);

        // The image is built from the repository root with the Dockerfile in docker/ (its own header says so).
        Assert.Contains("docker build -f docker/Dockerfile", dockerfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// The AirPlay receiver's deployment pieces agree (SPEC-receiver.md 7.2, 10): the FFmpeg majors that
    /// <c>tools/get-ffmpeg.ps1</c> puts into the Windows bundle are the ones the container image and the systemd notes
    /// install, the ports the install script and the image open are the <see cref="AirplayOptions"/> defaults, and the
    /// README and ARCHITECTURE.md port lists name them.
    /// </summary>
    [Fact]
    public void Ffmpeg_majors_and_receiver_ports_agree_between_bundle_image_unit_and_docs()
    {
        if (Root() is not { } root || !File.Exists(Path.Combine(root, "tools", "get-ffmpeg.ps1")))
        {
            output.WriteLine("repository or tools/get-ffmpeg.ps1 not found; skipped");
            return;
        }

        var getFfmpeg = File.ReadAllText(Path.Combine(root, "tools", "get-ffmpeg.ps1"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "docker", "Dockerfile"));
        var unit = File.ReadAllText(Path.Combine(root, "install", "linux", "tune-shuttle.service"));
        var install = File.ReadAllText(Path.Combine(root, "install", "windows", "install-service.ps1"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var architecture = File.ReadAllText(Path.Combine(root, "docs", "ARCHITECTURE.md"));

        foreach (var library in new[] { "avcodec", "avutil", "swresample" })
        {
            var match = System.Text.RegularExpressions.Regex.Match(getFfmpeg, library + @"-(\d+)\.dll");
            Assert.True(match.Success, $"{library}-<major>.dll not named in tools/get-ffmpeg.ps1");
            var package = "lib" + library + match.Groups[1].Value;
            Assert.Contains(package, dockerfile, StringComparison.Ordinal);
            Assert.Contains(package, unit, StringComparison.Ordinal);
        }

        var defaults = new AirplayOptions();
        Assert.Contains($"-Protocol TCP -LocalPort {defaults.RtspPort}", install, StringComparison.Ordinal);
        Assert.Contains($"-Protocol UDP -LocalPort {defaults.PtpEventPort}-{defaults.PtpGeneralPort}", install, StringComparison.Ordinal);
        Assert.Contains($"EXPOSE {defaults.RtspPort}/tcp {defaults.PtpEventPort}/udp {defaults.PtpGeneralPort}/udp", dockerfile, StringComparison.Ordinal);
        foreach (var doc in new[] { readme, architecture })
        {
            Assert.Contains($"TCP {defaults.RtspPort}", doc, StringComparison.Ordinal);
            Assert.Contains($"UDP {defaults.PtpEventPort}/{defaults.PtpGeneralPort}", doc, StringComparison.Ordinal);
            Assert.Contains("Bridge:Airplay:RtspPort", doc, StringComparison.Ordinal);
        }
    }

    private static string? Root()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }
}
