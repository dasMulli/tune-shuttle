// Port of core/src/audio_key.rs.
// TODO(port: core-session): request packet (RequestKey: file_id | track gid | seq | 0x0000), dispatch of AesKey /
// AesKeyError, pending map of TaskCompletionSource<AudioKey> keyed by seq, 1.5 s timeout.
namespace Librespot.Core;

/// <summary>A 16-byte AES-128 key for one audio file.</summary>
public readonly struct AudioKey : IEquatable<AudioKey>
{
    public const int Length = 16;

    private readonly byte[] _key;

    public AudioKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != Length)
        {
            throw new ArgumentException($"audio key must be {Length} bytes", nameof(key));
        }

        _key = key.ToArray();
    }

    public ReadOnlySpan<byte> Key => _key ?? new byte[Length];

    public bool Equals(AudioKey other) => Key.SequenceEqual(other.Key);
    public override bool Equals(object? obj) => obj is AudioKey other && Equals(other);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(Key);
        return h.ToHashCode();
    }

    public static bool operator ==(AudioKey left, AudioKey right) => left.Equals(right);
    public static bool operator !=(AudioKey left, AudioKey right) => !left.Equals(right);
}

/// <summary>Upstream <c>AudioKeyError</c>.</summary>
public sealed class AudioKeyException : LibrespotException
{
    public enum Reason
    {
        /// <summary>Audio key error (kind Unavailable).</summary>
        AesKey,
        /// <summary>Other end of channel disconnected (kind Aborted).</summary>
        Channel,
        /// <summary>Unexpected packet type (kind Unimplemented).</summary>
        Packet,
        /// <summary>Sequence not pending (kind Aborted).</summary>
        Sequence,
        /// <summary>Audio key response timeout (kind Aborted).</summary>
        Timeout,
    }

    public Reason Cause { get; }

    private AudioKeyException(Reason cause, ErrorKind kind, string detail) : base(kind, detail)
    {
        Cause = cause;
    }

    public static AudioKeyException AesKey() => new(Reason.AesKey, ErrorKind.Unavailable, "audio key error");
    public static AudioKeyException Channel() => new(Reason.Channel, ErrorKind.Aborted, "other end of channel disconnected");
    public static AudioKeyException Packet(byte cmd) => new(Reason.Packet, ErrorKind.Unimplemented, $"unexpected packet type {cmd}");
    public static AudioKeyException Sequence(uint seq) => new(Reason.Sequence, ErrorKind.Aborted, $"sequence {seq} not pending");
    public static AudioKeyException Timeout() => new(Reason.Timeout, ErrorKind.Aborted, "audio key response timeout");
}

/// <summary>Requests per-file AES keys over the AP connection.</summary>
public sealed class AudioKeyManager : SessionComponent
{
    internal AudioKeyManager(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>request(track, file)</c>.</summary>
    public Task<AudioKey> RequestAsync(SpotifyId track, FileId file, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-session)");

    /// <summary>Called by the session dispatch task for AesKey / AesKeyError packets.</summary>
    internal void Dispatch(PacketType cmd, ReadOnlyMemory<byte> data) =>
        throw new NotImplementedException("TODO(port: core-session)");
}
