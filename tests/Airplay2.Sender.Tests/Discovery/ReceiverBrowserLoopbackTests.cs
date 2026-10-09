using System.Net.Sockets;
using System.Security.Cryptography;
using Airplay2.Sender.Discovery;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Discovery;

/// <summary>
/// The browser against librespot's <see cref="MdnsResponder"/> on this host: real sockets on port 5353 and the mDNS
/// group (multicast loopback). The responder advertises a made-up receiver with a random device id under both service
/// types for a few seconds and sends goodbyes when disposed.
/// </summary>
/// <remarks>
/// The responder announces on every real interface (multicast cannot be confined to loopback here), so a device named
/// "ap2 browser loopback xxxx" is visible to every AirPlay client on the LAN for the second or two the test runs; the
/// goodbyes remove it again. Keep the registration window this short.
/// </remarks>
public class ReceiverBrowserLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Browser_finds_merges_and_loses_a_responder_advertised_receiver()
    {
        var mac = RandomNumberGenerator.GetBytes(6);
        mac[0] = (byte)((mac[0] & 0xFC) | 0x02); // locally administered, unicast
        var deviceId = string.Join(':', mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        var compact = deviceId.Replace(":", string.Empty, StringComparison.Ordinal);
        var name = "ap2 browser loopback " + compact[^4..];
        var host = "ap2-loopback-" + compact.ToLowerInvariant();

        string[] airplayTxt =
        [
            "acl=0", $"deviceid={deviceId}", "features=0x445F8A00,0x1C340", "flags=0x404", "model=LoopbackTest", "srcvers=366.0",
            $"pi={deviceId}", $"gid={deviceId}", "gcgl=0", "protovers=1.1",
            "pk=d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
        ];
        string[] raopTxt = ["cn=0,1", "et=0,4", "ft=0x445F8A00,0x1C340", "md=0,1,2", "sf=0x404", "tp=UDP", "vs=366.0", "am=LoopbackTest"];

        MdnsResponder responder;
        try
        {
            responder = MdnsResponder.Spawn([], publishIpv6Addresses: true, hostname: host);
        }
        catch (SocketException e)
        {
            output.WriteLine($"mDNS responder could not bind ({e.Message}); skipping");
            return;
        }

        await using var __ = responder;
        await using var browser = new MdnsReceiverBrowser(
            new ReceiverBrowserOptions { QueryInterval = TimeSpan.FromSeconds(5), ExpiryGrace = TimeSpan.FromSeconds(1) },
            NullLogger<MdnsReceiverBrowser>.Instance);

        var airplay = await responder.RegisterAsync("_airplay._tcp", name, 7000, airplayTxt, ttl: 120);
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
        var browser = new MdnsReceiverBrowser(new ReceiverBrowserOptions(), NullLogger<MdnsReceiverBrowser>.Instance);
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

        // receivers on the LAN may have queued events; the stream ends once they are read
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in browser.Events.ReadAllAsync(timeout.Token))
        {
        }

        Assert.True(browser.Events.Completion.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => browser.StartAsync(CancellationToken.None));
    }
}
