// Port of core/src/spotify_uri.rs.
using System.Globalization;
using Spotify.Metadata;
using Spotify.Playlist4.Proto;
using SpotifyPlaylistAnnotate3.Proto;

namespace Librespot.Core;

/// <summary>The upstream <c>SpotifyUriError</c> variants.</summary>
public enum SpotifyUriError
{
    /// <summary>Not a valid Spotify URI.</summary>
    InvalidFormat,
    /// <summary>URI does not belong to Spotify.</summary>
    InvalidRoot,
}

/// <summary>Thrown when a Spotify URI cannot be parsed; always <see cref="ErrorKind.InvalidArgument"/>.</summary>
public sealed class SpotifyUriException : LibrespotException
{
    public SpotifyUriError Error { get; }

    public SpotifyUriException(SpotifyUriError error)
        : base(ErrorKind.InvalidArgument, Describe(error))
    {
        Error = error;
    }

    private static string Describe(SpotifyUriError error) => error switch
    {
        SpotifyUriError.InvalidFormat => "not a valid Spotify URI",
        SpotifyUriError.InvalidRoot => "URI does not belong to Spotify",
        _ => "not a valid Spotify URI",
    };
}

/// <summary>
/// A parsed Spotify URI (<c>spotify:{type}:{id}</c>). Upstream this is an enum; here it is a closed
/// record hierarchy, so pattern-match on the nested types (<see cref="Track"/>, <see cref="Local"/>, ...).
/// </summary>
public abstract record SpotifyUri
{
    private const string ItemTypeAlbum = "album";
    private const string ItemTypeArtist = "artist";
    private const string ItemTypeEpisode = "episode";
    private const string ItemTypePlaylist = "playlist";
    private const string ItemTypeShow = "show";
    private const string ItemTypeTrack = "track";
    private const string ItemTypeLocal = "local";
    private const string ItemTypeUnknown = "unknown";

    private SpotifyUri()
    {
    }

    public sealed record Album(SpotifyId Id) : SpotifyUri;

    public sealed record Artist(SpotifyId Id) : SpotifyUri;

    public sealed record Episode(SpotifyId Id) : SpotifyUri;

    /// <param name="User">The owner for named playlists (<c>spotify:user:{user}:playlist:{id}</c>), else null.</param>
    /// <param name="Id">The playlist id.</param>
    public sealed record Playlist(string? User, SpotifyId Id) : SpotifyUri;

    public sealed record Show(SpotifyId Id) : SpotifyUri;

    public sealed record Track(SpotifyId Id) : SpotifyUri;

    /// <summary>A local file: <c>spotify:local:{artist}:{album_title}:{track_title}:{duration_in_seconds}</c>.</summary>
    /// <remarks>The artist field is named <c>ArtistName</c> because <c>Artist</c> is the sibling record type.</remarks>
    public sealed record Local(string ArtistName, string AlbumTitle, string TrackTitle, TimeSpan Duration) : SpotifyUri;

    /// <summary>Any other item type the library does not know; <paramref name="Id"/> is kept verbatim.</summary>
    public sealed record Unknown(string Kind, string Id) : SpotifyUri;

    /// <summary>Returns whether this URI is for a playable audio item, if known.</summary>
    public bool IsPlayable => this is Episode or Track or Local;

    /// <summary>Gets the item type of this URI as a static string.</summary>
    public string ItemType => this switch
    {
        Album => ItemTypeAlbum,
        Artist => ItemTypeArtist,
        Episode => ItemTypeEpisode,
        Playlist => ItemTypePlaylist,
        Show => ItemTypeShow,
        Track => ItemTypeTrack,
        Local => ItemTypeLocal,
        _ => ItemTypeUnknown,
    };

