// Port of metadata/src/artist.rs.
using Librespot.Core;
using ArtistRole = Spotify.Metadata.ArtistWithRole.Types.ArtistRole;
using ProtoActivityPeriod = Spotify.Metadata.ActivityPeriod;
using ProtoAlbumGroup = Spotify.Metadata.AlbumGroup;
using ProtoArtist = Spotify.Metadata.Artist;
using ProtoArtistWithRole = Spotify.Metadata.ArtistWithRole;
using ProtoBiography = Spotify.Metadata.Biography;
using ProtoTopTracks = Spotify.Metadata.TopTracks;

namespace Librespot.Metadata;

/// <summary>Artist metadata (upstream <c>Artist</c>).</summary>
public sealed record Artist : IMetadata<Artist, ProtoArtist>
{
    /// <summary>The artist URI.</summary>
    public required SpotifyUri Id { get; init; }

    /// <summary>Artist name.</summary>
    public required string Name { get; init; }

    /// <summary>Popularity.</summary>
    public required int Popularity { get; init; }

    /// <summary>Top tracks per country.</summary>
    public required CountryTopTracks TopTracks { get; init; }

    /// <summary>Albums, grouped by release variants.</summary>
    public required AlbumGroups Albums { get; init; }

    /// <summary>Singles, grouped by release variants.</summary>
    public required AlbumGroups Singles { get; init; }

    /// <summary>Compilations, grouped by release variants.</summary>
    public required AlbumGroups Compilations { get; init; }

    /// <summary>Albums the artist appears on, grouped by release variants.</summary>
    public required AlbumGroups AppearsOnAlbums { get; init; }

    /// <summary>External ids.</summary>
    public required ExternalIds ExternalIds { get; init; }

    /// <summary>Portrait images.</summary>
    public required Images Portraits { get; init; }

    /// <summary>Biographies.</summary>
    public required Biographies Biographies { get; init; }

    /// <summary>Periods the artist was active.</summary>
    public required ActivityPeriods ActivityPeriods { get; init; }

    /// <summary>Catalogue/country restrictions.</summary>
    public required Restrictions Restrictions { get; init; }

    /// <summary>Related artists (fully parsed).</summary>
    public required Artists Related { get; init; }

    /// <summary>Whether the portrait is an album cover.</summary>
    public required bool IsPortraitAlbumCover { get; init; }

    /// <summary>The portrait group images.</summary>
    public required Images PortraitGroup { get; init; }

    /// <summary>Sale periods.</summary>
    public required SalePeriods SalesPeriods { get; init; }

    /// <summary>Availability windows.</summary>
    public required Availabilities Availabilities { get; init; }

    /// <summary>
    /// Get the full list of albums, not containing duplicate variants of the same albums.
    /// See also <see cref="AlbumGroups"/> and <see cref="AlbumGroups.CurrentReleases"/>.
    /// </summary>
    public IEnumerable<SpotifyUri> AlbumsCurrent() => Albums.CurrentReleases();

    /// <summary>
    /// Get the full list of singles, not containing duplicate variants of the same singles.
    /// See also <see cref="AlbumGroups"/> and <see cref="AlbumGroups.CurrentReleases"/>.
    /// </summary>
    public IEnumerable<SpotifyUri> SinglesCurrent() => Singles.CurrentReleases();

    /// <summary>
    /// Get the full list of compilations, not containing duplicate variants of the same compilations.
    /// See also <see cref="AlbumGroups"/> and <see cref="AlbumGroups.CurrentReleases"/>.
    /// </summary>
    public IEnumerable<SpotifyUri> CompilationsCurrent() => Compilations.CurrentReleases();

    /// <summary>
    /// Get the full list of albums, not containing duplicate variants of the same albums.
    /// See also <see cref="AlbumGroups"/> and <see cref="AlbumGroups.CurrentReleases"/>.
    /// </summary>
    public IEnumerable<SpotifyUri> AppearsOnAlbumsCurrent() => AppearsOnAlbums.CurrentReleases();

    /// <summary>Requests the artist metadata via spclient (upstream <c>Metadata::request</c>).</summary>
    /// <exception cref="LibrespotException"><paramref name="artistUri"/> is not an artist URI (<see cref="ErrorKind.InvalidArgument"/>).</exception>
    public static Task<byte[]> RequestAsync(Session session, SpotifyUri artistUri, CancellationToken cancellationToken = default)
    {
        if (artistUri is not SpotifyUri.Artist)
        {
            return Task.FromException<byte[]>(LibrespotException.InvalidArgument("artist_uri"));
        }

        return session.SpClient.GetArtistMetadataAsync(artistUri, cancellationToken);
    }

