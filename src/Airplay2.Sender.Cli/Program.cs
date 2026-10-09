using System.CommandLine;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Cli;

/// <summary>
/// CLI harness (docs/PLAN.md): <c>discover</c>, <c>info</c>, <c>pair</c>, <c>play &lt;file.wav&gt; --to &lt;name&gt; [--to ...] --i-know</c>,
/// <c>volume</c>, plus <c>probe &lt;P-id&gt; &lt;name&gt;</c> for SPEC.md section 4 (ids <c>P1</c>..<c>P21</c>). Pairings are
/// stored in a JSON file next to the executable (<c>airplay2-pairings.json</c>). Audio is never sent without <c>--i-know</c>
/// (WORK-PLAN.md section 4: 3-second quiet tone rule); probes that send audio require it too.
/// </summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async Task<int> Main(string[] args)
    {
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Debug logging" };
        var root = new RootCommand("AirPlay 2 sender harness") { verbose };

        var discover = new Command("discover", "Browse _airplay._tcp / _raop._tcp for a few seconds and list receivers with their decoded capabilities");
        var seconds = new Option<int>("--seconds") { Description = "How long to browse", DefaultValueFactory = _ => 5 };
        discover.Add(seconds);
        discover.SetAction((parse, ct) => Commands.DiscoverAsync(parse.GetValue(seconds), Logging(parse.GetValue(verbose)), ct));

        var info = new Command("info", "GET /info against a receiver (unauthenticated; allowed at any time)");
        var host = new Argument<string>("host") { Description = "Receiver name or IP address" };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 7000 };
        info.Add(host);
        info.Add(port);
        info.SetAction((parse, ct) => Commands.InfoAsync(parse.GetValue(host)!, parse.GetValue(port), Logging(parse.GetValue(verbose)), ct));

        var pair = new Command("pair", "Pair with a receiver (transient check, or the PIN flow when the receiver requires it)");
        var pairTarget = new Argument<string>("name") { Description = "Receiver name" };
        pair.Add(pairTarget);
        pair.SetAction((parse, ct) => Commands.PairAsync(parse.GetValue(pairTarget)!, Logging(parse.GetValue(verbose)), ct));

        var play = new Command("play", "Play a 16-bit 44.1 kHz stereo WAV (or a generated test tone) to one or more receivers in sync. Refuses to send audio without --i-know (WORK-PLAN.md section 4).");
        var file = new Argument<string>("file") { Description = "WAV file path, or 'tone:<seconds>' for a quiet 440 Hz test tone (-24 dBFS)" };
        var to = new Option<string[]>("--to") { Description = "Receiver name (repeatable)", AllowMultipleArgumentsPerToken = true, Required = true };
        var level = new Option<int>("--level") { Description = "Level 0..100 (live rule: <= 20 until final integration)", DefaultValueFactory = _ => 15 };
        var latency = new Option<int>("--latency") { Description = "Sender latency L in frames", DefaultValueFactory = _ => 77175 };
        var iKnow = new Option<bool>("--i-know") { Description = "Acknowledge the live-audio rules (3 s quiet tone, user present); required for any audio" };
        play.Add(file);
        play.Add(to);
        play.Add(level);
        play.Add(latency);
        play.Add(iKnow);
        play.SetAction((parse, ct) => Commands.PlayAsync(parse.GetValue(file)!, parse.GetValue(to)!, parse.GetValue(level), parse.GetValue(latency), parse.GetValue(iKnow), Logging(parse.GetValue(verbose)), ct));

        var volume = new Command("volume", "Set or read a receiver's volume during a session (GET_PARAMETER / SET_PARAMETER)");
        var volTarget = new Argument<string>("name");
        var volLevel = new Argument<int?>("level") { Arity = ArgumentArity.ZeroOrOne };
        volume.Add(volTarget);
        volume.Add(volLevel);
        volume.SetAction((parse, ct) => Commands.VolumeAsync(parse.GetValue(volTarget)!, parse.GetValue(volLevel), Logging(parse.GetValue(verbose)), ct));

        var probe = new Command("probe", "Run one of the SPEC.md section 4 probes against a receiver (never the TV); probes that send audio need --i-know");
        var probeId = new Argument<string>("id") { Description = "Probe id from SPEC.md section 4: P1 .. P21 (e.g. P1, P14)" };
        var probeTarget = new Argument<string>("name");
        var probeIKnow = new Option<bool>("--i-know") { Description = "Required for probes that send audio (P1 audio part, P7, P12, P14, P15, P18, P19)" };
        probe.Add(probeId);
        probe.Add(probeTarget);
        probe.Add(probeIKnow);
        probe.SetAction((parse, ct) => Commands.ProbeAsync(parse.GetValue(probeId)!, parse.GetValue(probeTarget)!, parse.GetValue(probeIKnow), Logging(parse.GetValue(verbose)), ct));

        root.Add(discover);
        root.Add(info);
        root.Add(pair);
        root.Add(play);
        root.Add(volume);
        root.Add(probe);
        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    private static ILoggerFactory Logging(bool verbose) => LoggerFactory.Create(b => b
        .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; }));
}

/// <summary>Command implementations.</summary>
public static class Commands
{
    public static Task<int> DiscoverAsync(int seconds, ILoggerFactory logging, CancellationToken ct) => throw new NotImplementedException("TODO(impl: sender-api-cli)");

    public static Task<int> InfoAsync(string host, int port, ILoggerFactory logging, CancellationToken ct) => throw new NotImplementedException("TODO(impl: sender-api-cli)");

    public static Task<int> PairAsync(string name, ILoggerFactory logging, CancellationToken ct) => throw new NotImplementedException("TODO(impl: sender-api-cli)");

    /// <summary>Plays a file or tone; returns exit code 2 without sending anything when <paramref name="iKnow"/> is false.</summary>
    public static Task<int> PlayAsync(string file, string[] to, int level, int latency, bool iKnow, ILoggerFactory logging, CancellationToken ct) => throw new NotImplementedException("TODO(impl: sender-api-cli)");

    public static Task<int> VolumeAsync(string name, int? level, ILoggerFactory logging, CancellationToken ct) => throw new NotImplementedException("TODO(impl: sender-api-cli)");

    /// <summary>Runs probe <paramref name="id"/> (<c>P1</c>..<c>P21</c>); audio-sending probes return exit code 2 without <paramref name="iKnow"/>. Logs the decrypted exchange to <c>artifacts/live/&lt;date&gt;-&lt;id&gt;.log</c>.</summary>
    public static Task<int> ProbeAsync(string id, string name, bool iKnow, ILoggerFactory logging, CancellationToken ct) => throw new NotImplementedException("TODO(impl: sender-api-cli)");
}
