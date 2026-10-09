// Port of discovery/src/lib.rs.
//
// Advertises this device to Spotify clients in the local network.
//
// This device will show up in the list of "available devices". Once it is selected from the list, Credentials are
// received. Those can be used to establish a new Session with Librespot.Core.
//
// This library uses mDNS and DNS-SD so that other devices can find it, and spawns an http server to answer requests
// of Spotify clients.
//
// Backends: "libmdns" is the built-in responder in Mdns/ (it replaces the libmdns crate). "avahi" (`avahi_task`,
// `launch_avahi`, Avahi.cs) is Linux-only and not ported; "dns-sd" (`launch_dns_sd`, Bonjour's dns_sd library) is not
// ported either. Both stay in the registry as known-but-not-compiled entries, as upstream does without their features.
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Core.Logging;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging;

namespace Librespot.Discovery;

/// <summary>Upstream <c>DiscoveryEvent</c>.</summary>
public abstract record DiscoveryEvent
{
    private DiscoveryEvent()
    {
    }

    /// <summary>A Spotify client selected this device and sent credentials.</summary>
    /// <param name="Value">The credentials.</param>
    public sealed record Credentials(Core.Credentials Value) : DiscoveryEvent;

    /// <summary>The HTTP server failed fatally.</summary>
    /// <param name="Error">The error.</param>
    public sealed record ServerError(DiscoveryException Error) : DiscoveryEvent;

    /// <summary>The mDNS / DNS-SD backend failed fatally.</summary>
    /// <param name="Error">The error.</param>
    public sealed record ZeroconfError(DiscoveryException Error) : DiscoveryEvent;
}

/// <summary>Errors that can occur while setting up a <see cref="Discovery"/> instance (upstream <c>DiscoveryError</c>).</summary>
public sealed class DiscoveryException : LibrespotException
{
    /// <summary>The upstream <c>DiscoveryError</c> variant.</summary>
    public enum Reason
    {
        /// <summary><c>AesError</c>: "Creating SHA1 block cipher failed".</summary>
        AesKey,

        /// <summary><c>DnsSdError</c>: "Setting up dns-sd failed: {0}".</summary>
        DnsSdError,

        /// <summary><c>HmacError</c>: "Creating SHA1 HMAC failed for base key {0:?}".</summary>
        HmacError,

        /// <summary><c>HttpServerError</c>: "Setting up the HTTP server failed: {0}".</summary>
        HttpServer,

        /// <summary><c>ParamsError</c>: "Missing params for key {0}".</summary>
        Params,
    }

    /// <summary>The variant.</summary>
    public Reason Cause { get; }

    /// <summary>Creates the exception; prefer the factories, which fix the upstream message and kind.</summary>
    public DiscoveryException(Reason cause, ErrorKind kind, string detail, Exception? inner = null) : base(kind, detail, inner)
    {
        Cause = cause;
    }

    /// <summary><c>DiscoveryError::AesError</c> (kind Unavailable).</summary>
    public static DiscoveryException Aes(Exception inner) =>
        new(Reason.AesKey, ErrorKind.Unavailable, "Creating SHA1 block cipher failed", inner);

    /// <summary><c>DiscoveryError::DnsSdError</c> (kind Unavailable).</summary>
    public static DiscoveryException DnsSd(Exception inner) =>
        new(Reason.DnsSdError, ErrorKind.Unavailable, $"Setting up dns-sd failed: {inner.Message}", inner);

    /// <summary><c>DiscoveryError::HmacError</c> (kind InvalidArgument).</summary>
    public static DiscoveryException Hmac(ReadOnlySpan<byte> baseKey) =>
        new(Reason.HmacError, ErrorKind.InvalidArgument, $"Creating SHA1 HMAC failed for base key [{string.Join(", ", baseKey.ToArray())}]");

    /// <summary><c>DiscoveryError::HttpServerError</c> (kind Unavailable).</summary>
    public static DiscoveryException HttpServer(Exception inner) =>
        new(Reason.HttpServer, ErrorKind.Unavailable, $"Setting up the HTTP server failed: {inner.Message}", inner);

