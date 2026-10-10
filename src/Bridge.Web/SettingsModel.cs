using System.ComponentModel.DataAnnotations;
using Bridge.Contracts;
using Bridge.Web.Localization;

namespace Bridge.Web;

/// <summary>
/// The editable form of <see cref="SettingsDto"/> with the service's validation rules (BridgeController: name 1..64
/// characters, L between 22050 and 176400 frames, stream Realtime or Buffered, forget offline receivers after 0..3650
/// days), so the form rejects what the service would answer with 422. The attributes' <c>ErrorMessage</c> values are catalog keys (<c>settings.validation.*</c>,
/// UI-SPEC 4.6); see <see cref="CatalogMessage"/> for how they become text.
/// </summary>
public sealed class SettingsModel
{
    /// <summary>Smallest accepted L (0.5 s).</summary>
    public const int MinLatencyFrames = 22050;

    /// <summary>Largest accepted L (4 s).</summary>
    public const int MaxLatencyFrames = 176400;

    /// <summary>Longest "forget offline receivers after" in days (the service's <c>ReceiverRetention.MaxDays</c>).</summary>
    public const int MaxForgetOfflineAfterDays = 3650;

    /// <summary>The service's default "forget offline receivers after", for answers from a service that predates the field.</summary>
    public const double DefaultForgetOfflineAfterDays = 3;

    /// <summary>The Spotify Connect device name.</summary>
    [CatalogRequired(AllowEmptyStrings = false, ErrorMessage = "settings.validation.nameEmpty")]
    [CatalogStringLength(64, ErrorMessage = "settings.validation.nameTooLong")]
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>L in frames.</summary>
    [CatalogRange(MinLatencyFrames, MaxLatencyFrames, ErrorMessage = "settings.validation.latencyRange")]
    public int LatencyFrames { get; set; }

    /// <summary>Poll receiver volume.</summary>
    public bool PollReceiverVolume { get; set; }

    /// <summary>"Realtime" or "Buffered".</summary>
    [CatalogRegularExpression("(?i)^(realtime|buffered)$", ErrorMessage = "settings.validation.stream")]
    public string PreferredStream { get; set; } = "Realtime";

    /// <summary>Offline receivers without user state are forgotten after this many days; 0 never.</summary>
    [CatalogRange(0d, MaxForgetOfflineAfterDays, ErrorMessage = "settings.validation.forgetDaysRange")]
    public double ForgetOfflineAfterDays { get; set; } = DefaultForgetOfflineAfterDays;

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
            ForgetOfflineAfterDays = dto.ForgetOfflineAfterDays ?? DefaultForgetOfflineAfterDays,
        };
    }

    /// <summary>To the DTO (name trimmed).</summary>
    public SettingsDto ToDto() => new(DeviceName.Trim(), LatencyFrames, PollReceiverVolume, PreferredStream, ForgetOfflineAfterDays);

    /// <summary>
    /// Turns a failed result's message (a catalog key) into the catalog text when the validation context offers an
    /// <see cref="I18n"/> service: Blazor's <c>DataAnnotationsValidator</c> passes the app's service provider, so the
    /// <c>ValidationMessage</c> of a form shows text in the current language. Without the service (plain
    /// <see cref="Validator"/> calls) the key stays, and a page renders <c>L[message]</c> itself; a text that is not a
    /// key passes <c>L[…]</c> unchanged, so both paths agree.
    /// </summary>
    internal static class CatalogMessage
    {
        public static ValidationResult? Localize(ValidationResult? result, ValidationContext context)
        {
            if (result?.ErrorMessage is { } key && context.GetService(typeof(I18n)) is I18n l)
            {
                result.ErrorMessage = l[key];
            }

            return result;
        }
    }

    /// <summary><see cref="RequiredAttribute"/> with a catalog message (<see cref="CatalogMessage"/>).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    private sealed class CatalogRequiredAttribute : RequiredAttribute
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
            CatalogMessage.Localize(base.IsValid(value, validationContext), validationContext);
    }

    /// <summary><see cref="StringLengthAttribute"/> with a catalog message (<see cref="CatalogMessage"/>).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    private sealed class CatalogStringLengthAttribute(int maximumLength) : StringLengthAttribute(maximumLength)
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
            CatalogMessage.Localize(base.IsValid(value, validationContext), validationContext);
    }

    /// <summary><see cref="RangeAttribute"/> with a catalog message (<see cref="CatalogMessage"/>).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    private sealed class CatalogRangeAttribute : RangeAttribute
    {
        public CatalogRangeAttribute(int minimum, int maximum)
            : base(minimum, maximum)
        {
        }

        public CatalogRangeAttribute(double minimum, double maximum)
            : base(minimum, maximum)
        {
        }

        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
            CatalogMessage.Localize(base.IsValid(value, validationContext), validationContext);
    }

    /// <summary><see cref="RegularExpressionAttribute"/> with a catalog message (<see cref="CatalogMessage"/>).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    private sealed class CatalogRegularExpressionAttribute(string pattern) : RegularExpressionAttribute(pattern)
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
            CatalogMessage.Localize(base.IsValid(value, validationContext), validationContext);
    }
}
