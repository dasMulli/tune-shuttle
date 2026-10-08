// Port of playback/src/local_file.rs (contract). Package `playback-pipeline` implements it; `playback-player`
// (wave 4) consumes it via Player.Create -> LocalFileLookup.Create(config.LocalFileDirectories) and
// PlayerTrackLoader -> lookup.Get(trackUri).
// TODO(port: playback-pipeline): directory walk (SUPPORTED_FILE_EXTENSIONS mp3/mp4/m4p/flac), tag probing
//   (NVorbis / NLayer tags) into the spotify:local:{artist}:{album}:{title}:{duration} key, LocalFileMetadata.
using Librespot.Core;

namespace Librespot.Playback;

/// <summary>Upstream <c>LocalFileLookup(HashMap&lt;SpotifyUri, PathBuf&gt;)</c>: maps <c>spotify:local:</c> URIs to file paths.</summary>
public sealed class LocalFileLookup
{
    private readonly Dictionary<SpotifyUri, string> _entries;

    /// <summary>An empty lookup (upstream <c>Default</c>).</summary>
    public LocalFileLookup() : this(new Dictionary<SpotifyUri, string>())
    {
    }

    internal LocalFileLookup(Dictionary<SpotifyUri, string> entries)
    {
        _entries = entries;
    }

    /// <summary>Upstream <c>get(&amp;SpotifyUri) -&gt; Option&lt;&amp;Path&gt;</c>; the player calls it with any <see cref="SpotifyUri"/>.</summary>
    public string? Get(SpotifyUri uri) => _entries.TryGetValue(uri, out var path) ? path : null;

    /// <summary>Upstream free function <c>create_local_file_lookup(&amp;[PathBuf])</c>: scans the directories once.</summary>
    public static LocalFileLookup Create(IReadOnlyList<string> directories) =>
        throw new NotImplementedException("TODO(port: playback-pipeline)");
}
