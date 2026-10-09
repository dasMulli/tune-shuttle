// Port of src/main.rs (everything except the `Setup` struct and `get_setup`, which live in Setup.cs, and the getopts
// option table, which lives in Options.cs).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Librespot.Connect;
using Librespot.Core;
using Librespot.Core.Logging;
using Librespot.OAuth;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Mixer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using LibrespotDiscovery = Librespot.Discovery.Discovery;

namespace Librespot.Cli;

/// <summary>The librespot binary: a headless Spotify Connect receiver (upstream <c>src/main.rs</c>).</summary>
public static class Program
{
    /// <summary>Upstream <c>RECONNECT_RATE_LIMIT_WINDOW</c>.</summary>
    private static readonly TimeSpan ReconnectRateLimitWindow = TimeSpan.FromSeconds(600);

    /// <summary>Upstream <c>DISCOVERY_RETRY_TIMEOUT</c>.</summary>
    private static readonly TimeSpan DiscoveryRetryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Upstream <c>RECONNECT_RATE_LIMIT</c>.</summary>
    private const int ReconnectRateLimit = 5;

    /// <summary>Upstream <c>CARGO_PKG_DESCRIPTION</c>.</summary>
    internal const string PackageDescription = "An open source client library for Spotify, with support for Spotify Connect";

    /// <summary>Upstream <c>CARGO_PKG_REPOSITORY</c>.</summary>
    internal const string PackageRepository = "https://github.com/librespot-org/librespot";

    /// <summary>The environment variable that replaces upstream's <c>RUST_LOG</c> (same <c>env_logger</c> filter syntax).</summary>
    public const string LogEnvironmentVariable = "LIBRESPOT_LOG";

    /// <summary>Spotify's Desktop app uses these. Some of these are only available when requested with Spotify's client IDs.</summary>
    public static IReadOnlyList<string> OAuthScopes { get; } =
    [
        "app-remote-control",
        "playlist-modify",
        "playlist-modify-private",
        "playlist-modify-public",
        "playlist-read",
        "playlist-read-collaborative",
        "playlist-read-private",
        "streaming",
        "ugc-image-upload",
        "user-follow-modify",
        "user-follow-read",
        "user-library-modify",
        "user-library-read",
        "user-modify",
        "user-modify-playback-state",
        "user-modify-private",
        "user-personalized",
        "user-read-birthdate",
        "user-read-currently-playing",
        "user-read-email",
        "user-read-play-history",
        "user-read-playback-position",
        "user-read-playback-state",
        "user-read-private",
        "user-read-recently-played",
        "user-top-read",
    ];

    private static ILoggerFactory? s_loggerFactory;

    private static ILogger Log => LibrespotLog.CreateLogger(typeof(Program).FullName!);

