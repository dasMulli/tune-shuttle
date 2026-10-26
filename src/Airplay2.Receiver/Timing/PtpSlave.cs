using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Timing;

/// <summary>Options of <see cref="PtpSlave"/> (SPEC-receiver.md 6.2).</summary>
public sealed record PtpSlaveOptions
{
    /// <summary>
    /// The shared PTP port (SPEC-receiver.md R5, 6.1): the slave registers the sender's addresses with it and sends from it.
    /// Null gives the slave a private <see cref="PtpPort"/> on <see cref="BindAddress"/>/<see cref="EventPort"/>/<see cref="GeneralPort"/>
    /// whose default handler it is (it then hears every message; the grandmaster's loopback tests run it that way).
    /// </summary>
    public IPtpPort? Port { get; init; }

    /// <summary>Local address of the private port (loopback by default); unused with <see cref="Port"/>.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Local event port of the private port; 0 lets the OS choose (the master's 319 is usually taken on the same host).</summary>
    public int EventPort { get; init; }

    /// <summary>Local general port of the private port; 0 lets the OS choose.</summary>
    public int GeneralPort { get; init; }

    /// <summary>
    /// The sender's addresses known at start (SPEC-receiver.md 6.2: session SETUP <c>timingPeerInfo.Addresses</c> and the
    /// control connection's remote address); <see cref="PtpSlave.AddSenderAddress"/> adds SETPEERS additions later.
    /// </summary>
    public IReadOnlyList<IPAddress> SenderAddresses { get; init; } = [];

    /// <summary>
    /// The sender's event port, where a Delay_Req goes (SPEC-receiver.md 6.2: 319); 0 sends it to the port the Sync came
    /// from (the fake receiver's slave next to a master on OS-chosen ports).
    /// </summary>
    public int PeerEventPort { get; init; } = PtpMessages.EventPort;

    /// <summary>The sender's general port, where the start-up Announces go (SPEC-receiver.md 6.2: 320; loopback tests set the test master's port).</summary>
    public int PeerGeneralPort { get; init; } = PtpMessages.GeneralPort;

    /// <summary>
    /// The slave's clock identity (EUI-64, as receivers use). A product receiver derives it from its R11 identity with
    /// <see cref="PtpSlave.ClockIdFromDeviceId"/> (SPEC-receiver.md 6.2); null picks a random locally-administered EUI-64.
    /// </summary>
    public ulong? ClockId { get; init; }

    /// <summary>The slave's port number in its <c>sourcePortIdentity</c>.</summary>
    public ushort PortNumber { get; init; } = 1;

    /// <summary>
    /// Send a Delay_Req after a Follow_Up at most every <see cref="DelayRequestInterval"/> (open question P17/A-Q8: nqptp
    /// does not); off by default, the passive slave of SPEC-receiver.md R5 (RQ5).
    /// </summary>
    public bool SendDelayRequests { get; init; }

    /// <summary>Minimum time between Delay_Reqs.</summary>
    public TimeSpan DelayRequestInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Sync/Follow_Up pairs in the sliding minimum window (SPEC-receiver.md 6.2: 8).</summary>
    public int WindowSize { get; init; } = 8;

    /// <summary>
    /// Announces of the slave's own clock sent to each sender address at session start (SPEC-receiver.md R5, 6.2: two, as
    /// nqptp sends, SPEC-audio.md 4.4 note); 0 sends none.
    /// </summary>
    public int StartAnnounces { get; init; } = 2;

