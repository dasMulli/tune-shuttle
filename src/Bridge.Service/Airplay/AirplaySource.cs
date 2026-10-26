using System.Globalization;
using Airplay2.Receiver;
using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Bridge.Service.Spotify;
using Bridge.Service.Store;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Airplay;

/// <summary>
/// Hosts the <see cref="AirplayReceiver"/> in the bridge (SPEC-receiver.md 8.3): builds it from the settings
/// (<see cref="AirplaySettings"/>) and <see cref="BridgeOptions.Airplay"/>, gives it the sender's clock and the shared
/// <see cref="IPtpPort"/>, implements <see cref="IReceiverAudioOutput"/> against the <see cref="OutputManager"/> behind the
/// <see cref="SourceSwitch"/> gate, maps the receiver's events to claims, releases and the AirPlay playback view, applies
/// settings changes (3.3) and exposes its state for <c>DtoFactory</c> (<see cref="IAirplayApiView"/>).
/// </summary>
/// <remarks>
/// <para>
/// Output (SPEC-receiver.md 5.5, 8.2, 8.3): <see cref="WriteAsync"/> writes to the active player only while
/// <c>SourceSwitch.MayWrite(Airplay)</c> and only to a player this source started (a write into a stopped player blocks on
/// its full ring); frames of a non-owner are counted and dropped. The player commands are made only while AirPlay owns the
/// output; a resume, a level and a now-playing made before the claim completed are remembered and replayed by
/// <see cref="ResumeIfPlayingAsync"/>. <see cref="ReleaseAsync"/> pauses first, then releases.
/// </para>
/// <para>
/// Playback view (SPEC-receiver.md 8.2, 9.1): title, artist, album and duration come from the coalesced now-playing call,
/// the position from its buffer-derived <c>elapsed</c> advanced by the time since while playing, the artwork from the
/// sender's artwork with its <c>rtptime</c> as the URL version. It is kept in <c>RuntimeState.AirplayPlayback</c> and
/// published only while AirPlay owns the output.
/// </para>
/// </remarks>
public sealed class AirplaySource : IHostedService, IReceiverAudioOutput, IAirplayApiView, IAsyncDisposable
{
    /// <summary>The <see cref="DecoderStatus"/> while the AAC decoder is loaded (SPEC-receiver.md 7.2, 9.1).</summary>
    public const string DecoderAac = "AAC-LC (libavcodec)";

    /// <summary>The <see cref="DecoderStatus"/> without an AAC decoder: the ALAC-only variant (SPEC-receiver.md R3, R4).</summary>
    public const string DecoderAlacOnly = "AAC decoder not loaded: ALAC only";

    private readonly IOptions<BridgeOptions> _options;
    private readonly IPtpPort _ptpPort;
    private readonly IAirplaySourceHost _host;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _viewLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _startup = Task.CompletedTask;
    private AirplayReceiver? _receiver;
    private Task _pump = Task.CompletedTask;
    private AirplaySettings? _settings;
    private bool _failed;
    private bool _disposed;

    // Output state (SPEC-receiver.md 8.2): what the receiver asked for, and what reached the player.
    private volatile bool _wantPlaying;
    private volatile IGroupPlayer? _startedPlayer;
    private int? _pendingLevel;
    private PendingNowPlaying? _pendingNowPlaying;
    private long _droppedFrames;

    // Playback view (SPEC-receiver.md 8.2, 9.1).
    private TrackMetadata? _metadata;
    private TimeSpan _position;
    private long _positionAt;
    private bool _playing;
    private AirplayArtwork? _artwork;
    private uint? _artworkSentKey;
    private PlaybackDto? _lastPublished;
    private StreamType? _streamType;
    private string? _format;
    private PlayoutStatistics? _lastPlayout;
    private PtpSlaveStatistics? _lastPtp;

    /// <summary>Creates the source over the bridge's services; the receiver is built in <see cref="StartAsync"/> (SPEC-receiver.md 8.3).</summary>
    public AirplaySource(
        IOptions<BridgeOptions> options,
        SourceSwitch sources,
        IPtpPort ptpPort,
        IBridgeStore store,
        RuntimeState runtime,
        EventHub hub,
        IServiceProvider services,
        ILoggerFactory loggerFactory)
        : this(options, ptpPort, new BridgeAirplaySourceHost(sources, store, runtime, hub, options, services, loggerFactory.CreateLogger<AirplaySource>()), loggerFactory)
    {
    }

