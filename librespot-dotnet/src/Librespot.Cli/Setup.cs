// Port of src/main.rs (the `Setup` struct and `get_setup`).
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Librespot.Connect;
using Librespot.Core;
using Librespot.Core.Logging;
using Librespot.Discovery;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Mixer;
using Microsoft.Extensions.Logging;
using O = Librespot.Cli.CliOptions;
using LibrespotDiscovery = Librespot.Discovery.Discovery;

namespace Librespot.Cli;

/// <summary>
/// Thrown where upstream calls <c>std::process::exit(code)</c>; <see cref="Program.Main"/> flushes the log and
/// returns <see cref="ExitCode"/>. Using an exception keeps the setup testable.
/// </summary>
/// <param name="exitCode">The process exit code.</param>
public sealed class CliExitException(int exitCode) : Exception($"exit({exitCode})")
{
    /// <summary>The process exit code.</summary>
    public int ExitCode { get; } = exitCode;
}

/// <summary>Everything <see cref="Program"/> needs to run, derived from the command line and environment (upstream <c>Setup</c>).</summary>
public sealed record Setup
{
    /// <summary>The sample format handed to the sink.</summary>
    public required AudioFormat Format { get; init; }

    /// <summary>The audio backend.</summary>
    public required SinkBuilder Backend { get; init; }

    /// <summary>The backend's output device, or null for its default.</summary>
    public string? Device { get; init; }

    /// <summary>The mixer factory.</summary>
    public required MixerFn Mixer { get; init; }

    /// <summary>The credentials/volume/audio cache, or null when unavailable.</summary>
    public Cache? Cache { get; init; }

    /// <summary>The player configuration.</summary>
    public required PlayerConfig PlayerConfig { get; init; }

    /// <summary>The session configuration.</summary>
    public required SessionConfig SessionConfig { get; init; }

    /// <summary>The connect device configuration.</summary>
    public required ConnectConfig ConnectConfig { get; init; }

    /// <summary>The mixer configuration.</summary>
    public required MixerConfig MixerConfig { get; init; }

    /// <summary>Credentials from the command line or cache, or null.</summary>
    public Credentials? Credentials { get; init; }

    /// <summary>Perform interactive OAuth sign in.</summary>
    public bool EnableOAuth { get; init; }

    /// <summary>Perform OAuth sign in by pairing a code.</summary>
    public bool EnableDeviceAuth { get; init; }

    /// <summary>The port of the OAuth redirect server, or null for none.</summary>
    public ushort? OAuthPort { get; init; }

    /// <summary>The port the zeroconf server listens on (0: any).</summary>
    public ushort ZeroconfPort { get; init; }

    /// <summary>The <c>--onevent</c> program, or null.</summary>
    public string? PlayerEventProgram { get; init; }

    /// <summary>Run <see cref="PlayerEventProgram"/> on sink events.</summary>
    public bool EmitSinkEvents { get; init; }

    /// <summary>The interface addresses zeroconf binds to (empty: all).</summary>
    public IReadOnlyList<IPAddress> ZeroconfIp { get; init; } = [];

    /// <summary>The zeroconf backend, or null when discovery is disabled.</summary>
    public DnsSdServiceBuilder? ZeroconfBackend { get; init; }

    private const ushort ValidInitialVolumeMin = 0;
    private const ushort ValidInitialVolumeMax = 100;
    private const double ValidVolumeRangeMin = 0.0;
    private const double ValidVolumeRangeMax = 100.0;
    private const double ValidNormalisationKneeMin = 0.0;
    private const double ValidNormalisationKneeMax = 10.0;
    private const double ValidNormalisationPregainMin = -10.0;
    private const double ValidNormalisationPregainMax = 10.0;
    private const double ValidNormalisationThresholdMin = -10.0;
    private const double ValidNormalisationThresholdMax = 0.0;
    private const ulong ValidNormalisationAttackMin = 1;
    private const ulong ValidNormalisationAttackMax = 500;
    private const ulong ValidNormalisationReleaseMin = 1;
    private const ulong ValidNormalisationReleaseMax = 1000;

    private static ILogger Log => LibrespotLog.CreateLogger(typeof(Program).FullName!);

    /// <summary>Upstream <c>get_setup</c> for the current process: the command line, environment and console.</summary>
    /// <param name="args">The command line arguments (without the program name).</param>
    /// <exception cref="CliExitException">Upstream exits the process (help, version, invalid options, ...).</exception>
    public static Setup Get(string[] args) => Get(new SetupContext
    {
        Program = Program.ProgramName(),
        Args = args,
        Environment = SetupContext.ReadEnvironment(),
        ConfigureLogging = Program.SetupLogging,
    });

