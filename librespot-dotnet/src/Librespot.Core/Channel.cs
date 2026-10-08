// Port of core/src/channel.rs.
// NOTE: dead code upstream in 0.8.0. ChannelManager is only constructed by Session (session.rs:109) and fed by the
// dispatch task (StreamChunkRes / ChannelError); no other crate calls allocate() - audio streaming goes through
// the CDN (spclient.stream_from_cdn). Port it minimally (allocate, Dispatch, Shutdown, GetDownloadRateEstimate)
// and do not expect consumers; audio-fetch does NOT use it.
// TODO(port: core-session): channel allocation, StreamChunkRes / ChannelError dispatch, header/data split
// (upstream uses futures BiLock; use a Channel<ChannelEvent> + two reader views), download-rate estimate.
namespace Librespot.Core;

/// <summary>Upstream <c>ChannelError</c> ("channel error"), kind Aborted.</summary>
public sealed class ChannelException() : LibrespotException(ErrorKind.Aborted, "channel error");

/// <summary>Events delivered on a channel: a header (id + bytes) or a data chunk.</summary>
public abstract record ChannelEvent
{
    private ChannelEvent()
    {
    }

    public sealed record Header(byte Id, ReadOnlyMemory<byte> Payload) : ChannelEvent;

    public sealed record Data(ReadOnlyMemory<byte> Bytes) : ChannelEvent;
}

/// <summary>A multiplexed stream of chunks from the AP (upstream <c>Channel</c>, a <c>Stream&lt;ChannelEvent&gt;</c>).</summary>
public sealed class SpotifyChannel
{
    internal SpotifyChannel(System.Threading.Channels.ChannelReader<ChannelEvent> reader)
    {
        Events = reader;
    }

    public System.Threading.Channels.ChannelReader<ChannelEvent> Events { get; }
}

/// <summary>Allocates channel ids and routes StreamChunkRes / ChannelError packets.</summary>
public sealed class ChannelManager : SessionComponent
{
    internal ChannelManager(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>allocate()</c>: a fresh channel id and its event stream.</summary>
    public (ushort Id, SpotifyChannel Channel) Allocate() =>
        throw new NotImplementedException("TODO(port: core-session)");

    /// <summary>Bytes per second observed over the last interval.</summary>
    public long GetDownloadRateEstimate() =>
        throw new NotImplementedException("TODO(port: core-session)");

    internal void Dispatch(PacketType cmd, ReadOnlyMemory<byte> data) =>
        throw new NotImplementedException("TODO(port: core-session)");

    /// <summary>Closes all channels (called from <see cref="Session.Shutdown"/>).</summary>
    public void Shutdown()
    {
        // TODO(port: core-session): complete all channel writers. No-op until then so Session.Shutdown works.
    }
}
