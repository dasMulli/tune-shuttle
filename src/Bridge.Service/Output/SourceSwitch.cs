using Airplay2.Receiver;
using Bridge.Contracts;
using Bridge.Service.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bridge.Service.Output;

/// <summary>Who owns the output (SPEC-receiver.md 8.2 <c>Owner</c>; 9.1 <c>StatusDto.ActiveSource</c>, <c>PlaybackDto.Source</c>).</summary>
public enum AudioSource
{
    /// <summary>Unowned: the default at start, and after a sender left (R8).</summary>
    None,

    /// <summary>Spotify Connect through librespot and the <see cref="AirplaySink"/>.</summary>
    Spotify,

    /// <summary>The AirPlay receiver (<c>AirplaySource</c>).</summary>
    Airplay,
}

/// <summary>An ownership change of the output (SPEC-receiver.md 8.2), as <see cref="SourceSwitch.OwnerChanged"/> reports it.</summary>
/// <param name="Previous">The owner before.</param>
/// <param name="Current">The owner now.</param>
/// <param name="Sender">The sender while <paramref name="Current"/> is <see cref="AudioSource.Airplay"/>, else null.</param>
public sealed record OwnerChange(AudioSource Previous, AudioSource Current, SenderInfo? Sender);

/// <summary>
/// What the <see cref="SourceSwitch"/> calls on the sources (SPEC-receiver.md 8.2). Delegates instead of the source types,
/// so the switch does not depend on <c>SpotifyHost</c>, <see cref="AirplaySink"/>, <c>AirplaySource</c> or
/// <see cref="DtoFactory"/> (all of which depend on it); the host wires them in <c>Program.cs</c>, tests stub them. A hook
/// left null is skipped.
/// </summary>
public sealed record SourceSwitchHooks
{
    /// <summary>
    /// Evicts Spotify (8.2): <c>SpotifyHost.TryCommand("pause")</c> and the <c>PauseAsync</c> the resulting
    /// <c>AirplaySink.Stop()</c> queues (<see cref="AirplaySink.EvictAsync"/>); the switch bounds it by
    /// <see cref="SourceSwitch.SpotifyEvictionTimeout"/>.
    /// </summary>
    public Func<CancellationToken, Task>? EvictSpotifyAsync { get; init; }

    /// <summary>Evicts AirPlay (8.2, R8): <c>AirplaySource.EndSessionAsync(reason)</c>, which completes after the receiver's <c>ReleaseAsync</c>.</summary>
    public Func<string, CancellationToken, Task>? EvictAirplayAsync { get; init; }

    /// <summary>Replays Spotify's dropped <c>Start</c> once it owns the output (8.2): <see cref="AirplaySink.StartIfRunning"/>.</summary>
    public Func<CancellationToken, Task>? StartSpotifyIfRunningAsync { get; init; }

    /// <summary>Replays AirPlay's dropped resume once it owns the output (8.2): <c>AirplaySource.ResumeIfPlayingAsync</c>.</summary>
    public Func<CancellationToken, Task>? ResumeAirplayIfPlayingAsync { get; init; }

    /// <summary>Builds the status for <c>statusChanged</c> (<see cref="DtoFactory.StatusAsync"/>); the switch sets <c>ActiveSource</c> on it (8.2, 9.1).</summary>
    public Func<CancellationToken, Task<StatusDto>>? StatusAsync { get; init; }
}

