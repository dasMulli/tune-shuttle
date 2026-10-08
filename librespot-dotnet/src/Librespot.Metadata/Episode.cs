// Port of metadata/src/episode.rs.
using Librespot.Core;
using Librespot.Metadata.Audio;
using EpisodeType = Spotify.Metadata.Episode.Types.EpisodeType;
using ProtoEpisode = Spotify.Metadata.Episode;

namespace Librespot.Metadata;

/// <summary>Podcast episode / audiobook chapter metadata (upstream <c>Episode</c>).</summary>
/// <remarks>Upstream re-exports <c>EpisodeType</c>; it is the generated <c>Spotify.Metadata.Episode.Types.EpisodeType</c>.</remarks>
public sealed record Episode : IMetadata<Episode, ProtoEpisode>
{
    /// <summary>The episode URI.</summary>
    public required SpotifyUri Id { get; init; }

    /// <summary>Episode title.</summary>
    public required string Name { get; init; }

    /// <summary>Duration in milliseconds.</summary>
    public required int Duration { get; init; }

    /// <summary>The audio files by format.</summary>
    public required AudioFiles Audio { get; init; }

    /// <summary>Description.</summary>
    public required string Description { get; init; }

    /// <summary>Episode number.</summary>
    public required int Number { get; init; }

    /// <summary>Publication time.</summary>
    public required Date PublishTime { get; init; }

    /// <summary>Cover images.</summary>
    public required Images Covers { get; init; }

    /// <summary>Language.</summary>
    public required string Language { get; init; }

    /// <summary>Whether the episode is marked explicit.</summary>
    public required bool IsExplicit { get; init; }

    /// <summary>Name of the show the episode belongs to.</summary>
    public required string ShowName { get; init; }

    /// <summary>Video files.</summary>
    public required VideoFiles Videos { get; init; }

    /// <summary>Video preview files.</summary>
    public required VideoFiles VideoPreviews { get; init; }

    /// <summary>Audio preview files by format.</summary>
    public required AudioFiles AudioPreviews { get; init; }

    /// <summary>Catalogue/country restrictions.</summary>
    public required Restrictions Restrictions { get; init; }

    /// <summary>Freeze frame images.</summary>
    public required Images FreezeFrames { get; init; }

    /// <summary>Keywords.</summary>
    public required IReadOnlyList<string> Keywords { get; init; }

    /// <summary>Whether background playback is allowed.</summary>
    public required bool AllowBackgroundPlayback { get; init; }

    /// <summary>Availability windows.</summary>
    public required Availabilities Availability { get; init; }

    /// <summary>External URL.</summary>
    public required string ExternalUrl { get; init; }

    /// <summary>Episode type (full, trailer, bonus).</summary>
    public required EpisodeType EpisodeType { get; init; }

    /// <summary>Whether the episode mixes music and talk.</summary>
    public required bool HasMusicAndTalk { get; init; }

    /// <summary>Content ratings.</summary>
    public required ContentRatings ContentRating { get; init; }

    /// <summary>Whether this is an audiobook chapter.</summary>
    public required bool IsAudiobookChapter { get; init; }

    /// <summary>Requests the episode metadata via spclient (upstream <c>Metadata::request</c>).</summary>
    /// <exception cref="LibrespotException"><paramref name="episodeUri"/> is not an episode URI (<see cref="ErrorKind.InvalidArgument"/>).</exception>
    public static Task<byte[]> RequestAsync(Session session, SpotifyUri episodeUri, CancellationToken cancellationToken = default)
    {
        if (episodeUri is not SpotifyUri.Episode)
        {
            return Task.FromException<byte[]>(LibrespotException.InvalidArgument("episode_uri"));
        }

        return session.SpClient.GetEpisodeMetadataAsync(episodeUri, cancellationToken);
    }

    /// <summary>Requests and parses the episode metadata (upstream <c>Metadata::get</c>).</summary>
    public static Task<Episode> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default) =>
        MetadataRequest.GetAsync<Episode, ProtoEpisode>(session, id, cancellationToken);

    /// <summary>Upstream <c>Metadata::parse</c>.</summary>
    public static Episode Parse(ProtoEpisode message, SpotifyUri id) => From(message);

    /// <summary>Upstream <c>TryFrom&lt;&amp;protocol::metadata::Episode&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An id, date or nested item failed to convert.</exception>
    public static Episode From(ProtoEpisode episode) => new()
    {
        Id = SpotifyUri.From(episode),
        Name = episode.Name,
        Duration = episode.Duration,
        Audio = new AudioFiles(episode.Audio),
        Description = episode.Description,
        Number = episode.Number,
        PublishTime = Date.From(Util.OrDefault(episode.PublishTime)),
        Covers = new Images(Util.OrDefault(episode.CoverImage).Image),
        Language = episode.Language,
        IsExplicit = episode.Explicit,
        ShowName = Util.OrDefault(episode.Show).Name,
        Videos = new VideoFiles(episode.Video),
        VideoPreviews = new VideoFiles(episode.VideoPreview),
        AudioPreviews = new AudioFiles(episode.AudioPreview),
        Restrictions = new Restrictions(episode.Restriction),
        FreezeFrames = new Images(Util.OrDefault(episode.FreezeFrame).Image),
        Keywords = episode.Keyword.ToList(),
        AllowBackgroundPlayback = episode.AllowBackgroundPlayback,
        Availability = new Availabilities(episode.Availability),
        ExternalUrl = episode.ExternalUrl,
        EpisodeType = Util.EnumOrDefault(episode.Type, EpisodeType.Full),
        HasMusicAndTalk = episode.MusicAndTalk,
        ContentRating = new ContentRatings(episode.ContentRating),
        IsAudiobookChapter = episode.IsAudiobookChapter,
    };
}

/// <summary>Upstream <c>Episodes(Vec&lt;SpotifyUri&gt;)</c>.</summary>
public sealed class Episodes : List<SpotifyUri>
{
    /// <summary>Creates an empty list.</summary>
    public Episodes()
    {
    }

    /// <summary>Wraps the given URIs.</summary>
    public Episodes(IEnumerable<SpotifyUri> items) : base(items)
    {
    }

    /// <summary>
    /// Upstream <c>impl_try_from_repeated!(protocol::metadata::Episode, Episodes)</c>: only the URI of each episode is kept.
    /// </summary>
    /// <exception cref="LibrespotException">An episode gid is not a valid Spotify id.</exception>
    public Episodes(IEnumerable<ProtoEpisode> messages) : base(messages.Select(SpotifyUri.From))
    {
    }
}
