namespace Airplay2.Receiver.Timing;

/// <summary>A snapshot of a <see cref="IPtpSlave"/> for Diagnostics (SPEC-receiver.md 6.2: offset, pairs seen, last Sync age; 9.1 <c>PtpOffsetNs</c>).</summary>
/// <param name="OffsetNanos">The current estimate of slave minus master in nanoseconds, or null before the first pair.</param>
/// <param name="PairsSeen">Completed Sync/Follow_Up pairs.</param>
/// <param name="LastSyncAge">Time since the last Sync arrived, or null before the first.</param>
/// <param name="MasterClockId">The grandmaster identity from the last Follow_Up or Announce; 0 before any.</param>
public sealed record PtpSlaveStatistics(long? OffsetNanos, int PairsSeen, TimeSpan? LastSyncAge, ulong MasterClockId);

/// <summary>
/// The PTP slave of a sender session (SPEC-receiver.md R5, 6.2): follows the sender's grandmaster from its two-step
/// Sync/Follow_Up pairs and maps sender time onto the local monotonic clock. <see cref="PtpSlave"/> is the implementation.
/// </summary>
public interface IPtpSlave : IAsyncDisposable
{
    /// <summary>The slave's own clock identity.</summary>
    ulong ClockId { get; }

    /// <summary>The grandmaster clock identity from the last Follow_Up or Announce; 0 before any.</summary>
    ulong MasterClockId { get; }

    /// <summary>The completed Sync/Follow_Up pairs, oldest first.</summary>
    IReadOnlyCollection<PtpSlaveSample> Samples { get; }

    /// <summary>The snapshot for Diagnostics (SPEC-receiver.md 6.2).</summary>
    PtpSlaveStatistics Statistics { get; }

    /// <summary>Starts receiving.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Maps a sender (grandmaster) time to local monotonic nanoseconds (SPEC-receiver.md 6.2: <c>senderNs + offset</c>, the
    /// offset being the minimum over a sliding window of 8 pairs); false while there is no estimate, and the caller uses
    /// the anchor-only mapping of R6.
    /// </summary>
    bool TryMapToLocal(ulong senderNanos, out ulong localNanos);
}