    /// <summary>Entry point: runs upstream <c>main</c> and returns the exit code.</summary>
    /// <param name="args">The command line arguments.</param>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (CliExitException e)
        {
            return e.ExitCode;
        }
        finally
        {
            FlushLogging();
        }
    }

    /// <summary>Upstream <c>device_id</c>: the lowercase hex SHA-1 of the device name.</summary>
    /// <param name="name">The device name.</param>
    public static string DeviceId(string name) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(name)));

    /// <summary>Upstream <c>usage</c>: the help text.</summary>
    /// <param name="program">The program name (upstream <c>args[0]</c>).</param>
    /// <param name="opts">The option table.</param>
    public static string Usage(string program, Options opts)
    {
        var version = GetVersionString();
        var brief = $"{version}\n\n{PackageDescription}\n\n{PackageRepository}\n\nUsage: {program} [<Options>]";
        return opts.Usage(brief);
    }

    /// <summary>
    /// Upstream <c>setup_logging</c>: configures <see cref="LibrespotLog.Factory"/> with a console logger on stderr.
    /// The <c>env_logger</c> filter comes from <see cref="LogEnvironmentVariable"/> (upstream <c>RUST_LOG</c>) when
    /// set, otherwise from <paramref name="verbose"/> / <paramref name="quiet"/>.
    /// </summary>
    /// <param name="quiet">Only log warnings and errors.</param>
    /// <param name="verbose">Log everything of librespot.</param>
    public static void SetupLogging(bool quiet, bool verbose)
    {
        var config = Environment.GetEnvironmentVariable(LogEnvironmentVariable);
        var filter = config is not null
            ? LogFilter.Parse(config, Console.Error)
            : LogFilter.Parse(verbose ? "libmdns=info,librespot=trace" : quiet ? "libmdns=warn,librespot=warn" : "libmdns=info,librespot=info", Console.Error);

        var factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            // env_logger writes to stderr; stdout may carry audio (`--backend pipe`).
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.IncludeScopes = false;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
                o.ColorBehavior = LoggerColorBehavior.Default;
            })
            .AddFilter(filter.IsEnabled));

        var previous = Interlocked.Exchange(ref s_loggerFactory, factory);
        LibrespotLog.Factory = factory;
        previous?.Dispose();

        if (config is not null)
        {
            if (verbose)
            {
                Log.LogWarning("`--verbose` flag overidden by `{Variable}` environment variable", LogEnvironmentVariable);
            }
            else if (quiet)
            {
                Log.LogWarning("`--quiet` flag overidden by `{Variable}` environment variable", LogEnvironmentVariable);
            }
        }
        else if (verbose && quiet)
        {
            Log.LogWarning("`--verbose` and `--quiet` are mutually exclusive. Logging can not be both verbose and quiet. Using verbose mode.");
        }
    }

    /// <summary>Upstream <c>list_backends</c>.</summary>
    /// <param name="stdout">Where to print the list.</param>
    public static void ListBackends(TextWriter stdout)
    {
        stdout.WriteLine("Available backends: ");
        for (var idx = 0; idx < AudioBackends.Backends.Count; idx++)
        {
            var name = AudioBackends.Backends[idx].Name;
            stdout.WriteLine(idx == 0 ? $"- {name} (default)" : $"- {name}");
        }
    }

    /// <summary>
    /// Upstream <c>parse_file_size</c>: a number with an optional SI (<c>K</c>, <c>M</c>, ... base 1000) or IEC
    /// (<c>Ki</c>, <c>Mi</c>, ... base 1024) suffix, in bytes.
    /// </summary>
    /// <param name="input">The size, e.g. <c>16G</c> or <c>1.5Mi</c>.</param>
    /// <exception cref="ParseFileSizeException">The input is not a valid size.</exception>
    public static ulong ParseFileSize(string input)
    {
        var end = input.Length;
        if (end == 0)
        {
            throw new ParseFileSizeException(ParseFileSizeException.Reason.EmptyInput);
        }

        var suffix = input[--end];
        var suffixLen = 0;

        var iec = suffix is 'i' or 'I';

        if (iec)
        {
            suffixLen++;
            if (end == 0)
            {
                throw new ParseFileSizeException(ParseFileSizeException.Reason.InvalidSuffix);
            }

            suffix = input[--end];
        }

        ulong @base = iec ? 1024UL : 1000UL;

        suffixLen++;
        uint exponent;
        switch (char.ToUpperInvariant(suffix))
        {
            case >= '0' and <= '9' when !iec:
                suffixLen--;
                exponent = 0;
                break;
            case 'K': exponent = 1; break;
            case 'M': exponent = 2; break;
            case 'G': exponent = 3; break;
            case 'T': exponent = 4; break;
            case 'P': exponent = 5; break;
            case 'E': exponent = 6; break;
            case 'Z': exponent = 7; break;
            case 'Y': exponent = 8; break;
            default:
                throw new ParseFileSizeException(ParseFileSizeException.Reason.InvalidSuffix);
        }

        var numStr = input[..^suffixLen];
        if (RustParse.F64(numStr) is not { } num)
        {
            throw new ParseFileSizeException(
                ParseFileSizeException.Reason.InvalidNumber,
                numStr.Length == 0 ? "cannot parse float from empty string" : "invalid float literal");
        }

        if (!double.IsFinite(num))
        {
            throw new ParseFileSizeException(ParseFileSizeException.Reason.NotFinite);
        }

        // `base.pow(exponent)` wraps for Z/Y like upstream's release build; `as u64` saturates.
        ulong multiplier = 1;
        for (var i = 0; i < exponent; i++)
        {
            multiplier = unchecked(multiplier * @base);
        }

        return SaturatingToU64(num * multiplier);
    }

    /// <summary>Upstream <c>get_version_string()</c>.</summary>
    public static string GetVersionString() =>
        $"librespot {LibrespotVersion.SemVer} {LibrespotVersion.ShaShort} (Built on {LibrespotVersion.BuildDate}, Build ID: {LibrespotVersion.BuildId}, Profile: {BuildProfile})";

    private static string BuildProfile =>
