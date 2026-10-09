using System.Net;
using System.Net.Sockets;
using System.Text;
using Airplay2.Sender.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Sender.Tests.Session;

/// <summary><see cref="EventChannel"/> over a plaintext loopback socket: the answer bytes and who closed the channel.</summary>
public sealed class EventChannelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("RTSP/1.0", "CSeq: 7\r\nServer: AirTunes/366.0\r\n", "RTSP/1.0 200 OK\r\nContent-Length: 0\r\nAudio-Latency: 0\r\nServer: AirTunes/366.0\r\nCSeq: 7\r\n\r\n")]
    [InlineData("HTTP/1.1", "CSeq: 12\r\n", "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nAudio-Latency: 0\r\nCSeq: 12\r\n\r\n")]
    [InlineData("RTSP/1.0", "", "RTSP/1.0 200 OK\r\nContent-Length: 0\r\nAudio-Latency: 0\r\n\r\n")]
    public async Task Answer_echoes_protocol_cseq_and_server(string protocol, string headers, string expected)
    {
        // SPEC-control.md 4.3: protocol echoed, Content-Length 0, Audio-Latency 0, Server and CSeq echoed when present
        // [pyatv:pyatv/protocols/airplay/channels.py:60-97].
        await using var link = await LoopbackAsync();
        await link.Receiver.WriteAsync(Encoding.ASCII.GetBytes($"POST /command {protocol}\r\n{headers}Content-Length: 0\r\n\r\n"));
        var answer = await ReadHeadAsync(link.Receiver);
        Assert.Equal(expected, answer);
        var message = Assert.IsType<ReceiverEventMessage.Unknown>(await link.Channel.Messages.ReadAsync().AsTask().WaitAsync(Wait));
        Assert.Equal("POST", message.Request.Method);
        Assert.Equal(1, link.Channel.RequestsAnswered);
    }

    [Fact]
    public async Task Sender_dispose_completes_messages_without_a_closed_message()
    {
        await using var link = await LoopbackAsync();
        await link.Channel.DisposeAsync();
        Assert.Equal(EventChannel.ClosedBySenderReason, await link.Channel.Closed.WaitAsync(Wait));
        var messages = await DrainAsync(link.Channel);
        Assert.DoesNotContain(messages, m => m is ReceiverEventMessage.Closed);
    }

    [Fact]
    public async Task Receiver_close_publishes_a_closed_message()
    {
        await using var link = await LoopbackAsync();
        link.Socket.Shutdown(SocketShutdown.Both);
        link.Socket.Close();
        var reason = await link.Channel.Closed.WaitAsync(Wait);
        Assert.NotEqual(EventChannel.ClosedBySenderReason, reason);
        var messages = await DrainAsync(link.Channel);
        var closed = Assert.IsType<ReceiverEventMessage.Closed>(Assert.Single(messages));
        Assert.Equal(reason, closed.Reason);
    }

    private static async Task<List<ReceiverEventMessage>> DrainAsync(EventChannel channel)
    {
        using var cts = new CancellationTokenSource(Wait);
        var messages = new List<ReceiverEventMessage>();
        await foreach (var message in channel.Messages.ReadAllAsync(cts.Token))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Wait);
        var buffer = new List<byte>();
        var chunk = new byte[256];
        while (!Encoding.ASCII.GetString([.. buffer]).Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(chunk, cts.Token);
            Assert.NotEqual(0, read);
            buffer.AddRange(chunk.AsSpan(0, read));
        }

        return Encoding.ASCII.GetString([.. buffer]);
    }

    private static async Task<Link> LoopbackAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accept = listener.AcceptSocketAsync();
            var channel = await EventChannel.ConnectAsync((IPEndPoint)listener.LocalEndpoint, keys: null, new EventChannelOptions(), NullLogger.Instance, CancellationToken.None);
            var socket = await accept.WaitAsync(Wait);
            return new Link(channel, socket, new NetworkStream(socket, ownsSocket: false));
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed record Link(EventChannel Channel, Socket Socket, NetworkStream Receiver) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Channel.DisposeAsync();
            await Receiver.DisposeAsync();
            Socket.Dispose();
        }
    }
}
