// Port of metadata/src/audio/item.rs.
using Librespot.Core;
using ImageSizeEnum = Spotify.Metadata.Image.Types.Size;

namespace Librespot.Metadata.Audio;

/// <summary>A cover image URL of an audio item.</summary>
/// <param name="Url">The image URL (the user's <c>image-url</c> template with the file id filled in).</param>
/// <param name="Size">The size class.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record CoverImage(string Url, ImageSizeEnum Size, int Width, int Height);

/// <summary>What the player needs to know about a track, episode or local file.</summary>
public sealed record AudioItem
{
    /// <summary>The item's URI.</summary>
    public required SpotifyUri TrackId { get; init; }

    /// <summary>The item's URI as a string.</summary>
    public required string Uri { get; init; }

    /// <summary>The audio files by format.</summary>
    public required AudioFiles Files { get; init; }

    /// <summary>Title.</summary>
    public required string Name { get; init; }

    /// <summary>Cover images, widest first.</summary>
    public IReadOnlyList<CoverImage> Covers { get; init; } = [];

    /// <summary>Languages.</summary>
    public IReadOnlyList<string> Language { get; init; } = [];

    /// <summary>Duration in milliseconds.</summary>
    public uint DurationMs { get; init; }

    /// <summary>Whether the item is marked explicit.</summary>
    public bool IsExplicit { get; init; }

    /// <summary>Upstream <c>AudioItemAvailability = Result&lt;(), UnavailabilityReason&gt;</c>: null means available.</summary>
    public UnavailabilityReason? Availability { get; init; }

    /// <summary>Alternative tracks, or null when there are none.</summary>
    public Tracks? Alternatives { get; init; }

    /// <summary>Fields specific to the item kind.</summary>
    public required UniqueFields UniqueFields { get; init; }

    /// <summary>
    /// Upstream <c>AudioItem::get_file(session, uri)</c>: fetches track or episode metadata and converts it.
    /// </summary>
    /// <remarks>
    /// As upstream, only track and episode URIs are handled; any other URI (including <c>spotify:local:</c>, whose
    /// <see cref="AudioItem"/> the player builds itself from the file's tags) fails with
    /// <see cref="MetadataException.Reason.NonPlayable"/>.
    /// </remarks>
    /// <exception cref="MetadataException">
    /// Non-positive duration, explicit content filtered by the user's setting, or a non-playable URI.
    /// </exception>
    /// <exception cref="LibrespotException">The metadata request or its conversion failed.</exception>
    public static async Task<AudioItem> GetFileAsync(Session session, SpotifyUri uri, CancellationToken cancellationToken = default)
    {
        var imageUrl = session.GetUserAttribute("image-url") ?? "https://i.scdn.co/image/{file_id}";

        switch (uri)
        {
            case SpotifyUri.Track:
            {
                var track = await Track.GetAsync(session, uri, cancellationToken).ConfigureAwait(false);

                if (track.Duration <= 0)
                {
                    throw MetadataException.InvalidDuration(track.Duration);
                }

                if (track.IsExplicit && session.FilterExplicitContent)
                {
                    throw MetadataException.ExplicitContentFiltered();
                }

                var uriString = uri.ToUri();
                var album = track.Album.Name;

                var albumArtists = track.Album.Artists.Select(a => a.Name).ToList();

                var covers = GetCovers(track.Album.Covers, imageUrl);

                var alternatives = track.Alternatives.Count == 0 ? null : track.Alternatives;

                var availability = Date.NowUtc().CompareTo(track.EarliestLiveTimestamp) < 0
                    ? UnavailabilityReason.Embargo
                    : AvailableForUser(session.UserData, track.Availability, track.Restrictions);

                var popularity = (byte)Math.Clamp(track.Popularity, 0, 100);
                var number = (uint)Math.Max(track.Number, 0);
                var discNumber = (uint)Math.Max(track.DiscNumber, 0);

                var uniqueFields = new UniqueFields.Track(
                    track.ArtistsWithRole,
                    album,
                    albumArtists,
                    popularity,
                    number,
                    discNumber);

                return new AudioItem
                {
                    TrackId = uri,
                    Uri = uriString,
                    Files = track.Files,
                    Name = track.Name,
                    Covers = covers,
                    Language = track.LanguageOfPerformance,
                    DurationMs = (uint)track.Duration,
                    IsExplicit = track.IsExplicit,
                    Availability = availability,
                    Alternatives = alternatives,
                    UniqueFields = uniqueFields,
                };
            }

            case SpotifyUri.Episode:
            {
                var episode = await Episode.GetAsync(session, uri, cancellationToken).ConfigureAwait(false);

                if (episode.Duration <= 0)
                {
                    throw MetadataException.InvalidDuration(episode.Duration);
                }

                if (episode.IsExplicit && session.FilterExplicitContent)
                {
                    throw MetadataException.ExplicitContentFiltered();
                }

                var uriString = uri.ToUri();

                var covers = GetCovers(episode.Covers, imageUrl);

                var availability = AvailableForUser(session.UserData, episode.Availability, episode.Restrictions);

                var uniqueFields = new UniqueFields.Episode(episode.Description, episode.PublishTime, episode.ShowName);

                return new AudioItem
                {
                    TrackId = uri,
                    Uri = uriString,
                    Files = episode.Audio,
                    Name = episode.Name,
                    Covers = covers,
                    Language = [episode.Language],
                    DurationMs = (uint)episode.Duration,
                    IsExplicit = episode.IsExplicit,
                    Availability = availability,
                    Alternatives = null,
                    UniqueFields = uniqueFields,
                };
            }

            default:
                throw MetadataException.NonPlayable();
        }
    }

