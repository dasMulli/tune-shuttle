// Port of core/src/file_id.rs.
using Spotify.Metadata;

namespace Librespot.Core;

/// <summary>A 20-byte Spotify file identifier (audio file, image, video), formatted as 40 hex chars.</summary>
public readonly struct FileId : IEquatable<FileId>, IComparable<FileId>
{
    /// <summary>Upstream <c>RAW_LEN</c>.</summary>
    public const int RawLength = 20;

    private readonly byte[]? _raw; // null == all zero (default(FileId))

    private FileId(byte[] raw)
    {
        _raw = raw;
    }

    /// <summary>The raw bytes, always <see cref="RawLength"/> long.</summary>
    public ReadOnlySpan<byte> Raw => _raw ?? new byte[RawLength];

    /// <summary>
    /// Upstream <c>from_raw</c>: copies up to 20 bytes; shorter input is zero-padded (some tracks return 16
    /// instead of 20 bytes, librespot#1188) and longer input yields an all-zero id.
    /// </summary>
    public static FileId FromRaw(ReadOnlySpan<byte> src)
    {
        var dst = new byte[RawLength];
        if (src.Length <= RawLength)
        {
            src.CopyTo(dst);
        }

        return new FileId(dst);
    }

    /// <summary>Upstream <c>From&lt;&amp;protocol::metadata::Image&gt;</c>.</summary>
    public static FileId From(Image image) => FromRaw(image.FileId.Span);

    /// <summary>Upstream <c>From&lt;&amp;protocol::metadata::AudioFile&gt;</c>.</summary>
    public static FileId From(AudioFile file) => FromRaw(file.FileId.Span);

    /// <summary>Upstream <c>From&lt;&amp;protocol::metadata::VideoFile&gt;</c>.</summary>
    public static FileId From(VideoFile video) => FromRaw(video.FileId.Span);

    /// <summary>Lowercase hex, 40 characters.</summary>
    public string ToBase16() => Convert.ToHexStringLower(Raw);

    public override string ToString() => ToBase16();

    public bool Equals(FileId other) => Raw.SequenceEqual(other.Raw);

    public override bool Equals(object? obj) => obj is FileId other && Equals(other);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(Raw);
        return h.ToHashCode();
    }

    public int CompareTo(FileId other) => Raw.SequenceCompareTo(other.Raw);

    public static bool operator ==(FileId left, FileId right) => left.Equals(right);

    public static bool operator !=(FileId left, FileId right) => !left.Equals(right);
}
