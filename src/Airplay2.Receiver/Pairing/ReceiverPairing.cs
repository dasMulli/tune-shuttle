using System.Collections.Concurrent;
using Airplay2.Receiver.Rtsp;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Pairing;

/// <summary>How the receiver answers the pairing POSTs (SPEC-receiver.md R7, 4.2).</summary>
public sealed record ReceiverPairingOptions
{
    /// <summary>
    /// Require <c>X-Apple-HKP</c> on pairing POSTs (SPEC-control.md open question C-7.11); a POST without it is answered
    /// with 470. Off in the product receiver (shairport-sync ignores the header); a test switch of the fake receiver.
    /// </summary>
    public bool RequireHkpHeader { get; init; }

    /// <summary>
    /// Switch the control channel to encryption after pair-verify M4 (Apple receivers per pyatv and owntone; the product
    /// behaviour). False emulates shairport-sync 5.5.2, whose cipher set-up after pair-verify is commented out
    /// [sps:rtsp.c:1725-1741] (SPEC-control.md 3.7 note); transient M4 always switches.
    /// </summary>
    public bool EncryptAfterPairVerify { get; init; } = true;

    /// <summary>Label used in log messages.</summary>
    public string Name { get; init; } = "receiver";
}

/// <summary>What a pairing POST produced: the answer to write and, when a procedure completed, its session.</summary>
/// <param name="Response">The answer (status and TLV8 body; switches the channel to encryption after it when the procedure completed).</param>
/// <param name="Completed">The pairing session when this request completed transient pair-setup or pair-verify, else null.</param>
public sealed record PairingOutcome(RtspServerResponse Response, PairingSession? Completed);

/// <summary>
/// The receiver-wide pairing state (SPEC-receiver.md R7, 4.2): one <c>HapAccessoryPairing</c> (the accessory side of
/// SPEC-control.md 3, in <c>Airplay2.Sender/Pairing</c>) whose controller registry is seeded from an
/// <see cref="IControllerStore"/> by <see cref="LoadAsync"/> and writes every change through to it. Each control
/// connection gets its own procedure state from <see cref="ForConnection"/>. Promoted from the fake receiver's pairing
/// handlers (SPEC-receiver.md R2).
/// </summary>
public sealed class ReceiverPairing
{
    private readonly IControllerStore _store;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<Task> _pendingWrites = new();
    private int _seeding;

    /// <summary>Creates the pairing state; the registry is empty until <see cref="LoadAsync"/>.</summary>
    /// <param name="accessory">The accessory identity, PIN and transient-pairing switch (R7).</param>
    /// <param name="store">Where registered controllers are persisted.</param>
    /// <param name="options">Answer switches.</param>
    /// <param name="logger">Logger.</param>
    public ReceiverPairing(HapAccessoryOptions accessory, IControllerStore store, ReceiverPairingOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(accessory);
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        Options = options ?? new ReceiverPairingOptions();
        _logger = logger ?? NullLogger.Instance;
        Controllers = new HapControllerRegistry(OnControllerChanged);
        Accessory = new HapAccessoryPairing(accessory, Controllers);
    }

    /// <summary>The answer switches.</summary>
    public ReceiverPairingOptions Options { get; }

    /// <summary>The number of registered controllers.</summary>
    public int ControllerCount => Controllers.Count;

    /// <summary>The accessory's pairing identifier (sent in M6 and pair-verify M2).</summary>
    public string AccessoryId => Accessory.AccessoryId;

    /// <summary>The accessory's Ed25519 long-term public key (<c>pk</c>).</summary>
    public byte[] AccessoryPublicKey => Accessory.AccessoryPublicKey;

    /// <summary>The logger.</summary>
    internal ILogger Logger => _logger;

    /// <summary>The registry of paired controllers, shared by every connection.</summary>
    internal HapControllerRegistry Controllers { get; }

    /// <summary>The receiver-wide accessory; per-connection views come from <see cref="ForConnection"/>.</summary>
    internal HapAccessoryPairing Accessory { get; }

    /// <summary>The keys the receiver uses on the event channel of a paired connection (the sender's event keys swapped, SPEC-control.md 4.3).</summary>
    public static HapChannelKeys EventKeysFor(PairingSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return HapAccessoryPairing.EventKeysFor(session);
    }

    /// <summary>Seeds the registry from the store (SPEC-receiver.md 4.2: at receiver start); the seeding is not written back.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var stored = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _seeding, 1);
        try
        {
            foreach (var (id, key) in stored)
            {
                Controllers.Set(id, key);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _seeding, 0);
        }

