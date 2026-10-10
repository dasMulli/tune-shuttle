using System.CommandLine;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Cli;

/// <summary>
/// CLI harness (README.md, "The CLI"): <c>discover</c>, <c>info</c>, <c>pair</c>, <c>play &lt;file.wav&gt; --to &lt;name&gt; [--to ...] --i-know</c>,
/// <c>volume</c>, plus <c>probe &lt;P-id&gt; &lt;name&gt;</c> for SPEC.md section 4 (ids <c>P1</c>..<c>P21</c>). Pairings are
/// stored in a JSON file next to the executable (<c>airplay2-pairings.json</c>). Audio is never sent without <c>--i-know</c>
/// (docs/DEVELOPMENT.md, "Live-device rules": 3-second quiet tone); probes that send audio require it too.
/// </summary>
/// <remarks>
/// Exit codes (<see cref="ExitCodes"/>): 0 success; 1 failure (bad input, receiver not found, network/protocol error,
/// failed probe; also System.CommandLine parse errors); 2 audio refused without <c>--i-know</c> (nothing sent, no socket
/// opened); 3 probe not available in this build; 130 interrupted (Ctrl+C or SIGTERM: the command's token is cancelled
/// and the process waits up to <see cref="TeardownGrace"/> for its TEARDOWNs, SPEC.md D7).
/// </remarks>
public static class Program
{
    /// <summary>
    /// How long the process waits after Ctrl+C/SIGTERM for the interrupted command to finish its TEARDOWNs (D7) before it
    /// exits anyway: each member's TEARDOWN is bounded by 10 s (<see cref="Playback.GroupPlayerOptions.TeardownTimeout"/>,
    /// <see cref="CliCommands.TeardownQuietlyAsync"/>) and the srcvers-366 speaker answered FLUSH/TEARDOWN after 10-30 s on bad days,
    /// so System.CommandLine's 2 s default would exit in the middle of them.
    /// </summary>
    public static readonly TimeSpan TeardownGrace = TimeSpan.FromSeconds(60);

    /// <summary>Entry point.</summary>
    public static Task<int> Main(string[] args) => RunAsync(args, CliServices.Default, CancellationToken.None);

