// Port of metadata/src/playlist/attribute.rs.
// Namespace Librespot.Metadata (not .Playlist): see Playlist/Permission.cs.
using Librespot.Core;
using PlaylistAttributeKind = Spotify.Playlist4.Proto.ListAttributeKind;
using PlaylistItemAttributeKind = Spotify.Playlist4.Proto.ItemAttributeKind;
using ProtoFormatListAttribute = Spotify.Playlist4.Proto.FormatListAttribute;
using ProtoItemAttributes = Spotify.Playlist4.Proto.ItemAttributes;
using ProtoItemAttributesPartialState = Spotify.Playlist4.Proto.ItemAttributesPartialState;
using ProtoListAttributes = Spotify.Playlist4.Proto.ListAttributes;
using ProtoListAttributesPartialState = Spotify.Playlist4.Proto.ListAttributesPartialState;
using ProtoUpdateItemAttributes = Spotify.Playlist4.Proto.UpdateItemAttributes;
using ProtoUpdateListAttributes = Spotify.Playlist4.Proto.UpdateListAttributes;

namespace Librespot.Metadata;

/// <summary>Playlist-level attributes (upstream <c>PlaylistAttributes</c>).</summary>
public sealed record PlaylistAttributes
{
    /// <summary>Playlist name.</summary>
    public required string Name { get; init; }

    /// <summary>Description.</summary>
    public required string Description { get; init; }

    /// <summary>Picture id bytes.</summary>
    public required byte[] Picture { get; init; }

    /// <summary>Whether the playlist is collaborative.</summary>
    public required bool IsCollaborative { get; init; }

    /// <summary>Playlist v3 version.</summary>
    public required string Pl3Version { get; init; }

    /// <summary>Whether the owner deleted the playlist.</summary>
    public required bool IsDeletedByOwner { get; init; }

    /// <summary>Client id.</summary>
    public required string ClientId { get; init; }

    /// <summary>Format (e.g. <c>artist-mix-reader</c>).</summary>
    public required string Format { get; init; }

    /// <summary>Format attributes.</summary>
    public required PlaylistFormatAttribute FormatAttributes { get; init; }

    /// <summary>Picture sizes.</summary>
    public required PictureSizes PictureSizes { get; init; }

    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistAttributesMessage&gt;</c>.</summary>
    public static PlaylistAttributes From(ProtoListAttributes attributes) => new()
    {
        Name = attributes.Name,
        Description = attributes.Description,
        Picture = attributes.Picture.ToByteArray(),
        IsCollaborative = attributes.Collaborative,
        Pl3Version = attributes.Pl3Version,
        IsDeletedByOwner = attributes.DeletedByOwner,
        ClientId = attributes.ClientId,
        Format = attributes.Format,
        FormatAttributes = new PlaylistFormatAttribute(attributes.FormatAttributes),
        PictureSizes = new PictureSizes(attributes.PictureSize),
    };
}

/// <summary>Upstream <c>PlaylistAttributeKinds(Vec&lt;PlaylistAttributeKind&gt;)</c>.</summary>
/// <remarks>Upstream re-exports <c>PlaylistAttributeKind</c>; it is the generated <c>Spotify.Playlist4.Proto.ListAttributeKind</c>.</remarks>
public sealed class PlaylistAttributeKinds : List<PlaylistAttributeKind>
{
    /// <summary>Creates an empty list.</summary>
    public PlaylistAttributeKinds()
    {
    }

    /// <summary>
    /// Upstream <c>impl_from_repeated_copy!(PlaylistAttributeKind, PlaylistAttributeKinds)</c>, fed with
    /// <c>enum_value_or_default</c> values: unknown numbers become <c>LIST_UNKNOWN</c>.
    /// </summary>
    public PlaylistAttributeKinds(IEnumerable<PlaylistAttributeKind> items) : base(items.Select(kind => Util.EnumOrDefault(kind)))
    {
    }
}

/// <summary>Upstream <c>PlaylistFormatAttribute(HashMap&lt;String, String&gt;)</c>.</summary>
public sealed class PlaylistFormatAttribute : Dictionary<string, string>
{
    /// <summary>Creates an empty map.</summary>
    public PlaylistFormatAttribute()
    {
    }

    /// <summary>Upstream <c>From&lt;&amp;[PlaylistFormatAttributeMessage]&gt;</c>: later duplicates of a key win.</summary>
    public PlaylistFormatAttribute(IEnumerable<ProtoFormatListAttribute> attributes)
    {
        foreach (var attribute in attributes)
        {
            this[attribute.Key] = attribute.Value;
        }
    }
}

/// <summary>Per-item attributes of a playlist entry (upstream <c>PlaylistItemAttributes</c>).</summary>
public sealed record PlaylistItemAttributes
{
    /// <summary>User that added the item.</summary>
    public required string AddedBy { get; init; }

    /// <summary>When the item was added.</summary>
    public required Date Timestamp { get; init; }

    /// <summary>When the item was seen.</summary>
    public required Date SeenAt { get; init; }

    /// <summary>Whether the item is public.</summary>
    public required bool IsPublic { get; init; }

