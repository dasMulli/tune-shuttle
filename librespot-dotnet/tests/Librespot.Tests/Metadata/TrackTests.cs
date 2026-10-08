// Parse tests for metadata/src/track.rs, album.rs, artist.rs, audio/file.rs (no upstream #[cfg(test)] modules).
using Google.Protobuf;
using Librespot.Core;
using Librespot.Metadata;
using Librespot.Metadata.Audio;
using Xunit;
using static Librespot.Tests.Metadata.MetadataFixtures;
using ProtoActivityPeriod = Spotify.Metadata.ActivityPeriod;
using ProtoAlbumGroup = Spotify.Metadata.AlbumGroup;
using ProtoArtist = Spotify.Metadata.Artist;
using ProtoAudioFile = Spotify.Metadata.AudioFile;
using ProtoFormat = Spotify.Metadata.AudioFile.Types.Format;
using ProtoTopTracks = Spotify.Metadata.TopTracks;
using ProtoTrack = Spotify.Metadata.Track;

namespace Librespot.Tests.Metadata;

public class TrackTests
{
    private static readonly SpotifyUri TrackUri = SpotifyUri.FromUri($"spotify:track:{TrackBase62}");

    [Fact]
    public void ParseTrack()
    {
        var track = Track.Parse(TrackMessage(), TrackUri);

        Assert.Equal(TrackUri, track.Id);
        Assert.Equal("Track", track.Name);
        Assert.Equal(3, track.Number);
        Assert.Equal(1, track.DiscNumber);
        Assert.Equal(215_000, track.Duration);
        Assert.Equal(77, track.Popularity);
        Assert.True(track.IsExplicit);
        Assert.True(track.HasLyrics);
        Assert.Equal(1_000, track.EarliestLiveTimestamp.AsTimestampMs());
        Assert.Equal(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), track.Licensor);
        Assert.Equal(["en"], track.LanguageOfPerformance);
        Assert.Equal("Original", track.OriginalTitle);
        Assert.Equal("Remastered", track.VersionTitle);

        var artist = Assert.Single(track.Artists);
        Assert.Equal(new SpotifyUri.Artist(SpotifyId.FromBase62(ArtistBase62)), artist.Id);

        var withRole = Assert.Single(track.ArtistsWithRole);
        Assert.Equal("Artist", withRole.Name);
        Assert.Equal(Spotify.Metadata.ArtistWithRole.Types.ArtistRole.MainArtist, withRole.Role);

        Assert.Equal([new SpotifyUri.Track(SpotifyId.FromBase62(AlternativeBase62))], track.Alternatives);

        var restriction = Assert.Single(track.Restrictions);
        Assert.Equal(["premium"], restriction.CatalogueStrs);
        Assert.Null(restriction.CountriesAllowed);
        Assert.Equal(["XX", "YY"], restriction.CountriesForbidden!);

