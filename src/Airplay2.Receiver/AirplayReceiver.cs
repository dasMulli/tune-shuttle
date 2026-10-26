using System.Net;
using System.Threading.Channels;
using Airplay2.Receiver.Codecs;
using Airplay2.Receiver.Discovery;
using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Session;
using Airplay2.Receiver.Streams;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver;

/// <summary>
/// The AirPlay 2 receiver (SPEC-receiver.md 0, R1-R16): advertises the two services (section 3), accepts one sender at
/// a time on the RTSP listener (section 4), decodes its stream into the <see cref="PlayoutBuffer"/> (section 5) and
/// drives the host's <see cref="IReceiverAudioOutput"/> (5.5). The host observes it through <see cref="Events"/> and
/// <see cref="Status"/>. Works without the bridge (G5); the bridge hosts it as <c>AirplaySource</c> (8.3).
/// </summary>
/// <remarks>
/// <para>
/// Wiring (SPEC-receiver.md 4.1-4.9, 5.1-5.5, 6.2): <see cref="StartAsync"/> probes the AAC decoder (R4: the ALAC-only
/// variant of R3 when it does not load, logged once), binds the <see cref="ReceiverControlPlane"/> (RTSP listener, one
/// <see cref="ControlSession"/> per connection, pairing against the <see cref="IControllerStore"/>) and registers the
/// services with a <see cref="ReceiverAdvertiser"/>. The receiver is the control plane's <see cref="IControlPlaneHost"/>:
/// it answers <c>/info</c> with <see cref="InfoResponseBuilder"/>, starts a <see cref="PtpSlave"/> on
/// <see cref="AirplayReceiverOptions.PtpPort"/> for every PTP session (anchor-only timing without a port, R5/R6), and
/// binds each stream through <see cref="ReceiverStreamFactory"/> (a <see cref="RealtimeStreamReceiver"/> or
/// <see cref="BufferedStreamReceiver"/> feeding a <see cref="PlayoutBuffer"/> on the process clock).
/// </para>
/// <para>
/// Status (SPEC-receiver.md 9.1): <see cref="ReceiverStatus.Off"/> → <see cref="ReceiverStatus.Starting"/> →
/// <see cref="ReceiverStatus.Advertising"/>; <see cref="ReceiverStatus.Connected"/> while a sender has a session without a
/// stream, <see cref="ReceiverStatus.Streaming"/> while a stream is set up; <see cref="ReceiverStatus.Error"/> when starting
/// failed; <see cref="ReceiverStatus.Off"/> again after <see cref="StopAsync"/>. The receiver can be started again after a stop
/// (3.3 <c>enabled → true</c>).
/// </para>
/// </remarks>
public sealed class AirplayReceiver : IAsyncDisposable
{
    private readonly Channel<ReceiverEvent> _events = Channel.CreateBounded<ReceiverEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _lock = new();
    private readonly IMasterClock _clock;
    private readonly IMonotonicClock _monotonic;
    private readonly IControllerStore _store;
    private readonly OutputGate _output;
    private readonly Host _host;
    private AirplayReceiverOptions _settings;
    private InfoResponseBuilder _info;
    private ReceiverControlPlane? _plane;
    private ReceiverAdvertiser? _advertiser;
    private ReceiverStreamFactory? _streams;
    private NowPlayingCoalescer? _nowPlaying;
    private CancellationTokenSource? _run;
    private Task _statisticsLoop = Task.CompletedTask;
    private TrackedStream? _active;
    private int? _lastSenderLevel;
    private int? _lastOutputLevel;
    private bool _running;
    private bool _aacReasonLogged;
    private bool _disposed;

