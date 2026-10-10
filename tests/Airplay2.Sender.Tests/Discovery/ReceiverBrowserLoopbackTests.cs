using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Discovery;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Discovery;

/// <summary>
/// The browser against librespot's <see cref="MdnsResponder"/> on this host: real sockets, real multicast (loopback), the
/// same code paths as production, but on a private <see cref="MdnsEndpoint"/> instead of the mDNS group. The responder
/// advertises a made-up receiver with a random device id under both service types for a few seconds and sends goodbyes
/// when disposed.
/// </summary>
/// <remarks>
/// <para>Why a private endpoint: on the real mDNS group (224.0.0.251:5353) the responder announces on every interface,
/// and the LAN's mDNS reflector carried the made-up "ap2 browser loopback xxxx" receivers to the other VLANs, where the
/// deployed bridge stored 21 of them. A lower TTL would not have helped: mDNS wants 255 (RFC 6762 section 11) and a
/// reflector re-originates the records anyway.</para>
/// <para>The tests therefore use an administratively scoped IPv4 group (239.255.0.0/16, RFC 2365), a site-local IPv6
/// group (ff05::/16) and a port nobody listens on (5353x), with a hop limit of 1 on top. Nothing on the LAN joins those
/// groups, so no reflector or AirPlay client ever sees the test receiver; multicast loopback still delivers every packet
/// to the sockets on this host, so binding, joining, sending per interface and receiving run exactly as in production.</para>
/// </remarks>
public class ReceiverBrowserLoopbackTests(ITestOutputHelper output)
{
    /// <summary>The private endpoint of the round-trip test.</summary>
    internal static readonly MdnsEndpoint Private = PrivateEndpoint(53530);

    /// <summary>A private group on <paramref name="port"/>: 239.255.0.251 / ff05::1:fb, hop limit 1.</summary>
    internal static MdnsEndpoint PrivateEndpoint(int port) =>
        new(IPAddress.Parse("239.255.0.251"), IPAddress.Parse("ff05::1:fb"), port, HopLimit: 1);

