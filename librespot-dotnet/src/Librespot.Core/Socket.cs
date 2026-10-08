// Port of core/src/socket.rs.
using System.Globalization;
using System.Net.Sockets;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core;

/// <summary>TCP connection to an access point, directly or through an HTTP proxy (upstream private module <c>socket</c>).</summary>
/// <remarks>Named <c>ApSocket</c> rather than <c>Socket</c> so it cannot shadow <see cref="System.Net.Sockets.Socket"/>.</remarks>
internal static class ApSocket
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger("Librespot.Core.Socket");

    /// <summary>
    /// Upstream <c>socket::connect(host, port, proxy)</c>: connects to <paramref name="host"/>:<paramref name="port"/>,
    /// tunnelling through <paramref name="proxy"/> with HTTP <c>CONNECT</c> when one is given. The returned client
    /// is connected (to the proxy when tunnelling) and its stream carries the tunnel.
    /// </summary>
    public static async Task<TcpClient> ConnectAsync(string host, ushort port, Uri? proxy, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        try
        {
            if (proxy is not null)
            {
                Log.LogInformation("Using proxy \"{Proxy}\"", proxy);

                // Upstream `Url::socket_addrs(|| None)`: the URL must carry a port or a scheme with a known default.
                if (string.IsNullOrEmpty(proxy.Host) || proxy.Port < 0)
                {
                    throw new IOException("No socket addresses for proxy URL");
                }

                await client.ConnectAsync(proxy.IdnHost, proxy.Port, cancellationToken).ConfigureAwait(false);
                await ProxyTunnel.ProxyConnectAsync(client.GetStream(), host, port.ToString(CultureInfo.InvariantCulture), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
