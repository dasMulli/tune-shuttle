// New code replacing the `libmdns` crate (v0.10, used by discovery/src/lib.rs `launch_libmdns`): a minimal mDNS
// responder that advertises DNS-SD services. The structure follows libmdns file by file:
//   lib.rs            -> MdnsResponder (Spawn / RegisterAsync / service drop = goodbye)
//   services.rs       -> MdnsServices, MdnsService (ServiceData)
//   fsm.rs            -> MdnsAnswers (handle_packet / handle_question / send_unsolicited) + Fsm (socket loop)
//   address_family.rs -> Fsm.Bind (reuse-address bind on 5353, multicast join per interface)
//
// Deliberate deviations from libmdns, each for multi-NIC hosts (Windows in particular):
//   * a multicast join failure on one interface is logged and skipped (libmdns' own TODO) instead of failing the
//     whole address family;
//   * multicast responses go out on the interface the query arrived on, and announcements on every joined interface
//     (libmdns sends everything through the OS default multicast interface);
//   * address records list the addresses of that interface (falling back to all addresses when it has none that
//     pass the allow-list); libmdns lists every address of the host;
//   * identical records are not repeated within one message.
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Discovery.Mdns;

/// <summary>A registered DNS-SD service (libmdns <c>ServiceData</c>).</summary>
/// <param name="Name">The instance name, <c>&lt;name&gt;.&lt;type&gt;.local</c>.</param>
/// <param name="Type">The service type, <c>&lt;type&gt;.local</c>.</param>
/// <param name="Port">The port the service listens on.</param>
/// <param name="Txt">The TXT record.</param>
internal sealed record MdnsService(DnsName Name, DnsName Type, ushort Port, DnsRecordData.Txt Txt)
{
    /// <summary>libmdns <c>Responder::register_with_ttl</c> name building: <c>{svc_name}.{svc_type}.local</c>.</summary>
    public static MdnsService Create(string svcType, string svcName, ushort port, IReadOnlyList<string> txt)
    {
        var type = DnsName.Parse(svcType + ".local");
        return new MdnsService(type.Prepend(DnsName.TruncateLabel(svcName)), type, port, DnsRecordData.Txt.FromStrings(txt));
    }

    /// <summary>libmdns <c>ServiceData::add_ptr_rr</c>.</summary>
    public DnsRecord PtrRecord(uint ttl) => DnsRecord.Create(Type, ttl, new DnsRecordData.Ptr(Name));

    /// <summary>libmdns <c>ServiceData::add_srv_rr</c>.</summary>
    public DnsRecord SrvRecord(DnsName hostname, uint ttl) => DnsRecord.Create(Name, ttl, new DnsRecordData.Srv(0, 0, Port, hostname));

    /// <summary>libmdns <c>ServiceData::add_txt_rr</c>.</summary>
    public DnsRecord TxtRecord(uint ttl) => DnsRecord.Create(Name, ttl, Txt);
}

/// <summary>The registered services and the host name (libmdns <c>ServicesInner</c>).</summary>
internal sealed class MdnsServices
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, MdnsService> _byId = [];
    private int _nextId;

    /// <summary>Creates the registry for <paramref name="hostname"/> (<c>.local</c> is appended when missing).</summary>
    public MdnsServices(string hostname)
    {
        if (!hostname.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            hostname += ".local";
        }

        Hostname = DnsName.Parse(hostname);
    }

    /// <summary>The SRV target / A / AAAA owner name.</summary>
    public DnsName Hostname { get; }

    /// <summary>Adds a service, returning its id.</summary>
    public int Register(MdnsService svc)
    {
        lock (_lock)
        {
            var id = ++_nextId;
            _byId.Add(id, svc);
            return id;
        }
    }

    /// <summary>Removes a service by id.</summary>
    public MdnsService Unregister(int id)
    {
        lock (_lock)
        {
            if (!_byId.Remove(id, out var svc))
            {
                throw new InvalidOperationException("unknown service");
            }

            return svc;
        }
    }

    /// <summary>A snapshot of all services.</summary>
    public IReadOnlyList<MdnsService> Snapshot()
    {
        lock (_lock)
        {
            return [.. _byId.Values];
        }
    }
}

/// <summary>The packet logic of libmdns' <c>FSM</c>, free of sockets so it can be unit-tested.</summary>
internal static class MdnsAnswers
{
    /// <summary>libmdns <c>DEFAULT_TTL</c>.</summary>
    public const uint DefaultTtl = 60;

