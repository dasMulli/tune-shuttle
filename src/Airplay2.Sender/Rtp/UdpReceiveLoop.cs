using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Rtp;

/// <summary>
/// Handles one datagram received by a <see cref="UdpReceiveLoop"/>. <paramref name="datagram"/> and
/// <paramref name="source"/> are the loop's buffers and are only valid until the returned task completes; the loop
/// calls the handler for one datagram at a time.
/// </summary>
internal delegate ValueTask UdpDatagramHandler(Socket socket, ReadOnlyMemory<byte> datagram, SocketAddress source, CancellationToken stop);

/// <summary>
/// The socket lifetime and receive loop shared by <see cref="TimingResponder"/> and <see cref="ControlPort"/>: binds on
/// <see cref="Start"/>, receives into one buffer and hands each datagram to the owner's handler, and stops (cancel, close
/// the socket, await the loop) on <see cref="DisposeAsync"/>.
/// </summary>
/// <remarks>
/// Error ladder (docs/DEVELOPMENT.md, "Code conventions": errors are never swallowed silently): a transient receive error (one datagram, or a stale
/// ICMP) is logged at Debug and the loop carries on; any other receive failure is logged at Error and the loop backs off
/// 300 ms before receiving again, as shairport-sync does (<c>[sps] rtp.c:1211-1214</c>). A socket error while the handler
/// replies is logged at Warning; any other handler failure (e.g. from a foreign <see cref="IPacketHistory"/>) is logged at
/// Error and that datagram is dropped. The loop only ends on <see cref="DisposeAsync"/>, so the port keeps answering.
/// </remarks>
internal sealed partial class UdpReceiveLoop : IAsyncDisposable
{
    private const int ReceiveBufferSize = 2048;
    private static readonly TimeSpan BackOff = TimeSpan.FromMilliseconds(300);

    private readonly string _name;
    private readonly ILogger _logger;
    private readonly IPAddress? _bindAddress;
    private readonly int _requestedPort;
    private readonly UdpDatagramHandler _handler;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private Task? _loop;
    private bool _disposed;

    /// <param name="name">What the owner is called in log messages ("control port", "timing port").</param>
    /// <param name="logger">The owner's logger.</param>
    /// <param name="bindAddress">Local address; null binds the wildcard (dual-mode IPv6 where available).</param>
    /// <param name="port">Local port; 0 lets the OS choose.</param>
    /// <param name="handler">Called for every received datagram.</param>
    public UdpReceiveLoop(string name, ILogger logger, IPAddress? bindAddress, int port, UdpDatagramHandler handler)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        _name = name;
        _logger = logger;
        _bindAddress = bindAddress;
        _requestedPort = port;
        _handler = handler;
    }

    /// <summary>The bound port; 0 before <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>The bound socket; null before <see cref="Start"/>.</summary>
    public Socket? Socket => Volatile.Read(ref _socket);

    /// <summary>True once <see cref="DisposeAsync"/> has begun.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed);

    /// <summary>Binds the socket and starts the receive loop; idempotent.</summary>
    /// <param name="owner">The object named in the <see cref="ObjectDisposedException"/> after disposal.</param>
    public void Start(object owner)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, owner);
            if (_socket is not null)
            {
                return;
            }

            var socket = UdpSockets.Bind(_bindAddress, _requestedPort);
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            Volatile.Write(ref _socket, socket);
            LogStarted(_logger, _name, Port);
            _loop = RunAsync(socket, _stop.Token);
        }
    }

    /// <summary>Stops the loop and closes the socket; idempotent. A faulted loop is logged, not rethrown.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            loop = _loop;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The loop handles its own errors, so this is a bug; disposal must still complete (await using).
                LogLoopFaulted(_logger, ex, _name);
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync(Socket socket, CancellationToken stop)
    {
        var buffer = new byte[ReceiveBufferSize];
        var address = UdpSockets.NewAddress(socket);
        var maxAddressSize = address.Size;
        while (!stop.IsCancellationRequested)
        {
            int received;
            try
            {
                address.Size = maxAddressSize;
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, address, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (UdpSockets.IsTransient(ex))
            {
                LogTransientError(_logger, _name, ex.SocketErrorCode);
                continue;
            }
            catch (Exception ex)
            {
                LogReceiveFailed(_logger, ex, _name);
                if (!await BackOffAsync(stop).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            try
            {
                await _handler(socket, buffer.AsMemory(0, received), address, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                LogReplyFailed(_logger, ex, _name, UdpSockets.ToEndPoint(address));
            }
            catch (Exception ex)
            {
                LogHandlerFailed(_logger, ex, _name, UdpSockets.ToEndPoint(address));
            }
        }

        LogStopped(_logger, _name, Port);
    }

    private static async Task<bool> BackOffAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(BackOff, stop).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "The {Name} is listening on UDP {Port}")]
    private static partial void LogStarted(ILogger logger, string name, int port);

    [LoggerMessage(EventId = 101, Level = LogLevel.Debug, Message = "Transient receive error on the {Name}: {Error}")]
    private static partial void LogTransientError(ILogger logger, string name, SocketError error);

    [LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "Receive on the {Name} failed; retrying in 300 ms")]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception, string name);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning, Message = "Reply from the {Name} to {Target} could not be sent")]
    private static partial void LogReplyFailed(ILogger logger, Exception exception, string name, IPEndPoint target);

    [LoggerMessage(EventId = 104, Level = LogLevel.Error, Message = "Handling a datagram from {Source} on the {Name} failed; datagram dropped")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string name, IPEndPoint source);

    [LoggerMessage(EventId = 105, Level = LogLevel.Error, Message = "The {Name} receive loop faulted")]
    private static partial void LogLoopFaulted(ILogger logger, Exception exception, string name);

    [LoggerMessage(EventId = 106, Level = LogLevel.Debug, Message = "The {Name} on UDP {Port} stopped")]
    private static partial void LogStopped(ILogger logger, string name, int port);
}
