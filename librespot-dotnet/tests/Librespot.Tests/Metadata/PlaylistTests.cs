// Parse tests for metadata/src/playlist/*.rs, show.rs, episode.rs and lyrics.rs (no upstream #[cfg(test)] modules).
using System.Text;
using Librespot.Core;
using Librespot.Metadata;
using Xunit;
using static Librespot.Tests.Metadata.MetadataFixtures;
using ProtoDiff = Spotify.Playlist4.Proto.Diff;
using ProtoEpisode = Spotify.Metadata.Episode;
using ProtoItem = Spotify.Playlist4.Proto.Item;
using ProtoItemAttributes = Spotify.Playlist4.Proto.ItemAttributes;
using ProtoListAttributes = Spotify.Playlist4.Proto.ListAttributes;
using ProtoListItems = Spotify.Playlist4.Proto.ListItems;
using ProtoOp = Spotify.Playlist4.Proto.Op;
using ProtoSelectedListContent = Spotify.Playlist4.Proto.SelectedListContent;
using ProtoShow = Spotify.Metadata.Show;

namespace Librespot.Tests.Metadata;

public class PlaylistTests
{
    private const string PlaylistBase62 = "37i9dQZF1DXcBWIGoYBM5M";

    private static ProtoSelectedListContent Content(long timestamp) => new()
    {
        Revision = Bytes(24, 0),
        Length = 2,
        Attributes = new ProtoListAttributes
        {
            Name = "Today's Top Hits",
            Collaborative = true,
            FormatAttributes =
            {
                new Spotify.Playlist4.Proto.FormatListAttribute { Key = "k", Value = "v1" },
                new Spotify.Playlist4.Proto.FormatListAttribute { Key = "k", Value = "v2" },
            },
        },
        Contents = new ProtoListItems
        {
            Pos = 0,
            Truncated = false,
            Items =
            {
                new ProtoItem { Uri = $"spotify:track:{TrackBase62}", Attributes = new ProtoItemAttributes { AddedBy = "alice", Timestamp = 1_600_000_000_000 } },
                new ProtoItem { Uri = $"spotify:episode:{AlternativeBase62}" },
            },
        },
        Diff = new ProtoDiff
        {
            FromRevision = Bytes(16, 0),
            ToRevision = Bytes(16, 16),
            Ops = { new ProtoOp { Kind = ProtoOp.Types.Kind.Mov, Mov = new Spotify.Playlist4.Proto.Mov { FromIndex = 1, Length = 1, ToIndex = 0 } } },
        },
        ResultingRevisions = { Bytes(16, 32) },
        Nonces = { 1, 2 },
        Timestamp = timestamp,
        OwnerUsername = "spotify",
        Capabilities = new Spotify.PlaylistPermission.Proto.Capabilities
        {
            CanView = true,
            GrantableLevel = { Spotify.PlaylistPermission.Proto.PermissionLevel.Viewer },
        },
        Geoblock = { Spotify.Playlist4.Proto.GeoblockBlockingType.Title },
    };

    [Fact]
    public void ParsePlaylist()
    {
        var uri = SpotifyUri.FromUri($"spotify:playlist:{PlaylistBase62}");
        var playlist = Playlist.Parse(Content(1_700_000_000_000), uri);

        Assert.Equal(new SpotifyUri.Playlist("spotify", SpotifyId.FromBase62(PlaylistBase62)), playlist.Id);
        Assert.Equal($"spotify:user:spotify:playlist:{PlaylistBase62}", playlist.Id.ToUri());
        Assert.Equal("Today's Top Hits", playlist.Name);
        Assert.True(playlist.Attributes.IsCollaborative);
        Assert.Equal("v2", playlist.Attributes.FormatAttributes["k"]);
        Assert.Equal(1_700_000_000_000, playlist.Timestamp.AsTimestampMs());
        Assert.Equal(
            [new SpotifyUri.Track(SpotifyId.FromBase62(TrackBase62)), new SpotifyUri.Episode(SpotifyId.FromBase62(AlternativeBase62))],
            playlist.Tracks());
        Assert.Equal("alice", playlist.Contents.Items[0].Attributes.AddedBy);
        Assert.Equal(1_600_000_000_000, playlist.Contents.Items[0].Attributes.Timestamp.AsTimestampMs());
        Assert.Equal([1L, 2L], playlist.Nonces);
        Assert.Equal([SpotifyId.FromRaw(Bytes(16, 32).Span)], playlist.ResultingRevisions);
        Assert.True(playlist.Capabilities.CanView);
        Assert.Equal([Spotify.PlaylistPermission.Proto.PermissionLevel.Viewer], playlist.Capabilities.GrantableLevels);
        Assert.Equal([Spotify.Playlist4.Proto.GeoblockBlockingType.Title], playlist.Geoblocks);
        Assert.Null(playlist.SyncResult);

        var diff = Assert.IsType<PlaylistDiff>(playlist.Diff);
        Assert.Equal(SpotifyId.FromRaw(Bytes(16, 16).Span), diff.ToRevision);
        var op = Assert.Single(diff.Operations);
        Assert.Equal(ProtoOp.Types.Kind.Mov, op.Kind);
        Assert.Equal(new PlaylistOperationMove(1, 1, 0), op.Mov);
        Assert.Empty(op.Add.Items);
    }

    [Fact]
    public void MicrosecondTimestampIsScaled()
    {
        var uri = SpotifyUri.FromUri($"spotify:playlist:{PlaylistBase62}");
        var playlist = Playlist.Parse(Content(1_700_000_000_000_000), uri);
        Assert.Equal(1_700_000_000_000, playlist.Timestamp.AsTimestampMs());
    }