    /// <summary>RFC 6763 section 9 meta-query name.</summary>
    public static readonly DnsName ServiceTypeEnumerationName = DnsName.Parse("_services._dns-sd._udp.local");

    private static readonly ILogger Log = LibrespotLog.CreateLogger(typeof(MdnsResponder).FullName!);

    /// <summary>
    /// libmdns <c>FSM::handle_packet</c>: answers the questions of <paramref name="packet"/>. Questions with the QU bit
    /// go into the unicast reply, the others into the multicast reply; either is <c>null</c> when empty.
    /// </summary>
    /// <param name="packet">The received packet.</param>
    /// <param name="from">The sender (for logging).</param>
    /// <param name="hostname">The host name.</param>
    /// <param name="services">The registered services.</param>
    /// <param name="addresses">
    /// Supplies the addresses to publish for the host name (already filtered to this family). Like libmdns, which calls
    /// <c>get_if_addrs</c> only inside <c>add_ip_rr</c>, it is invoked only when an address record is added, and at most
    /// once per packet.
    /// </param>
    public static (DnsMessage? Multicast, DnsMessage? Unicast) HandlePacket(
        DnsMessage packet, EndPoint from, DnsName hostname, IReadOnlyList<MdnsService> services, Func<IReadOnlyList<IPAddress>> addresses)
    {
        if (packet.IsResponse)
        {
            Log.LogTrace("received packet from {From} with no query", from);
            return (null, null);
        }

        if (packet.IsTruncated)
        {
            Log.LogWarning("dropping truncated packet from {From}", from);
            return (null, null);
        }

        var unicast = DnsMessage.NewResponse(packet.Id);
        var multicast = DnsMessage.NewResponse(packet.Id);
        var lazyAddresses = new Lazy<IReadOnlyList<IPAddress>>(addresses, LazyThreadSafetyMode.None);

        foreach (var question in packet.Questions)
        {
            Log.LogDebug("received question: {Class} {Name}", question.Class, question.Name);

            if (question.Class is DnsClass.In or DnsClass.Any)
            {
                HandleQuestion(question, hostname, services, () => lazyAddresses.Value, question.UnicastResponse ? unicast : multicast);
            }
        }

        return (multicast.IsEmpty ? null : multicast, unicast.IsEmpty ? null : unicast);
    }

    /// <summary>libmdns <c>FSM::handle_question</c>. <paramref name="addresses"/> is invoked only when address records are added.</summary>
    public static void HandleQuestion(
        DnsQuestion question, DnsName hostname, IReadOnlyList<MdnsService> services, Func<IReadOnlyList<IPAddress>> addresses, DnsMessage builder)
    {
        switch (question.Type)
        {
            case DnsType.A or DnsType.Aaaa when question.Name.Equals(hostname):
                AddIpRecords(builder, hostname, addresses, DefaultTtl);
                break;
            case DnsType.Any:
                // A / AAAA
                if (question.Name.Equals(hostname))
                {
                    AddIpRecords(builder, hostname, addresses, DefaultTtl);
                }

                // PTR
                HandleServiceTypeEnumeration(question, services, builder);
                foreach (var svc in services.Where(s => s.Type.Equals(question.Name)))
                {
                    AddServiceRecords(builder, svc, hostname, addresses);
                }

                // SRV
                if (services.FirstOrDefault(s => s.Name.Equals(question.Name)) is { } byName)
                {
                    Add(builder, byName.SrvRecord(hostname, DefaultTtl));
                    AddIpRecords(builder, hostname, addresses, DefaultTtl);
                }

                break;
            case DnsType.Ptr:
                HandleServiceTypeEnumeration(question, services, builder);
                foreach (var svc in services.Where(s => s.Type.Equals(question.Name)))
                {
                    AddServiceRecords(builder, svc, hostname, addresses);
                }

                break;
            case DnsType.Srv:
                if (services.FirstOrDefault(s => s.Name.Equals(question.Name)) is { } srv)
                {
                    Add(builder, srv.SrvRecord(hostname, DefaultTtl));
                    AddIpRecords(builder, hostname, addresses, DefaultTtl);
                }

                break;
            case DnsType.Txt:
                if (services.FirstOrDefault(s => s.Name.Equals(question.Name)) is { } txt)
                {
                    Add(builder, txt.TxtRecord(DefaultTtl));
                }

                break;
        }
    }

