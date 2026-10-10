using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Discovery;

/// <summary>
/// mDNS implementation of <see cref="IReceiverBrowser"/> on top of <c>Librespot.Discovery.Mdns.DnsMessage</c>
/// (SPEC-control.md 1.1-1.6).
/// </summary>
/// <remarks>
/// <para>Sockets, per address family: one listener bound to port 5353 with <c>ReuseAddress</c> that joins
/// 224.0.0.251 / ff02::fb on every selected interface and sends the multicast queries (answers come back by multicast);
/// group, port and hop limit come from <see cref="ReceiverBrowserOptions.Endpoint"/> (the standard mDNS endpoint unless a
/// test passes a private one);
/// and, when <see cref="ReceiverBrowserOptions.UseLegacyUnicastQueries"/> is set, one ephemeral-port socket per local
/// address (IPv4) or interface (IPv6) that sends the same queries as legacy unicast queries, as pyatv does with one
/// socket per local address plus one on 5353 [pyatv:pyatv/core/mdns.py:506-531]. The second path is the fallback for
/// reflectors: an mDNS proxy (for example a router reflecting mDNS between VLANs) answers for receivers on other subnets.</para>
/// <para>Queries: PTR for <c>_airplay._tcp.local</c> and <c>_raop._tcp.local</c> 20-120 ms after start (RFC 6762
/// section 5.2), then after 1 s and 3 s, then at the constant <see cref="ReceiverBrowserOptions.QueryInterval"/> (the
/// scaffold contract; RFC 6762 5.2 asks continuous queriers to keep doubling the interval up to a cap, a deviation left
/// to the plan owner); SRV/TXT/A/AAAA follow-ups when an answer is incomplete; refresh queries at 80/85/90/95 % of a
/// record's TTL. The cache rules (TTL, goodbye, cache flush, merge) live in <see cref="ReceiverCache"/>.</para>
/// <para>Addresses come only from A/AAAA records; the UDP source address is logged but never used.</para>
/// </remarks>
public sealed class MdnsReceiverBrowser : IReceiverBrowser
{
    /// <summary>The mDNS port (of <see cref="MdnsEndpoint.Default"/>).</summary>
    public const int MdnsPort = 5353;

    /// <summary>The IPv4 mDNS group (of <see cref="MdnsEndpoint.Default"/>).</summary>
    public static readonly IPAddress GroupV4 = MdnsEndpoint.Default.GroupV4;

    /// <summary>The IPv6 mDNS group (of <see cref="MdnsEndpoint.Default"/>).</summary>
    public static readonly IPAddress GroupV6 = MdnsEndpoint.Default.GroupV6;

    // Windows: SIO_UDP_CONNRESET, so an ICMP port-unreachable does not fail the next receive (same as librespot's responder).
    private const int SioUdpConnReset = -1744830452;

    // RFC 6762 section 17: an mDNS message may be up to 9000 bytes; a jumbo-frame LAN can carry more, so be generous.
    private const int ReceiveBufferSize = 65536;

    // Questions per follow-up message, so a burst of incomplete answers never builds an oversized query.
    private const int MaxQuestionsPerMessage = 24;

    // The first delay is replaced by a random 20-120 ms (RFC 6762 5.2), so queriers started together do not synchronise.
    private static readonly TimeSpan[] InitialQueryDelays = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly Channel<ReceiverEvent> _events = Channel.CreateBounded<ReceiverEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly TimeProvider _time;
    private readonly ReceiverCache _cache;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<MdnsSocket> _sockets = [];
    private readonly List<Task> _loops = [];
    private readonly Lock _socketsGate = new();

    private IReadOnlyList<DiscoveredReceiver> _snapshot = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    private int _disposed;
    private int _interfacesDirty;

