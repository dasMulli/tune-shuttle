using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Airplay2.Sender.Rtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Sender.Streams;

/// <summary>Options of <see cref="PtpMaster"/>. The defaults are what libairptp sends and nqptp/Apple receivers accept (SPEC-audio.md 4.4).</summary>
public sealed record PtpMasterOptions
{
    /// <summary>Local address for both sockets; null binds the wildcard (dual-mode IPv6 where available).</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>Local event port (Sync, Delay_Req). 319 by the standard; 0 lets the OS choose (tests, or a host where nqptp owns 319).</summary>
    public int EventPort { get; init; } = PtpMessages.EventPort;

    /// <summary>Local general port (Announce, Follow_Up, Delay_Resp, Signaling). 320 by the standard; 0 lets the OS choose.</summary>
    public int GeneralPort { get; init; } = PtpMessages.GeneralPort;

    /// <summary>The event port peers listen on (Sync, Pdelay_Resp go there); 319 for every real receiver.</summary>
    public int PeerEventPort { get; init; } = PtpMessages.EventPort;

    /// <summary>The general port peers listen on (Announce, Follow_Up, Delay_Resp, Signaling go there); 320 for every real receiver.</summary>
    public int PeerGeneralPort { get; init; } = PtpMessages.GeneralPort;

    /// <summary>The 48-bit seed of the clock identity (<c>0xFFFF &lt;&lt; 48 | seed</c>); random when null. Persist it to keep one identity across restarts.</summary>
    public ulong? ClockIdSeed { get; init; }

    /// <summary>Sync + Follow_Up interval: 125 ms (log interval -3), as iOS, the Apple TV, nqptp and libairptp use (<c>[ot] libairptp/src/airptp_internal.h:32-34</c>).</summary>
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromMilliseconds(125);

    /// <summary>Announce interval: 1 s (log interval 0; "the ATV uses -2 ... my amp uses 0, as does nqptp", <c>airptp_internal.h:27-31</c>).</summary>
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Signaling interval: 1 s, as iOS (<c>airptp_internal.h:35-37</c>).</summary>
    public TimeSpan SignalingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gap between a tick's Syncs and their Follow_Ups: 100 µs, as libairptp sleeps (<c>ptp_msg_handle.c:948-951</c>), so a
    /// slave that reads its event and general sockets separately has processed the Sync before the Follow_Up arrives.
    /// Gaps below 1 ms are a bounded spin (no I/O wait, at most this long per tick); longer ones use a timer.
    /// </summary>
    public TimeSpan FollowUpDelay { get; init; } = TimeSpan.FromTicks(1000);

    /// <summary>Send the iOS Signaling message (two Apple TLVs) every <see cref="SignalingInterval"/>.</summary>
    public bool SendSignaling { get; init; } = true;

    /// <summary>
    /// Quirk: write the peer's own port identity (learned from any message it sent) as the Signaling
    /// <c>targetPortIdentity</c>, as iOS does; off by default because libairptp sends zeros ("TODO iOS sets
    /// targetPortIdentity", <c>ptp_msg_handle.c:325-363</c>) and nqptp and Apple receivers accept that.
    /// </summary>
    public bool SignalingTargetsPeer { get; init; }

    /// <summary>Answer Pdelay_Req with Pdelay_Resp + Pdelay_Resp_Follow_Up (libairptp does, as a guess; no AirPlay device was seen sending Pdelay_Req).</summary>
    public bool AnswerPeerDelayRequests { get; init; } = true;

    /// <summary>
    /// Liveness threshold: a peer whose address sent nothing for this long is silent. 15 s, libairptp's
    /// <c>AIRPTP_STALE_SECS</c> (<c>airptp_internal.h:17</c>, <c>ptp_msg_handle.c:885-891</c>).
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables the check; any other value must be positive. What happens to a silent peer is
    /// <see cref="DropSilentPeers"/>.
    /// </summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Quirk: drop a peer after <see cref="PeerTimeout"/> of silence, as libairptp stops sending to it. Off by default:
    /// nqptp (shairport-sync 5.5.2) sends only two Announces when it starts following us and nothing after, not even
    /// Delay_Req (probe P17 in the Docker VM, 2026-10-09, SPEC-audio.md 4.4 note), so libairptp's rule would cut every
    /// shairport-sync session's Syncs 15 s in. By default a silent peer is reported once
    /// (<see cref="PtpMasterEvent.PeerSilent"/>, Warning) and keeps receiving until <see cref="IPtpMaster.RemovePeer"/>,
    /// which the session calls on TEARDOWN.
    /// </summary>
    public bool DropSilentPeers { get; init; }
}

/// <summary>Why a peer left the grandmaster's list.</summary>
public enum PtpPeerRemovalReason
{
    /// <summary><see cref="IPtpMaster.RemovePeer"/> was called.</summary>
    Removed,

    /// <summary>Nothing arrived from the peer within <see cref="PtpMasterOptions.PeerTimeout"/> and <see cref="PtpMasterOptions.DropSilentPeers"/> is on.</summary>
    TimedOut,
}

/// <summary>Observations of the grandmaster (bounded feed, oldest dropped).</summary>
public abstract record PtpMasterEvent
{
    /// <summary>A peer was added (or re-added).</summary>
    public sealed record PeerAdded(IPAddress Address) : PtpMasterEvent;

    /// <summary>A peer was removed.</summary>
    public sealed record PeerRemoved(IPAddress Address, PtpPeerRemovalReason Reason) : PtpMasterEvent;

