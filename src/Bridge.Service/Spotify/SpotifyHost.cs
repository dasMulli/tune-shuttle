using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Bridge.Service.Store;
using Librespot.Connect;
using Librespot.Core;
using Librespot.Core.Connection;
using Librespot.Core.Logging;
using Librespot.Playback;
using Librespot.Playback.Mixer;
using Librespot.Protocol.Authentication;
using Microsoft.Extensions.Options;
using LibrespotDiscovery = Librespot.Discovery.Discovery;

namespace Bridge.Service.Spotify;

/// <summary>
/// Hosts librespot (docs/ARCHITECTURE.md, "Spotify side"): Zeroconf discovery with the configured device name, the credential
/// cache in SQLite, a Session + Spirc + Player with <see cref="AirplaySink"/> as the backend, the reconnect loop from
/// Librespot.Cli, and the mapping of player events to the group player (pause/seek/skip -> flush, track change ->
/// metadata/artwork/progress, volume -> G).
/// </summary>
/// <remarks>
/// <para>
/// The loop follows <c>Librespot.Cli/Program.cs</c> (upstream <c>main.rs</c>): credentials come from the store's cache at
/// start and from every Zeroconf handoff; each handoff replaces the running Spirc and session; a Spirc that ends on its own
/// is restarted at most five times per ten minutes. Unlike the CLI the service never exits: a failed connect is retried
/// with back-off (5 s doubling to 5 min), rejected cached credentials are forgotten and the device waits for the next
/// handoff, and Zeroconf is relaunched when the device name changes.
/// </para>
/// <para>
/// The player lives as long as the host. librespot disposes its sink when the player thread ends (at shutdown, and after a
/// crash), so it gets <see cref="AirplaySink.LibrespotSink"/>, a view that only pauses then: the output manager keeps the
/// sink and tears the receivers down after this host stopped.
/// Receiver buttons (event channel <c>sendMediaRemoteCommand</c>) and level changes made in the web app reach Spirc through
/// <see cref="IPlaybackOutput.RemoteCommands"/> and <see cref="IPlaybackOutput.SpotifyVolumeUpdates"/>.
/// </para>
/// </remarks>
public sealed class SpotifyHost : BackgroundService
{
    /// <summary>Upstream <c>RECONNECT_RATE_LIMIT_WINDOW</c>.</summary>
    private static readonly TimeSpan ReconnectRateLimitWindow = TimeSpan.FromSeconds(600);

    /// <summary>Upstream <c>RECONNECT_RATE_LIMIT</c>.</summary>
    private const int ReconnectRateLimit = 5;

    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxConnectRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DiscoveryRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SpircShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly IPlaybackOutput _output;
    private readonly IArtworkLoader _artwork;
    private readonly RuntimeState _runtime;
    private readonly EventHub _hub;
    private readonly DtoFactory _dtos;
    private readonly BridgePaths _paths;
    private readonly IOptions<BridgeOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<ControlRequest> _control = Channel.CreateUnbounded<ControlRequest>(new UnboundedChannelOptions { SingleReader = true });
    private volatile Spirc? _spirc;
    private BridgeMixer? _mixer;

    /// <summary>Creates the host.</summary>
    public SpotifyHost(
        IBridgeStore store,
        AirplaySink sink,
        IPlaybackOutput output,
        IArtworkLoader artwork,
        RuntimeState runtime,
        EventHub hub,
        DtoFactory dtos,
        BridgePaths paths,
        IOptions<BridgeOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<SpotifyHost> logger)
    {
        _loggerFactory = loggerFactory;
        Store = store;
        Sink = sink;
        Logger = logger;
        _output = output;
        _artwork = artwork;
        _runtime = runtime;
        _hub = hub;
        _dtos = dtos;
        _paths = paths;
        _options = options;
    }

    private enum ControlRequest
    {
        Forget,
        Restart,
    }

