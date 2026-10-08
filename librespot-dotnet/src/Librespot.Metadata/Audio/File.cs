// Port of metadata/src/audio/file.rs.
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using ProtoAudioFile = Spotify.Metadata.AudioFile;
using ProtoFormat = Spotify.Metadata.AudioFile.Types.Format;

namespace Librespot.Metadata.Audio;

/// <summary>Audio file formats; numeric values match the protobuf <c>AudioFile.Format</c> enum, plus undocumented ones Spotify sends.</summary>
public enum AudioFileFormat
{
    /// <summary>Ogg Vorbis, 96 kbit/s.</summary>
    OggVorbis96 = 0,
    /// <summary>Ogg Vorbis, 160 kbit/s.</summary>
    OggVorbis160 = 1,
    /// <summary>Ogg Vorbis, 320 kbit/s.</summary>
    OggVorbis320 = 2,
    /// <summary>MP3, 256 kbit/s.</summary>
    Mp3256 = 3,
    /// <summary>MP3, 320 kbit/s.</summary>
    Mp3320 = 4,
    /// <summary>MP3, 160 kbit/s.</summary>
    Mp3160 = 5,
    /// <summary>MP3, 96 kbit/s.</summary>
    Mp396 = 6,
    /// <summary>MP3, 160 kbit/s, encrypted.</summary>
    Mp3160Enc = 7,
    /// <summary>AAC, 24 kbit/s.</summary>
    Aac24 = 8,
    /// <summary>AAC, 48 kbit/s.</summary>
    Aac48 = 9,
    /// <summary>FLAC.</summary>
    FlacFlac = 16,
    /// <summary>xHE-AAC, 24 kbit/s.</summary>
    XheAac24 = 18,
    /// <summary>xHE-AAC, 16 kbit/s.</summary>
    XheAac16 = 19,
    /// <summary>xHE-AAC, 12 kbit/s.</summary>
    XheAac12 = 20,
    /// <summary>FLAC, 24 bit.</summary>
    FlacFlac24Bit = 22,

    // not defined in protobuf, but sometimes sent

    /// <summary>AAC, 160 kbit/s (not in the protobuf).</summary>
    Aac160 = 10,
    /// <summary>AAC, 320 kbit/s (not in the protobuf).</summary>
    Aac320 = 11,
    /// <summary>MP4, 128 kbit/s (not in the protobuf).</summary>
    Mp4128 = 12,
    /// <summary>Unknown format 13 (not in the protobuf).</summary>
    Other5 = 13,
}

/// <summary>Conversions into <see cref="AudioFileFormat"/>.</summary>
public static class AudioFileFormatExtensions
{
    /// <summary>
    /// Upstream <c>TryFrom&lt;i32&gt;</c>: 10..=13 map to the formats that are not in the protobuf, every other value
    /// must be a protobuf <c>Format</c>; null for unknown values.
    /// </summary>
    public static AudioFileFormat? FromInt(int value) => value switch
    {
        10 => AudioFileFormat.Aac160,
        11 => AudioFileFormat.Aac320,
        12 => AudioFileFormat.Mp4128,
        13 => AudioFileFormat.Other5,
        _ when Enum.IsDefined((ProtoFormat)value) => FromProto((ProtoFormat)value),
        _ => null,
    };

    /// <summary>Upstream <c>From&lt;Format&gt;</c>: the numeric values are identical.</summary>
    public static AudioFileFormat FromProto(ProtoFormat format) => (AudioFileFormat)(int)format;
}

/// <summary>Map of format to file id for one audio item (upstream <c>AudioFiles(HashMap&lt;AudioFileFormat, FileId&gt;)</c>).</summary>
public sealed class AudioFiles : Dictionary<AudioFileFormat, FileId>
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<AudioFiles>();

    /// <summary>Creates an empty map.</summary>
    public AudioFiles()
    {
    }

    /// <summary>
    /// Upstream <c>From&lt;&amp;[AudioFileMessage]&gt;</c>. Files without a format, or with a number that is not a
    /// protobuf <c>Format</c> (including the undocumented 10..=13), are ignored with a trace message.
    /// </summary>
    public AudioFiles(IEnumerable<ProtoAudioFile> files)
    {
        foreach (var file in files)
        {
            var fileId = FileId.From(file);
            if (!file.HasFormat)
            {
                Log.LogTrace("Ignoring file <{FileId}> with unspecified format", fileId);
                continue;
            }

            if (!Enum.IsDefined(file.Format))
            {
                Log.LogTrace("Ignoring file <{FileId}> with unknown format {Format}", fileId, (int)file.Format);
                continue;
            }

            this[AudioFileFormatExtensions.FromProto(file.Format)] = fileId;
        }
    }

    /// <summary>Whether the format is Ogg Vorbis.</summary>
    public static bool IsOggVorbis(AudioFileFormat format) =>
        format is AudioFileFormat.OggVorbis320 or AudioFileFormat.OggVorbis160 or AudioFileFormat.OggVorbis96;

    /// <summary>Whether the format is MP3.</summary>
    public static bool IsMp3(AudioFileFormat format) =>
        format is AudioFileFormat.Mp3320 or AudioFileFormat.Mp3256 or AudioFileFormat.Mp3160 or AudioFileFormat.Mp396 or AudioFileFormat.Mp3160Enc;

    /// <summary>Whether the format is FLAC. Upstream only matches <c>FLAC_FLAC</c>, not <c>FLAC_FLAC_24BIT</c>.</summary>
    public static bool IsFlac(AudioFileFormat format) => format is AudioFileFormat.FlacFlac;

    /// <summary>The MIME type of the format's container, if it is one the player can decode.</summary>
    public static string? MimeType(AudioFileFormat format)
    {
        if (IsOggVorbis(format))
        {
            return "audio/ogg";
        }

        if (IsMp3(format))
        {
            return "audio/mpeg";
        }

        if (IsFlac(format))
        {
            return "audio/flac";
        }

        return null;
    }
}
