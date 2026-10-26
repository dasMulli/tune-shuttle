using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Codecs;

/// <summary>
/// AAC-LC through ffmpeg's libavcodec via P/Invoke (SPEC-receiver.md R4, 7.2: FFmpeg.AutoGen, dynamically loaded
/// bindings, <c>avcodec_find_decoder(AV_CODEC_ID_AAC)</c>, <c>avcodec_send_packet</c> with the ADTS block,
/// <c>avcodec_receive_frame</c>; planar <c>fltp</c>/<c>s16p</c> output interleaved and converted to S16 with clipping).
/// One decoder per stream, <see cref="Reset"/> flushes.
/// </summary>
/// <remarks>
/// Created by <see cref="FfmpegAacDecoderFactory.TryCreate"/>, which binds the native libraries first. Not thread-safe;
/// owned by the buffered reader (7.2). Output is always stereo: a mono stream is duplicated into both channels, channels
/// beyond the second are dropped. Frames at a sampling rate other than 44 100 Hz are refused (R3: no resampler, N4).
/// </remarks>
public sealed unsafe class FfmpegAacDecoder : IAacDecoder
{
    /// <summary>The only output rate (SPEC-receiver.md R3).</summary>
    public const int SampleRate = 44100;

    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;

    /// <summary>Opens a libavcodec AAC decoder context (SPEC-receiver.md 7.2). The bindings must be initialized.</summary>
    /// <exception cref="InvalidOperationException">libavcodec has no AAC decoder or the context cannot be opened.</exception>
    internal FfmpegAacDecoder()
    {
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null)
        {
            throw new InvalidOperationException("libavcodec has no AAC decoder (avcodec_find_decoder(AV_CODEC_ID_AAC) returned null)");
        }

        try
        {
            _context = ffmpeg.avcodec_alloc_context3(codec);
            if (_context == null)
            {
                throw new InvalidOperationException("avcodec_alloc_context3 failed");
            }

            var rc = ffmpeg.avcodec_open2(_context, codec, null);
            if (rc < 0)
            {
                throw new InvalidOperationException($"avcodec_open2 failed: {FfmpegLibraries.ErrorText(rc)}");
            }

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            if (_packet == null || _frame == null)
            {
                throw new InvalidOperationException("av_packet_alloc/av_frame_alloc failed");
            }
        }
        catch
        {
            Free();
            throw;
        }
    }

    /// <summary>Frees the native state if <see cref="Dispose"/> was not called.</summary>
    ~FfmpegAacDecoder() => Free();

    /// <inheritdoc />
    /// <remarks>
    /// SPEC-receiver.md 7.2: one <c>avcodec_send_packet</c>, then <c>avcodec_receive_frame</c> until it reports
    /// <c>EAGAIN</c>. False (and <paramref name="frames"/> 0) when libavcodec rejects the block, the output is not 44.1 kHz
    /// or not in a supported sample format, or <paramref name="pcmOut"/> cannot hold the decoded frames.
    /// </remarks>
    public bool TryDecode(ReadOnlySpan<byte> adtsBlock, Span<short> pcmOut, out int frames)
    {
        ObjectDisposedException.ThrowIf(_context == null, this);
        frames = 0;
        if (adtsBlock.IsEmpty)
        {
            return false;
        }

        ffmpeg.av_packet_unref(_packet);
        if (ffmpeg.av_new_packet(_packet, adtsBlock.Length) < 0)
        {
            return false;
        }

        adtsBlock.CopyTo(new Span<byte>(_packet->data, adtsBlock.Length));
        var rc = ffmpeg.avcodec_send_packet(_context, _packet);
        ffmpeg.av_packet_unref(_packet);
        var ok = rc >= 0;

        // Drain whatever the decoder has, also after a rejected packet, so the next block starts from an empty queue.
        var written = 0;
        while (true)
        {
            rc = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (rc == ffmpeg.AVERROR(ffmpeg.EAGAIN) || rc == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (rc < 0)
            {
                ok = false;
                break;
            }

            try
            {
                if (ok && Interleave(_frame, pcmOut[(written * 2)..], out var n))
                {
                    written += n;
                }
                else
                {
                    ok = false;
                }
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }

        if (!ok)
        {
            return false;
        }

        frames = written;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>SPEC-receiver.md 7.2: <c>avcodec_flush_buffers</c>; the next block decodes without the previous overlap.</remarks>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_context == null, this);
        ffmpeg.avcodec_flush_buffers(_context);
    }

    /// <summary>Frees the codec context, packet and frame (SPEC-receiver.md 7.2). Idempotent.</summary>
    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    /// <summary>Converts one float sample to S16 with clipping (SPEC-receiver.md 7.2; the libswresample scale of 2^15).</summary>
    internal static short ClipToS16(float sample)
    {
        var scaled = sample * 32768f;
        if (scaled >= 32767f)
        {
            return short.MaxValue;
        }

        if (scaled <= -32768f)
        {
            return short.MinValue;
        }

        return (short)MathF.Round(scaled); // NaN rounds to NaN and converts to 0.
    }

    private static bool Interleave(AVFrame* frame, Span<short> destination, out int frames)
    {
        frames = frame->nb_samples;
        var channels = frame->ch_layout.nb_channels;
        if (frame->sample_rate != SampleRate || channels < 1 || frames < 0 || destination.Length < frames * 2)
        {
            frames = 0;
            return false;
        }

        var right = channels > 1 ? 1 : 0;
        switch ((AVSampleFormat)frame->format)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
            {
                var l = (float*)frame->extended_data[0];
                var r = (float*)frame->extended_data[right];
                for (var i = 0; i < frames; i++)
                {
                    destination[2 * i] = ClipToS16(l[i]);
                    destination[(2 * i) + 1] = ClipToS16(r[i]);
                }

                return true;
            }

            case AVSampleFormat.AV_SAMPLE_FMT_S16P:
            {
                var l = (short*)frame->extended_data[0];
                var r = (short*)frame->extended_data[right];
                for (var i = 0; i < frames; i++)
                {
                    destination[2 * i] = l[i];
                    destination[(2 * i) + 1] = r[i];
                }

                return true;
            }

            case AVSampleFormat.AV_SAMPLE_FMT_FLT:
            {
                var p = (float*)frame->extended_data[0];
                for (var i = 0; i < frames; i++)
                {
                    destination[2 * i] = ClipToS16(p[i * channels]);
                    destination[(2 * i) + 1] = ClipToS16(p[(i * channels) + right]);
                }

                return true;
            }

            case AVSampleFormat.AV_SAMPLE_FMT_S16:
            {
                var p = (short*)frame->extended_data[0];
                for (var i = 0; i < frames; i++)
                {
                    destination[2 * i] = p[i * channels];
                    destination[(2 * i) + 1] = p[(i * channels) + right];
                }

                return true;
            }

            default:
                frames = 0;
                return false;
        }
    }

    private void Free()
    {
        if (_frame != null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet != null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_context != null)
        {
            var context = _context;
            ffmpeg.avcodec_free_context(&context);
            _context = null;
        }
    }
}

