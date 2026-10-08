// Port of metadata/src/show.rs.
using Librespot.Core;
using ProtoShow = Spotify.Metadata.Show;
using ShowConsumptionOrder = Spotify.Metadata.Show.Types.ConsumptionOrder;
using ShowMediaType = Spotify.Metadata.Show.Types.MediaType;

namespace Librespot.Metadata;

/// <summary>Podcast / audiobook show metadata (upstream <c>Show</c>).</summary>
/// <remarks>
/// Upstream re-exports <c>ShowMediaType</c> and <c>ShowConsumptionOrder</c>; they are the generated
/// <c>Spotify.Metadata.Show.Types.MediaType</c> and <c>Spotify.Metadata.Show.Types.ConsumptionOrder</c>.
/// </remarks>
public sealed record Show : IMetadata<Show, ProtoShow>
{
    /// <summary>The show URI.</summary>
    public required SpotifyUri Id { get; init; }

    /// <summary>Show title.</summary>
    public required string Name { get; init; }

    /// <summary>Description.</summary>
    public required string Description { get; init; }

    /// <summary>Publisher.</summary>
    public required string Publisher { get; init; }

    /// <summary>Language.</summary>
    public required string Language { get; init; }

    /// <summary>Whether the show is marked explicit.</summary>
    public required bool IsExplicit { get; init; }

    /// <summary>Cover images.</summary>
    public required Images Covers { get; init; }

    /// <summary>The episode URIs.</summary>
    public required Episodes Episodes { get; init; }

    /// <summary>Copyright notices.</summary>
    public required Copyrights Copyrights { get; init; }

    /// <summary>Catalogue/country restrictions.</summary>
    public required Restrictions Restrictions { get; init; }

    /// <summary>Keywords.</summary>
    public required IReadOnlyList<string> Keywords { get; init; }

    /// <summary>Media type (mixed, audio, video).</summary>
    public required ShowMediaType MediaType { get; init; }

    /// <summary>Consumption order (sequential, episodic, recent).</summary>
    public required ShowConsumptionOrder ConsumptionOrder { get; init; }

    /// <summary>Availability windows.</summary>
    public required Availabilities Availability { get; init; }

    /// <summary>The trailer URI, if any.</summary>
    public SpotifyUri? TrailerUri { get; init; }

    /// <summary>Whether the show mixes music and talk.</summary>
    public required bool HasMusicAndTalk { get; init; }

    /// <summary>Whether this is an audiobook.</summary>
    public required bool IsAudiobook { get; init; }

    /// <summary>Requests the show metadata via spclient (upstream <c>Metadata::request</c>).</summary>
    /// <exception cref="LibrespotException"><paramref name="showUri"/> is not a show URI (<see cref="ErrorKind.InvalidArgument"/>).</exception>
    public static Task<byte[]> RequestAsync(Session session, SpotifyUri showUri, CancellationToken cancellationToken = default)
    {
        if (showUri is not SpotifyUri.Show)
        {
            return Task.FromException<byte[]>(LibrespotException.InvalidArgument("show_uri"));
        }

        return session.SpClient.GetShowMetadataAsync(showUri, cancellationToken);
    }

    /// <summary>Requests and parses the show metadata (upstream <c>Metadata::get</c>).</summary>
    public static Task<Show> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default) =>
        MetadataRequest.GetAsync<Show, ProtoShow>(session, id, cancellationToken);

    /// <summary>Upstream <c>Metadata::parse</c>.</summary>
    public static Show Parse(ProtoShow message, SpotifyUri id) => From(message);

    /// <summary>Upstream <c>TryFrom&lt;&amp;protocol::metadata::Show&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An id, URI or nested item failed to convert.</exception>
    public static Show From(ProtoShow show) => new()
    {
        Id = SpotifyUri.From(show),
        Name = show.Name,
        Description = show.Description,
        Publisher = show.Publisher,
        Language = show.Language,
        IsExplicit = show.Explicit,
        Covers = new Images(Util.OrDefault(show.CoverImage).Image),
        Episodes = new Episodes(show.Episode),
        Copyrights = new Copyrights(show.Copyright),
        Restrictions = new Restrictions(show.Restriction),
        Keywords = show.Keyword.ToList(),
        MediaType = Util.EnumOrDefault(show.MediaType, ShowMediaType.Mixed),
        ConsumptionOrder = Util.EnumOrDefault(show.ConsumptionOrder, ShowConsumptionOrder.Sequential),
        Availability = new Availabilities(show.Availability),
        TrailerUri = show.HasTrailerUri && show.TrailerUri.Length > 0 ? SpotifyUri.FromUri(show.TrailerUri) : null,
        HasMusicAndTalk = show.MusicAndTalk,
        IsAudiobook = show.IsAudiobook,
    };
}