    /// <summary>Parses and runs <paramref name="args"/> against <paramref name="services"/> (tests pass fakes).</summary>
    public static Task<int> RunAsync(string[] args, CliServices services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);
        var root = BuildRootCommand(services);
        return root.Parse(args).InvokeAsync(CreateInvocationConfiguration(services), cancellationToken);
    }

    /// <summary>The invocation settings: console streams of <paramref name="services"/> and the Ctrl+C grace <see cref="TeardownGrace"/>.</summary>
    public static InvocationConfiguration CreateInvocationConfiguration(CliServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new InvocationConfiguration { Output = services.Out, Error = services.Error, ProcessTerminationTimeout = TeardownGrace };
    }

    /// <summary>The command tree.</summary>
    public static RootCommand BuildRootCommand(CliServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var commands = new CliCommands(services);
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Debug logging on the console (includes the decrypted wire exchange)", Recursive = true };
        var logFile = new Option<string?>("--log-file") { Description = "Also append the complete Debug log to this file (probes default to artifacts/live/<date>-<id>.log)", Recursive = true };
        var root = new RootCommand("AirPlay 2 sender harness") { verbose, logFile };
        CliLogOptions LogOptions(ParseResult parse) => new(parse.GetValue(verbose), parse.GetValue(logFile));

        var discover = new Command("discover", "Browse _airplay._tcp / _raop._tcp for a few seconds and list receivers with their decoded capabilities");
        var seconds = new Option<int>("--seconds") { Description = "How long to browse", DefaultValueFactory = _ => 5 };
        discover.Add(seconds);
        discover.SetAction((parse, ct) => WithLogging(services, LogOptions(parse), logging => commands.DiscoverAsync(parse.GetValue(seconds), logging, ct)));

        var info = new Command("info", "GET /info against a receiver (unauthenticated; allowed at any time)");
        var host = new Argument<string>("host") { Description = "Receiver name or IP address" };
        var port = new Option<int>("--port") { Description = "RTSP port when the host is an IP address", DefaultValueFactory = _ => 7000 };
        var qualifier = new Option<bool>("--qualifier") { Description = "Send the iOS stage-1 body {qualifier: [txtAirPlay]}" };
        info.Add(host);
        info.Add(port);
        info.Add(qualifier);
        info.SetAction((parse, ct) => WithLogging(services, LogOptions(parse), logging => commands.InfoAsync(parse.GetValue(host)!, parse.GetValue(port), parse.GetValue(qualifier), logging, ct)));

        var pair = new Command("pair", "Pair with a receiver (transient check, or the PIN flow when the receiver requires it)");
        var pairTarget = new Argument<string>("name") { Description = "Receiver name" };
        var pin = new Option<string?>("--pin") { Description = "The PIN shown by the receiver (or its password); asked for when omitted" };
        var forget = new Option<bool>("--forget") { Description = "Delete the stored pairing for this receiver instead" };
        pair.Add(pairTarget);
        pair.Add(pin);
        pair.Add(forget);
        pair.SetAction((parse, ct) => WithLogging(services, LogOptions(parse), logging => commands.PairAsync(parse.GetValue(pairTarget)!, parse.GetValue(pin), parse.GetValue(forget), logging, ct)));

        var play = new Command("play", "Play a 16-bit 44.1 kHz stereo WAV (or a generated test tone) to one or more receivers in sync. Refuses to send audio without --i-know (docs/DEVELOPMENT.md, \"Live-device rules\").");
        var file = new Argument<string>("file") { Description = "WAV file path, or 'tone:<seconds>' for a quiet 440 Hz test tone (-24 dBFS)" };
        var to = new Option<string[]>("--to") { Description = "Receiver name (repeatable)", AllowMultipleArgumentsPerToken = true, Required = true };
        var level = new Option<int>("--level") { Description = "Level 0..100 (live rule: <= 20 until final integration)", DefaultValueFactory = _ => 15 };
        var latency = new Option<int>("--latency") { Description = "Sender latency L in frames", DefaultValueFactory = _ => 77175 };
        var iKnow = new Option<bool>("--i-know") { Description = "Acknowledge the live-audio rules (3 s quiet tone, user present); required for any audio" };
        var noMetadata = new Option<bool>("--no-metadata") { Description = "Do not send DMAP text/progress (some receivers answer DMAP with 500 after up to 10 s, e.g. srcvers 366.0)" };
        play.Add(file);
        play.Add(to);
        play.Add(level);
        play.Add(latency);
        play.Add(iKnow);
        play.Add(noMetadata);
        play.SetAction((parse, ct) => WithLogging(services, LogOptions(parse), logging => commands.PlayAsync(parse.GetValue(file)!, parse.GetValue(to)!, parse.GetValue(level), parse.GetValue(latency), parse.GetValue(iKnow), !parse.GetValue(noMetadata), logging, ct)));

        var volume = new Command("volume", "Set or read a receiver's volume during a session (GET_PARAMETER / SET_PARAMETER)");
        var volTarget = new Argument<string>("name") { Description = "Receiver name" };
        var volLevel = new Argument<int?>("level") { Description = "Level 0..100 to set; omitted: read only", Arity = ArgumentArity.ZeroOrOne };
        volume.Add(volTarget);
        volume.Add(volLevel);
        volume.SetAction((parse, ct) => WithLogging(services, LogOptions(parse), logging => commands.VolumeAsync(parse.GetValue(volTarget)!, parse.GetValue(volLevel), logging, ct)));

        var probe = new Command("probe", "Run one of the SPEC.md section 4 probes against a receiver (never the TV); probes that send audio need --i-know");
        var probeId = new Argument<string>("id") { Description = "Probe id from SPEC.md section 4: P1 .. P21 (e.g. P1, P14)" };
        var probeTarget = new Argument<string>("name") { Description = "Receiver name" };
        var probeIKnow = new Option<bool>("--i-know") { Description = "Required for probes that send audio (P1 audio part, P7, P12, P14, P15, P18, P19)" };
        var probeSeconds = new Option<double?>("--seconds") { Description = "Duration: P4 session (10), P8 idle (0), P13 answer delay (5), P17 capture (20), audio tone (<= 3)" };
        var probeLevel = new Option<int?>("--level") { Description = "Audio probes: level 0..20 (15)" };
        var probeLatency = new Option<int?>("--latency") { Description = "P7: sender latency L in frames (77175)" };
        var probeFlags = new Option<ushort?>("--sync-flags") { Description = "P7: sync packet flags (7)" };
        var probeWithhold = new Option<int?>("--withhold") { Description = "P1 audio part / P14: withhold every n-th packet (50)" };
        probe.Add(probeId);
        probe.Add(probeTarget);
        probe.Add(probeIKnow);
        probe.Add(probeSeconds);
        probe.Add(probeLevel);
        probe.Add(probeLatency);
        probe.Add(probeFlags);
        probe.Add(probeWithhold);
        probe.SetAction((parse, ct) => new ProbeCommand(services).RunAsync(
            parse.GetValue(probeId)!,
            parse.GetValue(probeTarget)!,
            parse.GetValue(probeIKnow),
            new ProbeParameters(parse.GetValue(probeSeconds), parse.GetValue(probeLevel), parse.GetValue(probeLatency), parse.GetValue(probeFlags), parse.GetValue(probeWithhold)),
            LogOptions(parse),
            ct));

        root.Add(discover);
        root.Add(info);
        root.Add(pair);
        root.Add(play);
        root.Add(volume);
        root.Add(probe);
        return root;
    }

    private static async Task<int> WithLogging(CliServices services, CliLogOptions options, Func<ILoggerFactory, Task<int>> action)
    {
        using var logging = services.CreateLogging(options);
        return await action(logging).ConfigureAwait(false);
    }
}