/// <summary>
/// Creates <see cref="FfmpegAacDecoder"/>s (SPEC-receiver.md R4, 7.2): loads libavcodec from <see cref="LibraryPath"/> or the
/// default search path on first use; a missing library makes <see cref="TryCreate"/> fail with the reason, and the receiver
/// runs ALAC-only (R3).
/// </summary>
public sealed class FfmpegAacDecoderFactory : IAacDecoderFactory
{
    private readonly ILogger _logger;
    private int _loggedVersion;

    /// <summary>Creates the factory (SPEC-receiver.md 7.2, 8.5).</summary>
    /// <param name="libraryPath">Directory of the libavcodec libraries, or null for the default search path (<c>Bridge:Airplay:FfmpegPath</c>, SPEC-receiver.md 8.5).</param>
    /// <param name="logger">Logger.</param>
    public FfmpegAacDecoderFactory(string? libraryPath = null, ILogger? logger = null)
    {
        LibraryPath = string.IsNullOrWhiteSpace(libraryPath) ? null : libraryPath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The configured library directory, or null (SPEC-receiver.md 7.2).</summary>
    public string? LibraryPath { get; }

    /// <summary>
    /// Binds the FFmpeg libraries of the pinned major (avutil, swresample, avcodec; <see cref="FfmpegLibraries"/>) and opens
    /// a decoder (SPEC-receiver.md R4, 7.2). False with the reason when the libraries are missing, of another major or the
    /// AAC decoder cannot be opened; the caller then advertises the ALAC-only variant (R3) and logs the reason once.
    /// </summary>
    public bool TryCreate([NotNullWhen(true)] out IAacDecoder? decoder, [NotNullWhen(false)] out string? reason)
    {
        decoder = null;
        if (!FfmpegLibraries.TryBind(LibraryPath, out var version, out reason))
        {
            _logger.LogDebug("AAC decoder unavailable: {Reason}", reason);
            return false;
        }

        try
        {
            decoder = new FfmpegAacDecoder();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            reason = $"libavcodec AAC decoder unusable: {ex.Message}";
            _logger.LogDebug("AAC decoder unavailable: {Reason}", reason);
            return false;
        }

        if (Interlocked.Exchange(ref _loggedVersion, 1) == 0)
        {
            _logger.LogInformation("AAC decoder: libavcodec {Version}", version);
        }

        reason = null;
        return true;
    }
}

/// <summary>
/// Locates and binds the FFmpeg libraries the AAC decoder needs (SPEC-receiver.md 7.2): avutil, swresample and avcodec of
/// the major FFmpeg.AutoGen targets (<c>ffmpeg.LibraryVersionMap</c>: avcodec 61 for FFmpeg 7.1), loaded in dependency order
/// from a directory or the platform's default search path, then <c>DynamicallyLoadedBindings</c> with <c>ffmpeg.RootPath</c>.
/// </summary>
/// <remarks>
/// <c>ffmpeg.RootPath</c> and the bindings are process-wide: the first directory that binds wins, and the loaded libraries
/// stay loaded for the life of the process. Every <see cref="TryBind"/> still probes its own directory first, so a factory
/// pointed at a directory without the libraries fails even after another one bound.
/// </remarks>
internal static class FfmpegLibraries
{
    private static readonly string[] Libraries = ["avutil", "swresample", "avcodec"];
    private static readonly object Gate = new();
    private static string? s_version;