#if DEBUG
        "debug";
#else
        "release";
#endif

    /// <summary>
    /// The program name shown in the usage (upstream <c>args[0]</c>): the executable's file name, or <c>librespot</c>
    /// when running through the <c>dotnet</c> host.
    /// </summary>
    public static string ProgramName()
    {
        var path = Environment.ProcessPath;
        var name = path is null ? null : Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(name) || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? "librespot" : name;
    }

    /// <summary><c>coefficient_to_duration(cf).as_millis()</c>: nanoseconds rounded like <c>Duration::from_secs_f64</c>, then truncated to ms.</summary>
    internal static ulong CoefficientToMillis(double coefficient)
    {
        var secs = -1.0 / Math.Log(coefficient) / AudioConstants.SamplesPerSecond;
        var nanos = SaturatingToU64(Math.Round(secs * 1e9));
        return nanos / 1_000_000;
    }

    /// <summary>Rust's saturating <c>f64 as u64</c> (NaN becomes 0).</summary>
    private static ulong SaturatingToU64(double value) =>
        double.IsNaN(value) || value <= 0 ? 0 : value >= 18446744073709551615.0 ? ulong.MaxValue : (ulong)value;

    /// <summary>Upstream <c>{e}</c> of an error: librespot and OAuth errors render their own text, anything else is classified first.</summary>
    private static string Display(Exception e) => e is LibrespotException or OAuthException ? e.Message : LibrespotException.From(e).Message;

    /// <summary>Flushes and stops the console logger (its queue is processed on a background thread).</summary>
    private static void FlushLogging()
    {
        if (Interlocked.Exchange(ref s_loggerFactory, null) is { } factory)
        {
            LibrespotLog.Factory = null!;
            factory.Dispose();
        }
    }

    /// <summary>Upstream <c>exit(1)</c> from a callback outside the main flow: flush the log and end the process.</summary>
    private static void ExitProcess(int code)
    {
        FlushLogging();
        Environment.Exit(code);
    }

    private static Session NewSession(Setup setup)
    {
        var session = new Session(setup.SessionConfig, setup.Cache);
        // Upstream `check_catalogue` logs an error and exits the process for non-premium accounts.
        session.UnsupportedAccountType += _ => ExitProcess(1);
        return session;
    }

    /// <summary>Upstream <c>main</c>.</summary>
    private static async Task<int> RunAsync(string[] args)
    {
        // Upstream sets RUST_BACKTRACE=full here (through `set_env_var`); that is a Rust runtime knob without a
        // .NET equivalent, and the pulseaudio PULSE_PROP_* variables are not needed without a pulseaudio backend.
        var setup = Setup.Get(args);

        Credentials? lastCredentials = null;
        Spirc? spirc = null;
        Task? spircTask = null;
        var autoConnectTimes = new List<long>();
        LibrespotDiscovery? discovery = null;
        var connecting = false;
        PlayerEventHandler? eventHandler = null;

        var session = NewSession(setup);
        // The spirc task that last used `session`; the session is disposed (upstream: dropped) after it ended.
        Task sessionUser = Task.CompletedTask;
        var detached = new List<Task>();

        if (setup.ZeroconfBackend is { } zeroconfBackend)
        {
            // When started at boot as a service discovery may fail due to it
            // trying to bind to interfaces before the network is actually up.
            // This could be prevented in systemd by starting the service after
            // network-online.target but it requires that a wait-online.service is
            // also enabled which is not always the case since a wait-online.service
            // can potentially hang the boot process until it times out in certain situations.
            // This allows for discovery to retry every 10 secs in the 1st min of uptime
            // before giving up thus papering over the issue and not holding up the boot process.
            while (true)
            {
                var deviceId = setup.SessionConfig.DeviceId;
                var clientId = setup.SessionConfig.ClientId;

                try
                {
                    discovery = LibrespotDiscovery.Builder(deviceId, clientId)
                        .Name(setup.ConnectConfig.Name)
                        .DeviceType(setup.ConnectConfig.DeviceType)
                        .IsGroup(setup.ConnectConfig.IsGroup)
                        .Port(setup.ZeroconfPort)
                        .ZeroconfIp(setup.ZeroconfIp)
                        .ZeroconfBackend(zeroconfBackend)
                        .Launch();
                    break;
                }
                catch (Exception e)
                {
                    // Upstream compares `System::uptime()` (whole seconds) with 1.
                    if (Environment.TickCount64 / 1000 <= 1)
                    {
                        Log.LogDebug("Retrying to initialise discovery: {Error}", Display(e));
                        await Task.Delay(DiscoveryRetryTimeout).ConfigureAwait(false);
                    }
                    else
                    {
                        Log.LogDebug("System uptime > 1 min, not retrying to initialise discovery");
                        Log.LogWarning("Could not initialise discovery: {Error}", Display(e));
                        break;
                    }
                }
            }
        }

        if (setup.Credentials is { } credentials)
        {
            lastCredentials = credentials;
            connecting = true;
        }
        else if (setup.EnableDeviceAuth)
        {
            DeviceAuthClient client;
            try
            {
                client = new DeviceAuthClientBuilder(setup.SessionConfig.ClientId, OAuthScopes).Build();
            }
            catch (Exception e)
            {
                Log.LogError("Failed to create device auth client: {Error}", Display(e));
                throw new CliExitException(1);
            }

            using (client)
            {
                OAuthToken oauthToken;
                try
                {
                    oauthToken = await client.GetAccessTokenAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.LogError("Failed to get Spotify access token: {Error}", Display(e));
                    throw new CliExitException(1);
                }

                lastCredentials = Credentials.WithAccessToken(oauthToken.AccessToken);
                connecting = true;
            }
        }
        else if (setup.EnableOAuth)
        {
            var portStr = setup.OAuthPort is { } port ? $":{port}" : string.Empty;
            OAuthClient client;
            try
            {
                client = new OAuthClientBuilder(setup.SessionConfig.ClientId, $"http://127.0.0.1{portStr}/login", OAuthScopes)
                    .OpenInBrowser()
                    .Build();
            }
            catch (Exception e)
            {
                Log.LogError("Failed to create OAuth client: {Error}", Display(e));
                throw new CliExitException(1);
            }

            using (client)
            {
                OAuthToken oauthToken;
                try
                {
                    // Upstream uses the blocking `get_access_token`; nothing else runs yet, so awaiting is equivalent.
                    oauthToken = await client.GetAccessTokenAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.LogError("Failed to get Spotify access token: {Error}", Display(e));
                    throw new CliExitException(1);
                }

                lastCredentials = Credentials.WithAccessToken(oauthToken.AccessToken);
                connecting = true;
            }
        }
        else if (discovery is null)
        {
            Log.LogError("Discovery is unavailable and no credentials provided. Authentication is not possible.");
            throw new CliExitException(1);
        }

        IMixer mixer;
        try
        {
            mixer = setup.Mixer(setup.MixerConfig);
        }
        catch (Exception why)
        {
            Log.LogError("{Error}", Display(why));
            throw new CliExitException(1);
        }

        var playerConfig = setup.PlayerConfig;

        var softVolume = mixer.GetSoftVolume();
        var format = setup.Format;
        var backend = setup.Backend;
        var device = setup.Device;
        var player = Player.Create(playerConfig, session, softVolume, () => backend(device, format));

        if (setup.PlayerEventProgram is { } playerEventProgram)
        {
            eventHandler = new PlayerEventHandler(player.GetPlayerEventChannel(), playerEventProgram);

            if (setup.EmitSinkEvents)
            {
                player.SetSinkEventCallback(sinkStatus => PlayerEventHandler.RunProgramOnSinkEvents(sinkStatus, playerEventProgram));
            }
        }

        // `tokio::signal::ctrl_c()`: the first Ctrl-C ends the main loop, a second one ends the graceful shutdown.
        var ctrlC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCtrlC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctrlCCount = 0;
        ConsoleCancelEventHandler onCancelKeyPress = (_, e) =>
        {
            switch (Interlocked.Increment(ref ctrlCCount))
            {
                case 1:
                    e.Cancel = true;
                    ctrlC.TrySetResult();
                    break;
                case 2:
                    e.Cancel = true;
                    secondCtrlC.TrySetResult();
                    break;
            }
        };
        Console.CancelKeyPress += onCancelKeyPress;

        var discoveryCredentials = discovery?.ReadCredentialsAsync().GetAsyncEnumerator();
        Task<bool>? discoveryNext = null;

        try
        {
            while (true)
            {
                detached.RemoveAll(t => t.IsCompleted);

                // `_ = async {}, if player.is_invalid()`
                if (player.IsInvalid)
                {
                    Log.LogError("Player shut down unexpectedly");
                    throw new CliExitException(1);
                }

                // `_ = async {}, if connecting && last_credentials.is_some()`
                if (connecting && lastCredentials is not null)
                {
                    if (session.IsInvalid)
                    {
                        var oldSession = session;
                        session = NewSession(setup);
                        player.SetSession(session);
                        detached.Add(DisposeSessionAfterAsync(oldSession, sessionUser));
                    }

                    var connectConfig = setup.ConnectConfig;

                    try
                    {
                        (spirc, spircTask) = await Spirc.CreateAsync(connectConfig, session, lastCredentials, player, mixer).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Log.LogError("could not initialize spirc: {Error}", Display(e));
                        throw new CliExitException(1);
                    }

                    sessionUser = spircTask;
                    connecting = false;
                    continue;
                }

                var waits = new List<Task>(3) { ctrlC.Task };
                if (discoveryCredentials is not null)
                {
                    discoveryNext ??= discoveryCredentials.MoveNextAsync().AsTask();
                    waits.Add(discoveryNext);
                }

                if (spircTask is not null && !connecting)
                {
                    waits.Add(spircTask);
                }

                await Task.WhenAny(waits).ConfigureAwait(false);

                if (ctrlC.Task.IsCompleted)
                {
                    break;
                }

                if (discoveryNext is { IsCompleted: true } next)
                {
                    discoveryNext = null;

                    bool hasCredentials;
                    try
                    {
                        hasCredentials = await next.ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Log.LogDebug("Discovery stream failed: {Error}", Display(e));
                        hasCredentials = false;
                    }

                    if (!hasCredentials)
                    {
                        Log.LogError("Discovery stopped unexpectedly");
                        throw new CliExitException(1);
                    }

                    lastCredentials = discoveryCredentials!.Current;
                    autoConnectTimes.Clear();

                    if (spirc is not null)
                    {
                        try
                        {
                            spirc.Shutdown();
                        }
                        catch (LibrespotException e)
                        {
                            Log.LogError("error sending spirc shutdown message: {Error}", e.Message);
                        }

                        spirc = null;
                    }

                    if (spircTask is not null)
                    {
                        // Continue shutdown in its own task
                        detached.Add(ObserveAsync(spircTask));
                        spircTask = null;
                    }

                    if (!session.IsInvalid)
                    {
                        session.Shutdown();
                    }

                    connecting = true;
                    continue;
                }

                if (spircTask is { IsCompleted: true } finished && !connecting)
                {
                    await ObserveAsync(finished).ConfigureAwait(false);
                    spircTask = null;

                    Log.LogWarning("Spirc shut down unexpectedly");

                    autoConnectTimes.RemoveAll(t => Stopwatch.GetElapsedTime(t) >= ReconnectRateLimitWindow);
                    var reconnectExceedsRateLimit = autoConnectTimes.Count > ReconnectRateLimit;

                    if (lastCredentials is not null && !reconnectExceedsRateLimit)
                    {
                        autoConnectTimes.Add(Stopwatch.GetTimestamp());
                        if (!session.IsInvalid)
                        {
                            session.Shutdown();
                        }

                        connecting = true;
                    }
                    else
                    {
                        Log.LogError("Spirc shut down too often. Not reconnecting automatically.");
                        throw new CliExitException(1);
                    }
                }
            }

            Log.LogInformation("Gracefully shutting down");

            var shutdownTasks = new List<Task>();

            // Shutdown spirc if necessary
            if (spirc is not null)
            {
                try
                {
                    spirc.Shutdown();
                }
                catch (LibrespotException e)
                {
                    Log.LogError("error sending spirc shutdown message: {Error}", e.Message);
                }

                if (spircTask is not null)
                {
                    shutdownTasks.Add(ObserveAsync(spircTask));
                }
            }

            if (discovery is not null)
            {
                shutdownTasks.Add(ObserveAsync(discovery.ShutdownAsync()));
            }

            await Task.WhenAny(Task.WhenAll(shutdownTasks), secondCtrlC.Task).ConfigureAwait(false);

            if (secondCtrlC.Task.IsCompleted)
            {
                // Upstream returns from `main` right away; the remaining work is abandoned with the process.
                return 0;
            }

            // Upstream drops its locals in reverse declaration order: the player (joining its thread, which ends the
            // event channel), then the session, then the event handler (joining its thread).
            player.Dispose();
            if (discoveryCredentials is not null)
            {
                // The last MoveNextAsync may still be pending (Ctrl-C broke the loop); it completes because
                // ShutdownAsync completed the events channel. Disposing an async iterator that is still suspended in
                // an await throws NotSupportedException, so drain that step first.
                if (discoveryNext is not null)
                {
                    await ObserveAsync(discoveryNext).ConfigureAwait(false);
                }

                await discoveryCredentials.DisposeAsync().ConfigureAwait(false);
            }

            await session.DisposeAsync().ConfigureAwait(false);
            eventHandler?.Dispose();
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKeyPress;
        }
    }

    /// <summary>Awaits a background task and logs (instead of throwing) a failure, so no exception goes unobserved.</summary>
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.LogError("Background task failed: {Error}", Display(e));
        }
    }

    /// <summary>
    /// Upstream drops a replaced session once nothing references it any more; here it is disposed once the spirc
    /// task that used it has ended.
    /// </summary>
    private static async Task DisposeSessionAfterAsync(Session session, Task user)
    {
        await ObserveAsync(user).ConfigureAwait(false);
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.LogError("Background task failed: {Error}", Display(e));
        }
    }
}

