// Port of metadata/src/availability.rs.
using Librespot.Core;
using ProtoAvailability = Spotify.Metadata.Availability;

namespace Librespot.Metadata;

/// <summary>
/// When and in which catalogues an item is available. Upstream <c>AudioItemAvailability</c>
/// (<c>Result&lt;(), UnavailabilityReason&gt;</c>) is represented as <c>UnavailabilityReason?</c>: <c>null</c> means available.
/// </summary>
/// <param name="CatalogueStrs">The catalogues (e.g. <c>premium</c>, <c>free</c>) this availability applies to.</param>
/// <param name="Start">The item is available from this date on.</param>
public sealed record Availability(IReadOnlyList<string> CatalogueStrs, Date Start)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;AvailabilityMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">The start date is out of range.</exception>
    public static Availability From(ProtoAvailability availability) =>
        new(availability.CatalogueStr.ToList(), Date.From(Util.OrDefault(availability.Start)));
}

/// <summary>Upstream <c>Availabilities(Vec&lt;Availability&gt;)</c>.</summary>
public sealed class Availabilities : List<Availability>
{
    /// <summary>Creates an empty list.</summary>
    public Availabilities()
    {
    }

    /// <summary>Wraps the given availabilities.</summary>
    public Availabilities(IEnumerable<Availability> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(AvailabilityMessage, Availabilities)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public Availabilities(IEnumerable<ProtoAvailability> messages) : base(messages.Select(Availability.From))
    {
    }
}

/// <summary>Why an audio item is not playable (upstream <c>UnavailabilityReason</c>).</summary>
public enum UnavailabilityReason
{
    /// <summary>Blacklist present and country on it.</summary>
    Blacklisted,
    /// <summary>Available date is in the future.</summary>
    Embargo,
    /// <summary>Required data was not present.</summary>
    NoData,
    /// <summary>Whitelist present and country not on it.</summary>
    NotWhitelisted,
}

/// <summary>Display text of <see cref="UnavailabilityReason"/>.</summary>
public static class UnavailabilityReasonExtensions
{
    /// <summary>Upstream <c>#[error("...")]</c> text of the variant.</summary>
    public static string Describe(this UnavailabilityReason reason) => reason switch
    {
        UnavailabilityReason.Blacklisted => "blacklist present and country on it",
        UnavailabilityReason.Embargo => "available date is in the future",
        UnavailabilityReason.NoData => "required data was not present",
        UnavailabilityReason.NotWhitelisted => "whitelist present and country not on it",
        _ => reason.ToString(),
    };
}
