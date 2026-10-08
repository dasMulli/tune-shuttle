// Port of metadata/src/album.rs.
using Librespot.Core;
using AlbumType = Spotify.Metadata.Album.Types.Type;
using ProtoAlbum = Spotify.Metadata.Album;
using ProtoDisc = Spotify.Metadata.Disc;

namespace Librespot.Metadata;

/// <summary>Album metadata (upstream <c>Album</c>).</summary>
/// <remarks>Upstream re-exports <c>AlbumType</c>; it is the generated <c>Spotify.Metadata.Album.Types.Type</c>.</remarks>
public sealed record Album : IMetadata<Album, ProtoAlbum>
{
    /// <summary>The album URI.</summary>
    public required SpotifyUri Id { get; init; }

    /// <summary>Album title.</summary>
    public required string Name { get; init; }

    /// <summary>The album's artists.</summary>
    public required Artists Artists { get; init; }

    /// <summary>Album type (album, single, compilation, ...).</summary>
    public required AlbumType AlbumType { get; init; }

    /// <summary>Record label.</summary>
    public required string Label { get; init; }

    /// <summary>Release date.</summary>
    public required Date Date { get; init; }

    /// <summary>Popularity.</summary>
    public required int Popularity { get; init; }

    /// <summary>Cover images (from the cover group).</summary>
    public required Images Covers { get; init; }

    /// <summary>External ids (UPC, ...).</summary>
    public required ExternalIds ExternalIds { get; init; }

    /// <summary>The discs and their tracks.</summary>
    public required Discs Discs { get; init; }

    /// <summary>Reviews.</summary>
    public required IReadOnlyList<string> Reviews { get; init; }

    /// <summary>Copyright notices.</summary>
    public required Copyrights Copyrights { get; init; }

    /// <summary>Catalogue/country restrictions.</summary>
    public required Restrictions Restrictions { get; init; }

    /// <summary>Related albums.</summary>
    public required Albums Related { get; init; }

    /// <summary>Sale periods.</summary>
    public required SalePeriods SalePeriods { get; init; }

    /// <summary>The cover group images.</summary>
    public required Images CoverGroup { get; init; }

    /// <summary>Original title.</summary>
    public required string OriginalTitle { get; init; }

    /// <summary>Version title.</summary>
    public required string VersionTitle { get; init; }

    /// <summary>Album type as a string.</summary>
    public required string TypeStr { get; init; }

    /// <summary>Availability windows.</summary>
    public required Availabilities Availability { get; init; }

    /// <summary>All track URIs of all discs, in order (upstream <c>tracks()</c>).</summary>
    public IEnumerable<SpotifyUri> Tracks() => Discs.SelectMany(disc => disc.Tracks);

    /// <summary>Requests the album metadata via spclient (upstream <c>Metadata::request</c>).</summary>
    /// <exception cref="LibrespotException"><paramref name="albumUri"/> is not an album URI (<see cref="ErrorKind.InvalidArgument"/>).</exception>
    public static Task<byte[]> RequestAsync(Session session, SpotifyUri albumUri, CancellationToken cancellationToken = default)
    {
        if (albumUri is not SpotifyUri.Album)
        {
            return Task.FromException<byte[]>(LibrespotException.InvalidArgument("album_uri"));
        }

        return session.SpClient.GetAlbumMetadataAsync(albumUri, cancellationToken);
    }

    /// <summary>Requests and parses the album metadata (upstream <c>Metadata::get</c>).</summary>
    public static Task<Album> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default) =>
        MetadataRequest.GetAsync<Album, ProtoAlbum>(session, id, cancellationToken);

    /// <summary>Upstream <c>Metadata::parse</c>.</summary>
    public static Album Parse(ProtoAlbum message, SpotifyUri id) => From(message);

    /// <summary>Upstream <c>TryFrom&lt;&amp;protocol::metadata::Album&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An id, date or nested item failed to convert.</exception>
    public static Album From(ProtoAlbum album)
    {
        var coverGroup = Util.OrDefault(album.CoverGroup);
        return new Album
        {
            Id = SpotifyUri.From(album),
            Name = album.Name,
            Artists = new Artists(album.Artist),
            AlbumType = Util.EnumOrDefault(album.Type, AlbumType.Album),
            Label = album.Label,
            Date = Date.From(Util.OrDefault(album.Date)),
            Popularity = album.Popularity,
            Covers = new Images(coverGroup),
            ExternalIds = new ExternalIds(album.ExternalId),
            Discs = new Discs(album.Disc),
            Reviews = album.Review.ToList(),
            Copyrights = new Copyrights(album.Copyright),
            Restrictions = new Restrictions(album.Restriction),
            Related = new Albums(album.Related),
            SalePeriods = new SalePeriods(album.SalePeriod),
            CoverGroup = new Images(coverGroup.Image),
            OriginalTitle = album.OriginalTitle,
            VersionTitle = album.VersionTitle,
            TypeStr = album.TypeStr,
            Availability = new Availabilities(album.Availability),
        };
    }
}

/// <summary>Upstream <c>Albums(Vec&lt;SpotifyUri&gt;)</c>.</summary>
public sealed class Albums : List<SpotifyUri>
{
    /// <summary>Creates an empty list.</summary>
    public Albums()
    {
    }

    /// <summary>Wraps the given URIs.</summary>
    public Albums(IEnumerable<SpotifyUri> items) : base(items)
    {
    }

    /// <summary>
    /// Upstream <c>impl_try_from_repeated!(protocol::metadata::Album, Albums)</c>: only the URI of each album is kept.
    /// </summary>
    /// <exception cref="LibrespotException">An album gid is not a valid Spotify id.</exception>
    public Albums(IEnumerable<ProtoAlbum> messages) : base(messages.Select(SpotifyUri.From))
    {
    }
}

/// <summary>One disc of an album.</summary>
/// <param name="Number">Disc number.</param>
/// <param name="Name">Disc name.</param>
/// <param name="Tracks">The disc's track URIs.</param>
public sealed record Disc(int Number, string Name, Tracks Tracks)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;DiscMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">A track gid is not a valid Spotify id.</exception>
    public static Disc From(ProtoDisc disc) => new(disc.Number, disc.Name, new Tracks(disc.Track));
}

/// <summary>Upstream <c>Discs(Vec&lt;Disc&gt;)</c>.</summary>
public sealed class Discs : List<Disc>
{
    /// <summary>Creates an empty list.</summary>
    public Discs()
    {
    }

    /// <summary>Wraps the given discs.</summary>
    public Discs(IEnumerable<Disc> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(DiscMessage, Discs)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public Discs(IEnumerable<ProtoDisc> messages) : base(messages.Select(Disc.From))
    {
    }
}
