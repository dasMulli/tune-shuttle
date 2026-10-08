// Port of metadata/src/copyright.rs.
using CopyrightType = Spotify.Metadata.Copyright.Types.Type;
using ProtoCopyright = Spotify.Metadata.Copyright;

namespace Librespot.Metadata;

/// <summary>A copyright notice.</summary>
/// <param name="CopyrightType">Upstream <c>CopyrightType</c> (re-export of <c>protocol::metadata::copyright::Type</c>).</param>
/// <param name="Text">The notice text.</param>
public sealed record Copyright(CopyrightType CopyrightType, string Text)
{
    /// <summary>Upstream <c>From&lt;&amp;CopyrightMessage&gt;</c>.</summary>
    public static Copyright From(ProtoCopyright copyright) =>
        new(Util.EnumOrDefault(copyright.Type, CopyrightType.P), copyright.Text);
}

/// <summary>Upstream <c>Copyrights(Vec&lt;Copyright&gt;)</c>.</summary>
public sealed class Copyrights : List<Copyright>
{
    /// <summary>Creates an empty list.</summary>
    public Copyrights()
    {
    }

    /// <summary>Wraps the given copyrights.</summary>
    public Copyrights(IEnumerable<Copyright> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_from_repeated!(CopyrightMessage, Copyrights)</c>.</summary>
    public Copyrights(IEnumerable<ProtoCopyright> messages) : base(messages.Select(Copyright.From))
    {
    }
}
