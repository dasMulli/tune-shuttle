// Port of metadata/src/playlist/list.rs.
// Namespace Librespot.Metadata (not .Playlist): see Playlist/Permission.cs.
using Google.Protobuf;
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using Geoblock = Spotify.Playlist4.Proto.GeoblockBlockingType;
using ProtoSelectedListContent = Spotify.Playlist4.Proto.SelectedListContent;

namespace Librespot.Metadata;

/// <summary>Upstream <c>Geoblocks(Vec&lt;Geoblock&gt;)</c>.</summary>
public sealed class Geoblocks : List<Geoblock>
{
    /// <summary>Creates an empty list.</summary>
    public Geoblocks()
    {
    }

    /// <summary>
    /// Upstream <c>impl_from_repeated_copy!(Geoblock, Geoblocks)</c>, fed with <c>enum_value_or_default</c> values:
    /// unknown numbers become <c>GEOBLOCK_BLOCKING_TYPE_UNSPECIFIED</c>.
    /// </summary>
    public Geoblocks(IEnumerable<Geoblock> items) : base(items.Select(geoblock => Util.EnumOrDefault(geoblock)))
    {
    }
}

/// <summary>A playlist with its contents (upstream <c>Playlist</c>).</summary>
public sealed record Playlist : IMetadata<Playlist, ProtoSelectedListContent>
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Playlist>();

    /// <summary>The playlist URI, decorated with the owner's username.</summary>
    public required SpotifyUri Id { get; init; }

    /// <summary>Revision bytes.</summary>
    public required byte[] Revision { get; init; }

    /// <summary>Number of items the playlist should contain.</summary>
    public required int Length { get; init; }

    /// <summary>Playlist attributes.</summary>
    public required PlaylistAttributes Attributes { get; init; }

    /// <summary>The contents.</summary>
    public required PlaylistItemList Contents { get; init; }

    /// <summary>Diff, if any.</summary>
    public PlaylistDiff? Diff { get; init; }

    /// <summary>Sync result, if any.</summary>
    public PlaylistDiff? SyncResult { get; init; }

    /// <summary>Resulting revisions.</summary>
    public required Playlists ResultingRevisions { get; init; }

    /// <summary>Whether the playlist has multiple heads.</summary>
    public required bool HasMultipleHeads { get; init; }

    /// <summary>Whether the playlist is up to date.</summary>
    public required bool IsUpToDate { get; init; }

    /// <summary>Nonces.</summary>
    public required IReadOnlyList<long> Nonces { get; init; }

    /// <summary>Timestamp.</summary>
    public required Date Timestamp { get; init; }

    /// <summary>Whether abuse reporting is enabled.</summary>
    public required bool HasAbuseReporting { get; init; }

    /// <summary>The user's capabilities on the playlist.</summary>
    public required Capabilities Capabilities { get; init; }

    /// <summary>Geoblocking.</summary>
    public required Geoblocks Geoblocks { get; init; }

    /// <summary>
    /// The URIs of the playlist's items (upstream <c>tracks()</c>). Logs a warning when the count differs from
    /// <see cref="Length"/>.
    /// </summary>
    public IReadOnlyList<SpotifyUri> Tracks()
    {
        var tracks = Contents.Items.Select(item => item.Id).ToList();

        var length = tracks.Count;
        var expectedLength = Length;
        if (length != expectedLength)
        {
            Log.LogWarning("Got {Length} tracks, but the list should contain {ExpectedLength} tracks.", length, expectedLength);
        }

        return tracks;
    }

    /// <summary>The playlist name (upstream <c>name()</c>).</summary>
    public string Name => Attributes.Name;

    /// <summary>Requests the playlist via spclient (upstream <c>Metadata::request</c>).</summary>
    /// <exception cref="LibrespotException"><paramref name="playlistUri"/> is not a playlist URI (<see cref="ErrorKind.InvalidArgument"/>).</exception>
    public static Task<byte[]> RequestAsync(Session session, SpotifyUri playlistUri, CancellationToken cancellationToken = default)
    {
        if (playlistUri is not SpotifyUri.Playlist { Id: var playlistId })
        {
            return Task.FromException<byte[]>(LibrespotException.InvalidArgument("playlist_uri"));
        }

        return session.SpClient.GetPlaylistAsync(playlistId, cancellationToken);
    }

    /// <summary>Requests and parses the playlist (upstream <c>Metadata::get</c>).</summary>
    public static Task<Playlist> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default) =>
        MetadataRequest.GetAsync<Playlist, ProtoSelectedListContent>(session, id, cancellationToken);

    /// <summary>
    /// Upstream <c>Metadata::parse</c>: the playlist proto doesn't contain the id, so it is decorated with
    /// <paramref name="uri"/>'s id and the owner's username.
    /// </summary>
    /// <exception cref="LibrespotException">
    /// <paramref name="uri"/> is not a playlist URI (<see cref="ErrorKind.InvalidArgument"/>), or the content failed to convert.
    /// </exception>
    public static Playlist Parse(ProtoSelectedListContent message, SpotifyUri uri)
    {
        if (uri is not SpotifyUri.Playlist { Id: var playlistId })
        {
            throw LibrespotException.InvalidArgument("playlist_uri");
        }

        // the playlist proto doesn't contain the id so we decorate it
        var playlist = SelectedListContent.From(message);

        var newUri = new SpotifyUri.Playlist(playlist.OwnerUsername, playlistId);

        return new Playlist
        {
            Id = newUri,
            Revision = playlist.Revision,
            Length = playlist.Length,
            Attributes = playlist.Attributes,
            Contents = playlist.Contents,
            Diff = playlist.Diff,
            SyncResult = playlist.SyncResult,
            ResultingRevisions = playlist.ResultingRevisions,
            HasMultipleHeads = playlist.HasMultipleHeads,
            IsUpToDate = playlist.IsUpToDate,
            Nonces = playlist.Nonces,
            Timestamp = playlist.Timestamp,
            HasAbuseReporting = playlist.HasAbuseReporting,
            Capabilities = playlist.Capabilities,
            Geoblocks = playlist.Geoblocks,
        };
    }
}

