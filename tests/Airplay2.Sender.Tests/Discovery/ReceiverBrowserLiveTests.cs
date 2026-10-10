using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Airplay2.Sender.Discovery;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Discovery;

/// <summary>
/// Live discovery of the Test Speaker ABCD (docs/DEVELOPMENT.md, "Live-device rules": mDNS browsing is allowed at any time). Only mDNS
/// traffic is sent; no receiver is contacted. Runs only with <c>AIRPLAY_LIVE=1</c>:
/// <c>$env:AIRPLAY_LIVE=1; dotnet test --filter Category=SpeakerALive</c>.
/// </summary>
public class ReceiverBrowserLiveTests(ITestOutputHelper output)
{
    private const string SpeakerAName = "Test Speaker ABCD";

    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task Discover_lists_the_SpeakerA_with_the_capabilities_of_spec_control_1_7()
    {
        if (Environment.GetEnvironmentVariable("AIRPLAY_LIVE") != "1")
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        await using var browser = new MdnsReceiverBrowser(new ReceiverBrowserOptions(), new TestOutputLogger<MdnsReceiverBrowser>(output));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var SpeakerA = await browser.FindAsync(SpeakerAName, TimeSpan.FromSeconds(10), timeout.Token);
        if (SpeakerA is null)
        {
            output.WriteLine("Test Speaker ABCD not on the network; nothing to verify");
            return;
        }

        // give the _raop record a moment to merge
        for (var i = 0; i < 20 && SpeakerA!.Raop is null; i++)
        {
            await Task.Delay(250, timeout.Token);
            SpeakerA = MdnsReceiverBrowser.Match(browser.Snapshot(), SpeakerAName);
        }

        var log = Describe(browser.Snapshot());
        output.WriteLine(log);
        WriteArtifact(log);

        Assert.NotNull(SpeakerA);
        Assert.Equal("02:1A:2B:3C:4D:5E", SpeakerA!.Id);
        Assert.Equal(7000, SpeakerA.Port);
        Assert.NotNull(SpeakerA.AddressOf(AddressFamily.InterNetwork));
        Assert.NotNull(SpeakerA.Raop);

        // SPEC-control.md 1.7
        Assert.Equal((AirplayFeatures)0x0001C340445F8A00UL, SpeakerA.Airplay.Features);
        Assert.Equal(AirplayStatusFlags.AudioLink | AirplayStatusFlags.HomeKitAccessControl, SpeakerA.Airplay.Flags);
        Assert.Equal("366.0", SpeakerA.Airplay.SourceVersion);
        Assert.Equal(0, SpeakerA.Airplay.AccessControlLevel);
        Assert.Equal(new[] { 0, 4 }, SpeakerA.Raop!.EncryptionTypes);
        Assert.Equal(new[] { 0, 1 }, SpeakerA.Raop.Codecs);
        Assert.Equal(new[] { 0, 1, 2 }, SpeakerA.Raop.MetadataTypes);
        Assert.Equal("UDP", SpeakerA.Raop.Transport);

        var caps = SpeakerA.Capabilities;
        Assert.True(caps.IsAirPlay2);
        Assert.Equal(PairingRequirement.Transient, caps.Pairing);
        Assert.True(caps.SupportsRealtimeStream);
        Assert.True(caps.SupportsBufferedStream);
        Assert.True(caps.HasPtpClock);
        Assert.False(caps.PrefersNtp);
        Assert.True(caps.SupportsAlac);
        Assert.True(caps.WantsTextMetadata && caps.WantsArtwork && caps.WantsProgress);
        Assert.False(caps.UsesNowPlayingCommands);
    }

    private static string Describe(IReadOnlyList<DiscoveredReceiver> receivers)
    {
        var sb = new StringBuilder();
        foreach (var r in receivers.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var c = r.Capabilities;
            sb.Append(CultureInfo.InvariantCulture, $"{r.Name} [{r.Id}] {string.Join(", ", r.Addresses)} port {r.Port} model {r.Airplay.Model} srcvers {r.Airplay.SourceVersion}")
              .AppendLine()
              .Append(CultureInfo.InvariantCulture, $"  features {AirplayTxtValues.FormatFeatures(r.Airplay.Features)} bits {string.Join(',', AirplayTxtValues.Bits(r.Airplay.Features))} flags 0x{(ulong)r.Airplay.Flags:X} raop {(r.Raop is null ? "-" : "yes")}")
              .AppendLine()
              .Append(CultureInfo.InvariantCulture, $"  AP2 {c.IsAirPlay2} pairing {c.Pairing} realtime {c.SupportsRealtimeStream} buffered {c.SupportsBufferedStream} ptp {c.HasPtpClock} alac {c.SupportsAlac} meta {c.WantsTextMetadata}/{c.WantsArtwork}/{c.WantsProgress}")
              .AppendLine();
        }

        return sb.ToString();
    }

    private static void WriteArtifact(string text)
    {
        // docs/DEVELOPMENT.md, "Live-device rules": live tests log to artifacts/live/<date>-<probe>.log at the repository root.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")))
            {
                var folder = Path.Combine(dir.FullName, "artifacts", "live");
                Directory.CreateDirectory(folder);
                File.AppendAllText(
                    Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd}-discover.log"),
                    $"# {DateTimeOffset.Now:O} mDNS browse (MdnsReceiverBrowser)\n{text}\n");
                return;
            }
        }
    }
}

/// <summary>Writes log lines to the xunit test output.</summary>
internal sealed class TestOutputLogger<T>(ITestOutputHelper output, LogLevel minimum = LogLevel.Information) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {logLevel}: {formatter(state, exception)}");
        }
        catch (InvalidOperationException)
        {
            // the test has finished; late log lines from background loops are dropped
        }
    }
}
