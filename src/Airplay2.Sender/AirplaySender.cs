using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender;

/// <summary>Top-level options.</summary>
public sealed record AirplaySenderOptions
{
    /// <summary>
    /// Who we say we are in the session SETUP. The default is a placeholder; hosts derive a stable identity per
    /// installation with <see cref="AirplaySender.DeriveIdentity"/>.
    /// </summary>
    public SenderIdentity Identity { get; init; } = new("airplay2-streamer", "AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF");

    /// <summary>Browser options.</summary>
    public ReceiverBrowserOptions Browser { get; init; } = new();

    /// <summary>Default group player options (used when <see cref="AirplaySender.CreateGroupPlayerAsync(GroupPlayerOptions?, CancellationToken)"/> gets none).</summary>
    public GroupPlayerOptions Player { get; init; } = new();

    /// <summary>Preferred stream type; buffered falls back to realtime when a receiver lacks bit 40.</summary>
    public StreamType PreferredStream { get; init; } = StreamType.Realtime;

    /// <summary>
    /// Per-receiver quirks by receiver id (the <c>deviceid</c>; colons and case are ignored). They replace the model
    /// defaults of <see cref="ReceiverQuirks.ForReceiver"/>, except that a null <see cref="ReceiverQuirks.TimingProtocol"/>
    /// keeps the model's timing protocol (probe P1).
    /// </summary>
    public IReadOnlyDictionary<string, ReceiverQuirks> Quirks { get; init; } = new Dictionary<string, ReceiverQuirks>();

    /// <summary>
    /// The timing protocol for receivers whose quirks do not name one (SPEC.md D1: NTP; the SpeakerA is NTP and
    /// shairport-sync PTP by <see cref="ReceiverQuirks.ForReceiver"/>, probe P1).
    /// </summary>
    public TimingProtocol DefaultTimingProtocol { get; init; } = TimingProtocol.Ntp;

    /// <summary>
    /// Control-connection options: 25 s connect, and 30 s per request instead of D6's 10 s, because the Test Speaker ABCD
    /// on Wi-Fi answered pair-setup and RECORD after up to ~10 s (SPEC-control.md 2.1 note, SPEC.md section 4 "Timeouts").
    /// </summary>
    public RtspConnectionOptions Connection { get; init; } = new() { RequestTimeout = TimeSpan.FromSeconds(30) };

    /// <summary>Receiver session options (event channel, PIN pairing method byte).</summary>
    public ReceiverSessionOptions Session { get; init; } = new();

    /// <summary>Options of the PTP grandmaster, which is started on first use by a PTP-timed group.</summary>
    public PtpMasterOptions Ptp { get; init; } = new();

    /// <summary>Local address for the timing responder and the control port; null binds the wildcard.</summary>
    public IPAddress? LocalAddress { get; init; }

    /// <summary>
    /// Attempts of <see cref="AirplaySender.ConnectAsync"/> (connect, GET /info, pairing) on a transport failure or a
    /// retryable pairing error (TLV <c>Busy</c>/<c>BackOff</c>). Each attempt tries every address of the receiver.
    /// </summary>
    public int ConnectAttempts { get; init; } = 2;