    [Fact]
    public void Default_endpoint_is_the_standard_mdns_endpoint()
    {
        var endpoint = MdnsEndpoint.Default;
        Assert.Equal(IPAddress.Parse("224.0.0.251"), endpoint.GroupV4);
        Assert.Equal(IPAddress.Parse("ff02::fb"), endpoint.GroupV6);
        Assert.Equal(5353, endpoint.Port);
        Assert.Equal(255, endpoint.HopLimit);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), endpoint.GroupEndPoint(AddressFamily.InterNetwork));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("ff02::fb"), 5353), endpoint.GroupEndPoint(AddressFamily.InterNetworkV6));

        // Production callers get it without asking: the browser options and the constants both sides kept.
        Assert.Same(MdnsEndpoint.Default, new ReceiverBrowserOptions().Endpoint);
        Assert.Equal(endpoint.GroupV4, MdnsReceiverBrowser.GroupV4);
        Assert.Equal(endpoint.GroupV6, MdnsReceiverBrowser.GroupV6);
        Assert.Equal(MdnsReceiverBrowser.MdnsPort, endpoint.Port);
        Assert.Equal(endpoint.GroupV4, MdnsResponder.GroupV4);
        Assert.Equal(MdnsResponder.MdnsPort, endpoint.Port);
    }

    [Fact]
    public void Endpoint_rejects_non_multicast_groups_and_bad_ports()
    {
        Assert.Throws<ArgumentException>(() => new MdnsEndpoint(IPAddress.Parse("198.51.100.1"), IPAddress.Parse("ff02::fb"), 5353));
        Assert.Throws<ArgumentException>(() => new MdnsEndpoint(IPAddress.Parse("224.0.0.251"), IPAddress.Parse("fe80::1"), 5353));
        Assert.Throws<ArgumentException>(() => new MdnsEndpoint(IPAddress.Parse("ff02::fb"), IPAddress.Parse("ff02::fb"), 5353));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MdnsEndpoint(IPAddress.Parse("239.255.0.251"), IPAddress.Parse("ff05::1:fb"), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MdnsEndpoint(IPAddress.Parse("239.255.0.251"), IPAddress.Parse("ff05::1:fb"), 53530, HopLimit: 0));
        Assert.Equal(53530, Private.Port);
    }

    /// <summary>
    /// Both sides honour an alternate endpoint: a plain socket on the private group and port receives the responder's
    /// announcement and the browser's PTR query (so neither went to 224.0.0.251:5353 instead), and a browser on a second
    /// private port never sees a receiver announced on the first.
    /// </summary>
    [Fact]
    public async Task Alternate_endpoint_is_honoured_by_responder_and_browser()
    {
        var endpoint = PrivateEndpoint(53531);
        var (deviceId, name, host) = MadeUpReceiver();

        using var tap = TryTap(endpoint);
        if (tap is null)
        {
            output.WriteLine("no multicast-capable IPv4 interface; skipping");
            return;
        }

        MdnsResponder responder;
        try
        {
            responder = MdnsResponder.Spawn([], publishIpv6Addresses: true, hostname: host, endpoint: endpoint);
        }
        catch (SocketException e)
        {
            output.WriteLine($"mDNS responder could not bind ({e.Message}); skipping");
            return;
        }

        await using var __ = responder;
        await using var browser = new MdnsReceiverBrowser(
            new ReceiverBrowserOptions { QueryInterval = TimeSpan.FromSeconds(5), ExpiryGrace = TimeSpan.FromSeconds(1), Endpoint = endpoint },
            NullLogger<MdnsReceiverBrowser>.Instance);
        await using var elsewhere = new MdnsReceiverBrowser(
            new ReceiverBrowserOptions { QueryInterval = TimeSpan.FromSeconds(5), Endpoint = PrivateEndpoint(53532) },
            NullLogger<MdnsReceiverBrowser>.Instance);

        await using var airplay = await responder.RegisterAsync("_airplay._tcp", name, 7000, AirplayTxt(deviceId), ttl: 120);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The responder's announcement arrives on the private group and port.
        var announcement = await tap.WaitAsync(m => m.IsResponse && m.Answers.Any(a => a.Name.Labels.Count > 0 && a.Name.Labels[0] == name), timeout.Token);
        Assert.Contains(announcement.Answers, a => a.Data is DnsRecordData.Srv { Port: 7000 });

        try
        {
            await browser.StartAsync(CancellationToken.None);
            await elsewhere.StartAsync(CancellationToken.None);
        }
        catch (InvalidOperationException e)
        {
            output.WriteLine($"no usable interface ({e.Message}); skipping");
            return;
        }

        // The browser's PTR query arrives there too.
        var query = await tap.WaitAsync(m => !m.IsResponse && m.Questions.Any(q => q.Type == DnsType.Ptr && q.Name.Equals(DnsName.Parse(AirplayServiceTypes.Airplay))), timeout.Token);
        Assert.NotEmpty(query.Questions);

        // Round trip on the shared endpoint; nothing on the other one.
        var found = await browser.FindAsync(deviceId, TimeSpan.FromSeconds(15), timeout.Token);
        Assert.Equal(name, found?.Name);
        Assert.Null(await elsewhere.FindAsync(deviceId, TimeSpan.FromSeconds(2), timeout.Token));
    }

    [Fact]
    public async Task Browser_finds_merges_and_loses_a_responder_advertised_receiver()
    {
        var (deviceId, name, host) = MadeUpReceiver();
        var compact = deviceId.Replace(":", string.Empty, StringComparison.Ordinal);
        string[] raopTxt = ["cn=0,1", "et=0,4", "ft=0x445F8A00,0x1C340", "md=0,1,2", "sf=0x404", "tp=UDP", "vs=366.0", "am=LoopbackTest"];

        MdnsResponder responder;
        try
        {
            responder = MdnsResponder.Spawn([], publishIpv6Addresses: true, hostname: host, endpoint: Private);
        }
        catch (SocketException e)
        {
            output.WriteLine($"mDNS responder could not bind ({e.Message}); skipping");
            return;
        }

        await using var __ = responder;
        await using var browser = new MdnsReceiverBrowser(
            new ReceiverBrowserOptions { QueryInterval = TimeSpan.FromSeconds(5), ExpiryGrace = TimeSpan.FromSeconds(1), Endpoint = Private },
            NullLogger<MdnsReceiverBrowser>.Instance);

        var airplay = await responder.RegisterAsync("_airplay._tcp", name, 7000, AirplayTxt(deviceId), ttl: 120);
        var raop = await responder.RegisterAsync("_raop._tcp", $"{compact}@{name}", 7000, raopTxt, ttl: 120);
        try
        {
            try
            {
                await browser.StartAsync(CancellationToken.None);
            }
            catch (InvalidOperationException e)
            {
                output.WriteLine($"no usable interface ({e.Message}); skipping");
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var found = await browser.FindAsync(deviceId, TimeSpan.FromSeconds(15), timeout.Token);
            Assert.NotNull(found);
            Assert.Equal(name, found!.Name);
            Assert.Equal(deviceId, found.Id);
            Assert.Equal(7000, found.Port);
            Assert.NotEmpty(found.Addresses);
            output.WriteLine($"found {found.Name} at {string.Join(", ", found.Addresses)}");

            // by name, too
            Assert.Equal(deviceId, (await browser.FindAsync(name, TimeSpan.FromSeconds(1), timeout.Token))?.Id);

            // the _raop record is merged (it may arrive in the same answer or a moment later)
            var merged = found;
            while (merged.Raop is null)
            {
                var e = await browser.Events.ReadAsync(timeout.Token);
                if (e is ReceiverEvent.Updated u && u.Receiver.Id == deviceId)
                {
                    merged = u.Receiver;
                }
            }

            Assert.Equal(new[] { 0, 1 }, merged.Raop!.Codecs);
            Assert.True(merged.Capabilities.IsAirPlay2);
            Assert.Equal(PairingRequirement.Transient, merged.Capabilities.Pairing);
        }
        finally
        {
            await raop.DisposeAsync();
            await airplay.DisposeAsync();
        }

        // goodbye (TTL 0) -> Lost without waiting for the TTL
        using var lostTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var e = await browser.Events.ReadAsync(lostTimeout.Token);
            if (e is ReceiverEvent.Lost lost && lost.Receiver.Id == deviceId)
            {
                break;
            }
        }

        Assert.Null(MdnsReceiverBrowser.Match(browser.Snapshot(), deviceId));
    }

    [Fact]
    public async Task Start_is_idempotent_and_dispose_completes_the_event_stream()
    {
        // On the private endpoint as well: the queries of this test stay off the LAN's mDNS.
        var browser = new MdnsReceiverBrowser(new ReceiverBrowserOptions { Endpoint = PrivateEndpoint(53533) }, NullLogger<MdnsReceiverBrowser>.Instance);
        try
        {
            await browser.StartAsync(CancellationToken.None);
            await browser.StartAsync(CancellationToken.None);
        }
        catch (InvalidOperationException e)
        {
            output.WriteLine($"no usable interface ({e.Message}); skipping");
        }

        await browser.DisposeAsync();
        await browser.DisposeAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in browser.Events.ReadAllAsync(timeout.Token))
        {
        }

        Assert.True(browser.Events.Completion.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => browser.StartAsync(CancellationToken.None));
    }

    /// <summary>A random, locally administered device id, the instance name "ap2 browser loopback xxxx" and a host name.</summary>
    private static (string DeviceId, string Name, string Host) MadeUpReceiver()
    {
        var mac = RandomNumberGenerator.GetBytes(6);
        mac[0] = (byte)((mac[0] & 0xFC) | 0x02); // locally administered, unicast
        var deviceId = string.Join(':', mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        var compact = deviceId.Replace(":", string.Empty, StringComparison.Ordinal);
        return (deviceId, "ap2 browser loopback " + compact[^4..], "ap2-loopback-" + compact.ToLowerInvariant());
    }

    private static string[] AirplayTxt(string deviceId) =>
    [
        "acl=0", $"deviceid={deviceId}", "features=0x445F8A00,0x1C340", "flags=0x404", "model=LoopbackTest", "srcvers=366.0",
        $"pi={deviceId}", $"gid={deviceId}", "gcgl=0", "protovers=1.1",
        "pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
    ];

    /// <summary>A plain IPv4 socket on the endpoint's port that joined its group on every up interface; null without one.</summary>
    private static Tap? TryTap(MdnsEndpoint endpoint)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, endpoint.Port));
            var joined = 0;
            foreach (var address in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                         .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                         .Select(u => u.Address)
                         .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(endpoint.GroupV4, address));
                    joined++;
                }
                catch (SocketException)
                {
                    // an interface that refuses the join; the others still count
                }
            }

            if (joined == 0)
            {
                socket.Dispose();
                return null;
            }

            return new Tap(socket);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>Reads mDNS messages from a socket until one matches.</summary>
    private sealed class Tap(Socket socket) : IDisposable
    {
        private readonly byte[] _buffer = new byte[65536];

        public async Task<DnsMessage> WaitAsync(Func<DnsMessage, bool> match, CancellationToken cancellationToken)
        {
            while (true)
            {
                var received = await socket.ReceiveFromAsync(_buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
                if (DnsMessage.TryParse(_buffer.AsSpan(0, received.ReceivedBytes), out var message, out _) && match(message))
                {
                    return message;
                }
            }
        }

        public void Dispose() => socket.Dispose();
    }
}
