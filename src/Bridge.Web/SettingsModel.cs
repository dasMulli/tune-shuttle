using System.ComponentModel.DataAnnotations;
using Bridge.Contracts;

namespace Bridge.Web;

/// <summary>
/// The editable form of <see cref="SettingsDto"/> with the service's validation rules (BridgeController: name 1..64
/// characters, L between 22050 and 176400 frames, stream Realtime or Buffered), so the form rejects what the service
/// would answer with 422.
/// </summary>
public sealed class SettingsModel
{
    /// <summary>Smallest accepted L (0.5 s).</summary>
    public const int MinLatencyFrames = 22050;

    /// <summary>Largest accepted L (4 s).</summary>
    public const int MaxLatencyFrames = 176400;

    /// <summary>The Spotify Connect device name.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "The device name is empty.")]
    [StringLength(64, ErrorMessage = "The device name is longer than 64 characters.")]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>L in frames.</summary>
    [Range(MinLatencyFrames, MaxLatencyFrames, ErrorMessage = "The latency must be between 22050 and 176400 frames.")]
    public int LatencyFrames { get; set; }

    /// <summary>Poll receiver volume.</summary>
    public bool PollReceiverVolume { get; set; }

    /// <summary>"Realtime" or "Buffered".</summary>
    [RegularExpression("(?i)^(realtime|buffered)$", ErrorMessage = "The stream type is Realtime or Buffered.")]
    public string PreferredStream { get; set; } = "Realtime";

    /// <summary>From the DTO.</summary>
    public static SettingsModel From(SettingsDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new SettingsModel
        {
            DeviceName = dto.DeviceName,
            LatencyFrames = dto.LatencyFrames,
            PollReceiverVolume = dto.PollReceiverVolume,
            PreferredStream = dto.PreferredStream,
        };
    }

    /// <summary>To the DTO (name trimmed).</summary>
    public SettingsDto ToDto() => new(DeviceName.Trim(), LatencyFrames, PollReceiverVolume, PreferredStream);
}