    /// <summary>Gap between the start-up Announces (nqptp: about 150 ms, SPEC-audio.md 4.4 note).</summary>
    public TimeSpan StartAnnounceGap { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Keep a copy of every received message in <see cref="PtpSlave.Messages"/> (tests and the fake receiver); off in the product.</summary>
    public bool RecordMessages { get; init; }

    /// <summary>
    /// The slave's local clock (SPEC-receiver.md 6.2); a <see cref="MonotonicClock.CreateStopwatch"/> clock when null. The
    /// product passes <see cref="MonotonicClock.FromMasterClock"/> of the process clock; tests pass the master's clock plus
    /// a known offset so the estimated offset can be checked against the truth.
    /// </summary>
    public IMonotonicClock? LocalClock { get; init; }
}

/// <summary>A PTP message a <see cref="PtpSlave"/> received (SPEC-receiver.md 6.2).</summary>
/// <param name="Header">Its parsed header.</param>
/// <param name="Bytes">A copy of the datagram.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="LocalPort">The local port it arrived on.</param>
/// <param name="ReceivedAt">The slave's local clock at arrival.</param>
public sealed record PtpSlaveMessage(PtpHeader Header, byte[] Bytes, IPEndPoint Source, int LocalPort, ulong ReceivedAt);

/// <summary>One completed two-step exchange of a <see cref="PtpSlave"/> (SPEC-receiver.md 6.2): Sync arrival <c>t2</c> (slave clock) and the Follow_Up's <c>t1</c> (master clock).</summary>
/// <param name="Sequence">The Sync/Follow_Up sequence id.</param>
/// <param name="T1">preciseOriginTimestamp in nanoseconds.</param>
/// <param name="T2">Sync arrival on the slave clock.</param>
/// <param name="MeanPathDelay">The path delay in effect (0 until a Delay_Resp arrived).</param>
public sealed record PtpSlaveSample(ushort Sequence, ulong T1, ulong T2, long MeanPathDelay)
{
    /// <summary>Slave minus master: <c>t2 - t1 - meanPathDelay</c> (IEEE 1588-2008 11.2).</summary>
    public long OffsetNanos => unchecked((long)(T2 - T1)) - MeanPathDelay;
}

/// <summary>
/// The receiver's PTP slave of one sender session (SPEC-receiver.md R5, 6.2), promoted from the fake receiver's
/// <c>FakePtpSlave</c>. It pairs each two-step Sync with its Follow_Up, takes <c>offset = t2 − t1</c> (minus the path
/// delay once a Delay_Resp measured one) and estimates the offset of its clock to the sender's grandmaster as the
/// <b>minimum over a sliding window of 8 pairs</b> (network delay only adds, as nqptp assumes, SPEC-audio.md 4.4); the
/// window starts again when the grandmaster identity of the Follow_Ups changes. <see cref="TryMapToLocal"/> maps sender
/// time onto the local monotonic clock with that estimate.
/// </summary>
/// <remarks>
/// <para>
/// Sockets: on the shared <see cref="IPtpPort"/> (<see cref="PtpSlaveOptions.Port"/>) the slave registers each sender
/// address, so the port routes the sender's Sync, Follow_Up, Announce, Signaling and Delay_Resp to it (6.1); without one
/// it runs a private port and is its default handler. At start it sends two Announces of its own clock (priority 248,
/// class 248: it never wins a best-master election) to every sender address at <see cref="PtpSlaveOptions.PeerGeneralPort"/>,
/// as nqptp does; it sends nothing else unless <see cref="PtpSlaveOptions.SendDelayRequests"/> is on (RQ5).
/// </para>
/// <para>
/// Threads: the port's two receive loops call <see cref="HandleAsync"/> concurrently (the Follow_Up may be processed
/// before its Sync); the estimate is guarded by one lock.
/// </para>
/// </remarks>
public sealed partial class PtpSlave : IPtpSlave, IPtpEndpoint
{
    private const int MaxMessages = 20_000;
    private const int MaxSamples = 4_096;

    /// <summary>The priority and clock class of the slave's Announces: the lowest a master may have (nqptp's "slave only" values).</summary>
    private const byte SlavePriority = 248;
    private const sbyte StartAnnounceLogInterval = -2;

