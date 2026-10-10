using System.Globalization;
using System.Net;
using Airplay2.Sender.Discovery;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Store;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Output;

/// <summary>
/// The receiver inventory (docs/ARCHITECTURE.md, "Receiver inventory and pruning"): runs the mDNS
/// browser, writes every discovered receiver into the store (<see cref="Mapping.ToReceiverRow"/> keeps the user's level,
/// offset and quirks), publishes <c>receiverChanged</c> on every change, asks the output manager to retry a receiver that
/// (re)appears, and resolves a receiver id to a <see cref="DiscoveredReceiver"/> for connecting, with the stored last
/// address as a fallback when mDNS is slow or blocked.
/// </summary>
public sealed class ReceiverInventory(
    IReceiverBrowser browser,
    IBridgeStore store,
    DtoFactory dtos,
    EventHub hub,
    RuntimeState runtime,
    IOptions<BridgeOptions> options,
    ILogger<ReceiverInventory> logger) : BackgroundService
{
    /// <summary>Delay before the browser is started again after its sockets could not be bound.</summary>
    public static readonly TimeSpan BrowserRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>The browser.</summary>
    public IReceiverBrowser Browser { get; } = browser;

    /// <summary>
    /// Resolves a receiver for connecting: the browser's cache, else a short <see cref="IReceiverBrowser.FindAsync"/>, else
    /// the stored row's last address and TXT data (<see cref="FromRow"/>). Null when the id is unknown everywhere.
    /// </summary>
    public async Task<DiscoveredReceiver?> ResolveAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        var cached = Browser.Snapshot().FirstOrDefault(r => r.Id == receiverId);
        if (cached is not null)
        {
            return cached;
        }

        if (options.Value.BrowseReceivers)
        {
            try
            {
                var found = await Browser.FindAsync(receiverId, options.Value.ResolveTimeout, cancellationToken).ConfigureAwait(false);
                if (found is not null && found.Id == receiverId)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                logger.LogDebug("mDNS lookup of {Receiver} failed: {Error}", receiverId, ex.Message);
            }
        }

        var row = await store.GetReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
        var fallback = row is null ? null : FromRow(row);
        if (fallback is not null)
        {
            logger.LogInformation("{Receiver} is not in mDNS right now; trying its last address {Address}:{Port}", row!.Name, row.LastAddress, row.LastPort);
        }

        return fallback;
    }

    /// <summary>
    /// Writes a discovered receiver into the store (insert or merge) and returns the row. The pairing state follows the
    /// receiver's requirement and whether a pairing is stored (SPEC-control.md 1.6, 3.8).
    /// </summary>
    public async Task<ReceiverRow> RememberAsync(DiscoveredReceiver receiver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        var existing = await store.GetReceiverAsync(receiver.Id, cancellationToken).ConfigureAwait(false);
        var hasPairing = await store.GetAsync(receiver.Id, cancellationToken).ConfigureAwait(false) is not null;
        var row = receiver.ToReceiverRow(existing, hasPairing);
        if (existing is not null && existing.PublicKey is { } oldKey && row.PublicKey is { } newKey && !oldKey.AsSpan().SequenceEqual(newKey))
        {
            // SPEC-control.md 3.8: a new pk means the receiver was reset; the stored pairing will fail pair-verify and be dropped.
            logger.LogWarning("{Receiver} announces a new public key (reset?); its stored pairing is expected to fail", receiver.Name);
        }

        await store.UpsertReceiverAsync(row, cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Makes sure a receiver id the API was given exists in the store: an already stored row, else the discovered receiver
    /// remembered now. Throws <see cref="KeyNotFoundException"/> when the id is unknown.
    /// </summary>
    public async Task<ReceiverRow> RequireRowAsync(string receiverId, CancellationToken cancellationToken)
    {
        var row = await store.GetReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        var discovered = Browser.Snapshot().FirstOrDefault(r => r.Id == receiverId)
            ?? throw new KeyNotFoundException($"Unknown receiver '{receiverId}'.");
        return await RememberAsync(discovered, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes the current view of a receiver (<c>receiverChanged</c>).</summary>
    public async Task PublishAsync(string receiverId, CancellationToken cancellationToken)
    {
        var dto = await dtos.ReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false);
        if (dto is not null)
        {
            hub.Publish(new BridgeEvent.ReceiverChanged(dto));
        }
    }

    /// <summary>
    /// A <see cref="DiscoveredReceiver"/> rebuilt from a stored row (last address and port, feature and flag words,
    /// <c>pk</c>, model), so a receiver whose mDNS records are missing can still be connected to. Null without an address.
    /// </summary>
    public static DiscoveredReceiver? FromRow(ReceiverRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(row.LastAddress) || !IPAddress.TryParse(row.LastAddress, out var address))
        {
            return null;
        }

        var txt = new List<string>
        {
            "deviceid=" + row.Id,
            "features=" + AirplayTxtValues.FormatFeatures(Mapping.FeaturesOf(row)),
            "flags=0x" + ((uint)Mapping.FlagsOf(row)).ToString("X", CultureInfo.InvariantCulture),
        };
        if (row.Model is { } model)
        {
            txt.Add("model=" + model);
        }

        if (row.Manufacturer is { } manufacturer)
        {
            txt.Add("manufacturer=" + manufacturer);
        }

        if (row.PublicKey is { } pk)
        {
            txt.Add("pk=" + Convert.ToHexStringLower(pk));
        }

        if (row.PairingState == PairingStates.Disabled)
        {
            txt.Add("acl=1");
        }

        var lastSeen = DateTimeOffset.TryParse(row.LastSeen, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen) ? seen : DateTimeOffset.MinValue;
        return new DiscoveredReceiver(row.Id, row.Name, [address], row.LastPort, AirplayTxtRecord.Parse(txt), null, lastSeen);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BrowseReceivers)
        {
            logger.LogInformation("mDNS browsing is off (Bridge:BrowseReceivers=false); receivers come from the configured browser only");
        }

        while (options.Value.BrowseReceivers && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Browser.StartAsync(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                logger.LogWarning("mDNS browser could not start: {Error}; retrying in {Delay}", ex.Message, BrowserRetryDelay);
                await Task.Delay(BrowserRetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        foreach (var receiver in Browser.Snapshot())
        {
            await OnEventAsync(new ReceiverEvent.Found(receiver), stoppingToken).ConfigureAwait(false);
        }

        await foreach (var receiverEvent in Browser.Events.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await OnEventAsync(receiverEvent, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or ArgumentException or FormatException)
            {
                logger.LogWarning("Could not record {Receiver}: {Error}", receiverEvent.Receiver.Name, ex.Message);
            }
        }
    }

    private async Task OnEventAsync(ReceiverEvent receiverEvent, CancellationToken cancellationToken)
    {
        var receiver = receiverEvent.Receiver;
        switch (receiverEvent)
        {
            case ReceiverEvent.Found:
                logger.LogInformation("Receiver found: {Receiver} ({Id}, {Model}) at {Addresses}", receiver.Name, receiver.Id, receiver.Airplay.Model ?? "?", string.Join(", ", receiver.Addresses));
                await RememberAsync(receiver, cancellationToken).ConfigureAwait(false);
                runtime.RequestReconnect(receiver.Id);
                break;
            case ReceiverEvent.Updated:
                logger.LogDebug("Receiver updated: {Receiver}", receiver.Name);
                await RememberAsync(receiver, cancellationToken).ConfigureAwait(false);
                break;
            case ReceiverEvent.Lost:
                logger.LogInformation("Receiver gone from mDNS: {Receiver}", receiver.Name);
                // The row's last_seen is written on Found/Updated only, and the cache raises no Updated for a LastSeen-only
                // change (a re-announcement with the same records). Store the arrival time of the receiver's last record:
                // the last time it was actually heard, not the time of this Lost event (up to TTL plus grace later).
                if (receiver.LastSeen != DateTimeOffset.MinValue)
                {
                    await store.TouchReceiverAsync(receiver.Id, receiver.LastSeen, cancellationToken).ConfigureAwait(false);
                }

                break;
        }

        await PublishAsync(receiver.Id, cancellationToken).ConfigureAwait(false);
    }
}
