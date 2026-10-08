// Port of metadata/src/audio/file.rs.
using Librespot.Core;
using ProtoFormat = Spotify.Metadata.AudioFile.Types.Format;

namespace Librespot.Metadata.Audio;

/// <summary>Audio file formats; numeric values match the protobuf <c>AudioFile.Format</c> enum, plus undocumented ones Spotify sends.</summary>
public enum AudioFileFormat
{
    OggVorbis96 = 0,
    OggVorbis160 = 1,
    OggVorbis320 = 2,
    Mp3256 = 3,
    Mp3320 = 4,
    Mp3160 = 5,
    Mp396 = 6,
    Mp3160Enc = 7,
    Aac24 = 8,
    Aac48 = 9,
    FlacFlac = 16,
    XheAac24 = 18,
    XheAac16 = 19,
    XheAac12 = 20,
    FlacFlac24Bit = 22,
    // not defined in protobuf, but sometimes sent
    Aac160 = 10,
    Aac320 = 11,
    Mp4128 = 12,
    Other5 = 13,
}

public static class AudioFileFormatExtensions
{
    /// <summary>Upstream <c>TryFrom&lt;i32&gt;</c>: null for unknown values.</summary>
    public static AudioFileFormat? FromInt(int value) =>
        Enum.IsDefined(typeof(AudioFileFormat), value) ? (AudioFileFormat)value : null;

    /// <summary>Upstream <c>From&lt;Format&gt;</c>.</summary>
    public static AudioFileFormat FromProto(ProtoFormat format) => (AudioFileFormat)(int)format;
}

/// <summary>Map of format to file id for one audio item (upstream <c>AudioFiles(HashMap&lt;AudioFileFormat, FileId&gt;)</c>).</summary>
public sealed class AudioFiles : Dictionary<AudioFileFormat, FileId>
{
    public AudioFiles()
    {
    }

    public AudioFiles(IEnumerable<Spotify.Metadata.AudioFile> files)
    {
        foreach (var file in files)
        {
            if (file.HasFormat && AudioFileFormatExtensions.FromInt((int)file.Format) is { } format)
            {
                this[format] = FileId.From(file);
            }
        }
    }

    public static bool IsOggVorbis(AudioFileFormat format) => format is AudioFileFormat.OggVorbis320 or AudioFileFormat.OggVorbis160 or AudioFileFormat.OggVorbis96;

    public static bool IsMp3(AudioFileFormat format) => format is AudioFileFormat.Mp3320 or AudioFileFormat.Mp3256 or AudioFileFormat.Mp3160 or AudioFileFormat.Mp396 or AudioFileFormat.Mp3160Enc;

    public static bool IsFlac(AudioFileFormat format) => format is AudioFileFormat.FlacFlac or AudioFileFormat.FlacFlac24Bit;

    public static string? MimeType(AudioFileFormat format)
    {
        if (IsOggVorbis(format)) return "audio/ogg";
        if (IsMp3(format)) return "audio/mpeg";
        if (IsFlac(format)) return "audio/flac";
        return null;
    }
}
