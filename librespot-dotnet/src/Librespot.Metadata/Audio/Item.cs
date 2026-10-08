// Port of metadata/src/audio/item.rs (shape only; AudioItem.GetFileAsync is TODO(port: metadata)).
using Librespot.Core;
using Librespot.Metadata.Audio;

namespace Librespot.Metadata.Audio;

public sealed record CoverImage(string Url, Spotify.Metadata.Image.Types.Size Size, int Width, int Height);

/// <summary>What the player needs to know about a track, episode or local file.</summary>
public sealed record AudioItem
{
    public required SpotifyUri TrackId { get; init; }
    public required string Uri { get; init; }
    public required AudioFiles Files { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<CoverImage> Covers { get; init; } = [];
    public IReadOnlyList<string> Language { get; init; } = [];
    public uint DurationMs { get; init; }
    public bool IsExplicit { get; init; }

    /// <summary>Upstream <c>AudioItemAvailability = Result&lt;(), UnavailabilityReason&gt;</c>: null means available.</summary>
    public UnavailabilityReason? Availability { get; init; }

    public Tracks? Alternatives { get; init; }
    public required UniqueFields UniqueFields { get; init; }

    /// <summary>Upstream <c>AudioItem::get_file(session, uri)</c>: Track / Episode / Local lookup.</summary>
    public static Task<AudioItem> GetFileAsync(Session session, SpotifyUri uri, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: metadata)");
}

/// <summary>Upstream <c>UniqueFields</c> enum.</summary>
public abstract record UniqueFields
{
    private UniqueFields()
    {
    }

    public sealed record Track(ArtistsWithRole Artists, string Album, IReadOnlyList<string> AlbumArtists, byte Popularity, uint Number, uint DiscNumber) : UniqueFields;

    /// <remarks>Artists / album artists come from file metadata as single strings; no assumptions about separators.</remarks>
    public sealed record Local(string? Artists, string? Album, string? AlbumArtists, uint? Number, uint? DiscNumber, string Path) : UniqueFields;

    public sealed record Episode(string Description, Date PublishTime, string ShowName) : UniqueFields;
}
