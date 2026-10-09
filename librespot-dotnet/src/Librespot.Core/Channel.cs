// Port of core/src/channel.rs.
//
// NOTE: dead code upstream in 0.8.0. ChannelManager is only constructed by Session (session.rs:109) and fed by the
// dispatch task (StreamChunkRes / ChannelError); no other crate calls allocate() - audio streaming goes through
// the CDN (spclient.stream_from_cdn). It is ported minimally and faithfully; audio-fetch does NOT use it.
//
// Upstream's `Channel` is a futures Stream over the raw (cmd, bytes) packets, and `split()` wraps it in a BiLock
// shared by a `ChannelHeaders` and a `ChannelData` stream. The port keeps that shape: SpotifyChannel reads the
// raw packet channel and runs the header/data state machine in NextAsync; Split() returns two views that share the
// channel behind a SemaphoreSlim (the BiLock).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core;

/// <summary>Upstream <c>ChannelError</c> ("channel error"), kind Aborted.</summary>
public sealed class ChannelException() : LibrespotException(ErrorKind.Aborted, "channel error");

/// <summary>Upstream <c>ChannelEvent</c>: a header (id + bytes) or a data chunk.</summary>
public abstract record ChannelEvent
{
    private ChannelEvent()
    {
    }

    /// <summary>A header block.</summary>
    /// <param name="Id">Header id.</param>
    /// <param name="Payload">Header bytes.</param>
    public sealed record Header(byte Id, ReadOnlyMemory<byte> Payload) : ChannelEvent;

    /// <summary>A data chunk.</summary>
    /// <param name="Bytes">The chunk.</param>
    public sealed record Data(ReadOnlyMemory<byte> Bytes) : ChannelEvent;
}

/// <summary>
/// A multiplexed stream of chunks from the AP (upstream <c>Channel</c>, a <c>Stream</c> of
/// <c>Result&lt;ChannelEvent, ChannelError&gt;</c>): header blocks until a zero-length header, then data chunks until
/// an empty chunk.
/// </summary>
/// <remarks>Read by one consumer at a time (or through the two views returned by <see cref="Split"/>).</remarks>
public sealed class SpotifyChannel
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<SpotifyChannel>();

    private readonly ChannelReader<(byte Cmd, ReadOnlyMemory<byte> Data)> _receiver;

    // upstream ChannelState: Header(Bytes) | Data | Closed
    private State _state = State.Header;
    private ReadOnlyMemory<byte> _headerData = ReadOnlyMemory<byte>.Empty;

    private enum State
    {
        Header,
        Data,
        Closed,
    }

    internal SpotifyChannel(ChannelReader<(byte Cmd, ReadOnlyMemory<byte> Data)> receiver)
    {
        _receiver = receiver;
    }

    /// <summary>
    /// Upstream <c>poll_next</c>: the next event, or null when the channel has ended.
    /// </summary>
    /// <exception cref="ChannelException">The AP reported a channel error, or the channel was shut down.</exception>
    public async ValueTask<ChannelEvent?> NextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            switch (_state)
            {
                case State.Closed:
                    Log.LogError("Polling already terminated channel");
                    return null;

                case State.Header:
                {
                    var data = _headerData;
                    if (data.IsEmpty)
                    {
                        data = await RecvPacketAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var length = BinaryPrimitives.ReadUInt16BigEndian(data.Span);
                    data = data[2..];
                    if (length == 0)
                    {
                        _state = State.Data;
                        _headerData = ReadOnlyMemory<byte>.Empty;
                    }
                    else
                    {
                        var headerId = data.Span[0];
                        var headerData = data.Slice(1, length - 1).ToArray();

                        _headerData = data[length..];

                        return new ChannelEvent.Header(headerId, headerData);
                    }

                    break;
                }

                case State.Data:
                {
                    var data = await RecvPacketAsync(cancellationToken).ConfigureAwait(false);
                    if (data.IsEmpty)
                    {
                        _state = State.Closed;
                        return null;
                    }

                    return new ChannelEvent.Data(data);
                }
            }
        }
    }

    /// <summary>Enumerates <see cref="NextAsync"/> until the channel ends.</summary>
    public async IAsyncEnumerable<ChannelEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await NextAsync(cancellationToken).ConfigureAwait(false) is { } ev)
        {
            yield return ev;
        }
    }

    /// <summary>Upstream <c>split()</c>: a header view and a data view sharing this channel.</summary>
    public (ChannelHeaders Headers, ChannelData Data) Split()
    {
        var biLock = new SemaphoreSlim(1, 1);
        return (new ChannelHeaders(this, biLock), new ChannelData(this, biLock));
    }

    private async ValueTask<ReadOnlyMemory<byte>> RecvPacketAsync(CancellationToken cancellationToken)
    {
        (byte Cmd, ReadOnlyMemory<byte> Data) packet;
        try
        {
            packet = await _receiver.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ChannelException();
        }

        if (PacketTypeExtensions.FromByte(packet.Cmd) == PacketType.ChannelError)
        {
            var code = BinaryPrimitives.ReadUInt16BigEndian(packet.Data.Span);
            Log.LogError("channel error: {Length} {Code}", packet.Data.Length, code);

            _state = State.Closed;

            throw new ChannelException();
        }

        return packet.Data;
    }
}

