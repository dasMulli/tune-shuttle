using System.Diagnostics.CodeAnalysis;

namespace Airplay2.Receiver.Codecs;

/// <summary>
/// Creates the AAC decoder of a stream (SPEC-receiver.md R4, 7.2). When it fails the receiver starts with the ALAC-only
/// variant of R3 and logs <c>reason</c> once.
/// </summary>
public interface IAacDecoderFactory
{
    /// <summary>Tries to create a decoder; false with <paramref name="reason"/> when the native libraries are missing or unusable (SPEC-receiver.md R4).</summary>
    bool TryCreate([NotNullWhen(true)] out IAacDecoder? decoder, [NotNullWhen(false)] out string? reason);
}
