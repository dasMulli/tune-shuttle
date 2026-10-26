using System.ComponentModel.DataAnnotations;
using Bridge.Contracts;
using Bridge.Web.Localization;

namespace Bridge.Web;

/// <summary>
/// The editable form of <see cref="SettingsDto"/> with the service's validation rules (BridgeController: name 1..64
/// characters, L between 22050 and 176400 frames, stream Realtime or Buffered, forget offline receivers after 0..3650
/// days, AirPlay receiver name 1..63 and password up to 63 characters, SPEC-receiver.md 9.1-9.2), so the form rejects what
/// the service would answer with 422. The attributes' <c>ErrorMessage</c> values are catalog keys (<c>settings.validation.*</c>,
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

    /// <summary>Longest AirPlay receiver name (the service's <c>MaxAirplayNameLength</c>, the mDNS label limit; SPEC-receiver.md 9.1).</summary>
    public const int MaxAirplayNameLength = 63;

    /// <summary>Longest AirPlay receiver password (the service's <c>MaxAirplayPasswordLength</c>, SPEC-receiver.md 9.1).</summary>
    public const int MaxAirplayPasswordLength = 63;

    private string _airplayPassword = string.Empty;

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

    /// <summary>
    /// The service answered the AirPlay receiver fields (SPEC-receiver.md 9.1: every answer of a service with the receiver
    /// carries them). False for a service that predates the receiver: the page shows no AirPlay card and
    /// <see cref="ToDto"/> sends nulls, which keep whatever the service has.
    /// </summary>
    public bool AirplayOffered { get; private set; }

    /// <summary>Offer the bridge as an AirPlay speaker (SPEC-receiver.md 9.2, settings.airplayEnabled).</summary>
    public bool AirplayEnabled { get; set; }

    /// <summary>
    /// The advertised AirPlay name (SPEC-receiver.md 9.1-9.2): 1 to <see cref="MaxAirplayNameLength"/> characters, trimmed
    /// on save. Null only when <see cref="AirplayOffered"/> is false, and then valid.
    /// </summary>
    [CatalogNotEmpty(ErrorMessage = "settings.validation.airplayNameEmpty")]
    [CatalogStringLength(MaxAirplayNameLength, ErrorMessage = "settings.validation.airplayNameTooLong")]
    public string? AirplayName { get; set; }

    /// <summary>
    /// The password field's text (SPEC-receiver.md 9.1-9.2). The service never answers the password, so the field starts
    /// empty; empty means "keep" unless <see cref="RemoveAirplayPassword"/> was chosen (see <see cref="ToDto"/>).
    /// </summary>
    [CatalogStringLength(MaxAirplayPasswordLength, ErrorMessage = "settings.validation.airplayPasswordTooLong")]
    public string? AirplayPassword
    {
        get => _airplayPassword;
        set => _airplayPassword = value ?? string.Empty;
    }

    /// <summary>The service has a password stored (<see cref="SettingsDto.AirplayPasswordSet"/>, SPEC-receiver.md 9.1).</summary>
    public bool AirplayPasswordSet { get; private set; }

    /// <summary>"Remove password" was pressed since the last load or save: an empty field then sends <c>""</c> (SPEC-receiver.md 9.1).</summary>
    public bool AirplayPasswordRemoved { get; private set; }

    /// <summary>
    /// The password field is empty and a stored password stays as it is: the page shows settings.airplayPasswordSetHint
    /// and the "remove password" button then (SPEC-receiver.md 9.2).
    /// </summary>
    public bool KeepsStoredAirplayPassword => AirplayPasswordSet && !AirplayPasswordRemoved && _airplayPassword.Length == 0;

    /// <summary>The "remove password" button (SPEC-receiver.md 9.1-9.2): empties the field, and the next save sends <c>""</c>.</summary>
    public void RemoveAirplayPassword()
    {
        _airplayPassword = string.Empty;
        AirplayPasswordRemoved = true;
    }

    /// <summary>
    /// Takes a save's answer (SPEC-receiver.md 9.1) into this model, so the page keeps its <c>EditContext</c>: the values
    /// as stored, the password field emptied (the answer never carries the password) and the removal forgotten.
    /// </summary>
    public void Accept(SettingsDto saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        DeviceName = saved.DeviceName;
        LatencyFrames = saved.LatencyFrames;
        PollReceiverVolume = saved.PollReceiverVolume;
        PreferredStream = saved.PreferredStream;
        ForgetOfflineAfterDays = saved.ForgetOfflineAfterDays ?? ForgetOfflineAfterDays;
        TakeAirplay(saved);
    }

    /// <summary>From the DTO.</summary>
    public static SettingsModel From(SettingsDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var model = new SettingsModel
        {
            DeviceName = dto.DeviceName,
            LatencyFrames = dto.LatencyFrames,
            PollReceiverVolume = dto.PollReceiverVolume,
            PreferredStream = dto.PreferredStream,
            ForgetOfflineAfterDays = dto.ForgetOfflineAfterDays ?? DefaultForgetOfflineAfterDays,
        };
        model.TakeAirplay(dto);
        return model;
    }

    /// <summary>
    /// To the DTO (names trimmed). SPEC-receiver.md 9.1 null-keeps: the password is the typed text, <c>""</c> after
    /// <see cref="RemoveAirplayPassword"/> with an empty field, and null (keep) for an untouched field;
    /// <c>AirplayPasswordSet</c> is never sent, and every AirPlay field is null when <see cref="AirplayOffered"/> is false.
    /// </summary>
    public SettingsDto ToDto() => new(
        DeviceName.Trim(),
        LatencyFrames,
        PollReceiverVolume,
        PreferredStream,
        ForgetOfflineAfterDays,
        AirplayOffered ? AirplayEnabled : null,
        AirplayOffered ? AirplayName?.Trim() : null,
        !AirplayOffered ? null : _airplayPassword.Length > 0 ? _airplayPassword : AirplayPasswordRemoved ? string.Empty : null);

    private void TakeAirplay(SettingsDto dto)
    {
        // An answer of a service with the receiver always carries the name (SPEC-receiver.md 9.1).
        AirplayOffered = dto.AirplayName is not null;
        AirplayEnabled = dto.AirplayEnabled ?? false;
        AirplayName = dto.AirplayName;
        AirplayPasswordSet = dto.AirplayPasswordSet ?? false;
        _airplayPassword = string.Empty;
        AirplayPasswordRemoved = false;
    }

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

    /// <summary>
    /// <see cref="RequiredAttribute"/> (no whitespace-only text) with a catalog message (<see cref="CatalogMessage"/>) that
    /// accepts null: a field the service did not offer (<see cref="AirplayOffered"/>) is not validated.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    private sealed class CatalogNotEmptyAttribute : RequiredAttribute
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
            value is null ? ValidationResult.Success : CatalogMessage.Localize(base.IsValid(value, validationContext), validationContext);
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
