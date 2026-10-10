using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.FakeReceiver;

/// <summary>Options of <see cref="FakePtpSlave"/>.</summary>
public sealed record FakePtpSlaveOptions
{
    /// <summary>Local address of both sockets (loopback by default).</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Local event port; 0 lets the OS choose (the master's 319 is usually taken on the same host).</summary>
    public int EventPort { get; init; }

    /// <summary>Local general port; 0 lets the OS choose.</summary>
    public int GeneralPort { get; init; }

    /// <summary>The slave's clock identity (EUI-64 style, as receivers use).</summary>
    public ulong ClockId { get; init; } = 0x501E2DFFFE6DB69BUL;

    /// <summary>The slave's port number in its <c>sourcePortIdentity</c>.</summary>
    public ushort PortNumber { get; init; } = 1;

    /// <summary>Send a Delay_Req after a Follow_Up at most every <see cref="DelayRequestInterval"/> (open question P17/A-Q8: nqptp may not).</summary>
    public bool SendDelayRequests { get; init; }

    /// <summary>Minimum time between Delay_Reqs.</summary>
    public TimeSpan DelayRequestInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The slave's local clock in nanoseconds; a monotonic <see cref="Stopwatch"/> clock when null. Tests pass the master's
    /// clock plus a known offset so the estimated offset can be checked against the truth.
    /// </summary>
    public Func<ulong>? LocalClock { get; init; }

    /// <summary>Logger.</summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;
}

/// <summary>A PTP message the slave received.</summary>
/// <param name="Header">Its parsed header.</param>
/// <param name="Bytes">A copy of the datagram.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="LocalPort">The local port it arrived on.</param>
/// <param name="ReceivedAt">The slave's local clock at arrival.</param>
public sealed record FakePtpMessage(PtpHeader Header, byte[] Bytes, IPEndPoint Source, int LocalPort, ulong ReceivedAt);

/// <summary>One completed two-step exchange: Sync arrival <c>t2</c> (slave clock) and the Follow_Up's <c>t1</c> (master clock).</summary>
/// <param name="Sequence">The Sync/Follow_Up sequence id.</param>
/// <param name="T1">preciseOriginTimestamp in nanoseconds.</param>
/// <param name="T2">Sync arrival on the slave clock.</param>
/// <param name="MeanPathDelay">The path delay in effect (0 until a Delay_Resp arrived).</param>
public sealed record FakePtpSample(ushort Sequence, ulong T1, ulong T2, long MeanPathDelay)
{
    /// <summary>Slave minus master: <c>t2 - t1 - meanPathDelay</c> (IEEE 1588-2008 11.2).</summary>
    public long OffsetNanos => unchecked((long)(T2 - T1)) - MeanPathDelay;
}

/// <summary>
/// A minimal PTP slave for the fake receiver and the grandmaster's loopback tests: it records every
/// message, pairs each Sync with its Follow_Up to estimate the offset of its clock to the master's (as nqptp does from
/// <c>t1</c> and its Sync receive time, SPEC-audio.md 4.4), and optionally sends Delay_Req to the master's event port and
/// uses the Delay_Resp to measure the path delay. It runs on its own (OS-chosen) ports, so tests register it with
/// <see cref="PtpMaster.AddPeer(IPAddress, int, int)"/>.
/// </summary>
public sealed class FakePtpSlave : IAsyncDisposable
{
    private const int MaxMessages = 20_000;

    private readonly FakePtpSlaveOptions _options;
    private readonly ILogger _logger;
    private readonly Func<ulong> _clock;
    private readonly UdpReceiveLoop _eventLoop;
    private readonly UdpReceiveLoop _generalLoop;
    private readonly ConcurrentQueue<FakePtpMessage> _messages = new();
    private readonly ConcurrentQueue<FakePtpSample> _samples = new();
    private readonly ConcurrentDictionary<ushort, ulong> _syncArrivals = new();
    private readonly ConcurrentDictionary<ushort, ulong> _earlyFollowUps = new();
    private readonly ConcurrentDictionary<PtpMessageType, int> _counts = new();
    private readonly Lock _gate = new();
    private readonly byte[] _delayReq = new byte[PtpMessages.DelayReqSize];
    private ushort _delayReqSequence;
    private (ushort Sequence, ulong T3)? _pendingDelayReq;
    private ulong _lastDelayReqAt;
    private (ulong T1, ulong T2)? _lastPair;
    private long _meanPathDelay;
    private int _delayResponses;
    private int _mismatchedDelayResponses;
    private ulong _masterClockId;
    private IPEndPoint? _lastSyncSource;

    /// <summary>Creates the slave; nothing is bound until <see cref="StartAsync"/>.</summary>
    public FakePtpSlave(FakePtpSlaveOptions? options = null)
    {
        _options = options ?? new FakePtpSlaveOptions();
        _logger = _options.Logger;
        if (_options.LocalClock is { } clock)
        {
            _clock = clock;
        }
        else
        {
            var start = Stopwatch.GetTimestamp();
            _clock = () => (ulong)((Stopwatch.GetTimestamp() - start) * (1_000_000_000.0 / Stopwatch.Frequency));
        }

        _eventLoop = new UdpReceiveLoop("fake PTP slave event port", _logger, _options.BindAddress, _options.EventPort, HandleAsync);
        _generalLoop = new UdpReceiveLoop("fake PTP slave general port", _logger, _options.BindAddress, _options.GeneralPort, HandleAsync);
    }

    /// <summary>The bound event port.</summary>
    public int EventPort => _eventLoop.Port;

