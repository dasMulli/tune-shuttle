// Port of metadata/src/playlist/item.rs.
// Namespace Librespot.Metadata (not .Playlist): see Playlist/Permission.cs.
using Librespot.Core;
using ProtoItem = Spotify.Playlist4.Proto.Item;
using ProtoListItems = Spotify.Playlist4.Proto.ListItems;
using ProtoMetaItem = Spotify.Playlist4.Proto.MetaItem;

namespace Librespot.Metadata;

/// <summary>One entry of a playlist (upstream <c>PlaylistItem</c>).</summary>
/// <param name="Id">The item's URI.</param>
/// <param name="Attributes">The item's attributes.</param>
public sealed record PlaylistItem(SpotifyUri Id, PlaylistItemAttributes Attributes)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistItemMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">The URI or a timestamp could not be converted.</exception>
    public static PlaylistItem From(ProtoItem item) =>
        new(SpotifyUri.From(item), PlaylistItemAttributes.From(Util.OrDefault(item.Attributes)));
}

/// <summary>Upstream <c>PlaylistItems(Vec&lt;PlaylistItem&gt;)</c>.</summary>
public sealed class PlaylistItems : List<PlaylistItem>
{
    /// <summary>Creates an empty list.</summary>
    public PlaylistItems()
    {
    }

    /// <summary>Wraps the given items.</summary>
    public PlaylistItems(IEnumerable<PlaylistItem> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(PlaylistItemMessage, PlaylistItems)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public PlaylistItems(IEnumerable<ProtoItem> messages) : base(messages.Select(PlaylistItem.From))
    {
    }
}

/// <summary>A page of playlist contents (upstream <c>PlaylistItemList</c>).</summary>
/// <param name="Position">Position of the first item.</param>
/// <param name="IsTruncated">Whether more items exist.</param>
/// <param name="Items">The items.</param>
/// <param name="MetaItems">Meta items (for root lists).</param>
public sealed record PlaylistItemList(int Position, bool IsTruncated, PlaylistItems Items, PlaylistMetaItems MetaItems)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistItemsMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An item failed to convert.</exception>
    public static PlaylistItemList From(ProtoListItems listItems) =>
        new(listItems.Pos, listItems.Truncated, new PlaylistItems(listItems.Items), new PlaylistMetaItems(listItems.MetaItems));
}

/// <summary>Metadata of a playlist inside a root list (upstream <c>PlaylistMetaItem</c>).</summary>
public sealed record PlaylistMetaItem
{
    /// <summary>The revision of the item's metadata (not the id of the item or playlist).</summary>
    public required SpotifyUri Revision { get; init; }

    /// <summary>The playlist's attributes.</summary>
    public required PlaylistAttributes Attributes { get; init; }

    /// <summary>Number of items.</summary>
    public required int Length { get; init; }

    /// <summary>Timestamp.</summary>
    public required Date Timestamp { get; init; }

    /// <summary>Owner's username.</summary>
    public required string OwnerUsername { get; init; }

    /// <summary>Whether abuse reporting is enabled.</summary>
    public required bool HasAbuseReporting { get; init; }

    /// <summary>The user's capabilities on the playlist.</summary>
    public required Capabilities Capabilities { get; init; }

    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistMetaItemMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">The revision or timestamp could not be converted.</exception>
    public static PlaylistMetaItem From(ProtoMetaItem item) => new()
    {
        Revision = SpotifyUri.From(item),
        Attributes = PlaylistAttributes.From(Util.OrDefault(item.Attributes)),
        Length = item.Length,
        Timestamp = Date.FromTimestampMs(item.Timestamp),
        OwnerUsername = item.OwnerUsername,
        HasAbuseReporting = item.AbuseReportingEnabled,
        Capabilities = Capabilities.From(Util.OrDefault(item.Capabilities)),
    };
}

/// <summary>Upstream <c>PlaylistMetaItems(Vec&lt;PlaylistMetaItem&gt;)</c>.</summary>
public sealed class PlaylistMetaItems : List<PlaylistMetaItem>
{
    /// <summary>Creates an empty list.</summary>
    public PlaylistMetaItems()
    {
    }

    /// <summary>Wraps the given items.</summary>
    public PlaylistMetaItems(IEnumerable<PlaylistMetaItem> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(PlaylistMetaItemMessage, PlaylistMetaItems)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public PlaylistMetaItems(IEnumerable<ProtoMetaItem> messages) : base(messages.Select(PlaylistMetaItem.From))
    {
    }
}
