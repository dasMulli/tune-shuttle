using System.Collections;
using System.Net;
using Airplay2.Receiver.Timing;
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
    /// The slave's local clock in nanoseconds; a monotonic <see cref="System.Diagnostics.Stopwatch"/> clock when null. Tests
    /// pass the master's clock plus a known offset so the estimated offset can be checked against the truth.
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
/// A minimal PTP slave for the fake receiver and the grandmaster's loopback tests: the library's <see cref="PtpSlave"/>
/// (SPEC-receiver.md 6.2), which records every message, pairs each Sync with its Follow_Up to estimate the offset of its
/// clock to the master's (as nqptp does from <c>t1</c> and its Sync receive time, SPEC-audio.md 4.4), and optionally sends
/// Delay_Req to the master's event port and uses the Delay_Resp to measure the path delay. It runs on its own
/// (OS-chosen) ports, so tests register it with <see cref="PtpMaster.AddPeer(IPAddress, int, int)"/>. Kept with the
/// fake's names so the tests written against it stay unchanged.
/// </summary>
public sealed class FakePtpSlave : IAsyncDisposable
{
    private readonly PtpSlave _slave;

    /// <summary>Creates the slave; nothing is bound until <see cref="StartAsync"/>.</summary>
    public FakePtpSlave(FakePtpSlaveOptions? options = null)
    {
        options ??= new FakePtpSlaveOptions();
        _slave = new PtpSlave(
            new PtpSlaveOptions
            {
                BindAddress = options.BindAddress,
                EventPort = options.EventPort,
                GeneralPort = options.GeneralPort,
                ClockId = options.ClockId,
                PortNumber = options.PortNumber,
                SendDelayRequests = options.SendDelayRequests,
                DelayRequestInterval = options.DelayRequestInterval,
                LocalClock = options.LocalClock is { } clock ? MonotonicClock.FromDelegate(clock) : null,
            },
            options.Logger);
        Messages = new MappedCollection<PtpSlaveMessage, FakePtpMessage>(
            _slave.Messages,
            static m => new FakePtpMessage(m.Header, m.Bytes, m.Source, m.LocalPort, m.ReceivedAt));
        Samples = new MappedCollection<PtpSlaveSample, FakePtpSample>(
            _slave.Samples,
            static s => new FakePtpSample(s.Sequence, s.T1, s.T2, s.MeanPathDelay));
    }

    /// <summary>The bound event port.</summary>
    public int EventPort => _slave.EventPort;

    /// <summary>The bound general port.</summary>
    public int GeneralPort => _slave.GeneralPort;

    /// <summary>The slave's clock identity.</summary>
    public ulong ClockId => _slave.ClockId;

    /// <summary>The master clock identity from the last Follow_Up or Announce; 0 before any.</summary>
    public ulong MasterClockId => _slave.MasterClockId;

    /// <summary>Every message received, in order.</summary>
    public IReadOnlyCollection<FakePtpMessage> Messages { get; }

    /// <summary>Completed Sync/Follow_Up pairs.</summary>
    public IReadOnlyCollection<FakePtpSample> Samples { get; }

    /// <summary>The current mean path delay estimate (0 until a Delay_Resp arrived).</summary>
    public long MeanPathDelayNanos => _slave.MeanPathDelayNanos;

    /// <summary>Delay_Resp messages that matched an outstanding Delay_Req (sequence and requestingPortIdentity).</summary>
    public int DelayResponses => _slave.DelayResponses;

    /// <summary>Delay_Resp messages that did not match.</summary>
    public int MismatchedDelayResponses => _slave.MismatchedDelayResponses;

    /// <summary>How many messages of a type arrived.</summary>
    public int Count(PtpMessageType type) => _slave.Count(type);

    /// <summary>Binds both ports.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => _slave.StartAsync(cancellationToken);

    /// <summary>Closes both ports.</summary>
    public ValueTask DisposeAsync() => _slave.DisposeAsync();

    /// <summary>A live view of the library's collection, each item converted on enumeration.</summary>
    private sealed class MappedCollection<TSource, TResult>(IReadOnlyCollection<TSource> source, Func<TSource, TResult> map) : IReadOnlyCollection<TResult>
    {
        public int Count => source.Count;

        public IEnumerator<TResult> GetEnumerator()
        {
            foreach (var item in source)
            {
                yield return map(item);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
