// Port of metadata/src/sale_period.rs.
using Librespot.Core;
using ProtoSalePeriod = Spotify.Metadata.SalePeriod;

namespace Librespot.Metadata;

/// <summary>A period during which an item is on sale, with its restrictions.</summary>
/// <param name="Restrictions">Restrictions that apply during the period.</param>
/// <param name="Start">Start of the period.</param>
/// <param name="End">End of the period.</param>
public sealed record SalePeriod(Restrictions Restrictions, Date Start, Date End)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;SalePeriodMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">A date is out of range.</exception>
    public static SalePeriod From(ProtoSalePeriod salePeriod) =>
        new(
            new Restrictions(salePeriod.Restriction),
            Date.From(Util.OrDefault(salePeriod.Start)),
            Date.From(Util.OrDefault(salePeriod.End)));
}

/// <summary>Upstream <c>SalePeriods(Vec&lt;SalePeriod&gt;)</c>.</summary>
public sealed class SalePeriods : List<SalePeriod>
{
    /// <summary>Creates an empty list.</summary>
    public SalePeriods()
    {
    }

    /// <summary>Wraps the given periods.</summary>
    public SalePeriods(IEnumerable<SalePeriod> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(SalePeriodMessage, SalePeriods)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public SalePeriods(IEnumerable<ProtoSalePeriod> messages) : base(messages.Select(SalePeriod.From))
    {
    }
}