/// <summary>
/// The command implementations with the real console, mDNS and pairing file (<see cref="CliServices.Default"/>); the
/// same code as <see cref="CliCommands"/> and <see cref="ProbeCommand"/>, which take replaceable services.
/// </summary>
public static class Commands
{
    /// <summary><c>discover</c>: browse and list receivers with decoded capabilities.</summary>
    public static Task<int> DiscoverAsync(int seconds, ILoggerFactory logging, CancellationToken ct) => new CliCommands(CliServices.Default).DiscoverAsync(seconds, logging, ct);

    /// <summary><c>info</c>: unauthenticated <c>GET /info</c>.</summary>
    public static Task<int> InfoAsync(string host, int port, ILoggerFactory logging, CancellationToken ct) => new CliCommands(CliServices.Default).InfoAsync(host, port, withQualifier: false, logging, ct);

    /// <summary><c>pair</c>: transient check or the PIN flow (PIN asked on the console).</summary>
    public static Task<int> PairAsync(string name, ILoggerFactory logging, CancellationToken ct) => new CliCommands(CliServices.Default).PairAsync(name, pin: null, forget: false, logging, ct);

    /// <summary>Plays a file or tone; returns exit code 2 without sending anything when <paramref name="iKnow"/> is false.</summary>
    public static Task<int> PlayAsync(string file, string[] to, int level, int latency, bool iKnow, ILoggerFactory logging, CancellationToken ct) =>
        new CliCommands(CliServices.Default).PlayAsync(file, to, level, latency, iKnow, sendMetadata: true, logging, ct);

    /// <summary><c>volume</c>: read and optionally set a receiver's level in a session without audio.</summary>
    public static Task<int> VolumeAsync(string name, int? level, ILoggerFactory logging, CancellationToken ct) => new CliCommands(CliServices.Default).VolumeAsync(name, level, logging, ct);

    /// <summary>Runs probe <paramref name="id"/> (<c>P1</c>..<c>P21</c>); audio-sending probes return exit code 2 without <paramref name="iKnow"/>. Logs the decrypted exchange to <c>artifacts/live/&lt;date&gt;-&lt;id&gt;.log</c>.</summary>
    public static Task<int> ProbeAsync(string id, string name, bool iKnow, ILoggerFactory logging, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logging);
        var services = CliServices.Default with { CreateLogging = options => new TeeLoggerFactory(logging, options.LogFile) };
        return new ProbeCommand(services).RunAsync(id, name, iKnow, new ProbeParameters(), new CliLogOptions(false, null), ct);
    }
}
