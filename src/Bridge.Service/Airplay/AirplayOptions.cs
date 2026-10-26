namespace Bridge.Service.Airplay;

/// <summary>
/// The <c>Bridge:Airplay</c> configuration section (SPEC-receiver.md 8.5), bound as <see cref="BridgeOptions.Airplay"/>.
/// The bind address is the existing <c>Bridge:LocalAddress</c>; there is no separate one here.
/// </summary>
public sealed class AirplayOptions
{
    /// <summary>Start the receiver (SPEC-receiver.md 8.5); the stored <c>airplay_enabled</c> setting wins (8.4).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The RTSP listener port (SPEC-receiver.md R13, 8.5); tests use 0.</summary>
    public int RtspPort { get; set; } = 7000;

    /// <summary>The shared <c>PtpPort</c>'s event port (SPEC-receiver.md R5, 8.5); tests use 0.</summary>
    public int PtpEventPort { get; set; } = 319;

    /// <summary>The shared <c>PtpPort</c>'s general port (SPEC-receiver.md R5, 8.5); tests use 0.</summary>
    public int PtpGeneralPort { get; set; } = 320;

    /// <summary>Directory of the libavcodec libraries, or null for the default search path (SPEC-receiver.md 7.2, 8.5).</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>The anchor-only lead of R6 in milliseconds (SPEC-receiver.md 8.5).</summary>
    public int AnchorLeadMs { get; set; } = 300;

    /// <summary>A sender silent on the control connection for this long after a stream SETUP is gone (SPEC-receiver.md R16, 8.5).</summary>
    public int SessionTimeoutSeconds { get; set; } = 30;
}