    /// <summary>
    /// Upstream <c>get_covers</c>: sorts by width (widest first, stable) and fills <c>{file_id}</c> in the URL template,
    /// skipping images without an id.
    /// </summary>
    internal static List<CoverImage> GetCovers(Images covers, string imageUrl) =>
        covers
            .OrderByDescending(cover => cover.Width)
            .Select(cover => (cover, coverId: cover.Id.ToString()))
            .Where(x => x.coverId.Length != 0)
            .Select(x => new CoverImage(imageUrl.Replace("{file_id}", x.coverId, StringComparison.Ordinal), x.cover.Size, x.cover.Width, x.cover.Height))
            .ToList();

    /// <summary>Upstream <c>allowed_for_user</c>: checks the restrictions that apply to the user's catalogue.</summary>
    internal static UnavailabilityReason? AllowedForUser(UserData userData, Restrictions restrictions)
    {
        var country = userData.Country;
        var userCatalogue = userData.Attributes.TryGetValue("catalogue", out var catalogue) ? catalogue : "premium";

        foreach (var premiumRestriction in restrictions.Where(restriction =>
                     restriction.CatalogueStrs.Any(restrictedCatalogue => restrictedCatalogue == userCatalogue)))
        {
            if (premiumRestriction.CountriesAllowed is { } allowedCountries)
            {
                // A restriction will specify either a whitelast *or* a blacklist,
                // but not both. So restrict availability if there is a whitelist
                // and the country isn't on it.
                return allowedCountries.Any(allowed => country == allowed) ? null : UnavailabilityReason.NotWhitelisted;
            }

            if (premiumRestriction.CountriesForbidden is { } forbiddenCountries)
            {
                return forbiddenCountries.Any(forbidden => country == forbidden) ? UnavailabilityReason.Blacklisted : null;
            }
        }

        return null; // no restrictions in place
    }

    /// <summary>Upstream <c>available</c>: embargoed unless at least one availability window has started.</summary>
    internal static UnavailabilityReason? Available(Availabilities availability)
    {
        if (availability.Count == 0)
        {
            // not all items have availability specified
            return null;
        }

        var now = Date.NowUtc();
        if (!availability.Any(a => now.CompareTo(a.Start) >= 0))
        {
            return UnavailabilityReason.Embargo;
        }

        return null;
    }

    /// <summary>Upstream <c>available_for_user</c>: <see cref="Available"/>, then <see cref="AllowedForUser"/>.</summary>
    internal static UnavailabilityReason? AvailableForUser(UserData userData, Availabilities availability, Restrictions restrictions) =>
        Available(availability) ?? AllowedForUser(userData, restrictions);
}

/// <summary>Upstream <c>UniqueFields</c> enum: the fields specific to tracks, local files and episodes.</summary>
public abstract record UniqueFields
{
    private UniqueFields()
    {
    }

    /// <summary>A Spotify track.</summary>
    /// <param name="Artists">The track's artists with roles.</param>
    /// <param name="Album">Album title.</param>
    /// <param name="AlbumArtists">Names of the album's artists.</param>
    /// <param name="Popularity">Popularity, clamped to 0..100.</param>
    /// <param name="Number">Track number (negative values become 0).</param>
    /// <param name="DiscNumber">Disc number (negative values become 0).</param>
    public sealed record Track(ArtistsWithRole Artists, string Album, IReadOnlyList<string> AlbumArtists, byte Popularity, uint Number, uint DiscNumber) : UniqueFields;

    /// <summary>A local file.</summary>
    /// <remarks>
    /// Artists / album artists can't be a list here, they are retrieved from metadata as a string, and we cannot make
    /// any assumptions about them being e.g. comma-separated.
    /// </remarks>
    /// <param name="Artists">Artists tag.</param>
    /// <param name="Album">Album tag.</param>
    /// <param name="AlbumArtists">Album artists tag.</param>
    /// <param name="Number">Track number tag.</param>
    /// <param name="DiscNumber">Disc number tag.</param>
    /// <param name="Path">Path of the file.</param>
    public sealed record Local(string? Artists, string? Album, string? AlbumArtists, uint? Number, uint? DiscNumber, string Path) : UniqueFields;

    /// <summary>A podcast episode.</summary>
    /// <param name="Description">Episode description.</param>
    /// <param name="PublishTime">Publication time.</param>
    /// <param name="ShowName">Name of the show.</param>
    public sealed record Episode(string Description, Date PublishTime, string ShowName) : UniqueFields;
}