    /// <summary>Delay between two attempts of <see cref="AirplaySender.ConnectAsync"/>; a receiver's <c>RetryDelay</c> wins when larger (capped at <see cref="MaxRetryDelay"/>).</summary>
    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for a receiver-requested back-off before the next attempt.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a PIN pairing started by <see cref="AirplaySender.BeginPinPairingAsync"/> waits for
    /// <see cref="AirplaySender.CompletePinPairingAsync"/> before its connection is closed (the receiver keeps its SRP
    /// session only while the connection is open, SPEC-control.md 3.6).
    /// </summary>
    public TimeSpan PinPairingTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The library facade used by the CLI and the bridge: discovery, sessions and group players share one master clock,
/// one timing responder and one control port per process (SPEC-audio.md 3.1), plus one PTP grandmaster started on first
/// use (SPEC-audio.md 4.4).
/// </summary>
/// <remarks>
/// <para>
/// Sessions: <see cref="ConnectAsync"/> opens the control connection, runs <c>GET /info</c> and the pairing the receiver
/// requires (SPEC.md D2, D5) with the receiver's quirks (<see cref="QuirksFor"/>); <see cref="GroupPlayer"/>s created
/// here use it as their session factory, so every member gets the same treatment.
/// </para>
/// <para>
/// Transport choice (<see cref="CreateGroupPlayerAsync(IReadOnlyCollection{DiscoveredReceiver}, GroupPlayerOptions?, CancellationToken)"/>):
/// the realtime stream (type 96, SPEC.md D1) unless <see cref="AirplaySenderOptions.PreferredStream"/> is buffered and
/// every intended member advertises bit 40; the timing protocol is the members' (P1 result per model through
/// <see cref="ReceiverQuirks.TimingProtocol"/>, else <see cref="AirplaySenderOptions.DefaultTimingProtocol"/>), and PTP
/// as soon as one member needs it, because a group has one timeline and one clock (SPEC.md D3).
/// </para>
/// <para>
/// Ownership: the sender owns the shared clock, timing responder, control port and PTP master, and disposes them (and
/// any player it created that is still alive) in <see cref="DisposeAsync"/>; a player owns its transport and sessions.
/// </para>
/// </remarks>
public sealed class AirplaySender : IAsyncDisposable
{
    /// <summary>The placeholder of <see cref="GroupPlayerOptions.Sender"/>, replaced by <see cref="AirplaySenderOptions.Identity"/>.</summary>
    private static readonly SenderIdentity PlaceholderSender = new GroupPlayerOptions().Sender;

    private readonly ConcurrentDictionary<PinPairingInProgress, PendingPinPairing> _pinPairings = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<GroupPlayer, object> _players = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ILogger<AirplaySender> _logger;
    private readonly TimingResponder _timing;
    private readonly ControlPort _control;
    private readonly Lazy<MdnsReceiverBrowser> _browser;
    private PtpMaster? _ptp;
    private bool _socketsStarted;
    private int _disposed;

