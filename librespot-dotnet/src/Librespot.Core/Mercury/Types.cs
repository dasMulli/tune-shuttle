// Port of core/src/mercury/types.rs.
using System.Buffers.Binary;
using Google.Protobuf;
using ProtoHeader = Librespot.Protocol.Mercury.Header;

namespace Librespot.Core.Mercury;

/// <summary>Upstream <c>MercuryMethod</c>: the Hermes verb of a request.</summary>
public enum MercuryMethod
{
    /// <summary>GET request.</summary>
    Get,
    /// <summary>SUB: subscribe to a uri.</summary>
    Sub,
    /// <summary>UNSUB: cancel a subscription.</summary>
    Unsub,
    /// <summary>SEND request.</summary>
    Send,
}

/// <summary>Upstream <c>impl Display for MercuryMethod</c> and <c>MercuryMethod::command</c>.</summary>
public static class MercuryMethodExtensions
{
    /// <summary>Upstream <c>Display</c>: the wire method name.</summary>
    public static string ToWireString(this MercuryMethod m) => m switch
    {
        MercuryMethod.Get => "GET",
        MercuryMethod.Sub => "SUB",
        MercuryMethod.Unsub => "UNSUB",
        MercuryMethod.Send => "SEND",
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    /// <summary>Upstream <c>command()</c>: the packet type used to send this method.</summary>
    public static PacketType Command(this MercuryMethod m) => m switch
    {
        MercuryMethod.Get or MercuryMethod.Send => PacketType.MercuryReq,
        MercuryMethod.Sub => PacketType.MercurySub,
        MercuryMethod.Unsub => PacketType.MercuryUnsub,
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };
}

/// <summary>Upstream <c>MercuryRequest</c>.</summary>
/// <param name="Method">The Hermes verb.</param>
/// <param name="Uri">The <c>hm://</c> uri.</param>
/// <param name="ContentType">Optional content type header.</param>
/// <param name="Payload">The payload parts sent after the header part.</param>
public sealed record MercuryRequest(MercuryMethod Method, string Uri, string? ContentType, IReadOnlyList<byte[]> Payload)
{
    /// <summary>
    /// Upstream <c>encode(seq)</c>: serialises the request for the given sequence bytes as
    /// <c>seq len (u16 BE) | seq | flags (0x01 = FINAL) | part count (u16 BE) | header len (u16 BE) | Header proto |
    /// (part len (u16 BE) | part)*</c>.
    /// </summary>
    public byte[] Encode(ReadOnlySpan<byte> seq)
    {
        var header = new ProtoHeader
        {
            Uri = Uri,
            Method = Method.ToWireString(),
        };

        if (ContentType is { } contentType)
        {
            header.ContentType = contentType;
        }

        var headerSize = header.CalculateSize();

        var length = 2 + seq.Length + 1 + 2 + 2 + headerSize;
        foreach (var p in Payload)
        {
            length += 2 + p.Length;
        }

        var packet = new byte[length];
        var span = packet.AsSpan();

        // Upstream writes the lengths with `as u16`, i.e. truncating; keep that.
        BinaryPrimitives.WriteUInt16BigEndian(span, unchecked((ushort)seq.Length));
        span = span[2..];
        seq.CopyTo(span);
        span = span[seq.Length..];
        span[0] = 1; // Flags: FINAL
        span = span[1..];
        BinaryPrimitives.WriteUInt16BigEndian(span, unchecked((ushort)(1 + Payload.Count))); // Part count
        span = span[2..];

        BinaryPrimitives.WriteUInt16BigEndian(span, unchecked((ushort)headerSize));
        span = span[2..];
        header.WriteTo(span[..headerSize]);
        span = span[headerSize..];

        foreach (var p in Payload)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span, unchecked((ushort)p.Length));
            span = span[2..];
            p.CopyTo(span);
            span = span[p.Length..];
        }

        return packet;
    }
}

/// <summary>Upstream <c>MercuryResponse</c>.</summary>
/// <param name="Uri">The uri from the response header.</param>
/// <param name="StatusCode">The status code from the response header.</param>
/// <param name="Payload">The payload parts after the header part.</param>
public sealed record MercuryResponse(string Uri, int StatusCode, IReadOnlyList<byte[]> Payload)
{
    /// <summary>Renders like upstream's derived <c>Debug</c>.</summary>
    public override string ToString() =>
        $"MercuryResponse {{ uri: \"{Uri}\", status_code: {StatusCode}, payload: [{string.Join(", ", Payload.Select(p => "[" + string.Join(", ", p) + "]"))}] }}";
}

/// <summary>Upstream <c>MercuryError</c>.</summary>
public sealed class MercuryException : LibrespotException
{
    /// <summary>The upstream variants.</summary>
    public enum Reason
    {
        /// <summary>Callback receiver was disconnected (kind Aborted).</summary>
        Channel,
        /// <summary>Error handling packet type (kind Unimplemented).</summary>
        Command,
        /// <summary>Error handling Mercury response (kind Unavailable).</summary>
        Response,
    }

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    /// <summary>The response, for <see cref="Reason.Response"/>.</summary>
    public MercuryResponse? Response { get; }

    private MercuryException(Reason cause, ErrorKind kind, string detail, MercuryResponse? response = null) : base(kind, detail)
    {
        Cause = cause;
        Response = response;
    }

    /// <summary>Upstream <c>MercuryError::Channel</c>.</summary>
    public static MercuryException Channel() => new(Reason.Channel, ErrorKind.Aborted, "callback receiver was disconnected");

    /// <summary>Upstream <c>MercuryError::Command(cmd)</c>.</summary>
    public static MercuryException Command(PacketType cmd) => new(Reason.Command, ErrorKind.Unimplemented, $"error handling packet type: {cmd}");

    /// <summary>Upstream <c>MercuryError::Response(response)</c>.</summary>
    public static MercuryException ResponseError(MercuryResponse response) => new(Reason.Response, ErrorKind.Unavailable, $"error handling Mercury response: {response}", response);
}