    /// <summary>Requests and parses the artist metadata (upstream <c>Metadata::get</c>).</summary>
    public static Task<Artist> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default) =>
        MetadataRequest.GetAsync<Artist, ProtoArtist>(session, id, cancellationToken);

    /// <summary>Upstream <c>Metadata::parse</c>.</summary>
    public static Artist Parse(ProtoArtist message, SpotifyUri id) => From(message);

    /// <summary>Upstream <c>TryFrom&lt;&amp;protocol::metadata::Artist&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An id, date or nested item failed to convert.</exception>
    public static Artist From(ProtoArtist artist) => new()
    {
        Id = SpotifyUri.From(artist),
        Name = artist.Name,
        Popularity = artist.Popularity,
        TopTracks = new CountryTopTracks(artist.TopTrack),
        Albums = new AlbumGroups(artist.AlbumGroup),
        Singles = new AlbumGroups(artist.SingleGroup),
        Compilations = new AlbumGroups(artist.CompilationGroup),
        AppearsOnAlbums = new AlbumGroups(artist.AppearsOnGroup),
        ExternalIds = new ExternalIds(artist.ExternalId),
        Portraits = new Images(artist.Portrait),
        Biographies = new Biographies(artist.Biography),
        ActivityPeriods = new ActivityPeriods(artist.ActivityPeriod),
        Restrictions = new Restrictions(artist.Restriction),
        Related = new Artists(artist.Related),
        IsPortraitAlbumCover = artist.IsPortraitAlbumCover,
        PortraitGroup = new Images(Util.OrDefault(artist.PortraitGroup).Image),
        SalesPeriods = new SalePeriods(artist.SalePeriod),
        Availabilities = new Availabilities(artist.Availability),
    };
}

/// <summary>Upstream <c>Artists(Vec&lt;Artist&gt;)</c>: fully parsed artists.</summary>
public sealed class Artists : List<Artist>
{
    /// <summary>Creates an empty list.</summary>
    public Artists()
    {
    }

    /// <summary>Wraps the given artists.</summary>
    public Artists(IEnumerable<Artist> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(protocol::metadata::Artist, Artists)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public Artists(IEnumerable<ProtoArtist> messages) : base(messages.Select(Artist.From))
    {
    }
}

/// <summary>An artist credited on a track, with their role.</summary>
/// <param name="Id">The artist URI.</param>
/// <param name="Name">The artist name.</param>
/// <param name="Role">The role (upstream re-exports <c>ArtistRole</c>).</param>
public sealed record ArtistWithRole(SpotifyUri Id, string Name, ArtistRole Role)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;ArtistWithRoleMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">The artist gid is not a valid Spotify id.</exception>
    public static ArtistWithRole From(ProtoArtistWithRole artistWithRole) =>
        new(SpotifyUri.From(artistWithRole), artistWithRole.ArtistName, Util.EnumOrDefault(artistWithRole.Role, ArtistRole.Unknown));
}

/// <summary>Upstream <c>ArtistsWithRole(Vec&lt;ArtistWithRole&gt;)</c>.</summary>
public sealed class ArtistsWithRole : List<ArtistWithRole>
{
    /// <summary>Creates an empty list.</summary>
    public ArtistsWithRole()
    {
    }

    /// <summary>Wraps the given artists.</summary>
    public ArtistsWithRole(IEnumerable<ArtistWithRole> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(ArtistWithRoleMessage, ArtistsWithRole)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public ArtistsWithRole(IEnumerable<ProtoArtistWithRole> messages) : base(messages.Select(ArtistWithRole.From))
    {
    }
}

/// <summary>The top tracks of an artist in one country (empty country: global).</summary>
/// <param name="Country">Country code, or empty for the global list.</param>
/// <param name="Tracks">The track URIs.</param>
public sealed record TopTracks(string Country, Tracks Tracks)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;TopTracksMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">A track gid is not a valid Spotify id.</exception>
    public static TopTracks From(ProtoTopTracks topTracks) => new(topTracks.Country, new Tracks(topTracks.Track));
}

/// <summary>Upstream <c>CountryTopTracks(Vec&lt;TopTracks&gt;)</c>.</summary>
public sealed class CountryTopTracks : List<TopTracks>
{
    /// <summary>Creates an empty list.</summary>
    public CountryTopTracks()
    {
    }

    /// <summary>Wraps the given lists.</summary>
    public CountryTopTracks(IEnumerable<TopTracks> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(TopTracksMessage, CountryTopTracks)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public CountryTopTracks(IEnumerable<ProtoTopTracks> messages) : base(messages.Select(TopTracks.From))
    {
    }

    /// <summary>
    /// The top tracks for <paramref name="country"/>, else the global list (empty country), else an empty list
    /// (upstream <c>for_country</c>).
    /// </summary>
    public Tracks ForCountry(string country)
    {
        if (this.FirstOrDefault(topTrack => topTrack.Country == country) is { } forCountry)
        {
            return new Tracks(forCountry.Tracks);
        }

        if (this.FirstOrDefault(topTrack => topTrack.Country.Length == 0) is { } global)
        {
            return new Tracks(global.Tracks);
        }

        return new Tracks(); // none found
    }
}

/// <summary>Upstream <c>AlbumGroup(Albums)</c>: the release variants of one album, current release first.</summary>
public sealed class AlbumGroup : List<SpotifyUri>
{
    /// <summary>Creates an empty group.</summary>
    public AlbumGroup()
    {
    }