    /// <summary>Nothing arrived from a peer for <see cref="PtpMasterOptions.PeerTimeout"/>; it stays a peer (reported once until it sends again).</summary>
    public sealed record PeerSilent(IPAddress Address, TimeSpan Timeout) : PtpMasterEvent;

    /// <summary>
    /// Another clock announced itself (reported once per clock identity). We announce class 6 / priority 128 and "always
    /// expect to become master clock" (<c>ptp_msg_handle.c:577-581</c>), so this is informational.
    /// </summary>
    public sealed record ForeignAnnounce(IPAddress Source, ulong ClockId, ulong GrandmasterId) : PtpMasterEvent;

    /// <summary>The first message of a type from a peer (SPEC.md P17: which messages does a receiver's slave send?).</summary>
    public sealed record FirstMessageFromPeer(IPAddress Source, int SourcePort, PtpMessageType Type, PtpPortIdentity SourcePortIdentity) : PtpMasterEvent;
}

/// <summary>Counters of a <see cref="PtpMaster"/>.</summary>
/// <param name="SyncsSent">Sync messages (one per peer per interval).</param>
/// <param name="FollowUpsSent">Follow_Up messages.</param>
/// <param name="AnnouncesSent">Announce messages.</param>
/// <param name="SignalingsSent">Signaling messages.</param>
/// <param name="DelayResponsesSent">Delay_Resp answers.</param>
/// <param name="PeerDelayResponsesSent">Pdelay_Resp answers.</param>
/// <param name="MessagesReceived">Datagrams received on either port.</param>
/// <param name="MessagesIgnored">Datagrams that were not PTPv2 or came from our own clock.</param>
/// <param name="SendErrors">Datagrams that could not be sent.</param>
/// <param name="PeersTimedOut">Peers dropped by the liveness check (<see cref="PtpMasterOptions.DropSilentPeers"/>).</param>
/// <param name="SilentPeerReports">Times a peer was reported silent.</param>
/// <param name="SendLoopErrors">Send-loop ticks that failed with an unexpected exception (logged; the loop goes on with the next tick).</param>
public sealed record PtpMasterStatistics(
    long SyncsSent,
    long FollowUpsSent,
    long AnnouncesSent,
    long SignalingsSent,
    long DelayResponsesSent,
    long PeerDelayResponsesSent,
    long MessagesReceived,
    long MessagesIgnored,
    long SendErrors,
    long PeersTimedOut,
    long SilentPeerReports,
    long SendLoopErrors);

/// <summary>One peer as the grandmaster sees it.</summary>
/// <param name="Address">The peer's address.</param>
/// <param name="EventEndPoint">Where its Syncs go.</param>
/// <param name="GeneralEndPoint">Where its Announce/Follow_Up/Delay_Resp/Signaling go.</param>
/// <param name="SinceLastSeen">Time since it was added or last sent us anything.</param>
/// <param name="MessagesReceived">Datagrams received from its address.</param>
/// <param name="PortIdentity">Its PTP port identity, once it sent a PTP message.</param>
public sealed record PtpPeerStatus(IPAddress Address, IPEndPoint EventEndPoint, IPEndPoint GeneralEndPoint, TimeSpan SinceLastSeen, long MessagesReceived, PtpPortIdentity? PortIdentity);

/// <summary>The grandmaster could not bind its ports.</summary>
public sealed class PtpMasterException : IOException
{
    /// <summary>Creates the exception.</summary>
    public PtpMasterException(string message, int port, Exception? innerException = null)
        : base(message, innerException)
    {
        Port = port;
    }

    /// <summary>The port that could not be bound.</summary>
    public int Port { get; }
}

/// <summary>
/// The PTP grandmaster for PTP-timed AirPlay 2 sessions (SPEC-audio.md 4.4; SPEC.md D1): IEEE 1588-2008 on UDP 319 (event)
/// and 320 (general), unicast to each peer, with the message set libairptp sends and nqptp and Apple receivers accept:
/// Announce every second, two-step Sync every 125 ms followed by a Follow_Up carrying the Sync's send time and the
/// 802.1AS and Apple TLVs, Signaling every second, Delay_Resp to every Delay_Req (and Pdelay_Resp to Pdelay_Req). Every
/// timestamp is <see cref="IMasterClock.PtpNowNanos"/> of the one shared clock, the same timeline the 0xD7 sync packets
/// and <c>SETRATEANCHORTIME</c> use. Other clocks' Announces are ignored (we announce a class-6 clock and expect to win).
/// </summary>
/// <remarks>
/// <para>
/// Threads: one send loop (a <see cref="PeriodicTimer"/> at the Sync interval; Announce and Signaling ride on the same
/// tick when due) and one receive loop per socket (<see cref="UdpReceiveLoop"/>). The peer list is guarded by a lock and
/// published to the send loop as an immutable array, so a tick allocates nothing. The Sync send time <c>t1</c> is
/// captured right before each peer's Sync leaves; all Syncs of a tick go out before their Follow_Ups, with libairptp's
/// 100 µs gap in between (<see cref="PtpMasterOptions.FollowUpDelay"/>).
/// </para>
/// <para>
/// Liveness: a peer whose address sent nothing for <see cref="PtpMasterOptions.PeerTimeout"/> (15 s) is reported silent
/// once and kept (nqptp never sends after its start-up Announces, SPEC-audio.md 4.4 note of 2026-10-09); with
/// <see cref="PtpMasterOptions.DropSilentPeers"/> it is dropped as libairptp does. An <see cref="AddPeer(IPAddress)"/> of
/// the same address re-arms the check. Send errors are logged once per peer until a send succeeds again and do not
/// remove the peer.
/// </para>
/// </remarks>
public sealed partial class PtpMaster : IPtpMaster
{
    private readonly IMasterClock _clock;
    private readonly ILogger _logger;
    private readonly PtpMasterOptions _options;
    private readonly UdpReceiveLoop _eventLoop;
    private readonly UdpReceiveLoop _generalLoop;
    private const string EventLoopName = "PTP event port";
    private const string GeneralLoopName = "PTP general port";

    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Peer> _peers = [];
    private readonly HashSet<ulong> _foreignClocks = [];
    private readonly Channel<PtpMasterEvent> _events = Channel.CreateBounded<PtpMasterEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly sbyte _syncLogInterval;
    private readonly sbyte _announceLogInterval;
    private readonly long _peerTimeoutNanos;

