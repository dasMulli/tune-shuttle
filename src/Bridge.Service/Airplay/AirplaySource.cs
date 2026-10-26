using Airplay2.Receiver;
using Airplay2.Receiver.Playout;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Bridge.Service.Output;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Airplay;

/// <summary>
/// Hosts the <see cref="AirplayReceiver"/> in the bridge (SPEC-receiver.md 8.3): builds it from the settings
/// (<see cref="AirplaySettings"/>) and <see cref="BridgeOptions.Airplay"/>, gives it the sender's clock and the shared
/// <see cref="IPtpPort"/>, implements <see cref="IReceiverAudioOutput"/> against the <see cref="OutputManager"/> behind the
/// <see cref="SourceSwitch"/> gate, applies settings changes (3.3) and exposes its state for <c>DtoFactory</c>.
/// </summary>
/// <remarks>
/// Wave 0 scaffold: registered as a hosted service that does nothing (it logs once when <c>Bridge:Airplay:Enabled</c> is
/// true); the behaviour is the wave 3 <c>airplay-source</c> package (SPEC-receiver.md 13).
/// </remarks>
public sealed class AirplaySource : IHostedService, IReceiverAudioOutput, IAsyncDisposable
{
    private readonly IOptions<BridgeOptions> _options;
    private readonly SourceSwitch _sources;
    private readonly IPtpPort _ptpPort;
    private readonly ILogger<AirplaySource> _logger;

    /// <summary>Creates the source; the receiver is built in <see cref="StartAsync"/>.</summary>
    public AirplaySource(IOptions<BridgeOptions> options, SourceSwitch sources, IPtpPort ptpPort, ILogger<AirplaySource> logger)
    {
        _options = options;
        _sources = sources;
        _ptpPort = ptpPort;
        _logger = logger;
    }

    /// <summary>The receiver's state (SPEC-receiver.md 9.1 <c>AirplayReceiverDto.State</c>); <see cref="ReceiverStatus.Off"/> while not hosted.</summary>
    public ReceiverStatus Status => ReceiverStatus.Off;

    /// <summary>The current sender, or null (SPEC-receiver.md 8.3).</summary>
    public SenderInfo? Sender => null;

    /// <summary>True when an AAC decoder loaded (SPEC-receiver.md R4, 9.1 <c>AacAvailable</c>).</summary>
    public bool AacAvailable => false;

    /// <summary>The playout statistics for Diagnostics, or null without a stream (SPEC-receiver.md 9.1).</summary>
    public PlayoutStatistics? Statistics => null;

    /// <summary>The shared PTP port (SPEC-receiver.md 6.1).</summary>
    public IPtpPort PtpPort => _ptpPort;

    /// <inheritdoc />
    public int QueuedFrames => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <summary>Starts the receiver when the stored <c>airplay_enabled</c> (else <c>Bridge:Airplay:Enabled</c>) is true (SPEC-receiver.md 8.3, 8.4).</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var airplay = _options.Value.Airplay;
        if (!airplay.Enabled)
        {
            _logger.LogDebug("AirPlay receiver disabled by Bridge:Airplay:Enabled");
            return Task.CompletedTask;
        }

        _logger.LogInformation("AirPlay receiver not hosted yet (SPEC-receiver.md 8.3, wave 3); Bridge:Airplay:RtspPort {Port} stays closed, output owner {Owner}", airplay.RtspPort, _sources.Owner);
        return Task.CompletedTask;
    }

    /// <summary>Stops the receiver like <c>enabled → false</c> (SPEC-receiver.md 3.3).</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Ends the sender's session as the switch asks (SPEC-receiver.md R8, 8.2: "replaced by Spotify"); completes after the output was released.</summary>
    public Task EndSessionAsync(string reason, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.2, wave 3");

    /// <summary>Replays a remembered resume once the switch grants ownership (SPEC-receiver.md 8.2).</summary>
    public Task ResumeIfPlayingAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.2, wave 3");

    /// <summary>Applies a settings change (SPEC-receiver.md 3.3, 8.3): name and password without a restart, <c>enabled</c> starts or stops the receiver.</summary>
    public Task ApplySettingsAsync(AirplaySettings settings, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <inheritdoc />
    public Task SetMasterLevelAsync(int level, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <inheritdoc />
    public Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <inheritdoc />
    public Task ReleaseAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 8.3, wave 3");

    /// <summary>Stops the receiver when it is running.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
