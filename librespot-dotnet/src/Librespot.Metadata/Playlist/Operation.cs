// Port of metadata/src/playlist/operation.rs.
// Namespace Librespot.Metadata (not .Playlist): see Playlist/Permission.cs.
using Librespot.Core;
using PlaylistOperationKind = Spotify.Playlist4.Proto.Op.Types.Kind;
using ProtoAdd = Spotify.Playlist4.Proto.Add;
using ProtoMov = Spotify.Playlist4.Proto.Mov;
using ProtoOp = Spotify.Playlist4.Proto.Op;
using ProtoRem = Spotify.Playlist4.Proto.Rem;

namespace Librespot.Metadata;

/// <summary>One playlist change operation (upstream <c>PlaylistOperation</c>).</summary>
/// <remarks>
/// Upstream re-exports <c>PlaylistOperationKind</c>; it is the generated <c>Spotify.Playlist4.Proto.Op.Types.Kind</c>.
/// As upstream, every sub-operation is filled in (from a default message when absent); <see cref="Kind"/> says which applies.
/// </remarks>
public sealed record PlaylistOperation
{
    /// <summary>The kind of operation.</summary>
    public required PlaylistOperationKind Kind { get; init; }

    /// <summary>The add operation.</summary>
    public required PlaylistOperationAdd Add { get; init; }

    /// <summary>The remove operation.</summary>
    public required PlaylistOperationRemove Rem { get; init; }

    /// <summary>The move operation.</summary>
    public required PlaylistOperationMove Mov { get; init; }

    /// <summary>The item attribute update.</summary>
    public required PlaylistUpdateItemAttributes UpdateItemAttributes { get; init; }

    /// <summary>The list attribute update.</summary>
    public required PlaylistUpdateAttributes UpdateListAttributes { get; init; }

    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistOperationMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An item URI or timestamp could not be converted.</exception>
    public static PlaylistOperation From(ProtoOp operation) => new()
    {
        Kind = Util.EnumOrDefault(operation.Kind, PlaylistOperationKind.Unknown),
        Add = PlaylistOperationAdd.From(Util.OrDefault(operation.Add)),
        Rem = PlaylistOperationRemove.From(Util.OrDefault(operation.Rem)),
        Mov = PlaylistOperationMove.From(Util.OrDefault(operation.Mov)),
        UpdateItemAttributes = PlaylistUpdateItemAttributes.From(Util.OrDefault(operation.UpdateItemAttributes)),
        UpdateListAttributes = PlaylistUpdateAttributes.From(Util.OrDefault(operation.UpdateListAttributes)),
    };
}

/// <summary>Upstream <c>PlaylistOperations(Vec&lt;PlaylistOperation&gt;)</c>.</summary>
public sealed class PlaylistOperations : List<PlaylistOperation>
{
    /// <summary>Creates an empty list.</summary>
    public PlaylistOperations()
    {
    }

    /// <summary>Wraps the given operations.</summary>
    public PlaylistOperations(IEnumerable<PlaylistOperation> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(PlaylistOperationMessage, PlaylistOperations)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public PlaylistOperations(IEnumerable<ProtoOp> messages) : base(messages.Select(PlaylistOperation.From))
    {
    }
}

/// <summary>An add operation (upstream <c>PlaylistOperationAdd</c>).</summary>
/// <param name="FromIndex">Insert position.</param>
/// <param name="Items">The added items.</param>
/// <param name="AddLast">Append at the end.</param>
/// <param name="AddFirst">Insert at the start.</param>
public sealed record PlaylistOperationAdd(int FromIndex, PlaylistItems Items, bool AddLast, bool AddFirst)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistAddMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An item failed to convert.</exception>
    public static PlaylistOperationAdd From(ProtoAdd add) =>
        new(add.FromIndex, new PlaylistItems(add.Items), add.AddLast, add.AddFirst);
}

/// <summary>A move operation (upstream <c>PlaylistOperationMove</c>).</summary>
/// <param name="FromIndex">First moved index.</param>
/// <param name="Length">Number of moved items.</param>
/// <param name="ToIndex">Target index.</param>
public sealed record PlaylistOperationMove(int FromIndex, int Length, int ToIndex)
{
    /// <summary>Upstream <c>From&lt;&amp;PlaylistMoveMessage&gt;</c>.</summary>
    public static PlaylistOperationMove From(ProtoMov mov) => new(mov.FromIndex, mov.Length, mov.ToIndex);
}

/// <summary>A remove operation (upstream <c>PlaylistOperationRemove</c>).</summary>
/// <param name="FromIndex">First removed index.</param>
/// <param name="Length">Number of removed items.</param>
/// <param name="Items">The removed items.</param>
/// <param name="HasItemsAsKey">Whether the items identify what to remove.</param>
public sealed record PlaylistOperationRemove(int FromIndex, int Length, PlaylistItems Items, bool HasItemsAsKey)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;PlaylistRemoveMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">An item failed to convert.</exception>
    public static PlaylistOperationRemove From(ProtoRem remove) =>
        new(remove.FromIndex, remove.Length, new PlaylistItems(remove.Items), remove.ItemsAsKey);
}
