using System.Net;
using System.Net.Sockets;

namespace Airplay2.Sender.Rtp;

/// <summary>Socket plumbing shared by <see cref="TimingResponder"/> and <see cref="ControlPort"/> (via <see cref="UdpReceiveLoop"/>).</summary>
internal static class UdpSockets
{
    // WSAIoctl SIO_UDP_CONNRESET: without it, an ICMP "port unreachable" for a datagram we sent (a receiver that went
    // away) makes the *next* ReceiveFrom on the socket fail with WSAECONNRESET on Windows.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>
    /// Creates and binds a UDP socket. A null or <see cref="IPAddress.IPv6Any"/> bind address gives a dual-mode IPv6
    /// socket (IPv4 peers appear as IPv4-mapped addresses, see <see cref="Normalize"/>) where the OS supports IPv6, an
    /// IPv4 wildcard socket otherwise; any other address binds that address only.
    /// </summary>
    public static Socket Bind(IPAddress? bindAddress, int port)
    {
        Socket socket;
        if (bindAddress is null || bindAddress.Equals(IPAddress.IPv6Any))
        {
            if (Socket.OSSupportsIPv6)
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
                bindAddress = IPAddress.IPv6Any;
            }
            else
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                bindAddress = IPAddress.Any;
            }
        }
        else
        {
            socket = new Socket(bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            socket.Bind(new IPEndPoint(bindAddress, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>A receive-address buffer large enough for the socket's family.</summary>
    public static SocketAddress NewAddress(Socket socket) => new(socket.AddressFamily, SocketAddress.GetMaximumAddressSize(socket.AddressFamily));

    /// <summary>Converts a received socket address to a normalised <see cref="IPEndPoint"/> (allocates; not for per-packet hot paths).</summary>
    public static IPEndPoint ToEndPoint(SocketAddress address) => Normalize((IPEndPoint)AnyEndPoint.Create(address));

    private static readonly IPEndPoint AnyEndPoint = new(IPAddress.Any, 0);

    /// <summary>Maps IPv4-mapped IPv6 addresses back to IPv4 so endpoints compare equal whatever socket saw them.</summary>
    public static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;

    /// <summary>The endpoint to pass to <c>SendTo</c> on <paramref name="socket"/>: an IPv4 target on an IPv4 socket stays as is, on a dual-mode socket .NET maps it.</summary>
    public static EndPoint Target(Socket socket, IPEndPoint endpoint)
    {
        if (socket.AddressFamily == AddressFamily.InterNetwork && endpoint.Address.IsIPv4MappedToIPv6)
        {
            return new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port);
        }

        return endpoint;
    }

    /// <summary>True for receive errors that concern one datagram (or a stale ICMP), after which the loop carries on.</summary>
    public static bool IsTransient(SocketException ex) => ex.SocketErrorCode is
        SocketError.ConnectionReset or SocketError.MessageSize or SocketError.NetworkReset or SocketError.HostUnreachable
        or SocketError.NetworkUnreachable or SocketError.ConnectionRefused or SocketError.NoBufferSpaceAvailable;
}