    /// <summary>True while a Spirc (Spotify Connect device) is running.</summary>
    public bool IsConnected => _spirc is not null;

    private IBridgeStore Store { get; }

    private AirplaySink Sink { get; }

    private ILogger Logger { get; }

    /// <summary>
    /// Forwards a playback command to Spirc (<c>POST /playback</c>, receiver buttons): <c>play</c>, <c>pause</c>,
    /// <c>next</c>, <c>prev</c>, <c>playpause</c>, and the DACP-style names receivers send (<c>paus</c>, <c>nitm</c>,
    /// <c>pitm</c>). False when Spotify is not connected.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown command.</exception>
    public bool TryCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Action<Spirc> action = command.Trim().ToLowerInvariant() switch
        {
            "play" => s => s.Play(),
            "pause" or "paus" => s => s.Pause(),
            "playpause" or "toggle" => s => s.PlayPause(),
            "next" or "nitm" => s => s.Next(),
            "prev" or "previous" or "pitm" => s => s.Prev(),
            _ => throw new ArgumentException($"Unknown playback command '{command}' (play, pause, next, prev).", nameof(command)),
        };
        var spirc = _spirc;
        if (spirc is null)
        {
            return false;
        }

        try
        {
            action(spirc);
            return true;
        }
        catch (LibrespotException ex)
        {
            Logger.LogWarning("Spotify command {Command} failed: {Error}", command, ex.Message);
            return false;
        }
    }

    /// <summary>Forgets the cached credentials and disconnects (<c>DELETE /spotify</c>); the device waits for a new handoff.</summary>
    public async Task ForgetAsync(CancellationToken cancellationToken)
    {
        await Store.ClearSpotifyCredentialsAsync(cancellationToken).ConfigureAwait(false);
        _control.Writer.TryWrite(ControlRequest.Forget);
        Logger.LogInformation("Spotify credentials forgotten");
    }

    /// <summary>Relaunches Zeroconf and Spirc with the current device name (after a settings change).</summary>
    public void RequestRestart() => _control.Writer.TryWrite(ControlRequest.Restart);

    /// <summary>Librespot credentials from the cache row.</summary>
    public static Credentials ToCredentials(SpotifyCredentialRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Credentials { Username = row.Username, AuthType = (AuthenticationType)row.AuthType, AuthData = row.AuthData };
    }

    /// <summary>The cache row for a session's reusable credentials (what librespot's file cache would store).</summary>
    public static SpotifyCredentialRow ToRow(string username, byte[] reusableAuthData) =>
        new(username, (int)AuthenticationType.AuthenticationStoredSpotifyCredentials, reusableAuthData);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = await BridgeSettings.LoadAsync(Store, _options.Value, stoppingToken).ConfigureAwait(false);
        _runtime.Spotify = new SpotifyStatus(false, null, settings.DeviceName);
        if (!_options.Value.EnableSpotify)
        {
            Logger.LogInformation("Spotify Connect is off (Bridge:EnableSpotify=false)");
            return;
        }

        // librespot logs through its own static factory; route it into the host's providers (console, file, ring buffer).
        // Set only when librespot runs: the factory is process-wide and test hosts come and go.
        LibrespotLog.Factory = _loggerFactory;
        var deviceId = await DeviceIdAsync(stoppingToken).ConfigureAwait(false);
        Directory.CreateDirectory(_paths.TempDirectory);
        var sessionConfig = SessionConfig.Default() with { DeviceId = deviceId, TmpDir = _paths.TempDirectory };
        var initialLevel = await _output.CurrentLevelAsync(stoppingToken).ConfigureAwait(false) ?? 50;
        var mixer = new BridgeMixer(PlayerEventMapper.ToSpotifyVolume(initialLevel));
        _mixer = mixer;
        var session = new Session(sessionConfig, cache: null);
        var player = Player.Create(new PlayerConfig { Bitrate = Bitrate.Bitrate320, Gapless = true }, session, new NoOpVolume(), () => Sink.LibrespotSink);
        var events = player.GetPlayerEventChannel();
        var mapper = new PlayerEventMapper(_output, _artwork, _runtime, _hub, Logger);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var mapping = mapper.RunAsync(events.Reader, lifetime.Token);
        var remote = PumpRemoteCommandsAsync(lifetime.Token);
        var volume = PumpVolumeAsync(lifetime.Token);
        var loop = new ConnectLoop(this, sessionConfig, session, player, mixer);
        try
        {
            await loop.RunAsync(settings.DeviceName, stoppingToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // The librespot player thread died: Spotify Connect is gone until the service restarts; the web app and the
            // receivers stay up (the CLI exits with 1 here).
            Logger.LogCritical(ex, "Spotify Connect stopped");
        }
        finally
        {
            await loop.ShutdownAsync().ConfigureAwait(false);
            _spirc = null;
            await lifetime.CancelAsync().ConfigureAwait(false);

            // The player thread ends once its command channel completes; the sink is paused by Spirc's shutdown, so no
            // Write is blocked (the output manager stops after this host and still drains the ring meanwhile).
            player.Dispose();
            events.Dispose();
            await loop.Session.DisposeAsync().ConfigureAwait(false);
            await mapping.ConfigureAwait(false);
            await ObserveAsync(remote).ConfigureAwait(false);
            await ObserveAsync(volume).ConfigureAwait(false);
            _runtime.Spotify = _runtime.Spotify with { Connected = false, User = null };
        }
    }

    private async Task<string> DeviceIdAsync(CancellationToken cancellationToken)
    {
        var stored = await Store.GetSettingAsync(BridgeSettingKeys.SpotifyDeviceId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        // Upstream derives the id from a SHA-1 (40 hex digits); a random one of the same shape, kept forever.
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        await Store.SetSettingAsync(BridgeSettingKeys.SpotifyDeviceId, id, cancellationToken).ConfigureAwait(false);
        return id;
    }

    private async Task PumpRemoteCommandsAsync(CancellationToken cancellationToken)
    {
        await foreach (var command in _output.RemoteCommands.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (!TryCommand(command))
                {
                    Logger.LogInformation("Receiver button {Command} ignored: Spotify is not connected", command);
                }
            }
            catch (ArgumentException ex)
            {
                Logger.LogInformation("Receiver button ignored: {Error}", ex.Message);
            }
        }
    }

    private async Task PumpVolumeAsync(CancellationToken cancellationToken)
    {
        await foreach (var level in _output.SpotifyVolumeUpdates.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var spirc = _spirc;
            var mixer = _mixer;
            if (spirc is null || mixer is null || PlayerEventMapper.ToLevel(mixer.Volume) == level)
            {
                continue;
            }

            try
            {
                spirc.SetVolume(PlayerEventMapper.ToSpotifyVolume(level));
            }
            catch (LibrespotException ex)
            {
                Logger.LogDebug("Could not update the Spotify volume: {Error}", ex.Message);
            }
        }
    }

    private async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            _hub.Publish(new BridgeEvent.StatusChanged(await _dtos.StatusAsync(cancellationToken).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or OperationCanceledException or ObjectDisposedException)
        {
            Logger.LogDebug("Status not published: {Error}", ex.Message);
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex) when (ex is LibrespotException or IOException or ChannelClosedException)
        {
            Logger.LogWarning("Spotify background task failed: {Error}", ex.Message);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Spotify background task failed unexpectedly");
        }
    }

    /// <summary>The session/Spirc/Zeroconf state machine of one host lifetime (upstream <c>main</c>'s loop).</summary>
    private sealed class ConnectLoop(SpotifyHost host, SessionConfig sessionConfig, Session session, Player player, BridgeMixer mixer)
    {
        private readonly List<long> _autoConnectTimes = [];
        private readonly List<Task> _detached = [];
        private LibrespotDiscovery? _discovery;
        private IAsyncEnumerator<Credentials>? _handoffs;
        private Task<bool>? _nextHandoff;
        private Task<bool>? _nextControl;
        private Spirc? _spirc;
        private Task? _spircTask;
        private Credentials? _credentials;
        private bool _fromCache;
        private bool _connecting;
        private int _failures;
        private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
        private string _name = string.Empty;

        public Session Session { get; private set; } = session;

        private ILogger Logger => host.Logger;

        public async Task RunAsync(string deviceName, CancellationToken cancellationToken)
        {
            _name = deviceName;
            if (await host.Store.GetSpotifyCredentialsAsync(cancellationToken).ConfigureAwait(false) is { } cached)
            {
                _credentials = ToCredentials(cached);
                _fromCache = true;
                _connecting = true;
                Logger.LogInformation("Using the cached Spotify credentials of {User}", cached.Username);
            }

            await LaunchDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            if (_discovery is null && _credentials is null)
            {
                Logger.LogError("Zeroconf is unavailable and no credentials are cached: the device cannot be selected in Spotify until the next restart or settings change");
            }

            var stopped = Task.Delay(Timeout.Infinite, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                _detached.RemoveAll(t => t.IsCompleted);
                if (player.IsInvalid)
                {
                    throw new InvalidOperationException("The librespot player shut down unexpectedly.");
                }

                if (_connecting && _credentials is not null && DateTimeOffset.UtcNow >= _retryAt)
                {
                    await ConnectAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // One pending wait per source, replaced only after it completed (a new WaitToReadAsync on the single-reader
                // channel would cancel the previous one each round).
                _nextControl ??= host._control.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var waits = new List<Task>(5) { stopped, _nextControl };
                if (_handoffs is not null)
                {
                    _nextHandoff ??= _handoffs.MoveNextAsync().AsTask();
                    waits.Add(_nextHandoff);
                }

                if (_spircTask is not null)
                {
                    waits.Add(_spircTask);
                }

                if (_connecting && _credentials is not null)
                {
                    var wait = _retryAt - DateTimeOffset.UtcNow;
                    waits.Add(Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, cancellationToken));
                }

                await Task.WhenAny(waits).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (_nextControl.IsCompleted)
                {
                    _nextControl = null;
                    while (host._control.Reader.TryRead(out var request))
                    {
                        await HandleControlAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (_nextHandoff is { IsCompleted: true } next)
                {
                    _nextHandoff = null;
                    await OnHandoffAsync(next, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_spircTask is { IsCompleted: true } finished)
                {
                    await OnSpircEndedAsync(finished, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async Task ShutdownAsync()
        {
            await StopSpircAsync().ConfigureAwait(false);
            await StopDiscoveryAsync().ConfigureAwait(false);
            foreach (var task in _detached)
            {
                await host.ObserveAsync(task).ConfigureAwait(false);
            }
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (Session.IsInvalid)
            {
                await ReplaceSessionAsync().ConfigureAwait(false);
            }

            var level = await host._output.CurrentLevelAsync(cancellationToken).ConfigureAwait(false) ?? PlayerEventMapper.ToLevel(mixer.Volume);
            var config = new ConnectConfig { Name = _name, DeviceType = DeviceType.Speaker, InitialVolume = PlayerEventMapper.ToSpotifyVolume(level) };
            try
            {
                (_spirc, _spircTask) = await Spirc.CreateAsync(config, Session, _credentials!, player, mixer, cancellationToken).ConfigureAwait(false);
                host._spirc = _spirc;
                _connecting = false;
                _failures = 0;
                var user = Session.Username;
                Logger.LogInformation("Spotify Connect device {Name} is up for {User}", _name, user);
                await host.Store.SaveSpotifyCredentialsAsync(ToRow(user, Session.AuthData), cancellationToken).ConfigureAwait(false);
                _fromCache = true;
                host._runtime.Spotify = new SpotifyStatus(true, user, _name);
                await host.PublishStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ApAuthenticationException ex) when (ex.Cause == ApAuthenticationException.Reason.LoginFailed)
            {
                Logger.LogError("Spotify rejected the {Source} credentials: {Error}; waiting for the device to be selected in a Spotify app", _fromCache ? "cached" : "handed-over", ex.Message);
                if (_fromCache)
                {
                    await host.Store.ClearSpotifyCredentialsAsync(cancellationToken).ConfigureAwait(false);
                }

                _credentials = null;
                _connecting = false;
                await ReplaceSessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Network, TLS, protobuf, JSON or Spotify errors all mean "try again later" here; the CLI exits instead,
                // a service keeps the device alive (back-off below).
                _failures++;
                var delay = TimeSpan.FromTicks(Math.Min(MaxConnectRetryDelay.Ticks, ConnectRetryDelay.Ticks * (1L << Math.Min(_failures - 1, 10))));
                _retryAt = DateTimeOffset.UtcNow + delay;
                Logger.LogWarning("Connecting to Spotify failed: {Error}; retrying in {Delay:0} s", ex.Message, delay.TotalSeconds);
                await ReplaceSessionAsync().ConfigureAwait(false);
            }
        }

        private async Task HandleControlAsync(ControlRequest request, CancellationToken cancellationToken)
        {
            switch (request)
            {
                case ControlRequest.Forget:
                    await StopSpircAsync().ConfigureAwait(false);
                    if (!Session.IsInvalid)
                    {
                        Session.Shutdown();
                    }

                    _credentials = null;
                    _connecting = false;
                    host._runtime.Spotify = new SpotifyStatus(false, null, _name);
                    await host.PublishStatusAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case ControlRequest.Restart:
                    var settings = await BridgeSettings.LoadAsync(host.Store, host._options.Value, cancellationToken).ConfigureAwait(false);
                    if (settings.DeviceName == _name)
                    {
                        break;
                    }

                    Logger.LogInformation("Spotify Connect device renamed {Old} -> {New}; relaunching", _name, settings.DeviceName);
                    _name = settings.DeviceName;
                    await StopSpircAsync().ConfigureAwait(false);
                    if (!Session.IsInvalid)
                    {
                        Session.Shutdown();
                    }

                    await StopDiscoveryAsync().ConfigureAwait(false);
                    await LaunchDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                    _connecting = _credentials is not null;
                    _retryAt = DateTimeOffset.MinValue;
                    host._runtime.Spotify = new SpotifyStatus(false, null, _name);
                    await host.PublishStatusAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        private async Task OnHandoffAsync(Task<bool> next, CancellationToken cancellationToken)
        {
            bool hasCredentials;
            try
            {
                hasCredentials = await next.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LibrespotException or IOException or OperationCanceledException)
            {
                Logger.LogDebug("Zeroconf stream failed: {Error}", ex.Message);
                hasCredentials = false;
            }

            if (!hasCredentials)
            {
                Logger.LogError("Zeroconf stopped unexpectedly; relaunching it in {Delay}", DiscoveryRetryDelay);
                await StopDiscoveryAsync().ConfigureAwait(false);
                await Task.Delay(DiscoveryRetryDelay, cancellationToken).ConfigureAwait(false);
                await LaunchDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _credentials = _handoffs!.Current;
            _fromCache = false;
            _autoConnectTimes.Clear();
            _failures = 0;
            _retryAt = DateTimeOffset.MinValue;
            Logger.LogInformation("Spotify handoff from Zeroconf for {User}", _credentials.Username ?? "?");
            await StopSpircAsync().ConfigureAwait(false);
            if (!Session.IsInvalid)
            {
                Session.Shutdown();
            }

            _connecting = true;
        }

        private async Task OnSpircEndedAsync(Task finished, CancellationToken cancellationToken)
        {
            await host.ObserveAsync(finished).ConfigureAwait(false);
            _spircTask = null;
            _spirc = null;
            host._spirc = null;
            Logger.LogWarning("Spirc shut down unexpectedly");
            host._runtime.Spotify = new SpotifyStatus(false, null, _name);
            await host.PublishStatusAsync(cancellationToken).ConfigureAwait(false);

            _autoConnectTimes.RemoveAll(t => Stopwatch.GetElapsedTime(t) >= ReconnectRateLimitWindow);
            if (_credentials is not null && _autoConnectTimes.Count <= ReconnectRateLimit)
            {
                _autoConnectTimes.Add(Stopwatch.GetTimestamp());
                if (!Session.IsInvalid)
                {
                    Session.Shutdown();
                }

                _connecting = true;
            }
            else
            {
                Logger.LogError("Spirc shut down too often; waiting for the device to be selected in a Spotify app");
                _connecting = false;
            }
        }

        private async Task StopSpircAsync()
        {
            var spirc = _spirc;
            var task = _spircTask;
            _spirc = null;
            _spircTask = null;
            host._spirc = null;
            if (spirc is not null)
            {
                try
                {
                    spirc.Shutdown();
                }
                catch (LibrespotException ex)
                {
                    Logger.LogDebug("Spirc shutdown message not sent: {Error}", ex.Message);
                }
            }

            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(SpircShutdownTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Logger.LogWarning("Spirc did not stop within {Timeout}; continuing", SpircShutdownTimeout);
                    _detached.Add(task);
                }
                catch (Exception ex) when (ex is LibrespotException or IOException or OperationCanceledException or ChannelClosedException)
                {
                    Logger.LogDebug("Spirc ended with {Error}", ex.Message);
                }
            }
        }

        private async Task ReplaceSessionAsync()
        {
            var old = Session;
            Session = new Session(sessionConfig, cache: null);
            player.SetSession(Session);
            if (!old.IsInvalid)
            {
                old.Shutdown();
            }

            await old.DisposeAsync().ConfigureAwait(false);
        }

        private async Task LaunchDiscoveryAsync(CancellationToken cancellationToken)
        {
            // Upstream retries while the network is not up yet (service start at boot); the service keeps trying a minute.
            for (var attempt = 1; attempt <= 6 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    _discovery = LibrespotDiscovery.Builder(sessionConfig.DeviceId, sessionConfig.ClientId)
                        .Name(_name)
                        .DeviceType(DeviceType.Speaker)
                        .Port((ushort)Math.Clamp(host._options.Value.ZeroconfPort, 0, ushort.MaxValue))
                        .Launch();
                    _handoffs = _discovery.ReadCredentialsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                    _nextHandoff = null;
                    Logger.LogInformation("Spotify Connect device {Name} announced via Zeroconf on port {Port}", _name, _discovery.Port);
                    return;
                }
                catch (Exception ex) when (ex is LibrespotException or IOException or SocketException)
                {
                    Logger.LogWarning("Zeroconf could not start (attempt {Attempt}): {Error}", attempt, ex.Message);
                    await Task.Delay(DiscoveryRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task StopDiscoveryAsync()
        {
            var discovery = _discovery;
            var handoffs = _handoffs;
            var next = _nextHandoff;
            _discovery = null;
            _handoffs = null;
            _nextHandoff = null;
            if (discovery is not null)
            {
                await host.ObserveAsync(discovery.ShutdownAsync()).ConfigureAwait(false);
            }

            if (handoffs is not null)
            {
                // A pending MoveNextAsync completes once the shutdown completed the event channel; drain it before disposing.
                if (next is not null)
                {
                    await host.ObserveAsync(next).ConfigureAwait(false);
                }

                try
                {
                    await handoffs.DisposeAsync().ConfigureAwait(false);
                }
                catch (NotSupportedException)
                {
                    // still suspended in an await (cancellation raced the shutdown); nothing left to release
                }
            }
        }
    }
}
