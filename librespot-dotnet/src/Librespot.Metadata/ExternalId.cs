// Port of metadata/src/external_id.rs.
using ProtoExternalId = Spotify.Metadata.ExternalId;

namespace Librespot.Metadata;

/// <summary>An external identifier of an item.</summary>
/// <param name="ExternalType">The kind of id (e.g. <c>isrc</c>, <c>upc</c>).</param>
/// <param name="Id">This can be anything from a URL to a ISRC, EAN or UPC.</param>
public sealed record ExternalId(string ExternalType, string Id)
{
    /// <summary>Upstream <c>From&lt;&amp;ExternalIdMessage&gt;</c>.</summary>
    public static ExternalId From(ProtoExternalId externalId) => new(externalId.Type, externalId.Id);
}

/// <summary>Upstream <c>ExternalIds(Vec&lt;ExternalId&gt;)</c>.</summary>
public sealed class ExternalIds : List<ExternalId>
{
    /// <summary>Creates an empty list.</summary>
    public ExternalIds()
    {
    }

    /// <summary>Wraps the given ids.</summary>
    public ExternalIds(IEnumerable<ExternalId> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_from_repeated!(ExternalIdMessage, ExternalIds)</c>.</summary>
    public ExternalIds(IEnumerable<ProtoExternalId> messages) : base(messages.Select(ExternalId.From))
    {
    }
}
