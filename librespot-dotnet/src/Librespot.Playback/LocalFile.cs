// Port of playback/src/local_file.rs. Package `playback-player` (wave 4) consumes it via
// Player.Create -> LocalFileLookup.Create(config.LocalFileDirectories) and PlayerTrackLoader -> lookup.Get(trackUri).
//
// Probing uses this port's decoders instead of symphonia: of the supported extensions only MP3 files (ID3v2 tags,
// NLayer) can be probed. MP4/M4P (no AAC/MP4 demuxer) and FLAC (not ported, see PORT-PLAN section 2) fail the probe
// and are skipped with the same warning upstream logs for unreadable files.
using System.Text;
using Librespot.Core;
using Librespot.Core.Logging;
using Librespot.Playback.Decoder;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback;

/// <summary>Upstream <c>LocalFileLookup(HashMap&lt;SpotifyUri, PathBuf&gt;)</c>: maps <c>spotify:local:</c> URIs to file paths.</summary>
public sealed class LocalFileLookup
{
    // "Spotify supports .mp3, .mp4, and .m4p files. It doesn't support .mp4 files that contain video,
    // or the iTunes lossless format (M4A)."
    // https://community.spotify.com/t5/FAQs/Local-Files/ta-p/5186118
    //
    // There are some indications online that FLAC is supported, so check for this as well.
    private static readonly string[] SupportedFileExtensions = ["mp3", "mp4", "m4p", "flac"];

    private static readonly ILogger Log = LibrespotLog.CreateLogger<LocalFileLookup>();

    private readonly Dictionary<SpotifyUri, string> _entries;

    /// <summary>An empty lookup (upstream <c>Default</c>).</summary>
    public LocalFileLookup() : this(new Dictionary<SpotifyUri, string>())
    {
    }

    internal LocalFileLookup(Dictionary<SpotifyUri, string> entries)
    {
        _entries = entries;
    }

    /// <summary>The number of indexed files.</summary>
    public int Count => _entries.Count;

    /// <summary>Upstream <c>get(&amp;SpotifyUri) -&gt; Option&lt;&amp;Path&gt;</c>; the player calls it with any <see cref="SpotifyUri"/>.</summary>
    public string? Get(SpotifyUri uri) => _entries.TryGetValue(uri, out var path) ? path : null;

    /// <summary>Upstream free function <c>create_local_file_lookup(&amp;[PathBuf])</c>: scans the directories once.</summary>
    public static LocalFileLookup Create(IReadOnlyList<string> directories)
    {
        var lookup = new LocalFileLookup();

        foreach (var path in directories)
        {
            if (!Directory.Exists(path))
            {
                Log.LogWarning("Ignoring local file source {Path}: not a directory", path);
                continue;
            }

            try
            {
                VisitDir(path, lookup);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.LogWarning("Failed to load entries from local file source {Path}: {Error}", path, e.Message);
            }
        }

        return lookup;
    }

    /// <summary>Upstream <c>visit_dir</c>: recursive walk; an I/O error aborts the walk of this source.</summary>
    private static void VisitDir(string dir, LocalFileLookup accumulator)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Directory.Exists(path))
            {
                VisitDir(path, accumulator);
                continue;
            }

            if (RustExtension(path) is not { } fileExtension)
            {
                continue;
            }

            var lowercaseExtension = fileExtension.ToLowerInvariant();

            if (Array.IndexOf(SupportedFileExtensions, lowercaseExtension) >= 0)
            {
                SpotifyUri uri;
                try
                {
                    uri = GetUriFromFile(path, fileExtension);
                }
                catch (LibrespotException e)
                {
                    Log.LogWarning("Failed to determine URI of local file {Path}: {Error}", path, e.Message);
                    continue;
                }

                accumulator._entries[uri] = path;
            }
        }
    }

    /// <summary>Upstream <c>get_uri_from_file</c>: builds the <c>spotify:local:</c> URI from the file's tags and duration.</summary>
    /// <exception cref="LibrespotException">The file cannot be opened or probed.</exception>
    internal static SpotifyUri GetUriFromFile(string audioPath, string fileExtension)
    {
        FileStream src;
        try
        {
            src = File.OpenRead(audioPath);
        }
        catch (Exception e) when (e is not LibrespotException)
        {
            throw LibrespotException.From(e);
        }

        ProbedTrack probed;
        using (src)
        {
            try
            {
                probed = AudioDecoders.ProbeTrack(src, new DecoderHint(Extension: fileExtension));
            }
            catch (Exception e) when (e is DecoderException or not LibrespotException)
            {
                throw LibrespotException.Internal("Failed to probe file");
            }
        }

        string? artist = null;
        string? albumTitle = null;
        string? trackTitle = null;

        var tags = probed.Metadata ?? throw LibrespotException.Internal("Failed to probe audio tags");
        foreach (var tag in tags)
        {
            switch (tag.StdKey)
            {
                case StandardTagKey.Album:
                    albumTitle = tag.Value.ToString();
                    break;
                case StandardTagKey.Artist:
                    artist = tag.Value.ToString();
                    break;
                case StandardTagKey.TrackTitle:
                    trackTitle = tag.Value.ToString();
                    break;
            }
        }

        // Upstream: default_track() always exists after a successful probe here; time_base is 1/sample_rate.
        if (probed.SampleRate is not { } sampleRate)
        {
            throw LibrespotException.Internal("Failed to calculate track duration");
        }

        if (probed.NFrames is not { } numFrames)
        {
            throw LibrespotException.Internal("Failed to calculate track duration");
        }

        // time_base.calc_time(num_frames).seconds
        var seconds = numFrames / sampleRate;

        return new SpotifyUri.Local(
            FormatUriPart(artist),
            FormatUriPart(albumTitle),
            FormatUriPart(trackTitle),
            TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Upstream <c>format_uri_part</c>: <c>application/x-www-form-urlencoded</c> byte serialization
    /// (<c>form_urlencoded::byte_serialize</c>): ASCII alphanumerics and <c>*-._</c> stay, space becomes <c>+</c>,
    /// every other UTF-8 byte becomes <c>%XX</c> (upper-case hex). Null becomes the empty string.
    /// </summary>
    internal static string FormatUriPart(string? input)
    {
        if (input is null)
        {
            return "";
        }

        var sb = new StringBuilder(input.Length);
        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            if (b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
            {
                sb.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rust <c>Path::extension</c>: the text after the last '.' of the file name; none when there is no '.', or the
    /// only '.' starts the file name (".mp3").
    /// </summary>
    internal static string? RustExtension(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName) || fileName == "..")
        {
            return null;
        }

        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? null : fileName[(dot + 1)..];
    }
}