    // Send-loop buffers (one loop, so one set).
    private readonly byte[] _syncBuffer = new byte[PtpMessages.SyncSize];
    private readonly byte[] _followUpBuffer = new byte[PtpMessages.FollowUpSize];
    private readonly byte[] _announceBuffer = new byte[PtpMessages.AnnounceSize];
    private readonly byte[] _signalingBuffer = new byte[PtpMessages.SignalingSize];
    private ulong[] _syncTimes = new ulong[4];

    // Reply buffers, one per receive loop (the two loops run concurrently).
    private readonly byte[] _eventReplyBuffer = new byte[PtpMessages.MaxMessageSize];
    private readonly byte[] _generalReplyBuffer = new byte[PtpMessages.MaxMessageSize];

    private Peer[] _snapshot = [];
    private Task? _sendLoop;
    private bool _started;
    private bool _disposed;
    private int _announceNow;
    private ushort _syncSequence;
    private ushort _announceSequence;
    private ushort _signalingSequence;
    private long _nextAnnounce = long.MinValue;
    private long _nextSignaling = long.MinValue;

    private long _syncsSent;
    private long _followUpsSent;
    private long _announcesSent;
    private long _signalingsSent;
    private long _delayResponsesSent;
    private long _peerDelayResponsesSent;
    private long _messagesReceived;
    private long _messagesIgnored;
    private long _sendErrors;
    private long _peersTimedOut;
    private long _silentPeerReports;
    private long _sendLoopErrors;

