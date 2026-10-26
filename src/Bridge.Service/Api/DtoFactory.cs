using System.Security.Cryptography;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Session;
using Bridge.Contracts;
using Bridge.Service.Airplay;
using Bridge.Service.Output;
using Bridge.Service.Store;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Api;

/// <summary>
/// Builds the API views from the three sources of truth: the store (receivers, groups, levels), discovery (online,
/// address, capabilities) and the runtime state (connection states, the selected output, Spotify), plus the AirPlay
/// fields of SPEC-receiver.md 9.1 from <see cref="IAirplayApiView"/> (the owner of the output and the receiver's state,
/// resolved from the service provider on first use, so <see cref="SourceSwitch"/> and <see cref="AirplaySource"/>
/// may depend on this factory). Pure reads, except the AirPlay artwork it keeps for <c>GET /api/playback/artwork</c>
/// (<see cref="AirplayPlayback"/>).
/// </summary>
public sealed class DtoFactory(IBridgeStore store, IReceiverBrowser browser, RuntimeState runtime, IOptions<BridgeOptions> options, IServiceProvider services)
{
    private readonly Lazy<IAirplayApiView> _airplay = new(
        () => services.GetService<IAirplayApiView>() ?? services.GetRequiredService<AirplaySource>(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private volatile AirplayArtwork? _artwork;
    private volatile Airplay2.Receiver.ReceiverIdentity? _self;

    /// <summary>The API's view of the sources (SPEC-receiver.md 8.2, 8.3).</summary>
    public IAirplayApiView Airplay => _airplay.Value;

    /// <summary>Who owns the output (SPEC-receiver.md 8.2): the switch's <c>Owner</c>.</summary>
    public AudioSource Owner => Airplay.Owner;

    /// <summary>
    /// The AirPlay sender's current artwork, or null (SPEC-receiver.md 9.1): the one kept by <see cref="AirplayPlayback"/>,
    /// else <see cref="IAirplayApiView.Artwork"/>; <c>GET /api/playback/artwork</c> serves it while AirPlay owns the output.
    /// </summary>
    public AirplayArtwork? AirplayArtwork => _artwork ?? Airplay.Artwork;

    /// <summary>
    /// Builds the AirPlay playback view (SPEC-receiver.md 8.3, 9.1) for <c>RuntimeState.AirplayPlayback</c> and
    /// <c>playbackChanged</c>, and keeps <paramref name="artwork"/> for <c>GET /api/playback/artwork</c>: new artwork
    /// replaces the kept one; a track without artwork clears it unless <paramref name="artworkKey"/> names the kept one. The
    /// key is the sender's <c>rtptime</c>; without one a key is derived from the bytes. <c>ArtworkUrl</c> is
    /// <c>/api/playback/artwork?v=&lt;key&gt;</c> while artwork is kept; <c>Source</c> is <c>airplay</c>.
    /// </summary>
    public PlaybackDto AirplayPlayback(TrackMetadata? metadata, bool playing, TimeSpan elapsed, ReadOnlyMemory<byte>? artwork, string? artworkContentType, uint? artworkKey, int level)
    {
        if (artwork is { Length: > 0 } bytes && !string.IsNullOrWhiteSpace(artworkContentType))
        {
            var copy = bytes.ToArray();
            _artwork = new AirplayArtwork(copy, artworkContentType, artworkKey ?? KeyOf(copy));
        }
        else if (artworkKey is null || _artwork?.Key != artworkKey)
        {
            _artwork = null;
        }

        return Mapping.ToAirplayPlaybackDto(metadata, playing, elapsed, _artwork?.Key, level);
    }

    /// <summary>Forgets the AirPlay artwork (SPEC-receiver.md 9.1: the session ended; the route answers 404 again).</summary>
    public void ClearAirplayArtwork() => _artwork = null;

    /// <summary>
    /// The playback view the API shows (SPEC-receiver.md 8.2, 9.1): <c>RuntimeState.Playback</c>, the owner's view, with
    /// <c>Source</c> set to the owner when the view does not name one.
    /// </summary>
    public PlaybackDto Playback()
    {
        var playback = runtime.Playback;
        return playback.Source is null && SourceSwitch.ToApiName(Owner) is { } owner ? playback with { Source = owner } : playback;
    }

    /// <summary>
    /// The settings answer (SPEC-receiver.md 9.1): the bridge settings, the forget-offline period, and the receiver's
    /// <c>AirplayEnabled</c>, <c>AirplayName</c> and <c>AirplayPasswordSet</c>; <c>AirplayPassword</c> is never carried.
    /// </summary>
    public async Task<SettingsDto> SettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await BridgeSettings.LoadAsync(store, options.Value, cancellationToken).ConfigureAwait(false);
        var retention = await ReceiverRetention.LoadAsync(store, options.Value, cancellationToken).ConfigureAwait(false);
        var airplay = await AirplaySettingsAsync(settings.DeviceName, cancellationToken).ConfigureAwait(false);
        return new SettingsDto(
            settings.DeviceName,
            settings.LatencyFrames,
            settings.PollReceiverVolume,
            settings.PreferredStream,
            ReceiverRetention.ToDays(retention),
            airplay.Enabled,
            airplay.Name,
            null,
            airplay.PasswordSet);
    }

    /// <summary>
    /// The stored receiver settings as <see cref="AirplaySettings.LoadAsync"/> reads them (SPEC-receiver.md 8.4), without
    /// touching the identity: <c>airplay_enabled</c> (else <c>Bridge:Airplay:Enabled</c>), <c>airplay_name</c> (else
    /// <paramref name="deviceName"/>) and whether <c>airplay_password</c> is set.
    /// </summary>
    public async Task<(bool Enabled, string Name, bool PasswordSet)> AirplaySettingsAsync(string deviceName, CancellationToken cancellationToken)
    {
        var enabled = await store.GetSettingAsync(BridgeSettingKeys.AirplayEnabled, cancellationToken).ConfigureAwait(false);
        var name = await store.GetSettingAsync(BridgeSettingKeys.AirplayName, cancellationToken).ConfigureAwait(false);
        var password = await store.GetSettingAsync(BridgeSettingKeys.AirplayPassword, cancellationToken).ConfigureAwait(false);
        return (bool.TryParse(enabled, out var e) ? e : options.Value.Airplay.Enabled, string.IsNullOrWhiteSpace(name) ? deviceName : name, !string.IsNullOrEmpty(password));
    }

    /// <summary>The receiver as the status reports it (SPEC-receiver.md 9.1 <c>StatusDto.Airplay</c>).</summary>
    public async Task<AirplayReceiverDto> AirplayReceiverAsync(CancellationToken cancellationToken)
    {
        var view = Airplay;
        var name = view.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            var settings = await BridgeSettings.LoadAsync(store, options.Value, cancellationToken).ConfigureAwait(false);
            name = (await AirplaySettingsAsync(settings.DeviceName, cancellationToken).ConfigureAwait(false)).Name;
        }

        return Mapping.ToAirplayReceiverDto(view.Status, name, view.Sender, view.AacAvailable);
    }

