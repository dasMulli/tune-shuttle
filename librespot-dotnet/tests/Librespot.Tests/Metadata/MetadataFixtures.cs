// Hand-built protobuf messages for the metadata parse tests (upstream metadata has no #[cfg(test)] modules).
using Google.Protobuf;
using Librespot.Core;
using ProtoAlbum = Spotify.Metadata.Album;
using ProtoArtist = Spotify.Metadata.Artist;
using ProtoArtistWithRole = Spotify.Metadata.ArtistWithRole;
using ProtoAudioFile = Spotify.Metadata.AudioFile;
using ProtoAvailability = Spotify.Metadata.Availability;
using ProtoDate = Spotify.Metadata.Date;
using ProtoDisc = Spotify.Metadata.Disc;
using ProtoFormat = Spotify.Metadata.AudioFile.Types.Format;
using ProtoImage = Spotify.Metadata.Image;
using ProtoImageGroup = Spotify.Metadata.ImageGroup;
using ProtoRestriction = Spotify.Metadata.Restriction;
using ProtoTrack = Spotify.Metadata.Track;

namespace Librespot.Tests.Metadata;

internal static class MetadataFixtures
{
    public const string TrackBase62 = "4GNcXTGWmnZ3ySrqvol3o4";
    public const string AlternativeBase62 = "5sWHDYs0csV6RS48xBl0tH";
    public const string AlbumBase62 = "6rqhFgbbKwnb9MLmUQDhG6";
    public const string ArtistBase62 = "0OdUWJ0sBjDrqHygGUXeCF";

    public static ByteString Gid(string base62) => ByteString.CopyFrom(SpotifyId.FromBase62(base62).ToRaw());

    public static ByteString Bytes(int length, byte start)
    {
        var raw = new byte[length];
        for (var i = 0; i < length; i++)
        {
            raw[i] = (byte)(start + i);
        }

        return ByteString.CopyFrom(raw);
    }

    public static ProtoDate DateMessage(int year, int month, int day) => new() { Year = year, Month = month, Day = day };

    public static ProtoArtist ArtistMessage(string name = "Artist") => new() { Gid = Gid(ArtistBase62), Name = name };

    public static ProtoAlbum AlbumMessage() => new()
    {
        Gid = Gid(AlbumBase62),
        Name = "Album",
        Artist = { ArtistMessage("Album Artist") },
        Type = ProtoAlbum.Types.Type.Single,
        Label = "Label",
        Date = DateMessage(2020, 5, 17),
        Popularity = 42,
        CoverGroup = new ProtoImageGroup
        {
            Image =
            {
                new ProtoImage { FileId = Bytes(20, 1), Size = ProtoImage.Types.Size.Small, Width = 64, Height = 64 },
                new ProtoImage { FileId = Bytes(20, 100), Size = ProtoImage.Types.Size.Large, Width = 640, Height = 640 },
                new ProtoImage { FileId = Bytes(20, 50), Size = ProtoImage.Types.Size.Default, Width = 300, Height = 300 },
            },
        },
        Disc =
        {
            new ProtoDisc { Number = 1, Name = "Disc 1", Track = { new ProtoTrack { Gid = Gid(TrackBase62) } } },
            new ProtoDisc { Number = 2, Name = "Disc 2", Track = { new ProtoTrack { Gid = Gid(AlternativeBase62) } } },
        },
        Review = { "great" },
    };

    /// <summary>A track with an alternative, a restriction for country "XX", 20- and 16-byte file ids and an unknown format.</summary>
    public static ProtoTrack TrackMessage() => new()
    {
        Gid = Gid(TrackBase62),
        Name = "Track",
        Album = AlbumMessage(),
        Artist = { ArtistMessage() },
        Number = 3,
        DiscNumber = 1,
        Duration = 215_000,
        Popularity = 77,
        Explicit = true,
        Restriction =
        {
            new ProtoRestriction { CatalogueStr = { "premium" }, CountriesForbidden = "XXYY" },
        },
        File =
        {
            new ProtoAudioFile { FileId = Bytes(20, 10), Format = ProtoFormat.OggVorbis320 },
            new ProtoAudioFile { FileId = Bytes(16, 40), Format = ProtoFormat.Mp3160 },
            new ProtoAudioFile { FileId = Bytes(20, 70), Format = (ProtoFormat)10 }, // AAC_160: not a protobuf Format
            new ProtoAudioFile { FileId = Bytes(20, 90) }, // unspecified format
        },
        Alternative = { new ProtoTrack { Gid = Gid(AlternativeBase62) } },
        EarliestLiveTimestamp = 1_000,
        HasLyrics = true,
        Availability = { new ProtoAvailability { CatalogueStr = { "premium" }, Start = DateMessage(2001, 1, 1) } },
        Licensor = new Spotify.Metadata.Licensor { Uuid = ByteString.CopyFrom(Convert.FromHexString("00112233445566778899aabbccddeeff")) },
        LanguageOfPerformance = { "en" },
        OriginalTitle = "Original",
        VersionTitle = "Remastered",
        ArtistWithRole =
        {
            new ProtoArtistWithRole
            {
                ArtistGid = Gid(ArtistBase62),
                ArtistName = "Artist",
                Role = ProtoArtistWithRole.Types.ArtistRole.MainArtist,
            },
        },
    };
}
