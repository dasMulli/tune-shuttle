using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Sender.Rtp;

/// <summary>
/// The process-wide NTP timing responder (SPEC-audio.md 2.2): one UDP socket, announced as <c>timingPort</c> in every
/// NTP-timed session SETUP. Each 0xD2 request is answered at once from the same socket to the request's source
/// endpoint with a 0xD3 reply: reference = the request's transmit timestamp, receive = our clock right after the
/// datagram arrived, transmit = our clock right before the reply leaves. All times come from the one
/// <see cref="IMasterClock"/> that also feeds the sync packets (SPEC-audio.md 2.1).
/// </summary>
/// <remarks>
/// shairport-sync's AirPlay 1 requester sends the first three requests 300 ms apart, then one every 3 s, and ignores a
/// reply whose round trip exceeds 200 ms (<c>[sps] rtp.c:595-664, 779-885</c>), so the receive loop never does
/// anything slow between receive and reply. Receivers are not authenticated: any source gets an answer, as with pyatv
/// (<c>protocols/__init__.py:120-140</c>) and owntone. Socket lifetime and receive errors: see <see cref="UdpReceiveLoop"/>.
/// </remarks>
public sealed partial class TimingResponder : ITimingResponder
{
    private readonly IMasterClock _clock;
    private readonly ILogger _logger;
    private readonly UdpReceiveLoop _loop;
    // One reply buffer: the loop hands over one datagram at a time.
    private readonly byte[] _reply = new byte[TimingPacket.Size];
    private long _repliesSent;
    private long _ignored;

    /// <summary>Creates the responder; nothing is bound until <see cref="StartAsync"/>.</summary>
    /// <param name="clock">The master clock shared with the sync packets.</param>
    /// <param name="logger">Logger; null for none.</param>
    /// <param name="bindAddress">Local address; null binds the wildcard (dual-mode IPv6 where available).</param>
    /// <param name="port">Local port; 0 lets the OS choose.</param>
    public TimingResponder(IMasterClock clock, ILogger<TimingResponder>? logger = null, IPAddress? bindAddress = null, int port = 0)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _loop = new UdpReceiveLoop("timing port", _logger, bindAddress, port, HandleAsync);
    }

    /// <inheritdoc />
    public int Port => _loop.Port;

    /// <inheritdoc />
    public long RepliesSent => Interlocked.Read(ref _repliesSent);

    /// <summary>Datagrams that were not timing requests (diagnostics).</summary>
    public long PacketsIgnored => Interlocked.Read(ref _ignored);

    /// <summary>
    /// Binds the socket and starts the receive loop; idempotent. The token only covers starting: the loop runs until
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _loop.Start(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _loop.DisposeAsync();

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask HandleAsync(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop)
    {
        // Take the receive time first: the receiver subtracts our processing time (transmit - receive).
        var receiveNtp = _clock.NtpNow;
        if (!TimingPacket.TryParse(datagram.Span, out var request) || !request.IsRequest)
        {
            Interlocked.Increment(ref _ignored);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogIgnored(datagram.Length, datagram.Length > 1 ? datagram.Span[1] : (byte)0, UdpSockets.ToEndPoint(source));
            }

            return;
        }

        TimingPacket.Reply(request, receiveNtp, _clock.NtpNow).Write(_reply);
        await socket.SendToAsync(_reply, SocketFlags.None, source, stop).ConfigureAwait(false);
        Interlocked.Increment(ref _repliesSent);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogReplied(UdpSockets.ToEndPoint(source), request.Sequence, request.Transmit, receiveNtp);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Timing reply to {Source}: seq {Sequence}, reference {Reference:X16}, receive {Receive:X16}")]
    private partial void LogReplied(IPEndPoint source, ushort sequence, ulong reference, ulong receive);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Ignored {Length}-byte datagram of type 0x{Type:X2} from {Source} on the timing port")]
    private partial void LogIgnored(int length, byte type, IPEndPoint source);
}
