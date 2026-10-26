using Airplay2.Receiver;
using Airplay2.Receiver.Playout;
using Airplay2.Receiver.Timing;
using Bridge.Service.Airplay;
using Bridge.Service.Output;

namespace Bridge.Service.Api;

/// <summary>
/// What the API reads of the sources to build the AirPlay fields of <c>StatusDto</c>, <c>PlaybackDto</c> and
/// <c>DiagnosticsDto</c> (SPEC-receiver.md 8.2, 8.3, 9.1), and how a settings change reaches the receiver (3.3).
/// <see cref="AirplaySource"/> implements it; <c>DtoFactory</c> uses a registered <see cref="IAirplayApiView"/> (a test
/// stub) instead when there is one.
/// </summary>
public interface IAirplayApiView
{
    /// <summary>Who owns the output (SPEC-receiver.md 8.2 <c>SourceSwitch.Owner</c>).</summary>
    AudioSource Owner { get; }

    /// <summary>The receiver's state (SPEC-receiver.md 9.1 <c>AirplayReceiverDto.State</c>).</summary>
    ReceiverStatus Status { get; }

    /// <summary>The name the receiver advertises, or null to use the stored <c>airplay_name</c> (SPEC-receiver.md 8.4).</summary>
    string? Name { get; }

    /// <summary>The connected sender, or null (SPEC-receiver.md 8.3).</summary>
    SenderInfo? Sender { get; }

    /// <summary>An AAC decoder loaded (SPEC-receiver.md R4).</summary>
    bool AacAvailable { get; }

    /// <summary>The RTSP port: the bound one while running, else the configured one (SPEC-receiver.md 8.5, 9.1).</summary>
    int RtspPort { get; }

    /// <summary>The shared PTP port is bound (SPEC-receiver.md 6.1; false: anchor-only timing, R5).</summary>
    bool PtpBound { get; }

    /// <summary>The playout statistics, or null without a stream (SPEC-receiver.md 5.3).</summary>
    PlayoutStatistics? Statistics { get; }

    /// <summary>The PTP slave's statistics, or null without a PTP session (SPEC-receiver.md 6.2).</summary>
    PtpSlaveStatistics? PtpStatistics { get; }

    /// <summary>Why the AAC decoder is unavailable, or the loaded library (SPEC-receiver.md 7.2, 9.1 <c>DecoderStatus</c>).</summary>
    string? DecoderStatus { get; }

    /// <summary>The sender's last artwork with its <c>rtptime</c> key, or null (SPEC-receiver.md 9.1 <c>GET /api/playback/artwork</c>).</summary>
    AirplayArtwork? Artwork { get; }

    /// <summary>Applies changed receiver settings (SPEC-receiver.md 3.3, 8.3 <c>AirplaySource.ApplySettingsAsync</c>).</summary>
    Task ApplySettingsAsync(AirplaySettings settings, CancellationToken cancellationToken);
}

/// <summary>The artwork the AirPlay sender sent last (SPEC-receiver.md 9.1 <c>GET /api/playback/artwork</c>).</summary>
/// <param name="Bytes">The image bytes.</param>
/// <param name="ContentType">The image's content type (<c>image/jpeg</c>, <c>image/png</c>).</param>
/// <param name="Key">The <c>rtptime</c> the sender sent it with: the <c>v</c> of the URL and the <c>ETag</c>.</param>
public sealed record AirplayArtwork(ReadOnlyMemory<byte> Bytes, string ContentType, uint Key)
{
    /// <summary>The quoted entity tag of the artwork (SPEC-receiver.md 9.1: an <c>ETag</c> of the <c>rtptime</c>).</summary>
    public string ETag => "\"" + Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
}
