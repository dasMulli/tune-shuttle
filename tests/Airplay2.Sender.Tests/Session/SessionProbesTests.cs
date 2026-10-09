using Airplay2.FakeReceiver;
using Airplay2.Sender.Session;
using Xunit;
using Xunit.Abstractions;
using static Airplay2.Sender.Tests.Session.SessionTestSupport;
using Harness = Airplay2.Sender.Tests.Fake.FakeReceiverHarness;

namespace Airplay2.Sender.Tests.Session;

/// <summary>The probe methods run against the fake receiver, so the live runs exercise tested code.</summary>
public sealed class SessionProbesTests(ITestOutputHelper output)
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly SessionProbeOptions Options = new() { TimingWait = TimeSpan.FromSeconds(1) };

    [Fact]
    public async Task P1_on_an_ntp_receiver_decides_ntp()
    {
        await using var fake = await StartAsync();
        var report = await Probes(fake).TimingProtocolAsync(None);
        Print(report);
        Assert.Equal(200, report.NtpSetupStatus);
        Assert.True(report.NtpEventPort > 0);
        Assert.True(report.TimingRepliesSent > 0);
        Assert.NotNull(report.FirstTimingReplyAfter);
        Assert.Equal(200, report.PtpSetupStatus);
        Assert.Equal(TimingProtocol.Ntp, report.Decision);

        // Every session was torn down: one per-stream + one session TEARDOWN for NTP, one session TEARDOWN for PTP.
        var teardowns = (await Harness.DrainAsync(fake, TimeSpan.FromMilliseconds(300))).OfType<FakeReceiverEvent.Teardown>().Count();
        Assert.Equal(3, teardowns);
    }

    [Fact]
    public async Task P1_on_a_receiver_that_refuses_ntp_decides_ptp()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { RefuseNtp = true });
        var report = await Probes(fake).TimingProtocolAsync(None);
        Print(report);
        Assert.Equal(501, report.NtpSetupStatus);
        Assert.Equal(0, report.TimingRepliesSent);
        Assert.Equal(200, report.PtpSetupStatus);
        Assert.Single(report.PtpTimingPeerAddresses);
        Assert.Equal(TimingProtocol.Ptp, report.Decision);
    }

    [Fact]
    public async Task P1_without_timing_requests_decides_ptp()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var report = await Probes(fake).TimingProtocolAsync(None);
        Assert.Equal(200, report.NtpSetupStatus);
        Assert.Equal(0, report.TimingRepliesSent);
        Assert.Equal(TimingProtocol.Ptp, report.Decision);
    }

    [Fact]
    public async Task P3_runs_both_orders()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var report = await Probes(fake).StartupOrderAsync(TimingProtocol.Ntp, None);
        Print(report);
        Assert.StartsWith("A: ", report.Summary);
        Assert.Contains("| B: ", report.Summary);
        Assert.Contains("GET_PARAMETER volume 200 (-24)", report.Summary);
        Assert.Contains("GET_PARAMETER volume 200 (-25.5)", report.Summary);
        Assert.Equal(2, report.Summary.Split("RECORD 200 (Audio-Latency 0)").Length - 1);
    }

    [Fact]
    public async Task P4_sees_volume_changes_through_polling_and_events()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var probe = Probes(fake).DeviceVolumeAsync(TimeSpan.FromSeconds(3), None);
        await Task.Delay(1500);
        await fake.ChangeVolumeAsync(-9.0, None);
        var report = await probe;
        Print(report);
        Assert.StartsWith("1 volume change(s)", report.Summary);
        Assert.Contains(report.Observations, o => o.Contains("initialVolume -9", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task P5_reports_stream_id_and_both_teardown_forms(bool streamId)
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false, ReturnStreamId = streamId });
        var report = await Probes(fake).StreamIdAndTeardownAsync(None);
        Print(report);
        Assert.Contains(streamId ? "per-stream TEARDOWN 200" : "per-stream TEARDOWN not sent", report.Summary);
        Assert.Contains("session TEARDOWN 200 with Connection: close", report.Summary);
        Assert.EndsWith("next session SETUP 200", report.Summary);
    }

    [Fact]
    public async Task P8_feedback_bodies()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var report = await Probes(fake).FeedbackAsync(TimeSpan.FromSeconds(1), None);
        Print(report);
        Assert.Contains("none -> 200 {streams: [{type: 96, sr: 44100}]}", report.Summary);
        Assert.Contains("{} -> 200", report.Summary);
        Assert.Contains("still connected after 1 s", report.Summary);
    }

    [Fact]
    public async Task P9_sends_metadata_artwork_and_progress()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var report = await Probes(fake).MetadataAsync(new TrackMetadata("Probe", "Artist", "Album", TimeSpan.FromSeconds(10)), SessionProbes.SampleJpeg(), None);
        Print(report);
        Assert.Equal("DMAP 200; DMAP minimal 200; DMAP minimal with seq 200; artwork 634 B 200; progress 200", report.Summary);
        var events = await Harness.DrainAsync(fake, TimeSpan.FromMilliseconds(300));
        Assert.Contains(events, e => e is FakeReceiverEvent.Artwork { RtpTime: 77175 });
        Assert.Contains(events, e => e is FakeReceiverEvent.Progress { Start: 61815, Current: 77175, End: 77175 + 441000 });
    }

    [Fact]
    public async Task P10_reports_initial_volume_and_get_parameter()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false, InitialVolume = -21.0 });
        var report = await Probes(fake).InitialVolumeAsync(None);
        Print(report);
        Assert.Equal("paired GET /info initialVolume -21; GET_PARAMETER volume after session SETUP -21, after stream SETUP -21", report.Summary);
    }

    [Fact]
    public async Task P13_measures_the_first_event_and_a_late_answer()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false, EventAnswerTimeout = TimeSpan.FromSeconds(1) });
        var report = await Probes(fake).EventTimingAsync(TimeSpan.FromSeconds(2), None);
        Print(report);
        Assert.Contains("(before RECORD)", report.Summary);
        Assert.Contains("channel closed", report.Summary);
    }

    [Fact]
    public async Task P15_accepts_both_stream_keys()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false });
        var report = await Probes(fake).StreamKeyAsync(None);
        Print(report);
        Assert.StartsWith("shk = SS[0..31]: 200", report.Summary);
        Assert.Contains("random shk: 200", report.Summary);
    }

    [Fact]
    public async Task P21_sends_one_oversized_frame_and_reports_received_frame_sizes()
    {
        await using var fake = await StartAsync(new FakeReceiverOptions { SendTimingRequests = false, LargeEventFrames = true });
        var report = await Probes(fake).LargeFrameAsync(2048, None);
        Print(report);
        Assert.StartsWith("GET /info as one 2048-byte frame: 200", report.Summary);
        Assert.Matches(@"event channel \d+ frames, largest (1[1-9]\d\d|[2-9]\d\d\d|\d{5}) B", report.Summary);
        var request = await Harness.NextAsync<FakeReceiverEvent.Request>(fake, r => r.Uri == "/info" && r.Encrypted && r.Headers.ContainsKey("X-Probe-Padding"));
        Assert.Equal("GET", request.Method);
    }

    private static SessionProbes Probes(Airplay2.FakeReceiver.FakeReceiver fake) =>
        new(Describe(fake), new InMemoryPairingStore(), loggerFactory: null, Options);

    private void Print(ProbeReport report)
    {
        foreach (var line in report.Observations)
        {
            output.WriteLine(line);
        }
    }
}
