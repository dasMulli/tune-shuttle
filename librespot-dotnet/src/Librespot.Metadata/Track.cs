// Port of metadata/src/track.rs.
using Librespot.Core;
using Librespot.Metadata.Audio;
using ProtoTrack = Spotify.Metadata.Track;

namespace Librespot.Metadata;

/// <summary>Track metadata (upstream <c>Track</c>).</summary>
public sealed record Track : IMetadata<Track, ProtoTrack>
{
    /// <summary>The track URI.</summary>
    public required SpotifyUri Id { get; init; }

    /// <summary>Track title.</summary>
    public required string Name { get; init; }

    /// <summary>The album the track is on.</summary>
    public required Album Album { get; init; }

    /// <summary>The track's artists.</summary>
    public required Artists Artists { get; init; }

    /// <summary>Track number on its disc.</summary>
    public required int Number { get; init; }

    /// <summary>Disc number.</summary>
    public required int DiscNumber { get; init; }

    /// <summary>Duration in milliseconds.</summary>
    public required int Duration { get; init; }

    /// <summary>Popularity, 0..100.</summary>
    public required int Popularity { get; init; }

    /// <summary>Whether the track is marked explicit.</summary>
    public required bool IsExplicit { get; init; }

    /// <summary>External ids (ISRC, ...).</summary>
    public required ExternalIds ExternalIds { get; init; }

    /// <summary>Catalogue/country restrictions.</summary>
    public required Restrictions Restrictions { get; init; }

    /// <summary>The audio files by format.</summary>
    public required AudioFiles Files { get; init; }

    /// <summary>Alternative tracks (other releases) that may be playable when this one is not.</summary>
    public required Tracks Alternatives { get; init; }

    /// <summary>Sale periods.</summary>
    public required SalePeriods SalePeriods { get; init; }

    /// <summary>Preview audio files by format.</summary>
    public required AudioFiles Previews { get; init; }

    /// <summary>Tags.</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>The track is embargoed before this date.</summary>
    public required Date EarliestLiveTimestamp { get; init; }

    /// <summary>Whether lyrics are available.</summary>
    public required bool HasLyrics { get; init; }

    /// <summary>Availability windows.</summary>
    public required Availabilities Availability { get; init; }

    /// <summary>The licensor UUID, or <see cref="Guid.Empty"/> when absent or malformed.</summary>
    public required Guid Licensor { get; init; }

    /// <summary>Languages of the performance.</summary>
    public required IReadOnlyList<string> LanguageOfPerformance { get; init; }

    /// <summary>Content ratings.</summary>
    public required ContentRatings ContentRatings { get; init; }

    /// <summary>Original title.</summary>
    public required string OriginalTitle { get; init; }

    /// <summary>Version title (e.g. "Remastered").</summary>
    public required string VersionTitle { get; init; }

    /// <summary>Artists with their role on this track.</summary>
    public required ArtistsWithRole ArtistsWithRole { get; init; }

    /// <summary>Requests the track metadata via spclient (upstream <c>Metadata::request</c>).</summary>
    /// <exception cref="LibrespotException"><paramref name="trackUri"/> is not a track URI (<see cref="ErrorKind.InvalidArgument"/>).</exception>
    public static Task<byte[]> RequestAsync(Session session, SpotifyUri trackUri, CancellationToken cancellationToken = default)
    {
        if (trackUri is not SpotifyUri.Track)
        {
            return Task.FromException<byte[]>(LibrespotException.InvalidArgument("track_uri"));
        }

        return session.SpClient.GetTrackMetadataAsync(trackUri, cancellationToken);
    }

    /// <summary>Requests and parses the track metadata (upstream <c>Metadata::get</c>).</summary>
    public static Task<Track> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default) =>
        MetadataRequest.GetAsync<Track, ProtoTrack>(session, id, cancellationToken);

    /// <summary>Upstream <c>Metadata::parse</c>.</summary>
    public static Track Parse(ProtoTrack message, SpotifyUri id) => From(message);

    /// <summary>Upstream <c>TryFrom&lt;&amp;protocol::metadata::Track&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An id, date or nested item failed to convert.</exception>
    public static Track From(ProtoTrack track) => new()
    {
        Id = SpotifyUri.From(track),
        Name = track.Name,
        Album = Album.From(Util.OrDefault(track.Album)),
        Artists = new Artists(track.Artist),
        Number = track.Number,
        DiscNumber = track.DiscNumber,
        Duration = track.Duration,
        Popularity = track.Popularity,
        IsExplicit = track.Explicit,
        ExternalIds = new ExternalIds(track.ExternalId),
        Restrictions = new Restrictions(track.Restriction),
        Files = new AudioFiles(track.File),
        Alternatives = new Tracks(track.Alternative),
        SalePeriods = new SalePeriods(track.SalePeriod),
        Previews = new AudioFiles(track.Preview),
        Tags = track.Tags.ToList(),
        EarliestLiveTimestamp = Date.FromTimestampMs(track.EarliestLiveTimestamp),
        HasLyrics = track.HasLyrics,
        Availability = new Availabilities(track.Availability),
        Licensor = ParseLicensor(Util.OrDefault(track.Licensor).Uuid.Span),
        LanguageOfPerformance = track.LanguageOfPerformance.ToList(),
        ContentRatings = new ContentRatings(track.ContentRating),
        OriginalTitle = track.OriginalTitle,
        VersionTitle = track.VersionTitle,
        ArtistsWithRole = new ArtistsWithRole(track.ArtistWithRole),
    };

    /// <summary>Upstream <c>Uuid::from_slice(..).unwrap_or_else(|_| Uuid::nil())</c>: 16 big-endian bytes, else nil.</summary>
    private static Guid ParseLicensor(ReadOnlySpan<byte> uuid) =>
        uuid.Length == 16 ? new Guid(uuid, bigEndian: true) : Guid.Empty;
}

/// <summary>Upstream <c>Tracks(Vec&lt;SpotifyUri&gt;)</c>.</summary>
public sealed class Tracks : List<SpotifyUri>
{
    /// <summary>Creates an empty list.</summary>
    public Tracks()
    {
    }

    /// <summary>Wraps the given URIs.</summary>
    public Tracks(IEnumerable<SpotifyUri> items) : base(items)
    {
    }

    /// <summary>
    /// Upstream <c>impl_try_from_repeated!(protocol::metadata::Track, Tracks)</c>: only the URI of each track is
    /// kept (<c>TryFrom&lt;&amp;Track&gt; for SpotifyUri</c>).
    /// </summary>
    /// <exception cref="LibrespotException">A track gid is not a valid Spotify id.</exception>
    public Tracks(IEnumerable<ProtoTrack> messages) : base(messages.Select(SpotifyUri.From))
    {
    }
}