    /// <summary>The libavcodec major the bindings target.</summary>
    internal static int AvcodecMajor => ffmpeg.LibraryVersionMap["avcodec"];

    /// <summary>The platform file name of <paramref name="library"/> at the major the bindings target (as FFmpeg.AutoGen's resolvers build it).</summary>
    internal static string FileName(string library)
    {
        var major = ffmpeg.LibraryVersionMap[library];
        if (OperatingSystem.IsWindows())
        {
            return $"{library}-{major}.dll";
        }

        return OperatingSystem.IsMacOS() ? $"lib{library}.{major}.dylib" : $"lib{library}.so.{major}";
    }

    /// <summary>Loads the libraries from <paramref name="directory"/> (or the default search) and initializes the bindings once.</summary>
    internal static bool TryBind(string? directory, [NotNullWhen(true)] out string? version, [NotNullWhen(false)] out string? reason)
    {
        version = null;
        string? found;
        if (directory is not null)
        {
            var full = Path.GetFullPath(directory);
            if (!Directory.Exists(full))
            {
                reason = $"libavcodec not found: the directory {full} does not exist (Bridge:Airplay:FfmpegPath)";
                return false;
            }

            if (!TryLoadAll(full, out reason))
            {
                return false;
            }

            found = full;
        }
        else if (TryLoadAll(AppContext.BaseDirectory, out _))
        {
            found = AppContext.BaseDirectory;
        }
        else if (TryLoadAll(null, out reason))
        {
            found = string.Empty;
        }
        else
        {
            reason += $" (searched {AppContext.BaseDirectory} and the default library path)";
            return false;
        }

        lock (Gate)
        {
            if (s_version is null)
            {
                try
                {
                    ffmpeg.RootPath = found;
                    DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                    DynamicallyLoadedBindings.Initialize();
                    var avcodec = ffmpeg.avcodec_version();
                    if ((int)(avcodec >> 16) != AvcodecMajor)
                    {
                        reason = $"libavcodec major {avcodec >> 16} loaded, the bindings need {AvcodecMajor}";
                        return false;
                    }

                    s_version = $"{avcodec >> 16}.{(avcodec >> 8) & 0xFF}.{avcodec & 0xFF} ({ffmpeg.av_version_info()})";
                }
                catch (Exception ex) when (ex is NotSupportedException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
                {
                    reason = $"libavcodec binding failed: {ex.Message}";
                    return false;
                }
            }

            version = s_version;
        }

        reason = null;
        return true;
    }

    /// <summary>The text of an FFmpeg error code (<c>av_strerror</c>).</summary>
    internal static unsafe string ErrorText(int error)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        return ffmpeg.av_strerror(error, buffer, size) == 0
            ? Marshal.PtrToStringAnsi((IntPtr)buffer) ?? error.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"error {error}";
    }

    private static bool TryLoadAll(string? directory, [NotNullWhen(false)] out string? reason)
    {
        foreach (var library in Libraries)
        {
            var name = FileName(library);
            var path = directory is null ? name : Path.Combine(directory, name);
            if (!NativeLibrary.TryLoad(path, out _))
            {
                reason = directory is null
                    ? $"libavcodec not found: {name} is not on the default library path"
                    : $"libavcodec not found: {name} could not be loaded from {directory}";
                return false;
            }
        }

        reason = null;
        return true;
    }
}