/// <summary>Upstream <c>ParseFileSizeError</c>.</summary>
public sealed class ParseFileSizeException : Exception
{
    /// <summary>The upstream variant.</summary>
    public enum Reason
    {
        /// <summary>empty argument</summary>
        EmptyInput,

        /// <summary>invalid suffix</summary>
        InvalidSuffix,

        /// <summary>invalid number</summary>
        InvalidNumber,

        /// <summary>non-finite number specified</summary>
        NotFinite,
    }

    /// <summary>Creates the error; <see cref="Exception.Message"/> is upstream's <c>#[error]</c> text.</summary>
    /// <param name="cause">The variant.</param>
    /// <param name="parseError">For <see cref="Reason.InvalidNumber"/>: the <c>ParseFloatError</c> text.</param>
    public ParseFileSizeException(Reason cause, string? parseError = null)
        : base(cause switch
        {
            Reason.EmptyInput => "empty argument",
            Reason.InvalidSuffix => "invalid suffix",
            Reason.InvalidNumber => $"invalid number: {parseError}",
            _ => "non-finite number specified",
        })
    {
        Cause = cause;
    }

    /// <summary>The upstream variant.</summary>
    public Reason Cause { get; }
}

/// <summary>
/// The <c>env_logger</c> filter syntax (<c>RUST_LOG</c>): comma-separated <c>target=level</c>, <c>level</c> or
/// <c>target</c> directives. Rust targets are mapped to .NET logger categories: <c>librespot_core::session</c>
/// becomes <c>Librespot.Core.Session</c>, <c>librespot</c> becomes <c>Librespot</c> and <c>libmdns</c> (the
/// upstream mDNS crate) becomes <c>Librespot.Discovery.Mdns</c>; targets that already look like .NET categories
/// (containing a '.') are used as they are. As in <c>env_logger</c>, the longest matching prefix wins, a spec without
/// any directive enables errors only, and targets without a matching directive are off.
/// </summary>
/// <remarks>Deviation: the optional <c>/regex</c> message filter of <c>env_logger</c> is not supported and ignored.</remarks>
public sealed class LogFilter
{
    private readonly List<(string? Category, LogLevel Level)> _directives;