    /// <summary>Format attributes.</summary>
    public required PlaylistFormatAttribute FormatAttributes { get; init; }

    /// <summary>Item id bytes.</summary>
    public required byte[] ItemId { get; init; }

    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistItemAttributesMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">A timestamp is out of range.</exception>
    public static PlaylistItemAttributes From(ProtoItemAttributes attributes) => new()
    {
        AddedBy = attributes.AddedBy,
        Timestamp = Date.FromTimestampMs(attributes.Timestamp),
        SeenAt = Date.FromTimestampMs(attributes.SeenAt),
        IsPublic = attributes.Public,
        FormatAttributes = new PlaylistFormatAttribute(attributes.FormatAttributes),
        ItemId = attributes.ItemId.ToByteArray(),
    };
}

/// <summary>Upstream <c>PlaylistItemAttributeKinds(Vec&lt;PlaylistItemAttributeKind&gt;)</c>.</summary>
/// <remarks>Upstream re-exports <c>PlaylistItemAttributeKind</c>; it is the generated <c>Spotify.Playlist4.Proto.ItemAttributeKind</c>.</remarks>
public sealed class PlaylistItemAttributeKinds : List<PlaylistItemAttributeKind>
{
    /// <summary>Creates an empty list.</summary>
    public PlaylistItemAttributeKinds()
    {
    }

    /// <summary>
    /// Upstream <c>impl_from_repeated_copy!(PlaylistItemAttributeKind, PlaylistItemAttributeKinds)</c>, fed with
    /// <c>enum_value_or_default</c> values: unknown numbers become <c>ITEM_UNKNOWN</c>.
    /// </summary>
    public PlaylistItemAttributeKinds(IEnumerable<PlaylistItemAttributeKind> items) : base(items.Select(kind => Util.EnumOrDefault(kind)))
    {
    }
}

/// <summary>A partial playlist attribute state (upstream <c>PlaylistPartialAttributes</c>; its fields are private upstream).</summary>
public sealed record PlaylistPartialAttributes
{
    private PlaylistPartialAttributes(PlaylistAttributes values, PlaylistAttributeKinds noValue)
    {
        Values = values;
        NoValue = noValue;
    }

    /// <summary>The attribute values.</summary>
    internal PlaylistAttributes Values { get; }

    /// <summary>The attributes that have no value.</summary>
    internal PlaylistAttributeKinds NoValue { get; }

    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistPartialAttributesMessage&gt;</c>.</summary>
    public static PlaylistPartialAttributes From(ProtoListAttributesPartialState attributes) =>
        new(PlaylistAttributes.From(Util.OrDefault(attributes.Values)), new PlaylistAttributeKinds(attributes.NoValue));
}

/// <summary>A partial playlist item attribute state (upstream <c>PlaylistPartialItemAttributes</c>; its fields are private upstream).</summary>
public sealed record PlaylistPartialItemAttributes
{
    private PlaylistPartialItemAttributes(PlaylistItemAttributes values, PlaylistItemAttributeKinds noValue)
    {
        Values = values;
        NoValue = noValue;
    }

    /// <summary>The attribute values.</summary>
    internal PlaylistItemAttributes Values { get; }

    /// <summary>The attributes that have no value.</summary>
    internal PlaylistItemAttributeKinds NoValue { get; }

    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistPartialItemAttributesMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">A timestamp is out of range.</exception>
    public static PlaylistPartialItemAttributes From(ProtoItemAttributesPartialState attributes) =>
        new(PlaylistItemAttributes.From(Util.OrDefault(attributes.Values)), new PlaylistItemAttributeKinds(attributes.NoValue));
}

/// <summary>A playlist attribute update (upstream <c>PlaylistUpdateAttributes</c>).</summary>
/// <param name="NewAttributes">The attributes after the update.</param>
/// <param name="OldAttributes">The attributes before the update.</param>
public sealed record PlaylistUpdateAttributes(PlaylistPartialAttributes NewAttributes, PlaylistPartialAttributes OldAttributes)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistUpdateAttributesMessage&gt;</c>.</summary>
    public static PlaylistUpdateAttributes From(ProtoUpdateListAttributes update) =>
        new(
            PlaylistPartialAttributes.From(Util.OrDefault(update.NewAttributes)),
            PlaylistPartialAttributes.From(Util.OrDefault(update.OldAttributes)));
}

/// <summary>A playlist item attribute update (upstream <c>PlaylistUpdateItemAttributes</c>).</summary>
/// <param name="Index">Index of the updated item.</param>
/// <param name="NewAttributes">The attributes after the update.</param>
/// <param name="OldAttributes">The attributes before the update.</param>
public sealed record PlaylistUpdateItemAttributes(int Index, PlaylistPartialItemAttributes NewAttributes, PlaylistPartialItemAttributes OldAttributes)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistUpdateItemAttributesMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">A timestamp is out of range.</exception>
    public static PlaylistUpdateItemAttributes From(ProtoUpdateItemAttributes update) =>
        new(
            update.Index,
            PlaylistPartialItemAttributes.From(Util.OrDefault(update.NewAttributes)),
            PlaylistPartialItemAttributes.From(Util.OrDefault(update.OldAttributes)));
}