/// <summary>Upstream <c>Playlists(Vec&lt;SpotifyId&gt;)</c>.</summary>
public sealed class Playlists : List<SpotifyId>
{
    /// <summary>Creates an empty list.</summary>
    public Playlists()
    {
    }

    /// <summary>Wraps the given ids.</summary>
    public Playlists(IEnumerable<SpotifyId> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(Vec&lt;u8&gt;, Playlists)</c>: each entry must be a 16-byte raw id.</summary>
    /// <exception cref="LibrespotException">An entry is not 16 bytes.</exception>
    public Playlists(IEnumerable<ByteString> messages) : base(messages.Select(raw => SpotifyId.FromRaw(raw.Span)))
    {
    }
}

/// <summary>The decoded <c>SelectedListContent</c> message (upstream <c>SelectedListContent</c>).</summary>
public sealed record SelectedListContent
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<SelectedListContent>();

    /// <summary>Revision bytes.</summary>
    public required byte[] Revision { get; init; }

    /// <summary>Number of items.</summary>
    public required int Length { get; init; }

    /// <summary>Playlist attributes.</summary>
    public required PlaylistAttributes Attributes { get; init; }

    /// <summary>The contents.</summary>
    public required PlaylistItemList Contents { get; init; }

    /// <summary>Diff, if any.</summary>
    public PlaylistDiff? Diff { get; init; }

    /// <summary>Sync result, if any.</summary>
    public PlaylistDiff? SyncResult { get; init; }

    /// <summary>Resulting revisions.</summary>
    public required Playlists ResultingRevisions { get; init; }

    /// <summary>Whether the playlist has multiple heads.</summary>
    public required bool HasMultipleHeads { get; init; }

    /// <summary>Whether the playlist is up to date.</summary>
    public required bool IsUpToDate { get; init; }

    /// <summary>Nonces.</summary>
    public required IReadOnlyList<long> Nonces { get; init; }

    /// <summary>Timestamp.</summary>
    public required Date Timestamp { get; init; }

    /// <summary>Owner's username.</summary>
    public required string OwnerUsername { get; init; }

    /// <summary>Whether abuse reporting is enabled.</summary>
    public required bool HasAbuseReporting { get; init; }

    /// <summary>The user's capabilities on the playlist.</summary>
    public required Capabilities Capabilities { get; init; }

    /// <summary>Geoblocking.</summary>
    public required Geoblocks Geoblocks { get; init; }

    /// <summary>Upstream <c>TryFrom&lt;&amp;protocol::playlist4_external::SelectedListContent&gt;</c>.</summary>
    /// <exception cref="LibrespotException">A revision, timestamp or item failed to convert.</exception>
    public static SelectedListContent From(ProtoSelectedListContent playlist)
    {
        var timestamp = playlist.Timestamp;
        if (timestamp > 9295169800000)
        {
            // timestamp is way out of range for milliseconds. Some seem to be in microseconds?
            // Observed on playlists where:
            //   format: "artist-mix-reader"
            //   format_attributes {
            //     key: "mediaListConfig"
            //     value: "spotify:medialistconfig:artist-seed-mix:default_v18"
            //   }
            Log.LogWarning("timestamp is very large; assuming it's in microseconds");
            timestamp /= 1000;
        }

        var date = Date.FromTimestampMs(timestamp);

        return new SelectedListContent
        {
            Revision = playlist.Revision.ToByteArray(),
            Length = playlist.Length,
            Attributes = PlaylistAttributes.From(Util.OrDefault(playlist.Attributes)),
            Contents = PlaylistItemList.From(Util.OrDefault(playlist.Contents)),
            Diff = playlist.Diff is { } diff ? PlaylistDiff.From(diff) : null,
            SyncResult = playlist.SyncResult is { } syncResult ? PlaylistDiff.From(syncResult) : null,
            ResultingRevisions = new Playlists(playlist.ResultingRevisions),
            HasMultipleHeads = playlist.MultipleHeads,
            IsUpToDate = playlist.UpToDate,
            Nonces = playlist.Nonces.ToList(),
            Timestamp = date,
            OwnerUsername = playlist.OwnerUsername,
            HasAbuseReporting = playlist.AbuseReportingEnabled,
            Capabilities = Capabilities.From(Util.OrDefault(playlist.Capabilities)),
            Geoblocks = new Geoblocks(playlist.Geoblock),
        };
    }
}
