using System.Net;
using System.Net.Sockets;
using Airplay2.Receiver.Discovery;
using Airplay2.Sender.Discovery;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Receiver.Tests.Discovery;

/// <summary>
/// <see cref="ReceiverAdvertiser"/> against our own <see cref="MdnsReceiverBrowser"/> (SPEC-receiver.md 3.1, 3.3, 11.1): real
/// sockets on a private <see cref="MdnsEndpoint"/> (an administratively scoped IPv4 group, a site-local IPv6 group, a port
/// nobody listens on, hop limit 1), so nothing reaches the LAN's mDNS, as in the sender's
/// <c>ReceiverBrowserLoopbackTests</c>. The advertised receiver has a fresh synthetic identity.
/// </summary>
public class ReceiverAdvertiserLoopbackTests(ITestOutputHelper output)
{
    /// <summary>A private group on its own port (the sender tests use 53530-53533).</summary>
    private static readonly MdnsEndpoint Private = new(IPAddress.Parse("239.255.0.251"), IPAddress.Parse("ff05::1:fb"), 53541, HopLimit: 1);

    [Fact]
    public async Task Browser_discovers_the_receiver_follows_a_rename_and_loses_it_on_stop()
    {
        var identity = ReceiverIdentity.Create();
        var suffix = identity.DeviceIdCompact[^4..];
        var options = new AirplayReceiverOptions { Identity = identity, Name = "ap2 receiver loopback " + suffix, RtspPort = 0 };
        const int port = 47000; // advertised only; nothing listens or connects

        await using var advertiser = new ReceiverAdvertiser([], NullLogger.Instance, Private, "ap2-receiver-loopback-" + suffix.ToLowerInvariant());
        try
        {
            await advertiser.StartAsync(ReceiverTxtRecords.Advertisement(options, port, ReceiverFeatures.Default), CancellationToken.None);
        }
        catch (SocketException e)
        {
            output.WriteLine($"mDNS responder could not bind ({e.Message}); skipping");
            return;
        }

        Assert.True(advertiser.IsRunning);
        await using var browser = new MdnsReceiverBrowser(
            new ReceiverBrowserOptions { QueryInterval = TimeSpan.FromSeconds(2), ExpiryGrace = TimeSpan.FromSeconds(1), Endpoint = Private },
            NullLogger<MdnsReceiverBrowser>.Instance);
        try
        {
            await browser.StartAsync(CancellationToken.None);
        }
        catch (InvalidOperationException e)
        {
            output.WriteLine($"no usable interface ({e.Message}); skipping");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        // Discovered with both services merged and the R15 word, transient pairing (flags 0x4).
        var found = await WaitForAsync(browser, r => r.Id == identity.DeviceId && r.Raop is not null, timeout.Token);
        Assert.Equal(options.Name, found.Name);
        Assert.Equal(port, found.Port);
        Assert.Equal((AirplayFeatures)ReceiverFeatures.Default, found.Airplay.Features);
        Assert.Equal(identity.PublicKey, found.Airplay.PublicKey);
        Assert.Equal(new[] { 0, 1, 2 }, found.Raop!.Codecs);
        Assert.Equal(PairingRequirement.Transient, found.Capabilities.Pairing);
        Assert.False(found.Airplay.Flags.HasFlag(AirplayStatusFlags.PasswordRequired));
        Assert.NotEmpty(found.Addresses);

        // Rename and password: goodbye, then announce (3.3); the browser follows the new name.
        var renamed = options with { Name = "ap2 receiver renamed " + suffix, Password = "loopback" };
        await advertiser.UpdateAsync(ReceiverTxtRecords.Advertisement(renamed, port, ReceiverFeatures.Default), CancellationToken.None);
        Assert.Equal(renamed.Name, advertiser.Current?.Name);
        var followed = await WaitForAsync(browser, r => r.Id == identity.DeviceId && r.Name == renamed.Name && r.Airplay.Flags.HasFlag(AirplayStatusFlags.PasswordRequired) && r.Raop?.PasswordRequired == true, timeout.Token);
        Assert.Equal(renamed.Name, followed.Name);
        Assert.Equal(PairingRequirement.Password, followed.Capabilities.Pairing);

        // Stop: goodbye records, the receiver disappears without waiting for the TTL.
        await advertiser.StopAsync(CancellationToken.None);
        Assert.False(advertiser.IsRunning);
        while (browser.Snapshot().Any(r => r.Id == identity.DeviceId))
        {
            await Task.Delay(100, timeout.Token);
        }

        // Stop is idempotent; Update while stopped is refused.
        await advertiser.StopAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => advertiser.UpdateAsync(ReceiverTxtRecords.Advertisement(options, port, ReceiverFeatures.Default), CancellationToken.None));
    }

    /// <summary>Waits until the browser's snapshot holds a receiver matching <paramref name="match"/>.</summary>
    private static async Task<DiscoveredReceiver> WaitForAsync(MdnsReceiverBrowser browser, Func<DiscoveredReceiver, bool> match, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (browser.Snapshot().FirstOrDefault(match) is { } receiver)
            {
                return receiver;
            }

            // Events are only drained; the snapshot is the truth (it is published right after the events).
            while (browser.Events.TryRead(out _))
            {
            }

            await Task.Delay(100, cancellationToken);
        }
    }
}