    /// <summary>Creates a browser.</summary>
    public MdnsReceiverBrowser(ReceiverBrowserOptions options, ILogger<MdnsReceiverBrowser> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    /// <summary>Creates a browser with an explicit clock (tests use a fake one for TTL expiry).</summary>
    internal MdnsReceiverBrowser(ReceiverBrowserOptions options, ILogger<MdnsReceiverBrowser> logger, TimeProvider time)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.Endpoint, nameof(options));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        if (options.QueryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "QueryInterval must be positive");
        }

        _cache = new ReceiverCache(options.EnableIPv6, options.ExpiryGrace, LegacyTtlFloorFor(options), logger);
    }

    /// <summary>The options.</summary>
    public ReceiverBrowserOptions Options { get; }

    /// <inheritdoc />
    public ChannelReader<ReceiverEvent> Events => _events.Reader;

    private ILogger Logger { get; }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredReceiver> Snapshot() => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            BindSockets();
            string[] bound;
            lock (_socketsGate)
            {
                bound = [.. _sockets.Select(s => s.Description)];
            }

            if (bound.Length == 0)
            {
                throw new InvalidOperationException($"mDNS browser: no socket could be bound (no usable interface, or port {Options.Endpoint.Port} and every ephemeral bind failed); see the log for details.");
            }

            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _started = true;

            var token = _shutdown.Token;
            lock (_loops)
            {
                _loops.Add(QueryLoopAsync(token));
                _loops.Add(SweepLoopAsync(token));
            }

            Logger.LogInformation("mDNS browser started on {Count} socket(s): {Sockets}", bound.Length, string.Join(", ", bound));
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RefreshAsync(TimeSpan settle, CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _cache.ResetFollowUps();
        }

        await SendBrowseQueriesAsync(cancellationToken).ConfigureAwait(false);
        if (settle > TimeSpan.Zero)
        {
            await Task.Delay(settle, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<DiscoveredReceiver?> FindAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await StartAsync(cancellationToken).ConfigureAwait(false);

        var deadline = _time.GetUtcNow() + timeout;
        var queried = false;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (Match(_snapshot, id) is { } hit)
                {
                    return hit;
                }

                changed = _changed.Task;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return null;
            }

            if (!queried)
            {
                // Ask once right away rather than waiting for the next periodic query.
                queried = true;
                await SendBrowseQueriesAsync(cancellationToken).ConfigureAwait(false);
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            try
            {
                await changed.WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return Match(Snapshot(), id);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        List<MdnsSocket> sockets;
        lock (_socketsGate)
        {
            sockets = [.. _sockets];
            _sockets.Clear();
        }

        foreach (var socket in sockets)
        {
            socket.Dispose();
        }

        Task[] loops;
        lock (_loops)
        {
            loops = [.. _loops];
        }

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            Logger.LogDebug("mDNS browser loops stopped with {Error}", e.Message);
        }

        _events.Writer.TryComplete();
        lock (_gate)
        {
            _changed.TrySetResult();
        }

        _shutdown.Dispose();
        _startLock.Dispose();
    }

    /// <summary>Matches <paramref name="id"/> against device ids (colons optional), then names.</summary>
    internal static DiscoveredReceiver? Match(IReadOnlyList<DiscoveredReceiver> receivers, string id)
    {
        var compact = id.Replace(":", string.Empty, StringComparison.Ordinal);
        return receivers.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(r.Id.Replace(":", string.Empty, StringComparison.Ordinal), compact, StringComparison.OrdinalIgnoreCase))
               ?? receivers.FirstOrDefault(r => string.Equals(r.Name, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The PTR query for both service types.</summary>
    internal static DnsMessage BrowseQuery()
    {
        var query = new DnsMessage();
        query.Questions.Add(new DnsQuestion(ReceiverCache.AirplayType, DnsType.Ptr));
        query.Questions.Add(new DnsQuestion(ReceiverCache.RaopType, DnsType.Ptr));
        return query;
    }

    /// <summary>The TTL floor for legacy unicast answers: two query intervals, so one lost answer does not drop a receiver.</summary>
    internal static TimeSpan LegacyTtlFloorFor(ReceiverBrowserOptions options) => options.QueryInterval * 2;

    /// <summary>
    /// Feeds one received datagram (the receive loops call this; tests call it directly). Returns the follow-up questions
    /// the caller should send.
    /// </summary>
    internal IReadOnlyList<DnsQuestion> Ingest(ReadOnlySpan<byte> packet, EndPoint from, int interfaceIndex, bool legacyUnicast)
    {
        // Cheap filter before parsing: most mDNS traffic on a LAN is queries (QR bit clear).
        if (packet.Length < 12 || (packet[2] & 0x80) == 0)
        {
            return [];
        }

        if (!DnsMessage.TryParse(packet, out var message, out var error))
        {
            Logger.LogDebug("Ignoring malformed mDNS packet from {From}: {Error}", from, error);
            return [];
        }

        lock (_gate)
        {
            var update = _cache.Apply(message, interfaceIndex, legacyUnicast, _time.GetUtcNow());
            if (update.Events.Count > 0 || update.Changed)
            {
                Logger.LogDebug("mDNS answer from {From} on interface {Interface}{Legacy} changed the receiver set", from, interfaceIndex, legacyUnicast ? " (legacy unicast)" : string.Empty);
            }

            Publish(update);
            return update.Questions;
        }
    }

    /// <summary>Expires records and returns the refresh/follow-up questions to send (the sweep loop calls this every second).</summary>
    internal IReadOnlyList<DnsQuestion> Sweep()
    {
        lock (_gate)
        {
            var update = _cache.Sweep(_time.GetUtcNow());
            Publish(update);
            return update.Questions;
        }
    }

    private void Publish(CacheUpdate update)
    {
        foreach (var e in update.Events)
        {
            switch (e)
            {
                case ReceiverEvent.Found found:
                    Logger.LogInformation(
                        "Found receiver {Name} ({Id}) at {Addresses} port {Port}",
                        found.Receiver.Name, found.Receiver.Id, string.Join(", ", found.Receiver.Addresses), found.Receiver.Port);
                    break;
                case ReceiverEvent.Updated updated:
                    Logger.LogInformation("Receiver {Name} ({Id}) updated", updated.Receiver.Name, updated.Receiver.Id);
                    break;
                case ReceiverEvent.Lost lost:
                    Logger.LogInformation("Lost receiver {Name} ({Id})", lost.Receiver.Name, lost.Receiver.Id);
                    break;
            }

            _events.Writer.TryWrite(e);
        }

        if (update.Changed || update.Events.Count > 0)
        {
            Volatile.Write(ref _snapshot, _cache.Current);
            var previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    private async Task QueryLoopAsync(CancellationToken cancellationToken)
    {
        var round = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = round == 0 ? TimeSpan.FromMilliseconds(Random.Shared.Next(20, 121))
                : round < InitialQueryDelays.Length ? InitialQueryDelays[round]
                : Options.QueryInterval;
            round++;
            if (delay > TimeSpan.Zero && !await WaitAsync(delay, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (Interlocked.Exchange(ref _interfacesDirty, 0) != 0 || round > InitialQueryDelays.Length)
                {
                    RebindIfInterfacesChanged();
                }

                lock (_gate)
                {
                    _cache.ResetFollowUps();
                }

                await SendBrowseQueriesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is SocketException or NetworkInformationException or ObjectDisposedException)
            {
                Logger.LogWarning("mDNS browse query round failed: {Error}", e.Message);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                // Anything unexpected is logged and the next round (one query interval later) tries again.
                Logger.LogError(e, "mDNS browse query round failed unexpectedly");
            }
        }
    }

    /// <summary>Waits <paramref name="delay"/> or until a network change wakes the loop; false when shutting down.</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task wake;
        lock (_gate)
        {
            wake = _wake.Task;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sleep = Task.Delay(delay, _time, timer.Token);
        await Task.WhenAny(sleep, wake).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        try
        {
            await sleep.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // woken early, or shutting down
        }

        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return !cancellationToken.IsCancellationRequested;
    }

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var questions = Sweep();
                    if (questions.Count > 0)
                    {
                        await SendQuestionsAsync(questions, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    // One failed sweep must not end expiry and refreshes; the next tick tries again.
                    Logger.LogError(e, "mDNS cache sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private Task SendBrowseQueriesAsync(CancellationToken cancellationToken)
    {
        Logger.LogDebug("mDNS browse: PTR {Airplay}, PTR {Raop}", AirplayServiceTypes.Airplay, AirplayServiceTypes.Raop);
        return SendAsync(BrowseQuery().Encode(), cancellationToken);
    }

    private async Task SendQuestionsAsync(IReadOnlyList<DnsQuestion> questions, CancellationToken cancellationToken)
    {
        foreach (var chunk in questions.Chunk(MaxQuestionsPerMessage))
        {
            var query = new DnsMessage();
            query.Questions.AddRange(chunk);
            Logger.LogDebug("mDNS follow-up: {Questions}", string.Join(", ", chunk.Select(q => $"{q.Type} {q.Name}")));
            await SendAsync(query.Encode(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        MdnsSocket[] sockets;
        lock (_socketsGate)
        {
            sockets = [.. _sockets];
        }

        foreach (var socket in sockets)
        {
            await socket.SendQueryAsync(packet, Logger, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(MdnsSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        var family = socket.Socket.AddressFamily;
        EndPoint any = new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        while (!cancellationToken.IsCancellationRequested && !socket.IsDisposed)
        {
            int length;
            EndPoint from;
            int interfaceIndex;
            try
            {
                if (socket.IsListener)
                {
                    var received = await socket.Socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                    (length, from, interfaceIndex) = (received.ReceivedBytes, received.RemoteEndPoint, received.PacketInformation.Interface);
                }
                else
                {
                    var received = await socket.Socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                    (length, from, interfaceIndex) = (received.ReceivedBytes, received.RemoteEndPoint, socket.InterfaceIndex);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e) when (socket.IsDisposed)
            {
                Logger.LogDebug("mDNS socket {Socket} closed: {Error}", socket.Description, e.Message);
                return;
            }
            catch (SocketException e)
            {
                Logger.LogWarning("mDNS receive on {Socket} failed: {Error}", socket.Description, e.Message);
                try
                {
                    // Keep a persistently failing socket from spinning.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            IReadOnlyList<DnsQuestion> questions;
            try
            {
                questions = Ingest(buffer.AsSpan(0, length), from, interfaceIndex, legacyUnicast: !socket.IsListener);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
            {
                // A record the codec accepted but the TXT/feature parsing could not digest must not end the loop.
                Logger.LogWarning("mDNS packet from {From} could not be processed: {Error}", from, e.Message);
                continue;
            }
            catch (Exception e)
            {
                // Anything unexpected: logged, and this socket keeps receiving.
                Logger.LogError(e, "mDNS packet from {From} on {Socket} could not be processed", from, socket.Description);
                continue;
            }

            if (questions.Count > 0)
            {
                try
                {
                    await SendQuestionsAsync(questions, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "mDNS follow-up query on {Socket} failed", socket.Description);
                }
            }
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _interfacesDirty, 1);
        lock (_gate)
        {
            _wake.TrySetResult();
        }
    }

    // ---- sockets ---------------------------------------------------------------------------------------------------

    private void BindSockets()
    {
        var interfaces = SelectInterfaces();
        foreach (var family in Families())
        {
            var ofFamily = interfaces.Where(i => i.Address.AddressFamily == family).ToList();
            if (ofFamily.Count == 0)
            {
                Logger.LogDebug("mDNS browser: no {Family} interface selected", family);
                continue;
            }

            TryAdd(() => MdnsSocket.BindListener(family, ofFamily, Options.Endpoint, Logger), $"{family} listener on port {Options.Endpoint.Port}");
            if (Options.UseLegacyUnicastQueries)
            {
                foreach (var iface in Queriers(ofFamily))
                {
                    TryAdd(() => MdnsSocket.BindQuerier(iface, Options.Endpoint), $"querier on {iface.Address}");
                }
            }
        }
    }

    private void RebindIfInterfacesChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var interfaces = SelectInterfaces();
        var wanted = Options.UseLegacyUnicastQueries
            ? Families().SelectMany(f => Queriers(interfaces.Where(i => i.Address.AddressFamily == f).ToList())).ToList()
            : [];
        List<MdnsSocket> stale;
        List<LocalInterface> missingQueriers;
        List<AddressFamily> missingListeners;
        lock (_socketsGate)
        {
            // Listeners: join the group on new interfaces.
            foreach (var listener in _sockets.Where(s => s.IsListener))
            {
                listener.JoinMissing(interfaces.Where(i => i.Address.AddressFamily == listener.Socket.AddressFamily), Logger);
            }

            // Queriers: close those whose address vanished, open those for new addresses.
            stale = [.. _sockets.Where(s => !s.IsListener && !wanted.Any(w => w.Matches(s)))];
            foreach (var s in stale)
            {
                _sockets.Remove(s);
            }

            missingQueriers = [.. wanted.Where(w => !_sockets.Any(s => !s.IsListener && w.Matches(s)))];

            // A family whose listener could not bind earlier (no interface yet): try again.
            missingListeners = [.. Families().Where(f => !_sockets.Any(s => s.IsListener && s.Socket.AddressFamily == f))];
        }

        foreach (var s in stale)
        {
            Logger.LogInformation("mDNS browser: closing {Socket} (address gone)", s.Description);
            s.Dispose();
        }

        foreach (var w in missingQueriers)
        {
            TryAdd(() => MdnsSocket.BindQuerier(w, Options.Endpoint), $"querier on {w.Address}");
        }

        foreach (var family in missingListeners)
        {
            var ofFamily = interfaces.Where(i => i.Address.AddressFamily == family).ToList();
            if (ofFamily.Count > 0)
            {
                TryAdd(() => MdnsSocket.BindListener(family, ofFamily, Options.Endpoint, Logger), $"{family} listener on port {Options.Endpoint.Port}");
            }
        }
    }

    private void TryAdd(Func<MdnsSocket> bind, string what)
    {
        MdnsSocket socket;
        try
        {
            socket = bind();
        }
        catch (Exception e) when (e is SocketException or NetworkInformationException or PlatformNotSupportedException)
        {
            Logger.LogWarning("mDNS browser: could not bind {What}: {Error}", what, e.Message);
            return;
        }

        lock (_socketsGate)
        {
            _sockets.Add(socket);
        }

        if (!_shutdown.IsCancellationRequested)
        {
            var loop = ReceiveLoopAsync(socket, _shutdown.Token);
            lock (_loops)
            {
                _loops.RemoveAll(t => t.IsCompleted);
                _loops.Add(loop);
            }
        }
    }

    private IEnumerable<AddressFamily> Families() =>
        Options.EnableIPv6 && Socket.OSSupportsIPv6 ? [AddressFamily.InterNetwork, AddressFamily.InterNetworkV6] : [AddressFamily.InterNetwork];

    /// <summary>One querier per IPv4 address, one per IPv6 interface (bound to the link-local address when it has one).</summary>
    private static IEnumerable<LocalInterface> Queriers(List<LocalInterface> ofFamily) =>
        ofFamily.Count == 0 || ofFamily[0].Address.AddressFamily == AddressFamily.InterNetwork
            ? ofFamily.DistinctBy(i => i.Address)
            : ofFamily.GroupBy(i => i.Index).Select(g => g.OrderByDescending(i => i.Address.IsIPv6LinkLocal).First());

    /// <summary>Up, multicast-capable, non-loopback interfaces and their addresses, limited to <see cref="ReceiverBrowserOptions.LocalAddresses"/>.</summary>
    private List<LocalInterface> SelectInterfaces()
    {
        var result = new List<LocalInterface>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException e)
        {
            Logger.LogWarning("mDNS browser: could not list network interfaces: {Error}", e.Message);
            return result;
        }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || !nic.SupportsMulticast)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            // With LocalAddresses, an interface is selected when one of its addresses (either family) is listed; its
            // IPv4 sockets then use the listed IPv4 addresses (all of them when only an IPv6 address was listed).
            if (Options.LocalAddresses.Count > 0
                && !props.UnicastAddresses.Any(u => Options.LocalAddresses.Any(l => SameAddress(l, u.Address))))
            {
                continue;
            }

            foreach (var family in Families())
            {
                int index;
                try
                {
                    index = family == AddressFamily.InterNetwork ? props.GetIPv4Properties()?.Index ?? -1 : props.GetIPv6Properties()?.Index ?? -1;
                }
                catch (NetworkInformationException)
                {
                    continue; // the protocol is not enabled on this interface
                }

                if (index < 0)
                {
                    continue;
                }

                var addresses = props.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == family).ToList();
                if (Options.LocalAddresses.Count > 0 && family == AddressFamily.InterNetwork)
                {
                    var listed = addresses.Where(a => Options.LocalAddresses.Any(l => SameAddress(l, a))).ToList();
                    if (listed.Count > 0)
                    {
                        addresses = listed;
                    }
                }

                result.AddRange(addresses.Select(a => new LocalInterface(index, a, nic.Name)));
            }
        }

        return result;
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

    /// <summary>A local interface address.</summary>
    private sealed record LocalInterface(int Index, IPAddress Address, string Name)
    {
        public bool Matches(MdnsSocket socket) => socket.InterfaceIndex == Index && socket.LocalAddress is { } local && SameAddress(local, Address);
    }

    /// <summary>One UDP socket of the browser and how to send a query on it.</summary>
    private sealed class MdnsSocket : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        // Joined interfaces: index -> the IPv4 address used for IP_ADD_MEMBERSHIP / IP_MULTICAST_IF (null for IPv6, which
        // takes the index). The address form works on Windows and Linux alike; an index in IP_MULTICAST_IF does not.
        private readonly Dictionary<int, IPAddress?> _joined = [];
        private int _disposed;

        // The querier's description; a listener's is computed from the joined interfaces.
        private readonly string _description;

        // The group and port the queries go to (the listener also joins the group and is bound to the port).
        private readonly MdnsEndpoint _endpoint;

        private MdnsSocket(Socket socket, bool isListener, int interfaceIndex, IPAddress? localAddress, string description, MdnsEndpoint endpoint)
        {
            Socket = socket;
            IsListener = isListener;
            InterfaceIndex = interfaceIndex;
            LocalAddress = localAddress;
            _description = description;
            _endpoint = endpoint;
        }

        public Socket Socket { get; }

        /// <summary>True for the multicast socket on the mDNS port (5353); false for an ephemeral legacy-unicast querier.</summary>
        public bool IsListener { get; }

        /// <summary>The interface of a querier (0 for a listener, whose packets carry their own interface).</summary>
        public int InterfaceIndex { get; }

        public IPAddress? LocalAddress { get; }

        /// <summary>For logs: the address and port of a querier, or the port and joined interfaces of a listener.</summary>
        public string Description
        {
            get
            {
                if (!IsListener)
                {
                    return _description;
                }

                int[] joined;
                lock (_joined)
                {
                    joined = [.. _joined.Keys.Order()];
                }

                return $"{_description} (interfaces {string.Join(',', joined)})";
            }
        }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        private IPAddress Group => _endpoint.GroupOf(Socket.AddressFamily);

        /// <summary>Port 5353, <c>ReuseAddress</c>, group joined per interface (librespot's responder binds the same way).</summary>
        public static MdnsSocket BindListener(AddressFamily family, IEnumerable<LocalInterface> interfaces, MdnsEndpoint endpoint, ILogger logger)
        {
            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                Prepare(socket);
                var v6 = family == AddressFamily.InterNetworkV6;
                if (v6)
                {
                    socket.DualMode = false;
                }

                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, endpoint.Port));
                var level = v6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;
                socket.SetSocketOption(level, SocketOptionName.PacketInformation, true);
                socket.SetSocketOption(level, SocketOptionName.MulticastTimeToLive, endpoint.HopLimit);
                socket.SetSocketOption(level, SocketOptionName.MulticastLoopback, true);

                var result = new MdnsSocket(socket, isListener: true, 0, null, $"{(v6 ? "IPv6" : "IPv4")} :{endpoint.Port}", endpoint);
                result.JoinMissing(interfaces, logger);
                if (result._joined.Count == 0)
                {
                    throw new SocketException((int)SocketError.AddressNotAvailable);
                }

                return result;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>An ephemeral-port socket on one local address whose queries are legacy unicast queries (RFC 6762 6.7).</summary>
        public static MdnsSocket BindQuerier(LocalInterface iface, MdnsEndpoint endpoint)
        {
            var family = iface.Address.AddressFamily;
            var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                Prepare(socket);
                if (family == AddressFamily.InterNetwork)
                {
                    socket.Bind(new IPEndPoint(iface.Address, 0));
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, iface.Address.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, endpoint.HopLimit);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                }
                else
                {
                    socket.DualMode = false;
                    socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, iface.Index);
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, endpoint.HopLimit);
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, true);
                }

                return new MdnsSocket(socket, isListener: false, iface.Index, iface.Address, $"{iface.Address}:{((IPEndPoint)socket.LocalEndPoint!).Port} ({iface.Name})", endpoint);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>Joins the group on interfaces not joined yet; a failure on one interface is logged and skipped.</summary>
        public void JoinMissing(IEnumerable<LocalInterface> interfaces, ILogger logger)
        {
            foreach (var iface in interfaces.DistinctBy(i => i.Index).ToList())
            {
                lock (_joined)
                {
                    if (_joined.ContainsKey(iface.Index))
                    {
                        continue;
                    }
                }

                try
                {
                    IPAddress? v4 = null;
                    if (Socket.AddressFamily == AddressFamily.InterNetwork)
                    {
                        v4 = iface.Address;
                        Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, v4));
                    }
                    else
                    {
                        Socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(Group, iface.Index));
                    }

                    lock (_joined)
                    {
                        _joined[iface.Index] = v4;
                    }
                }
                catch (SocketException e)
                {
                    logger.LogWarning("mDNS browser: could not join {Group} on interface {Index} ({Name}): {Error}", Group, iface.Index, iface.Name, e.Message);
                }
            }
        }

        /// <summary>Sends <paramref name="packet"/> to the group: once per joined interface for a listener, once for a querier.</summary>
        public async Task SendQueryAsync(byte[] packet, ILogger logger, CancellationToken cancellationToken)
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                var to = _endpoint.GroupEndPoint(Socket.AddressFamily);
                if (!IsListener)
                {
                    await Socket.SendToAsync(packet, SocketFlags.None, to, cancellationToken).ConfigureAwait(false);
                    return;
                }

                KeyValuePair<int, IPAddress?>[] joined;
                lock (_joined)
                {
                    joined = [.. _joined];
                }

                foreach (var (index, v4) in joined)
                {
                    try
                    {
                        if (v4 is not null)
                        {
                            Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, v4.GetAddressBytes());
                        }
                        else
                        {
                            Socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                        }

                        await Socket.SendToAsync(packet, SocketFlags.None, to, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException e)
                    {
                        logger.LogDebug("mDNS send on interface {Index} failed: {Error}", index, e.Message);
                    }
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                logger.LogDebug("mDNS send on {Socket} failed: {Error}", Description, e.Message);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Socket.Dispose();
        }

        private static void Prepare(Socket socket)
        {
            socket.ExclusiveAddressUse = false;
            if (OperatingSystem.IsWindows())
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
        }
    }
}
