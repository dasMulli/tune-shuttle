using System.Net;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;

namespace Airplay2.Sender.Streams;

/// <summary>
/// A receiver session as the buffered stream needs it, so a control flow written for the realtime stream (the group
/// player's D5 sequence and its pause/seek FLUSH) drives a type-103 stream unchanged (SPEC-audio.md 4.1, 4.3, 4.5):
/// <list type="bullet">
/// <item>session SETUP: PTP timing with the grandmaster's clock id; afterwards the receiver's
/// <c>timingPeerInfo</c> address becomes a grandmaster peer (<see cref="PtpSessionWiring.SelectPeerAddress"/>, the same
/// choice the group player makes for its <c>SETPEERS</c>);</item>
/// <item>stream SETUP: the transport's <see cref="BufferedAudioFormat"/> (type 103, <c>ct</c>, <c>audioFormat</c>,
/// <c>sr</c>, <c>spf</c>); the answer's <c>audioBufferSize</c> sizes the run-ahead window;</item>
/// <item><c>FLUSH</c> becomes <see cref="BufferedStreamTransport.FlushReceiverAsync"/> (rate 0 + immediate
/// <c>FLUSHBUFFERED</c>), and the next packet re-anchors with <c>SETRATEANCHORTIME rate 1</c>;</item>
/// <item>TEARDOWN and dispose release the grandmaster peer; dispose detaches the session from the transport.</item>
/// </list>
/// Everything else is passed through. Created by <see cref="BufferedStreamTransport.WrapSessionFactory"/>.
/// </summary>
public sealed class BufferedReceiverSession : IReceiverSession
{
    private readonly BufferedStreamTransport _transport;
    private readonly Lock _gate = new();
    private IPAddress? _timingPeer;
    private bool _disposed;