    /// <summary>
    /// libmdns <c>FSM::send_unsolicited</c>: the announcement (<paramref name="ttl"/> &gt; 0, with addresses) or goodbye
    /// (<paramref name="ttl"/> = 0, without addresses) for <paramref name="svc"/>. <paramref name="addresses"/> is
    /// <c>null</c> for <c>include_ip = false</c>.
    /// </summary>
    public static DnsMessage BuildUnsolicited(MdnsService svc, DnsName hostname, uint ttl, Func<IReadOnlyList<IPAddress>>? addresses)
    {
        var builder = DnsMessage.NewResponse(0);
        Add(builder, svc.PtrRecord(ttl));
        Add(builder, svc.SrvRecord(hostname, ttl));
        Add(builder, svc.TxtRecord(ttl));
        if (addresses is not null)
        {
            AddIpRecords(builder, hostname, addresses, ttl);
        }

        return builder;
    }

    /// <summary>libmdns <c>FSM::handle_service_type_enumeration</c> (RFC 6763 section 9).</summary>
    private static void HandleServiceTypeEnumeration(DnsQuestion question, IReadOnlyList<MdnsService> services, DnsMessage builder)
    {
        if (question.Name.Equals(ServiceTypeEnumerationName))
        {
            foreach (var svc in services)
            {
                Add(builder, DnsRecord.Create(ServiceTypeEnumerationName, DefaultTtl, new DnsRecordData.Ptr(svc.Type)));
            }
        }
    }

    private static void AddServiceRecords(DnsMessage builder, MdnsService svc, DnsName hostname, Func<IReadOnlyList<IPAddress>> addresses)
    {
        Add(builder, svc.PtrRecord(DefaultTtl));
        Add(builder, svc.SrvRecord(hostname, DefaultTtl));
        Add(builder, svc.TxtRecord(DefaultTtl));
        AddIpRecords(builder, hostname, addresses, DefaultTtl);
    }

    /// <summary>libmdns <c>FSM::add_ip_rr</c>; the only place that enumerates the addresses.</summary>
    private static void AddIpRecords(DnsMessage builder, DnsName hostname, Func<IReadOnlyList<IPAddress>> addresses, uint ttl)
    {
        foreach (var address in addresses())
        {
            DnsRecordData data = address.AddressFamily == AddressFamily.InterNetworkV6
                ? new DnsRecordData.Aaaa(address)
                : new DnsRecordData.A(address);
            Add(builder, DnsRecord.Create(hostname, ttl, data));
        }
    }

    // libmdns puts every record into the answer section; repeats are dropped here.
    private static void Add(DnsMessage builder, DnsRecord record)
    {
        if (!builder.Answers.Contains(record))
        {
            builder.Answers.Add(record);
        }
    }
}

/// <summary>
/// A minimal mDNS responder (libmdns <c>Responder</c>): one socket per address family bound to port 5353, answering
/// queries for the registered services until disposed.
/// </summary>
internal sealed class MdnsResponder : IAsyncDisposable
{
    /// <summary>libmdns <c>MDNS_PORT</c>.</summary>
    public const int MdnsPort = 5353;

    /// <summary>The IPv4 mDNS group.</summary>
    public static readonly IPAddress GroupV4 = IPAddress.Parse("224.0.0.251");

    /// <summary>The IPv6 mDNS group.</summary>
    public static readonly IPAddress GroupV6 = IPAddress.Parse("ff02::fb");

    private static readonly ILogger Log = LibrespotLog.CreateLogger<MdnsResponder>();

    private readonly MdnsServices _services;
    private readonly List<Fsm> _fsms;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _task;
    private int _disposed;

    private MdnsResponder(MdnsServices services, List<Fsm> fsms)
    {
        _services = services;
        _fsms = fsms;
        _task = Task.WhenAll(fsms.Select(f => f.RunAsync(services, _shutdown.Token)));
    }

    /// <summary>The host name used as SRV target.</summary>
    public DnsName Hostname => _services.Hostname;