    /// <summary>Upstream <c>get_setup</c>.</summary>
    /// <param name="ctx">The command line, environment, console writers and logging hook.</param>
    /// <exception cref="CliExitException">Upstream exits the process (help, version, invalid options, ...).</exception>
    public static Setup Get(SetupContext ctx)
    {
        var stdout = ctx.Stdout;
        var opts = CliOptions.Build();

        // Upstream drops (with a message) arguments that are not valid Unicode; .NET strings are always UTF-16.
        var args = ctx.Args;

        Options.Matches matches;
        try
        {
            matches = opts.Parse(args);
        }
        catch (GetoptsException e)
        {
            ctx.Stderr.WriteLine($"Error parsing command line options: {e.Message}");
            stdout.WriteLine($"\n{Program.Usage(ctx.Program, opts)}");
            throw new CliExitException(1);
        }

        static string StrippedEnvKey(string k)
        {
            // `trim_start_matches` removes the prefix repeatedly.
            while (k.StartsWith("LIBRESPOT_", StringComparison.Ordinal))
            {
                k = k["LIBRESPOT_".Length..];
            }

            return k.Replace('_', '-').ToLowerInvariant();
        }

        var envVars = new List<(string Key, string Value)>();
        foreach (var (key, value) in ctx.Environment)
        {
            if (key.StartsWith("LIBRESPOT_", StringComparison.Ordinal))
            {
                var strippedKey = StrippedEnvKey(key);
                // We only care about long option/flag names.
                if (strippedKey.Length > 1 && matches.OptDefined(strippedKey))
                {
                    envVars.Add((key, value));
                }
            }
        }

        bool OptPresent(string opt) => matches.OptPresent(opt) || envVars.Any(kv => StrippedEnvKey(kv.Key) == opt);

        string? OptStr(string opt)
        {
            if (matches.OptPresent(opt))
            {
                return matches.OptStr(opt);
            }

            foreach (var (k, v) in envVars)
            {
                if (StrippedEnvKey(k) == opt)
                {
                    return v;
                }
            }

            return null;
        }

        if (OptPresent(O.Help))
        {
            stdout.WriteLine(Program.Usage(ctx.Program, opts));
            throw new CliExitException(0);
        }

        if (OptPresent(CliOptions.Version))
        {
            stdout.WriteLine(Program.GetVersionString());
            throw new CliExitException(0);
        }

        ctx.ConfigureLogging?.Invoke(OptPresent(O.Quiet), OptPresent(O.Verbose));

        Log.LogInformation("{Version}", Program.GetVersionString());

        if (envVars.Count != 0)
        {
            Log.LogTrace("Environment variable(s):");

            foreach (var (k, v) in envVars)
            {
                if (k is "LIBRESPOT_PASSWORD" or "LIBRESPOT_USERNAME" or "LIBRESPOT_ACCESS_TOKEN")
                {
                    Log.LogTrace("\t\t{Key}=\"XXXXXXXX\"", k);
                }
                else if (v.Length == 0)
                {
                    Log.LogTrace("\t\t{Key}=", k);
                }
                else
                {
                    Log.LogTrace("\t\t{Key}=\"{Value}\"", k, v);
                }
            }
        }

        // `args` in upstream includes the program name at index 0.
        var fullArgs = new List<string>(args.Count + 1) { ctx.Program };
        fullArgs.AddRange(args);

        if (fullArgs.Count > 1)
        {
            Log.LogTrace("Command line argument(s):");

            for (var index = 0; index < fullArgs.Count; index++)
            {
                var key = fullArgs[index];
                var opt = key.TrimStart('-');
                var eq = opt.IndexOf('=');
                if (eq >= 0)
                {
                    opt = opt[..eq];
                }

                if (index > 0
                    && key.StartsWith('-')
                    && fullArgs[index - 1] != key
                    && matches.OptDefined(opt)
                    && matches.OptPresent(opt))
                {
                    if (opt is O.Password or O.PasswordShort or O.Username or O.UsernameShort or O.AccessToken or O.AccessTokenShort)
                    {
                        // Don't log creds.
                        Log.LogTrace("\t\t{Opt} \"XXXXXXXX\"", opt);
                    }
                    else
                    {
                        var value = matches.OptStr(opt) ?? string.Empty;
                        if (value.Length == 0)
                        {
                            Log.LogTrace("\t\t{Opt}", opt);
                        }
                        else
                        {
                            Log.LogTrace("\t\t{Opt} \"{Value}\"", opt, value);
                        }
                    }
                }
            }
        }

        // #[cfg(not(feature = "alsa-backend"))]
        foreach (var a in new[] { O.MixerType, O.AlsaMixerDevice, O.AlsaMixerIndex, O.AlsaMixerControl })
        {
            if (OptPresent(a))
            {
                Log.LogWarning("Alsa specific options have no effect if the alsa backend is not enabled at build time.");
                break;
            }
        }

        var backendName = OptStr(O.Backend);
        if (backendName == "?")
        {
            Program.ListBackends(stdout);
            throw new CliExitException(0);
        }

        static string FormatFlag(string @long, string @short) =>
            @short.Length == 0 ? $"`--{@long}`" : $"`--{@long}` / `-{@short}`";

        void InvalidErrorMsg(string @long, string @short, string invalid, string validValues, string defaultValue)
        {
            var flag = FormatFlag(@long, @short);
            Log.LogError("Invalid {Flag}: \"{Invalid}\"", flag, invalid);

            if (validValues.Length != 0)
            {
                stdout.WriteLine($"Valid {flag} values: {validValues}");
            }

            if (defaultValue.Length != 0)
            {
                stdout.WriteLine($"Default: {defaultValue}");
            }
        }

        CliExitException EmptyStringErrorMsg(string @long, string @short)
        {
            Log.LogError("`--{Long}` / `-{Short}` can not be an empty string", @long, @short);
            return new CliExitException(1);
        }

        var backend = AudioBackends.Find(backendName);
        if (backend is null)
        {
            InvalidErrorMsg(O.Backend, O.BackendShort, OptStr(O.Backend) ?? string.Empty, string.Empty, string.Empty);
            Program.ListBackends(stdout);
            throw new CliExitException(1);
        }

        var format = AudioFormatExtensions.Default;
        if (OptStr(O.Format) is { } formatStr)
        {
            format = AudioFormatExtensions.Parse(formatStr) ?? throw Invalid(() => InvalidErrorMsg(
                O.Format,
                O.FormatShort,
                formatStr,
                "F64, F32, S32, S24, S24_3, S16",
                AudioFormatExtensions.Default.ToString()));
        }

        var device = OptStr(O.Device);
        if (device is not null)
        {
            if (device == "?")
            {
                // The backends list their devices (or print their usage) and exit the process themselves.
                backend(device, format).Dispose();
                throw new CliExitException(0);
            }
            else if (device.Length == 0)
            {
                throw EmptyStringErrorMsg(O.Device, O.DeviceShort);
            }
        }

        // #[cfg(not(feature = "alsa-backend"))]: the mixer type is not configurable.
        string? mixerType = null;

        var mixer = Mixers.Find(mixerType) ?? throw Invalid(() => InvalidErrorMsg(
            O.MixerType,
            O.MixerTypeShort,
            OptStr(O.MixerType) ?? string.Empty,
            "alsa, softvol",
            "softvol"));

        const bool isAlsaMixer = false;

        MixerConfig mixerConfig;
        {
            var mixerDefaultConfig = new MixerConfig();

            // #[cfg(not(feature = "alsa-backend"))]
            var index = mixerDefaultConfig.Index;
            var mixerDevice = mixerDefaultConfig.Device;
            var control = mixerDefaultConfig.Control;

            var volumeRange = isAlsaMixer ? 0.0 : VolumeCtrl.DefaultDbRange;
            if (OptStr(O.VolumeRange) is { } range)
            {
                if (RustParse.F64(range) is { } value && value >= ValidVolumeRangeMin && value <= ValidVolumeRangeMax)
                {
                    volumeRange = value;
                }
                else
                {
                    var validValues = $"{F64(ValidVolumeRangeMin)} - {F64(ValidVolumeRangeMax)}";
                    var defaultValue = F64(VolumeCtrl.DefaultDbRange);
                    InvalidErrorMsg(O.VolumeRange, O.VolumeRangeShort, range, validValues, defaultValue);
                    throw new CliExitException(1);
                }
            }

            VolumeCtrl volumeCtrl = new VolumeCtrl.Log(volumeRange);
            if (OptStr(O.VolumeCtrl) is { } volumeCtrlStr)
            {
                volumeCtrl = Playback.VolumeCtrl.ParseWithRange(volumeCtrlStr, volumeRange) ?? throw Invalid(() => InvalidErrorMsg(
                    O.VolumeCtrl,
                    O.VolumeCtrlShort,
                    volumeCtrlStr,
                    "cubic, fixed, linear, log",
                    "log"));
            }

            mixerConfig = new MixerConfig
            {
                Device = mixerDevice,
                Control = control,
                Index = index,
                VolumeCtrl = volumeCtrl,
            };
        }

        var tmpDir = SessionConfig.Default().TmpDir;
        if (OptStr(O.TempDir) is { } p)
        {
            tmpDir = p;
            try
            {
                Directory.CreateDirectory(tmpDir);
            }
            catch (Exception e)
            {
                Log.LogError("could not create or access specified tmp directory: {Error}", e.Message);
                throw new CliExitException(1);
            }
        }

        var enableOAuth = OptPresent(O.EnableOAuth);
        var enableDeviceAuth = OptPresent(O.EnableDeviceAuth);

        if (enableOAuth && enableDeviceAuth)
        {
            Log.LogError("`--{EnableOAuth}` / `-{EnableOAuthShort}` and `--{EnableDeviceAuth}` are mutually exclusive.", O.EnableOAuth, O.EnableOAuthShort, O.EnableDeviceAuth);
            throw new CliExitException(1);
        }

        Cache? cache;
        string? credDir;
        {
            var volumeDir = OptStr(O.SystemCache) ?? OptStr(O.Cache);

            credDir = OptPresent(O.DisableCredentialCache) ? null : volumeDir;

            var audioDir = OptPresent(O.DisableAudioCache)
                ? null
                : OptStr(O.Cache) is { } cachePath ? Path.Combine(cachePath, "files") : null;

            ulong? limit = null;
            if (audioDir is not null && OptStr(O.CacheSizeLimit) is { } sizeLimit)
            {
                try
                {
                    limit = Program.ParseFileSize(sizeLimit);
                }
                catch (ParseFileSizeException e)
                {
                    InvalidErrorMsg(O.CacheSizeLimit, O.CacheSizeLimitShort, e.Message, string.Empty, string.Empty);
                    throw new CliExitException(1);
                }
            }

            if (audioDir is null && OptPresent(O.CacheSizeLimit))
            {
                Log.LogWarning(
                    "Without a `--{Cache}` / `-{CacheShort}` path, and/or if the `--{DisableAudioCache}` / `-{DisableAudioCacheShort}` flag is set, `--{CacheSizeLimit}` / `-{CacheSizeLimitShort}` has no effect.",
                    O.Cache, O.CacheShort, O.DisableAudioCache, O.DisableAudioCacheShort, O.CacheSizeLimit, O.CacheSizeLimitShort);
            }

            try
            {
                cache = new Cache(credDir, volumeDir, audioDir, limit);
            }
            catch (Exception e)
            {
                Log.LogWarning("Cannot create cache: {Error}", e.Message);
                cache = null;
            }

            if ((enableOAuth || enableDeviceAuth) && (cache is null || credDir is null))
            {
                Log.LogWarning("Credential caching is unavailable, but advisable when using OAuth login.");
            }
        }

        Credentials? credentials;
        {
            var cachedCreds = cache?.Credentials();

            if (OptStr(O.AccessToken) is { } accessToken)
            {
                if (accessToken.Length == 0)
                {
                    throw EmptyStringErrorMsg(O.AccessToken, O.AccessTokenShort);
                }

                credentials = Core.Credentials.WithAccessToken(accessToken);
            }
            else if (OptStr(O.Username) is { } username)
            {
                if (username.Length == 0)
                {
                    throw EmptyStringErrorMsg(O.Username, O.UsernameShort);
                }

                if (OptPresent(O.Password))
                {
                    Log.LogError("Invalid `--{Password}` / `-{PasswordShort}`: Password authentication no longer supported, use OAuth", O.Password, O.PasswordShort);
                    throw new CliExitException(1);
                }

                if (cachedCreds is not null && cachedCreds.Username == username)
                {
                    Log.LogTrace("Using cached credentials for specified username.");
                    credentials = cachedCreds;
                }
                else
                {
                    Log.LogTrace("No cached credentials for specified username.");
                    credentials = null;
                }
            }
            else
            {
                if (cachedCreds is not null)
                {
                    Log.LogTrace("Using cached credentials.");
                }

                credentials = cachedCreds;
            }
        }

        // This port always has a zeroconf backend (the built-in mDNS responder), so the
        // "librespot compiled without zeroconf backend" reason never applies.
        string? noDiscoveryReason = OptPresent(O.DisableDiscovery)
            ? $"the `--{O.DisableDiscovery}` / `-{O.DisableDiscoveryShort}` flag set"
            : null;

        if (credentials is null && noDiscoveryReason is not null && !enableOAuth && !enableDeviceAuth)
        {
            Log.LogError("Credentials are required if discovery and oauth login are disabled.");
            throw new CliExitException(1);
        }

        ushort? oauthPort;
        if (OptPresent(O.OAuthPort))
        {
            if (enableDeviceAuth)
            {
                Log.LogWarning("With the `--{EnableDeviceAuth}` flag set `--{OAuthPort}` / `-{OAuthPortShort}` has no effect.", O.EnableDeviceAuth, O.OAuthPort, O.OAuthPortShort);
            }

            if (!enableOAuth)
            {
                Log.LogWarning("Without the `--{EnableOAuth}` / `-{EnableOAuthShort}` flag set `--{OAuthPort}` / `-{OAuthPortShort}` has no effect.", O.EnableOAuth, O.EnableOAuthShort, O.OAuthPort, O.OAuthPortShort);
            }

            oauthPort = null;
            if (OptStr(O.OAuthPort) is { } port)
            {
                if (RustParse.U16(port) is { } value)
                {
                    oauthPort = value > 0 ? value : null;
                }
                else
                {
                    InvalidErrorMsg(O.OAuthPort, O.OAuthPortShort, port, $"1 - {ushort.MaxValue}", string.Empty);
                    throw new CliExitException(1);
                }
            }
        }
        else
        {
            oauthPort = 5588;
        }

        if (noDiscoveryReason is not null && OptPresent(O.ZeroconfPort))
        {
            Log.LogWarning("With {Reason} `--{ZeroconfPort}` / `-{ZeroconfPortShort}` has no effect.", noDiscoveryReason, O.ZeroconfPort, O.ZeroconfPortShort);
        }

        ushort zeroconfPort = 0;
        if (noDiscoveryReason is null && OptStr(O.ZeroconfPort) is { } zeroconfPortStr)
        {
            if (RustParse.U16(zeroconfPortStr) is { } value && value != 0)
            {
                zeroconfPort = value;
            }
            else
            {
                InvalidErrorMsg(O.ZeroconfPort, O.ZeroconfPortShort, zeroconfPortStr, $"1 - {ushort.MaxValue}", string.Empty);
                throw new CliExitException(1);
            }
        }

        // #1046: not all connections are supplied an `autoplay` user attribute to run statelessly.
        // This knob allows for a manual override.
        bool? autoplay;
        switch (OptStr(O.Autoplay))
        {
            case { } value:
                switch (value)
                {
                    case "on":
                        autoplay = true;
                        break;
                    case "off":
                        autoplay = false;
                        break;
                    default:
                        InvalidErrorMsg(O.Autoplay, O.AutoplayShort, OptStr(O.Autoplay) ?? string.Empty, "on, off", string.Empty);
                        throw new CliExitException(1);
                }

                break;
            default:
                autoplay = SessionConfig.Default().Autoplay;
                break;
        }

        if (noDiscoveryReason is not null && OptPresent(O.ZeroconfInterface))
        {
            Log.LogWarning("With {Reason} {Flag} has no effect.", noDiscoveryReason, FormatFlag(O.ZeroconfInterface, O.ZeroconfInterfaceShort));
        }

        var zeroconfIp = new List<IPAddress>();
        if (OptPresent(O.ZeroconfInterface))
        {
            if (OptStr(O.ZeroconfInterface) is { } zeroconfIpStr)
            {
                foreach (var s in zeroconfIpStr.Split(','))
                {
                    if (RustParse.IpAddr(s.Trim()) is { } ip)
                    {
                        zeroconfIp.Add(ip);
                    }
                    else
                    {
                        InvalidErrorMsg(O.ZeroconfInterface, O.ZeroconfInterfaceShort, s, "IPv4 and IPv6 addresses", string.Empty);
                        throw new CliExitException(1);
                    }
                }
            }
            else
            {
                Log.LogWarning("Unable to use zeroconf-interface option, default to all interfaces.");
            }
        }

        if (noDiscoveryReason is not null && OptPresent(O.ZeroconfBackend))
        {
            Log.LogWarning("With {Reason} `--{ZeroconfBackend}` / `-{ZeroconfBackendShort}` has no effect.", noDiscoveryReason, O.ZeroconfBackend, O.ZeroconfBackendShort);
        }

        var zeroconfBackendName = OptStr(O.ZeroconfBackend);
        DnsSdServiceBuilder? zeroconfBackend = null;
        if (noDiscoveryReason is null)
        {
            try
            {
                zeroconfBackend = DnsSdBackends.Find(zeroconfBackendName);
            }
            catch (LibrespotException)
            {
                var availableBackends = DnsSdBackends.All.Where(b => b.Builder is not null).Select(b => b.Name);
                var defaultBackend = DnsSdBackends.All.FirstOrDefault(b => b.Builder is not null).Name ?? "<none>";

                InvalidErrorMsg(O.ZeroconfBackend, O.ZeroconfBackendShort, zeroconfBackendName ?? string.Empty, string.Join(", ", availableBackends), defaultBackend);
                throw new CliExitException(1);
            }
        }

        // Upstream reads `--local-file-dir` from the command line only (no environment variable fallback).
        var localFileDirectories = matches.OptStrs(O.LocalFileDir).ToList();

        ConnectConfig connectConfig;
        {
            var connectDefaultConfig = new ConnectConfig();

            var name = OptStr(O.Name);
            if (name is { Length: 0 })
            {
                throw EmptyStringErrorMsg(O.Name, O.NameShort);
            }

            // #[cfg(feature = "pulseaudio-backend")] PULSE_PROP_* variables: no pulseaudio backend in this port.

            ushort? initialVolume = null;
            if (OptStr(O.InitialVolume) is { } initialVolumeStr)
            {
                if (RustParse.U16(initialVolumeStr) is { } volume && volume >= ValidInitialVolumeMin && volume <= ValidInitialVolumeMax)
                {
                    // Upstream computes in f32 and truncates.
                    initialVolume = (ushort)(volume / 100.0f * Playback.VolumeCtrl.MaxVolume);
                }
                else
                {
                    var validValues = $"{ValidInitialVolumeMin} - {ValidInitialVolumeMax}";
                    var defaultValue = connectDefaultConfig.InitialVolume.ToString(CultureInfo.InvariantCulture);
                    InvalidErrorMsg(O.InitialVolume, O.InitialVolumeShort, initialVolumeStr, validValues, defaultValue);
                    throw new CliExitException(1);
                }
            }
            else if (!isAlsaMixer)
            {
                initialVolume = cache?.Volume();
            }

            Core.DeviceType? deviceType = null;
            if (OptStr(O.DeviceType) is { } deviceTypeStr)
            {
                deviceType = DeviceTypeExtensions.Parse(deviceTypeStr) ?? throw Invalid(() => InvalidErrorMsg(
                    O.DeviceType,
                    O.DeviceTypeShort,
                    deviceTypeStr,
                    "computer, tablet, smartphone, speaker, tv, avr, stb, audiodongle, gameconsole, castaudio, castvideo, automobile, smartwatch, chromebook, carthing",
                    DeviceTypeExtensions.Default.ToDisplayString()));
            }

            ushort? volumeSteps = null;
            if (OptStr(O.VolumeSteps) is { } steps)
            {
                volumeSteps = RustParse.U16(steps) ?? throw Invalid(() => InvalidErrorMsg(
                    O.VolumeSteps,
                    O.VolumeStepsShort,
                    steps,
                    "a positive whole number <= 65535",
                    connectDefaultConfig.VolumeSteps.ToString(CultureInfo.InvariantCulture)));
            }

            var isGroup = OptPresent(O.DeviceIsGroup);

            // use config defaults if not provided
            connectConfig = new ConnectConfig
            {
                Name = name ?? connectDefaultConfig.Name,
                DeviceType = deviceType ?? connectDefaultConfig.DeviceType,
                IsGroup = isGroup,
                InitialVolume = initialVolume ?? connectDefaultConfig.InitialVolume,
                DisableVolume = mixerConfig.VolumeCtrl is VolumeCtrl.Fixed,
                VolumeSteps = volumeSteps ?? connectDefaultConfig.VolumeSteps,
                EmitSetQueueEvents = false,
            };
        }

        Uri? proxy = null;
        if ((OptStr(O.Proxy) ?? ctx.Environment.GetValueOrDefault("http_proxy")) is { } proxyStr)
        {
            proxy = ParseProxy(proxyStr);
        }

        ushort? apPort = null;
        if (OptStr(O.ApPort) is { } apPortStr)
        {
            if (RustParse.U16(apPortStr) is { } value && value != 0)
            {
                apPort = value;
            }
            else
            {
                InvalidErrorMsg(O.ApPort, O.ApPortShort, apPortStr, $"1 - {ushort.MaxValue}", string.Empty);
                throw new CliExitException(1);
            }
        }

        var sessionConfig = SessionConfig.Default() with
        {
            DeviceId = Program.DeviceId(connectConfig.Name),
            Proxy = proxy,
            ApPort = apPort,
            TmpDir = tmpDir,
            Autoplay = autoplay,
        };

        PlayerConfig playerConfig;
        {
            var playerDefaultConfig = new PlayerConfig();

            var bitrate = playerDefaultConfig.Bitrate;
            if (OptStr(O.Bitrate) is { } bitrateStr)
            {
                bitrate = BitrateExtensions.Parse(bitrateStr) ?? throw Invalid(() => InvalidErrorMsg(O.Bitrate, O.BitrateShort, bitrateStr, "96, 160, 320", "160"));
            }

            var gapless = !OptPresent(O.DisableGapless);

            var normalisation = OptPresent(O.EnableVolumeNormalisation);

            var normalisationMethod = playerDefaultConfig.NormalisationMethod;
            var normalisationType = playerDefaultConfig.NormalisationType;
            var normalisationPregainDb = playerDefaultConfig.NormalisationPregainDb;
            var normalisationThresholdDbfs = playerDefaultConfig.NormalisationThresholdDbfs;
            var normalisationAttackCf = playerDefaultConfig.NormalisationAttackCf;
            var normalisationReleaseCf = playerDefaultConfig.NormalisationReleaseCf;
            var normalisationKneeDb = playerDefaultConfig.NormalisationKneeDb;

            if (!normalisation)
            {
                foreach (var a in new[]
                         {
                             O.NormalisationMethod, O.NormalisationGainType, O.NormalisationPregain, O.NormalisationThreshold,
                             O.NormalisationAttack, O.NormalisationRelease, O.NormalisationKnee,
                         })
                {
                    if (OptPresent(a))
                    {
                        Log.LogWarning(
                            "Without the `--{EnableVolumeNormalisation}` / `-{EnableVolumeNormalisationShort}` flag normalisation options have no effect.",
                            O.EnableVolumeNormalisation, O.EnableVolumeNormalisationShort);
                        break;
                    }
                }
            }
            else
            {
                if (OptStr(O.NormalisationMethod) is { } method)
                {
                    normalisationMethod = NormalisationMethodExtensions.Parse(method) ?? throw Invalid(() => InvalidErrorMsg(
                        O.NormalisationMethod,
                        O.NormalisationMethodShort,
                        method,
                        "basic, dynamic",
                        playerDefaultConfig.NormalisationMethod.ToString()));
                }

                if (OptStr(O.NormalisationGainType) is { } gainType)
                {
                    normalisationType = NormalisationTypeExtensions.Parse(gainType) ?? throw Invalid(() => InvalidErrorMsg(
                        O.NormalisationGainType,
                        O.NormalisationGainTypeShort,
                        gainType,
                        "track, album, auto",
                        playerDefaultConfig.NormalisationType.ToString()));
                }

                if (OptStr(O.NormalisationPregain) is { } pregain)
                {
                    normalisationPregainDb = RustParse.F64(pregain) is { } value && value >= ValidNormalisationPregainMin && value <= ValidNormalisationPregainMax
                        ? value
                        : throw Invalid(() => InvalidErrorMsg(
                            O.NormalisationPregain,
                            O.NormalisationPregainShort,
                            pregain,
                            $"{F64(ValidNormalisationPregainMin)} - {F64(ValidNormalisationPregainMax)}",
                            F64(playerDefaultConfig.NormalisationPregainDb)));
                }

                if (OptStr(O.NormalisationThreshold) is { } threshold)
                {
                    normalisationThresholdDbfs = RustParse.F64(threshold) is { } value && value >= ValidNormalisationThresholdMin && value <= ValidNormalisationThresholdMax
                        ? value
                        : throw Invalid(() => InvalidErrorMsg(
                            O.NormalisationThreshold,
                            O.NormalisationThresholdShort,
                            threshold,
                            $"{F64(ValidNormalisationThresholdMin)} - {F64(ValidNormalisationThresholdMax)}",
                            F64(playerDefaultConfig.NormalisationThresholdDbfs)));
                }

                if (OptStr(O.NormalisationAttack) is { } attack)
                {
                    normalisationAttackCf = RustParse.U64(attack) is { } value && value >= ValidNormalisationAttackMin && value <= ValidNormalisationAttackMax
                        ? Player.DurationToCoefficient(TimeSpan.FromMilliseconds((long)value))
                        : throw Invalid(() => InvalidErrorMsg(
                            O.NormalisationAttack,
                            O.NormalisationAttackShort,
                            attack,
                            $"{ValidNormalisationAttackMin} - {ValidNormalisationAttackMax}",
                            Program.CoefficientToMillis(playerDefaultConfig.NormalisationAttackCf).ToString(CultureInfo.InvariantCulture)));
                }

                if (OptStr(O.NormalisationRelease) is { } release)
                {
                    normalisationReleaseCf = RustParse.U64(release) is { } value && value >= ValidNormalisationReleaseMin && value <= ValidNormalisationReleaseMax
                        ? Player.DurationToCoefficient(TimeSpan.FromMilliseconds((long)value))
                        : throw Invalid(() => InvalidErrorMsg(
                            O.NormalisationRelease,
                            O.NormalisationReleaseShort,
                            release,
                            $"{ValidNormalisationReleaseMin} - {ValidNormalisationReleaseMax}",
                            Program.CoefficientToMillis(playerDefaultConfig.NormalisationReleaseCf).ToString(CultureInfo.InvariantCulture)));
                }

                if (OptStr(O.NormalisationKnee) is { } knee)
                {
                    normalisationKneeDb = RustParse.F64(knee) is { } value && value >= ValidNormalisationKneeMin && value <= ValidNormalisationKneeMax
                        ? value
                        : throw Invalid(() => InvalidErrorMsg(
                            O.NormalisationKnee,
                            O.NormalisationKneeShort,
                            knee,
                            $"{F64(ValidNormalisationKneeMin)} - {F64(ValidNormalisationKneeMax)}",
                            F64(playerDefaultConfig.NormalisationKneeDb)));
                }
            }

            var dithererName = OptStr(O.Dither);
            DithererBuilder? ditherer;
            switch (dithererName)
            {
                case { } value:
                    if (value == "none")
                    {
                        ditherer = null;
                    }
                    else if (format is AudioFormat.F64 or AudioFormat.F32)
                    {
                        Log.LogError("Dithering is not available with format: {Format}.", format);
                        throw new CliExitException(1);
                    }
                    else
                    {
                        ditherer = Playback.Dither.FindDitherer(dithererName) ?? throw Invalid(() => InvalidErrorMsg(
                            O.Dither,
                            O.DitherShort,
                            OptStr(O.Dither) ?? string.Empty,
                            "none, gpdf, tpdf, tpdf_hp for formats S16, S24, S24_3, S32, none for formats F32, F64",
                            "tpdf for formats S16, S24, S24_3 and none for formats S32, F32, F64"));
                    }

                    break;
                default:
                    ditherer = format is AudioFormat.S16 or AudioFormat.S24 or AudioFormat.S24_3
                        ? playerDefaultConfig.Ditherer
                        : null;
                    break;
            }

            // #[cfg(feature = "passthrough-decoder")]
            var passthrough = OptPresent(O.Passthrough);

            playerConfig = new PlayerConfig
            {
                Bitrate = bitrate,
                Gapless = gapless,
                Passthrough = passthrough,
                Normalisation = normalisation,
                NormalisationType = normalisationType,
                NormalisationMethod = normalisationMethod,
                NormalisationPregainDb = normalisationPregainDb,
                NormalisationThresholdDbfs = normalisationThresholdDbfs,
                NormalisationAttackCf = normalisationAttackCf,
                NormalisationReleaseCf = normalisationReleaseCf,
                NormalisationKneeDb = normalisationKneeDb,
                Ditherer = ditherer,
                PositionUpdateInterval = null,
                LocalFileDirectories = localFileDirectories,
            };
        }

        var playerEventProgram = OptStr(O.OnEvent);
        var emitSinkEvents = OptPresent(O.EmitSinkEvents);

        return new Setup
        {
            Format = format,
            Backend = backend,
            Device = device,
            Mixer = mixer,
            Cache = cache,
            PlayerConfig = playerConfig,
            SessionConfig = sessionConfig,
            ConnectConfig = connectConfig,
            MixerConfig = mixerConfig,
            Credentials = credentials,
            EnableOAuth = enableOAuth,
            EnableDeviceAuth = enableDeviceAuth,
            OAuthPort = oauthPort,
            ZeroconfPort = zeroconfPort,
            PlayerEventProgram = playerEventProgram,
            EmitSinkEvents = emitSinkEvents,
            ZeroconfIp = zeroconfIp,
            ZeroconfBackend = zeroconfBackend,
        };
    }