    /// <summary>Wraps <paramref name="inner"/> and attaches it to <paramref name="transport"/> under <paramref name="receiverId"/>.</summary>
    /// <param name="transport">The buffered transport.</param>
    /// <param name="inner">The receiver's session (paired or not).</param>
    /// <param name="receiverId">The id the transport registers the receiver under (the group player uses <see cref="DiscoveredReceiver.Id"/>).</param>
    public BufferedReceiverSession(BufferedStreamTransport transport, IReceiverSession inner, string receiverId)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(receiverId);
        _transport = transport;
        Inner = inner;
        ReceiverId = receiverId;
        transport.AttachSession(receiverId, inner);
    }

    /// <summary>The wrapped session.</summary>
    public IReceiverSession Inner { get; }

    /// <summary>The id the transport knows the receiver by.</summary>
    public string ReceiverId { get; }

    /// <summary>The grandmaster peer added for this session, null before the session SETUP or after TEARDOWN.</summary>
    public IPAddress? TimingPeer
    {
        get
        {
            lock (_gate)
            {
                return _timingPeer;
            }
        }
    }

    /// <summary>The receiver's <c>audioBufferSize</c> from the last stream SETUP answer (null when it sent none).</summary>
    public long? AudioBufferSize { get; private set; }

    /// <inheritdoc />
    public DiscoveredReceiver Receiver => Inner.Receiver;

    /// <inheritdoc />
    public IRtspConnection Connection => Inner.Connection;

    /// <inheritdoc />
    public PairingSession? Pairing => Inner.Pairing;

    /// <inheritdoc />
    public Task<PlistDictionary> GetInfoAsync(bool withQualifier, CancellationToken cancellationToken) => Inner.GetInfoAsync(withQualifier, cancellationToken);

    /// <inheritdoc />
    public Task<PairingSession> PairAsync(IPairingStore store, Func<CancellationToken, Task<string>>? pinPrompt, CancellationToken cancellationToken) => Inner.PairAsync(store, pinPrompt, cancellationToken);

    /// <inheritdoc />
    public Task AuthSetupAsync(CancellationToken cancellationToken) => Inner.AuthSetupAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Forces <c>timingProtocol: PTP</c> with the grandmaster's clock id (SPEC-audio.md 4.1: the buffered stream needs a PTP
    /// timeline), then adds the receiver as a grandmaster peer (SPEC-audio.md 4.4).
    /// </remarks>
    public async Task<SessionSetupResult> SetupSessionAsync(SessionSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { TimingProtocol = TimingProtocol.Ptp, TimingPort = 0, PtpClockId = _transport.Ptp.ClockId };
        var result = await Inner.SetupSessionAsync(request, cancellationToken).ConfigureAwait(false);
        var connection = Inner.Connection;
        var peer = PtpSessionWiring.SelectPeerAddress(result.TimingPeerAddresses, connection.RemoteEndPoint.Address, connection.LocalAddress);
        IPAddress? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _timingPeer;
            _timingPeer = peer;
        }

        _transport.AddTimingPeer(peer);
        if (previous is not null)
        {
            _transport.RemoveTimingPeer(previous);
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IEventChannel> ConnectEventChannelAsync(CancellationToken cancellationToken) => Inner.ConnectEventChannelAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> RecordAsync(CancellationToken cancellationToken) => Inner.RecordAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetPeersAsync(IReadOnlyList<IPAddress> peers, CancellationToken cancellationToken) => Inner.SetPeersAsync(peers, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Applies the transport's format (type 103 and its <c>ct</c>/<c>audioFormat</c>/<c>sr</c>/<c>spf</c>) and hands <c>audioBufferSize</c> to the transport.</remarks>
    public async Task<StreamSetupResult> SetupStreamAsync(StreamSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Inner.SetupStreamAsync(_transport.Format.Apply(request), cancellationToken).ConfigureAwait(false);
        if (result.Type != StreamType.Buffered)
        {
            throw new InvalidDataException($"{Inner.Receiver.Name} answered the buffered stream SETUP with stream type {(int)result.Type}.");
        }

        AudioBufferSize = result.AudioBufferSize;
        _transport.SetAudioBufferSize(ReceiverId, result.AudioBufferSize);
        return result;
    }

    /// <inheritdoc />
    public Task SetVolumeAsync(double volumeDb, CancellationToken cancellationToken) => Inner.SetVolumeAsync(volumeDb, cancellationToken);

    /// <inheritdoc />
    public Task<double> GetVolumeAsync(CancellationToken cancellationToken) => Inner.GetVolumeAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetMetadataAsync(TrackMetadata metadata, uint startFrame, CancellationToken cancellationToken) => Inner.SetMetadataAsync(metadata, startFrame, cancellationToken);

    /// <inheritdoc />
    public Task SetArtworkAsync(ReadOnlyMemory<byte> image, string contentType, uint startFrame, CancellationToken cancellationToken) => Inner.SetArtworkAsync(image, contentType, startFrame, cancellationToken);

    /// <inheritdoc />
    public Task SetProgressAsync(ProgressInfo progress, CancellationToken cancellationToken) => Inner.SetProgressAsync(progress, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The realtime <c>FLUSH</c> has no meaning on a buffered stream (SPEC-control.md 4.8): it becomes the transport's
    /// immediate flush for this receiver (<c>SETRATEANCHORTIME rate 0</c> + <c>FLUSHBUFFERED</c> until the given packet,
    /// SPEC-audio.md 4.5).
    /// </remarks>
    public Task FlushAsync(ushort nextSequence, uint nextTimestamp, CancellationToken cancellationToken) =>
        _transport.FlushReceiverAsync(ReceiverId, nextSequence, nextTimestamp, cancellationToken);

    /// <inheritdoc />
    public Task SetRateAnchorTimeAsync(int rate, uint rtpTime, ulong networkTimeSecs, ulong networkTimeFrac, ulong timelineId, CancellationToken cancellationToken) =>
        Inner.SetRateAnchorTimeAsync(rate, rtpTime, networkTimeSecs, networkTimeFrac, timelineId, cancellationToken);

    /// <inheritdoc />
    public Task FlushBufferedAsync(uint flushUntilSeq, uint flushUntilTimestamp, uint? flushFromSeq, uint? flushFromTimestamp, CancellationToken cancellationToken) =>
        Inner.FlushBufferedAsync(flushUntilSeq, flushUntilTimestamp, flushFromSeq, flushFromTimestamp, cancellationToken);

    /// <inheritdoc />
    public Task<PlistDictionary?> FeedbackAsync(CancellationToken cancellationToken) => Inner.FeedbackAsync(cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The inner TEARDOWN (per stream, then <c>{}</c>, SPEC-audio.md 4.5 "Stop", D7); the grandmaster peer is released
    /// whatever its outcome.
    /// </remarks>
    public async Task TeardownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Inner.TeardownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleasePeer();
        }
    }

    /// <inheritdoc />
    /// <remarks>Releases the peer, detaches from the transport (pending anchors/flushes fail) and disposes the inner session.</remarks>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        ReleasePeer();
        _transport.DetachSession(ReceiverId, Inner);
        await Inner.DisposeAsync().ConfigureAwait(false);
    }

    private void ReleasePeer()
    {
        IPAddress? peer;
        lock (_gate)
        {
            peer = _timingPeer;
            _timingPeer = null;
        }

        if (peer is not null)
        {
            _transport.RemoveTimingPeer(peer); // a no-op once the transport is disposed (it released every peer then)
        }
    }
}
