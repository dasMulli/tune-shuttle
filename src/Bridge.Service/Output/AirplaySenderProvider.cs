using System.Collections.Concurrent;
using Airplay2.Sender;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Session;
using Bridge.Service.Store;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Output;

/// <summary>
/// Creates and owns the process's <see cref="AirplaySender"/> (shared clock, timing responder, control port, PTP master),
/// lazily because its identity comes from the store: the sender's <c>deviceID</c>/<c>macAddress</c> are derived from the
/// installation's controller identity (<see cref="AirplaySender.DeriveIdentity"/>), so they stay stable across restarts.
/// Per-receiver <c>offset_frames</c> from the store reach the sessions through <see cref="AirplaySenderOptions.Quirks"/>,
/// a live dictionary updated before each connect.
/// </summary>
public sealed class AirplaySenderProvider : IAsyncDisposable
{
    private readonly IBridgeStore _store;
    private readonly IOptions<BridgeOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ReceiverQuirks> _quirks = new(StringComparer.Ordinal);
    private AirplaySender? _sender;
    private bool _disposed;

    /// <summary>Creates the provider; nothing is bound until <see cref="GetAsync"/>.</summary>
    public AirplaySenderProvider(IBridgeStore store, IOptions<BridgeOptions> options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _store = store;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AirplaySenderProvider>();
    }

    /// <summary>The sender if it was created, else null (diagnostics read its ports without creating it).</summary>
    public AirplaySender? Current => Volatile.Read(ref _sender);

    /// <summary>The sender, created on first use with the current settings and bound (timing and control sockets).</summary>
    public async Task<AirplaySender> GetAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _sender) is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sender is { } created)
            {
                return created;
            }

            var settings = await BridgeSettings.LoadAsync(_store, _options.Value, cancellationToken).ConfigureAwait(false);
            var controller = await _store.GetControllerIdentityAsync(cancellationToken).ConfigureAwait(false);
            var options = _options.Value;
            if (options.SenderPorts.Timing != 0 || options.SenderPorts.Control != 0)
            {
                // AirplaySender binds its shared sockets itself and offers no port option yet; the install script's
                // per-program firewall rule covers the dynamic ports (install/windows/install-service.ps1).
                _logger.LogWarning("Bridge:SenderPorts is set ({Timing}/{Control}) but the sender chooses its UDP ports itself; the setting is ignored", options.SenderPorts.Timing, options.SenderPorts.Control);
            }

            var senderOptions = new AirplaySenderOptions
            {
                Identity = AirplaySender.DeriveIdentity(settings.DeviceName, controller),
                PreferredStream = settings.PreferredStream == "Buffered" ? StreamType.Buffered : StreamType.Realtime,
                LocalAddress = options.LocalIPAddress,
                Quirks = _quirks,
                Player = PlayerOptions(settings, options.PollReceiverVolumeSeconds, null),
            };
            var sender = new AirplaySender(senderOptions, _store, _loggerFactory);
            await sender.StartAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _sender, sender);
            _logger.LogInformation("AirPlay sender ready as {Name} ({DeviceId}), {Stream} stream preferred", senderOptions.Identity.Name, senderOptions.Identity.DeviceId, senderOptions.PreferredStream);
            return sender;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The group player options for the current settings: L, the volume poll (SPEC.md D10 fallback) and the group UUID
    /// sent as <c>groupUUID</c> (the store's group id, so a group keeps its identity across restarts).
    /// </summary>
    public static GroupPlayerOptions PlayerOptions(BridgeSettings settings, int pollSeconds, string? groupUuid)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new GroupPlayerOptions
        {
            LatencyFrames = settings.LatencyFrames,
            VolumePollInterval = settings.PollReceiverVolume ? TimeSpan.FromSeconds(Math.Max(1, pollSeconds)) : null,
        };
        return groupUuid is null ? options : options with { GroupUuid = groupUuid.ToUpperInvariant() };
    }

    /// <summary>
    /// Applies a receiver's stored <c>offset_frames</c> (SPEC-audio.md 3.3) to the quirks its next session gets: the
    /// model's quirks (<see cref="ReceiverQuirks.ForReceiver"/>, incl. the P1 timing protocol) with the offset replaced.
    /// </summary>
    public void ApplyOffset(DiscoveredReceiver receiver, int offsetFrames)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (offsetFrames == 0)
        {
            _quirks.TryRemove(receiver.Id, out _);
            return;
        }

        _quirks[receiver.Id] = ReceiverQuirks.ForReceiver(receiver) with { OffsetFrames = offsetFrames };
    }

    /// <summary>
    /// Disposes the current sender (its players are torn down) so the next <see cref="GetAsync"/> builds one with the new
    /// settings (device name, preferred stream). The caller has detached its own player first.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _sender, null) is { } sender)
            {
                await sender.DisposeAsync().ConfigureAwait(false);
                _logger.LogInformation("AirPlay sender disposed; the next connect builds it with the new settings");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (Interlocked.Exchange(ref _sender, null) is { } sender)
            {
                await sender.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