    /// <summary>Creates the grandmaster; nothing is bound until <see cref="StartAsync"/>.</summary>
    /// <param name="clock">The shared master clock (the realtime transport's <see cref="IStreamTransport.Clock"/>).</param>
    /// <param name="logger">Logger; null for none.</param>
    /// <param name="options">Options; null for the defaults.</param>
    public PtpMaster(IMasterClock clock, ILogger<PtpMaster>? logger = null, PtpMasterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _options = options ?? new PtpMasterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.SyncInterval, TimeSpan.Zero, "options.SyncInterval");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.AnnounceInterval, TimeSpan.Zero, "options.AnnounceInterval");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.SignalingInterval, TimeSpan.Zero, "options.SignalingInterval");
        ValidatePort(_options.PeerEventPort, "options.PeerEventPort", allowZero: false);
        ValidatePort(_options.PeerGeneralPort, "options.PeerGeneralPort", allowZero: false);
        ClockId = PtpMessages.ClockIdFromSeed(_options.ClockIdSeed ?? PtpMessages.RandomSeed());
        _syncLogInterval = PtpMessages.LogInterval(_options.SyncInterval);
        _announceLogInterval = PtpMessages.LogInterval(_options.AnnounceInterval);
        if (_options.PeerTimeout != Timeout.InfiniteTimeSpan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.PeerTimeout, TimeSpan.Zero, "options.PeerTimeout");
        }

        _peerTimeoutNanos = _options.PeerTimeout == Timeout.InfiniteTimeSpan
            ? long.MaxValue
            : (long)_options.PeerTimeout.TotalMilliseconds * 1_000_000L;
        _eventLoop = new UdpReceiveLoop(EventLoopName, _logger, _options.BindAddress, _options.EventPort, HandleEventAsync);
        _generalLoop = new UdpReceiveLoop(GeneralLoopName, _logger, _options.BindAddress, _options.GeneralPort, HandleGeneralAsync);
    }

    /// <inheritdoc />
    public ulong ClockId { get; }

    /// <summary>The bound event port; 0 before <see cref="StartAsync"/>.</summary>
    public int EventPort => _eventLoop.Port;

    /// <summary>The bound general port; 0 before <see cref="StartAsync"/>.</summary>
    public int GeneralPort => _generalLoop.Port;

    /// <summary>The options in effect.</summary>
    public PtpMasterOptions Options => _options;

    /// <summary>Peer and diagnostic events (bounded, oldest dropped).</summary>
    public ChannelReader<PtpMasterEvent> Events => _events.Reader;

    /// <summary>The current peers.</summary>
    public IReadOnlyList<PtpPeerStatus> Peers
    {
        get
        {
            var now = _clock.NowNanos;
            var peers = Volatile.Read(ref _snapshot);
            var result = new PtpPeerStatus[peers.Length];
            for (var i = 0; i < peers.Length; i++)
            {
                var p = peers[i];
                result[i] = new PtpPeerStatus(p.Address, p.EventEndPoint, p.GeneralEndPoint, TimeSpan.FromTicks(Math.Max(0, now - Volatile.Read(ref p.LastSeenNanos)) / 100), Interlocked.Read(ref p.Received), p.Identity);
            }

            return result;
        }
    }

    /// <summary>A snapshot of the counters.</summary>
    public PtpMasterStatistics GetStatistics() => new(
        Interlocked.Read(ref _syncsSent),
        Interlocked.Read(ref _followUpsSent),
        Interlocked.Read(ref _announcesSent),
        Interlocked.Read(ref _signalingsSent),
        Interlocked.Read(ref _delayResponsesSent),
        Interlocked.Read(ref _peerDelayResponsesSent),
        Interlocked.Read(ref _messagesReceived),
        Interlocked.Read(ref _messagesIgnored),
        Interlocked.Read(ref _sendErrors),
        Interlocked.Read(ref _peersTimedOut),
        Interlocked.Read(ref _silentPeerReports),
        Interlocked.Read(ref _sendLoopErrors));

    /// <summary>Adds a unicast peer at the standard ports (<see cref="PtpMasterOptions.PeerEventPort"/>/<see cref="PtpMasterOptions.PeerGeneralPort"/>); re-adding refreshes its liveness.</summary>
    public void AddPeer(IPAddress address) => AddPeer(address, _options.PeerEventPort, _options.PeerGeneralPort);

    /// <summary>Adds a unicast peer listening on non-standard ports (tests: a slave on loopback next to the master).</summary>
    public void AddPeer(IPAddress address, int eventPort, int generalPort)
    {
        ArgumentNullException.ThrowIfNull(address);
        ValidatePort(eventPort, nameof(eventPort), allowZero: false);
        ValidatePort(generalPort, nameof(generalPort), allowZero: false);
        address = PtpSessionWiring.Normalize(address);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var peer = new Peer(address, new IPEndPoint(address, eventPort), new IPEndPoint(address, generalPort), _clock.NowNanos);
            if (_peers.TryGetValue(address, out var existing))
            {
                peer.Identity = existing.Identity;
                Interlocked.Exchange(ref peer.Received, Interlocked.Read(ref existing.Received));
            }

            peer.Bind(_eventLoop.Socket, _generalLoop.Socket);
            _peers[address] = peer;
            Publish();
        }

        Interlocked.Exchange(ref _announceNow, 1);
        LogPeerAdded(address, eventPort, generalPort);
        _events.Writer.TryWrite(new PtpMasterEvent.PeerAdded(address));
    }

    /// <inheritdoc />
    public void RemovePeer(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = PtpSessionWiring.Normalize(address);
        bool removed;
        lock (_gate)
        {
            removed = _peers.Remove(address);
            if (removed)
            {
                Publish();
            }
        }

        if (removed)
        {
            LogPeerRemoved(address);
            _events.Writer.TryWrite(new PtpMasterEvent.PeerRemoved(address, PtpPeerRemovalReason.Removed));
        }
    }

    /// <summary>
    /// Binds the event and general ports and starts the send loop; idempotent. Throws <see cref="PtpMasterException"/> when a
    /// port is taken (nqptp, ptp4l or another grandmaster on this host) or needs privileges (ports below 1024 on Linux).
    /// The token only covers starting; the master runs until <see cref="DisposeAsync"/>.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationToken stop;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return Task.CompletedTask;
            }

            Bind(_eventLoop, EventLoopName, _options.EventPort);
            Bind(_generalLoop, GeneralLoopName, _options.GeneralPort);
            foreach (var peer in _peers.Values)
            {
                peer.Bind(_eventLoop.Socket, _generalLoop.Socket);
            }

            _started = true;
            stop = _stop.Token;
        }

        // Started outside the lock: the loop's first tick runs synchronously up to its first real await (CheckPeerLiveness
        // takes the lock, UDP sends often complete inline, the Follow_Up gap spins), which must not block AddPeer/RemovePeer.
        var loop = Task.Run(() => RunSendLoopAsync(stop), CancellationToken.None);
        lock (_gate)
        {
            // A DisposeAsync between the two locks saw no loop to await; it cancels the token, so this loop ends after at most one tick.
            _sendLoop = loop;
            if (_disposed)
            {
                return Task.CompletedTask;
            }
        }

        LogStarted(ClockId, EventPort, GeneralPort);
        return Task.CompletedTask;
    }

    /// <summary>Stops the send loop, closes both sockets and completes <see cref="Events"/>; idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _sendLoop;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogSendLoopFaulted(ex);
            }
        }

        await _eventLoop.DisposeAsync().ConfigureAwait(false);
        await _generalLoop.DisposeAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        _stop.Dispose();
    }

    private void Bind(UdpReceiveLoop loop, string name, int port)
    {
        try
        {
            loop.Start(this);
        }
        catch (SocketException ex)
        {
            // A bound event port stays bound until DisposeAsync; the caller disposes the master after this exception.
            var why = ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => "it is in use (nqptp, ptp4l or another PTP grandmaster on this host?)",
                SocketError.AccessDenied => "access was denied (ports below 1024 need root or CAP_NET_BIND_SERVICE on Linux)",
                _ => ex.SocketErrorCode.ToString(),
            };
            var what = port == 0 ? "an OS-chosen UDP port" : $"UDP {port}";
            LogBindFailed(ex, name, what, why);
            throw new PtpMasterException($"The PTP grandmaster cannot bind its {name} ({what}): {why}.", port, ex);
        }
    }

    private async Task RunSendLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.SyncInterval);
        var failing = false;
        try
        {
            do
            {
                try
                {
                    await TickAsync(stop).ConfigureAwait(false);
                    if (failing)
                    {
                        failing = false;
                        LogSendLoopRecovered(ClockId);
                    }
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    // Never let one bad tick end Announce/Sync for every peer: count it, log the first of a streak, go on.
                    Interlocked.Increment(ref _sendLoopErrors);
                    if (!failing)
                    {
                        failing = true;
                        LogSendLoopTickFailed(ex, ClockId);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested)
        {
        }

        LogStopped(ClockId);
    }

    /// <summary>One send-loop tick: liveness, then Announce and Signaling when due, then Sync + Follow_Up to every peer.</summary>
    private async ValueTask TickAsync(CancellationToken stop)
    {
        var now = _clock.NowNanos;
        CheckPeerLiveness(now);
        var peers = Volatile.Read(ref _snapshot);
        if (peers.Length == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _announceNow, 0) == 1 || now >= _nextAnnounce)
        {
            await SendAnnounceAsync(peers, stop).ConfigureAwait(false);
            _nextAnnounce = now + (long)(_options.AnnounceInterval.TotalMilliseconds * 1_000_000);
        }

        if (_options.SendSignaling && now >= _nextSignaling)
        {
            await SendSignalingAsync(peers, stop).ConfigureAwait(false);
            _nextSignaling = now + (long)(_options.SignalingInterval.TotalMilliseconds * 1_000_000);
        }

        await SendSyncAsync(peers, stop).ConfigureAwait(false);
    }

    private async ValueTask SendAnnounceAsync(Peer[] peers, CancellationToken stop)
    {
        var length = PtpMessages.WriteAnnounce(_announceBuffer, ClockId, _announceSequence++, _announceLogInterval);
        foreach (var peer in peers)
        {
            if (await SendAsync(_generalLoop.Socket, _announceBuffer.AsMemory(0, length), peer, general: true, stop).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _announcesSent);
            }
        }
    }

    private async ValueTask SendSignalingAsync(Peer[] peers, CancellationToken stop)
    {
        var sequence = _signalingSequence++;
        foreach (var peer in peers)
        {
            var target = _options.SignalingTargetsPeer && peer.Identity is { } identity ? identity : default;
            var length = PtpMessages.WriteSignaling(_signalingBuffer, ClockId, sequence, target);
            if (await SendAsync(_generalLoop.Socket, _signalingBuffer.AsMemory(0, length), peer, general: true, stop).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _signalingsSent);
            }
        }
    }

    private async ValueTask SendSyncAsync(Peer[] peers, CancellationToken stop)
    {
        var sequence = _syncSequence++;
        var syncLength = PtpMessages.WriteSync(_syncBuffer, ClockId, sequence, _syncLogInterval);
        if (_syncTimes.Length < peers.Length)
        {
            _syncTimes = new ulong[peers.Length * 2];
        }

        // All Syncs first, each with t1 taken right before it leaves; then the Follow_Ups carrying those t1.
        for (var i = 0; i < peers.Length; i++)
        {
            _syncTimes[i] = _clock.PtpNowNanos;
            if (await SendAsync(_eventLoop.Socket, _syncBuffer.AsMemory(0, syncLength), peers[i], general: false, stop).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _syncsSent);
            }
            else
            {
                _syncTimes[i] = 0;
            }
        }

        await FollowUpGapAsync(stop).ConfigureAwait(false);
        for (var i = 0; i < peers.Length; i++)
        {
            if (_syncTimes[i] == 0)
            {
                continue;
            }

            var length = PtpMessages.WriteFollowUp(_followUpBuffer, ClockId, sequence, PtpTimestamp.FromNanoseconds(_syncTimes[i]), _syncLogInterval);
            if (await SendAsync(_generalLoop.Socket, _followUpBuffer.AsMemory(0, length), peers[i], general: true, stop).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _followUpsSent);
            }
        }
    }

    private ValueTask FollowUpGapAsync(CancellationToken stop)
    {
        var gap = _options.FollowUpDelay;
        if (gap <= TimeSpan.Zero)
        {
            return ValueTask.CompletedTask;
        }

        if (gap >= TimeSpan.FromMilliseconds(1))
        {
            return new ValueTask(Task.Delay(gap, stop));
        }

        // Deliberate bounded spin (100 µs by default, once per tick): a timer cannot wait this short.
        var until = Stopwatch.GetTimestamp() + (long)(gap.TotalSeconds * Stopwatch.Frequency);
        var spinner = default(SpinWait);
        while (Stopwatch.GetTimestamp() < until)
        {
            spinner.SpinOnce(sleep1Threshold: -1);
        }

        return ValueTask.CompletedTask;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> SendAsync(Socket? socket, ReadOnlyMemory<byte> message, Peer peer, bool general, CancellationToken stop)
    {
        var target = general ? peer.GeneralTarget : peer.EventTarget;
        if (socket is null || target is null)
        {
            return false;
        }

        try
        {
            await socket.SendToAsync(message, SocketFlags.None, target, stop).ConfigureAwait(false);
            if (peer.SendFailing)
            {
                peer.SendFailing = false;
                LogSendRecovered(peer.Address);
            }

            return true;
        }
        catch (SocketException ex)
        {
            Interlocked.Increment(ref _sendErrors);
            if (!peer.SendFailing)
            {
                peer.SendFailing = true;
                LogSendFailed(ex, (PtpMessageType)(message.Span[0] & 0x0F), general ? peer.GeneralEndPoint : peer.EventEndPoint, ex.SocketErrorCode);
            }

            return false;
        }
    }

    private void CheckPeerLiveness(long now)
    {
        if (_peerTimeoutNanos == long.MaxValue)
        {
            return;
        }

        if (!_options.DropSilentPeers)
        {
            foreach (var peer in Volatile.Read(ref _snapshot))
            {
                if (now - Volatile.Read(ref peer.LastSeenNanos) > _peerTimeoutNanos && peer.TryMarkSilent())
                {
                    Interlocked.Increment(ref _silentPeerReports);
                    LogPeerSilent(peer.Address, _options.PeerTimeout, Interlocked.Read(ref peer.Received));
                    _events.Writer.TryWrite(new PtpMasterEvent.PeerSilent(peer.Address, _options.PeerTimeout));
                }
            }

            return;
        }

        List<Peer>? dropped = null;
        lock (_gate)
        {
            foreach (var peer in _peers.Values)
            {
                if (now - Volatile.Read(ref peer.LastSeenNanos) > _peerTimeoutNanos)
                {
                    (dropped ??= []).Add(peer);
                }
            }

            if (dropped is null)
            {
                return;
            }

            foreach (var peer in dropped)
            {
                _peers.Remove(peer.Address);
            }

            Publish();
        }

        foreach (var peer in dropped)
        {
            Interlocked.Increment(ref _peersTimedOut);
            LogPeerTimedOut(peer.Address, _options.PeerTimeout, Interlocked.Read(ref peer.Received));
            _events.Writer.TryWrite(new PtpMasterEvent.PeerRemoved(peer.Address, PtpPeerRemovalReason.TimedOut));
        }
    }

    private ValueTask HandleEventAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop) =>
        HandleAsync(socket, datagram, source, eventPort: true, _eventReplyBuffer, stop);

    private ValueTask HandleGeneralAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop) =>
        HandleAsync(socket, datagram, source, eventPort: false, _generalReplyBuffer, stop);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask HandleAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, bool eventPort, byte[] reply, CancellationToken stop)
    {
        // The receive time (t4 of a Delay_Req, t2 of a Pdelay_Req) first.
        var receivedAt = _clock.PtpNowNanos;
        var nowNanos = _clock.NowNanos;
        Interlocked.Increment(ref _messagesReceived);
        var from = UdpSockets.ToEndPoint(source);
        var peer = FindPeer(from.Address);
        if (peer is not null)
        {
            Volatile.Write(ref peer.LastSeenNanos, nowNanos);
            Interlocked.Increment(ref peer.Received);
            if (peer.ClearSilent())
            {
                LogPeerSendsAgain(peer.Address);
            }
        }

        if (!PtpHeader.TryRead(datagram.Span, out var header))
        {
            Interlocked.Increment(ref _messagesIgnored);
            LogNotPtp(datagram.Length, from, eventPort ? EventPort : GeneralPort);
            return;
        }

        if (header.SourcePortIdentity.ClockId == ClockId)
        {
            Interlocked.Increment(ref _messagesIgnored);
            return; // our own message (a peer on this host reflecting, or a loop)
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogReceived(header.MessageType, from, eventPort ? EventPort : GeneralPort, header.SourcePortIdentity.ClockId, header.SequenceId, header.LogMessageInterval);
        }

        if (peer is not null)
        {
            if (peer.Identity != header.SourcePortIdentity)
            {
                peer.Identity = header.SourcePortIdentity;
            }

            if (peer.MarkFirst(header.MessageType))
            {
                LogFirstMessage(header.MessageType, from, header.SourcePortIdentity.ClockId, header.LogMessageInterval);
                _events.Writer.TryWrite(new PtpMasterEvent.FirstMessageFromPeer(from.Address, from.Port, header.MessageType, header.SourcePortIdentity));
            }
        }

        switch (header.MessageType)
        {
            case PtpMessageType.DelayReq:
            {
                var length = PtpMessages.WriteDelayResp(reply, ClockId, header, PtpTimestamp.FromNanoseconds(receivedAt));
                var target = peer?.GeneralEndPoint ?? new IPEndPoint(from.Address, _options.PeerGeneralPort);
                if (await ReplyAsync(_generalLoop.Socket, reply.AsMemory(0, length), target, stop).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _delayResponsesSent);
                    LogDelayResp(target, header.SequenceId);
                }

                break;
            }

            case PtpMessageType.PdelayReq when _options.AnswerPeerDelayRequests:
            {
                var eventTarget = peer?.EventEndPoint ?? new IPEndPoint(from.Address, _options.PeerEventPort);
                var length = PtpMessages.WritePdelayResp(reply, ClockId, header, PtpTimestamp.FromNanoseconds(receivedAt));
                if (await ReplyAsync(_eventLoop.Socket, reply.AsMemory(0, length), eventTarget, stop).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _peerDelayResponsesSent);
                    var generalTarget = peer?.GeneralEndPoint ?? new IPEndPoint(from.Address, _options.PeerGeneralPort);
                    length = PtpMessages.WritePdelayRespFollowUp(reply, ClockId, header, PtpTimestamp.FromNanoseconds(_clock.PtpNowNanos));
                    await ReplyAsync(_generalLoop.Socket, reply.AsMemory(0, length), generalTarget, stop).ConfigureAwait(false);
                }

                break;
            }

            case PtpMessageType.Announce:
            {
                if (PtpMessages.TryReadGrandmasterIdentity(datagram.Span, out var grandmaster) && grandmaster != ClockId)
                {
                    bool first;
                    lock (_gate)
                    {
                        first = _foreignClocks.Add(header.SourcePortIdentity.ClockId);
                    }

                    if (first)
                    {
                        LogForeignAnnounce(from, header.SourcePortIdentity.ClockId, grandmaster);
                        _events.Writer.TryWrite(new PtpMasterEvent.ForeignAnnounce(from.Address, header.SourcePortIdentity.ClockId, grandmaster));
                    }
                }

                break;
            }

            case PtpMessageType.Signaling:
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    LogSignaling(from, DescribeTlvs(datagram.Span, header));
                }

                break;

            default:
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    LogOtherMessage(header.MessageType, from, header.SourcePortIdentity.ClockId, header.SequenceId);
                }

                break;
        }
    }

    private async ValueTask<bool> ReplyAsync(Socket? socket, ReadOnlyMemory<byte> message, IPEndPoint target, CancellationToken stop)
    {
        if (socket is null)
        {
            return false;
        }

        try
        {
            await socket.SendToAsync(message, SocketFlags.None, UdpSockets.Target(socket, target), stop).ConfigureAwait(false);
            return true;
        }
        catch (SocketException ex)
        {
            Interlocked.Increment(ref _sendErrors);
            LogReplyFailed(ex, (PtpMessageType)(message.Span[0] & 0x0F), target, ex.SocketErrorCode);
            return false;
        }
    }

    private static string DescribeTlvs(ReadOnlySpan<byte> message, in PtpHeader header)
    {
        var reader = new PtpTlvReader(PtpMessages.TlvArea(message, header));
        var parts = new List<string>();
        while (reader.TryRead(out var tlv))
        {
            if (tlv.Type == PtpMessages.TlvOrganizationExtension && tlv.OrganizationId == PtpMessages.OrganizationIeee8021
                && tlv.OrganizationSubType == PtpMessages.SubtypeMessageIntervalRequest && tlv.Value.Length >= 9)
            {
                // 802.1AS message interval request: linkDelayInterval, timeSyncInterval, announceInterval (libairptp logs and ignores it).
                parts.Add($"interval request linkDelay {(sbyte)tlv.Value[6]} timeSync {(sbyte)tlv.Value[7]} announce {(sbyte)tlv.Value[8]}");
            }
            else
            {
                parts.Add($"type 0x{tlv.Type:X4} org {tlv.OrganizationId:X6}/{tlv.OrganizationSubType:X6} ({tlv.Value.Length} bytes)");
            }
        }

        if (reader.Malformed)
        {
            parts.Add("malformed TLV");
        }

        return parts.Count == 0 ? "no TLVs" : string.Join("; ", parts);
    }

    private Peer? FindPeer(IPAddress address)
    {
        var peers = Volatile.Read(ref _snapshot);
        foreach (var peer in peers)
        {
            if (peer.Address.Equals(address))
            {
                return peer;
            }
        }

        // Link-local IPv6: the received address carries the interface scope, the configured one may not.
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal)
        {
            foreach (var peer in peers)
            {
                if (peer.Address.AddressFamily == AddressFamily.InterNetworkV6 && peer.Address.GetAddressBytes().AsSpan().SequenceEqual(address.GetAddressBytes()))
                {
                    return peer;
                }
            }
        }

        return null;
    }

    private void Publish() => Volatile.Write(ref _snapshot, [.. _peers.Values]);

    private static void ValidatePort(int port, string name, bool allowZero)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, allowZero ? 0 : 1, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort, name);
    }

    /// <summary>One unicast peer. Mutable fields are written by the send/receive loops with Volatile/Interlocked.</summary>
    private sealed class Peer(IPAddress address, IPEndPoint eventEndPoint, IPEndPoint generalEndPoint, long addedAtNanos)
    {
        public long LastSeenNanos = addedAtNanos;
        public long Received;
        private int _firstSeenTypes;

        public IPAddress Address { get; } = address;

        public IPEndPoint EventEndPoint { get; } = eventEndPoint;

        public IPEndPoint GeneralEndPoint { get; } = generalEndPoint;

        /// <summary>Pre-serialised targets for the event and general sockets (no per-send allocation).</summary>
        public SocketAddress? EventTarget { get; private set; }

        public SocketAddress? GeneralTarget { get; private set; }

        private StrongBox<PtpPortIdentity>? _identity;

        /// <summary>The peer's port identity once it sent a PTP message (boxed so the send loop never reads a torn value).</summary>
        public PtpPortIdentity? Identity
        {
            get => Volatile.Read(ref _identity)?.Value;
            set => Volatile.Write(ref _identity, value is { } v ? new StrongBox<PtpPortIdentity>(v) : null);
        }

        public bool SendFailing { get; set; }

        public void Bind(Socket? eventSocket, Socket? generalSocket)
        {
            EventTarget = eventSocket is null ? null : Serialize(eventSocket, EventEndPoint);
            GeneralTarget = generalSocket is null ? null : Serialize(generalSocket, GeneralEndPoint);
        }

        private int _silent;

        /// <summary>True when this call marked the peer silent (it was not already).</summary>
        public bool TryMarkSilent() => Interlocked.Exchange(ref _silent, 1) == 0;

        /// <summary>True when the peer was marked silent.</summary>
        public bool ClearSilent() => Volatile.Read(ref _silent) == 1 && Interlocked.Exchange(ref _silent, 0) == 1;

        /// <summary>True the first time a message of <paramref name="type"/> arrives from this peer.</summary>
        public bool MarkFirst(PtpMessageType type)
        {
            var bit = 1 << ((int)type & 0x0F);
            int seen;
            do
            {
                seen = Volatile.Read(ref _firstSeenTypes);
                if ((seen & bit) != 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _firstSeenTypes, seen | bit, seen) != seen);

            return true;
        }

        private static SocketAddress Serialize(Socket socket, IPEndPoint endpoint)
        {
            if (socket.AddressFamily == AddressFamily.InterNetworkV6 && endpoint.AddressFamily == AddressFamily.InterNetwork)
            {
                endpoint = new IPEndPoint(endpoint.Address.MapToIPv6(), endpoint.Port);
            }

            return UdpSockets.Target(socket, endpoint).Serialize();
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "PTP grandmaster {ClockId:X16} running on UDP {EventPort}/{GeneralPort}")]
    private partial void LogStarted(ulong clockId, int eventPort, int generalPort);

    [LoggerMessage(EventId = 201, Level = LogLevel.Debug, Message = "PTP grandmaster {ClockId:X16} stopped")]
    private partial void LogStopped(ulong clockId);

    [LoggerMessage(EventId = 202, Level = LogLevel.Error, Message = "PTP grandmaster cannot bind its {Loop} ({Port}): {Reason}")]
    private partial void LogBindFailed(Exception exception, string loop, string port, string reason);

    [LoggerMessage(EventId = 203, Level = LogLevel.Information, Message = "PTP peer {Address} added (event port {EventPort}, general port {GeneralPort})")]
    private partial void LogPeerAdded(IPAddress address, int eventPort, int generalPort);

    [LoggerMessage(EventId = 204, Level = LogLevel.Information, Message = "PTP peer {Address} removed")]
    private partial void LogPeerRemoved(IPAddress address);

    [LoggerMessage(EventId = 205, Level = LogLevel.Warning, Message = "PTP peer {Address} dropped: nothing received for {Timeout} ({Received} datagrams in total)")]
    private partial void LogPeerTimedOut(IPAddress address, TimeSpan timeout, long received);

    [LoggerMessage(EventId = 206, Level = LogLevel.Warning, Message = "PTP {Type} to {Target} could not be sent: {Error} (logged once until a send succeeds)")]
    private partial void LogSendFailed(Exception exception, PtpMessageType type, IPEndPoint target, SocketError error);

    [LoggerMessage(EventId = 207, Level = LogLevel.Information, Message = "PTP sends to {Address} succeed again")]
    private partial void LogSendRecovered(IPAddress address);

    [LoggerMessage(EventId = 208, Level = LogLevel.Warning, Message = "PTP {Type} answer to {Target} could not be sent: {Error}")]
    private partial void LogReplyFailed(Exception exception, PtpMessageType type, IPEndPoint target, SocketError error);

    [LoggerMessage(EventId = 209, Level = LogLevel.Debug, Message = "Ignored {Length}-byte non-PTPv2 datagram from {Source} on UDP {Port}")]
    private partial void LogNotPtp(int length, IPEndPoint source, int port);

    [LoggerMessage(EventId = 210, Level = LogLevel.Information, Message = "First PTP {Type} from peer {Source} (clock {ClockId:X16}, log interval {LogInterval})")]
    private partial void LogFirstMessage(PtpMessageType type, IPEndPoint source, ulong clockId, sbyte logInterval);

    [LoggerMessage(EventId = 211, Level = LogLevel.Debug, Message = "PTP Delay_Resp to {Target} for sequence {Sequence}")]
    private partial void LogDelayResp(IPEndPoint target, ushort sequence);

    [LoggerMessage(EventId = 212, Level = LogLevel.Information, Message = "PTP Announce from another clock {ClockId:X16} at {Source} (grandmaster {Grandmaster:X16}); ignored, we expect to stay master")]
    private partial void LogForeignAnnounce(IPEndPoint source, ulong clockId, ulong grandmaster);

    [LoggerMessage(EventId = 213, Level = LogLevel.Debug, Message = "PTP Signaling from {Source}: {Tlvs}")]
    private partial void LogSignaling(IPEndPoint source, string tlvs);

    [LoggerMessage(EventId = 214, Level = LogLevel.Debug, Message = "PTP {Type} from {Source} (clock {ClockId:X16}, sequence {Sequence}) ignored")]
    private partial void LogOtherMessage(PtpMessageType type, IPEndPoint source, ulong clockId, ushort sequence);

    [LoggerMessage(EventId = 217, Level = LogLevel.Warning, Message = "PTP peer {Address} silent: nothing received for {Timeout} ({Received} datagrams in total); still sending to it until it is removed")]
    private partial void LogPeerSilent(IPAddress address, TimeSpan timeout, long received);

    [LoggerMessage(EventId = 218, Level = LogLevel.Information, Message = "PTP peer {Address} sends again")]
    private partial void LogPeerSendsAgain(IPAddress address);

    [LoggerMessage(EventId = 216, Level = LogLevel.Debug, Message = "PTP {Type} from {Source} on UDP {Port}: clock {ClockId:X16}, sequence {Sequence}, log interval {LogInterval}")]
    private partial void LogReceived(PtpMessageType type, IPEndPoint source, int port, ulong clockId, ushort sequence, sbyte logInterval);

    [LoggerMessage(EventId = 215, Level = LogLevel.Error, Message = "The PTP send loop faulted")]
    private partial void LogSendLoopFaulted(Exception exception);

    [LoggerMessage(EventId = 219, Level = LogLevel.Error, Message = "PTP grandmaster {ClockId:X16}: a send-loop tick failed; continuing with the next tick (logged once until a tick succeeds)")]
    private partial void LogSendLoopTickFailed(Exception exception, ulong clockId);

    [LoggerMessage(EventId = 220, Level = LogLevel.Information, Message = "PTP grandmaster {ClockId:X16}: send-loop ticks succeed again")]
    private partial void LogSendLoopRecovered(ulong clockId);
}
