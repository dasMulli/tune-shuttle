// Port of core/src/audio_key.rs.
using System.Buffers.Binary;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core;

/// <summary>A 16-byte AES-128 key for one audio file (upstream <c>AudioKey(pub [u8; 16])</c>).</summary>
public readonly struct AudioKey : IEquatable<AudioKey>
{
    /// <summary>Key length in bytes.</summary>
    public const int Length = 16;

    private readonly byte[] _key;

    /// <summary>Copies a 16-byte key.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 16 bytes long.</exception>
    public AudioKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != Length)
        {
            throw new ArgumentException($"audio key must be {Length} bytes", nameof(key));
        }

        _key = key.ToArray();
    }

    /// <summary>The raw key bytes (all zero for <c>default</c>).</summary>
    public ReadOnlySpan<byte> Key => _key ?? new byte[Length];

    /// <inheritdoc />
    public bool Equals(AudioKey other) => Key.SequenceEqual(other.Key);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AudioKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(Key);
        return h.ToHashCode();
    }

    /// <summary>Value equality.</summary>
    public static bool operator ==(AudioKey left, AudioKey right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(AudioKey left, AudioKey right) => !left.Equals(right);
}

/// <summary>Upstream <c>AudioKeyError</c>.</summary>
public sealed class AudioKeyException : LibrespotException
{
    /// <summary>The upstream variants.</summary>
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

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    private AudioKeyException(Reason cause, ErrorKind kind, string detail) : base(kind, detail)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>AudioKeyError::AesKey</c>.</summary>
    public static AudioKeyException AesKey() => new(Reason.AesKey, ErrorKind.Unavailable, "audio key error");

    /// <summary>Upstream <c>AudioKeyError::Channel</c>.</summary>
    public static AudioKeyException Channel() => new(Reason.Channel, ErrorKind.Aborted, "other end of channel disconnected");

    /// <summary>Upstream <c>AudioKeyError::Packet(cmd)</c>.</summary>
    public static AudioKeyException Packet(byte cmd) => new(Reason.Packet, ErrorKind.Unimplemented, $"unexpected packet type {cmd}");

    /// <summary>Upstream <c>AudioKeyError::Sequence(seq)</c>.</summary>
    public static AudioKeyException Sequence(uint seq) => new(Reason.Sequence, ErrorKind.Aborted, $"sequence {seq} not pending");

    /// <summary>Upstream <c>AudioKeyError::Timeout</c>.</summary>
    public static AudioKeyException Timeout() => new(Reason.Timeout, ErrorKind.Aborted, "audio key response timeout");
}

/// <summary>Requests per-file AES keys over the AP connection (upstream <c>AudioKeyManager</c>).</summary>
public sealed class AudioKeyManager : SessionComponent
{
    /// <summary>Upstream <c>KEY_RESPONSE_TIMEOUT</c>.</summary>
    public static readonly TimeSpan KeyResponseTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly ILogger Log = LibrespotLog.CreateLogger<AudioKeyManager>();

    // upstream AudioKeyManagerInner
    private SeqGenerator<uint> _sequence = new(0);
    private readonly Dictionary<uint, TaskCompletionSource<AudioKey>> _pending = new();

    internal AudioKeyManager(Session session) : base(session)
    {
    }

    /// <summary>Called by the session dispatch task for AesKey / AesKeyError packets (upstream <c>dispatch</c>).</summary>
    /// <exception cref="AudioKeyException">The sequence is not pending, or the packet type is unexpected.</exception>
    internal void Dispatch(PacketType cmd, ReadOnlyMemory<byte> data)
    {
        var seq = BinaryPrimitives.ReadUInt32BigEndian(data.Span);
        var rest = data.Span[4..];

        TaskCompletionSource<AudioKey>? sender;
        lock (Lock)
        {
            _pending.Remove(seq, out sender);
        }

        if (sender is null)
        {
            throw AudioKeyException.Sequence(seq);
        }

        switch (cmd)
        {
            case PacketType.AesKey:
            {
                var key = new AudioKey(rest);
                if (!sender.TrySetResult(key))
                {
                    throw AudioKeyException.Channel();
                }

                break;
            }

            case PacketType.AesKeyError:
                Log.LogError("error audio key {B0:x} {B1:x}", rest[0], rest[1]);
                if (!sender.TrySetException(AudioKeyException.AesKey()))
                {
                    throw AudioKeyException.Channel();
                }

                break;

            default:
                Log.LogTrace("Did not expect {Cmd} AES key packet with data {Data}", cmd, Convert.ToHexString(rest));

                // Upstream drops the oneshot sender here, so the waiting request fails with a RecvError.
                sender.TrySetException(LibrespotException.From(new System.Threading.Channels.ChannelClosedException()));
                throw AudioKeyException.Packet((byte)cmd);
        }
    }

    /// <summary>Upstream <c>request(track, file)</c>: asks the AP for the decryption key of an audio file.</summary>
    /// <exception cref="AudioKeyException">The AP answered with an error, or no answer arrived within 1.5 s.</exception>
    /// <exception cref="SessionException">The session is not connected.</exception>
    public async Task<AudioKey> RequestAsync(SpotifyId track, FileId file, CancellationToken cancellationToken = default)
    {
        var tx = new TaskCompletionSource<AudioKey>(TaskCreationOptions.RunContinuationsAsynchronously);

        uint seq;
        lock (Lock)
        {
            seq = _sequence.Get();
            _pending[seq] = tx;
        }

        SendKeyRequest(seq, track, file);
        try
        {
            return await tx.Task.WaitAsync(KeyResponseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.LogError("Audio key response timeout");
            throw AudioKeyException.Timeout();
        }
        finally
        {
            // Upstream drops the oneshot receiver on timeout or cancellation, so a late answer fails in dispatch.
            // Removing the pending entry does the same: Dispatch then reports AudioKeyException.Sequence, which the
            // dispatch task logs at debug. (After a normal answer Dispatch has already removed it.)
            lock (Lock)
            {
                _pending.Remove(seq);
            }
        }
    }

    private void SendKeyRequest(uint seq, SpotifyId track, FileId file) =>
        Session.SendPacket(PacketType.RequestKey, BuildKeyRequest(seq, track, file));

    /// <summary>The <c>RequestKey</c> payload: <c>file id (20) | track gid (16) | seq (u32 BE) | 0x0000 (u16 BE)</c>.</summary>
    internal static byte[] BuildKeyRequest(uint seq, SpotifyId track, FileId file)
    {
        var data = new byte[FileId.RawLength + SpotifyId.Size + 4 + 2];
        file.Raw.CopyTo(data);
        track.ToRaw().CopyTo(data.AsSpan(FileId.RawLength));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(FileId.RawLength + SpotifyId.Size), seq);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(FileId.RawLength + SpotifyId.Size + 4), 0x0000);
        return data;
    }
}