    private LogFilter(List<(string? Category, LogLevel Level)> directives)
    {
        // Sorted by name length so the most specific (last) match wins, like env_logger.
        _directives = [.. directives.OrderBy(d => d.Category?.Length ?? 0)];
    }

    /// <summary>The parsed directives (category prefix or null for all, minimum level), least specific first.</summary>
    public IReadOnlyList<(string? Category, LogLevel Level)> Directives => _directives;

    /// <summary>Parses an <c>env_logger</c> filter spec.</summary>
    /// <param name="spec">The spec, e.g. <c>info,librespot_core=debug</c>.</param>
    /// <param name="warnings">Where invalid directives are reported (like env_logger's "warning: invalid logging spec").</param>
    public static LogFilter Parse(string spec, TextWriter? warnings = null)
    {
        var directives = new List<(string? Category, LogLevel Level)>();

        var slash = spec.IndexOf('/');
        var mods = slash >= 0 ? spec[..slash] : spec;
        if (slash >= 0 && spec.IndexOf('/', slash + 1) >= 0)
        {
            warnings?.WriteLine($"warning: invalid logging spec '{spec}', ignoring it (too many '/'s)");
            return new LogFilter(directives);
        }

        foreach (var s in mods.Split(',').Select(ss => ss.Trim()))
        {
            if (s.Length == 0)
            {
                continue;
            }

            var parts = s.Split('=');
            switch (parts.Length)
            {
                case 1:
                    // if the single argument is a log-level string or number, treat that as a global fallback
                    if (ParseLevel(parts[0].Trim()) is { } level)
                    {
                        directives.Add((null, level));
                    }
                    else
                    {
                        directives.Add((MapTarget(parts[0]), LogLevel.Trace));
                    }

                    break;
                case 2 when parts[1].Length == 0:
                    directives.Add((MapTarget(parts[0]), LogLevel.Trace));
                    break;
                case 2:
                    if (ParseLevel(parts[1].Trim()) is { } l)
                    {
                        directives.Add((MapTarget(parts[0]), l));
                    }
                    else
                    {
                        warnings?.WriteLine($"warning: invalid logging spec '{parts[1]}', ignoring it");
                    }

                    break;
                default:
                    warnings?.WriteLine($"warning: invalid logging spec '{s}', ignoring it");
                    break;
            }
        }

        if (directives.Count == 0)
        {
            // env_logger: "Adds the default filter if none exist"
            directives.Add((null, LogLevel.Error));
        }

        return new LogFilter(directives);
    }

