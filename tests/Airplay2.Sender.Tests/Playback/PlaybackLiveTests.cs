using System.Net;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Session;
using Airplay2.Sender.Tests.Session;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Playback;

/// <summary>
/// Live audio probes against the Test Speaker ABCD (WORK-PLAN.md section 4, wave 4): <b>at most 3 s of a quiet 440 Hz
/// tone at -24 dBFS and level 15</b> per run, through the real <see cref="GroupPlayer"/>. They return early unless
/// <c>AIRPLAY_LIVE=1</c>, and additionally require <c>AIRPLAY_AUDIO_I_KNOW=1</c> (the test-run equivalent of the CLI's
/// <c>--i-know</c>). Every run ends with TEARDOWN and appends the wire log to <c>artifacts/live/&lt;date&gt;-&lt;probe&gt;.log</c>.
/// Run one at a time: <c>$env:AIRPLAY_LIVE=1; $env:AIRPLAY_AUDIO_I_KNOW=1; dotnet test --filter "FullyQualifiedName~SpeakerA_tone"</c>.
/// </summary>
public sealed class PlaybackLiveTests(ITestOutputHelper output)
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

    /// <summary>
    /// <c>airplay2 play tone:3 --to "Test Speaker ABCD" --level 15 --i-know</c> through the library, combined with P1 (audio
    /// confirmation under NTP), P14 (every 50th packet withheld), P9's audio half (DMAP while audio flows), P12 (gid) and
    /// P7's header dump.
    /// </summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_tone_3s_P1_P14() => RunAsync("P1-audio-tone", p => p.AudioConfirmationAsync(TimeSpan.FromSeconds(3), withholdEvery: 50, CancellationToken.None));

    /// <summary>P19: 1.2 s tone, pause (FLUSH) 2 s, resume without RECORD, 1.2 s tone, seek-style FLUSH.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P19_pause_resume() => RunAsync("P19-pause-resume", p => p.PauseResumeAsync(TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.2), CancellationToken.None));

    /// <summary>P15, audio part: 2 s tone with a random <c>shk</c>.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P15_random_key() => RunAsync("P15-audio-random-key", p => p.StreamKeyAsync(randomKey: true, TimeSpan.FromSeconds(2), CancellationToken.None));

    /// <summary>P18 within the 3 s budget: periodic syncs stopped for 2 s.</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P18_sync_gap() => RunAsync("P18-sync-gap", p => p.SyncGapAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), CancellationToken.None));

    /// <summary>P7 (needs a microphone for the delay; this logs the latency fields): click track with L = <c>P7_LATENCY</c> (77175) and flags <c>P7_FLAGS</c> (7).</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P7_latency() => RunAsync("P7-latency", p => p.LatencyAsync(
        int.Parse(Environment.GetEnvironmentVariable("P7_LATENCY") ?? "77175", System.Globalization.CultureInfo.InvariantCulture),
        ushort.Parse(Environment.GetEnvironmentVariable("P7_FLAGS") ?? "7", System.Globalization.CultureInfo.InvariantCulture),
        TimeSpan.FromSeconds(3),
        CancellationToken.None));

    /// <summary>P12: groupUUID / booleans and the advertised gid (one allowed receiver; the second member is final integration).</summary>
    [Fact]
    [Trait("Category", "SpeakerALive")]
    public Task SpeakerA_P12_group_fields() => RunAsync("P12-group-fields", p => p.GroupFieldsAsync(Guid.NewGuid().ToString().ToUpperInvariant(), false, true, TimeSpan.FromSeconds(2), CancellationToken.None));

    private async Task RunAsync(string name, Func<PlaybackProbes, Task<ProbeReport>> probe)
    {
        if (Environment.GetEnvironmentVariable("AIRPLAY_LIVE") != "1")
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        if (Environment.GetEnvironmentVariable("AIRPLAY_AUDIO_I_KNOW") != "1")
        {
            output.WriteLine("AIRPLAY_AUDIO_I_KNOW not set: audio probes need the explicit acknowledgement (WORK-PLAN.md section 4)");
            return;
        }

        using var log = new SessionLiveLog(name, output);
        var probes = new PlaybackProbes(SpeakerA(), new InMemoryPairingStore(), log, new PlaybackProbeOptions { Level = 15, Sender = SessionTestSupport.Sender with { Name = "airplay2-streamer probe" } });
        var report = await probe(probes);
        log.Write($"RESULT {report.Probe}: {report.Summary}");
        Assert.DoesNotContain("failed:", report.Summary, StringComparison.Ordinal);
    }

    private static DiscoveredReceiver SpeakerA()
    {
        var host = IPAddress.Parse(Environment.GetEnvironmentVariable("SpeakerA_HOST") ?? "192.0.2.26");
        return new DiscoveredReceiver("02:1A:2B:3C:4D:5E", "Test Speaker ABCD", [host], 7000, AirplayTxtRecord.Parse(SpeakerAAirplay), RaopTxtRecord.Parse(SpeakerARaop), DateTimeOffset.UtcNow);
    }
}