    /// <summary>The receiver's diagnostics (SPEC-receiver.md 9.1 <c>DiagnosticsDto.Airplay</c>, from 5.3 and 6.2).</summary>
    public AirplayDiagnosticsDto AirplayDiagnostics()
    {
        var view = Airplay;
        return Mapping.ToAirplayDiagnosticsDto(view.RtspPort, view.PtpBound, view.Statistics, view.PtpStatistics, view.DecoderStatus);
    }

    /// <summary>The receiver as discovery currently sees it, or null.</summary>
    public DiscoveredReceiver? Discovered(string receiverId) => Online().GetValueOrDefault(receiverId);

    /// <summary>The connection state the API shows: the output manager's, else Available/Offline by discovery.</summary>
    public ReceiverConnectionState StateOf(string receiverId, bool online) =>
        runtime.Get(receiverId)?.State ?? (online ? ReceiverConnectionState.Available : ReceiverConnectionState.Offline);

    /// <summary>One receiver (stored or only discovered), or null.</summary>
    public async Task<ReceiverDto?> ReceiverAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        var online = await OnlineAsync(cancellationToken).ConfigureAwait(false);
        var row = await store.GetReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
        var discovered = online.GetValueOrDefault(receiverId);
        if (row is null && discovered is null)
        {
            return null;
        }