/// <summary>
/// The "newest source wins" switch in front of the output (SPEC-receiver.md R8, 8.2): every call a source makes on the one
/// <c>IGroupPlayer</c> is gated by ownership. <see cref="MayWrite"/> is the per-write gate both sinks check; the player
/// commands go through <see cref="OutputManager"/> (and the <see cref="AirplaySink"/>'s command queue), which drop them for
/// a non-owner (<see cref="Admit"/>) and remember a dropped <c>Start</c> (<see cref="AdmitStart"/>).
/// <see cref="ClaimAsync"/> runs the eviction to completion (Spotify paused through Spirc and the sink's pause awaited,
/// or <c>AirplaySource.EndSessionAsync</c> awaited) before it grants and publishes <c>statusChanged</c>; the playback view
/// belongs to the owner (<see cref="PublishPlayback"/>).
/// </summary>
/// <remarks>
/// <para>
/// Commands while nobody owns the output (<see cref="AudioSource.None"/>) pass for every source: there is nothing to
/// protect, and Spotify keeps working exactly as before the receiver existed (SPEC-receiver.md G2). Spotify's first
/// write while unowned claims the output (8.2); the sink's <c>Start</c> counts as that first write, since librespot calls
/// it right before writing and the player must not be started for a source that does not own it.
/// </para>
/// <para>
/// Claims are serialised; the owner and the playback view change together under one lock, so a source's playback
/// publication (<see cref="PublishPlayback"/>) never interleaves with the republication of an ownership change.
/// <c>statusChanged</c> is built asynchronously after each change, in order.
/// </para>
/// </remarks>
public sealed class SourceSwitch
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _claims = new(1, 1);
    private readonly RuntimeState _runtime;
    private readonly EventHub? _hub;
    private readonly ILogger _logger;
    private readonly SourceSwitchHooks _hooks;
    private volatile AudioSource _owner = AudioSource.None;
    private volatile SenderInfo? _sender;
    private bool _spotifyStartDropped;
    private bool _airplayStartDropped;
    private long _droppedCommands;
    private Task _statusPublication = Task.CompletedTask;

    /// <summary>A switch without hooks or event hub (SPEC-receiver.md 8.2; tests and the wave 0 scaffold).</summary>
    public SourceSwitch()
        : this(new RuntimeState(), null, NullLogger<SourceSwitch>.Instance)
    {
    }

    /// <summary>Creates the switch (SPEC-receiver.md 8.2); <paramref name="hooks"/> are the evictions and replays, see <see cref="SourceSwitchHooks"/>.</summary>
    public SourceSwitch(RuntimeState runtime, EventHub? hub, ILogger<SourceSwitch> logger, SourceSwitchHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);
        _runtime = runtime;
        _hub = hub;
        _logger = logger;
        _hooks = hooks ?? new SourceSwitchHooks();
    }

    /// <summary>Raised after every ownership change (SPEC-receiver.md 8.2, 8.6: the output manager re-syncs the Spotify slider), outside the switch's lock.</summary>
    public event EventHandler<OwnerChange>? OwnerChanged;

    /// <summary>The current owner (SPEC-receiver.md 8.2); <see cref="AudioSource.None"/> at start.</summary>
    public AudioSource Owner => _owner;

    /// <summary>The sender while <see cref="Owner"/> is <see cref="AudioSource.Airplay"/>, else null.</summary>
    public SenderInfo? Sender => _sender;

    /// <summary>How long a claim waits for Spotify's pause (SPEC-receiver.md 8.2: "at most 2 s") before it grants anyway.</summary>
    public TimeSpan SpotifyEvictionTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a claim waits for the AirPlay session to end (SPEC-receiver.md 8.2) before it grants anyway.</summary>
    public TimeSpan AirplayEvictionTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Player commands dropped because their source did not own the output (SPEC-receiver.md 8.2).</summary>
    public long DroppedCommands => Interlocked.Read(ref _droppedCommands);

    /// <summary>The <c>statusChanged</c> publications queued so far (tests await it).</summary>
    internal Task StatusPublication
    {
        get
        {
            lock (_lock)
            {
                return _statusPublication;
            }
        }
    }

    /// <summary>Every event the switch publishes, also when it has no hub (tests observe it).</summary>
    internal event Action<BridgeEvent>? Published;

    /// <summary>The API name of a source (SPEC-receiver.md 9.1): <c>spotify</c>, <c>airplay</c>, or null for <see cref="AudioSource.None"/>.</summary>
    public static string? ToApiName(AudioSource source) => source switch
    {
        AudioSource.Spotify => OutputManager.SourceSpotify,
        AudioSource.Airplay => OutputManager.SourceAirplay,
        _ => null,
    };

    /// <summary>The source a level tag names (SPEC-receiver.md 8.6), or null for the tags of the web app and the receivers.</summary>
    public static AudioSource? FromLevelSource(string? source) => source switch
    {
        OutputManager.SourceSpotify => AudioSource.Spotify,
        OutputManager.SourceAirplay => AudioSource.Airplay,
        _ => null,
    };

    /// <summary>The per-write gate (SPEC-receiver.md 8.2): true while <paramref name="source"/> owns the output; frames of a non-owner are counted and dropped by the caller.</summary>
    public bool MayWrite(AudioSource source) => source != AudioSource.None && _owner == source;

    /// <summary>
    /// True when <paramref name="source"/> may command the player (SPEC-receiver.md 8.2): it owns the output, or nobody does
    /// (see the class remarks).
    /// </summary>
    public bool MayCommand(AudioSource source)
    {
        var owner = _owner;
        return owner == source || owner == AudioSource.None;
    }

    /// <summary>
    /// The command gate (SPEC-receiver.md 8.2): true when <paramref name="source"/> may send <paramref name="command"/> to the
    /// player; otherwise the command is counted and logged at debug level, and the caller drops it.
    /// </summary>
    public bool Admit(AudioSource source, string command)
    {
        if (MayCommand(source))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCommands);
        _logger.LogDebug("{Command} from {Source} dropped: the output belongs to {Owner}", command, source, _owner);
        return false;
    }

    /// <summary>
    /// The gate for a player start (SPEC-receiver.md 8.2): like <see cref="Admit"/>, and a start Spotify makes while nobody
    /// owns the output claims it (its first write). A dropped start is remembered and replayed through the source's hook
    /// when the source gains ownership.
    /// </summary>
    public bool AdmitStart(AudioSource source)
    {
        if (source == AudioSource.Spotify && TryClaimUnowned(AudioSource.Spotify))
        {
            return true;
        }

        if (source == AudioSource.Airplay && MayCommand(AudioSource.Airplay))
        {
            return true;
        }

        lock (_lock)
        {
            if (_owner == source)
            {
                return true;
            }

            if (source == AudioSource.Spotify)
            {
                _spotifyStartDropped = true;
            }
            else if (source == AudioSource.Airplay)
            {
                _airplayStartDropped = true;
            }
        }

        Interlocked.Increment(ref _droppedCommands);
        _logger.LogDebug("Start from {Source} dropped and remembered: the output belongs to {Owner}", source, _owner);
        return false;
    }

    /// <summary>
    /// Takes the output for <paramref name="source"/> when nobody owns it, without an eviction (SPEC-receiver.md 8.2:
    /// Spotify's first write while <see cref="AudioSource.None"/> claims). True when <paramref name="source"/> owns the
    /// output afterwards.
    /// </summary>
    public bool TryClaimUnowned(AudioSource source)
    {
        if (source == AudioSource.None)
        {
            throw new ArgumentException("None cannot claim the output.", nameof(source));
        }

        if (_owner == source)
        {
            return true;
        }

        OwnerChange change;
        lock (_lock)
        {
            if (_owner != AudioSource.None)
            {
                return _owner == source;
            }

            change = SetOwnerLocked(source, null);
        }

        AfterChange(change);
        return true;
    }

    /// <summary>
    /// Claims the output for <paramref name="source"/> (SPEC-receiver.md 8.2): evicts the current owner to completion (at
    /// most <see cref="SpotifyEvictionTimeout"/> for Spotify's pause), then sets the owner, replays a remembered <c>Start</c>
    /// and publishes <c>statusChanged</c>. A claim by the owner itself changes nothing, except that a new AirPlay sender
    /// replaces the previous one (R8: the receiver has ended the older session itself) and is published.
    /// </summary>
    public async Task ClaimAsync(AudioSource source, SenderInfo? sender, CancellationToken cancellationToken)
    {
        if (source == AudioSource.None)
        {
            throw new ArgumentException("None cannot claim the output; use Release.", nameof(source));
        }

        if (source != AudioSource.Airplay)
        {
            sender = null;
        }

        await _claims.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var current = _owner;
                if (current == source)
                {
                    if (source == AudioSource.Airplay && sender != _sender)
                    {
                        OwnerChange replaced;
                        lock (_lock)
                        {
                            replaced = SetOwnerLocked(source, sender);
                            _airplayStartDropped = false; // the replaced sender's dropped start is not the new one's
                        }

                        _logger.LogInformation("AirPlay sender {Sender} replaces the previous one", Describe(sender));
                        AfterChange(replaced);
                    }

                    return;
                }

                if (current == AudioSource.Spotify)
                {
                    await EvictAsync(AudioSource.Spotify, _hooks.EvictSpotifyAsync, SpotifyEvictionTimeout, cancellationToken).ConfigureAwait(false);
                }
                else if (current == AudioSource.Airplay && _hooks.EvictAirplayAsync is { } endSession)
                {
                    var reason = source == AudioSource.Spotify ? "replaced by Spotify" : "replaced by " + source;
                    await EvictAsync(AudioSource.Airplay, ct => endSession(reason, ct), AirplayEvictionTimeout, cancellationToken).ConfigureAwait(false);
                }

                OwnerChange change;
                bool replay;
                lock (_lock)
                {
                    if (_owner != current && _owner != AudioSource.None)
                    {
                        // A source took the output without the claim semaphore (TryClaimUnowned) while this claim evicted
                        // the previous owner: evict that one too before granting (8.2).
                        continue;
                    }

                    change = SetOwnerLocked(source, sender);
                    if (source == AudioSource.Spotify)
                    {
                        replay = _spotifyStartDropped;
                        _spotifyStartDropped = false;
                    }
                    else
                    {
                        replay = _airplayStartDropped;
                        _airplayStartDropped = false;
                    }
                }

                AfterChange(change);
                if (replay)
                {
                    await ReplayStartAsync(source, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
        }
        finally
        {
            _claims.Release();
        }
    }

    /// <summary>Sets <see cref="AudioSource.None"/> when <paramref name="source"/> owns the output (SPEC-receiver.md 8.2: a sender left, Spotify stopped).</summary>
    public void Release(AudioSource source) => Release(source, null);

    /// <summary>
    /// Sets <see cref="AudioSource.None"/> when <paramref name="source"/> owns the output (SPEC-receiver.md 8.2). With a
    /// <paramref name="sender"/>, an AirPlay release applies only while that sender owns it: the end of an older session a
    /// newer sender replaced (R8) does not release the newer one.
    /// </summary>
    public void Release(AudioSource source, SenderInfo? sender)
    {
        OwnerChange change;
        lock (_lock)
        {
            if (source == AudioSource.None || _owner != source)
            {
                return;
            }

            if (source == AudioSource.Airplay && sender is not null && _sender is not null && sender != _sender)
            {
                _logger.LogDebug("Release by the replaced sender {Sender} ignored", Describe(sender));
                return;
            }

            change = SetOwnerLocked(AudioSource.None, null);
        }

        AfterChange(change);
    }

    /// <summary>
    /// A source's playback view changed (SPEC-receiver.md 8.2): stores it as <see cref="RuntimeState.SpotifyPlayback"/> or
    /// <see cref="RuntimeState.AirplayPlayback"/> and, when it is visible (Spotify's while the owner is not AirPlay,
    /// AirPlay's while AirPlay owns), makes it <see cref="RuntimeState.Playback"/> with <c>Source</c> set and publishes
    /// <c>playbackChanged</c>. Returns true when it was published.
    /// </summary>
    public bool PublishPlayback(AudioSource source, PlaybackDto playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        PlaybackDto view;
        lock (_lock)
        {
            switch (source)
            {
                case AudioSource.Spotify:
                    _runtime.SpotifyPlayback = playback with { Source = OutputManager.SourceSpotify };
                    break;
                case AudioSource.Airplay:
                    _runtime.AirplayPlayback = playback with { Source = OutputManager.SourceAirplay };
                    break;
                default:
                    throw new ArgumentException("None has no playback view.", nameof(source));
            }

            var owner = _owner;
            var visible = source == AudioSource.Airplay ? owner == AudioSource.Airplay : owner != AudioSource.Airplay;
            if (!visible)
            {
                return false;
            }

            view = playback with { Source = ToApiName(owner) };
            _runtime.Playback = view;
            Emit(new BridgeEvent.PlaybackChanged(view));
        }

        return true;
    }

    private static string Describe(SenderInfo? sender) => sender is null ? "(unknown)" : sender.Name ?? sender.Model ?? sender.Address.ToString();

    /// <summary>The owner's view (SPEC-receiver.md 8.2): an empty one for <see cref="AudioSource.None"/>.</summary>
    private PlaybackDto ViewOf(AudioSource owner) => owner switch
    {
        AudioSource.Spotify => _runtime.SpotifyPlayback with { Source = OutputManager.SourceSpotify },
        AudioSource.Airplay => _runtime.AirplayPlayback with { Source = OutputManager.SourceAirplay },
        _ => new PlaybackDto(false, null, null, null, null, 0, 0, null, _runtime.SpotifyPlayback.SpotifyVolume),
    };

    /// <summary>Sets the owner and republishes its playback view; call under <see cref="_lock"/>.</summary>
    private OwnerChange SetOwnerLocked(AudioSource owner, SenderInfo? sender)
    {
        var previous = _owner;
        _owner = owner;
        _sender = owner == AudioSource.Airplay ? sender : null;
        var view = ViewOf(owner);
        _runtime.Playback = view;
        Emit(new BridgeEvent.PlaybackChanged(view));
        if (_hooks.StatusAsync is { } build)
        {
            // Queued under the lock with the change's owner, so the statuses follow the owner changes in order.
            _statusPublication = PublishStatusAsync(_statusPublication, build, owner);
        }

        return new OwnerChange(previous, owner, _sender);
    }

    private void AfterChange(OwnerChange change)
    {
        if (change.Previous != change.Current)
        {
            _logger.LogInformation("Output owner: {Previous} -> {Owner}{Sender}", change.Previous, change.Current, change.Sender is null ? string.Empty : " (" + Describe(change.Sender) + ")");
        }

        try
        {
            OwnerChanged?.Invoke(this, change);
        }
        catch (Exception ex)
        {
            // A subscriber's bug must not undo the change or fail the claimant.
            _logger.LogError(ex, "An ownership change handler failed");
        }
    }

    /// <summary>Builds and publishes one <c>statusChanged</c> after <paramref name="previous"/>, naming the owner of its change.</summary>
    private async Task PublishStatusAsync(Task previous, Func<CancellationToken, Task<StatusDto>> build, AudioSource owner)
    {
        await Task.Yield(); // the builder runs outside the switch's lock (it is queued under it)
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            var status = await build(CancellationToken.None).ConfigureAwait(false);
            Emit(new BridgeEvent.StatusChanged(status with { ActiveSource = ToApiName(owner) }));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("Publishing the status after an ownership change failed: {Error}", ex.Message);
        }
    }

    private void Emit(BridgeEvent bridgeEvent)
    {
        _hub?.Publish(bridgeEvent);
        Published?.Invoke(bridgeEvent);
    }

    private async Task EvictAsync(AudioSource evicted, Func<CancellationToken, Task>? evict, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (evict is null)
        {
            return;
        }

        try
        {
            await evict(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Evicting {Source} did not complete within {Timeout}; granting the output anyway", evicted, timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The claimant wins regardless (R8); the evicted source's failure is its own.
            _logger.LogWarning("Evicting {Source} failed: {Error}; granting the output anyway", evicted, ex.Message);
        }
    }

    private async Task ReplayStartAsync(AudioSource source, CancellationToken cancellationToken)
    {
        var replay = source == AudioSource.Spotify ? _hooks.StartSpotifyIfRunningAsync : _hooks.ResumeAirplayIfPlayingAsync;
        if (replay is null)
        {
            return;
        }

        try
        {
            _logger.LogDebug("Replaying the start {Source} made while it did not own the output", source);
            await replay(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Starting {Source} after its claim failed: {Error}", source, ex.Message);
        }
    }
}