    /// <summary>Runs <paramref name="report"/> (the upstream <c>invalid_error_msg</c> call) and returns the <c>exit(1)</c> to throw.</summary>
    private static CliExitException Invalid(Action report)
    {
        report();
        return new CliExitException(1);
    }

    /// <summary>Rust's <c>Display</c> for <c>f64</c> (shortest round-trip, no exponent for the values used here).</summary>
    private static string F64(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The proxy check of upstream <c>get_setup</c>: the URL must parse and have a host and a (known default) port.
    /// </summary>
    private static Uri ParseProxy(string s)
    {
        // Deviation: .NET's Uri parser and error texts differ from the `url` crate's.
        Uri url;
        try
        {
            url = new Uri(s, UriKind.Absolute);
        }
        catch (UriFormatException e)
        {
            Log.LogError("Invalid proxy URL: \"{Error}\", only URLs in the format \"http(s)://host:port\" are allowed", e.Message);
            throw new CliExitException(1);
        }

        // `url.port_or_known_default()`: .NET reports -1 for schemes without a known default port.
        if (string.IsNullOrEmpty(url.Host) || url.Port < 0)
        {
            Log.LogError("Invalid proxy url, only URLs on the format \"http(s)://host:port\" are allowed");
            throw new CliExitException(1);
        }

        return url;
    }
}

/// <summary>The inputs of <see cref="Setup.Get(SetupContext)"/>; the defaults are the current process' console.</summary>
public sealed record SetupContext
{
    /// <summary>The program name printed in the usage (upstream <c>args[0]</c>).</summary>
    public string Program { get; init; } = "librespot";

