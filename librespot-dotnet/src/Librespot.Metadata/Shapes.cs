// Shapes from several metadata modules that the AudioItem / PlayerEvent contracts depend on.
// Each type's full port (and the rest of its module) belongs to package `metadata`:
//   artist.rs       -> ArtistWithRole, ArtistsWithRole (Artist, Artists, TopTracks, ... TODO)
//   track.rs        -> Tracks (Track TODO)
//   availability.rs -> UnavailabilityReason (Availability, Availabilities TODO)
//   image.rs        -> ImageSize alias (Image, Images, PictureSize, TranscodedPicture TODO)
// Porters: split these into the matching file names (Artist.cs, Track.cs, Availability.cs, Image.cs) when porting
// and delete this file. The type names AND the namespace (Librespot.Metadata) must stay exactly as they are:
// Librespot.Playback/Player.cs and Audio/Item.cs (foundation) already reference them.
using Librespot.Core;

namespace Librespot.Metadata;

/// <summary>Upstream <c>pub use protocol::metadata::image::Size as ImageSize</c>.</summary>
public static class ImageSize
{
    public static Spotify.Metadata.Image.Types.Size Default => Spotify.Metadata.Image.Types.Size.Default;
}

public sealed record ArtistWithRole(SpotifyUri Id, string Name, Spotify.Metadata.ArtistWithRole.Types.ArtistRole Role);

/// <summary>Upstream <c>ArtistsWithRole(Vec&lt;ArtistWithRole&gt;)</c>.</summary>
public sealed class ArtistsWithRole : List<ArtistWithRole>
{
    public ArtistsWithRole()
    {
    }

    public ArtistsWithRole(IEnumerable<ArtistWithRole> items) : base(items)
    {
    }
}

/// <summary>Upstream <c>Tracks(Vec&lt;SpotifyUri&gt;)</c>.</summary>
public sealed class Tracks : List<SpotifyUri>
{
    public Tracks()
    {
    }

    public Tracks(IEnumerable<SpotifyUri> items) : base(items)
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

public static class UnavailabilityReasonExtensions
{
    public static string Describe(this UnavailabilityReason reason) => reason switch
    {
        UnavailabilityReason.Blacklisted => "blacklist present and country on it",
        UnavailabilityReason.Embargo => "available date is in the future",
        UnavailabilityReason.NoData => "required data was not present",
        UnavailabilityReason.NotWhitelisted => "whitelist present and country not on it",
        _ => reason.ToString(),
    };
}
