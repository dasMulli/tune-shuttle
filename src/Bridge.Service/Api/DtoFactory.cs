using Airplay2.Sender.Discovery;
using Bridge.Contracts;
using Bridge.Service.Output;
using Bridge.Service.Store;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Api;

/// <summary>
/// Builds the API views from the three sources of truth: the store (receivers, groups, levels), discovery (online,
/// address, capabilities) and the runtime state (connection states, the selected output, Spotify). Pure reads.
/// </summary>
public sealed class DtoFactory(IBridgeStore store, IReceiverBrowser browser, RuntimeState runtime, IOptions<BridgeOptions> options)
{
    /// <summary>The receiver as discovery currently sees it, or null.</summary>
    public DiscoveredReceiver? Discovered(string receiverId) => Online().GetValueOrDefault(receiverId);

    /// <summary>The connection state the API shows: the output manager's, else Available/Offline by discovery.</summary>
    public ReceiverConnectionState StateOf(string receiverId, bool online) =>
        runtime.Get(receiverId)?.State ?? (online ? ReceiverConnectionState.Available : ReceiverConnectionState.Offline);

    /// <summary>One receiver (stored or only discovered), or null.</summary>
    public async Task<ReceiverDto?> ReceiverAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        var online = Online();
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
        var online = Online();
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

    /// <summary>The service status.</summary>
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
            browser.Snapshot().Count,
            Program.StartedAt);
    }

    /// <summary>The <c>statusChanged</c> + receivers + groups + output snapshot a new WebSocket client gets first (section 7).</summary>
    public async Task<IReadOnlyList<BridgeEvent>> SnapshotAsync(CancellationToken cancellationToken)
    {
        var events = new List<BridgeEvent> { new BridgeEvent.StatusChanged(await StatusAsync(cancellationToken).ConfigureAwait(false)) };
        events.AddRange((await ReceiversAsync(cancellationToken).ConfigureAwait(false)).Select(r => new BridgeEvent.ReceiverChanged(r)));
        events.AddRange((await GroupsAsync(cancellationToken).ConfigureAwait(false)).Select(g => new BridgeEvent.GroupChanged(g)));
        events.Add(new BridgeEvent.OutputChanged(await OutputAsync(cancellationToken).ConfigureAwait(false)));
        events.Add(new BridgeEvent.PlaybackChanged(runtime.Playback));
        return events;
    }

    private Dictionary<string, DiscoveredReceiver> Online()
    {
        var map = new Dictionary<string, DiscoveredReceiver>(StringComparer.Ordinal);
        foreach (var receiver in browser.Snapshot())
        {
            map[receiver.Id] = receiver;
        }

        return map;
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
