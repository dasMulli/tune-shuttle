// Port of metadata/src/playlist/mod.rs.
//
// Upstream only declares the annotation, attribute, diff, item, list, operation and permission submodules and
// re-exports PlaylistAnnotation and Playlist. Their C# types live directly in namespace Librespot.Metadata (see
// Playlist/Permission.cs for why there is no Librespot.Metadata.Playlist namespace), so nothing else is needed here.
namespace Librespot.Metadata;
