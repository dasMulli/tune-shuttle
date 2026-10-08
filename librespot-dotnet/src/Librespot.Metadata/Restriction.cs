// Port of metadata/src/restriction.rs.
using ProtoRestriction = Spotify.Metadata.Restriction;
using RestrictionCatalogue = Spotify.Metadata.Restriction.Types.Catalogue;
using RestrictionType = Spotify.Metadata.Restriction.Types.Type;

namespace Librespot.Metadata;

/// <summary>
/// A catalogue/country restriction of an item. A restriction specifies either a whitelist
/// (<see cref="CountriesAllowed"/>) or a blacklist (<see cref="CountriesForbidden"/>), not both.
/// </summary>
/// <remarks>
/// Upstream re-exports <c>RestrictionCatalogue</c> (<c>Spotify.Metadata.Restriction.Types.Catalogue</c>) and
/// <c>RestrictionType</c> (<c>Spotify.Metadata.Restriction.Types.Type</c>); the generated enums are used directly.
/// </remarks>
public sealed record Restriction
{
    /// <summary>The restricted catalogues as enum values.</summary>
    public required RestrictionCatalogues Catalogues { get; init; }

    /// <summary>The restriction type.</summary>
    public required RestrictionType RestrictionType { get; init; }

    /// <summary>The restricted catalogues as strings (e.g. <c>premium</c>), matched against the user's <c>catalogue</c> attribute.</summary>
    public required IReadOnlyList<string> CatalogueStrs { get; init; }

    /// <summary>Two-letter codes of the countries the item is allowed in, if a whitelist is present.</summary>
    public IReadOnlyList<string>? CountriesAllowed { get; init; }

    /// <summary>Two-letter codes of the countries the item is forbidden in, if a blacklist is present.</summary>
    public IReadOnlyList<string>? CountriesForbidden { get; init; }

    /// <summary>Upstream <c>parse_country_codes</c>: splits a concatenation of two-letter country codes.</summary>
    /// <remarks>
    /// Deviation: upstream slices <c>&amp;data[..2]</c> and panics on an odd-length string; here the trailing
    /// single character becomes its own chunk.
    /// </remarks>
    internal static List<string> ParseCountryCodes(string countryCodes)
    {
        var result = new List<string>((countryCodes.Length + 1) / 2);
        for (var i = 0; i < countryCodes.Length; i += 2)
        {
            result.Add(countryCodes.Substring(i, Math.Min(2, countryCodes.Length - i)));
        }

        return result;
    }

    /// <summary>Upstream <c>From&lt;&amp;RestrictionMessage&gt;</c>.</summary>
    public static Restriction From(ProtoRestriction restriction)
    {
        var countriesAllowed = restriction.HasCountriesAllowed
            ? ParseCountryCodes(restriction.CountriesAllowed)
            : null;

        var countriesForbidden = restriction.HasCountriesForbidden
            ? ParseCountryCodes(restriction.CountriesForbidden)
            : null;

        return new Restriction
        {
            Catalogues = new RestrictionCatalogues(restriction.Catalogue),
            RestrictionType = Util.EnumOrDefault(restriction.Type, RestrictionType.Streaming),
            CatalogueStrs = restriction.CatalogueStr.ToList(),
            CountriesAllowed = countriesAllowed,
            CountriesForbidden = countriesForbidden,
        };
    }
}

/// <summary>Upstream <c>Restrictions(Vec&lt;Restriction&gt;)</c>.</summary>
public sealed class Restrictions : List<Restriction>
{
    /// <summary>Creates an empty list.</summary>
    public Restrictions()
    {
    }

    /// <summary>Wraps the given restrictions.</summary>
    public Restrictions(IEnumerable<Restriction> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_from_repeated!(RestrictionMessage, Restrictions)</c>.</summary>
    public Restrictions(IEnumerable<ProtoRestriction> messages) : base(messages.Select(Restriction.From))
    {
    }
}

/// <summary>Upstream <c>RestrictionCatalogues(Vec&lt;RestrictionCatalogue&gt;)</c>.</summary>
public sealed class RestrictionCatalogues : List<RestrictionCatalogue>
{
    /// <summary>Creates an empty list.</summary>
    public RestrictionCatalogues()
    {
    }

    /// <summary>
    /// Upstream <c>impl_from_repeated_copy!(RestrictionCatalogue, RestrictionCatalogues)</c>, fed with
    /// <c>enum_value_or_default</c> values: unknown catalogue numbers become the default (<c>AD</c>).
    /// </summary>
    public RestrictionCatalogues(IEnumerable<RestrictionCatalogue> items)
        : base(items.Select(c => Util.EnumOrDefault(c)))
    {
    }
}