    /// <summary>
    /// Gets the ID of this URI: the component that identifies the resource after its type label. For named
    /// playlists the user is omitted.
    /// </summary>
    public string ToId() => this switch
    {
        Album a => a.Id.ToBase62(),
        Artist a => a.Id.ToBase62(),
        Episode e => e.Id.ToBase62(),
        Playlist p => p.Id.ToBase62(),
        Show s => s.Id.ToBase62(),
        Track t => t.Id.ToBase62(),
        Local l => $"{l.ArtistName}:{l.AlbumTitle}:{l.TrackTitle}:{(long)l.Duration.TotalSeconds}",
        Unknown u => u.Id,
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// Parses a Spotify URI. <paramref name="src"/> is expected to be in the canonical form
    /// <c>spotify:{type}:{id}</c> (or <c>spotify:user:{user}:{type}:{id}</c>, or the local-file form).
    /// For most item types a 22-character base62 Spotify ID is expected; unknown types keep the id verbatim.
    /// </summary>
    public static SpotifyUri FromUri(string src)
    {
        var parts = src.Split(':');
        var i = 0;

        string Next() => i < parts.Length ? parts[i++] : throw new SpotifyUriException(SpotifyUriError.InvalidFormat);

        var scheme = Next();
        if (scheme != "spotify")
        {
            throw new SpotifyUriException(SpotifyUriError.InvalidRoot);
        }

        string? username = null;
        var itemType = Next();
        if (itemType == "user")
        {
            username = Next();
            itemType = Next();
        }

        var name = Next();

        switch (itemType)
        {
            case ItemTypeAlbum:
                return new Album(SpotifyId.FromBase62(name));
            case ItemTypeArtist:
                return new Artist(SpotifyId.FromBase62(name));
            case ItemTypeEpisode:
                return new Episode(SpotifyId.FromBase62(name));
            case ItemTypePlaylist:
                return new Playlist(username, SpotifyId.FromBase62(name));
            case ItemTypeShow:
                return new Show(SpotifyId.FromBase62(name));
            case ItemTypeTrack:
                return new Track(SpotifyId.FromBase62(name));
            case ItemTypeLocal:
            {
                var artist = name;
                var albumTitle = Next();
                var trackTitle = Next();
                var durationText = Next();
                if (!ulong.TryParse(durationText, NumberStyles.None, CultureInfo.InvariantCulture, out var durationSecs))
                {
                    throw new SpotifyUriException(SpotifyUriError.InvalidFormat);
                }

                return new Local(artist, albumTitle, trackTitle, TimeSpan.FromSeconds(durationSecs));
            }
            default:
                return new Unknown(itemType, name);
        }
    }

    /// <summary>
    /// Returns the URI in canonical form <c>spotify:{type}:{id}</c>; unknown item types keep their kind
    /// string, named playlists are returned as <c>spotify:user:{user}:{type}:{id}</c>.
    /// </summary>
    public string ToUri()
    {
        if (this is Playlist { User: { } user } p)
        {
            return $"spotify:user:{user}:{ItemType}:{p.Id.ToBase62()}";
        }

        return $"spotify:{ItemType}:{ToId()}";
    }

    /// <summary>Upstream <c>Display</c>: the canonical URI.</summary>
    public sealed override string ToString() => ToUri();

    // ---- TryFrom<&protocol::...> impls ---------------------------------------------------------------------------

    public static SpotifyUri From(Spotify.Metadata.Album album) => new Album(SpotifyId.FromRaw(album.Gid.Span));

    public static SpotifyUri From(Spotify.Metadata.Artist artist) => new Artist(SpotifyId.FromRaw(artist.Gid.Span));

    public static SpotifyUri From(Spotify.Metadata.Episode episode) => new Episode(SpotifyId.FromRaw(episode.Gid.Span));

    public static SpotifyUri From(Spotify.Metadata.Track track) => new Track(SpotifyId.FromRaw(track.Gid.Span));

    public static SpotifyUri From(Spotify.Metadata.Show show) => new Show(SpotifyId.FromRaw(show.Gid.Span));

    public static SpotifyUri From(ArtistWithRole artist) => new Artist(SpotifyId.FromRaw(artist.ArtistGid.Span));

    public static SpotifyUri From(Item item) => FromUri(item.Uri);

    /// <summary>Note: this is the unique revision of an item's metadata on a playlist, not the id of that item or playlist.</summary>
    public static SpotifyUri From(MetaItem item) => new Unknown("MetaItem", SpotifyId.FromRaw(item.Revision.Span).ToBase62());

    /// <summary>Note: this is the unique revision of a playlist, not the id of that playlist.</summary>
    public static SpotifyUri From(SelectedListContent playlist) => new Unknown("SelectedListContent", SpotifyId.FromRaw(playlist.Revision.Span).ToBase62());

    /// <summary>Upstream TODO (kept as is): check meaning and format of this field in the wild; it might be a FileId.</summary>
    public static SpotifyUri From(TranscodedPicture picture) => new Unknown("TranscodedPicture", picture.Uri);
}