/// <summary>Upstream <c>ChannelHeaders</c>: the header events of a split <see cref="SpotifyChannel"/>.</summary>
public sealed class ChannelHeaders
{
    private readonly SpotifyChannel _channel;
    private readonly SemaphoreSlim _lock;

    internal ChannelHeaders(SpotifyChannel channel, SemaphoreSlim biLock)
    {
        _channel = channel;
        _lock = biLock;
    }

    /// <summary>The next header, or null once the headers are over (the event that ended them is consumed).</summary>
    /// <exception cref="ChannelException">The AP reported a channel error.</exception>
    public async ValueTask<(byte Id, byte[] Data)?> NextAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _channel.NextAsync(cancellationToken).ConfigureAwait(false) switch
            {
                ChannelEvent.Header(var id, var data) => (id, data.ToArray()),
                _ => null,
            };
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Upstream <c>ChannelData</c>: the data chunks of a split <see cref="SpotifyChannel"/> (headers are skipped).</summary>
public sealed class ChannelData
{
    private readonly SpotifyChannel _channel;
    private readonly SemaphoreSlim _lock;

    internal ChannelData(SpotifyChannel channel, SemaphoreSlim biLock)
    {
        _channel = channel;
        _lock = biLock;
    }

    /// <summary>The next data chunk, or null when the channel has ended.</summary>
    /// <exception cref="ChannelException">The AP reported a channel error.</exception>
    public async ValueTask<ReadOnlyMemory<byte>?> NextAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                switch (await _channel.NextAsync(cancellationToken).ConfigureAwait(false))
                {
                    case ChannelEvent.Header:
                        break;
                    case ChannelEvent.Data(var data):
                        return data;
                    default:
                        return null;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Allocates channel ids and routes StreamChunkRes / ChannelError packets (upstream <c>ChannelManager</c>).</summary>
public sealed class ChannelManager : SessionComponent
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    // upstream ChannelManagerInner
    private SeqGenerator<ushort> _sequence = new(0);
    private readonly Dictionary<ushort, ChannelWriter<(byte Cmd, ReadOnlyMemory<byte> Data)>> _channels = new();
    private long _downloadRateEstimate;
    private long? _downloadMeasurementStart; // Stopwatch timestamp
    private long _downloadMeasurementBytes;
    private bool _invalid;

    internal ChannelManager(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>allocate()</c>: a fresh channel id and its event stream.</summary>
    public (ushort Id, SpotifyChannel Channel) Allocate()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<(byte Cmd, ReadOnlyMemory<byte> Data)>(
            new UnboundedChannelOptions { SingleReader = true });

        ushort seq;
        bool registered;
        lock (Lock)
        {
            seq = _sequence.Get();
            registered = !_invalid;
            if (registered)
            {
                _channels[seq] = channel.Writer;
            }
        }

        if (!registered)
        {
            // Upstream drops the sender, so the channel ends with an error on first read.
            channel.Writer.TryComplete();
        }

        return (seq, new SpotifyChannel(channel.Reader));
    }

    /// <summary>Upstream <c>dispatch(cmd, data)</c>: routes a StreamChunkRes / ChannelError packet by channel id.</summary>
    /// <exception cref="ChannelException">The channel's receiver is gone.</exception>
    internal void Dispatch(PacketType cmd, ReadOnlyMemory<byte> data)
    {
        var id = BinaryPrimitives.ReadUInt16BigEndian(data.Span);
        data = data[2..];

        lock (Lock)
        {
            var currentTime = Stopwatch.GetTimestamp();
            if (_downloadMeasurementStart is { } start)
            {
                var elapsed = Stopwatch.GetElapsedTime(start, currentTime);
                if (elapsed > OneSecond)
                {
                    _downloadRateEstimate = (long)OneSecond.TotalMilliseconds * _downloadMeasurementBytes
                        / (long)elapsed.TotalMilliseconds;
                    _downloadMeasurementStart = currentTime;
                    _downloadMeasurementBytes = 0;
                }
            }
            else
            {
                _downloadMeasurementStart = currentTime;
            }

            _downloadMeasurementBytes += data.Length;

            if (_channels.TryGetValue(id, out var sender) && !sender.TryWrite(((byte)cmd, data)))
            {
                throw new ChannelException();
            }
        }
    }

    /// <summary>Upstream <c>get_download_rate_estimate()</c>: bytes per second observed over the last interval.</summary>
    public long GetDownloadRateEstimate()
    {
        lock (Lock)
        {
            return _downloadRateEstimate;
        }
    }

    /// <summary>Closes all channels (called from <see cref="Session.Shutdown"/>).</summary>
    public void Shutdown()
    {
        lock (Lock)
        {
            _invalid = true;

            // destroy the sending halves of the channels to signal everyone who is waiting for something.
            foreach (var sender in _channels.Values)
            {
                sender.TryComplete();
            }

            _channels.Clear();
        }
    }
}
