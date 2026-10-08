// Port of discovery/src/lib.rs (public contract). server.rs (the zeroconf HTTP server: getInfo/addUser, DH +
// AES-CTR blob decryption) and the mDNS responder (replaces libmdns; avahi.rs/dns-sd are not ported) are
// package `discovery`.
using System.Net;
using System.Threading.Channels;
using Librespot.Core;

namespace Librespot.Discovery;

/// <summary>Upstream <c>DiscoveryEvent</c>.</summary>
public abstract record DiscoveryEvent
{
    private DiscoveryEvent()
    {
    }

    /// <summary>A Spotify client selected this device and sent credentials.</summary>
    public sealed record Credentials(Core.Credentials Value) : DiscoveryEvent;

    public sealed record ServerError(DiscoveryException Error) : DiscoveryEvent;

    public sealed record ZeroconfError(DiscoveryException Error) : DiscoveryEvent;
}

/// <summary>Upstream <c>DiscoveryError</c>.</summary>
public sealed class DiscoveryException : LibrespotException
{
    public enum Reason
    {
        AesKey,
        DnsSdError,
        HmacError,
        HttpServer,
        Params,
    }

    public Reason Cause { get; }

    public DiscoveryException(Reason cause, ErrorKind kind, string detail, Exception? inner = null) : base(kind, detail, inner)
    {
        Cause = cause;
    }
}

/// <summary>Handle to a running DNS-SD responder (upstream <c>DnsSdHandle</c>).</summary>
public sealed class DnsSdHandle : IAsyncDisposable
{
    private readonly Func<Task> _shutdown;

    public DnsSdHandle(Func<Task> shutdown)
    {
        _shutdown = shutdown;
    }

    public ValueTask DisposeAsync() => new(_shutdown());
}

/// <summary>
/// Upstream <c>DnsSdServiceBuilder</c>: starts advertising <c>_spotify-connect._tcp</c> with the given name on the
/// given addresses/port, reporting errors to <paramref name="events"/>.
/// </summary>
public delegate DnsSdHandle DnsSdServiceBuilder(string name, IReadOnlyList<IPAddress> zeroconfIps, ushort port, ChannelWriter<DiscoveryEvent> events);

/// <summary>Registry of DNS-SD backends (upstream <c>BACKENDS</c>/<c>find</c>). Only the built-in responder exists in this port.</summary>
public static class DnsSdBackends
{
    public const string BuiltInName = "libmdns";

    /// <summary>(name, builder-or-null-if-known-but-not-compiled); the first is the default.</summary>
    public static IReadOnlyList<(string Name, DnsSdServiceBuilder? Builder)> All { get; } =
    [
        (BuiltInName, (name, ips, port, events) => throw new NotImplementedException("TODO(port: discovery) built-in mDNS responder")),
        ("avahi", null),
        ("dns-sd", null),
    ];

    public static DnsSdServiceBuilder Find(string? name)
    {
        if (name is null)
        {
            return All[0].Builder!;
        }

        foreach (var (n, b) in All)
        {
            if (n == name)
            {
                return b ?? throw new DiscoveryException(DiscoveryException.Reason.DnsSdError, ErrorKind.Unimplemented, $"Librespot built without support for zeroconf backend {name}");
            }
        }

        throw new DiscoveryException(DiscoveryException.Reason.DnsSdError, ErrorKind.NotFound, $"Unknown zeroconf backend {name}");
    }
}

/// <summary>Builder for <see cref="Discovery"/> (upstream <c>Builder</c>).</summary>
public sealed class DiscoveryBuilder
{
    private readonly string _deviceId;
    private readonly string _clientId;
    private string _name = "librespot";
    private DeviceType _deviceType = Librespot.Core.DeviceType.Speaker;
    private bool _isGroup;
    private readonly List<(string Name, bool IsGroup, bool IsCurrent)> _aliases = [];
    private readonly List<IPAddress> _zeroconfIps = [];
    private DnsSdServiceBuilder? _zeroconfBackend;
    private ushort _port;

    public DiscoveryBuilder(string deviceId, string clientId)
    {
        _deviceId = deviceId;
        _clientId = clientId;
    }

    public DiscoveryBuilder Name(string name) { _name = name; return this; }
    public DiscoveryBuilder DeviceType(DeviceType deviceType) { _deviceType = deviceType; return this; }
    public DiscoveryBuilder IsGroup(bool isGroup) { _isGroup = isGroup; return this; }
    public DiscoveryBuilder AddAlias(string name, bool isGroup, bool isCurrent) { _aliases.Add((name, isGroup, isCurrent)); return this; }
    public DiscoveryBuilder ZeroconfIp(IEnumerable<IPAddress> ips) { _zeroconfIps.AddRange(ips); return this; }
    public DiscoveryBuilder ZeroconfBackend(DnsSdServiceBuilder backend) { _zeroconfBackend = backend; return this; }
    /// <summary>The port for the HTTP server; 0 picks a free one.</summary>
    public DiscoveryBuilder Port(ushort port) { _port = port; return this; }

    /// <summary>Starts the HTTP server and the DNS-SD responder.</summary>
    public Discovery Launch() => throw new NotImplementedException("TODO(port: discovery)");
}

/// <summary>
/// Advertises this device to Spotify clients in the local network; once selected, yields the received
/// <see cref="Core.Credentials"/> as a stream of <see cref="DiscoveryEvent"/>s.
/// </summary>
public sealed class Discovery : IAsyncDisposable
{
    private readonly ChannelReader<DiscoveryEvent> _events;

    internal Discovery(ChannelReader<DiscoveryEvent> events)
    {
        _events = events;
    }

    public static DiscoveryBuilder Builder(string deviceId, string clientId) => new(deviceId, clientId);

    /// <summary>Upstream <c>Discovery::new</c>: builder with defaults, launched.</summary>
    public static Discovery Create(string deviceId, string clientId) => Builder(deviceId, clientId).Launch();

    /// <summary>Upstream <c>impl Stream for Discovery</c>.</summary>
    public IAsyncEnumerable<DiscoveryEvent> Events(CancellationToken cancellationToken = default) => _events.ReadAllAsync(cancellationToken);

    /// <summary>Upstream <c>shutdown()</c>.</summary>
    public Task ShutdownAsync() => throw new NotImplementedException("TODO(port: discovery)");

    public ValueTask DisposeAsync() => new(ShutdownAsync());
}
