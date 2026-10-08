// Port of metadata/src/video.rs.
using Librespot.Core;
using ProtoVideoFile = Spotify.Metadata.VideoFile;

namespace Librespot.Metadata;

/// <summary>Upstream <c>VideoFiles(Vec&lt;FileId&gt;)</c>: the file ids of an item's video files.</summary>
public sealed class VideoFiles : List<FileId>
{
    /// <summary>Creates an empty list.</summary>
    public VideoFiles()
    {
    }

    /// <summary>Wraps the given file ids.</summary>
    public VideoFiles(IEnumerable<FileId> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_from_repeated!(VideoFileMessage, VideoFiles)</c> (via <c>From&lt;&amp;VideoFile&gt; for FileId</c>).</summary>
    public VideoFiles(IEnumerable<ProtoVideoFile> messages) : base(messages.Select(FileId.From))
    {
    }
}
