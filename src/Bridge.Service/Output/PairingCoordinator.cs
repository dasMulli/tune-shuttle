using System.Collections.Concurrent;
using System.Security.Cryptography;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Store;

namespace Bridge.Service.Output;

/// <summary>
/// The web app's PIN pairing flow with tokens (WORK-PLAN.md section 7, SPEC-control.md 3.6): <see cref="StartAsync"/>
/// opens the pairing connection and makes the receiver show its PIN (M1/M2), and hands out an opaque token;
/// <see cref="FinishAsync"/> takes the token and the PIN, runs M3-M6 plus pair-verify, stores the keys and asks the
/// output manager to connect the receiver again. One pending pairing per receiver (a new start replaces the old one); the
/// sender closes abandoned pairings after <c>PinPairingTimeout</c>.
/// </summary>
public sealed class PairingCoordinator(
    AirplaySenderProvider senders,
    ReceiverInventory inventory,
    IBridgeStore store,
    EventHub hub,
    DtoFactory dtos,
    RuntimeState runtime,
    ILogger<PairingCoordinator> logger)
{
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>Pairings waiting for their PIN.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Starts a PIN pairing; the receiver shows its PIN.</summary>
    /// <exception cref="KeyNotFoundException">Unknown receiver.</exception>
    /// <exception cref="InvalidOperationException">The receiver admits only HomeKit home members (acl=1).</exception>
    public async Task<PairingStartedDto> StartAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        var receiver = await inventory.ResolveAsync(receiverId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown receiver '{receiverId}'.");
        if (receiver.Capabilities.Pairing == PairingRequirement.Disabled)
        {
            throw new InvalidOperationException($"{receiver.Name} admits only members of its HomeKit home (acl=1); the bridge cannot pair with it (SPEC-control.md 1.6).");
        }

        // The receiver keeps one SRP session per pairing connection; a second start replaces the first one.
        foreach (var (token, pending) in _pending)
        {
            if (pending.ReceiverId == receiverId || pending.Created < DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30))
            {
                _pending.TryRemove(token, out _);
            }
        }

        var sender = await senders.GetAsync(cancellationToken).ConfigureAwait(false);
        var started = await sender.BeginPinPairingAsync(receiver, cancellationToken).ConfigureAwait(false);
        var newToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        _pending[newToken] = new Pending(receiverId, started, DateTimeOffset.UtcNow);
        logger.LogInformation("PIN pairing with {Receiver} started; waiting for the PIN from the web app", receiver.Name);
        return new PairingStartedDto(newToken, $"Enter the PIN that {receiver.Name} shows.");
    }

    /// <summary>
    /// Starts a PIN pairing for a connect that needed one and announces it with <c>pairingRequired</c> so the web app opens
    /// its PIN dialog (WORK-PLAN.md section 7, <c>/connect</c>). Failures are logged, not thrown: the connect already
    /// reports the receiver's state.
    /// </summary>
    public async Task AnnounceRequiredAsync(string receiverId, CancellationToken cancellationToken)
    {
        try
        {
            var started = await StartAsync(receiverId, cancellationToken).ConfigureAwait(false);
            hub.Publish(new BridgeEvent.PairingRequired(receiverId, started.PairingToken));
        }
        catch (Exception ex) when (ex is PairingException or IOException or TimeoutException or InvalidOperationException or KeyNotFoundException or System.Net.Sockets.SocketException or Airplay2.Sender.Rtsp.RtspStatusException)
        {
            logger.LogWarning("Could not start the PIN pairing with {Receiver}: {Error}", receiverId, ex.Message);
        }
    }

    /// <summary>Completes a PIN pairing and stores the keys; the receiver is connected again if it is part of the output.</summary>
    /// <exception cref="InvalidOperationException">Unknown or expired token, or a token of another receiver.</exception>
    /// <exception cref="ArgumentException">Empty PIN.</exception>
    /// <exception cref="PairingException">Wrong PIN (<see cref="PairingFailure.Authentication"/>) or another pairing failure.</exception>
    public async Task<ReceiverDto> FinishAsync(string receiverId, PairingPinRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Pin))
        {
            throw new ArgumentException("The PIN is empty.", nameof(request));
        }

        if (string.IsNullOrEmpty(request.PairingToken) || !_pending.TryGetValue(request.PairingToken, out var pending))
        {
            throw new InvalidOperationException("This pairing is unknown or expired; start the pairing again.");
        }

        if (pending.ReceiverId != receiverId)
        {
            throw new InvalidOperationException("The pairing token belongs to another receiver.");
        }

        _pending.TryRemove(request.PairingToken, out _);
        var sender = await senders.GetAsync(cancellationToken).ConfigureAwait(false);
        await sender.CompletePinPairingAsync(pending.Pairing, request.Pin.Trim(), cancellationToken).ConfigureAwait(false);
        await SetPairingStateAsync(receiverId, PairingState.Paired, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("PIN pairing with {Receiver} stored", receiverId);
        runtime.Clear(receiverId);
        runtime.RequestReconnect(receiverId);
        await inventory.PublishAsync(receiverId, cancellationToken).ConfigureAwait(false);
        return await dtos.ReceiverAsync(receiverId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown receiver '{receiverId}'.");
    }

    /// <summary>Forgets the stored pairing (<c>DELETE /receivers/{id}/pairing</c>); the state falls back to what the receiver requires.</summary>
    /// <exception cref="KeyNotFoundException">Unknown receiver.</exception>
    public async Task ForgetAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        var row = await inventory.RequireRowAsync(receiverId, cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(receiverId, cancellationToken).ConfigureAwait(false);
        var requirement = dtos.Discovered(receiverId)?.Capabilities.Pairing ?? Mapping.CapabilitiesOf(row).Pairing;
        var state = row.PairingState == PairingStates.Disabled ? PairingState.Disabled : Mapping.PairingStateFor(requirement, hasStoredPairing: false);
        await SetPairingStateAsync(receiverId, state, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Stored pairing of {Receiver} forgotten", row.Name);
        await inventory.PublishAsync(receiverId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a pairing state in the receiver's row (created from discovery when missing).</summary>
    public async Task SetPairingStateAsync(string receiverId, PairingState state, CancellationToken cancellationToken)
    {
        var row = await inventory.RequireRowAsync(receiverId, cancellationToken).ConfigureAwait(false);
        var value = state.ToStoreValue();
        if (row.PairingState != value)
        {
            await store.UpsertReceiverAsync(row with { PairingState = value }, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record Pending(string ReceiverId, PinPairingInProgress Pairing, DateTimeOffset Created);
}
