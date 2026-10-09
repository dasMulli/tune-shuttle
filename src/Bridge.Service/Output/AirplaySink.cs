using Airplay2.Sender.Playback;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Decoder;
using Microsoft.Extensions.Logging;

namespace Bridge.Service.Output;

/// <summary>
/// The librespot <see cref="ISink"/> that feeds the AirPlay group player (SPEC-audio.md 8). <see cref="Write"/> converts
/// the f64 samples to S16 and blocks while the player's ring is above its high-water mark; without a connected
/// output the audio is discarded (PLAN.md). <see cref="Start"/>/<see cref="Stop"/> map to resume/pause on the player.
/// The bridge swaps the target player when the user selects another output.
/// </summary>
public sealed class AirplaySink : ISink
{
    private readonly ILogger<AirplaySink> _logger;
    private volatile IGroupPlayer? _player;

    /// <summary>Creates the sink; <paramref name="format"/> must be S16 (the ALAC input format).</summary>
    public AirplaySink(AudioFormat format, ILogger<AirplaySink> logger)
    {
        if (format != AudioFormat.S16)
        {
            throw new SinkException(SinkException.Reason.InvalidParams, "AirplaySink requires S16");
        }

        _logger = logger;
    }

    /// <summary>The player audio currently goes to, or null to discard.</summary>
    public IGroupPlayer? Player
    {
        get => _player;
        set => _player = value;
    }

    /// <summary>Frames written since creation (diagnostics).</summary>
    public long FramesWritten { get; private set; }

    /// <summary>Frames discarded because no output was connected.</summary>
    public long FramesDiscarded { get; private set; }

    /// <inheritdoc />
    public void Start()
    {
        // TODO(impl: bridge-service): _player?.StartAsync; librespot calls this on play/resume (Player.cs:1858-1876)
    }

    /// <inheritdoc />
    public void Stop()
    {
        // TODO(impl: bridge-service): _player?.PauseAsync; called on pause and track end (Player.cs:1878-1909)
    }

    /// <inheritdoc />
    public void Write(AudioPacket packet, Converter converter)
    {
        if (packet is not AudioPacket.Samples samples)
        {
            return; // raw (passthrough) packets cannot be forwarded to AirPlay
        }

        var pcm = converter.F64ToS16(samples.Data);
        var player = _player;
        if (player is null)
        {
            FramesDiscarded += pcm.Length / 2;
            return;
        }

        player.Write(pcm); // blocks above the ring's high-water mark: this is the only clock librespot sees
        FramesWritten += pcm.Length / 2;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _player = null;
    }
}