    /// <summary>The command line arguments after the program name.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The environment variables (upstream <c>env::vars_os()</c> / <c>env::var</c>).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Upstream <c>println!</c>.</summary>
    public TextWriter Stdout { get; init; } = Console.Out;

    /// <summary>Upstream <c>eprintln!</c>.</summary>
    public TextWriter Stderr { get; init; } = Console.Error;

    /// <summary>Upstream <c>setup_logging(quiet, verbose)</c>; null leaves logging unchanged.</summary>
    public Action<bool, bool>? ConfigureLogging { get; init; }

    /// <summary>Snapshot of the process environment (names compare case-insensitively on Windows, like <c>env::var</c> there).</summary>
    public static IReadOnlyDictionary<string, string> ReadEnvironment()
    {
        var result = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }
}

/// <summary>Rust's strict <c>FromStr</c> parsing for the primitive types the CLI reads (no whitespace, no culture).</summary>
internal static class RustParse
{
    /// <summary><c>str::parse::&lt;u64&gt;</c>: an optional '+' followed by ASCII digits.</summary>
    public static ulong? U64(string s)
    {
        var digits = s.StartsWith('+') ? s.AsSpan(1) : s.AsSpan();
        if (digits.Length == 0)
        {
            return null;
        }

        ulong value = 0;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }

