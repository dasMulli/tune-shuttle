// Port of metadata/src/playlist/diff.rs.
// Namespace Librespot.Metadata (not .Playlist): see Playlist/Permission.cs.
using Librespot.Core;
using ProtoDiff = Spotify.Playlist4.Proto.Diff;

namespace Librespot.Metadata;

/// <summary>The operations between two playlist revisions (upstream <c>PlaylistDiff</c>).</summary>
/// <param name="FromRevision">The starting revision.</param>
/// <param name="Operations">The operations.</param>
/// <param name="ToRevision">The resulting revision.</param>
public sealed record PlaylistDiff(SpotifyId FromRevision, PlaylistOperations Operations, SpotifyId ToRevision)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;DiffMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">A revision is not 16 bytes, or an operation failed to convert.</exception>
    public static PlaylistDiff From(ProtoDiff diff) =>
        new(
            SpotifyId.FromRaw(diff.FromRevision.Span),
            new PlaylistOperations(diff.Ops),
            SpotifyId.FromRaw(diff.ToRevision.Span));
}
