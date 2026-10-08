// Port of metadata/src/content_rating.rs.
using ProtoContentRating = Spotify.Metadata.ContentRating;

namespace Librespot.Metadata;

/// <summary>A content rating (age rating tags) for one country.</summary>
/// <param name="Country">The country the rating applies to.</param>
/// <param name="Tags">The rating tags.</param>
public sealed record ContentRating(string Country, IReadOnlyList<string> Tags)
{
    /// <summary>Upstream <c>From&lt;&amp;ContentRatingMessage&gt;</c>.</summary>
    public static ContentRating From(ProtoContentRating contentRating) =>
        new(contentRating.Country, contentRating.Tag.ToList());
}

/// <summary>Upstream <c>ContentRatings(Vec&lt;ContentRating&gt;)</c>.</summary>
public sealed class ContentRatings : List<ContentRating>
{
    /// <summary>Creates an empty list.</summary>
    public ContentRatings()
    {
    }

    /// <summary>Wraps the given ratings.</summary>
    public ContentRatings(IEnumerable<ContentRating> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_from_repeated!(ContentRatingMessage, ContentRatings)</c>.</summary>
    public ContentRatings(IEnumerable<ProtoContentRating> messages) : base(messages.Select(ContentRating.From))
    {
    }
}
