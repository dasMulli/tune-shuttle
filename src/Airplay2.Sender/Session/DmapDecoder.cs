using System.Diagnostics.CodeAnalysis;

namespace Airplay2.Sender.Session;

/// <summary>
/// DMAP (<c>application/x-dmap-tagged</c>) decoder for the text metadata item a sender sends (SPEC-receiver.md 4.8,
/// SPEC-control.md 5.2): the inverse of <see cref="DmapEncoder"/>. Walks <c>tag || length || value</c> items inside the
/// outer <c>mlit</c> (shairport-sync skips the 8-byte outer header the same way, <c>[sps] rtsp.c:3282-3314</c>); unknown
/// tags are kept by code, malformed bodies are rejected.
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the decoder is the wave 1 <c>dmap-and-volume</c> package (SPEC-receiver.md 13).
/// </remarks>
public static class DmapDecoder
{
    /// <summary>Decodes an <c>mlit</c> body (SPEC-receiver.md 4.8).</summary>
    /// <exception cref="FormatException">The body is not a well-formed <c>mlit</c> container.</exception>
    public static DmapItem Decode(ReadOnlySpan<byte> body) => throw new NotImplementedException("SPEC-receiver.md 4.8, wave 1");

    /// <summary>Decodes an <c>mlit</c> body; false instead of throwing for a malformed one (SPEC-receiver.md 4.8).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> body, [NotNullWhen(true)] out DmapItem? item) => throw new NotImplementedException("SPEC-receiver.md 4.8, wave 1");
}
