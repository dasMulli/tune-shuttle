// Port of metadata/src/lib.rs (the `Metadata` trait) and metadata/src/error.rs.
using Google.Protobuf;
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Metadata;

/// <summary>
/// Upstream <c>MetadataError</c>. Upstream never converts it with a <c>From</c> impl: every use site wraps it as
/// <c>Error::unavailable(MetadataError::..)</c>, so <see cref="LibrespotException.Kind"/> is always
/// <see cref="ErrorKind.Unavailable"/>.
/// </summary>
public sealed class MetadataException : LibrespotException
{
    /// <summary>The upstream <c>MetadataError</c> variants.</summary>
    public enum Reason
    {
        /// <summary>Empty response.</summary>
        Empty,
        /// <summary>Audio item is non-playable when it should be.</summary>
        NonPlayable,
        /// <summary>Audio item duration can not be the given value.</summary>
        InvalidDuration,
        /// <summary>Track is marked as explicit, which client setting forbids.</summary>
        ExplicitContentFiltered,
    }

    /// <summary>Which upstream variant this error is.</summary>
    public Reason Cause { get; }

    private MetadataException(Reason cause, string detail) : base(ErrorKind.Unavailable, detail)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>MetadataError::Empty</c>.</summary>
    public static MetadataException Empty() => new(Reason.Empty, "empty response");

    /// <summary>Upstream <c>MetadataError::NonPlayable</c>.</summary>
    public static MetadataException NonPlayable() => new(Reason.NonPlayable, "audio item is non-playable when it should be");

    /// <summary>Upstream <c>MetadataError::InvalidDuration(i32)</c>.</summary>
    public static MetadataException InvalidDuration(int duration) => new(Reason.InvalidDuration, $"audio item duration can not be: {duration}");

    /// <summary>Upstream <c>MetadataError::ExplicitContentFiltered</c>.</summary>
    public static MetadataException ExplicitContentFiltered() => new(Reason.ExplicitContentFiltered, "track is marked as explicit, which client setting forbids");
}

/// <summary>
/// Upstream <c>#[async_trait] trait Metadata</c>. Implemented with static abstract members: <c>Track.GetAsync(session, uri)</c>
/// requests the protobuf bytes, parses <typeparamref name="TMessage"/> and converts it.
/// </summary>
/// <remarks>
/// C# cannot call a static virtual interface member through the implementing type, so every implementation also
/// declares its own <c>public static GetAsync</c> (forwarding to <see cref="MetadataRequest.GetAsync{TSelf, TMessage}"/>);
/// that declaration implements this member.
/// </remarks>
public interface IMetadata<TSelf, TMessage>
    where TSelf : IMetadata<TSelf, TMessage>
    where TMessage : IMessage<TMessage>, new()
{
    /// <summary>Request the raw protobuf for the id.</summary>
    static abstract Task<byte[]> RequestAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default);

    /// <summary>Convert the parsed message.</summary>
    static abstract TSelf Parse(TMessage message, SpotifyUri id);

    /// <summary>Request and parse a metadata struct (upstream default <c>get</c>).</summary>
    static virtual Task<TSelf> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default) =>
        MetadataRequest.GetAsync<TSelf, TMessage>(session, id, cancellationToken);
}

/// <summary>The body of the upstream default method <c>Metadata::get</c>, shared by every implementation.</summary>
public static class MetadataRequest
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger(typeof(MetadataRequest).FullName!);

    /// <summary>Requests the protobuf for <paramref name="id"/>, parses it and converts it (upstream <c>Metadata::get</c>).</summary>
    public static async Task<TSelf> GetAsync<TSelf, TMessage>(Session session, SpotifyUri id, CancellationToken cancellationToken = default)
        where TSelf : IMetadata<TSelf, TMessage>
        where TMessage : IMessage<TMessage>, new()
    {
        var response = await TSelf.RequestAsync(session, id, cancellationToken).ConfigureAwait(false);
        var message = ParseMessage<TMessage>(response);
        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("Received metadata: {Message}", message.ToString());
        }

        return TSelf.Parse(message, id);
    }

    /// <summary>Upstream <c>Message::parse_from_bytes(..)?</c>: a protobuf error becomes a <see cref="LibrespotException"/>.</summary>
    internal static TMessage ParseMessage<TMessage>(byte[] data)
        where TMessage : IMessage<TMessage>, new()
    {
        try
        {
            return ParserCache<TMessage>.Parser.ParseFrom(data);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw LibrespotException.From(e);
        }
    }

    private static class ParserCache<TMessage>
        where TMessage : IMessage<TMessage>, new()
    {
        public static readonly MessageParser<TMessage> Parser = new(() => new TMessage());
    }
}