    /// <summary>
    /// Creates the sender. No socket is bound until it is needed (<see cref="StartAsync"/>, a group player, the browser's own
    /// start), and the browser is created on first use of <see cref="Browser"/>.
    /// </summary>
    public AirplaySender(AirplaySenderOptions options, IPairingStore pairingStore, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pairingStore);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ConnectAttempts, 1, nameof(options));
        Options = options;
        PairingStore = pairingStore;
        LoggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AirplaySender>();
        Clock = new MasterClock();
        _timing = new TimingResponder(Clock, loggerFactory.CreateLogger<TimingResponder>(), options.LocalAddress);
        _control = new ControlPort(loggerFactory.CreateLogger<ControlPort>(), options.LocalAddress);
        _browser = new Lazy<MdnsReceiverBrowser>(() => new MdnsReceiverBrowser(options.Browser, loggerFactory.CreateLogger<MdnsReceiverBrowser>()), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The options.</summary>
    public AirplaySenderOptions Options { get; }

    /// <summary>Where regular pairings live.</summary>
    public IPairingStore PairingStore { get; }

    /// <summary>The process-wide master clock.</summary>
    public IMasterClock Clock { get; }

    /// <summary>The receiver browser with <see cref="AirplaySenderOptions.Browser"/>, created on first use (call <see cref="IReceiverBrowser.StartAsync"/>).</summary>
    public IReceiverBrowser Browser
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 && !_browser.IsValueCreated, this);
            return _browser.Value;
        }
    }

    /// <summary>The process-wide timing responder (NTP); bound by <see cref="StartAsync"/>.</summary>
    public ITimingResponder Timing => _timing;

    /// <summary>The process-wide control port (syncs, retransmits); bound by <see cref="StartAsync"/>.</summary>
    public IControlPort Control => _control;

    /// <summary>The PTP grandmaster once a PTP-timed group started it, else null.</summary>
    public IPtpMaster? Ptp => Volatile.Read(ref _ptp);

    /// <summary>PIN pairings started and not yet completed or expired.</summary>
    public int PendingPinPairings => _pinPairings.Count;

    private ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// A stable sender identity for one installation: <c>deviceID</c> and <c>macAddress</c> are a locally administered
    /// unicast MAC derived from the controller identity (SHA-256 of its id), so they stay the same across restarts and differ
    /// between installations (SPEC-control.md 4.2; GroupPlayerOptions.Sender).
    /// </summary>
    /// <param name="name">The display name sent as <c>name</c>.</param>
    /// <param name="controller">The installation's controller identity (<see cref="IPairingStore.GetControllerIdentityAsync"/>).</param>
    public static SenderIdentity DeriveIdentity(string name, ControllerIdentity controller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(controller);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(controller.Id), hash);
        hash[0] = (byte)((hash[0] & 0xFC) | 0x02); // locally administered, unicast
        var mac = string.Join(':', hash[..6].ToArray().Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        return new SenderIdentity(name, mac, mac);
    }

    /// <summary>
    /// The quirks for <paramref name="receiver"/>: the configured ones from <see cref="AirplaySenderOptions.Quirks"/> when
    /// present (a null timing protocol there keeps the model's), else the model defaults learned from the live probes
    /// (<see cref="ReceiverQuirks.ForReceiver"/>).
    /// </summary>
    public ReceiverQuirks QuirksFor(DiscoveredReceiver receiver) => ResolveQuirks(Options, receiver);

    /// <summary>
    /// <see cref="QuirksFor"/> without a sender: the quirks <paramref name="options"/> give <paramref name="receiver"/>
    /// (for hosts that describe receivers before they create a sender, e.g. the CLI's <c>discover</c>).
    /// </summary>
    public static ReceiverQuirks ResolveQuirks(AirplaySenderOptions options, DiscoveredReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(receiver);
        var known = ReceiverQuirks.ForReceiver(receiver);
        var configured = FindConfiguredQuirks(options, receiver.Id);
        if (configured is null)
        {
            return known;
        }

        return configured.TimingProtocol is null && known.TimingProtocol is not null ? configured with { TimingProtocol = known.TimingProtocol } : configured;
    }

    /// <summary>
    /// <c>GET /info</c>, bare or with the iOS stage-1 body <c>{qualifier: [txtAirPlay]}</c> (SPEC-control.md 1.8, 4.1): the
    /// request <see cref="ConnectAsync"/> sends, for hosts that query a receiver without a session (the CLI's <c>info</c>).
    /// </summary>
    public static RtspRequest InfoRequest(bool withQualifier) => SessionRequests.Info(withQualifier);

    /// <summary>The timing protocol to use with <paramref name="receiver"/> (quirk from probe P1, else the default, SPEC.md D1).</summary>
    public TimingProtocol TimingProtocolFor(DiscoveredReceiver receiver) => QuirksFor(receiver).TimingProtocol ?? Options.DefaultTimingProtocol;

    /// <summary>Binds the shared timing responder and control port; idempotent. Group players call it themselves.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socketsStarted)
            {
                return;
            }

            await _timing.StartAsync(cancellationToken).ConfigureAwait(false);
            await _control.StartAsync(cancellationToken).ConfigureAwait(false);
            _socketsStarted = true;
            _logger.LogInformation("Sender sockets up: timing UDP {TimingPort}, control UDP {ControlPort}", _timing.Port, _control.Port);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Opens a control connection to a receiver, runs GET /info and the pairing it requires; no SETUP yet.</summary>
    /// <remarks>
    /// SPEC.md D5/D2 and SPEC-control.md 1.6, 3.8: the receiver's addresses are tried in order (IPv4 first, then routable
    /// IPv6, then link-local); on the connection, <c>GET /info</c>, then pair-verify with stored credentials, else transient
    /// pairing, else the PIN flow through <paramref name="pinPrompt"/> (without a prompt a receiver that needs a PIN fails
    /// with <see cref="PairingException"/> <see cref="PairingFailure.TransientRefused"/>; the web app then uses
    /// <see cref="BeginPinPairingAsync"/>). A transport failure (refused/timed-out connect, connection lost, request timeout)
    /// or a retryable pairing error is retried up to <see cref="AirplaySenderOptions.ConnectAttempts"/> times in total.
    /// The returned session is paired and its control channel encrypted; the caller owns it.
    /// </remarks>
    public async Task<IReceiverSession> ConnectAsync(DiscoveredReceiver receiver, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (receiver.Capabilities.Pairing == PairingRequirement.Disabled)
        {
            throw new PairingException(PairingFailure.Protocol, $"{receiver.Name} admits only HomeKit home members (acl=1 or act=2); pairing is not attempted (SPEC-control.md 1.6).");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ConnectOnceAsync(receiver, pinPrompt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < Options.ConnectAttempts && !cancellationToken.IsCancellationRequested && RetryDelay(ex) is { } delay)
            {
                _logger.LogWarning("Connecting to {Receiver} failed (attempt {Attempt} of {Attempts}): {Error}; retrying in {Delay:0.0} s", receiver.Name, attempt, Options.ConnectAttempts, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Starts a PIN pairing for the web app flow: returns after M2 so the UI can ask for the PIN.</summary>
    /// <remarks>
    /// SPEC-control.md 3.6 steps 1-3: a dedicated connection, <c>GET /info</c>, <c>POST /pair-pin-start</c> (the receiver
    /// shows its PIN) and M1/M2 with the method byte of <see cref="ReceiverSessionOptions.PinPairingMethod"/> (D2). The
    /// connection stays open until <see cref="CompletePinPairingAsync"/> or until
    /// <see cref="AirplaySenderOptions.PinPairingTimeout"/> expires, whichever comes first.
    /// </remarks>
    public async Task<PinPairingInProgress> BeginPinPairingAsync(DiscoveredReceiver receiver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (receiver.Capabilities.Pairing == PairingRequirement.Disabled)
        {
            throw new PairingException(PairingFailure.Protocol, $"{receiver.Name} admits only HomeKit home members (acl=1 or act=2); pairing is not attempted (SPEC-control.md 1.6).");
        }

        var connection = await OpenConnectionAsync(receiver, cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.SendExpectOkAsync(SessionRequests.Info(withQualifier: false), cancellationToken).ConfigureAwait(false);
            var controller = await PairingStore.GetControllerIdentityAsync(cancellationToken).ConfigureAwait(false);
            var client = new HapPairingClient(LoggerFactory.CreateLogger<HapPairingClient>());
            _logger.LogInformation("PIN pairing with {Receiver} started: the receiver shows its PIN", receiver.Name);
            var started = await client.StartPinPairingAsync(connection, controller, Options.Session.PinPairingMethod, cancellationToken).ConfigureAwait(false);
            var pending = new PendingPinPairing(receiver, connection, client);
            _pinPairings[started] = pending;
            pending.StartExpiry(Options.PinPairingTimeout, () => ExpirePinPairingAsync(started));
            return started;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Completes a PIN pairing and persists the credentials.</summary>
    /// <remarks>
    /// SPEC-control.md 3.6 steps 4-8: M3-M6 with <paramref name="pin"/> (M6 accessory signature verified, D2), the
    /// credentials stored under the receiver id (3.8), then pair-verify on the same connection to prove them, as owntone
    /// does (behaviour described in SPEC-control.md 3.6). Any failure of that pair-verify (rejected, malformed answer,
    /// transport error, cancellation) deletes the stored credentials again (3.9). The pairing's connection is closed afterwards, also on failure:
    /// the receiver's SRP session accepts one M3, so a wrong PIN (<see cref="PairingFailure.Authentication"/>) means
    /// starting over with <see cref="BeginPinPairingAsync"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The pairing was not started by this sender, already completed, or expired.</exception>
    public async Task<PairingCredentials> CompletePinPairingAsync(PinPairingInProgress pairing, string pin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);
        if (!_pinPairings.TryRemove(pairing, out var pending))
        {
            throw new InvalidOperationException("This PIN pairing is unknown, already completed or expired; start a new one.");
        }

        try
        {
            var credentials = await pending.Client.FinishPinPairingAsync(pairing, pin.Trim(), cancellationToken).ConfigureAwait(false);
            await PairingStore.SaveAsync(pending.Receiver.Id, credentials, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("PIN pairing with {Receiver} stored (accessory {AccessoryId}); verifying", pending.Receiver.Name, credentials.AccessoryId);
            try
            {
                await pending.Client.VerifyAsync(pending.Connection, credentials, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // SPEC-control.md 3.9: a failed pair-verify drops the stored pairing; credentials that never verified are
                // not kept, whatever the failure was.
                await PairingStore.DeleteAsync(pending.Receiver.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            _logger.LogInformation("Pair-verify with {Receiver} succeeded with the new pairing", pending.Receiver.Name);
            return credentials;
        }
        finally
        {
            await pending.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates a group player with the preferred stream transport; receivers are added with <see cref="IGroupPlayer.AddReceiverAsync"/>.</summary>
    /// <remarks>
    /// Without knowing the members the transport is the realtime stream under <see cref="AirplaySenderOptions.DefaultTimingProtocol"/>
    /// (SPEC.md D1). Use the overload with the intended receivers to let their quirks and capabilities decide.
    /// </remarks>
    public Task<IGroupPlayer> CreateGroupPlayerAsync(GroupPlayerOptions? options, CancellationToken cancellationToken) =>
        CreatePlayerAsync(StreamType.Realtime, Options.DefaultTimingProtocol, options, cancellationToken);

    /// <summary>
    /// Creates a group player whose transport fits <paramref name="receivers"/> (the intended members; more can join later
    /// if they use the same timing protocol): buffered only when preferred and every receiver advertises bit 40, else
    /// realtime; PTP when any receiver needs it (quirk from probe P1), else the default timing protocol.
    /// </summary>
    public Task<IGroupPlayer> CreateGroupPlayerAsync(IReadOnlyCollection<DiscoveredReceiver> receivers, GroupPlayerOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receivers);
        var (type, timing) = ChooseTransport(receivers);
        return CreatePlayerAsync(type, timing, options, cancellationToken);
    }

    /// <summary>The stream type and timing protocol <see cref="CreateGroupPlayerAsync(IReadOnlyCollection{DiscoveredReceiver}, GroupPlayerOptions?, CancellationToken)"/> chooses for these receivers.</summary>
    public (StreamType Type, TimingProtocol Timing) ChooseTransport(IReadOnlyCollection<DiscoveredReceiver> receivers)
    {
        ArgumentNullException.ThrowIfNull(receivers);
        var type = StreamType.Realtime;
        if (Options.PreferredStream == StreamType.Buffered)
        {
            var lacking = receivers.Where(r => !r.Capabilities.SupportsBufferedStream).Select(r => r.Name).ToList();
            if (lacking.Count == 0 && receivers.Count > 0)
            {
                type = StreamType.Buffered;
            }
            else
            {
                _logger.LogInformation("Buffered stream preferred, but {Receivers} lack feature bit 40: using the realtime stream", lacking.Count == 0 ? "no receivers were named" : string.Join(", ", lacking));
            }
        }

        if (type == StreamType.Buffered)
        {
            return (type, TimingProtocol.Ptp); // SPEC-audio.md 4: the buffered stream is PTP-timed.
        }

        var protocols = receivers.Select(r => (r.Name, Protocol: TimingProtocolFor(r))).ToList();
        if (protocols.Any(p => p.Protocol == TimingProtocol.Ptp))
        {
            var ntp = protocols.Where(p => p.Protocol == TimingProtocol.Ntp).Select(p => p.Name).ToList();
            if (ntp.Count > 0)
            {
                // One group, one timeline, one clock (SPEC.md D3): the whole group follows the PTP grandmaster.
                _logger.LogWarning("Mixed timing protocols: {Receivers} prefer NTP but the group runs under PTP because another member needs it", string.Join(", ", ntp));
            }

            return (type, TimingProtocol.Ptp);
        }

        return (type, protocols.Count == 0 ? Options.DefaultTimingProtocol : TimingProtocol.Ntp);
    }

    /// <inheritdoc />
    /// <remarks>Disposes live players created here (TEARDOWN of their members), pending PIN pairings, the PTP master, the shared sockets and the browser.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var (player, _) in _players)
        {
            await DisposeQuietlyAsync(player, "group player").ConfigureAwait(false);
        }

        foreach (var key in _pinPairings.Keys.ToList())
        {
            if (_pinPairings.TryRemove(key, out var pending))
            {
                await DisposeQuietlyAsync(pending, "PIN pairing").ConfigureAwait(false);
            }
        }

        if (Interlocked.Exchange(ref _ptp, null) is { } ptp)
        {
            await DisposeQuietlyAsync(ptp, "PTP master").ConfigureAwait(false);
        }

        await DisposeQuietlyAsync(_control, "control port").ConfigureAwait(false);
        await DisposeQuietlyAsync(_timing, "timing responder").ConfigureAwait(false);
        if (_browser.IsValueCreated)
        {
            await DisposeQuietlyAsync(_browser.Value, "browser").ConfigureAwait(false);
        }
        _startGate.Dispose();
    }

    private static IEnumerable<IPAddress> AddressOrder(DiscoveredReceiver receiver) =>
        receiver.Addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : a.IsIPv6LinkLocal ? 2 : 1)
            .Distinct();

    private async Task<IGroupPlayer> CreatePlayerAsync(StreamType type, TimingProtocol timing, GroupPlayerOptions? options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        options ??= Options.Player;

        // GroupPlayerOptions.Sender defaults to a test placeholder; a host's identity replaces it unless the caller set one.
        if (options.Sender == PlaceholderSender)
        {
            options = options with { Sender = Options.Identity };
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
        var ptp = timing == TimingProtocol.Ptp ? await EnsurePtpMasterAsync(cancellationToken).ConfigureAwait(false) : null;
        Func<DiscoveredReceiver, CancellationToken, Task<IReceiverSession>> sessionFactory = (receiver, ct) => ConnectAsync(receiver, pinPrompt: null, ct);
        GroupPlayer player;
        if (type == StreamType.Buffered)
        {
            // The buffered stream needs its session decorator (anchors, FLUSHBUFFERED, PTP peers, type-103 SETUP) and one L
            // shared by the anchors and the player (SPEC-audio.md 4.3); CreateGroupPlayer wires both.
            var buffered = new BufferedStreamTransport(Clock, ptp!, _control, new BufferedStreamOptions { LatencyFrames = options.LatencyFrames }, LoggerFactory.CreateLogger<BufferedStreamTransport>());
            player = buffered.CreateGroupPlayer(sessionFactory, options, LoggerFactory.CreateLogger<GroupPlayer>());
        }
        else
        {
            var realtime = new RealtimeStreamTransport(Clock, _timing, _control, options.LatencyFrames, ptp, LoggerFactory.CreateLogger<RealtimeStreamTransport>());
            player = new GroupPlayer(realtime, sessionFactory, options, LoggerFactory.CreateLogger<GroupPlayer>());
        }

        _players.AddOrUpdate(player, type);
        _logger.LogInformation("Group player created: {StreamType} stream, {Timing} timing, L = {Latency} frames, group {Group}", type, timing, player.Options.LatencyFrames, options.GroupUuid);
        return player;
    }

    /// <summary>
    /// The process-wide PTP grandmaster (SPEC-audio.md 4.4), started with <see cref="AirplaySenderOptions.Ptp"/> on first
    /// call; PTP-timed group players use it, and hosts that open PTP sessions themselves (the CLI's probes and
    /// <c>volume</c>) use it for <c>timingPeerInfo</c> and <c>SETPEERS</c>. The sender disposes it.
    /// </summary>
    /// <exception cref="PtpMasterException">UDP 319/320 could not be bound (e.g. nqptp runs in the same network namespace).</exception>
    public async Task<IPtpMaster> EnsurePtpMasterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ptp is { } existing)
            {
                return existing;
            }

            var master = new PtpMaster(Clock, LoggerFactory.CreateLogger<PtpMaster>(), Options.Ptp);
            try
            {
                await master.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // PtpMasterException when UDP 319/320 are taken (nqptp in the same namespace, TEST-RECEIVER.md correction).
                await master.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            Volatile.Write(ref _ptp, master);
            _logger.LogInformation("PTP grandmaster {ClockId:X16} started on UDP {EventPort}/{GeneralPort}", master.ClockId, master.EventPort, master.GeneralPort);
            return master;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<IReceiverSession> ConnectOnceAsync(DiscoveredReceiver receiver, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(receiver, cancellationToken).ConfigureAwait(false);
        var quirks = QuirksFor(receiver);
        var session = new ReceiverSession(
            receiver,
            connection,
            new HapPairingClient(LoggerFactory.CreateLogger<HapPairingClient>()),
            quirks,
            LoggerFactory.CreateLogger<ReceiverSession>(),
            Options.Session);
        try
        {
            var info = await session.GetInfoAsync(withQualifier: false, cancellationToken).ConfigureAwait(false);
            var pairing = await session.PairAsync(PairingStore, pinPrompt, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Connected to {Receiver} at {Endpoint} ({Model}, srcvers {SourceVersion}), paired {Kind}, timing {Timing}",
                receiver.Name,
                connection.RemoteEndPoint,
                info.GetString("model") ?? receiver.Airplay.Model ?? "?",
                info.GetString("sourceVersion") ?? receiver.Airplay.SourceVersion ?? "?",
                pairing.Kind,
                quirks.TimingProtocol?.ToString() ?? $"{Options.DefaultTimingProtocol} (default)");
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<RtspConnection> OpenConnectionAsync(DiscoveredReceiver receiver, CancellationToken cancellationToken)
    {
        var addresses = AddressOrder(receiver).ToList();
        if (addresses.Count == 0)
        {
            throw new InvalidOperationException($"{receiver.Name} has no address (mDNS gave no A/AAAA record).");
        }

        var options = Options.Connection.ClientName is null ? Options.Connection with { ClientName = Options.Identity.Name } : Options.Connection;
        Exception? last = null;
        foreach (var address in addresses)
        {
            var endpoint = new IPEndPoint(address, receiver.Port);
            try
            {
                return await RtspConnection.ConnectAsync(endpoint, options, LoggerFactory.CreateLogger<RtspConnection>(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or IOException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Connect to {Receiver} at {Endpoint} failed: {Error}", receiver.Name, endpoint, ex.Message);
                last = ex;
            }
        }

        throw last is SocketException or TimeoutException
            ? new IOException($"Could not connect to {receiver.Name} at any of its {addresses.Count} address(es): {last.Message}", last)
            : last!;
    }

    private TimeSpan? RetryDelay(Exception ex) => ex switch
    {
        PairingException { Failure: PairingFailure.Retryable } p => Clamp(p.RetryAfter ?? Options.ConnectRetryDelay),
        PairingException => null,
        RtspStatusException => null,
        IOException or TimeoutException or SocketException => Options.ConnectRetryDelay,
        _ => null,
    };

    private TimeSpan Clamp(TimeSpan delay) => delay < Options.ConnectRetryDelay ? Options.ConnectRetryDelay : delay > Options.MaxRetryDelay ? Options.MaxRetryDelay : delay;

    private static ReceiverQuirks? FindConfiguredQuirks(AirplaySenderOptions options, string receiverId)
    {
        if (options.Quirks.TryGetValue(receiverId, out var exact))
        {
            return exact;
        }

        var wanted = NormalizeId(receiverId);
        foreach (var (id, quirks) in options.Quirks)
        {
            if (NormalizeId(id) == wanted)
            {
                return quirks;
            }
        }

        return null;
    }

    private static string NormalizeId(string id) => id.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private async Task ExpirePinPairingAsync(PinPairingInProgress pairing)
    {
        if (_pinPairings.TryRemove(pairing, out var pending))
        {
            _logger.LogWarning("PIN pairing with {Receiver} expired after {Timeout}; connection closed", pending.Receiver.Name, Options.PinPairingTimeout);
            await DisposeQuietlyAsync(pending, "expired PIN pairing").ConfigureAwait(false);
        }
    }

    private async Task DisposeQuietlyAsync(IAsyncDisposable disposable, string what)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("Disposing the {What} failed: {Error}", what, ex.Message);
        }
    }

    /// <summary>A PIN pairing between M2 and M3: its connection, client and expiry timer.</summary>
    private sealed class PendingPinPairing(DiscoveredReceiver receiver, RtspConnection connection, HapPairingClient client) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _expiry = new();
        private int _disposed;

        public DiscoveredReceiver Receiver { get; } = receiver;

        public RtspConnection Connection { get; } = connection;

        public HapPairingClient Client { get; } = client;

        /// <summary>The running expiry handler, kept so it is observed (it catches its own failures).</summary>
        public Task? Expiry { get; private set; }

        public void StartExpiry(TimeSpan timeout, Func<Task> onExpired)
        {
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                Expiry = RunExpiryAsync(timeout, onExpired);
            }
        }

        private async Task RunExpiryAsync(TimeSpan timeout, Func<Task> onExpired)
        {
            try
            {
                await Task.Delay(timeout, _expiry.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // completed or disposed in time
            }

            await onExpired().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _expiry.CancelAsync().ConfigureAwait(false);
            await Connection.DisposeAsync().ConfigureAwait(false);
            _expiry.Dispose();
        }
    }
}
