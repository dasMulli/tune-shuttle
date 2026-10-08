// Port of metadata/src/playlist/annotation.rs.
// Namespace Librespot.Metadata (not .Playlist): see Playlist/Permission.cs.
using Librespot.Core;
using AbuseReportState = SpotifyPlaylistAnnotate3.Proto.AbuseReportState;
using ProtoPlaylistAnnotation = SpotifyPlaylistAnnotate3.Proto.PlaylistAnnotation;

namespace Librespot.Metadata;

/// <summary>A playlist's annotation: description and pictures (upstream <c>PlaylistAnnotation</c>).</summary>
/// <remarks>Upstream re-exports <c>AbuseReportState</c>; it is the generated <c>SpotifyPlaylistAnnotate3.Proto.AbuseReportState</c>.</remarks>
public sealed record PlaylistAnnotation : IMetadata<PlaylistAnnotation, ProtoPlaylistAnnotation>
{
    /// <summary>Description.</summary>
    public required string Description { get; init; }

    /// <summary>Picture (upstream open question: is this a URL or Spotify URI?).</summary>
    public required string Picture { get; init; }

    /// <summary>Transcoded variants of the picture.</summary>
    public required TranscodedPictures TranscodedPictures { get; init; }

    /// <summary>Whether abuse reporting is enabled.</summary>
    public required bool HasAbuseReporting { get; init; }

    /// <summary>Abuse report state.</summary>
    public required AbuseReportState AbuseReportState { get; init; }

    /// <summary>
    /// Requests the current user's annotation of the playlist over Mercury (upstream <c>Metadata::request</c>).
    /// </summary>
    /// <exception cref="LibrespotException"><paramref name="playlistUri"/> is not a playlist URI (<see cref="ErrorKind.InvalidArgument"/>).</exception>
    public static Task<byte[]> RequestAsync(Session session, SpotifyUri playlistUri, CancellationToken cancellationToken = default)
    {
        var currentUser = session.Username;

        if (playlistUri is not SpotifyUri.Playlist { Id: var playlistId })
        {
            return Task.FromException<byte[]>(LibrespotException.InvalidArgument("playlist_uri"));
        }

        return RequestForUserAsync(session, currentUser, playlistId, cancellationToken);
    }

    /// <summary>Requests and parses the current user's annotation (upstream <c>Metadata::get</c>).</summary>
    public static Task<PlaylistAnnotation> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default) =>
        MetadataRequest.GetAsync<PlaylistAnnotation, ProtoPlaylistAnnotation>(session, id, cancellationToken);

    /// <summary>Upstream <c>Metadata::parse</c>.</summary>
    /// <exception cref="LibrespotException">A transcoded picture URI could not be converted.</exception>
    public static PlaylistAnnotation Parse(ProtoPlaylistAnnotation message, SpotifyUri id) => From(message);

    /// <summary>Upstream <c>TryFrom&lt;&amp;protocol::playlist_annotate3::PlaylistAnnotation&gt;</c> (the body of <see cref="Parse"/>).</summary>
    /// <exception cref="LibrespotException">A transcoded picture URI could not be converted.</exception>
    public static PlaylistAnnotation From(ProtoPlaylistAnnotation annotation) => new()
    {
        Description = annotation.Description,
        Picture = annotation.Picture, // upstream open question: is this a URL or Spotify URI?
        TranscodedPictures = new TranscodedPictures(annotation.TranscodedPicture),
        HasAbuseReporting = annotation.IsAbuseReportingEnabled,
        AbuseReportState = Util.EnumOrDefault(annotation.AbuseReportState, AbuseReportState.Ok),
    };

    /// <summary>Upstream private <c>request_for_user</c>.</summary>
    private static Task<byte[]> RequestForUserAsync(Session session, string username, SpotifyId playlistId, CancellationToken cancellationToken)
    {
        var uri = $"hm://playlist-annotate/v1/annotation/user/{username}/playlist/{playlistId.ToBase62()}";
        return MercuryRequest.RequestAsync(session, uri, cancellationToken);
    }

    /// <summary>Upstream private (dead-code) <c>get_for_user</c>: another user's annotation of the playlist.</summary>
    /// <exception cref="LibrespotException"><paramref name="playlistUri"/> is not a playlist URI, or the request/parse failed.</exception>
    internal static async Task<PlaylistAnnotation> GetForUserAsync(Session session, string username, SpotifyUri playlistUri, CancellationToken cancellationToken = default)
    {
        if (playlistUri is not SpotifyUri.Playlist { Id: var playlistId })
        {
            throw LibrespotException.InvalidArgument("playlist_uri");
        }

        var response = await RequestForUserAsync(session, username, playlistId, cancellationToken).ConfigureAwait(false);
        var message = MetadataRequest.ParseMessage<ProtoPlaylistAnnotation>(response);
        return Parse(message, playlistUri);
    }
}
