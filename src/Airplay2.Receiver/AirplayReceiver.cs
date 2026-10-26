using System.Threading.Channels;
using Airplay2.Receiver.Playout;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver;

/// <summary>
/// The AirPlay 2 receiver (SPEC-receiver.md 0, R1-R16): advertises the two services (section 3), accepts one sender at
/// a time on the RTSP listener (section 4), decodes its stream into the <see cref="PlayoutBuffer"/> (section 5) and
/// drives the host's <see cref="IReceiverAudioOutput"/> (5.5). The host observes it through <see cref="Events"/> and
/// <see cref="Status"/>. Works without the bridge (G5); the bridge hosts it as <c>AirplaySource</c> (8.3).
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the wiring is the wave 2 <c>receiver-facade</c> package (SPEC-receiver.md 13).
/// </remarks>
public sealed class AirplayReceiver : IAsyncDisposable
{
    private readonly Channel<ReceiverEvent> _events = Channel.CreateBounded<ReceiverEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>Creates the receiver; nothing is bound or advertised until <see cref="StartAsync"/>.</summary>
    /// <param name="options">Name, identity, password, ports, formats and timing.</param>
    /// <param name="output">Where released frames and pause/flush/volume/metadata go (SPEC-receiver.md 5.5).</param>
    /// <param name="loggerFactory">Logger factory; null logs nothing.</param>
    public AirplayReceiver(AirplayReceiverOptions options, IReceiverAudioOutput output, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        Options = options;
        Output = output;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<AirplayReceiver>();
    }

    /// <summary>The options the receiver was created with.</summary>
    public AirplayReceiverOptions Options { get; }

    /// <summary>The host's output (SPEC-receiver.md 5.5).</summary>
    public IReceiverAudioOutput Output { get; }

    /// <summary>The events for the host, in order (SPEC-receiver.md 8.3); the oldest is dropped when the host falls behind.</summary>
    public ChannelReader<ReceiverEvent> Events => _events.Reader;

    /// <summary>The receiver's state (SPEC-receiver.md 9.1 <c>AirplayReceiverDto.State</c>).</summary>
    public ReceiverStatus Status { get; private set; } = ReceiverStatus.Off;

    /// <summary>The sender of the current session, or null (SPEC-receiver.md 4.3).</summary>
    public SenderInfo? Sender { get; private set; }

    /// <summary>The bound RTSP port after <see cref="StartAsync"/> (the OS-chosen one when <see cref="AirplayReceiverOptions.RtspPort"/> is 0), else 0.</summary>
    public int RtspPort { get; private set; }

    /// <summary>True when an AAC decoder loaded (SPEC-receiver.md R4); false means the ALAC-only variant of R3 is advertised.</summary>
    public bool AacAvailable { get; private set; }

    /// <summary>The playout statistics of the current stream, or null without a stream (SPEC-receiver.md 5.3, 9.1).</summary>
    public PlayoutStatistics? Statistics => null;

    /// <summary>
    /// Binds the RTSP listener, loads the controllers, registers the two services and starts accepting senders
    /// (SPEC-receiver.md 3.1, 3.3 <c>enabled → true</c>, 4.1). <see cref="Status"/> becomes <see cref="ReceiverStatus.Advertising"/>.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 3.1, 4.1, wave 2");

    /// <summary>Goodbye records, listeners closed, a running session ended (SPEC-receiver.md 3.3 <c>enabled → false</c>, 4.9).</summary>
    public Task StopAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 3.3, wave 2");

    /// <summary>
    /// Ends the current session as the host asks (SPEC-receiver.md R8: Spotify claimed the output): stream, event channel and
    /// PTP slave closed, the output released (5.5), <see cref="ReceiverEvent.SenderGone"/> raised with <paramref name="reason"/>.
    /// Completes after <see cref="IReceiverAudioOutput.ReleaseAsync"/>; a no-op without a session.
    /// </summary>
    public Task EndSessionAsync(string reason, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 4.9, wave 2");

    /// <summary>
    /// Applies a name or password change without a restart (SPEC-receiver.md 3.3): the services are re-registered and
    /// new connections get the new pairing options; a running session is untouched.
    /// </summary>
    public Task UpdateSettingsAsync(string name, string? password, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 3.3, wave 2");

    /// <summary>Stops the receiver when it is running and completes <see cref="Events"/>.</summary>
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _logger.LogDebug("Receiver {Name} disposed (status {Status}, sender {Sender})", Options.Name, Status, Sender?.Name);
        return ValueTask.CompletedTask;
    }
}
