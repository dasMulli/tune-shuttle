using Airplay2.Receiver.Codecs;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Streams;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Session;

/// <summary>
/// The product implementation of <see cref="IControlPlaneHost.CreateStreamAsync"/> (SPEC-receiver.md 4.6): one
/// <see cref="PlayoutBuffer"/> per stream on the process clock with the session's PTP slave, fed by a
/// <see cref="RealtimeStreamReceiver"/> (type 96: UDP data and control on port 0, 0xD7 syncs as anchors, 5.1) or a
/// <see cref="BufferedStreamReceiver"/> (type 103: TCP data listener and idle UDP control socket on port 0, 5.2) with an AAC
/// decoder from the factory when the format needs one (R4).
/// </summary>
public sealed class ReceiverStreamFactory
{
    private readonly IReceiverAudioOutput _output;
    private readonly IMonotonicClock _clock;
    private readonly IMasterClock? _masterClock;
    private readonly PlayoutOptions _playout;
    private readonly IAacDecoderFactory? _aacDecoders;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates the factory (SPEC-receiver.md 4.6).</summary>
    /// <param name="output">The output every stream's buffer releases into (5.5).</param>
    /// <param name="clock">The process clock as an <see cref="IMonotonicClock"/> (5.3).</param>
    /// <param name="playout">Playout timing options (R6).</param>
    /// <param name="aacDecoders">The AAC decoder factory, or null in the ALAC-only variant (R3, R4).</param>
    /// <param name="masterClock">The process clock for the realtime receiver's NTP field, or null.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public ReceiverStreamFactory(IReceiverAudioOutput output, IMonotonicClock clock, PlayoutOptions? playout = null, IAacDecoderFactory? aacDecoders = null, IMasterClock? masterClock = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);
        _output = output;
        _clock = clock;
        _playout = playout ?? new PlayoutOptions();
        _aacDecoders = aacDecoders;
        _masterClock = masterClock;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>Binds and starts one stream (SPEC-receiver.md 4.6); see <see cref="IControlPlaneHost.CreateStreamAsync"/>.</summary>
    public async ValueTask<IReceiverStream> CreateAsync(ReceiverStreamRequest request, ISenderTiming? timing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new PlayoutBuffer(_output, _clock, timing?.Slave, _playout, _loggerFactory.CreateLogger<PlayoutBuffer>());
        try
        {
            await buffer.StartAsync(cancellationToken).ConfigureAwait(false);
            if (request.Type == StreamType.Realtime)
            {
                var realtime = new RealtimeStreamReceiver(
                    new RealtimeStreamReceiverOptions { BindAddress = request.BindAddress, Clock = _masterClock },
                    new BufferFeed(buffer),
                    _loggerFactory.CreateLogger<RealtimeStreamReceiver>());
                var stream = new PlayoutReceiverStream(buffer, realtime, null);
                realtime.Start();
                realtime.BeginStream(stream, request.SharedKey, request.SenderControl);
                return stream;
            }

            IAacDecoder? decoder = null;
            if (request.AudioFormat == ReceiverStreamFormats.AacLc && _aacDecoders is not null && !_aacDecoders.TryCreate(out decoder, out var reason))
            {
                _loggerFactory.CreateLogger<ReceiverStreamFactory>().LogWarning("No AAC decoder for the buffered stream: {Reason}", reason);
            }

            var buffered = new BufferedStreamReceiver(buffer, decoder, new BufferedStreamReceiverOptions { BindAddress = request.BindAddress }, _loggerFactory.CreateLogger<BufferedStreamReceiver>());
            await buffered.StartAsync(request.SharedKey, request.AudioFormat, cancellationToken).ConfigureAwait(false);
            return new PlayoutReceiverStream(buffer, null, buffered);
        }
        catch
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Feeds a realtime receiver's decoded packets and 0xD7 anchors into the buffer (SPEC-receiver.md 5.1).</summary>
    private sealed class BufferFeed(PlayoutBuffer buffer) : IRealtimeStreamObserver
    {
        public void OnAudioPacket(ushort sequence, uint timestamp, bool marker, ReadOnlySpan<short> pcm) =>
            buffer.Enqueue(timestamp, sequence, pcm.ToArray());

        public void OnPtpSync(PtpSyncPacket sync) => buffer.SetAnchor(sync.NowFrame, sync.PtpTimeNanos, sync.ClockId);
    }

    /// <summary>A stream over its buffer and its socket reader (SPEC-receiver.md 4.5-4.7, 4.9).</summary>
    private sealed class PlayoutReceiverStream(PlayoutBuffer buffer, RealtimeStreamReceiver? realtime, BufferedStreamReceiver? buffered) : IReceiverStream
    {
        public StreamType Type => realtime is not null ? StreamType.Realtime : StreamType.Buffered;

        public int DataPort => realtime?.DataPort ?? buffered!.DataPort;

        public int ControlPort => realtime?.ControlPort ?? buffered!.ControlPort;

        public long? AudioBufferSize => buffered?.Options.AudioBufferSize;

        public void SetAnchor(uint rtpTime, ulong networkTimeNanos, ulong timelineId) => buffer.SetAnchor(rtpTime, networkTimeNanos, timelineId);

        public void Pause() => buffer.Pause();

        public void Resume() => buffer.Resume();

        public void Flush(ushort? nextSequence)
        {
            realtime?.Flush(this, nextSequence);
            buffer.Flush(null);
        }

        public void FlushBuffered(uint untilSequence) => buffer.Flush(untilSequence);

        public void Splice(uint fromSequence, uint untilSequence) => buffer.Splice(fromSequence, untilSequence);

        public async ValueTask DisposeAsync()
        {
            if (realtime is not null)
            {
                realtime.EndStream(this);
                await realtime.DisposeAsync().ConfigureAwait(false);
            }

            if (buffered is not null)
            {
                await buffered.DisposeAsync().ConfigureAwait(false);
            }

            await buffer.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>The <c>audioFormat</c> bits a stream SETUP may carry (SPEC-receiver.md R3, SPEC-control.md 4.4).</summary>
public static class ReceiverStreamFormats
{
    /// <summary>Bit 18: ALAC/44100/16/2.</summary>
    public const uint Alac = 0x40000;

    /// <summary>Bit 22: AAC-LC/44100/2.</summary>
    public const uint AacLc = 0x400000;

    /// <summary>The decoder name of <see cref="Alac"/> (SPEC-receiver.md 4.6, <see cref="ReceiverEvent.SenderStreamStarted"/>).</summary>
    public const string AlacName = "ALAC/44100/16/2";

    /// <summary>The decoder name of <see cref="AacLc"/>.</summary>
    public const string AacLcName = "AAC-LC/44100/2";

    /// <summary>
    /// The decoder name for a buffered stream's <c>audioFormat</c>, or null when it is not one of R3's (SPEC-receiver.md 4.6:
    /// answered 400); AAC-LC is accepted only with a decoder (<paramref name="aacAvailable"/>, the ALAC-only variant of R3).
    /// </summary>
    public static string? BufferedFormatName(ulong audioFormat, bool aacAvailable) => audioFormat switch
    {
        Alac => AlacName,
        AacLc when aacAvailable => AacLcName,
        _ => null,
    };
}