    /// <summary>Creates the source over a host (tests: a stub player and switch).</summary>
    internal AirplaySource(IOptions<BridgeOptions> options, IPtpPort ptpPort, IAirplaySourceHost host, ILoggerFactory loggerFactory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ptpPort);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options;
        _ptpPort = ptpPort;
        _host = host;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AirplaySource>();
        _time = time ?? TimeProvider.System;
        _positionAt = _time.GetTimestamp();
    }

    /// <summary>Test hook: adjusts the receiver options before the receiver is built (loopback, no advertising).</summary>
    internal Func<AirplayReceiverOptions, AirplayReceiverOptions>? ConfigureReceiver { get; set; }

    /// <summary>The running receiver, or null (tests, diagnostics).</summary>
    internal AirplayReceiver? Receiver => Volatile.Read(ref _receiver);

    /// <summary>The receiver's state (SPEC-receiver.md 9.1 <c>AirplayReceiverDto.State</c>); <see cref="ReceiverStatus.Off"/> while not hosted, <see cref="ReceiverStatus.Error"/> after a failed start.</summary>
    public ReceiverStatus Status => Receiver?.Status ?? (_failed ? ReceiverStatus.Error : ReceiverStatus.Off);

    /// <summary>The current sender, or null (SPEC-receiver.md 8.3).</summary>
    public SenderInfo? Sender => Receiver?.Sender;

    /// <summary>The output owner (SPEC-receiver.md 8.2), as the API reads it.</summary>
    public AudioSource Owner => _host.Owner;

    /// <summary>The advertised name: the running receiver's, else the loaded setting, else null (SPEC-receiver.md 3.3, 8.4).</summary>
    public string? Name => Receiver?.Name ?? _settings?.Name;

    /// <summary>True when an AAC decoder loaded (SPEC-receiver.md R4, 9.1 <c>AacAvailable</c>).</summary>
    public bool AacAvailable => Receiver?.AacAvailable ?? false;

    /// <summary>The bound RTSP port while running, else the configured one (SPEC-receiver.md 8.5, 9.1).</summary>
    public int RtspPort => Receiver?.RtspPort is > 0 and var port ? port : _options.Value.Airplay.RtspPort;

    /// <summary>The shared PTP port is bound (SPEC-receiver.md 6.1; false: anchor-only timing, R5).</summary>
    public bool PtpBound => _ptpPort.IsBound;

    /// <summary>The playout statistics for Diagnostics, or null without a stream (SPEC-receiver.md 5.3, 9.1).</summary>
    public PlayoutStatistics? Statistics => Receiver?.Statistics ?? (_streamType is null ? null : _lastPlayout);

    /// <summary>The PTP slave's statistics, or null without a PTP session (SPEC-receiver.md 6.2, 9.1).</summary>
    public PtpSlaveStatistics? PtpStatistics => Receiver?.PtpStatistics ?? (_streamType is null ? null : _lastPtp);

    /// <summary>The decoder in use for Diagnostics (SPEC-receiver.md 7.2, 9.1 <c>DecoderStatus</c>), or null while the receiver is off.</summary>
    public string? DecoderStatus => Receiver is { } receiver ? (receiver.AacAvailable ? DecoderAac : DecoderAlacOnly) : null;

    /// <summary>The stream type of the current stream, or null (SPEC-receiver.md 9.1 <c>StreamType</c>).</summary>
    public StreamType? StreamType => _streamType;

    /// <summary>The format of the current stream (<c>ALAC/44100/16/2</c>, <c>AAC-LC/44100/2</c>), or null (SPEC-receiver.md R3, 9.1).</summary>
    public string? Format => _format;

    /// <summary>The sender's last artwork with its <c>rtptime</c> key, or null (SPEC-receiver.md 9.1 <c>GET /api/playback/artwork</c>).</summary>
    public AirplayArtwork? Artwork
    {
        get
        {
            lock (_viewLock)
            {
                return _artwork;
            }
        }
    }

    /// <summary>The AirPlay playback view now (SPEC-receiver.md 8.2, 9.1: source <c>airplay</c>, position advanced while playing).</summary>
    public PlaybackDto Playback
    {
        get
        {
            lock (_viewLock)
            {
                return BuildPlaybackLocked();
            }
        }
    }

    /// <summary>The shared PTP port (SPEC-receiver.md 6.1).</summary>
    public IPtpPort PtpPort => _ptpPort;

    /// <summary>Frames dropped because AirPlay did not own the output or no player was started (SPEC-receiver.md 8.2).</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <inheritdoc />
    public int QueuedFrames => _host.Owner == AudioSource.Airplay && _startedPlayer is { } player && ReferenceEquals(player, _host.Player) ? player.QueuedFrames : 0;

    /// <summary>
    /// Builds the receiver options (SPEC-receiver.md 8.3, 8.5): name, identity and password from the settings; RTSP port, ffmpeg
    /// path, anchor lead and session timeout from <c>Bridge:Airplay</c>; <c>Bridge:LocalAddress</c> as the bind and advertised
    /// address; the shared PTP port, the sender's clock, the controller store and <c>Lpipe</c> for the latency (5.4).
    /// </summary>
    public static AirplayReceiverOptions BuildReceiverOptions(
        AirplaySettings settings,
        BridgeOptions bridge,
        IPtpPort? ptpPort,
        IMasterClock? clock,
        IControllerStore? controllers,
        int latencyFrames,
        Func<CancellationToken, ValueTask<int?>>? outputLevel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bridge);
        var airplay = bridge.Airplay;
        var local = bridge.LocalIPAddress;
        return new AirplayReceiverOptions
        {
            Name = settings.Name,
            Identity = settings.Identity,
            Password = string.IsNullOrEmpty(settings.Password) ? null : settings.Password,
            BindAddress = local,
            AdvertisedAddresses = local is null ? null : [local],
            RtspPort = airplay.RtspPort,
            FfmpegPath = string.IsNullOrWhiteSpace(airplay.FfmpegPath) ? null : airplay.FfmpegPath,
            PtpPort = ptpPort,
            Clock = clock,
            ControllerStore = controllers,
            OutputLevel = outputLevel,
            Playout = new PlayoutOptions
            {
                Lpipe = PlayoutOptions.LpipeFor(latencyFrames, PlayoutOptions.DefaultPrefillFrames),
                PrefillFrames = PlayoutOptions.DefaultPrefillFrames,
                AnchorLead = TimeSpan.FromMilliseconds(Math.Max(0, airplay.AnchorLeadMs)),
            },
            SessionTimeout = TimeSpan.FromSeconds(Math.Max(1, airplay.SessionTimeoutSeconds)),
        };
    }

    // ---- hosting (SPEC-receiver.md 3.3, 8.3) ---------------------------------------------------------------------

    /// <summary>
    /// Loads the settings (creating the identity once, R11) and starts the receiver when the stored <c>airplay_enabled</c>
    /// (else <c>Bridge:Airplay:Enabled</c>) is true (SPEC-receiver.md 8.3, 8.4). Runs in the background: binding and
    /// advertising never delay the host, and a failure is logged and shown as <see cref="ReceiverStatus.Error"/>.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startup = Task.Run(() => StartupAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops the receiver like <c>enabled → false</c> (SPEC-receiver.md 3.3): the session ended, the output released.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_stopping.IsCancellationRequested)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        await _startup.ConfigureAwait(false);
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopReceiverLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Ends the sender's session as the switch asks (SPEC-receiver.md R8, 8.2: "replaced by Spotify"); completes after the
    /// output was released (5.5: paused, then unowned). Without a receiver or a session it only releases what AirPlay still owns.
    /// </summary>
    public async Task EndSessionAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (Receiver is { } receiver)
        {
            await receiver.EndSessionAsync(reason, cancellationToken).ConfigureAwait(false);
        }

        if (_host.Owner == AudioSource.Airplay || _startedPlayer is not null)
        {
            await ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replays what was dropped before the claim completed, once the switch granted ownership (SPEC-receiver.md 8.2): the
    /// sender's level, the latest now-playing (its elapsed time advanced while playing) and a resume. A no-op unless AirPlay owns the output.
    /// </summary>
    public async Task ResumeIfPlayingAsync(CancellationToken cancellationToken)
    {
        if (_host.Owner != AudioSource.Airplay)
        {
            return;
        }

        int? level;
        PendingNowPlaying? nowPlaying;
        lock (_viewLock)
        {
            (level, _pendingLevel) = (_pendingLevel, null);
            (nowPlaying, _pendingNowPlaying) = (_pendingNowPlaying, null);
        }

        if (level is { } l)
        {
            await _host.Output.SetSourceLevelAsync(l, OutputManager.SourceAirplay, cancellationToken).ConfigureAwait(false);
        }

        if (nowPlaying is { } pending)
        {
            var elapsed = pending.NowPlaying.Elapsed + (_wantPlaying ? _time.GetElapsedTime(pending.At) : TimeSpan.Zero);
            await _host.Output.SetNowPlayingAsync(pending.NowPlaying with { Elapsed = elapsed }, cancellationToken).ConfigureAwait(false);
        }

        if (_wantPlaying && _host.Player is { } player && !ReferenceEquals(player, _startedPlayer))
        {
            _startedPlayer = player;
            await player.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        PublishView();
    }

    /// <summary>
    /// Persists and applies a settings change (SPEC-receiver.md 3.3, 8.3, 8.4): name and password without a restart or a
    /// session end, <c>enabled</c> starts or stops the receiver. The stored identity is kept whatever <paramref name="settings"/> carries (R11).
    /// </summary>
    public async Task ApplySettingsAsync(AirplaySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Name);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _settings ?? await _host.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            var next = settings with { Identity = current.Identity, Password = string.IsNullOrEmpty(settings.Password) ? null : settings.Password };
            await _host.SaveSettingsAsync(next, cancellationToken).ConfigureAwait(false);

            _settings = next;
            if (!next.Enabled)
            {
                await StopReceiverLockedAsync().ConfigureAwait(false);
            }
            else if (Receiver is not { } receiver)
            {
                await StartReceiverLockedAsync(next, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.Equals(receiver.Name, next.Name, StringComparison.Ordinal) || !string.Equals(current.Password, next.Password, StringComparison.Ordinal))
            {
                await receiver.UpdateSettingsAsync(next.Name, next.Password, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        await PublishStatusAsync().ConfigureAwait(false);
    }

    /// <summary>Stops the receiver when it is running.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _stopping.Dispose();
    }

    private async Task StartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var settings = await _host.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            _settings = settings;
            if (!settings.Enabled)
            {
                _logger.LogInformation("AirPlay receiver is off (airplay_enabled / Bridge:Airplay:Enabled)");
                return;
            }

            await StartReceiverLockedAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _failed = true;
            _logger.LogError(ex, "AirPlay receiver could not be set up");
        }
        finally
        {
            _lifecycle.Release();
        }

        await PublishStatusAsync().ConfigureAwait(false);
    }

    private async Task StartReceiverLockedAsync(AirplaySettings settings, CancellationToken cancellationToken)
    {
        var clock = await _host.ClockAsync(cancellationToken).ConfigureAwait(false);
        var latency = await _host.LatencyFramesAsync(cancellationToken).ConfigureAwait(false);
        var output = _host.Output;
        var options = BuildReceiverOptions(settings, _options.Value, _ptpPort, clock, _host.Controllers, latency, async ct => await output.CurrentLevelAsync(ct).ConfigureAwait(false));
        if (ConfigureReceiver is { } configure)
        {
            options = configure(options);
        }

        var receiver = new AirplayReceiver(options, this, _loggerFactory);
        try
        {
            await receiver.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _failed = true;
            _logger.LogError("AirPlay receiver {Name} did not start on port {Port}: {Error}", settings.Name, options.RtspPort, ex.Message);
            await receiver.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _failed = false;
        Volatile.Write(ref _receiver, receiver);
        _pump = PumpAsync(receiver);
    }

    private async Task StopReceiverLockedAsync()
    {
        var receiver = Interlocked.Exchange(ref _receiver, null);
        _failed = false;
        if (receiver is null)
        {
            return;
        }

        try
        {
            await receiver.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping the AirPlay receiver failed");
        }

        await receiver.DisposeAsync().ConfigureAwait(false); // completes Events: the pump drains and ends
        await _pump.ConfigureAwait(false);
        _pump = Task.CompletedTask;
        if (_host.Owner == AudioSource.Airplay || _startedPlayer is not null)
        {
            await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        ClearSession();
        await PublishStatusAsync().ConfigureAwait(false);
    }

    private async Task PumpAsync(AirplayReceiver receiver)
    {
        await Task.Yield();
        await foreach (var receiverEvent in receiver.Events.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await HandleEventAsync(receiverEvent, receiver, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Handling the AirPlay receiver event {Event} failed", receiverEvent.GetType().Name);
            }
        }
    }

    // ---- receiver events (SPEC-receiver.md 8.2, 8.3) ---------------------------------------------------------------

    /// <summary>Maps one receiver event to claims, releases, the playback view and the status (SPEC-receiver.md 8.2, 8.3).</summary>
    internal async Task HandleEventAsync(ReceiverEvent receiverEvent, AirplayReceiver? receiver, CancellationToken cancellationToken)
    {
        switch (receiverEvent)
        {
            case ReceiverEvent.SenderConnected connected:
                _logger.LogInformation("AirPlay sender connected: {Sender} ({Model})", connected.Sender.Name ?? "?", connected.Sender.Model ?? "?");
                await PublishStatusAsync().ConfigureAwait(false);
                break;

            case ReceiverEvent.SenderStreamStarted started:
                _logger.LogInformation("AirPlay stream from {Sender}: {Type} {Format}", started.Sender.Name ?? "?", started.StreamType, started.Format);
                (_streamType, _format) = (started.StreamType, started.Format);
                try
                {
                    await _host.ClaimAsync(started.Sender, cancellationToken).ConfigureAwait(false); // R8: newest wins
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Claiming the output for AirPlay failed");
                }

                await ResumeIfPlayingAsync(cancellationToken).ConfigureAwait(false);
                await PublishStatusAsync().ConfigureAwait(false);
                break;

            case ReceiverEvent.SenderArtwork artwork:
                lock (_viewLock)
                {
                    _artwork = new AirplayArtwork(artwork.Bytes, artwork.ContentType, artwork.RtpTime);
                }

                PublishView();
                break;

            case ReceiverEvent.SenderPaused:
                lock (_viewLock)
                {
                    _position = PositionLocked();
                    _positionAt = _time.GetTimestamp();
                    _playing = false;
                }

                PublishView();
                break;

            case ReceiverEvent.SenderResumed:
                lock (_viewLock)
                {
                    _position = PositionLocked();
                    _positionAt = _time.GetTimestamp();
                    _playing = true;
                }

                PublishView();
                break;

            case ReceiverEvent.Statistics statistics:
                (_lastPlayout, _lastPtp) = (statistics.Playout, statistics.Ptp);
                break;

            case ReceiverEvent.SenderGone gone:
                _logger.LogInformation("AirPlay session ended: {Reason}", gone.Reason);
                if (receiver?.Status is not ReceiverStatus.Streaming)
                {
                    _host.Release(); // R8: the output is unowned once the sender left
                    ClearSession();
                }

                await PublishStatusAsync().ConfigureAwait(false);
                break;
        }
    }

    // ---- IReceiverAudioOutput (SPEC-receiver.md 5.5, 8.3) ----------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-receiver.md 8.2: written only while <c>MayWrite(Airplay)</c> and only to a player this source started; when the
    /// output changed while playing, the new player is started first (the player reference is read per write, 8.3).
    /// </remarks>
    public ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken)
    {
        if (!_host.MayWrite || _host.Player is not { } player)
        {
            Interlocked.Add(ref _droppedFrames, frames.Length / 2);
            return ValueTask.CompletedTask;
        }

        if (!ReferenceEquals(player, _startedPlayer))
        {
            if (!_wantPlaying)
            {
                Interlocked.Add(ref _droppedFrames, frames.Length / 2);
                return ValueTask.CompletedTask;
            }

            return StartThenWriteAsync(player, frames, cancellationToken);
        }

        player.Write(frames.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        _wantPlaying = false;
        var started = Interlocked.Exchange(ref _startedPlayer, null);
        if (_host.Admit("pause") && (started ?? _host.Player) is { } player)
        {
            await player.PauseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-receiver.md 8.2: through the switch's start gate; a dropped start (or one without a player) is remembered and
    /// replayed by <see cref="ResumeIfPlayingAsync"/> once AirPlay owns the output, or by the next write to a new player.
    /// </remarks>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        _wantPlaying = true;
        if (_host.AdmitStart() && _host.Player is { } player)
        {
            _startedPlayer = player;
            await player.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken) =>
        _host.Admit("flush") ? _host.Output.FlushAsync(cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>SPEC-receiver.md R9, 8.3: G of the output with the source <c>airplay</c>; a level dropped before the claim completed is remembered.</remarks>
    public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken)
    {
        level = Math.Clamp(level, 0, 100);
        var admitted = _host.Admit("level");
        lock (_viewLock)
        {
            _pendingLevel = admitted ? null : level;
        }

        return admitted ? _host.Output.SetSourceLevelAsync(level, OutputManager.SourceAirplay, cancellationToken) : Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-receiver.md 5.5, 8.3: the coalesced call with the buffer-derived <paramref name="elapsed"/> goes to
    /// <c>OutputManager.SetNowPlayingAsync</c> and becomes the playback view (published while AirPlay owns the output); one
    /// dropped before the claim completed is remembered.
    /// </remarks>
    public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var nowPlaying = new NowPlaying(metadata, artwork, artworkContentType, elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
        var admitted = _host.Admit("now playing");
        lock (_viewLock)
        {
            _metadata = metadata;
            _position = nowPlaying.Elapsed;
            _positionAt = _time.GetTimestamp();
            _pendingNowPlaying = admitted ? null : new PendingNowPlaying(nowPlaying, _positionAt);
        }

        PublishView();
        return admitted ? _host.Output.SetNowPlayingAsync(nowPlaying, cancellationToken) : Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>SPEC-receiver.md 5.5: the player is paused first (left running the pacer would send 5 s of silence), then the output is unowned.</remarks>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        _wantPlaying = false;
        var started = Interlocked.Exchange(ref _startedPlayer, null);
        lock (_viewLock)
        {
            _pendingLevel = null;
            _pendingNowPlaying = null;
        }

        var owner = _host.Owner;
        var player = owner == AudioSource.Airplay ? started ?? _host.Player : owner == AudioSource.None ? started : null;
        if (player is not null)
        {
            try
            {
                await player.PauseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Pausing the output at the end of the AirPlay session failed");
            }
        }

        _host.Release();
        ClearSession();
    }

    // ---- view ------------------------------------------------------------------------------------------------------

    private async ValueTask StartThenWriteAsync(IGroupPlayer player, ReadOnlyMemory<short> frames, CancellationToken cancellationToken)
    {
        _startedPlayer = player;
        await player.StartAsync(cancellationToken).ConfigureAwait(false);
        player.Write(frames.Span);
    }

    private void ClearSession()
    {
        PlaybackDto empty;
        lock (_viewLock)
        {
            _metadata = null;
            _artwork = null;
            _artworkSentKey = null;
            _playing = false;
            _position = TimeSpan.Zero;
            _positionAt = _time.GetTimestamp();
            _host.ClearArtwork();
            empty = BuildPlaybackLocked();
            _lastPublished = null;
        }

        (_streamType, _format, _lastPlayout, _lastPtp) = (null, null, null, null);
        _host.PublishPlayback(empty);
    }

    /// <summary>Stores the view and publishes it while AirPlay owns the output (SPEC-receiver.md 8.2); an unchanged view is not published again.</summary>
    private void PublishView()
    {
        PlaybackDto playback;
        lock (_viewLock)
        {
            playback = BuildPlaybackLocked();
            if (_lastPublished is { } last && last == playback)
            {
                return;
            }

            _lastPublished = playback;
        }

        _host.PublishPlayback(playback);
    }

    private TimeSpan PositionLocked()
    {
        var position = _position + (_playing ? _time.GetElapsedTime(_positionAt) : TimeSpan.Zero);
        return _metadata?.Duration is { } duration && duration > TimeSpan.Zero && position > duration ? duration : position;
    }

    /// <summary>The view through <c>DtoFactory.AirplayPlayback</c> (SPEC-receiver.md 8.3); the artwork bytes are handed over once per key.</summary>
    private PlaybackDto BuildPlaybackLocked()
    {
        ReadOnlyMemory<byte>? bytes = null;
        string? type = null;
        if (_artwork is { } art && _artworkSentKey != art.Key)
        {
            (bytes, type) = (art.Bytes, art.ContentType);
            _artworkSentKey = art.Key;
        }

        return _host.BuildPlayback(_metadata, _playing, PositionLocked(), bytes, type, _artwork?.Key, _host.SpotifyVolume);
    }

    private async Task PublishStatusAsync()
    {
        try
        {
            await _host.PublishStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Publishing the status failed: {Error}", ex.Message);
        }
    }

    private sealed record PendingNowPlaying(NowPlaying NowPlaying, long At);
}