    private readonly PtpSlaveOptions _options;
    private readonly ILogger _logger;
    private readonly IMonotonicClock _clock;
    private readonly IPtpPort _port;
    private readonly bool _ownsPort;
    private readonly ConcurrentQueue<PtpSlaveMessage> _messages = new();
    private readonly ConcurrentQueue<PtpSlaveSample> _samples = new();
    private readonly ConcurrentDictionary<ushort, (ulong T2, ulong Clock)> _syncArrivals = new();
    private readonly ConcurrentDictionary<ushort, (ulong T1, ulong Clock)> _earlyFollowUps = new();
    private readonly ConcurrentDictionary<PtpMessageType, int> _counts = new();
    // Routing key (unscoped, SPEC-receiver.md 6.1) -> send target (scoped for IPv6 link-local, so the Announces go out).
    private readonly Dictionary<IPAddress, IPAddress> _senders = [];
    private readonly long _linkLocalScope;
    private readonly List<Task> _announceTasks = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly byte[] _delayReq = new byte[PtpMessages.DelayReqSize];
    private readonly long[] _window;
    private int _windowCount;
    private int _windowNext;
    private ulong _windowClock;
    private int _rebases;
    private int _pairsSeen;
    private ushort _delayReqSequence;
    private (ushort Sequence, ulong T3)? _pendingDelayReq;
    private ulong _lastDelayReqAt;
    private (ulong T1, ulong T2)? _lastPair;
    private long _meanPathDelay;
    private int _delayResponses;
    private int _mismatchedDelayResponses;
    private int _announcesSent;
    private ulong _masterClockId;
    private ulong _announcedGrandmaster;
    private int _announcedPriority1 = -1;
    private ulong _lastSyncAt;
    private IPEndPoint? _lastSyncSource;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the slave; nothing is bound or registered until <see cref="StartAsync"/> (SPEC-receiver.md 6.2).</summary>
    /// <param name="options">Options; the defaults when null.</param>
    /// <param name="logger">Logger.</param>
    public PtpSlave(PtpSlaveOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new PtpSlaveOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.WindowSize, 1, "options.WindowSize");
        ArgumentOutOfRangeException.ThrowIfNegative(_options.StartAnnounces, "options.StartAnnounces");
        ArgumentOutOfRangeException.ThrowIfNegative(_options.PeerEventPort, "options.PeerEventPort");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.PeerEventPort, IPEndPoint.MaxPort, "options.PeerEventPort");
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.PeerGeneralPort, 1, "options.PeerGeneralPort");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.PeerGeneralPort, IPEndPoint.MaxPort, "options.PeerGeneralPort");
        _logger = logger ?? NullLogger.Instance;
        _clock = _options.LocalClock ?? MonotonicClock.CreateStopwatch();
        ClockId = _options.ClockId ?? RandomClockId();
        _window = new long[_options.WindowSize];
        if (_options.Port is { } shared)
        {
            _port = shared;
        }
        else
        {
            _port = new PtpPort(new PtpPortOptions { BindAddress = _options.BindAddress, EventPort = _options.EventPort, GeneralPort = _options.GeneralPort });
            _ownsPort = true;
        }

        // A plist-supplied link-local address carries no scope; borrow the one of the control connection's remote address.
        _linkLocalScope = _options.SenderAddresses.FirstOrDefault(a => a.IsIPv6LinkLocal && a.ScopeId != 0)?.ScopeId ?? 0;
        foreach (var address in _options.SenderAddresses)
        {
            AddSenderLocked(address);
        }
    }

    /// <summary>The bound event port of the slave's port (SPEC-receiver.md 6.1).</summary>
    public int EventPort => _port.EventPort;

    /// <summary>The bound general port of the slave's port (SPEC-receiver.md 6.1).</summary>
    public int GeneralPort => _port.GeneralPort;

    /// <summary>The port the slave receives and sends on: the shared one or its private one (SPEC-receiver.md 6.1).</summary>
    public IPtpPort Port => _port;

    /// <inheritdoc />
    public ulong ClockId { get; }

    /// <inheritdoc />
    public ulong MasterClockId => Interlocked.Read(ref _masterClockId);

    /// <summary>The grandmasterIdentity of the sender's last Announce; 0 before any (SPEC-receiver.md 6.2: Announces are recorded).</summary>
    public ulong AnnouncedGrandmaster => Interlocked.Read(ref _announcedGrandmaster);

    /// <summary>The grandmasterPriority1 of the sender's last Announce; null before any (SPEC-receiver.md 6.2).</summary>
    public byte? AnnouncedPriority1 => Volatile.Read(ref _announcedPriority1) is var p and >= 0 ? (byte)p : null;

    /// <summary>
    /// Every message received, in order, while <see cref="PtpSlaveOptions.RecordMessages"/> is on (at most 20 000);
    /// empty otherwise.
    /// </summary>
    public IReadOnlyCollection<PtpSlaveMessage> Messages => _messages;

    /// <summary>The most recent completed Sync/Follow_Up pairs (at most 4 096), oldest first.</summary>
    public IReadOnlyCollection<PtpSlaveSample> Samples => _samples;

    /// <summary>The current mean path delay estimate (0 until a Delay_Resp arrived).</summary>
    public long MeanPathDelayNanos => Interlocked.Read(ref _meanPathDelay);

    /// <summary>Delay_Resp messages that matched an outstanding Delay_Req (sequence and requestingPortIdentity).</summary>
    public int DelayResponses => Volatile.Read(ref _delayResponses);

    /// <summary>Delay_Resp messages that did not match.</summary>
    public int MismatchedDelayResponses => Volatile.Read(ref _mismatchedDelayResponses);

    /// <summary>Announces of the slave's own clock sent so far (SPEC-receiver.md 6.2: two per sender address at start).</summary>
    public int AnnouncesSent => Volatile.Read(ref _announcesSent);

    /// <summary>How often the estimate started again because the grandmaster identity changed (SPEC-receiver.md 6.2).</summary>
    public int Rebases => Volatile.Read(ref _rebases);

    /// <summary>The current estimate of slave minus master in nanoseconds (the window minimum), or null before the first pair (SPEC-receiver.md 6.2).</summary>
    public long? OffsetNanos
    {
        get
        {
            lock (_gate)
            {
                return EstimateLocked();
            }
        }
    }

    /// <summary>
    /// The snapshot for Diagnostics (SPEC-receiver.md 6.2): the windowed-minimum offset, pairs seen, the age of the last
    /// Sync and the grandmaster id.
    /// </summary>
    public PtpSlaveStatistics Statistics
    {
        get
        {
            long? offset;
            lock (_gate)
            {
                offset = EstimateLocked();
            }

            var lastSyncAt = Volatile.Read(ref _lastSyncAt);
            var now = _clock.NowNanoseconds;
            var age = lastSyncAt == 0 ? (TimeSpan?)null : TimeSpan.FromTicks((long)((now > lastSyncAt ? now - lastSyncAt : 0) / 100));
            return new PtpSlaveStatistics(offset, Volatile.Read(ref _pairsSeen), age, MasterClockId);
        }
    }

    /// <summary>
    /// The EUI-64 clock identity of a receiver identity (SPEC-receiver.md 6.2, R11): the 48-bit <c>deviceid</c> MAC with
    /// <c>FF:FE</c> inserted in the middle, the shape receivers announce (SPEC-audio.md 4.4 note: <c>…:FF:FE:…</c>).
    /// </summary>
    /// <param name="deviceId">The <c>deviceid</c>, six hex bytes separated by colons (<see cref="ReceiverIdentity.DeviceId"/>).</param>
    /// <exception cref="FormatException">The value is not six colon-separated hex bytes.</exception>
    public static ulong ClockIdFromDeviceId(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        var parts = deviceId.Split(':');
        if (parts.Length != 6)
        {
            throw new FormatException($"'{deviceId}' is not a MAC address of six hex bytes.");
        }

        Span<byte> mac = stackalloc byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out mac[i]))
            {
                throw new FormatException($"'{deviceId}' is not a MAC address of six hex bytes.");
            }
        }

        return ((ulong)mac[0] << 56) | ((ulong)mac[1] << 48) | ((ulong)mac[2] << 40) | (0xFFFEUL << 24)
            | ((ulong)mac[3] << 16) | ((ulong)mac[4] << 8) | mac[5];
    }

    /// <summary>How many messages of a type arrived.</summary>
    public int Count(PtpMessageType type) => _counts.TryGetValue(type, out var n) ? n : 0;

    /// <inheritdoc />
    public bool TryMapToLocal(ulong senderNanos, out ulong localNanos)
    {
        long? offset;
        lock (_gate)
        {
            offset = EstimateLocked();
        }

        if (offset is not { } o)
        {
            localNanos = 0;
            return false;
        }

        localNanos = unchecked(senderNanos + (ulong)o);
        return true;
    }

    /// <summary>
    /// Binds the port if nobody did yet (<see cref="IPtpPort.EnsureBoundAsync"/>), registers the sender's addresses (or
    /// becomes the private port's default handler) and sends the start-up Announces (SPEC-receiver.md 6.2); idempotent.
    /// </summary>
    /// <exception cref="PtpPortException">The port could not be bound; the receiver runs anchor-only (R5, R6).</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }
        }

        await _port.EnsureBoundAsync(cancellationToken).ConfigureAwait(false);
        IPAddress[] senders;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            senders = [.. _senders.Keys];
            if (_ownsPort)
            {
                _port.SetDefaultHandler(this);
            }
            else
            {
                foreach (var address in senders)
                {
                    _port.Register(address, this);
                }
            }

            foreach (var target in _senders.Values)
            {
                StartAnnouncesLocked(target);
            }
        }

        LogStarted(ClockId, EventPort, GeneralPort, senders.Length);
    }

    /// <summary>
    /// Adds a sender address (SPEC-receiver.md 6.2: a SETPEERS addition); after <see cref="StartAsync"/> it is registered
    /// with the shared port at once and gets the start-up Announces. Adding a known address does nothing.
    /// </summary>
    /// <param name="address">The sender's address.</param>
    public void AddSenderAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var key = PtpPortKey(address);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!AddSenderLocked(address) || !_started)
            {
                return;
            }

            if (!_ownsPort)
            {
                _port.Register(key, this);
            }

            StartAnnouncesLocked(_senders[key]);
        }
    }

    /// <summary>Unregisters from the shared port (only where this slave is still the handler) or closes the private port; idempotent (SPEC-receiver.md 6.1).</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] announces;
        IPAddress[] senders;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            announces = [.. _announceTasks];
            senders = [.. _senders.Keys];
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(announces).ConfigureAwait(false);
        if (_ownsPort)
        {
            await _port.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            foreach (var address in senders)
            {
                _port.Unregister(address, this);
            }
        }

        _stop.Dispose();
    }

    /// <summary>
    /// A datagram the port routed to the slave (SPEC-receiver.md 6.1, 6.2): Sync and Follow_Up feed the estimate, an
    /// Announce is recorded, a Delay_Resp completes the optional path-delay measurement.
    /// </summary>
    /// <param name="header">The parsed header.</param>
    /// <param name="datagram">The whole datagram; valid only during the call.</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="eventPort">True for the event port.</param>
    /// <param name="receivedAtNanos">Arrival on <see cref="PtpPort.NowNanos"/>.</param>
    /// <param name="cancellationToken">Cancelled when the port closes.</param>
    public async ValueTask HandleAsync(PtpHeader header, ReadOnlyMemory<byte> datagram, IPEndPoint source, bool eventPort, ulong receivedAtNanos, CancellationToken cancellationToken)
    {
        // t2 on the local clock, moved back by the time the datagram waited in the port before this call.
        var receivedAt = _clock.NowNanoseconds - PtpPort.ElapsedSince(receivedAtNanos);
        if (Volatile.Read(ref _disposed) || header.SourcePortIdentity.ClockId == ClockId)
        {
            return; // our own Announce reflected, or a late datagram after disposal
        }

        if (_options.RecordMessages && _messages.Count < MaxMessages)
        {
            _messages.Enqueue(new PtpSlaveMessage(header, datagram.ToArray(), source, eventPort ? EventPort : GeneralPort, receivedAt));
        }

        _counts.AddOrUpdate(header.MessageType, 1, static (_, n) => n + 1);
        var clock = header.SourcePortIdentity.ClockId;
        switch (header.MessageType)
        {
            case PtpMessageType.Sync:
                Volatile.Write(ref _lastSyncSource, source);
                Volatile.Write(ref _lastSyncAt, receivedAt);
                if (_earlyFollowUps.TryRemove(header.SequenceId, out var early) && early.Clock == clock)
                {
                    // The two sockets are read by separate loops: the Follow_Up may be processed first.
                    await CompleteAsync(header.SequenceId, clock, early.T1, receivedAt, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _syncArrivals[header.SequenceId] = (receivedAt, clock);
                    Prune(_syncArrivals, header.SequenceId);
                }

                break;

            case PtpMessageType.FollowUp when PtpMessages.TryReadTimestamp(datagram.Span, out var t1):
                Interlocked.Exchange(ref _masterClockId, clock);
                if (_syncArrivals.TryRemove(header.SequenceId, out var sync) && sync.Clock == clock)
                {
                    await CompleteAsync(header.SequenceId, clock, t1.TotalNanoseconds, sync.T2, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _earlyFollowUps[header.SequenceId] = (t1.TotalNanoseconds, clock);
                    Prune(_earlyFollowUps, header.SequenceId);
                }

                break;

            case PtpMessageType.DelayResp when PtpMessages.TryReadTimestamp(datagram.Span, out var t4)
                                               && PtpMessages.TryReadRequestingPortIdentity(datagram.Span, out var requester):
                HandleDelayResp(header, t4, requester);
                break;

            case PtpMessageType.Announce when PtpMessages.TryReadGrandmasterIdentity(datagram.Span, out var grandmaster):
                Interlocked.Exchange(ref _announcedGrandmaster, grandmaster);
                Volatile.Write(ref _announcedPriority1, datagram.Span[PtpHeader.Size + 13]);
                Interlocked.Exchange(ref _masterClockId, grandmaster);
                break;
        }
    }

    /// <summary>The send targets of the start-up Announces (SPEC-receiver.md 6.2), for tests.</summary>
    internal IReadOnlyList<IPAddress> AnnounceTargets
    {
        get
        {
            lock (_gate)
            {
                return [.. _senders.Values];
            }
        }
    }

    /// <summary>
    /// The address the Announces go to (SPEC-receiver.md 6.2): IPv4-mapped unmapped; an IPv6 link-local address without a
    /// scope gets <paramref name="linkLocalScope"/> (the control connection's), since an unscoped link-local target fails to send.
    /// </summary>
    internal static IPAddress SendTarget(IPAddress address, long linkLocalScope)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        return address.IsIPv6LinkLocal && address.ScopeId == 0 && linkLocalScope != 0
            ? new IPAddress(address.GetAddressBytes(), linkLocalScope)
            : address;
    }

    /// <summary>Records a sender address; true when its routing key is new. A scoped target replaces an unscoped one of the same key.</summary>
    private bool AddSenderLocked(IPAddress address)
    {
        var key = PtpPortKey(address);
        var target = SendTarget(address, _linkLocalScope);
        if (_senders.TryGetValue(key, out var known))
        {
            if (known.AddressFamily == AddressFamily.InterNetworkV6 && known.IsIPv6LinkLocal && known.ScopeId == 0 && target.ScopeId != 0)
            {
                _senders[key] = target;
            }

            return false;
        }

        _senders.Add(key, target);
        return true;
    }

    private static IPAddress PtpPortKey(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0 ? new IPAddress(address.GetAddressBytes()) : address;
    }

    private static ulong RandomClockId()
    {
        Span<byte> mac = stackalloc byte[6];
        RandomNumberGenerator.Fill(mac);
        mac[0] = (byte)((mac[0] & 0xFC) | 0x02); // locally administered, unicast (as R11's deviceid)
        return ((ulong)mac[0] << 56) | ((ulong)mac[1] << 48) | ((ulong)mac[2] << 40) | (0xFFFEUL << 24)
            | ((ulong)mac[3] << 16) | ((ulong)mac[4] << 8) | mac[5];
    }

    private long? EstimateLocked()
    {
        if (_windowCount == 0)
        {
            return null;
        }

        var min = long.MaxValue;
        for (var i = 0; i < _windowCount; i++)
        {
            min = Math.Min(min, _window[i]);
        }

        return min;
    }

    private async ValueTask CompleteAsync(ushort sequence, ulong clock, ulong t1, ulong t2, CancellationToken stop)
    {
        var sample = new PtpSlaveSample(sequence, t1, t2, MeanPathDelayNanos);
        bool rebased;
        ulong previous;
        lock (_gate)
        {
            previous = _windowClock;
            rebased = previous != 0 && previous != clock;
            if (rebased || previous == 0)
            {
                // SPEC-receiver.md 6.2: a new grandmaster's timeline has nothing to do with the old one; start again.
                _windowCount = 0;
                _windowNext = 0;
                _windowClock = clock;
                _lastPair = null;
                _pendingDelayReq = null;
            }

            _window[_windowNext] = sample.OffsetNanos;
            _windowNext = (_windowNext + 1) % _window.Length;
            _windowCount = Math.Min(_windowCount + 1, _window.Length);
            _lastPair = (t1, t2);
        }

        if (rebased)
        {
            Interlocked.Exchange(ref _meanPathDelay, 0);
            Interlocked.Increment(ref _rebases);
            LogRebased(previous, clock);
        }

        Interlocked.Increment(ref _pairsSeen);
        _samples.Enqueue(sample);
        while (_samples.Count > MaxSamples && _samples.TryDequeue(out _))
        {
        }

        if (_options.SendDelayRequests && Volatile.Read(ref _lastSyncSource) is { } syncSource)
        {
            var target = _options.PeerEventPort == 0 ? syncSource : new IPEndPoint(syncSource.Address, _options.PeerEventPort);
            await SendDelayRequestAsync(target, stop).ConfigureAwait(false);
        }
    }

    private static void Prune<T>(ConcurrentDictionary<ushort, T> pending, ushort current)
    {
        if (pending.Count > 64)
        {
            foreach (var key in pending.Keys.Where(k => (ushort)(current - k) > 32).ToList())
            {
                pending.TryRemove(key, out _);
            }
        }
    }

    private async ValueTask SendDelayRequestAsync(IPEndPoint master, CancellationToken stop)
    {
        var now = _clock.NowNanoseconds;
        ushort sequence;
        lock (_gate)
        {
            var interval = (ulong)(_options.DelayRequestInterval.TotalMilliseconds * 1_000_000);
            if (_lastDelayReqAt != 0 && now - _lastDelayReqAt < interval)
            {
                return;
            }

            _lastDelayReqAt = now;
            sequence = _delayReqSequence++;
        }

        if (!_port.IsBound)
        {
            return;
        }

        PtpMessages.WriteDelayReq(_delayReq, ClockId, _options.PortNumber, sequence, PtpTimestamp.Zero);
        var t3 = _clock.NowNanoseconds;
        lock (_gate)
        {
            _pendingDelayReq = (sequence, t3);
        }

        try
        {
            await _port.SendAsync(master, _delayReq, eventPort: true, stop).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            LogSendFailed(PtpMessageType.DelayReq, master, ex.SocketErrorCode);
        }
    }

    private void HandleDelayResp(in PtpHeader header, PtpTimestamp t4, PtpPortIdentity requester)
    {
        lock (_gate)
        {
            if (_pendingDelayReq is not { } pending || pending.Sequence != header.SequenceId
                || requester != new PtpPortIdentity(ClockId, _options.PortNumber) || _lastPair is not { } pair)
            {
                Interlocked.Increment(ref _mismatchedDelayResponses);
                return;
            }

            _pendingDelayReq = null;

            // meanPathDelay = ((t2 - t1) + (t4 - t3)) / 2 (IEEE 1588-2008 11.3); loopback makes it tiny, never negative in theory.
            var delay = (unchecked((long)(pair.T2 - pair.T1)) + unchecked((long)(t4.TotalNanoseconds - pending.T3))) / 2;
            Interlocked.Exchange(ref _meanPathDelay, Math.Max(0, delay));
            Interlocked.Increment(ref _delayResponses);
        }
    }

    /// <summary>Starts the start-up Announces to one address; called under <see cref="_gate"/> after start.</summary>
    private void StartAnnouncesLocked(IPAddress address)
    {
        if (_options.StartAnnounces > 0)
        {
            _announceTasks.RemoveAll(static t => t.IsCompleted);
            _announceTasks.Add(SendStartAnnouncesAsync(new IPEndPoint(address, _options.PeerGeneralPort), _stop.Token));
        }
    }

    /// <summary>
    /// The start-up Announces of SPEC-receiver.md R5/6.2, shaped as nqptp's (SPEC-audio.md 4.4 note: its own clock, sequence 0,
    /// log interval -2, about 150 ms apart): our clock as grandmaster with priority 248 and class 248, so the sender keeps
    /// its own grandmaster.
    /// </summary>
    private async Task SendStartAnnouncesAsync(IPEndPoint target, CancellationToken stop)
    {
        var message = new byte[PtpMessages.AnnounceSize];
        PtpMessages.WriteAnnounce(message, ClockId, 0, StartAnnounceLogInterval);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(28), _options.PortNumber); // sourcePortIdentity.portNumber
        message[PtpHeader.Size + 13] = SlavePriority; // grandmasterPriority1
        message[PtpHeader.Size + 14] = SlavePriority; // grandmasterClockQuality.clockClass
        message[PtpHeader.Size + 18] = SlavePriority; // grandmasterPriority2
        try
        {
            for (var i = 0; i < _options.StartAnnounces; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(_options.StartAnnounceGap, stop).ConfigureAwait(false);
                }

                try
                {
                    await _port.SendAsync(target, message, eventPort: false, stop).ConfigureAwait(false);
                    Interlocked.Increment(ref _announcesSent);
                }
                catch (SocketException ex)
                {
                    LogSendFailed(PtpMessageType.Announce, target, ex.SocketErrorCode);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException ex)
        {
            // The shared port was disposed by its owner before the session ended.
            LogAnnounceFailed(ex, target);
        }
    }

    [LoggerMessage(EventId = 600, Level = LogLevel.Information, Message = "PTP slave {ClockId:X16} following on UDP {EventPort}/{GeneralPort} ({Senders} sender address(es))")]
    private partial void LogStarted(ulong clockId, int eventPort, int generalPort, int senders);

    [LoggerMessage(EventId = 601, Level = LogLevel.Information, Message = "PTP slave: grandmaster changed from {Previous:X16} to {Current:X16}; offset estimate restarted")]
    private partial void LogRebased(ulong previous, ulong current);

    [LoggerMessage(EventId = 602, Level = LogLevel.Debug, Message = "PTP slave: {Type} to {Target} could not be sent: {Error}")]
    private partial void LogSendFailed(PtpMessageType type, IPEndPoint target, SocketError error);

    [LoggerMessage(EventId = 603, Level = LogLevel.Warning, Message = "PTP slave: start-up Announces to {Target} stopped")]
    private partial void LogAnnounceFailed(Exception exception, IPEndPoint target);
}
