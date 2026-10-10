using System.Globalization;
using System.Net;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Rtsp;

/// <summary>
/// Live checks of the transport (docs/DEVELOPMENT.md, "Live-device rules": unauthenticated <c>GET /info</c> is allowed any time). They
/// return early unless <c>AIRPLAY_LIVE=1</c>. SpeakerA: <c>$env:AIRPLAY_LIVE=1; dotnet test --filter Category=SpeakerALive</c>
/// (address from <c>SpeakerA_HOST</c>, default the documented 192.0.2.26:7000). shairport-sync: run inside the Docker VM
/// with <c>docker/shairport-sync/run-live-tests-in-vm.ps1</c> (<c>SPS_HOST</c>/<c>SPS_PORT</c>). Each run writes the
/// decrypted wire log to <c>artifacts/live/&lt;date&gt;-&lt;probe&gt;.log</c>.
/// </summary>
public class RtspLiveTests(ITestOutputHelper output)
{
    private const string SpeakerADefaultHost = "192.0.2.26";

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("AIRPLAY_LIVE") == "1";

    private static IPEndPoint SpeakerAEndpoint => new(
        IPAddress.Parse(Environment.GetEnvironmentVariable("SpeakerA_HOST") ?? SpeakerADefaultHost),
        int.Parse(Environment.GetEnvironmentVariable("SpeakerA_PORT") ?? "7000", CultureInfo.InvariantCulture));

    private static IPEndPoint ShairportEndpoint => new(
        IPAddress.Parse(Environment.GetEnvironmentVariable("SPS_HOST") ?? "127.0.0.1"),
        int.Parse(Environment.GetEnvironmentVariable("SPS_PORT") ?? "7000", CultureInfo.InvariantCulture));

    private static RtspRequest QualifierRequest() => RtspRequest.WithPlist(
        "GET",
        "/info",
        new PlistDictionary { ["qualifier"] = new List<object?> { "txtAirPlay" } },
        new Dictionary<string, string> { ["X-Apple-ProtocolVersion"] = "1" });

    [Fact]
    [Trait("Category", "SpeakerALive")]
    public async Task SpeakerA_GET_info_bare_and_with_qualifier_equal_the_stored_vectors()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        var log = new CapturingLogger();
        try
        {
            // Two fresh connections, as in the probe (one request each).
            RtspResponse bare;
            await using (var connection = await RtspConnection.ConnectAsync(SpeakerAEndpoint, new RtspConnectionOptions(), log, CancellationToken.None))
            {
                bare = await connection.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);
            }

            RtspResponse stage1;
            await using (var connection = await RtspConnection.ConnectAsync(SpeakerAEndpoint, new RtspConnectionOptions(), log, CancellationToken.None))
            {
                stage1 = await connection.SendExpectOkAsync(QualifierRequest(), CancellationToken.None);
            }

            Assert.Equal("RTSP/1.0", bare.Protocol);
            Assert.Equal(0, bare.CSeq);
            Assert.Equal(RtspContentTypes.BinaryPlist, bare.ContentType);
            Assert.Equal("Test Speaker ABCD", bare.PlistBody().GetString("name"));
            Assert.Equal(SpeakerACapture.InfoBody, bare.Body.ToArray());
            Assert.Equal(SpeakerACapture.Stage1InfoBody, stage1.Body.ToArray());
        }
        finally
        {
            WriteLiveLog("rtsp-get-info-SpeakerA", log);
        }
    }

    [Fact]
    [Trait("Category", "ShairportLive")]
    public async Task Shairport_GET_info_bare_and_with_qualifier()
    {
        if (!LiveEnabled)
        {
            output.WriteLine("AIRPLAY_LIVE not set");
            return;
        }

        var log = new CapturingLogger();
        try
        {
            await using var connection = await RtspConnection.ConnectAsync(ShairportEndpoint, new RtspConnectionOptions(), log, CancellationToken.None);
            var stage1 = await connection.SendExpectOkAsync(QualifierRequest(), CancellationToken.None);
            var bare = await connection.SendExpectOkAsync(RtspRequest.Create("GET", "/info"), CancellationToken.None);

            Assert.Equal(0, stage1.CSeq);
            Assert.Equal(1, bare.CSeq);
            var info = bare.PlistBody();
            Assert.NotNull(info.GetString("deviceID"));
            Assert.NotNull(info.GetDouble("initialVolume")); // shairport adds it, the SpeakerA does not (SPEC-control.md 1.8)
            Assert.NotNull(stage1.PlistBody().GetData("txtAirPlay"));
        }
        finally
        {
            WriteLiveLog("rtsp-get-info-shairport", log);
        }
    }

    private void WriteLiveLog(string probe, CapturingLogger log)
    {
        var text = log.Dump();
        output.WriteLine(text);
        try
        {
            var dir = Path.Combine(SpeakerACapture.RepositoryRoot, "artifacts", "live");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}-{probe}.log");
            File.AppendAllText(file, $"==== {DateTimeOffset.Now:O}\n{text}\n");
            output.WriteLine($"wire log: {file}");
        }
        catch (IOException ex)
        {
            output.WriteLine($"could not write the wire log: {ex.Message}");
        }
    }
}
