using Airplay2.Receiver.Timing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Playout;

/// <summary>
/// The one place where decoded frames wait (SPEC-receiver.md 5.3): both stream readers <see cref="Enqueue"/> S16 stereo
/// frames with their RTP timestamp; the anchor (SETRATEANCHORTIME or 0xD7) and the PTP offset (<see cref="IPtpSlave.TryMapToLocal"/>,
/// else the anchor-only mapping of R6) give each frame its play time <c>T</c>; one release thread writes a frame to the
/// <see cref="IReceiverAudioOutput"/> when <c>now &gt;= T - Lpipe + D</c> (R6), learns <c>D</c> from the first frame after
/// a (re-)anchor, drops late frames, fills gaps with silence, corrects drift and resumes a paused output before the first
/// frame (5.1, 5.5).
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the behaviour is the wave 2 <c>playout</c> package (SPEC-receiver.md 13).
/// </remarks>
public sealed class PlayoutBuffer : IAsyncDisposable
{
    private readonly ILogger _logger;

    /// <summary>Creates the buffer; the release thread starts with <see cref="StartAsync"/>.</summary>
    /// <param name="output">Where released frames go (SPEC-receiver.md 5.5).</param>
    /// <param name="clock">The process's one clock as an <see cref="IMonotonicClock"/> (SPEC-receiver.md 5.3: the pacer's timeline).</param>
    /// <param name="slave">The session's PTP slave for <see cref="IPtpSlave.TryMapToLocal"/>, or null for anchor-only timing (R5, R6).</param>
    /// <param name="options">Timing options; the defaults when null.</param>
    /// <param name="logger">Logger.</param>
    public PlayoutBuffer(IReceiverAudioOutput output, IMonotonicClock clock, IPtpSlave? slave = null, PlayoutOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);
        Output = output;
        Clock = clock;
        Slave = slave;
        Options = options ?? new PlayoutOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The output.</summary>
    public IReceiverAudioOutput Output { get; }

    /// <summary>The clock the release rule compares against (SPEC-receiver.md 5.3).</summary>
    public IMonotonicClock Clock { get; }

    /// <summary>The PTP slave, or null in anchor-only timing.</summary>
    public IPtpSlave? Slave { get; }

    /// <summary>The timing options.</summary>
    public PlayoutOptions Options { get; }

    /// <summary>The snapshot for Diagnostics (SPEC-receiver.md 5.3, 9.1).</summary>
    public PlayoutStatistics Statistics => throw new NotImplementedException("SPEC-receiver.md 5.3, wave 2");

    /// <summary>Starts the release thread (one per buffer, <c>PrecisionSleeper</c>; SPEC-receiver.md 5.3).</summary>
    public Task StartAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 5.3, wave 2");

    /// <summary>Queues decoded frames from either stream reader (SPEC-receiver.md 5.3 input).</summary>
    /// <param name="rtpTimestamp">The RTP timestamp of the first frame.</param>
    /// <param name="sequence">The packet or block sequence (16-bit realtime, 23-bit buffered) for gap and splice handling.</param>
    /// <param name="frames">Interleaved S16 stereo; copied.</param>
    public void Enqueue(uint rtpTimestamp, uint sequence, ReadOnlyMemory<short> frames) => throw new NotImplementedException("SPEC-receiver.md 5.3, wave 2");

    /// <summary>
    /// Sets the anchor <c>(rtpTime, senderTimeNs)</c> from SETRATEANCHORTIME or a 0xD7 sync (SPEC-receiver.md 4.7, 5.1, 5.3)
    /// and forgets the learnt <c>D</c> (R6). A <paramref name="timelineId"/> different from the slave's grandmaster is logged, the anchor applied anyway (RQ4).
    /// </summary>
    public void SetAnchor(uint rtpTime, ulong senderTimeNs, ulong timelineId) => throw new NotImplementedException("SPEC-receiver.md 5.3, wave 2");

    /// <summary><c>rate 0</c>: pauses the output and keeps the buffered frames (SPEC-receiver.md 4.7, 5.5).</summary>
    public void Pause() => throw new NotImplementedException("SPEC-receiver.md 5.3, wave 2");

    /// <summary><c>rate 1</c>: resumes the output, then frames per R6 (SPEC-receiver.md 4.7, 5.5).</summary>
    public void Resume() => throw new NotImplementedException("SPEC-receiver.md 5.3, wave 2");

    /// <summary>
    /// Drops buffered frames and flushes the output (SPEC-receiver.md 4.5, 4.7, 5.5): every frame for <c>FLUSH</c>
    /// (<paramref name="untilSequence"/> null), frames with a sequence below <paramref name="untilSequence"/> (23-bit modular
    /// compare) for an immediate <c>FLUSHBUFFERED</c>, which also pauses the output.
    /// </summary>
    public void Flush(uint? untilSequence) => throw new NotImplementedException("SPEC-receiver.md 5.3, wave 2");

    /// <summary>
    /// Registers a deferred <c>FLUSHBUFFERED</c> splice (SPEC-receiver.md 4.7, 5.5): when the block <paramref name="fromSequence"/>
    /// arrives, blocks up to but excluding <paramref name="untilSequence"/> are dropped without flushing or pausing the output.
    /// </summary>
    public void Splice(uint fromSequence, uint untilSequence) => throw new NotImplementedException("SPEC-receiver.md 5.3, wave 2");

    /// <summary>Stops the release thread; buffered frames are dropped.</summary>
    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("Playout buffer disposed");
        return ValueTask.CompletedTask;
    }
}