    /// <summary>
    /// libmdns <c>Responder::spawn_with_ip_list</c>: binds the IPv4 socket (failure is fatal) and the IPv6 socket
    /// (failure is logged), and starts answering.
    /// </summary>
    /// <param name="allowedIps">If not empty, only these addresses are published in A/AAAA records.</param>
    /// <param name="publishIpv6Addresses">Whether AAAA records are published at all.</param>
    /// <param name="hostname">The host name; defaults to the machine name.</param>
    public static MdnsResponder Spawn(IReadOnlyList<IPAddress> allowedIps, bool publishIpv6Addresses = true, string? hostname = null)
    {
        // Deviation from libmdns, which uses the full hostname::get() string (`mybox.fritz.box` -> `mybox.fritz.box.local`):
        // only the first label is used, so the SRV target is a single-label `.local` name as mDNS expects.
        hostname ??= Dns.GetHostName().Split('.')[0];
        var services = new MdnsServices(hostname);

        var fsms = new List<Fsm> { Fsm.Bind(AddressFamily.InterNetwork, allowedIps, publishAddresses: true) };
        try
        {
            fsms.Add(Fsm.Bind(AddressFamily.InterNetworkV6, allowedIps, publishIpv6Addresses));
        }
        catch (Exception e) when (e is SocketException or NetworkInformationException or PlatformNotSupportedException)
        {
            Log.LogWarning("Failed to register IPv6 receiver: {Error}", e.Message);
        }

        return new MdnsResponder(services, fsms);
    }

    /// <summary>
    /// libmdns <c>Responder::register</c>: announces the service and starts answering for it. Disposing the returned
    /// handle unregisters it and sends a goodbye (TTL 0).
    /// </summary>
    public async Task<Registration> RegisterAsync(
        string svcType, string svcName, ushort port, IReadOnlyList<string> txt, uint ttl = MdnsAnswers.DefaultTtl, CancellationToken cancellationToken = default)
    {
        var svc = MdnsService.Create(svcType, svcName, port, txt);
        await SendUnsolicitedAsync(svc, ttl, includeIp: true, cancellationToken).ConfigureAwait(false);
        var id = _services.Register(svc);
        return new Registration(this, id);
    }