    /// <summary><c>DiscoveryError::ParamsError</c> (kind InvalidArgument).</summary>
    public static DiscoveryException Params(string key) =>
        new(Reason.Params, ErrorKind.InvalidArgument, $"Missing params for key {key}");
}

/// <summary>Handle to a running DNS-SD responder (upstream <c>DnsSdHandle</c>). Disposing it shuts the responder down.</summary>
public sealed class DnsSdHandle : IAsyncDisposable
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<DnsSdHandle>();

    private readonly Func<Task> _shutdown;
    private readonly Lock _lock = new();
    private Task? _shutdownTask;

    /// <summary>Creates a handle whose shutdown runs <paramref name="shutdown"/> (once).</summary>
    public DnsSdHandle(Func<Task> shutdown)
    {
        _shutdown = shutdown;
    }

    /// <summary>
    /// Upstream <c>DnsSdHandle { task_handle, shutdown_tx }</c>: shutdown cancels <paramref name="shutdownTx"/> and
    /// awaits <paramref name="taskHandle"/>; when the task already ended, logs that the responder disappeared.
    /// </summary>
    /// <param name="taskHandle">The responder task; it must observe its own exceptions.</param>
    /// <param name="shutdownTx">Cancelled to ask the responder to stop.</param>
    public static DnsSdHandle FromTask(Task taskHandle, CancellationTokenSource shutdownTx) => new(async () =>
    {
        Log.LogDebug("Shutting down zeroconf responder");
        if (taskHandle.IsCompleted)
        {
            Log.LogWarning("Zeroconf responder unexpectedly disappeared");
        }
        else
        {
            await shutdownTx.CancelAsync().ConfigureAwait(false);
            try
            {
                await taskHandle.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // `let _ = task_handle.await;`
            }

            Log.LogDebug("Zeroconf responder stopped");
        }

        shutdownTx.Dispose();
    });

    /// <summary>Upstream <c>shutdown</c>. Idempotent.</summary>
    public Task ShutdownAsync()
    {
        lock (_lock)
        {
            return _shutdownTask ??= _shutdown();
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(ShutdownAsync());
}

/// <summary>
/// Upstream <c>DnsSdServiceBuilder</c>: starts advertising <c>_spotify-connect._tcp</c> with the given name on the
/// given addresses/port, reporting errors to <paramref name="events"/>.
/// </summary>
public delegate DnsSdHandle DnsSdServiceBuilder(string name, IReadOnlyList<IPAddress> zeroconfIps, ushort port, ChannelWriter<DiscoveryEvent> events);

/// <summary>Registry of DNS-SD backends (upstream <c>BACKENDS</c>/<c>find</c>). Only the built-in responder exists in this port.</summary>
public static class DnsSdBackends
{
    /// <summary>The name of the built-in responder (the upstream default backend <c>libmdns</c>).</summary>
    public const string BuiltInName = "libmdns";

    /// <summary>Upstream <c>DNS_SD_SERVICE_NAME</c>.</summary>
    public const string DnsSdServiceName = "_spotify-connect._tcp";

    /// <summary>Upstream <c>TXT_RECORD</c>.</summary>
    public static IReadOnlyList<string> TxtRecord { get; } = ["VERSION=1.0", "CPath=/"];

    private static readonly ILogger Log = LibrespotLog.CreateLogger(typeof(Discovery).FullName!);

    /// <summary>(name, builder-or-null-if-known-but-not-compiled); the first entry with a builder is the default.</summary>
    public static IReadOnlyList<(string Name, DnsSdServiceBuilder? Builder)> All { get; } =
    [
        ("avahi", null),
        ("dns-sd", null),
        (BuiltInName, LaunchLibmdns),
    ];

    /// <summary>Upstream <c>find</c>: the named backend, or the default when <paramref name="name"/> is <c>null</c>.</summary>
    /// <exception cref="LibrespotException">Unknown backend (NotFound) or backend not available in this build (Unavailable).</exception>
    public static DnsSdServiceBuilder Find(string? name)
    {
        if (name is not null)
        {
            foreach (var (id, launchSvc) in All)
            {
                if (id == name)
                {
                    return launchSvc ?? throw LibrespotException.Unavailable($"librespot built without '{name}' support");
                }
            }

            throw LibrespotException.NotFound($"unknown zeroconf backend '{name}'");
        }

        return All.Select(b => b.Builder).FirstOrDefault(b => b is not null)
            ?? throw LibrespotException.Unavailable("librespot built without zeroconf backends");
    }

    /// <summary>Upstream <c>launch_libmdns</c>, backed by the built-in <see cref="MdnsResponder"/>.</summary>
    private static DnsSdHandle LaunchLibmdns(string name, IReadOnlyList<IPAddress> zeroconfIp, ushort port, ChannelWriter<DiscoveryEvent> statusTx)
    {
        var shutdownTx = new CancellationTokenSource();
        var shutdownRx = shutdownTx.Token;

        var taskHandle = Task.Run(async () =>
        {
            try
            {
                // Deviation: the HTTP server only listens on IPv4 on Windows (server.rs), so AAAA records would point
                // clients at an address nothing listens on; they are only published where the server is dual-stack.
                var responder = MdnsResponder.Spawn(zeroconfIp, publishIpv6Addresses: !OperatingSystem.IsWindows());
                await using (responder.ConfigureAwait(false))
                {
                    var svc = await responder.RegisterAsync(DnsSdServiceName, name, port, TxtRecord, cancellationToken: shutdownRx).ConfigureAwait(false);
                    await using (svc.ConfigureAwait(false))
                    {
                        // `let _ = shutdown_rx.blocking_recv();`
                        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        await using (shutdownRx.Register(() => shutdown.TrySetResult()).ConfigureAwait(false))
                        {
                            await shutdown.Task.ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                var error = e as DiscoveryException ?? DiscoveryException.DnsSd(e);
                Log.LogError("libmdns error: {Error}", error.Detail);
                statusTx.TryWrite(new DiscoveryEvent.ZeroconfError(error));
            }
        });

        return DnsSdHandle.FromTask(taskHandle, shutdownTx);
    }
}

/// <summary>A builder for <see cref="Discovery"/> (upstream <c>Builder</c>).</summary>
public sealed class DiscoveryBuilder
{
    private readonly ServerConfig _serverConfig;
    private ushort _port;
    private IReadOnlyList<IPAddress> _zeroconfIp = [];
    private DnsSdServiceBuilder? _zeroconfBackend;

    /// <summary>Starts a new builder using the provided device and client IDs.</summary>
    public DiscoveryBuilder(string deviceId, string clientId)
    {
        _serverConfig = new ServerConfig
        {
            Name = "Librespot",
            DeviceType = DeviceTypeExtensions.Default,
            IsGroup = false,
            DeviceId = deviceId,
            ClientId = clientId,
        };
    }

    /// <summary>Sets the name to be displayed. Default is <c>"Librespot"</c>.</summary>
    public DiscoveryBuilder Name(string name)
    {
        _serverConfig.Name = name;
        return this;
    }

    /// <summary>Sets the device type which is visible as icon in other Spotify clients. Default is <c>Speaker</c>.</summary>
    public DiscoveryBuilder DeviceType(DeviceType deviceType)
    {
        _serverConfig.DeviceType = deviceType;
        return this;
    }

    /// <summary>Sets whether the device is a group. This affects the icon in Spotify clients. Default is <c>false</c>.</summary>
    public DiscoveryBuilder IsGroup(bool isGroup)
    {
        _serverConfig.IsGroup = isGroup;
        return this;
    }

    /// <summary>Adds an alias for this device. Multiple aliases can be added by calling this method multiple times.</summary>
    /// <param name="alias">The alias name.</param>
    /// <param name="id">The alias id.</param>
    /// <param name="isGroup">Whether the alias is a group.</param>
    public DiscoveryBuilder AddAlias(string alias, uint id, bool isGroup)
    {
        _serverConfig.Aliases.Add(new Alias(alias, id, isGroup));
        return this;
    }

    /// <summary>Set the ip addresses on which it should listen to incoming connections. The default is all interfaces.</summary>
    public DiscoveryBuilder ZeroconfIp(IEnumerable<IPAddress> ips)
    {
        _zeroconfIp = [.. ips];
        return this;
    }

    /// <summary>Set the zeroconf (MDNS and DNS-SD) implementation to use.</summary>
    public DiscoveryBuilder ZeroconfBackend(DnsSdServiceBuilder backend)
    {
        _zeroconfBackend = backend;
        return this;
    }

    /// <summary>Sets the port on which it should listen to incoming connections. The default value <c>0</c> means any port.</summary>
    public DiscoveryBuilder Port(ushort port)
    {
        _port = port;
        return this;
    }

    /// <summary>Sets up the <see cref="Discovery"/> instance: starts the HTTP server and the DNS-SD responder.</summary>
    /// <exception cref="LibrespotException">Setting up the mdns service or creating the server failed.</exception>
    public Discovery Launch()
    {
        var name = _serverConfig.Name;
        var zeroconfIp = _zeroconfIp;

        var events = Channel.CreateUnbounded<DiscoveryEvent>();

        var port = _port;
        var server = new DiscoveryServer(_serverConfig, ref port, events.Writer);

        DnsSdHandle svc;
        try
        {
            var launchSvc = _zeroconfBackend ?? DnsSdBackends.Find(null);
            svc = launchSvc(name, zeroconfIp, port, events.Writer);
        }
        catch
        {
            // Upstream drops the server here, which closes it.
            server.Stop();
            throw;
        }

        return new Discovery(server, svc, events);
    }
}

/// <summary>
/// Makes this device visible to Spotify clients in the local network. Every time this device is selected in the list
/// of available devices, it yields <see cref="Core.Credentials"/> (<see cref="ReadCredentialsAsync"/>, upstream
/// <c>impl Stream for Discovery</c>).
/// </summary>
public sealed class Discovery : IAsyncDisposable
{
    private readonly DiscoveryServer _server;

    /// <summary>An opaque handle to the DNS-SD service. Disposing it unregisters the service.</summary>
    private readonly DnsSdHandle _svc;

    private readonly Channel<DiscoveryEvent> _events;
    private readonly Lock _lock = new();
    private Task? _shutdownTask;

    internal Discovery(DiscoveryServer server, DnsSdHandle svc, Channel<DiscoveryEvent> events)
    {
        _server = server;
        _svc = svc;
        _events = events;
    }

    /// <summary>The port of the zeroconf HTTP server.</summary>
    public ushort Port => _server.Port;

    /// <summary>Starts a <see cref="DiscoveryBuilder"/> with the provided device id.</summary>
    public static DiscoveryBuilder Builder(string deviceId, string clientId) => new(deviceId, clientId);

    /// <summary>Upstream <c>Discovery::new</c>: create a new instance with the specified device id and default parameters.</summary>
    public static Discovery Create(string deviceId, string clientId) => Builder(deviceId, clientId).Launch();

    /// <summary>
    /// All raw events, including <see cref="DiscoveryEvent.ServerError"/> / <see cref="DiscoveryEvent.ZeroconfError"/>.
    /// Ends after <see cref="ShutdownAsync"/>. Do not consume this and <see cref="ReadCredentialsAsync"/> at the same time.
    /// </summary>
    public IAsyncEnumerable<DiscoveryEvent> Events(CancellationToken cancellationToken = default) => _events.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Upstream <c>impl Stream for Discovery</c>: yields credentials; terminates on fatal server or MDNS/DNS-SD errors
    /// and after <see cref="ShutdownAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<Core.Credentials> ReadCredentialsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var ev in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (ev)
            {
                // Yields credentials
                case DiscoveryEvent.Credentials credentials:
                    yield return credentials.Value;
                    break;

                // Also terminate the stream on fatal server or MDNS/DNS-SD errors.
                case DiscoveryEvent.ServerError or DiscoveryEvent.ZeroconfError:
                    yield break;
            }
        }
    }

    /// <summary>Upstream <c>shutdown()</c>: stops the server and the responder concurrently. Idempotent.</summary>
    public Task ShutdownAsync()
    {
        lock (_lock)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task ShutdownCoreAsync()
    {
        try
        {
            await Task.WhenAll(_server.ShutdownAsync(), _svc.ShutdownAsync()).ConfigureAwait(false);
        }
        finally
        {
            // Both event senders are gone: the stream ends (upstream `poll_recv` -> None).
            _events.Writer.TryComplete();
        }
    }
}
