using System.Diagnostics.CodeAnalysis;
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
/// Wave 0 scaffold: the surface compiles; the binding is the wave 1 <c>aac-decoder</c> package (SPEC-receiver.md 13), which
/// also adds the FFmpeg.AutoGen package pin and <c>tools/get-ffmpeg.ps1</c>.
/// </remarks>
public sealed class FfmpegAacDecoder : IAacDecoder
{
    internal FfmpegAacDecoder()
    {
    }

    /// <inheritdoc />
    public bool TryDecode(ReadOnlySpan<byte> adtsBlock, Span<short> pcmOut, out int frames) => throw new NotImplementedException("SPEC-receiver.md 7.2, wave 1");

    /// <inheritdoc />
    public void Reset() => throw new NotImplementedException("SPEC-receiver.md 7.2, wave 1");

    /// <summary>Frees the codec context.</summary>
    public void Dispose()
    {
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

    /// <summary>Creates the factory.</summary>
    /// <param name="libraryPath">Directory of the libavcodec libraries, or null for the default search path (<c>Bridge:Airplay:FfmpegPath</c>, SPEC-receiver.md 8.5).</param>
    /// <param name="logger">Logger.</param>
    public FfmpegAacDecoderFactory(string? libraryPath = null, ILogger? logger = null)
    {
        LibraryPath = libraryPath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The configured library directory, or null.</summary>
    public string? LibraryPath { get; }

    /// <summary>
    /// Until the wave 1 <c>aac-decoder</c> package lands this always fails, so the receiver advertises the ALAC-only variant
    /// (SPEC-receiver.md R3, R4) and the rest of the system is exercised with it.
    /// </summary>
    public bool TryCreate([NotNullWhen(true)] out IAacDecoder? decoder, [NotNullWhen(false)] out string? reason)
    {
        decoder = null;
        reason = "libavcodec binding not implemented yet (SPEC-receiver.md 7.2, wave 1); running ALAC-only";
        _logger.LogDebug("AAC decoder unavailable: {Reason}", reason);
        return false;
    }
}