    /// <summary>Stops answering and closes the sockets (libmdns <c>Shutdown</c>).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _task.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.LogDebug("mDNS responder stopped with {Error}", e.Message);
        }

        foreach (var fsm in _fsms)
        {
            fsm.Dispose();
        }

        _shutdown.Dispose();
    }

    private async Task SendUnsolicitedAsync(MdnsService svc, uint ttl, bool includeIp, CancellationToken cancellationToken)
    {
        foreach (var fsm in _fsms)
        {
            await fsm.SendUnsolicitedAsync(svc, _services.Hostname, ttl, includeIp, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A registered service (libmdns <c>Service</c>); disposing it sends the goodbye.</summary>
    public sealed class Registration : IAsyncDisposable
    {
        private readonly MdnsResponder _responder;
        private readonly int _id;
        private int _disposed;

        internal Registration(MdnsResponder responder, int id)
        {
            _responder = responder;
            _id = id;
        }

        /// <summary>libmdns <c>impl Drop for Service</c>: unregister and announce TTL 0 without addresses.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var svc = _responder._services.Unregister(_id);
            await _responder.SendUnsolicitedAsync(svc, 0, includeIp: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>A timestamped result of <c>GetIfAddrs</c>.</summary>
    private sealed record IfAddrsSnapshot(long Timestamp, List<(int Index, IPAddress Address)> Addresses);

    /// <summary>An interface this socket joined the group on.</summary>
    private sealed record JoinedInterface(int Index, IPAddress? V4Address);

    /// <summary>One address family's socket and loop (libmdns <c>FSM&lt;AF&gt;</c>).</summary>
    private sealed class Fsm : IDisposable
    {
        // Windows: SIO_UDP_CONNRESET, so an ICMP port-unreachable for a unicast reply does not fail later receives.
        private const int SioUdpConnReset = -1744830452;

        private static readonly TimeSpan IfAddrsCacheTime = TimeSpan.FromSeconds(1);

        private readonly Socket _socket;
        private readonly AddressFamily _family;
        private readonly IPAddress _group;
        private readonly IReadOnlyList<IPAddress> _allowedIps;
        private readonly bool _publishAddresses;
        private readonly List<JoinedInterface> _joined;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // Interface enumeration is slow on hosts with many adapters (tens of ms); the result is reused for
        // IfAddrsCacheTime. Replaced as a whole, so concurrent readers always see a consistent snapshot.
        private volatile IfAddrsSnapshot? _ifAddrsCache;

        private Fsm(Socket socket, AddressFamily family, IReadOnlyList<IPAddress> allowedIps, bool publishAddresses, List<JoinedInterface> joined)
        {
            _socket = socket;
            _family = family;
            _group = family == AddressFamily.InterNetwork ? GroupV4 : GroupV6;
            _allowedIps = allowedIps;
            _publishAddresses = publishAddresses;
            _joined = joined;
        }

        /// <summary>libmdns <c>AddressFamily::bind</c> + <c>join_multicast</c>.</summary>
        public static Fsm Bind(AddressFamily family, IReadOnlyList<IPAddress> allowedIps, bool publishAddresses)
        {
            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (OperatingSystem.IsWindows())
                {
                    socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
                }

                var v6 = family == AddressFamily.InterNetworkV6;
                if (v6)
                {
                    socket.DualMode = false;
                }

                socket.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, MdnsPort));

                var level = v6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
                socket.SetSocketOption(level, SocketOptionName.PacketInformation, true);
                socket.SetSocketOption(level, SocketOptionName.MulticastTimeToLive, 255);
                socket.SetSocketOption(level, SocketOptionName.MulticastLoopback, true);

                var joined = JoinMulticast(socket, family);
                return new Fsm(socket, family, allowedIps, publishAddresses, joined);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>The answer loop (libmdns <c>impl Future for FSM</c>): receive, answer, send.</summary>
        public async Task RunAsync(MdnsServices services, CancellationToken cancellationToken)
        {
            // Buffer size discussed in: https://github.com/librespot-org/libmdns/pull/40
            var buffer = new byte[65536];
            EndPoint any = new IPEndPoint(_family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult received;
                try
                {
                    received = await _socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException e)
                {
                    Log.LogError("ResponderRecvPacket Error: {Error}", e.Message);
                    try
                    {
                        // Keep a persistently failing socket from spinning.
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                await HandlePacketAsync(buffer.AsMemory(0, received.ReceivedBytes), received.RemoteEndPoint, received.PacketInformation.Interface, services, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public async Task SendUnsolicitedAsync(MdnsService svc, DnsName hostname, uint ttl, bool includeIp, CancellationToken cancellationToken)
        {
            foreach (var iface in InterfacesToSendOn())
            {
                Func<IReadOnlyList<IPAddress>>? addresses = includeIp ? () => CurrentAddresses(iface?.Index) : null;
                var message = MdnsAnswers.BuildUnsolicited(svc, hostname, ttl, addresses);
                if (!message.IsEmpty)
                {
                    await SendAsync(message.Encode(), new IPEndPoint(_group, MdnsPort), iface, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public void Dispose()
        {
            _socket.Dispose();
            _sendLock.Dispose();
        }

        private async Task HandlePacketAsync(ReadOnlyMemory<byte> data, EndPoint from, int interfaceIndex, MdnsServices services, CancellationToken cancellationToken)
        {
            Log.LogTrace("received packet from {From}", from);

            if (!DnsMessage.TryParse(data.Span, out var packet, out var error))
            {
                Log.LogWarning("couldn't parse packet from {From}: {Error}", from, error);
                return;
            }

            var iface = _joined.FirstOrDefault(j => j.Index == interfaceIndex);
            var addressIndex = iface?.Index ?? interfaceIndex;
            var (multicast, unicast) = MdnsAnswers.HandlePacket(packet, from, services.Hostname, services.Snapshot(), () => CurrentAddresses(addressIndex));

            if (multicast is not null)
            {
                await SendAsync(multicast.Encode(), new IPEndPoint(_group, MdnsPort), iface, cancellationToken).ConfigureAwait(false);
            }

            if (unicast is not null)
            {
                await SendAsync(unicast.Encode(), from, null, cancellationToken).ConfigureAwait(false);
            }
        }

        private IEnumerable<JoinedInterface?> InterfacesToSendOn() => _joined.Count == 0 ? [null] : _joined.Cast<JoinedInterface?>();

        private async Task SendAsync(byte[] packet, EndPoint to, JoinedInterface? iface, CancellationToken cancellationToken)
        {
            Log.LogTrace("sending packet to {To}", to);

            try
            {
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // The responder was disposed before this registration (libmdns' Service keeps the responder alive
                // through Arc<Shutdown>, so this cannot happen there): the socket is gone, nothing to send.
                return;
            }

            try
            {
                if (iface is not null)
                {
                    if (_family == AddressFamily.InterNetwork)
                    {
                        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, iface.V4Address!.GetAddressBytes());
                    }
                    else
                    {
                        _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, iface.Index);
                    }
                }

                var sent = await _socket.SendToAsync(packet, SocketFlags.None, to, cancellationToken).ConfigureAwait(false);
                if (sent != packet.Length)
                {
                    Log.LogWarning("failed to send entire packet");
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                Log.LogWarning("error sending packet {Error}", e.Message);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// libmdns <c>add_ip_rr</c> address selection: non-loopback addresses of this family, limited to the allow-list.
        /// Prefers the addresses of <paramref name="interfaceIndex"/>; falls back to all when it has none.
        /// </summary>
        private IReadOnlyList<IPAddress> CurrentAddresses(int? interfaceIndex)
        {
            if (!_publishAddresses)
            {
                return [];
            }

            List<(int Index, IPAddress Address)> all;
            try
            {
                all = CachedIfAddrs()
                    .Where(a => _allowedIps.Count == 0 || _allowedIps.Any(ip => SameAddress(ip, a.Address)))
                    .ToList();
            }
            catch (NetworkInformationException e)
            {
                Log.LogError("could not get list of interfaces: {Error}", e.Message);
                return [];
            }

            var own = all.Where(a => a.Index == interfaceIndex).Select(a => a.Address).ToList();
            return own.Count > 0 ? own : all.Select(a => a.Address).Distinct().ToList();
        }

        private List<(int Index, IPAddress Address)> CachedIfAddrs()
        {
            if (_ifAddrsCache is { } cache && Stopwatch.GetElapsedTime(cache.Timestamp) < IfAddrsCacheTime)
            {
                return cache.Addresses;
            }

            var addresses = GetIfAddrs(_family).ToList();
            _ifAddrsCache = new IfAddrsSnapshot(Stopwatch.GetTimestamp(), addresses);
            return addresses;
        }

        private static bool SameAddress(IPAddress a, IPAddress b)
        {
            if (a.IsIPv4MappedToIPv6)
            {
                a = a.MapToIPv4();
            }

            if (b.IsIPv4MappedToIPv6)
            {
                b = b.MapToIPv4();
            }

            return a.AddressFamily == b.AddressFamily && a.GetAddressBytes().AsSpan().SequenceEqual(b.GetAddressBytes());
        }

        /// <summary>
        /// libmdns <c>get_one_nonloopback_ipv4_addr_per_iface</c> / <c>get_one_nonloopback_ipv6_index_per_iface</c> +
        /// <c>join_multicast</c>: joins the group once per interface; with no interface, on the default one.
        /// </summary>
        private static List<JoinedInterface> JoinMulticast(Socket socket, AddressFamily family)
        {
            var joined = new List<JoinedInterface>();
            var candidates = GetIfAddrs(family)
                .GroupBy(a => a.Index)
                .Select(g => new JoinedInterface(g.Key, family == AddressFamily.InterNetwork ? g.First().Address : null))
                .ToList();

            foreach (var iface in candidates)
            {
                try
                {
                    if (family == AddressFamily.InterNetwork)
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GroupV4, iface.V4Address!));
                    }
                    else
                    {
                        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(GroupV6, iface.Index));
                    }

                    joined.Add(iface);
                }
                catch (SocketException e)
                {
                    Log.LogWarning("Failed to join mDNS group on interface {Index}: {Error}", iface.Index, e.Message);
                }
            }

            if (joined.Count == 0)
            {
                if (family == AddressFamily.InterNetwork)
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GroupV4, IPAddress.Any));
                }
                else
                {
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(GroupV6, 0));
                }
            }

            return joined;
        }

        /// <summary>The <c>if_addrs::get_if_addrs</c> equivalent: (interface index, address) of up, non-loopback interfaces.</summary>
        private static IEnumerable<(int Index, IPAddress Address)> GetIfAddrs(AddressFamily family)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties props;
                int index;
                try
                {
                    props = nic.GetIPProperties();
                    index = family == AddressFamily.InterNetwork
                        ? props.GetIPv4Properties()?.Index ?? -1
                        : props.GetIPv6Properties()?.Index ?? -1;
                }
                catch (NetworkInformationException)
                {
                    continue; // the protocol is not enabled on this interface
                }

                if (index < 0)
                {
                    continue;
                }

                foreach (var unicast in props.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == family && !IPAddress.IsLoopback(address))
                    {
                        yield return (index, address);
                    }
                }
            }
        }
    }
}