            try
            {
                value = checked((value * 10) + (ulong)(c - '0'));
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return value;
    }

    /// <summary><c>str::parse::&lt;u32&gt;</c>.</summary>
    public static uint? U32(string s) => U64(s) is { } v && v <= uint.MaxValue ? (uint)v : null;

    /// <summary><c>str::parse::&lt;u16&gt;</c>.</summary>
    public static ushort? U16(string s) => U64(s) is { } v && v <= ushort.MaxValue ? (ushort)v : null;

    /// <summary>
    /// <c>str::parse::&lt;f64&gt;</c>: decimal notation with optional sign and exponent, or inf/infinity/nan
    /// (case-insensitive); no surrounding whitespace, no thousands separators, no hex.
    /// </summary>
    public static double? F64(string s)
    {
        if (s.Length == 0 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]))
        {
            return null;
        }

        var unsigned = s[0] is '+' or '-' ? s[1..] : s;
        var negative = s[0] == '-';
        if (unsigned.Equals("inf", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (unsigned.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            return double.NaN;
        }

        foreach (var c in unsigned)
        {
            if (c is not ((>= '0' and <= '9') or '.' or 'e' or 'E' or '+' or '-'))
            {
                return null;
            }
        }

        return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// <c>str::parse::&lt;IpAddr&gt;</c>: a dotted-quad IPv4 address (no leading zeros) or an IPv6 address without a
    /// scope id. <see cref="IPAddress.TryParse(string, out IPAddress)"/> alone also accepts forms like "1" or "1.2".
    /// </summary>
    public static IPAddress? IpAddr(string s)
    {
        if (s.Contains(':'))
        {
            return !s.Contains('%') && IPAddress.TryParse(s, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : null;
        }

        var parts = s.Split('.');
        if (parts.Length != 4)
        {
            return null;
        }

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0') || part.Any(c => c is < '0' or > '9'))
            {
                return null;
            }

            var value = int.Parse(part, CultureInfo.InvariantCulture);
            if (value > 255)
            {
                return null;
            }

            bytes[i] = (byte)value;
        }

        return new IPAddress(bytes);
    }
}