    [Fact]
    public void ParseRejectsNonPlaylistUri()
    {
        var e = Assert.Throws<LibrespotException>(() => Playlist.Parse(Content(0), SpotifyUri.FromUri($"spotify:track:{TrackBase62}")));
        Assert.Equal(ErrorKind.InvalidArgument, e.Kind);
        Assert.Equal("playlist_uri", e.Detail);
    }

    [Fact]
    public void ParseAnnotation()
    {
        var message = new SpotifyPlaylistAnnotate3.Proto.PlaylistAnnotation
        {
            Description = "desc",
            Picture = "ab67",
            TranscodedPicture = { new SpotifyPlaylistAnnotate3.Proto.TranscodedPicture { TargetName = "default", Uri = "https://example/x" } },
        };

        var annotation = PlaylistAnnotation.Parse(message, SpotifyUri.FromUri($"spotify:playlist:{PlaylistBase62}"));

        Assert.Equal("desc", annotation.Description);
        Assert.True(annotation.HasAbuseReporting); // proto default = true
        Assert.Equal(SpotifyPlaylistAnnotate3.Proto.AbuseReportState.Ok, annotation.AbuseReportState);
        var picture = Assert.Single(annotation.TranscodedPictures);
        Assert.Equal(new SpotifyUri.Unknown("TranscodedPicture", "https://example/x"), picture.Uri);
    }

    [Fact]
    public void ParseShowAndEpisode()
    {
        var show = Show.Parse(
            new ProtoShow
            {
                Gid = Gid(AlbumBase62),
                Name = "Show",
                TrailerUri = $"spotify:episode:{TrackBase62}",
                Episode = { new ProtoEpisode { Gid = Gid(TrackBase62) } },
                ConsumptionOrder = ProtoShow.Types.ConsumptionOrder.Episodic,
            },
            new SpotifyUri.Show(SpotifyId.FromBase62(AlbumBase62)));

        Assert.Equal(new SpotifyUri.Episode(SpotifyId.FromBase62(TrackBase62)), show.TrailerUri);
        Assert.Equal([new SpotifyUri.Episode(SpotifyId.FromBase62(TrackBase62))], show.Episodes);
        Assert.Equal(ProtoShow.Types.ConsumptionOrder.Episodic, show.ConsumptionOrder);
        Assert.Equal(ProtoShow.Types.MediaType.Mixed, show.MediaType);

        var noTrailer = Show.Parse(new ProtoShow { Gid = Gid(AlbumBase62), TrailerUri = "" }, new SpotifyUri.Show(SpotifyId.FromBase62(AlbumBase62)));
        Assert.Null(noTrailer.TrailerUri);

        var episode = Episode.Parse(
            new ProtoEpisode
            {
                Gid = Gid(TrackBase62),
                Name = "Episode",
                Duration = 1234,
                PublishTime = DateMessage(2021, 3, 4),
                Show = new ProtoShow { Name = "Show" },
                Language = "en",
            },
            new SpotifyUri.Episode(SpotifyId.FromBase62(TrackBase62)));

        Assert.Equal("Show", episode.ShowName);
        Assert.Equal(1234, episode.Duration);
        Assert.Equal(Spotify.Metadata.Episode.Types.EpisodeType.Full, episode.EpisodeType);
        Assert.Empty(episode.Covers);
    }

    private const string LyricsJson = """
        {
          "lyrics": {
            "syncType": "LINE_SYNCED",
            "lines": [
              { "startTimeMs": "960", "words": "Hello", "syllables": [], "endTimeMs": "0" }
            ],
            "provider": "MusixMatch",
            "providerLyricsId": "123",
            "providerDisplayName": "Musixmatch",
            "syncLyricsUri": "",
            "isDenseTypeface": false,
            "alternatives": [],
            "language": "en",
            "isRtlLanguage": false,
            "capStatus": "NONE"
          },
          "colors": { "background": -9206234, "text": -16777216, "highlightText": -1 },
          "hasVocalRemoval": false
        }
        """;

    [Fact]
    public void ParseLyrics()
    {
        var lyrics = Lyrics.From(Encoding.UTF8.GetBytes(LyricsJson));

        Assert.Equal(SyncType.LineSynced, lyrics.LyricsInner.SyncType);
        Assert.Equal(-9206234, lyrics.Colors.Background);
        Assert.Equal(-1, lyrics.Colors.HighlightText);
        Assert.Equal(new Line { StartTimeMs = "960", EndTimeMs = "0", Words = "Hello" }, Assert.Single(lyrics.LyricsInner.Lines));
        Assert.Equal("Musixmatch", lyrics.LyricsInner.ProviderDisplayName);
    }

    [Fact]
    public void ParseLyricsMissingFieldFails()
    {
        var json = LyricsJson.Replace("\"hasVocalRemoval\": false", "\"other\": 1", StringComparison.Ordinal);
        var e = Assert.Throws<LibrespotException>(() => Lyrics.From(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);

        var badEnum = LyricsJson.Replace("LINE_SYNCED", "WORD_SYNCED", StringComparison.Ordinal);
        Assert.Throws<LibrespotException>(() => Lyrics.From(Encoding.UTF8.GetBytes(badEnum)));

        var nullString = LyricsJson.Replace("\"language\": \"en\"", "\"language\": null", StringComparison.Ordinal);
        Assert.Throws<LibrespotException>(() => Lyrics.From(Encoding.UTF8.GetBytes(nullString)));
    }

    [Fact]
    public void MalformedProtobufIsFailedPrecondition()
    {
        var e = Assert.Throws<LibrespotException>(() => MetadataRequest.ParseMessage<Spotify.Metadata.Track>([0xff, 0xff, 0xff]));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
    }
}