        var groups = await store.GetGroupsAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row ?? discovered!.ToReceiverRow(null, hasStoredPairing: false), discovered, groups);
    }

    /// <summary>Every known receiver: the stored ones plus any discovered but not yet stored, ordered by name.</summary>
    public async Task<IReadOnlyList<ReceiverDto>> ReceiversAsync(CancellationToken cancellationToken)
    {
        var online = await OnlineAsync(cancellationToken).ConfigureAwait(false);
        var rows = await store.GetReceiversAsync(cancellationToken).ConfigureAwait(false);
        var groups = await store.GetGroupsAsync(cancellationToken).ConfigureAwait(false);
        var result = rows.Select(r => ToDto(r, online.GetValueOrDefault(r.Id), groups)).ToList();
        var stored = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        result.AddRange(online.Values.Where(d => !stored.Contains(d.Id)).Select(d => ToDto(d.ToReceiverRow(null, hasStoredPairing: false), d, groups)));
        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>One group, or null.</summary>
    public async Task<GroupDto?> GroupAsync(string groupId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        var group = await store.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        return group is null ? null : ToDto(group, await ReceiverRowsAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>All groups.</summary>
    public async Task<IReadOnlyList<GroupDto>> GroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await store.GetGroupsAsync(cancellationToken).ConfigureAwait(false);
        var receivers = await ReceiverRowsAsync(cancellationToken).ConfigureAwait(false);
        return groups.Select(g => ToDto(g, receivers)).ToList();
    }

    /// <summary>The selected output (section 7 <c>/output</c>).</summary>
    public async Task<OutputDto> OutputAsync(CancellationToken cancellationToken)
    {
        var selection = runtime.Selection;
        var receivers = await ReceiverRowsAsync(cancellationToken).ConfigureAwait(false);
        var group = selection.GroupId is null ? null : await store.GetGroupAsync(selection.GroupId, cancellationToken).ConfigureAwait(false);
        var receiver = selection.ReceiverId is null ? null : receivers.GetValueOrDefault(selection.ReceiverId);
        var online = Online();
        return Mapping.ToOutputDto(group, receiver, receivers, id => StateOf(id, online.ContainsKey(id)));
    }

    /// <summary>The service status, with <c>ActiveSource</c> and <c>Airplay</c> (SPEC-receiver.md 9.1).</summary>
    public async Task<StatusDto> StatusAsync(CancellationToken cancellationToken)
    {
        var spotify = runtime.Spotify;
        var deviceName = spotify.DeviceName;
        if (string.IsNullOrEmpty(deviceName))
        {
            deviceName = (await BridgeSettings.LoadAsync(store, options.Value, cancellationToken).ConfigureAwait(false)).DeviceName;
        }

        var output = await OutputAsync(cancellationToken).ConfigureAwait(false);
        var connected = output.Members.Any(m => m.State is ReceiverConnectionState.Connected or ReceiverConnectionState.Streaming);
        return new StatusDto(
            typeof(DtoFactory).Assembly.GetName().Version?.ToString() ?? "0",
            spotify.Connected,
            spotify.User,
            deviceName,
            connected,
            (await OnlineAsync(cancellationToken).ConfigureAwait(false)).Count,
            Program.StartedAt,
            SourceSwitch.ToApiName(Owner),
            await AirplayReceiverAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The <c>statusChanged</c> + receivers + groups + output snapshot a new WebSocket client gets first (section 7).</summary>
    public async Task<IReadOnlyList<BridgeEvent>> SnapshotAsync(CancellationToken cancellationToken)
    {
        var events = new List<BridgeEvent> { new BridgeEvent.StatusChanged(await StatusAsync(cancellationToken).ConfigureAwait(false)) };
        events.AddRange((await ReceiversAsync(cancellationToken).ConfigureAwait(false)).Select(r => new BridgeEvent.ReceiverChanged(r)));
        events.AddRange((await GroupsAsync(cancellationToken).ConfigureAwait(false)).Select(g => new BridgeEvent.GroupChanged(g)));
        events.Add(new BridgeEvent.OutputChanged(await OutputAsync(cancellationToken).ConfigureAwait(false)));
        events.Add(new BridgeEvent.PlaybackChanged(Playback()));
        return events;
    }

    private static uint KeyOf(byte[] bytes) => BitConverter.ToUInt32(SHA256.HashData(bytes), 0);

    /// <summary>
    /// The discovered receivers without our own AirPlay advertisement (SPEC-receiver.md R12), judged against the identity
    /// cached by <see cref="OnlineAsync"/> once <c>airplay_identity</c> exists.
    /// </summary>
    private Dictionary<string, DiscoveredReceiver> Online()
    {
        var self = _self;
        var map = new Dictionary<string, DiscoveredReceiver>(StringComparer.Ordinal);
        foreach (var receiver in browser.Snapshot())
        {
            if (!ReceiverInventory.IsOwnAdvertisement(receiver, self))
            {
                map[receiver.Id] = receiver;
            }
        }

        return map;
    }

    /// <summary>
    /// <see cref="Online"/> after reading the stored identity (<c>airplay_identity</c>, SPEC-receiver.md R11, R12) while it is
    /// not cached yet; it never changes once it exists.
    /// </summary>
    private async Task<Dictionary<string, DiscoveredReceiver>> OnlineAsync(CancellationToken cancellationToken)
    {
        if (_self is null)
        {
            var json = await store.GetSettingAsync(BridgeSettingKeys.AirplayIdentity, cancellationToken).ConfigureAwait(false);
            if (AirplaySettings.TryParseIdentity(json, out var parsed))
            {
                _self = parsed;
            }
        }

        return Online();
    }

    private async Task<Dictionary<string, ReceiverRow>> ReceiverRowsAsync(CancellationToken cancellationToken)
    {
        var rows = await store.GetReceiversAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    private ReceiverDto ToDto(ReceiverRow row, DiscoveredReceiver? discovered, IReadOnlyList<GroupRow> groups)
    {
        var selection = runtime.Selection;
        var memberOf = groups.Where(g => g.Members.Any(m => m.ReceiverId == row.Id)).ToList();
        var groupId = memberOf.FirstOrDefault(g => g.Id == selection.GroupId)?.Id ?? memberOf.FirstOrDefault()?.Id;
        var runtimeState = runtime.Get(row.Id);
        return row.ToDto(discovered, StateOf(row.Id, discovered is not null), groupId, runtimeState?.Error);
    }

    private GroupDto ToDto(GroupRow group, IReadOnlyDictionary<string, ReceiverRow> receivers)
    {
        var online = Online();
        return group.ToDto(receivers, runtime.Selection.GroupId == group.Id, id => StateOf(id, online.ContainsKey(id)));
    }
}