    /// <summary>The bound general port.</summary>
    public int GeneralPort => _generalLoop.Port;

    /// <summary>The slave's clock identity.</summary>
    public ulong ClockId => _options.ClockId;

    /// <summary>The master clock identity from the last Follow_Up or Announce; 0 before any.</summary>
    public ulong MasterClockId => Interlocked.Read(ref _masterClockId);

    /// <summary>Every message received, in order.</summary>
    public IReadOnlyCollection<FakePtpMessage> Messages => _messages;

    /// <summary>Completed Sync/Follow_Up pairs.</summary>
    public IReadOnlyCollection<FakePtpSample> Samples => _samples;

    /// <summary>The current mean path delay estimate (0 until a Delay_Resp arrived).</summary>
    public long MeanPathDelayNanos => Interlocked.Read(ref _meanPathDelay);

    /// <summary>Delay_Resp messages that matched an outstanding Delay_Req (sequence and requestingPortIdentity).</summary>
    public int DelayResponses => Volatile.Read(ref _delayResponses);

    /// <summary>Delay_Resp messages that did not match.</summary>
    public int MismatchedDelayResponses => Volatile.Read(ref _mismatchedDelayResponses);

    /// <summary>How many messages of a type arrived.</summary>
    public int Count(PtpMessageType type) => _counts.TryGetValue(type, out var n) ? n : 0;

    /// <summary>Binds both ports.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _eventLoop.Start(this);
        _generalLoop.Start(this);
        return Task.CompletedTask;
    }

    /// <summary>Closes both ports.</summary>
    public async ValueTask DisposeAsync()
    {
        await _eventLoop.DisposeAsync().ConfigureAwait(false);
        await _generalLoop.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask HandleAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop)
    {
        var receivedAt = _clock();
        if (!PtpHeader.TryRead(datagram.Span, out var header))
        {
            _logger.LogWarning("Fake PTP slave: ignored a {Length}-byte non-PTP datagram", datagram.Length);
            return;
        }

        var from = UdpSockets.ToEndPoint(source);
        var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        if (_messages.Count < MaxMessages)
        {
            _messages.Enqueue(new FakePtpMessage(header, datagram.ToArray(), from, localPort, receivedAt));
        }

        _counts.AddOrUpdate(header.MessageType, 1, static (_, n) => n + 1);
        switch (header.MessageType)
        {
            case PtpMessageType.Sync:
                Volatile.Write(ref _lastSyncSource, from);
                if (_earlyFollowUps.TryRemove(header.SequenceId, out var earlyT1))
                {
                    // The two sockets are read by separate loops: the Follow_Up may be processed first.
                    await CompleteAsync(header.SequenceId, earlyT1, receivedAt, stop).ConfigureAwait(false);
                }
                else
                {
                    _syncArrivals[header.SequenceId] = receivedAt;
                    Prune(_syncArrivals, header.SequenceId);
                }

                break;

            case PtpMessageType.FollowUp when PtpMessages.TryReadTimestamp(datagram.Span, out var t1):
                Interlocked.Exchange(ref _masterClockId, header.SourcePortIdentity.ClockId);
                if (_syncArrivals.TryRemove(header.SequenceId, out var t2))
                {
                    await CompleteAsync(header.SequenceId, t1.TotalNanoseconds, t2, stop).ConfigureAwait(false);
                }
                else
                {
                    _earlyFollowUps[header.SequenceId] = t1.TotalNanoseconds;
                    Prune(_earlyFollowUps, header.SequenceId);
                }

                break;

            case PtpMessageType.DelayResp when PtpMessages.TryReadTimestamp(datagram.Span, out var t4)
                                               && PtpMessages.TryReadRequestingPortIdentity(datagram.Span, out var requester):
                HandleDelayResp(header, t4, requester);
                break;

            case PtpMessageType.Announce when PtpMessages.TryReadGrandmasterIdentity(datagram.Span, out var grandmaster):
                Interlocked.Exchange(ref _masterClockId, grandmaster);
                break;
        }
    }

    private async ValueTask CompleteAsync(ushort sequence, ulong t1, ulong t2, CancellationToken stop)
    {
        lock (_gate)
        {
            _lastPair = (t1, t2);
        }

        _samples.Enqueue(new FakePtpSample(sequence, t1, t2, MeanPathDelayNanos));
        if (_options.SendDelayRequests)
        {
            // The master's event socket is where its Syncs come from: same address, its event port.
            var syncSource = Volatile.Read(ref _lastSyncSource);
            if (syncSource is not null)
            {
                await SendDelayRequestAsync(syncSource, stop).ConfigureAwait(false);
            }
        }
    }

    private static void Prune(ConcurrentDictionary<ushort, ulong> pending, ushort current)
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
        var now = _clock();
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

        var socket = _eventLoop.Socket;
        if (socket is null)
        {
            return;
        }

        PtpMessages.WriteDelayReq(_delayReq, _options.ClockId, _options.PortNumber, sequence, PtpTimestamp.Zero);
        var t3 = _clock();
        lock (_gate)
        {
            _pendingDelayReq = (sequence, t3);
        }

        await socket.SendToAsync(_delayReq, SocketFlags.None, UdpSockets.Target(socket, master), stop).ConfigureAwait(false);
    }

    private void HandleDelayResp(in PtpHeader header, PtpTimestamp t4, PtpPortIdentity requester)
    {
        lock (_gate)
        {
            if (_pendingDelayReq is not { } pending || pending.Sequence != header.SequenceId
                || requester != new PtpPortIdentity(_options.ClockId, _options.PortNumber) || _lastPair is not { } pair)
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
}
