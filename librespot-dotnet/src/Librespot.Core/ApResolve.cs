// Port of core/src/apresolve.rs.
// TODO(port: core-connection): resolve via https://apresolve.spotify.com/?type=accesspoint&type=dealer&type=spclient,
// fallback addresses, port_config filtering, VecDeque rotation.
namespace Librespot.Core;

/// <summary>Upstream <c>SocketAddress = (String, u16)</c>.</summary>
public readonly record struct SocketAddress(string Host, ushort Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>Resolves Spotify access point / dealer / spclient endpoints.</summary>
public sealed class ApResolver : SessionComponent
{
    internal ApResolver(Session session) : base(session)
    {
    }

    /// <summary>
    /// Returns a port if a proxy URL and/or a proxy port was specified. This is useful even when there is no
    /// proxy, but firewalls only allow certain ports (e.g. 443 and not 4070).
    /// </summary>
    public ushort? PortConfig()
    {
        if (Session.Config.Proxy is not null || Session.Config.ApPort is not null)
        {
            return Session.Config.ApPort ?? 443;
        }

        return null;
    }

    /// <summary>Upstream <c>resolve(endpoint)</c> with endpoint one of "accesspoint", "dealer", "spclient".</summary>
    public Task<SocketAddress> ResolveAsync(string endpoint, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-connection)");
}