        _logger.LogDebug("Receiver {Name}: {Count} paired controllers loaded", Options.Name, stored.Count);
    }

    /// <summary>
    /// Forgets a registered controller (also in the store, written in the background; a store failure is logged), so its
    /// next pair-verify fails; false when it was not registered (SPEC-receiver.md 4.2).
    /// </summary>
    public bool RemoveController(string controllerId) => Controllers.Remove(controllerId);

    /// <summary>
    /// Forgets a registered controller like <see cref="RemoveController"/> and returns once the store writes started so far
    /// have finished (a store failure is logged, never thrown; SPEC-receiver.md 4.2); false when it was not registered.
    /// </summary>
    public async Task<bool> RemoveControllerAsync(string controllerId)
    {
        var removed = Controllers.Remove(controllerId);
        await FlushWritesAsync().ConfigureAwait(false);
        return removed;
    }

    /// <summary>A fresh per-connection procedure state sharing this receiver's identity and registry.</summary>
    public ConnectionPairing ForConnection() => new(this, Accessory.ForConnection());

    /// <summary>
    /// Awaits the store writes started by registry changes so far (SPEC-receiver.md 4.2). A failure was already logged by
    /// the write's own continuation (once, whoever flushes), so nothing is thrown here.
    /// </summary>
    internal async Task FlushWritesAsync()
    {
        while (_pendingWrites.TryDequeue(out var logged))
        {
            try
            {
                await logged.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Only reachable when the logger itself threw; the pairing still answers.
            }
        }
    }

    private void OnControllerChanged(string controllerId, byte[]? publicKey)
    {
        if (Volatile.Read(ref _seeding) != 0)
        {
            return;
        }

        Task write;
        try
        {
            write = publicKey is null
                ? _store.RemoveAsync(controllerId, CancellationToken.None)
                : _store.SaveAsync(controllerId, publicKey, CancellationToken.None);
        }
        catch (Exception ex)
        {
            write = Task.FromException(ex);
        }

        if (write.IsCompletedSuccessfully)
        {
            return;
        }

        // A failure is logged when the write finishes, whoever (if anyone) flushes; the handler of the request that caused
        // it, or RemoveControllerAsync, awaits it through the queue.
        var logged = write.ContinueWith(
            static (t, state) => ((ReceiverPairing)state!).LogStoreFailure(t),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _pendingWrites.Enqueue(logged);
    }

    private void LogStoreFailure(Task write)
    {
        if (write.IsFaulted)
        {
            _logger.LogWarning(write.Exception.GetBaseException(), "Receiver {Name}: storing a paired controller failed; the pairing still answers", Options.Name);
        }
        else if (write.IsCanceled)
        {
            _logger.LogWarning("Receiver {Name}: storing a paired controller was cancelled; the pairing still answers", Options.Name);
        }
    }
}

/// <summary>
/// The pairing state of one control connection (SPEC-receiver.md 4.1-4.2): a thin adapter that answers
/// <c>/pair-pin-start</c>, <c>/pair-setup</c> and <c>/pair-verify</c> through the accessory, whose status and TLV8 body
/// go back as is; a completed procedure switches the channel to encryption after this (plaintext) response
/// [sps:rtsp.c:1810-1833].
/// </summary>
public sealed class ConnectionPairing
{
    private readonly ReceiverPairing _owner;
    private readonly HapAccessoryPairing _accessory;
    private PairingSession? _session;

    internal ConnectionPairing(ReceiverPairing owner, HapAccessoryPairing accessory)
    {
        _owner = owner;
        _accessory = accessory;
    }

    /// <summary>The session of the last completed procedure on this connection, or null while unpaired.</summary>
    public PairingSession? Session => Volatile.Read(ref _session);

    /// <summary>True for the URIs this adapter answers (<c>/pair-pin-start</c>, <c>/pair-setup</c>, <c>/pair-verify</c>).</summary>
    public static bool Handles(string uri) => uri is HapPairingEndpoints.PinStart or HapPairingEndpoints.PairSetup or HapPairingEndpoints.PairVerify;

    /// <summary>
    /// Answers one pairing POST (SPEC-receiver.md 4.2). Store writes caused by the request (M5, a re-pair) are awaited
    /// before the answer; their failure is logged and the answer is sent anyway.
    /// </summary>
    /// <param name="connection">The connection the request arrived on (its encryption state decides the key switch).</param>
    /// <param name="request">The request.</param>
    public async ValueTask<PairingOutcome> HandleAsync(RtspServerConnection connection, RtspIncomingRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        var logger = _owner.Logger;
        var options = _owner.Options;
        if (options.RequireHkpHeader && request.Header(RtspHeaders.XAppleHkp) is null)
        {
            // Open question C-7.11: owntone marks the header "Required!!" [owntone:airplay.c:2886-2890]; shairport ignores it.
            logger.LogWarning("Receiver {Name}: POST {Uri} without X-Apple-HKP; answering 470", options.Name, request.Uri);
            return new PairingOutcome(RtspServerResponse.Status(RtspStatus.ConnectionAuthorizationRequired), null);
        }

        var result = _accessory.HandleRequest(request.Uri, request.Body.Span);
        await _owner.FlushWritesAsync().ConfigureAwait(false);
        var response = new RtspServerResponse(result.Status)
        {
            ContentType = result.Body.Length > 0 ? RtspContentTypes.OctetStream : null,
            Body = result.Body,
        };
        if (result.Session is not { } session)
        {
            return new PairingOutcome(response, null);
        }

        Volatile.Write(ref _session, session);
        if (connection.IsEncrypted)
        {
            logger.LogWarning("Receiver {Name}: pairing completed inside an encrypted channel; keeping the current keys", options.Name);
            return new PairingOutcome(response, session);
        }

        if (session.Kind == PairingKind.Verified && !options.EncryptAfterPairVerify)
        {
            // shairport-sync 5.5.2 behaviour (SPEC-control.md 3.7 note).
            return new PairingOutcome(response, session);
        }

        return new PairingOutcome(response with { EnableEncryptionAfter = HapAccessoryPairing.ControlKeysFor(session) }, session);
    }
}
