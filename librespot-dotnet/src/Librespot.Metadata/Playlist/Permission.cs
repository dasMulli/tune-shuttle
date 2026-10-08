// Port of metadata/src/playlist/permission.rs.
//
// Namespace: the playlist module's types live in Librespot.Metadata, not Librespot.Metadata.Playlist, because a
// namespace named Playlist would shadow the Playlist record (upstream re-exports it as librespot_metadata::Playlist)
// in every Librespot.Metadata file - the same exception the plan makes for Dealer/Protocol.
using PermissionLevel = Spotify.PlaylistPermission.Proto.PermissionLevel;
using ProtoCapabilities = Spotify.PlaylistPermission.Proto.Capabilities;

namespace Librespot.Metadata;

/// <summary>What the current user may do with a playlist (upstream <c>Capabilities</c>).</summary>
public sealed record Capabilities
{
    /// <summary>Whether the user may view the playlist.</summary>
    public required bool CanView { get; init; }

    /// <summary>Whether the user may administrate permissions.</summary>
    public required bool CanAdministratePermissions { get; init; }

    /// <summary>Permission levels the user may grant.</summary>
    public required PermissionLevels GrantableLevels { get; init; }

    /// <summary>Whether the user may edit the playlist's metadata.</summary>
    public required bool CanEditMetadata { get; init; }

    /// <summary>Whether the user may edit the playlist's items.</summary>
    public required bool CanEditItems { get; init; }

    /// <summary>Whether the user may cancel their membership.</summary>
    public required bool CanCancelMembership { get; init; }

    /// <summary>Upstream <c>From&lt;&amp;CapabilitiesMessage&gt;</c>.</summary>
    public static Capabilities From(ProtoCapabilities playlist) => new()
    {
        CanView = playlist.CanView,
        CanAdministratePermissions = playlist.CanAdministratePermissions,
        GrantableLevels = new PermissionLevels(playlist.GrantableLevel),
        CanEditMetadata = playlist.CanEditMetadata,
        CanEditItems = playlist.CanEditItems,
        CanCancelMembership = playlist.CanCancelMembership,
    };
}

/// <summary>Upstream <c>PermissionLevels(Vec&lt;PermissionLevel&gt;)</c>.</summary>
public sealed class PermissionLevels : List<PermissionLevel>
{
    /// <summary>Creates an empty list.</summary>
    public PermissionLevels()
    {
    }

    /// <summary>
    /// Upstream <c>impl_from_repeated_copy!(PermissionLevel, PermissionLevels)</c>, fed with
    /// <c>enum_value_or_default</c> values: unknown numbers become <c>UNKNOWN</c>.
    /// </summary>
    public PermissionLevels(IEnumerable<PermissionLevel> items) : base(items.Select(level => Util.EnumOrDefault(level)))
    {
    }
}
