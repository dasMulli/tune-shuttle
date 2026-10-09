using Airplay2.Sender.Cli;
using Airplay2.Sender.Tests.Session;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Cli;

/// <summary>
/// Sender M1 (PLAN.md) through the CLI against the Test Speaker ABCD: <c>airplay2 play tone:3 --to "Test Speaker ABCD"
/// --level 15 --i-know</c> with real mDNS discovery, transient pairing and the D5 start-up (WORK-PLAN.md section 4: a
/// 3-second quiet tone at -24 dBFS, level 15). Returns early unless <c>AIRPLAY_LIVE=1</c> and <c>AIRPLAY_AUDIO_I_KNOW=1</c>;
/// the complete Debug log goes to <c>artifacts/live/&lt;date&gt;-sender-M1-play.log</c>. Run alone:
/// <c>$env:AIRPLAY_LIVE=1; $env:AIRPLAY_AUDIO_I_KNOW=1; dotnet test --filter "FullyQualifiedName~SpeakerA_sender_M1"</c>.
/// </summary>
public sealed class CliLiveTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task SpeakerA_sender_M1_play_tone_3s()
    {
        if (Environment.GetEnvironmentVariable("AIRPLAY_LIVE") != "1")
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        if (Environment.GetEnvironmentVariable("AIRPLAY_AUDIO_I_KNOW") != "1")
        {
            output.WriteLine("AIRPLAY_AUDIO_I_KNOW not set: audio needs the explicit acknowledgement (WORK-PLAN.md section 4)");
            return;
        }

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var logFile = Path.Combine(CliLogging.LiveLogDirectory(), $"{DateTime.Now:yyyy-MM-dd}-sender-M1-play.log");
        var services = CliServices.Default with
        {
            Out = stdout,
            Error = stderr,
            CreatePairingStore = _ => new InMemoryPairingStore(),
        };

        // Only the Test Speaker ABCD (live-device rules); a 3-second tone at level 15.
        var exit = await Program.RunAsync(["play", "tone:3", "--to", "Test Speaker ABCD", "--level", "15", "--i-know", "--log-file", logFile], services, CancellationToken.None);
        output.WriteLine(stdout.ToString());
        output.WriteLine(stderr.ToString());
        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains("Done after", stdout.ToString());
    }
}
