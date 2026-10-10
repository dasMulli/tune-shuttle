using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.Channels;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtsp;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Spotify;
using Bridge.Service.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Output;

/// <summary>
/// Owns the active <see cref="IGroupPlayer"/>: applies the selected output
/// (a group or one receiver) from the settings, connects its receivers and reconnects lost ones with back-off, attaches
/// the player to the <see cref="AirplaySink"/>, keeps the volume model and the store in step (docs/ARCHITECTURE.md, "Group volume model"),
/// and turns the player's events into API events, persisted levels and Spotify commands.
/// </summary>
/// <remarks>
/// <para>
/// Volume (docs/ARCHITECTURE.md, "Group volume model"; SPEC-audio.md 6.3): for a group the store holds G (<c>groups.master_level</c>) and each r_i
/// (<c>group_members.level</c>); moving G scales every r_i by G'/G with <see cref="GroupVolumeModel"/>, clamping at 100.
/// While G is 0 the member levels keep their last values and the G they belong to is remembered
/// (<see cref="BridgeSettingKeys.GroupReferenceLevelPrefix"/>), so raising G again restores the relative levels. For a
/// single receiver G and r_i are the same value (<c>receivers.level</c>). A level a receiver reports on its own (its buttons,
/// SPEC.md D10) changes only its r_i. The Spotify slider drives G; changes made elsewhere are pushed back to Spotify
/// (<see cref="SpotifyVolumeUpdates"/>).
/// </para>
/// <para>
/// Lifecycle: <c>connected but idle</c> sessions stay up while Spotify is paused or stopped; a receiver is torn down only
/// when the user disconnects it, removes it from the active group or selects another output (SPEC-audio.md 8.4).
/// </para>
/// </remarks>
public sealed class OutputManager : IHostedService, IPlaybackOutput, IAsyncDisposable
{
    /// <summary>Source tag of level changes made in the web app.</summary>
    public const string SourceUser = "user";

    /// <summary>Source tag of level changes from the Spotify volume.</summary>
    public const string SourceSpotify = "spotify";

    /// <summary>Source tag of level changes a receiver reported itself.</summary>
    public const string SourceReceiver = "receiver";

    /// <summary>Largest per-receiver offset accepted (2 s either way, SPEC-audio.md 3.3).</summary>
    public const int MaxOffsetFrames = 88200;

    private static readonly TimeSpan SupervisorInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivationRetryInterval = TimeSpan.FromSeconds(30);

    private readonly IBridgeStore _store;
    private readonly ReceiverInventory _inventory;
    private readonly AirplaySenderProvider _senders;
    private readonly AirplaySink _sink;
    private readonly EventHub _hub;
    private readonly DtoFactory _dtos;
    private readonly RuntimeState _runtime;
    private readonly PairingCoordinator _pairing;
    private readonly IOptions<BridgeOptions> _options;
    private readonly ILogger<OutputManager> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _volumeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, MemberPlan> _plans = new(StringComparer.Ordinal);
    private readonly BackgroundTasks _background;
    private readonly Channel<string> _remote = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<int> _spotifyVolume = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stopping = new();
    private volatile ActiveOutput? _active;
    private volatile LastNowPlaying? _lastNowPlaying;
    private Task _supervisor = Task.CompletedTask;
    private int _stopped;
    private int _activating;
    private DateTimeOffset _nextActivation;

    /// <summary>Creates the manager; <see cref="StartAsync"/> restores the selected output.</summary>
    public OutputManager(
        IBridgeStore store,
        ReceiverInventory inventory,
        AirplaySenderProvider senders,
        AirplaySink sink,
        EventHub hub,
        DtoFactory dtos,
        RuntimeState runtime,
        PairingCoordinator pairing,
        IOptions<BridgeOptions> options,
        ILogger<OutputManager> logger,
        TimeProvider? time = null)
    {
        _store = store;
        _inventory = inventory;
        _senders = senders;
        _sink = sink;
        _hub = hub;
        _dtos = dtos;
        _runtime = runtime;
        _pairing = pairing;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _background = new BackgroundTasks(logger, static ex => IsReceiverFailure(ex) || ex is KeyNotFoundException or ArgumentException);
    }

    /// <summary>The active player, or null (diagnostics).</summary>
    public IGroupPlayer? ActivePlayer => _active?.Player;

    /// <inheritdoc />
    public long FramesWritten => _sink.FramesWritten;

    /// <inheritdoc />
    public ChannelReader<string> RemoteCommands => _remote.Reader;

    /// <inheritdoc />
    public ChannelReader<int> SpotifyVolumeUpdates => _spotifyVolume.Reader;

