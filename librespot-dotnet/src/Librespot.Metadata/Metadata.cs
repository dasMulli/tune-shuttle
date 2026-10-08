// Port of metadata/src/lib.rs (the `Metadata` trait) and metadata/src/error.rs.
using Google.Protobuf;
using Librespot.Core;

namespace Librespot.Metadata;

/// <summary>Upstream <c>MetadataError</c>.</summary>
public sealed class MetadataException : LibrespotException
{
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

    public Reason Cause { get; }

    private MetadataException(Reason cause, ErrorKind kind, string detail) : base(kind, detail)
    {
        Cause = cause;
    }

    public static MetadataException Empty() => new(Reason.Empty, ErrorKind.NotFound, "empty response");
    public static MetadataException NonPlayable() => new(Reason.NonPlayable, ErrorKind.Unavailable, "audio item is non-playable when it should be");
    public static MetadataException InvalidDuration(int duration) => new(Reason.InvalidDuration, ErrorKind.FailedPrecondition, $"audio item duration can not be: {duration}");
    public static MetadataException ExplicitContentFiltered() => new(Reason.ExplicitContentFiltered, ErrorKind.PermissionDenied, "track is marked as explicit, which client setting forbids");
}

/// <summary>
/// Upstream <c>#[async_trait] trait Metadata</c>. Implemented with static abstract members: <c>Track.GetAsync(session, uri)</c>
/// requests the protobuf bytes, parses <typeparamref name="TMessage"/> and converts it.
/// </summary>
public interface IMetadata<TSelf, TMessage>
    where TSelf : IMetadata<TSelf, TMessage>
    where TMessage : IMessage<TMessage>, new()
{
    /// <summary>Request the raw protobuf for the id.</summary>
    static abstract Task<byte[]> RequestAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default);

    /// <summary>Convert the parsed message.</summary>
    static abstract TSelf Parse(TMessage message, SpotifyUri id);

    /// <summary>Request and parse a metadata struct (upstream default <c>get</c>).</summary>
    static virtual async Task<TSelf> GetAsync(Session session, SpotifyUri id, CancellationToken cancellationToken = default)
    {
        var response = await TSelf.RequestAsync(session, id, cancellationToken).ConfigureAwait(false);
        var parser = new MessageParser<TMessage>(() => new TMessage());
        var message = parser.ParseFrom(response);
        return TSelf.Parse(message, id);
    }
}