    /// <summary>Creates the receiver; nothing is bound or advertised until <see cref="StartAsync"/>.</summary>
    /// <param name="options">Name, identity, password, ports, formats and timing.</param>
    /// <param name="output">Where released frames and pause/flush/volume/metadata go (SPEC-receiver.md 5.5).</param>
    /// <param name="loggerFactory">Logger factory; null logs nothing.</param>
    public AirplayReceiver(AirplayReceiverOptions options, IReceiverAudioOutput output, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options.Identity);
        Options = options;
        Output = output;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<AirplayReceiver>();
        _clock = options.Clock ?? new MasterClock();
        _monotonic = MonotonicClock.FromMasterClock(_clock);
        _store = options.ControllerStore ?? new InMemoryControllerStore();
        _settings = options with { Password = NormalizePassword(options.Password), ControllerStore = _store };
        _info = new InfoResponseBuilder(_settings);
        _output = new OutputGate(output, this);
        _host = new Host(this);
    }

    /// <summary>The options the receiver was created with (name and password as given; <see cref="Name"/> and <see cref="UpdateSettingsAsync"/> for the current ones).</summary>
    public AirplayReceiverOptions Options { get; }

    /// <summary>The host's output (SPEC-receiver.md 5.5).</summary>
    public IReceiverAudioOutput Output { get; }

    /// <summary>The events for the host, in order (SPEC-receiver.md 8.3); the oldest is dropped when the host falls behind.</summary>
    public ChannelReader<ReceiverEvent> Events => _events.Reader;

    /// <summary>The receiver's state (SPEC-receiver.md 9.1 <c>AirplayReceiverDto.State</c>).</summary>
    /// <remarks>Read lock-free from one immutable snapshot, so it always pairs with <see cref="Sender"/>.</remarks>
    public ReceiverStatus Status => Volatile.Read(ref _state).Status;

    /// <summary>The sender of the current session, or null (SPEC-receiver.md 4.3): the streaming sender, else the newest sender with a session.</summary>
    public SenderInfo? Sender => Volatile.Read(ref _state).Sender;

    // Status and sender change together (Recompute); one reference keeps the pair consistent for lock-free readers.
    private StateSnapshot _state = new(ReceiverStatus.Off, null);

    private sealed record StateSnapshot(ReceiverStatus Status, SenderInfo? Sender);

    private void SetState(ReceiverStatus status, SenderInfo? sender) => Volatile.Write(ref _state, new StateSnapshot(status, sender));

    private void SetStatus(ReceiverStatus status)
    {
        // Lifecycle transitions keep the sender; the stop path clears it explicitly.
        var current = Volatile.Read(ref _state);
        SetState(status, current.Sender);
    }

    /// <summary>The bound RTSP port after <see cref="StartAsync"/> (the OS-chosen one when <see cref="AirplayReceiverOptions.RtspPort"/> is 0), else 0.</summary>
    public int RtspPort { get; private set; }

    /// <summary>True when an AAC decoder loaded (SPEC-receiver.md R4); false means the ALAC-only variant of R3 is advertised.</summary>
    public bool AacAvailable { get; private set; }

    /// <summary>The advertised name now (SPEC-receiver.md 3.3: changed by <see cref="UpdateSettingsAsync"/>).</summary>
    public string Name => Volatile.Read(ref _settings).Name;

    /// <summary>The feature word in effect (SPEC-receiver.md R15, or the ALAC-only variant of R3 without an AAC decoder).</summary>
    public ulong Features => ReceiverFeatures.For(AacAvailable);

    /// <summary>The advertisement currently registered, or null while not advertising (SPEC-receiver.md 3.1).</summary>
    public ReceiverAdvertisement? Advertisement => _advertiser?.Current;

    /// <summary>The playout statistics of the current stream, or null without a stream (SPEC-receiver.md 5.3, 9.1).</summary>
    public PlayoutStatistics? Statistics => Volatile.Read(ref _active)?.Statistics;

    /// <summary>The PTP slave statistics of the current stream's session, or null without one or in anchor-only timing (SPEC-receiver.md 6.2, 9.1).</summary>
    public PtpSlaveStatistics? PtpStatistics => Volatile.Read(ref _active)?.Timing?.Slave?.Statistics;

    /// <summary>
    /// Binds the RTSP listener, loads the controllers, registers the two services and starts accepting senders
    /// (SPEC-receiver.md 3.1, 3.3 <c>enabled → true</c>, 4.1). <see cref="Status"/> becomes <see cref="ReceiverStatus.Advertising"/>;
    /// on a failure (for example the RTSP port is taken) it becomes <see cref="ReceiverStatus.Error"/>, everything bound is
    /// closed again and the exception is rethrown. A failure to register the services is logged and the listener keeps running.
    /// A no-op while running.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_plane is not null)
            {
                return;
            }

            SetStatus(ReceiverStatus.Starting);
            try
            {
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AirPlay receiver {Name} failed to start", Name);
                await StopCoreAsync("start failed", CancellationToken.None).ConfigureAwait(false);
                SetStatus(ReceiverStatus.Error);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Goodbye records, a running session ended, listeners closed (SPEC-receiver.md 3.3 <c>enabled → false</c>, 4.9), in that
    /// order; <see cref="Status"/> becomes <see cref="ReceiverStatus.Off"/>. A no-op while stopped.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_plane is null && _advertiser is null)
            {
                return;
            }

            await StopCoreAsync("receiver stopped", cancellationToken).ConfigureAwait(false);
            SetStatus(ReceiverStatus.Off);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Ends the current session as the host asks (SPEC-receiver.md R8: Spotify claimed the output): stream, event channel and
    /// PTP slave closed, the output released (5.5), <see cref="ReceiverEvent.SenderGone"/> raised with <paramref name="reason"/>,
    /// the connection closed. Completes after <see cref="IReceiverAudioOutput.ReleaseAsync"/>; a no-op without a session.
    /// </summary>
    public async Task EndSessionAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var plane = Volatile.Read(ref _plane);
        if (plane is null)
        {
            return;
        }

        await plane.EndSessionAsync(reason, cancellationToken).ConfigureAwait(false);
        Recompute();
    }

    /// <summary>
    /// Applies a name or password change without a restart (SPEC-receiver.md 3.3): the services are re-registered (goodbye,
    /// then announce) when the records change, <c>/info</c> answers the new name and new connections get the new pairing
    /// options (R7); a running session and its pairing are untouched. An empty password means none.
    /// </summary>
    public async Task UpdateSettingsAsync(string name, string? password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var old = _settings;
            var updated = old with { Name = name, Password = NormalizePassword(password) };
            Volatile.Write(ref _settings, updated);
            Volatile.Write(ref _info, new InfoResponseBuilder(updated, _info.Template));
            if (_plane is { } plane && !string.Equals(old.Password, updated.Password, StringComparison.Ordinal))
            {
                await plane.UpdatePasswordAsync(updated.Password, cancellationToken).ConfigureAwait(false);
            }

            if (_advertiser is { IsRunning: true } advertiser)
            {
                await advertiser.UpdateAsync(ReceiverTxtRecords.Advertisement(updated, RtspPort, Features), cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("AirPlay receiver settings: name {Name}, {Mode} pairing", name, updated.Password is null ? "transient" : "password");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops the receiver when it is running and completes <see cref="Events"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_plane is not null || _advertiser is not null)
            {
                await StopCoreAsync("receiver stopped", CancellationToken.None).ConfigureAwait(false);
            }

            SetStatus(ReceiverStatus.Off);
            _events.Writer.TryComplete();
            _logger.LogDebug("Receiver {Name} disposed", Name);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static string? NormalizePassword(string? password) => string.IsNullOrEmpty(password) ? null : password;

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        // R4: probe the decoder once per start; without it the ALAC-only variant of R3 is advertised in all three places.
        var factory = Options.AacDecoderFactory ?? new FfmpegAacDecoderFactory(Options.FfmpegPath, _loggerFactory.CreateLogger<FfmpegAacDecoderFactory>());
        if (factory.TryCreate(out var probe, out var reason))
        {
            probe.Dispose();
            AacAvailable = true;
        }
        else
        {
            AacAvailable = false;
            if (!_aacReasonLogged)
            {
                _aacReasonLogged = true;
                _logger.LogWarning("No AAC decoder ({Reason}): the receiver advertises the ALAC-only formats (SPEC-receiver.md R3, R4)", reason);
            }
        }

        _streams = new ReceiverStreamFactory(Output, _monotonic, Options.Playout, AacAvailable ? factory : null, _clock, _loggerFactory);
        _nowPlaying = new NowPlayingCoalescer(Output, () => Statistics?.NextTimestamp, Options.NowPlayingWindow, _logger);
        _lastSenderLevel = null;
        await RefreshOutputLevelAsync(cancellationToken).ConfigureAwait(false);

        var plane = new ReceiverControlPlane(_settings, _host, _loggerFactory);
        _plane = plane;
        await plane.StartAsync(cancellationToken).ConfigureAwait(false);
        RtspPort = plane.Port;
        lock (_lock)
        {
            _running = true;
        }

        if (Options.Advertise)
        {
            var advertiser = new ReceiverAdvertiser(Options.AdvertisedAddresses, _loggerFactory.CreateLogger<ReceiverAdvertiser>(), Options.Mdns, Options.MdnsHostname);
            try
            {
                await advertiser.StartAsync(ReceiverTxtRecords.Advertisement(_settings, RtspPort, Features), cancellationToken).ConfigureAwait(false);
                _advertiser = advertiser;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The listener still serves senders that know the address; the services are retried at the next start.
                _logger.LogError(ex, "AirPlay receiver {Name}: registering the DNS-SD services failed; not advertised", Name);
                await advertiser.DisposeAsync().ConfigureAwait(false);
            }
        }

        var run = new CancellationTokenSource();
        _run = run;
        _statisticsLoop = StatisticsLoopAsync(Options.StatisticsInterval, run.Token);
        Recompute();
        _logger.LogInformation(
            "AirPlay receiver {Name} started on port {Port} ({Formats}, {Timing} timing)",
            Name,
            RtspPort,
            AacAvailable ? "ALAC and AAC" : "ALAC only",
            Options.PtpPort is null ? "anchor-only" : "PTP");
    }

    /// <summary>SPEC-receiver.md 3.3 <c>enabled → false</c>: goodbye, end the session, close the listeners.</summary>
    private async Task StopCoreAsync(string reason, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _running = false;
        }

        if (_run is { } run)
        {
            await run.CancelAsync().ConfigureAwait(false);
            await _statisticsLoop.ConfigureAwait(false);
            run.Dispose();
            _run = null;
        }

        if (_advertiser is { } advertiser)
        {
            _advertiser = null;
            try
            {
                await advertiser.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "AirPlay receiver {Name}: the goodbye records failed", Name);
            }

            await advertiser.DisposeAsync().ConfigureAwait(false);
        }

        if (_plane is { } plane)
        {
            await plane.EndSessionAsync(reason, cancellationToken).ConfigureAwait(false);
            await plane.StopAsync(reason, cancellationToken).ConfigureAwait(false);
            await plane.DisposeAsync().ConfigureAwait(false);
            _plane = null;
        }

        if (_nowPlaying is { } nowPlaying)
        {
            _nowPlaying = null;
            await nowPlaying.DisposeAsync().ConfigureAwait(false);
        }

        Volatile.Write(ref _active, null);
        _streams = null;
        RtspPort = 0;
        SetState(Status, null);
        _logger.LogInformation("AirPlay receiver {Name} stopped: {Reason}", Name, reason);
    }

    private async Task StatisticsLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _active) is { } active && active.Statistics is { } playout)
                {
                    _events.Writer.TryWrite(new ReceiverEvent.Statistics(playout, active.Timing?.Slave?.Statistics));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async ValueTask<int?> RefreshOutputLevelAsync(CancellationToken cancellationToken)
    {
        if (Options.OutputLevel is not { } provider)
        {
            return null;
        }

        try
        {
            var level = await provider(cancellationToken).ConfigureAwait(false);
            _lastOutputLevel = level;
            return level;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the output level failed");
            return _lastOutputLevel;
        }
    }

    /// <summary>The status and sender from the control plane's sessions (SPEC-receiver.md 4.3, 4.6, 9.1).</summary>
    private void Recompute()
    {
        lock (_lock)
        {
            if (!_running || _plane is not { } plane)
            {
                return;
            }

            var sessions = plane.Sessions;
            if (sessions.FirstOrDefault(s => s.HasStream && !s.IsEnded)?.Sender is { } streaming)
            {
                SetState(ReceiverStatus.Streaming, streaming);
            }
            else if (sessions.LastOrDefault(s => s.IsSessionConnection)?.Sender is { } connected)
            {
                SetState(ReceiverStatus.Connected, connected);
            }
            else
            {
                SetState(ReceiverStatus.Advertising, null);
            }
        }
    }

    private void OnEvent(ReceiverEvent receiverEvent)
    {
        switch (receiverEvent)
        {
            case ReceiverEvent.SenderVolume volume:
                _lastSenderLevel = volume.Level;
                break;
            case ReceiverEvent.SenderMetadata metadata:
                _nowPlaying?.OnMetadata(metadata.Item, metadata.RtpTime);
                break;
            case ReceiverEvent.SenderArtwork artwork:
                _nowPlaying?.OnArtwork(artwork.Bytes, artwork.ContentType, artwork.RtpTime);
                break;
            case ReceiverEvent.SenderProgress progress:
                _nowPlaying?.OnProgress(progress.Progress);
                break;
            case ReceiverEvent.SenderGone:
                if (_plane?.Sessions.Any(s => s.IsSessionConnection) != true)
                {
                    _lastSenderLevel = null; // R9: the sender's level counts for its session only
                }

                break;
        }

        Recompute();
        _events.Writer.TryWrite(receiverEvent);
    }

    private void OnReleased()
    {
        _nowPlaying?.Reset();
        Recompute();
    }

    /// <summary>The receiver as the control plane's host (SPEC-receiver.md 4.1-4.9).</summary>
    private sealed class Host(AirplayReceiver receiver) : IControlPlaneHost
    {
        public bool AacAvailable => receiver.AacAvailable;

        public IReceiverAudioOutput Output => receiver._output;

        /// <remarks><c>txtAirPlay</c> is always included: <see cref="InfoResponseBuilder"/> answers every <c>/info</c> with it (3.2).</remarks>
        public PlistDictionary BuildInfo(IPEndPoint requester, bool includeTxt, bool streamActive)
        {
            var settings = Volatile.Read(ref receiver._settings);
            var flags = ReceiverFeatures.StatusFlagsFor(ReceiverTxtRecords.HasPassword(settings), streamActive);
            var db = ReceiverParameters.AnsweredDb(receiver._lastOutputLevel, receiver._lastSenderLevel);
            return Volatile.Read(ref receiver._info).Build(receiver.Features, flags, db, requester);
        }

        public ValueTask<int?> GetOutputLevelAsync(CancellationToken cancellationToken) => receiver.RefreshOutputLevelAsync(cancellationToken);

        public void Publish(ReceiverEvent receiverEvent) => receiver.OnEvent(receiverEvent);

        public async ValueTask<ISenderTiming?> StartTimingAsync(SenderTimingPeer peer, CancellationToken cancellationToken)
        {
            if (receiver.Options.PtpPort is not { } port)
            {
                return null; // R5/R6: anchor-only timing without the shared port
            }

            var addresses = peer.Addresses.Append(peer.ControlAddress).Distinct().ToList();
            var configured = receiver.Options.PtpSlave;
            var slave = new PtpSlave(
                configured with
                {
                    Port = port,
                    SenderAddresses = [.. configured.SenderAddresses, .. addresses],
                    ClockId = configured.ClockId ?? PtpSlave.ClockIdFromDeviceId(receiver.Options.Identity.DeviceId),
                    LocalClock = configured.LocalClock ?? receiver._monotonic,
                },
                receiver._loggerFactory.CreateLogger<PtpSlave>());
            try
            {
                await slave.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PtpPortException ex)
            {
                receiver._logger.LogWarning("The PTP port could not be bound ({Error}): anchor-only timing for this session (SPEC-receiver.md R5, R6)", ex.Message);
                await slave.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch
            {
                await slave.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new SenderTiming(slave);
        }

        public async ValueTask<IReceiverStream> CreateStreamAsync(ReceiverStreamRequest request, ISenderTiming? timing, CancellationToken cancellationToken)
        {
            var factory = receiver._streams ?? throw new InvalidOperationException("The receiver is not running.");
            var inner = await factory.CreateAsync(request, timing, cancellationToken).ConfigureAwait(false);
            var tracked = new TrackedStream(inner, timing, receiver);
            Volatile.Write(ref receiver._active, tracked);
            receiver._nowPlaying?.Reset();
            return tracked;
        }
    }

    /// <summary>The PTP side of a session (SPEC-receiver.md 4.3, 4.5): the slave and its SETPEERS additions.</summary>
    private sealed class SenderTiming(PtpSlave slave) : ISenderTiming
    {
        public IPtpSlave? Slave => slave;

        public void AddPeers(IReadOnlyList<IPAddress> addresses)
        {
            foreach (var address in addresses)
            {
                slave.AddSenderAddress(address);
            }
        }

        public ValueTask DisposeAsync() => slave.DisposeAsync();
    }

    /// <summary>A stream the receiver tracks for <see cref="Statistics"/> and the now-playing <c>elapsed</c> (SPEC-receiver.md 5.3, 5.5).</summary>
    private sealed class TrackedStream(IReceiverStream inner, ISenderTiming? timing, AirplayReceiver receiver) : IReceiverStream
    {
        public ISenderTiming? Timing { get; } = timing;

        public StreamType Type => inner.Type;

        public int DataPort => inner.DataPort;

        public int ControlPort => inner.ControlPort;

        public long? AudioBufferSize => inner.AudioBufferSize;

        public PlayoutStatistics? Statistics => inner.Statistics;

        public void SetAnchor(uint rtpTime, ulong networkTimeNanos, ulong timelineId) => inner.SetAnchor(rtpTime, networkTimeNanos, timelineId);

        public void Pause() => inner.Pause();

        public void Resume() => inner.Resume();

        public void Flush(ushort? nextSequence) => inner.Flush(nextSequence);

        public void FlushBuffered(uint untilSequence) => inner.FlushBuffered(untilSequence);

        public void Splice(uint fromSequence, uint untilSequence) => inner.Splice(fromSequence, untilSequence);

        public async ValueTask DisposeAsync()
        {
            Interlocked.CompareExchange(ref receiver._active, null, this);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The output as the control plane sees it: forwards everything, and a release also forgets the now-playing track (SPEC-receiver.md 4.9, 5.5).</summary>
    private sealed class OutputGate(IReceiverAudioOutput inner, AirplayReceiver receiver) : IReceiverAudioOutput
    {
        public int QueuedFrames => inner.QueuedFrames;

        public ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken) => inner.WriteAsync(frames, cancellationToken);

        public Task PauseAsync(CancellationToken cancellationToken) => inner.PauseAsync(cancellationToken);

        public Task ResumeAsync(CancellationToken cancellationToken) => inner.ResumeAsync(cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken) => inner.SetMasterLevelAsync(level, cancellationToken);

        public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken) =>
            inner.SetNowPlayingAsync(metadata, artwork, artworkContentType, elapsed, cancellationToken);

        public async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            receiver.OnReleased();
            await inner.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
