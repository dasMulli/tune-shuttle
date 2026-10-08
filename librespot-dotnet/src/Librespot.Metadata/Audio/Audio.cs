// Port of metadata/src/audio/mod.rs.
//
// Upstream only declares the `file` and `item` submodules and re-exports AudioFileFormat, AudioFiles, AudioItem and
// UniqueFields. In C# those types already live in this namespace (Audio/File.cs, Audio/Item.cs), so nothing else is
// needed here.
namespace Librespot.Metadata.Audio;