    // ---- hosting -------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>Starts the reconnect supervisor and restores the selected output in the background (connecting can take seconds).</remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _supervisor = SuperviseAsync(_stopping.Token);
        _background.Track(RestoreAsync(_stopping.Token), "Restoring the output");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Tears every receiver down (TEARDOWN, D7) and stops the supervisor.</remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _background.ObserveAsync(_supervisor, "The supervisor").ConfigureAwait(false);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DisposeActiveCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await _background.DrainAsync().ConfigureAwait(false);
        _remote.Writer.TryComplete();
        _spotifyVolume.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
    }

    // ---- output selection ----------------------------------------------------------------------------------------

    /// <summary>Selects the output (a group, one receiver, or none) and connects its receivers; returns the new output.</summary>
    /// <exception cref="KeyNotFoundException">Unknown group or receiver.</exception>
    /// <exception cref="ArgumentException">Both a group and a receiver were given.</exception>
    public async Task<OutputDto> SelectAsync(OutputSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.GroupId is not null && selection.ReceiverId is not null)
        {
            throw new ArgumentException("Select either a group or a receiver, not both.", nameof(selection));
        }

        await ActivateAsync(selection, interactive: true, persist: true, cancellationToken).ConfigureAwait(false);
        return await _dtos.OutputAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /receivers/{id}/connect</c>: a member of the active group is (re)connected into it; any other receiver becomes
    /// the single-receiver output. A receiver that needs a PIN gets <c>pairingRequired</c> with a running pairing.
    /// </summary>
    public async Task<ReceiverDto> ConnectReceiverAsync(string receiverId, CancellationToken cancellationToken)
    {
        await _inventory.RequireRowAsync(receiverId, cancellationToken).ConfigureAwait(false);
        var active = _active;
        if (active is not null && _plans.TryGetValue(receiverId, out var plan))
        {
            plan.Reset();
            if (!active.Player.Members.TryGetValue(receiverId, out var state) || state == GroupMemberState.Lost)
            {
                // The connect belongs to the output, not to the request: a caller that goes away only stops waiting.
                var connecting = _background.Track(ConnectMemberAsync(active, plan, interactive: true, _stopping.Token), "Connecting " + receiverId);
                await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await ActivateAsync(new OutputSelection(null, receiverId), interactive: true, persist: true, cancellationToken).ConfigureAwait(false);
        }

        return await ReceiverDtoAsync(receiverId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /receivers/{id}/disconnect</c>: the single-receiver output is deselected; a member of the active group is torn
    /// down and left out of reconnects until the output is activated again.
    /// </summary>
    public async Task<ReceiverDto> DisconnectReceiverAsync(string receiverId, CancellationToken cancellationToken)
    {
        await _inventory.RequireRowAsync(receiverId, cancellationToken).ConfigureAwait(false);
        var active = _active;
        if (active?.Selection.ReceiverId == receiverId)
        {
            await ActivateAsync(OutputSelection.None, interactive: false, persist: true, cancellationToken).ConfigureAwait(false);
        }
        else if (active is not null && _plans.TryGetValue(receiverId, out var plan))
        {
            plan.UserDisconnected = true;
            await active.Player.RemoveReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
            _runtime.Clear(receiverId);
            _logger.LogInformation("Receiver {Receiver} disconnected by the user", receiverId);
            await PublishOutputAsync(cancellationToken).ConfigureAwait(false);
        }

        await _inventory.PublishAsync(receiverId, cancellationToken).ConfigureAwait(false);
        return await ReceiverDtoAsync(receiverId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A receiver was added to a group: when the group is the output it joins now (mid-stream, SPEC-audio.md 3.2).</summary>
    public Task MemberAddedAsync(string groupId, string receiverId, CancellationToken cancellationToken)
    {
        var active = _active;
        if (active?.Selection.GroupId != groupId)
        {
            return Task.CompletedTask;
        }

        var plan = _plans.GetOrAdd(receiverId, id => new MemberPlan(id, _time.GetUtcNow()));
        plan.Reset();
        _background.Track(ConnectMemberAsync(active, plan, interactive: true, _stopping.Token), "Connecting " + receiverId);
        return Task.CompletedTask;
    }

    /// <summary>A receiver was removed from a group: when the group is the output it is torn down now.</summary>
    public async Task MemberRemovedAsync(string groupId, string receiverId, CancellationToken cancellationToken)
    {
        var active = _active;
        if (active?.Selection.GroupId != groupId)
        {
            return;
        }

        _plans.TryRemove(receiverId, out _);
        await active.Player.RemoveReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
        _runtime.Clear(receiverId);
        await _inventory.PublishAsync(receiverId, cancellationToken).ConfigureAwait(false);
        await PublishOutputAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A group is about to be deleted: when it is the output, the output is cleared first (TEARDOWN of its members).</summary>
    public async Task GroupDeletingAsync(string groupId, CancellationToken cancellationToken)
    {
        if (_runtime.Selection.GroupId == groupId)
        {
            await ActivateAsync(OutputSelection.None, interactive: false, persist: true, cancellationToken).ConfigureAwait(false);
        }

        await _store.SetSettingAsync(BridgeSettingKeys.GroupReferenceLevelPrefix + groupId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A receiver is about to be forgotten (<see cref="ReceiverJanitor"/>): as the single output it is deselected; as a
    /// member of the active group it leaves the player and its reconnect plan; its runtime state is dropped. Returns
    /// true when the output changed (the caller publishes the output once the row is gone).
    /// </summary>
    public async Task<bool> ReceiverForgettingAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        var changed = false;
        if (_runtime.Selection.ReceiverId == receiverId)
        {
            await ActivateAsync(OutputSelection.None, interactive: false, persist: true, cancellationToken).ConfigureAwait(false);
            changed = true;
        }
        else if (_active is { } active && _plans.TryRemove(receiverId, out _))
        {
            await active.Player.RemoveReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        _runtime.Clear(receiverId);
        return changed;
    }

    /// <summary>
    /// Applies changed settings: a new device name or stream preference needs a new sender (identity, transport choice), a
    /// new L or poll setting a new player; the active output is rebuilt and its receivers reconnected.
    /// </summary>
    public async Task ApplySettingsAsync(BridgeSettings previous, BridgeSettings current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous == current)
        {
            return;
        }

        var newSender = previous.DeviceName != current.DeviceName || previous.PreferredStream != current.PreferredStream;
        var newPlayer = newSender || previous.LatencyFrames != current.LatencyFrames || previous.PollReceiverVolume != current.PollReceiverVolume;
        var selection = _runtime.Selection;
        if (newSender)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DisposeActiveCoreAsync().ConfigureAwait(false);
                await _senders.ResetAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        if (newPlayer && !selection.IsNone)
        {
            _logger.LogInformation("Settings changed: rebuilding the output");
            await ActivateAsync(selection, interactive: false, persist: false, cancellationToken, onlyIfCurrent: true).ConfigureAwait(false);
        }
    }

    // ---- volume --------------------------------------------------------------------------------------------------

    /// <summary>Sets r_i (<c>PUT /receivers/{id}/volume</c>): inside the active group its group level, else its own level.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Level outside 0..100.</exception>
    /// <exception cref="KeyNotFoundException">Unknown receiver.</exception>
    public async Task<ReceiverDto> SetReceiverLevelAsync(string receiverId, int level, string source, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 100);
        string? groupId = null;
        await _volumeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _inventory.RequireRowAsync(receiverId, cancellationToken).ConfigureAwait(false);
            var selection = _runtime.Selection;
            var active = _active;
            var group = selection.GroupId is null ? null : await _store.GetGroupAsync(selection.GroupId, cancellationToken).ConfigureAwait(false);
            if (group is not null && group.Members.Any(m => m.ReceiverId == receiverId))
            {
                groupId = group.Id;
                await _store.SetGroupMemberAsync(group.Id, receiverId, level, cancellationToken).ConfigureAwait(false);
                if (active?.Selection == selection)
                {
                    await TrySetPlayerLevelAsync(active, receiverId, level, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await _store.SetReceiverLevelAsync(receiverId, level, cancellationToken).ConfigureAwait(false);
                if (selection.ReceiverId == receiverId && active?.Selection == selection)
                {
                    await TrySetPlayerLevelAsync(active, receiverId, level, cancellationToken).ConfigureAwait(false);
                    if (source != SourceSpotify)
                    {
                        _spotifyVolume.Writer.TryWrite(level);
                    }
                }
            }
        }
        finally
        {
            _volumeGate.Release();
        }

        _hub.Publish(new BridgeEvent.VolumeChanged(groupId, receiverId, level, source));
        await _inventory.PublishAsync(receiverId, cancellationToken).ConfigureAwait(false);
        await PublishOutputAsync(cancellationToken).ConfigureAwait(false);
        return await ReceiverDtoAsync(receiverId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets G of a group (<c>PUT /groups/{id}/volume</c>, the Spotify slider): every r_i scales by G'/G.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Level outside 0..100.</exception>
    /// <exception cref="KeyNotFoundException">Unknown group.</exception>
    public async Task<GroupDto> SetGroupLevelAsync(string groupId, int level, string source, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 100);
        await _volumeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var group = await _store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Unknown group '{groupId}'.");
            if (group.MasterLevel != level)
            {
                var desired = await ScaledLevelsAsync(group, level, cancellationToken).ConfigureAwait(false);
                if (level > 0)
                {
                    foreach (var member in group.Members)
                    {
                        if (desired.TryGetValue(member.ReceiverId, out var target) && target != member.Level)
                        {
                            await _store.SetGroupMemberAsync(group.Id, member.ReceiverId, target, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    await _store.SetSettingAsync(BridgeSettingKeys.GroupReferenceLevelPrefix + group.Id, null, cancellationToken).ConfigureAwait(false);
                }
                else if (group.MasterLevel > 0)
                {
                    // G = 0: keep the member levels and remember the G they belong to (see the class remarks).
                    await _store.SetSettingAsync(BridgeSettingKeys.GroupReferenceLevelPrefix + group.Id, group.MasterLevel.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                }

                await _store.SetGroupMasterLevelAsync(group.Id, level, cancellationToken).ConfigureAwait(false);
                var active = _active;
                if (active?.Selection.GroupId == group.Id)
                {
                    await active.Player.SetMasterLevelAsync(level, cancellationToken).ConfigureAwait(false);
                    foreach (var (receiverId, target) in desired)
                    {
                        // The player scales by its own weights; members that joined while G was 0 have none, so correct them.
                        if (active.Player.Volume.Levels.TryGetValue(receiverId, out var actual) && actual != target)
                        {
                            await TrySetPlayerLevelAsync(active, receiverId, target, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                _logger.LogInformation("Group {Group}: G {Old} -> {New} ({Source})", group.Name, group.MasterLevel, level, source);
            }

            if (source != SourceSpotify && _active?.Selection.GroupId == group.Id)
            {
                _spotifyVolume.Writer.TryWrite(level);
            }
        }
        finally
        {
            _volumeGate.Release();
        }

        _hub.Publish(new BridgeEvent.VolumeChanged(groupId, null, level, source));
        var dto = await _dtos.GroupAsync(groupId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Unknown group '{groupId}'.");
        _hub.Publish(new BridgeEvent.GroupChanged(dto));
        if (_runtime.Selection.GroupId == groupId)
        {
            _hub.Publish(new BridgeEvent.OutputChanged(await _dtos.OutputAsync(cancellationToken).ConfigureAwait(false)));
        }

        return dto;
    }

    /// <summary>Sets a receiver's latency trim (SPEC-audio.md 3.3); a connected receiver re-joins so its session uses it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Offset beyond ±<see cref="MaxOffsetFrames"/>.</exception>
    public async Task<ReceiverDto> SetOffsetAsync(string receiverId, int offsetFrames, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offsetFrames, -MaxOffsetFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offsetFrames, MaxOffsetFrames);
        var row = await _inventory.RequireRowAsync(receiverId, cancellationToken).ConfigureAwait(false);
        await _store.SetReceiverOffsetAsync(receiverId, offsetFrames, cancellationToken).ConfigureAwait(false);
        var active = _active;
        if (row.OffsetFrames != offsetFrames && active is not null && _plans.TryGetValue(receiverId, out var plan)
            && active.Player.Members.TryGetValue(receiverId, out var state) && state != GroupMemberState.Lost)
        {
            _logger.LogInformation("Offset of {Receiver} is now {Offset} frames; re-joining it", row.Name, offsetFrames);
            await active.Player.RemoveReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
            await ConnectMemberAsync(active, plan, interactive: false, cancellationToken).ConfigureAwait(false);
        }

        await _inventory.PublishAsync(receiverId, cancellationToken).ConfigureAwait(false);
        return await ReceiverDtoAsync(receiverId, cancellationToken).ConfigureAwait(false);
    }

    // ---- IPlaybackOutput -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken) => _active?.Player.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task SetNowPlayingAsync(NowPlaying nowPlaying, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nowPlaying);
        _lastNowPlaying = new LastNowPlaying(nowPlaying, _sink.FramesWritten);
        var player = _active?.Player;
        return player is null
            ? Task.CompletedTask
            : player.SetNowPlayingAsync(nowPlaying.Metadata, nowPlaying.Artwork, nowPlaying.ArtworkContentType, nowPlaying.Elapsed, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetSpotifyVolumeAsync(int level, CancellationToken cancellationToken)
    {
        level = Math.Clamp(level, 0, 100);
        var selection = _runtime.Selection;
        if (selection.GroupId is not null)
        {
            await SetGroupLevelAsync(selection.GroupId, level, SourceSpotify, cancellationToken).ConfigureAwait(false);
        }
        else if (selection.ReceiverId is not null)
        {
            var row = await _store.GetReceiverAsync(selection.ReceiverId, cancellationToken).ConfigureAwait(false);
            if (row is not null && row.Level != level)
            {
                await SetReceiverLevelAsync(selection.ReceiverId, level, SourceSpotify, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<int?> CurrentLevelAsync(CancellationToken cancellationToken)
    {
        var selection = _runtime.Selection;
        if (selection.GroupId is not null)
        {
            return (await _store.GetGroupAsync(selection.GroupId, cancellationToken).ConfigureAwait(false))?.MasterLevel;
        }

        return selection.ReceiverId is null ? null : (await _store.GetReceiverAsync(selection.ReceiverId, cancellationToken).ConfigureAwait(false))?.Level;
    }

    // ---- internals -----------------------------------------------------------------------------------------------

    private static bool IsReceiverFailure(Exception ex) =>
        ex is IOException or TimeoutException or SocketException or RtspStatusException or PairingException or InvalidOperationException
            or ObjectDisposedException or SqliteException; // PtpMasterException is an IOException

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var groupId = await _store.GetSettingAsync(SettingKeys.SelectedOutputGroup, cancellationToken).ConfigureAwait(false);
        var receiverId = await _store.GetSettingAsync(SettingKeys.SelectedOutputReceiver, cancellationToken).ConfigureAwait(false);
        var selection = new OutputSelection(string.IsNullOrEmpty(groupId) ? null : groupId, string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(receiverId) ? receiverId : null);
        if (selection.IsNone)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Restoring the output: {Selection}", selection.GroupId is not null ? "group " + selection.GroupId : "receiver " + selection.ReceiverId);
            await ActivateAsync(selection, interactive: false, persist: false, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning("The stored output no longer exists ({Error}); nothing selected", ex.Message);
            await _store.SetSettingAsync(SettingKeys.SelectedOutputGroup, null, cancellationToken).ConfigureAwait(false);
            await _store.SetSettingAsync(SettingKeys.SelectedOutputReceiver, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ActivateAsync(OutputSelection selection, bool interactive, bool persist, CancellationToken cancellationToken, bool onlyIfCurrent = false)
    {
        ActiveOutput? active = null;
        var previous = _runtime.Selection;
        _nextActivation = _time.GetUtcNow() + ActivationRetryInterval;
        Interlocked.Increment(ref _activating);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _activating);
            throw;
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
            if (onlyIfCurrent && _runtime.Selection != selection)
            {
                return; // a rebuild or retry that the user overtook with another selection
            }

            GroupRow? group = null;
            if (selection.GroupId is not null)
            {
                group = await _store.GetGroupAsync(selection.GroupId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Unknown group '{selection.GroupId}'.");
            }
            else if (selection.ReceiverId is not null)
            {
                await _inventory.RequireRowAsync(selection.ReceiverId, cancellationToken).ConfigureAwait(false);
            }

            if (persist)
            {
                await _store.SetSettingAsync(SettingKeys.SelectedOutputGroup, selection.GroupId, cancellationToken).ConfigureAwait(false);
                await _store.SetSettingAsync(SettingKeys.SelectedOutputReceiver, selection.ReceiverId, cancellationToken).ConfigureAwait(false);
            }

            await DisposeActiveCoreAsync().ConfigureAwait(false);
            _runtime.Selection = selection;
            if (!selection.IsNone)
            {
                active = await BuildAsync(selection, group, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            Interlocked.Decrement(ref _activating);
        }

        _logger.LogInformation("Output: {Selection}", selection.IsNone ? "none" : selection.GroupId is not null ? "group " + selection.GroupId : "receiver " + selection.ReceiverId);
        await PublishOutputAsync(cancellationToken, previous.GroupId).ConfigureAwait(false);
        if (active is null)
        {
            return;
        }

        // The connects (D5, up to ConnectTimeout each) belong to the output, not to the request: a caller that goes away
        // (a browser navigating off) only stops waiting; the receivers still connect.
        var connecting = _background.Track(ConnectAllAsync(active, interactive, _stopping.Token), "Connecting the output");
        await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConnectAllAsync(ActiveOutput active, bool interactive, CancellationToken cancellationToken)
    {
        await Task.WhenAll(_plans.Values.Select(plan => ConnectMemberAsync(active, plan, interactive, cancellationToken))).ConfigureAwait(false);
        await PublishOutputAsync(cancellationToken).ConfigureAwait(false);
        if (await CurrentLevelAsync(cancellationToken).ConfigureAwait(false) is { } level)
        {
            _spotifyVolume.Writer.TryWrite(level); // the Spotify slider shows the new output's G
        }
    }

    private async Task<ActiveOutput> BuildAsync(OutputSelection selection, GroupRow? group, CancellationToken cancellationToken)
    {
        var sender = await _senders.GetAsync(cancellationToken).ConfigureAwait(false);
        var settings = await BridgeSettings.LoadAsync(_store, _options.Value, cancellationToken).ConfigureAwait(false);
        var ids = group is not null
            ? group.Members.OrderBy(m => m.Position).Select(m => m.ReceiverId).ToList()
            : [selection.ReceiverId!];

        // The intended members decide the transport (realtime/buffered, NTP/PTP: AirplaySender.ChooseTransport); members not
        // on the network right now are described from their stored rows.
        var known = new List<DiscoveredReceiver>();
        var online = _inventory.Browser.Snapshot();
        foreach (var id in ids)
        {
            var receiver = online.FirstOrDefault(r => r.Id == id);
            if (receiver is null && await _store.GetReceiverAsync(id, cancellationToken).ConfigureAwait(false) is { } row)
            {
                receiver = ReceiverInventory.FromRow(row);
            }

            if (receiver is not null)
            {
                known.Add(receiver);
            }
        }

        var playerOptions = AirplaySenderProvider.PlayerOptions(settings, _options.Value.PollReceiverVolumeSeconds, group?.Id) with { Sender = sender.Options.Identity };
        var player = await sender.CreateGroupPlayerAsync(known, playerOptions, cancellationToken).ConfigureAwait(false);
        try
        {
            if (group is not null)
            {
                await player.SetMasterLevelAsync(group.MasterLevel, cancellationToken).ConfigureAwait(false);
            }

            var active = new ActiveOutput(player, selection);
            active.Pump = PumpAsync(active);
            _active = active;
            _plans.Clear();
            var firstAttempt = _time.GetUtcNow() + TimeSpan.FromSeconds(5); // the activation connects first; the supervisor retries
            foreach (var id in ids)
            {
                _plans[id] = new MemberPlan(id, firstAttempt);
            }

            await _sink.AttachAsync(player, cancellationToken).ConfigureAwait(false);
            if (_lastNowPlaying is { } last)
            {
                // A new output mid-track gets the current track at once (SPEC-audio.md 7: members joining later get it from the player).
                var written = Math.Max(0, _sink.FramesWritten - last.Mark);
                var elapsed = last.NowPlaying.Elapsed + TimeSpan.FromSeconds(written / (double)PlayerEventMapper.SampleRate);
                await player.SetNowPlayingAsync(last.NowPlaying.Metadata, last.NowPlaying.Artwork, last.NowPlaying.ArtworkContentType, elapsed, cancellationToken).ConfigureAwait(false);
            }

            return active;
        }
        catch
        {
            if (_active?.Player == player)
            {
                await DisposeActiveCoreAsync().ConfigureAwait(false);
            }
            else
            {
                await player.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Detaches and disposes the active player (TEARDOWN of every member). Call under <see cref="_gate"/>.</summary>
    private async Task DisposeActiveCoreAsync()
    {
        var active = _active;
        if (active is null)
        {
            return;
        }

        _active = null;
        var ids = _plans.Keys.ToList();
        _plans.Clear();
        try
        {
            await _sink.AttachAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The sink is closed already (disposed before this manager stopped): nothing feeds the player any more, and its
            // receivers must still get their TEARDOWN below (D7).
        }

        await active.Cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            await active.Player.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsReceiverFailure(ex) || ex is OperationCanceledException)
        {
            _logger.LogWarning("Closing the output failed: {Error}", ex.Message);
        }

        await _background.ObserveAsync(active.Pump, "The player event pump").ConfigureAwait(false);
        active.Cancel.Dispose();
        _runtime.Statistics = null;
        foreach (var id in ids)
        {
            _runtime.Clear(id);
            await PublishReceiverQuietlyAsync(id).ConfigureAwait(false);
        }
    }

    private async Task<bool> ConnectMemberAsync(ActiveOutput active, MemberPlan plan, bool interactive, CancellationToken cancellationToken)
    {
        if (!plan.TryBeginConnect())
        {
            return false;
        }

        var options = _options.Value;
        try
        {
            if (_active != active || plan.UserDisconnected)
            {
                return false;
            }

            await SetStateAsync(plan.Id, ReceiverConnectionState.Connecting, null).ConfigureAwait(false);
            var receiver = await _inventory.ResolveAsync(plan.Id, cancellationToken).ConfigureAwait(false);
            if (receiver is null)
            {
                plan.Failed(_time.GetUtcNow(), options);
                await SetStateAsync(plan.Id, ReceiverConnectionState.Offline, "Not found on the network.").ConfigureAwait(false);
                return false;
            }

            if (receiver.Capabilities.Pairing == PairingRequirement.Disabled)
            {
                plan.NeedsPairing = true;
                await SetStateAsync(plan.Id, ReceiverConnectionState.Error, "The receiver admits only members of its HomeKit home (acl=1).").ConfigureAwait(false);
                return false;
            }

            var row = await _store.GetReceiverAsync(plan.Id, cancellationToken).ConfigureAwait(false);
            _senders.ApplyOffset(receiver, row?.OffsetFrames ?? 0);
            var level = await DesiredLevelAsync(active.Selection, plan.Id, cancellationToken).ConfigureAwait(false);
            if (active.Player.Members.TryGetValue(plan.Id, out var existing))
            {
                if (existing != GroupMemberState.Lost)
                {
                    plan.Succeeded();
                    return true;
                }

                await active.Player.RemoveReceiverAsync(plan.Id, cancellationToken).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            timeout.CancelAfter(options.ConnectTimeout);
            _logger.LogInformation("Connecting {Receiver} at level {Level}", receiver.Name, level);
            try
            {
                await active.Player.AddReceiverAsync(receiver, level, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
            {
                throw new TimeoutException($"Connecting {receiver.Name} took longer than {options.ConnectTimeout.TotalSeconds:0} s.");
            }

            plan.Succeeded();
            if (_active == active && active.Player.Members.TryGetValue(plan.Id, out var state) && state != GroupMemberState.Lost)
            {
                await SetStateAsync(plan.Id, state == GroupMemberState.Streaming ? ReceiverConnectionState.Streaming : ReceiverConnectionState.Connected, null).ConfigureAwait(false);
            }

            return true;
        }
        catch (PairingException ex) when (ex.Failure is PairingFailure.TransientRefused or PairingFailure.CredentialsRejected or PairingFailure.Authentication)
        {
            // SPEC-control.md 3.9: transient refused (470) or stale keys: the user has to run the PIN flow.
            plan.NeedsPairing = true;
            _logger.LogWarning("{Receiver} needs a PIN pairing: {Error}", plan.Id, ex.Message);
            await TryAsync(() => _pairing.SetPairingStateAsync(plan.Id, PairingState.PinRequired, CancellationToken.None)).ConfigureAwait(false);
            await SetStateAsync(plan.Id, ReceiverConnectionState.Error, "PIN pairing required.").ConfigureAwait(false);
            if (interactive)
            {
                await _pairing.AnnounceRequiredAsync(plan.Id, _stopping.Token).ConfigureAwait(false);
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stopping.IsCancellationRequested)
        {
            plan.Failed(_time.GetUtcNow(), options);
            if (!_stopping.IsCancellationRequested)
            {
                await SetStateAsync(plan.Id, ReceiverConnectionState.Error, "Connect cancelled; retrying.").ConfigureAwait(false);
            }

            return false;
        }
        catch (Exception ex) when (IsReceiverFailure(ex) || ex is KeyNotFoundException)
        {
            plan.Failed(_time.GetUtcNow(), options);
            var detail = ex is RtspStatusException status ? $"{ex.Message} (RTSP {status.Response.StatusCode})" : ex.Message;
            _logger.LogWarning("Connecting {Receiver} failed: {Error}; next attempt in {Delay:0} s", plan.Id, detail, (plan.NextAttempt - _time.GetUtcNow()).TotalSeconds);
            if (_active == active)
            {
                await SetStateAsync(plan.Id, ReceiverConnectionState.Error, detail).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            plan.EndConnect();
        }
    }

    /// <summary>The level a member joins with: its stored r_i scaled to the group's current G, or the receiver's own level.</summary>
    private async Task<int> DesiredLevelAsync(OutputSelection selection, string receiverId, CancellationToken cancellationToken)
    {
        if (selection.GroupId is not null && await _store.GetGroupAsync(selection.GroupId, cancellationToken).ConfigureAwait(false) is { } group)
        {
            var levels = await ScaledLevelsAsync(group, group.MasterLevel, cancellationToken).ConfigureAwait(false);
            if (levels.TryGetValue(receiverId, out var level))
            {
                return level;
            }
        }

        var row = await _store.GetReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
        return row?.Level ?? SqliteBridgeStore.DefaultLevel;
    }

    /// <summary>The members' levels at G = <paramref name="masterLevel"/>, scaled from the stored levels and their reference G.</summary>
    private async Task<IReadOnlyDictionary<string, int>> ScaledLevelsAsync(GroupRow group, int masterLevel, CancellationToken cancellationToken)
    {
        var reference = group.MasterLevel;
        if (reference == 0)
        {
            var stored = await _store.GetSettingAsync(BridgeSettingKeys.GroupReferenceLevelPrefix + group.Id, cancellationToken).ConfigureAwait(false);
            reference = int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 100;
        }

        var model = new GroupVolumeModel(reference);
        foreach (var member in group.Members)
        {
            model.SetReceiverLevel(member.ReceiverId, member.Level);
        }

        model.SetMasterLevel(masterLevel);
        return model.Levels;
    }

    private async Task TrySetPlayerLevelAsync(ActiveOutput active, string receiverId, int level, CancellationToken cancellationToken)
    {
        if (!active.Player.Members.TryGetValue(receiverId, out var state) || state == GroupMemberState.Lost)
        {
            return; // applied when it (re)joins: DesiredLevelAsync reads the store
        }

        try
        {
            await active.Player.SetReceiverLevelAsync(receiverId, level, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KeyNotFoundException || IsReceiverFailure(ex))
        {
            _logger.LogWarning("Setting the level of {Receiver} failed: {Error}", receiverId, ex.Message);
        }
    }

    private async Task OnReceiverReportedAsync(ActiveOutput active, string receiverId, int level, CancellationToken cancellationToken)
    {
        string? groupId = null;
        await _volumeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var selection = active.Selection;
            if (selection.GroupId is not null)
            {
                groupId = selection.GroupId;
                await _store.SetGroupMemberAsync(selection.GroupId, receiverId, level, cancellationToken).ConfigureAwait(false);
            }
            else if (selection.ReceiverId == receiverId)
            {
                await _store.SetReceiverLevelAsync(receiverId, level, cancellationToken).ConfigureAwait(false);
                _spotifyVolume.Writer.TryWrite(level); // single receiver: its level is G
            }
        }
        finally
        {
            _volumeGate.Release();
        }

        _logger.LogInformation("{Receiver} reports level {Level} (changed on the device)", receiverId, level);
        _hub.Publish(new BridgeEvent.VolumeChanged(groupId, receiverId, level, SourceReceiver));
        await _inventory.PublishAsync(receiverId, cancellationToken).ConfigureAwait(false);
        await PublishOutputAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync(ActiveOutput active)
    {
        var token = active.Cancel.Token;
        try
        {
            await foreach (var playerEvent in active.Player.Events.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await OnPlayerEventAsync(active, playerEvent, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    // One bad event must not stop the pump: later state changes and volume reports still count.
                    _logger.LogWarning(ex, "Handling {Event} failed", playerEvent.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // output closed
        }
    }

    private async Task OnPlayerEventAsync(ActiveOutput active, GroupPlayerEvent playerEvent, CancellationToken cancellationToken)
    {
        switch (playerEvent)
        {
            case GroupPlayerEvent.MemberStateChanged changed when _active == active:
                if (changed.State == GroupMemberState.Lost)
                {
                    if (changed.Reason == "removed")
                    {
                        if (_runtime.Get(changed.ReceiverId)?.State is not ReceiverConnectionState.Connecting)
                        {
                            _runtime.Clear(changed.ReceiverId);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("{Receiver} lost: {Reason}", changed.ReceiverId, changed.Reason);
                        if (_plans.TryGetValue(changed.ReceiverId, out var plan))
                        {
                            plan.Failed(_time.GetUtcNow(), _options.Value);
                        }

                        _runtime.Set(changed.ReceiverId, ReceiverConnectionState.Error, "Lost: " + changed.Reason);
                    }
                }
                else
                {
                    _runtime.Set(changed.ReceiverId, changed.State == GroupMemberState.Streaming ? ReceiverConnectionState.Streaming : ReceiverConnectionState.Connected, null);
                }

                await _inventory.PublishAsync(changed.ReceiverId, cancellationToken).ConfigureAwait(false);
                await PublishOutputAsync(cancellationToken).ConfigureAwait(false);
                break;

            case GroupPlayerEvent.ReceiverVolumeReported reported:
                await OnReceiverReportedAsync(active, reported.ReceiverId, reported.Level, cancellationToken).ConfigureAwait(false);
                break;

            case GroupPlayerEvent.RemoteCommand command:
                _logger.LogInformation("{Receiver} button: {Command}", command.ReceiverId, command.Command);
                _remote.Writer.TryWrite(command.Command);
                break;

            case GroupPlayerEvent.Statistics statistics:
                _runtime.Statistics = statistics;
                break;
        }
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var requests = _runtime.ReconnectRequests;
        while (!cancellationToken.IsCancellationRequested)
        {
            using (var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                tick.CancelAfter(SupervisorInterval);
                try
                {
                    await requests.WaitToReadAsync(tick.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // the periodic check
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            while (requests.TryRead(out var receiverId))
            {
                if (_plans.TryGetValue(receiverId, out var plan) && !plan.UserDisconnected)
                {
                    plan.RetryNow();
                }
            }

            var active = _active;
            if (active is null)
            {
                var selection = _runtime.Selection;
                if (!selection.IsNone && _time.GetUtcNow() >= _nextActivation && Volatile.Read(ref _activating) == 0)
                {
                    // The selected output has no player (its build failed, e.g. the network was down at boot): try again.
                    _nextActivation = _time.GetUtcNow() + ActivationRetryInterval;
                    _ = _background.Track(ActivateAsync(selection, interactive: false, persist: false, cancellationToken, onlyIfCurrent: true), "Retrying the output");
                }

                continue;
            }

            if (active.Player is GroupPlayer { IsFaulted: true } && !active.Rebuilding)
            {
                // GroupPlayer.IsFaulted: the pacer thread failed (a bug, not the network); a new player replaces it.
                _logger.LogError("The AirPlay player failed; rebuilding the output");
                active.Rebuilding = true;
                _ = _background.Track(ActivateAsync(active.Selection, interactive: false, persist: false, cancellationToken, onlyIfCurrent: true), "Rebuilding the output");
                continue;
            }

            var now = _time.GetUtcNow();
            foreach (var plan in _plans.Values)
            {
                if (plan.UserDisconnected || plan.NeedsPairing || plan.IsConnecting || plan.NextAttempt > now)
                {
                    continue;
                }

                if (active.Player.Members.TryGetValue(plan.Id, out var state) && state != GroupMemberState.Lost)
                {
                    continue;
                }

                _ = _background.Track(ConnectMemberAsync(active, plan, interactive: false, cancellationToken), "Reconnecting " + plan.Id);
            }
        }
    }

    private async Task SetStateAsync(string receiverId, ReceiverConnectionState state, string? error)
    {
        if (_runtime.Set(receiverId, state, error))
        {
            await PublishReceiverQuietlyAsync(receiverId).ConfigureAwait(false);
        }
    }

    private async Task PublishReceiverQuietlyAsync(string receiverId)
    {
        try
        {
            await _inventory.PublishAsync(receiverId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("Publishing {Receiver} failed: {Error}", receiverId, ex.Message);
        }
    }

    private async Task PublishOutputAsync(CancellationToken cancellationToken, string? alsoGroup = null)
    {
        _hub.Publish(new BridgeEvent.OutputChanged(await _dtos.OutputAsync(cancellationToken).ConfigureAwait(false)));
        foreach (var groupId in new[] { _runtime.Selection.GroupId, alsoGroup }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (await _dtos.GroupAsync(groupId, cancellationToken).ConfigureAwait(false) is { } group)
            {
                _hub.Publish(new BridgeEvent.GroupChanged(group));
            }
        }
    }

    private async Task<ReceiverDto> ReceiverDtoAsync(string receiverId, CancellationToken cancellationToken) =>
        await _dtos.ReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Unknown receiver '{receiverId}'.");

    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or KeyNotFoundException or ArgumentException)
        {
            _logger.LogDebug("Ignored: {Error}", ex.Message);
        }
    }

    /// <summary>The active player and what it plays to.</summary>
    private sealed class ActiveOutput(IGroupPlayer player, OutputSelection selection)
    {
        public IGroupPlayer Player { get; } = player;

        public OutputSelection Selection { get; } = selection;

        public CancellationTokenSource Cancel { get; } = new();

        public Task Pump { get; set; } = Task.CompletedTask;

        public bool Rebuilding { get; set; }
    }

    /// <summary>The last now-playing and the sink's frame counter when it was sent (for an output built mid-track).</summary>
    private sealed record LastNowPlaying(NowPlaying NowPlaying, long Mark);

    /// <summary>Reconnect bookkeeping for one intended member of the output.</summary>
    private sealed class MemberPlan(string id, DateTimeOffset firstAttempt)
    {
        private int _connecting;
        private int _failures;
        private long _nextAttemptTicks = firstAttempt.UtcTicks;

        public string Id { get; } = id;

        public bool IsConnecting => Volatile.Read(ref _connecting) != 0;

        public bool UserDisconnected { get; set; }

        public bool NeedsPairing { get; set; }

        public DateTimeOffset NextAttempt => new(Interlocked.Read(ref _nextAttemptTicks), TimeSpan.Zero);

        public bool TryBeginConnect() => Interlocked.Exchange(ref _connecting, 1) == 0;

        public void EndConnect() => Volatile.Write(ref _connecting, 0);

        public void Succeeded()
        {
            _failures = 0;
            NeedsPairing = false;
        }

        public void Failed(DateTimeOffset now, BridgeOptions options)
        {
            var failures = Interlocked.Increment(ref _failures);
            var delay = TimeSpan.FromTicks(Math.Min(options.MaxReconnectDelay.Ticks, options.ReconnectDelay.Ticks * (1L << Math.Min(failures - 1, 16))));
            Interlocked.Exchange(ref _nextAttemptTicks, (now + delay).UtcTicks);
        }

        public void RetryNow()
        {
            NeedsPairing = false;
            Interlocked.Exchange(ref _nextAttemptTicks, DateTimeOffset.MinValue.UtcTicks);
        }

        public void Reset()
        {
            UserDisconnected = false;
            NeedsPairing = false;
            _failures = 0;
            Interlocked.Exchange(ref _nextAttemptTicks, DateTimeOffset.MinValue.UtcTicks);
        }
    }
}
