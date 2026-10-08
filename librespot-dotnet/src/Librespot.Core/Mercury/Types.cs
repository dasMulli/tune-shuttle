// Port of core/src/mercury/types.rs.
// TODO(port: core-session): MercuryRequest.Encode (seq len u16 | seq | flags 0x01 | part count u16 | header len u16 |
// Header proto | parts), MercuryError -> Error kind mapping is done.
namespace Librespot.Core.Mercury;

public enum MercuryMethod
{
    Get,
    Sub,
    Unsub,
    Send,
}

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

public sealed record MercuryRequest(MercuryMethod Method, string Uri, string? ContentType, IReadOnlyList<byte[]> Payload)
{
    /// <summary>Upstream <c>encode(seq)</c>: serialises the request for the given sequence bytes.</summary>
    public byte[] Encode(ReadOnlySpan<byte> seq) => throw new NotImplementedException("TODO(port: core-session)");
}

public sealed record MercuryResponse(string Uri, int StatusCode, IReadOnlyList<byte[]> Payload);

/// <summary>Upstream <c>MercuryError</c>.</summary>
public sealed class MercuryException : LibrespotException
{
    public enum Reason
    {
        /// <summary>Callback receiver was disconnected (kind Aborted).</summary>
        Channel,
        /// <summary>Error handling packet type (kind Unimplemented).</summary>
        Command,
        /// <summary>Error handling Mercury response (kind Unavailable).</summary>
        Response,
    }

    public Reason Cause { get; }
    public MercuryResponse? Response { get; }

    private MercuryException(Reason cause, ErrorKind kind, string detail, MercuryResponse? response = null) : base(kind, detail)
    {
        Cause = cause;
        Response = response;
    }

    public static MercuryException Channel() => new(Reason.Channel, ErrorKind.Aborted, "callback receiver was disconnected");
    public static MercuryException Command(PacketType cmd) => new(Reason.Command, ErrorKind.Unimplemented, $"error handling packet type: {cmd}");
    public static MercuryException ResponseError(MercuryResponse response) => new(Reason.Response, ErrorKind.Unavailable, $"error handling Mercury response: {response}", response);
}
