using Airplay2.Sender.Discovery;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Store;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Output;

/// <summary>
/// Forgets receivers: one on request (<c>DELETE /receivers/{id}</c>), every offline one (<c>DELETE
/// /receivers?offline=true</c>), and the offline ones without user state that have not been seen for
/// <see cref="ReceiverRetention"/> (<see cref="PruneAsync"/>, run hourly by <see cref="ReceiverPruner"/>).
/// </summary>
/// <remarks>
/// <para>Only offline receivers are forgotten: not in the browser's snapshot and not connecting, connected or streaming.
/// An online receiver would be back from mDNS within seconds, so forgetting it is refused (409).</para>
/// <para>Forgetting deletes the row with its pairing and group memberships (store cascade) and the selected-output
/// setting, takes the receiver out of the output (<see cref="OutputManager.ReceiverForgettingAsync"/>) and the runtime
/// state, and publishes <c>receiverRemoved</c>, <c>groupChanged</c> for every group that lost it (an emptied group is
/// kept) and <c>outputChanged</c> when the output changed. Nothing else caches an offline receiver: the browser's
/// snapshot only holds receivers that are on the network.</para>
/// </remarks>
public sealed class ReceiverJanitor(
    IBridgeStore store,
    IReceiverBrowser browser,
    OutputManager output,
    RuntimeState runtime,
    DtoFactory dtos,
    EventHub hub,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    ILogger<ReceiverJanitor> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Why a receiver is kept by <see cref="PruneAsync"/>, or null when it may be pruned at <paramref name="now"/> after
    /// <paramref name="retention"/>. The reasons are for the debug log and the tests.
    /// </summary>
    public static string? KeepReason(ReceiverRow row, bool offline, bool hasStoredPairing, bool inGroup, bool selected, DateTimeOffset now, TimeSpan retention)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (retention <= TimeSpan.Zero)
        {
            return "pruning is off";
        }

        if (!offline)
        {
            return "online";
        }

        if (Mapping.ParseTimestamp(row.LastSeen) is not { } lastSeen)
        {
            return "never seen";
        }

        if (now - lastSeen <= retention)
        {
            return "seen recently";
        }

        if (hasStoredPairing || row.PairingState == PairingStates.Paired)
        {
            return "paired";
        }

        if (inGroup)
        {
            return "in a group";
        }

        if (selected)
        {
            return "the selected output";
        }

        if (row.Level != SqliteBridgeStore.DefaultLevel || row.OffsetFrames != 0 || row.Quirks is not null)
        {
            return "has its own level, offset or quirks";
        }

        return null;
    }

    /// <summary>True when the receiver is neither on the network nor in a session (connecting, connected, streaming).</summary>
    public bool IsOffline(string receiverId) =>
        !browser.Snapshot().Any(r => r.Id == receiverId)
        && runtime.Get(receiverId)?.State is not (ReceiverConnectionState.Connecting or ReceiverConnectionState.Connected or ReceiverConnectionState.Streaming);

    /// <summary>
    /// Forgets one receiver. Throws <see cref="KeyNotFoundException"/> for an unknown id and
    /// <see cref="InvalidOperationException"/> (409) when it is online.
    /// </summary>
    public async Task ForgetAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var row = await store.GetReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                throw browser.Snapshot().Any(r => r.Id == receiverId)
                    ? new InvalidOperationException($"'{receiverId}' is online; only offline receivers can be forgotten (an online one is back from mDNS within seconds).")
                    : new KeyNotFoundException($"Unknown receiver '{receiverId}'.");
            }

            if (!IsOffline(receiverId))
            {
                throw new InvalidOperationException($"{row.Name} is online; only offline receivers can be forgotten (an online one is back from mDNS within seconds).");
            }

            await ForgetCoreAsync(row, "on request", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets every offline receiver; returns the ids removed.</summary>
    public async Task<IReadOnlyList<string>> ForgetOfflineAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = new List<string>();
            foreach (var row in await store.GetReceiversAsync(cancellationToken).ConfigureAwait(false))
            {
                if (IsOffline(row.Id))
                {
                    await ForgetCoreAsync(row, "on request (all offline receivers)", cancellationToken).ConfigureAwait(false);
                    removed.Add(row.Id);
                }
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The pruning run: moves the last-seen time of every online receiver forward (discovery writes it only when a record
    /// changes), then forgets the offline receivers without user state that were last seen longer than
    /// <see cref="ReceiverRetention"/> ago. Returns the ids removed.
    /// </summary>
    public async Task<IReadOnlyList<string>> PruneAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            foreach (var receiver in browser.Snapshot())
            {
                var seen = receiver.LastSeen > now || receiver.LastSeen == DateTimeOffset.MinValue ? now : receiver.LastSeen;
                await store.TouchReceiverAsync(receiver.Id, seen, cancellationToken).ConfigureAwait(false);
            }

            var retention = await ReceiverRetention.LoadAsync(store, options.Value, cancellationToken).ConfigureAwait(false);
            if (retention <= TimeSpan.Zero)
            {
                return [];
            }

            var rows = await store.GetReceiversAsync(cancellationToken).ConfigureAwait(false);
            var members = (await store.GetGroupsAsync(cancellationToken).ConfigureAwait(false))
                .SelectMany(g => g.Members).Select(m => m.ReceiverId).ToHashSet(StringComparer.Ordinal);
            var selectedSetting = await store.GetSettingAsync(SettingKeys.SelectedOutputReceiver, cancellationToken).ConfigureAwait(false);
            var removed = new List<string>();
            foreach (var row in rows)
            {
                var selected = runtime.Selection.ReceiverId == row.Id || selectedSetting == row.Id;
                var hasPairing = await store.GetAsync(row.Id, cancellationToken).ConfigureAwait(false) is not null;
                var reason = KeepReason(row, IsOffline(row.Id), hasPairing, members.Contains(row.Id), selected, now, retention);
                if (reason is not null)
                {
                    logger.LogDebug("Keeping {Receiver} ({Id}): {Reason}", row.Name, row.Id, reason);
                    continue;
                }

                await ForgetCoreAsync(row, $"offline since {row.LastSeen} (longer than {retention}) and without pairing, group or own settings", cancellationToken).ConfigureAwait(false);
                removed.Add(row.Id);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ForgetCoreAsync(ReceiverRow row, string why, CancellationToken cancellationToken)
    {
        var groups = (await store.GetGroupsAsync(cancellationToken).ConfigureAwait(false))
            .Where(g => g.Members.Any(m => m.ReceiverId == row.Id))
            .Select(g => g.Id)
            .ToList();
        var outputChanged = await output.ReceiverForgettingAsync(row.Id, cancellationToken).ConfigureAwait(false);
        await store.DeleteReceiverAsync(row.Id, cancellationToken).ConfigureAwait(false);
        runtime.Clear(row.Id);
        logger.LogInformation("Forgot receiver {Receiver} ({Id}): {Why}", row.Name, row.Id, why);

        hub.Publish(new BridgeEvent.ReceiverRemoved(row.Id));
        foreach (var groupId in groups)
        {
            if (await dtos.GroupAsync(groupId, cancellationToken).ConfigureAwait(false) is { } group)
            {
                hub.Publish(new BridgeEvent.GroupChanged(group));
            }
        }

        if (outputChanged || groups.Contains(runtime.Selection.GroupId ?? string.Empty, StringComparer.Ordinal))
        {
            hub.Publish(new BridgeEvent.OutputChanged(await dtos.OutputAsync(cancellationToken).ConfigureAwait(false)));
        }
    }
}