    /// <summary>Whether a message of <paramref name="level"/> from <paramref name="category"/> is logged.</summary>
    /// <param name="category">The logger category.</param>
    /// <param name="level">The message level.</param>
    public bool IsEnabled(string? category, LogLevel level)
    {
        for (var i = _directives.Count - 1; i >= 0; i--)
        {
            var (prefix, minLevel) = _directives[i];
            if (prefix is null || (category is not null && category.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return minLevel != LogLevel.None && level >= minLevel;
            }
        }

        return false;
    }

    /// <summary>The <c>log</c> crate's <c>LevelFilter::from_str</c> (case-insensitive level names).</summary>
    private static LogLevel? ParseLevel(string s) => s.ToLowerInvariant() switch
    {
        "off" => LogLevel.None,
        "error" => LogLevel.Error,
        "warn" => LogLevel.Warning,
        "info" => LogLevel.Information,
        "debug" => LogLevel.Debug,
        "trace" => LogLevel.Trace,
        _ => null,
    };

    /// <summary>Maps a Rust log target (module path) to the .NET logger category prefix.</summary>
    internal static string MapTarget(string target)
    {
        if (target == "libmdns")
        {
            return "Librespot.Discovery.Mdns";
        }

        if (target.Contains('.'))
        {
            return target;
        }

        // The crate name's words become namespace segments (`librespot_core` -> `Librespot.Core`); module names are
        // PascalCased into one segment (`audio_backend` -> `AudioBackend`), as the port names its namespaces/types.
        var modules = target.Split("::");
        var segments = modules[0]
            .Split('_')
            .Select(Capitalise)
            .Concat(modules.Skip(1).Select(module => string.Concat(module.Split('_').Select(Capitalise))))
            .Where(segment => segment.Length > 0);
        return string.Join('.', segments);

        static string Capitalise(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
    }
}
