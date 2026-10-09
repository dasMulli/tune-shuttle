using Bridge.Service;
using Bridge.Service.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bridge.Tests.Service;

/// <summary>
/// Deployment wiring (PLAN.md "Deployment", WORK-PLAN.md bridge-service): the web port reaches Kestrel the same way from
/// the Windows install script (command line), the container and the systemd unit (<c>BRIDGE_</c> variables), and both
/// override the endpoint in appsettings.json; Windows log files roll per day.
/// </summary>
public sealed class DeploymentTests
{
    private const string EndpointKey = "Kestrel:Endpoints:Http:Url";

    [Fact]
    public async Task The_command_line_endpoint_overrides_appsettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bridge-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var defaults = Program.Build(["--DataDirectory", dir, "--Bridge:FileLogging", "false"]))
            {
                Assert.Equal("http://0.0.0.0:8080", defaults.Configuration[EndpointKey]);
            }

            await using var app = Program.Build(["--DataDirectory", dir, "--Bridge:FileLogging", "false", "--" + EndpointKey, "http://0.0.0.0:9123"]);
            Assert.Equal("http://0.0.0.0:9123", app.Configuration[EndpointKey]);
            Assert.Equal(Path.GetFullPath(dir), app.Services.GetRequiredService<BridgePaths>().DataDirectory);
        }
        finally
        {
            BridgeFactory.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Install_script_container_and_systemd_set_the_endpoint_through_the_bridge_configuration()
    {
        var root = RepositoryRoot();
        var install = File.ReadAllText(Path.Combine(root, "install", "windows", "install-service.ps1"));
        Assert.Contains("--" + EndpointKey + " http://0.0.0.0:$WebPort", install, StringComparison.Ordinal);

        var dockerfile = File.ReadAllText(Path.Combine(root, "docker", "Dockerfile"));
        Assert.Contains("BRIDGE_Kestrel__Endpoints__Http__Url=", dockerfile, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, ".dockerignore")));

        var unit = File.ReadAllText(Path.Combine(root, "install", "linux", "airplay-bridge.service"));
        Assert.Contains("Environment=BRIDGE_Kestrel__Endpoints__Http__Url=", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment=ASPNETCORE_URLS", unit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_logger_writes_a_daily_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bridge-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var provider = new FileLoggerProvider(enabled: true, dir, retentionDays: 7))
            {
                provider.CreateLogger("Test.Category").LogWarning("hello {Name}", "file");
            }

            var file = Assert.Single(Directory.GetFiles(dir, "bridge-*.log"));
            var text = await File.ReadAllTextAsync(file);
            Assert.Contains("[warn] Test.Category: hello file", text, StringComparison.Ordinal);
        }
        finally
        {
            BridgeFactory.DeleteDirectory(dir);
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AirplayBridge.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("AirplayBridge.sln not found above the test output.");
    }
}
