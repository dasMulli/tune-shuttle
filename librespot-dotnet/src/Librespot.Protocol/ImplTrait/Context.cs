// Port of protocol/src/impl_trait/context.rs.
//
// Upstream adds `Hash`/`Eq` for `Context` (by serialized bytes) and `From<Vec<String>>` /
// `From<Vec<ContextTrack>>` for `ContextPage`. Generated protobuf messages are partial classes, so the
// conversions live here as static factory methods on the generated type, and a hashing helper exposes the
// upstream `Hash` semantics (C# messages already implement structural Equals).
using Google.Protobuf;

namespace Spotify.Player.Proto;

public sealed partial class Context
{
    /// <summary>
    /// Upstream <c>impl Hash for Context</c>: hashes the serialized bytes. Use when a Context is a dictionary key
    /// (the generated <see cref="GetHashCode"/> is structural and slower for large contexts).
    /// </summary>
    public int ByteHash()
    {
        var h = new HashCode();
        h.AddBytes(this.ToByteArray());
        return h.ToHashCode();
    }
}

public sealed partial class ContextPage
{
    /// <summary>Upstream <c>From&lt;Vec&lt;String&gt;&gt; for ContextPage</c>: one ContextTrack per uri.</summary>
    public static ContextPage FromUris(IEnumerable<string> uris)
    {
        var page = new ContextPage();
        page.Tracks.AddRange(uris.Select(uri => new ContextTrack { Uri = uri }));
        return page;
    }

    /// <summary>Upstream <c>From&lt;Vec&lt;ContextTrack&gt;&gt; for ContextPage</c>.</summary>
    public static ContextPage FromTracks(IEnumerable<ContextTrack> tracks)
    {
        var page = new ContextPage();
        page.Tracks.AddRange(tracks);
        return page;
    }
}