        var availability = Assert.Single(track.Availability);
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), availability.Start.Value);
    }

    [Fact]
    public void ParseTrackFiles()
    {
        var track = Track.Parse(TrackMessage(), TrackUri);

        // 10 (AAC_160) is not a protobuf Format and the last file has no format: both are ignored, as upstream.
        Assert.Equal(2, track.Files.Count);
        Assert.Equal(Convert.ToHexStringLower(Bytes(20, 10).ToByteArray()), track.Files[AudioFileFormat.OggVorbis320].ToBase16());

        // A 16-byte file id is zero-padded to 20 bytes (librespot#1188).
        var padded = new byte[FileId.RawLength];
        Bytes(16, 40).ToByteArray().CopyTo(padded, 0);
        Assert.Equal(Convert.ToHexStringLower(padded), track.Files[AudioFileFormat.Mp3160].ToBase16());
    }

    [Fact]
    public void ParseTrackAlbum()
    {
        var album = Track.Parse(TrackMessage(), TrackUri).Album;

        Assert.Equal(new SpotifyUri.Album(SpotifyId.FromBase62(AlbumBase62)), album.Id);
        Assert.Equal("Album", album.Name);
        Assert.Equal(Spotify.Metadata.Album.Types.Type.Single, album.AlbumType);
        Assert.Equal("Label", album.Label);
        Assert.Equal(new DateTimeOffset(2020, 5, 17, 0, 0, 0, TimeSpan.Zero), album.Date.Value);
        Assert.Equal("Album Artist", Assert.Single(album.Artists).Name);
        Assert.Equal(3, album.Covers.Count);
        Assert.Equal(3, album.CoverGroup.Count);
        Assert.Equal(["great"], album.Reviews);
        Assert.Equal(
            [new SpotifyUri.Track(SpotifyId.FromBase62(TrackBase62)), new SpotifyUri.Track(SpotifyId.FromBase62(AlternativeBase62))],
            album.Tracks());
    }

    [Fact]
    public void ParseTrackWithInvalidGidFails()
    {
        var message = TrackMessage();
        message.Gid = ByteString.CopyFrom(new byte[15]);
        Assert.Throws<SpotifyIdException>(() => Track.Parse(message, TrackUri));
    }

    [Fact]
    public void ParseTrackWithoutAlbumFails()
    {
        // Upstream uses album.get_or_default(): a default Album has an empty gid, which is not a valid Spotify id.
        var message = TrackMessage();
        message.Album = null;
        Assert.Throws<SpotifyIdException>(() => Track.Parse(message, TrackUri));
    }

    [Fact]
    public void ParseTrackFromBytesRoundTrip()
    {
        var bytes = TrackMessage().ToByteArray();
        var message = ProtoTrack.Parser.ParseFrom(bytes);
        Assert.Equal("Track", Track.Parse(message, TrackUri).Name);
    }

    [Fact]
    public async Task RequestRejectsWrongUriKind()
    {
        var albumUri = new SpotifyUri.Album(SpotifyId.FromBase62(AlbumBase62));
        var e = await Assert.ThrowsAsync<LibrespotException>(() => Track.RequestAsync(null!, albumUri));
        Assert.Equal(ErrorKind.InvalidArgument, e.Kind);
        Assert.Equal("track_uri", e.Detail);

        e = await Assert.ThrowsAsync<LibrespotException>(() => Album.RequestAsync(null!, TrackUri));
        Assert.Equal("album_uri", e.Detail);
        e = await Assert.ThrowsAsync<LibrespotException>(() => Artist.RequestAsync(null!, TrackUri));
        Assert.Equal("artist_uri", e.Detail);
        e = await Assert.ThrowsAsync<LibrespotException>(() => Episode.RequestAsync(null!, TrackUri));
        Assert.Equal("episode_uri", e.Detail);
        e = await Assert.ThrowsAsync<LibrespotException>(() => Show.RequestAsync(null!, TrackUri));
        Assert.Equal("show_uri", e.Detail);
        e = await Assert.ThrowsAsync<LibrespotException>(() => Playlist.RequestAsync(null!, TrackUri));
        Assert.Equal("playlist_uri", e.Detail);
    }

    [Fact]
    public void AudioFileFormatFromInt()
    {
        Assert.Equal(AudioFileFormat.OggVorbis96, AudioFileFormatExtensions.FromInt(0));
        Assert.Equal(AudioFileFormat.Aac160, AudioFileFormatExtensions.FromInt(10));
        Assert.Equal(AudioFileFormat.Other5, AudioFileFormatExtensions.FromInt(13));
        Assert.Equal(AudioFileFormat.FlacFlac24Bit, AudioFileFormatExtensions.FromInt(22));
        Assert.Null(AudioFileFormatExtensions.FromInt(14));
        Assert.Null(AudioFileFormatExtensions.FromInt(17));
        Assert.Null(AudioFileFormatExtensions.FromInt(-1));
    }

    [Fact]
    public void AudioFilesMimeTypes()
    {
        Assert.Equal("audio/ogg", AudioFiles.MimeType(AudioFileFormat.OggVorbis160));
        Assert.Equal("audio/mpeg", AudioFiles.MimeType(AudioFileFormat.Mp3160Enc));
        Assert.Equal("audio/flac", AudioFiles.MimeType(AudioFileFormat.FlacFlac));
        // Upstream is_flac only matches FLAC_FLAC.
        Assert.Null(AudioFiles.MimeType(AudioFileFormat.FlacFlac24Bit));
        Assert.Null(AudioFiles.MimeType(AudioFileFormat.Aac24));
    }

    [Fact]
    public void AudioFilesLaterDuplicateWins()
    {
        var files = new AudioFiles(
        [
            new ProtoAudioFile { FileId = Bytes(20, 1), Format = ProtoFormat.OggVorbis160 },
            new ProtoAudioFile { FileId = Bytes(20, 2), Format = ProtoFormat.OggVorbis160 },
        ]);
        Assert.Equal(Convert.ToHexStringLower(Bytes(20, 2).ToByteArray()), Assert.Single(files).Value.ToBase16());
    }

    [Fact]
    public void ArtistTopTracksAndAlbumGroups()
    {
        var message = new ProtoArtist
        {
            Gid = Gid(ArtistBase62),
            Name = "Artist",
            TopTrack =
            {
                new ProtoTopTracks { Country = "", Track = { new ProtoTrack { Gid = Gid(AlternativeBase62) } } },
                new ProtoTopTracks { Country = "DE", Track = { new ProtoTrack { Gid = Gid(TrackBase62) } } },
            },
            AlbumGroup =
            {
                new ProtoAlbumGroup { Album = { new Spotify.Metadata.Album { Gid = Gid(AlbumBase62) }, new Spotify.Metadata.Album { Gid = Gid(TrackBase62) } } },
                new ProtoAlbumGroup(),
                new ProtoAlbumGroup { Album = { new Spotify.Metadata.Album { Gid = Gid(AlternativeBase62) } } },
            },
            ActivityPeriod =
            {
                new ProtoActivityPeriod { Decade = 1990 },
                new ProtoActivityPeriod { StartYear = 2001 },
                new ProtoActivityPeriod { StartYear = 2001, EndYear = 2010 },
            },
        };

        var artist = Artist.Parse(message, new SpotifyUri.Artist(SpotifyId.FromBase62(ArtistBase62)));

        Assert.Equal([new SpotifyUri.Track(SpotifyId.FromBase62(TrackBase62))], artist.TopTracks.ForCountry("DE"));
        Assert.Equal([new SpotifyUri.Track(SpotifyId.FromBase62(AlternativeBase62))], artist.TopTracks.ForCountry("FR"));
        Assert.Empty(new CountryTopTracks().ForCountry("DE"));

        Assert.Equal(
            [new SpotifyUri.Album(SpotifyId.FromBase62(AlbumBase62)), new SpotifyUri.Album(SpotifyId.FromBase62(AlternativeBase62))],
            artist.AlbumsCurrent());

        Assert.Equal(
            [new ActivityPeriod.Decade(1990), new ActivityPeriod.Timespan(2001, null), new ActivityPeriod.Timespan(2001, 2010)],
            artist.ActivityPeriods);
    }

    [Fact]
    public void ActivityPeriodRejectsInvalidCombinations()
    {
        var e = Assert.Throws<LibrespotException>(() => ActivityPeriod.From(new ProtoActivityPeriod()));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
        Assert.Equal("ActivityPeriod is expected to be either a decade or timespan", e.Detail);

        Assert.Throws<LibrespotException>(() => ActivityPeriod.From(new ProtoActivityPeriod { Decade = 1990, StartYear = 1991 }));

        e = Assert.Throws<LibrespotException>(() => ActivityPeriod.From(new ProtoActivityPeriod { Decade = -10 }));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
    }
}