    /// <summary>Wraps the given album URIs.</summary>
    public AlbumGroup(IEnumerable<SpotifyUri> items) : base(items)
    {
    }

    /// <summary>Upstream <c>TryFrom&lt;&amp;AlbumGroupMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An album gid is not a valid Spotify id.</exception>
    public AlbumGroup(ProtoAlbumGroup albumGroup) : base(new Albums(albumGroup.Album))
    {
    }
}

/// <summary>
/// <c>AlbumGroups</c> contains collections of album variants (different releases of the same album).
/// Ignoring the wrapping types it is structured roughly like this:
/// <code>
/// AlbumGroups [
///     [Album1], [Album2-relelease, Album2-older-release], [Album3]
/// ]
/// </code>
/// In most cases only the current variant of each album is needed. A list of every album in its
/// current release variant can be obtained by using <see cref="CurrentReleases"/>.
/// </summary>
public sealed class AlbumGroups : List<AlbumGroup>
{
    /// <summary>Creates an empty list.</summary>
    public AlbumGroups()
    {
    }

    /// <summary>Wraps the given groups.</summary>
    public AlbumGroups(IEnumerable<AlbumGroup> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(AlbumGroupMessage, AlbumGroups)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public AlbumGroups(IEnumerable<ProtoAlbumGroup> messages) : base(messages.Select(group => new AlbumGroup(group)))
    {
    }

    /// <summary>
    /// Get the contained albums. This will only use the latest release / variant of an album if
    /// multiple variants are available. This should be used if multiple variants of the same album
    /// are not explicitely desired.
    /// </summary>
    public IEnumerable<SpotifyUri> CurrentReleases() => this.Where(group => group.Count > 0).Select(group => group[0]);
}

/// <summary>An artist biography.</summary>
/// <param name="Text">Biography text.</param>
/// <param name="Portraits">Portrait images.</param>
/// <param name="PortraitGroup">Groups of portrait images.</param>
public sealed record Biography(string Text, Images Portraits, IReadOnlyList<Images> PortraitGroup)
{
    /// <summary>Upstream <c>From&lt;&amp;BiographyMessage&gt;</c>.</summary>
    public static Biography From(ProtoBiography biography)
    {
        var portraitGroup = biography.PortraitGroup.Select(group => new Images(group.Image)).ToList();
        return new Biography(biography.Text, new Images(biography.Portrait), portraitGroup);
    }
}

/// <summary>Upstream <c>Biographies(Vec&lt;Biography&gt;)</c>.</summary>
public sealed class Biographies : List<Biography>
{
    /// <summary>Creates an empty list.</summary>
    public Biographies()
    {
    }

    /// <summary>Wraps the given biographies.</summary>
    public Biographies(IEnumerable<Biography> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_from_repeated!(BiographyMessage, Biographies)</c>.</summary>
    public Biographies(IEnumerable<ProtoBiography> messages) : base(messages.Select(Biography.From))
    {
    }
}

/// <summary>A period an artist was active: either a timespan or a decade (upstream enum <c>ActivityPeriod</c>).</summary>
public abstract record ActivityPeriod
{
    private ActivityPeriod()
    {
    }

    /// <summary>Active from <paramref name="StartYear"/> until <paramref name="EndYear"/> (open-ended when null).</summary>
    /// <param name="StartYear">First active year.</param>
    /// <param name="EndYear">Last active year, if the period is closed.</param>
    public sealed record Timespan(ushort StartYear, ushort? EndYear) : ActivityPeriod;

    /// <summary>Active during a decade.</summary>
    /// <param name="Year">The decade, e.g. 1990.</param>
    public sealed record Decade(ushort Year) : ActivityPeriod;

    /// <summary>Upstream <c>TryFrom&lt;&amp;ActivityPeriodMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">
    /// The message is neither a decade nor a timespan, or a year does not fit in 16 bits
    /// (<see cref="ErrorKind.FailedPrecondition"/>).
    /// </exception>
    public static ActivityPeriod From(ProtoActivityPeriod period) =>
        (period.HasDecade, period.HasStartYear, period.HasEndYear) switch
        {
            // (decade, start_year, end_year)
            (true, false, false) => new Decade(Util.ToUInt16(period.Decade)),
            (false, true, var closedPeriod) => new Timespan(
                Util.ToUInt16(period.StartYear),
                closedPeriod ? Util.ToUInt16(period.EndYear) : null),
            _ => throw LibrespotException.FailedPrecondition("ActivityPeriod is expected to be either a decade or timespan"),
        };
}

/// <summary>Upstream <c>ActivityPeriods(Vec&lt;ActivityPeriod&gt;)</c>.</summary>
public sealed class ActivityPeriods : List<ActivityPeriod>
{
    /// <summary>Creates an empty list.</summary>
    public ActivityPeriods()
    {
    }

    /// <summary>Wraps the given periods.</summary>
    public ActivityPeriods(IEnumerable<ActivityPeriod> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(ActivityPeriodMessage, ActivityPeriods)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public ActivityPeriods(IEnumerable<ProtoActivityPeriod> messages) : base(messages.Select(ActivityPeriod.From))
    {
    }
}
