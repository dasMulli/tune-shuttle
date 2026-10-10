#if BRIDGE_FAKE_RECEIVERS
using System.Globalization;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Discovery;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Output;

/// <summary>
/// The development switch <c>--Bridge:FakeReceivers=N</c> (WORK-PLAN.md web-app "Verify"): starts N in-process fake
/// receivers (tests/Airplay2.FakeReceiver) on loopback with stable ids <c>02:FA:CE:00:00:0n</c> and lists them in the
/// <see cref="StaticReceiverBrowser"/> that replaces mDNS, so the web app can be driven without a LAN. Compiled only into
/// Debug builds made from the full repository (the fake lives under <c>tests/</c>, which the container build excludes).
/// </summary>
public sealed class DevFakeReceivers(StaticReceiverBrowser browser, IOptions<BridgeOptions> options, ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private readonly List<FakeReceiver> _fakes = [];
    private readonly List<Task> _drains = [];
    private readonly ILogger _logger = loggerFactory.CreateLogger<DevFakeReceivers>();

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var count = Math.Clamp(options.Value.FakeReceivers, 0, 9);
        for (var i = 1; i <= count; i++)
        {
            var fake = new FakeReceiver(new FakeReceiverOptions
            {
                Name = "Fake Receiver " + i.ToString(CultureInfo.InvariantCulture),
                DeviceId = "02:FA:CE:00:00:0" + i.ToString(CultureInfo.InvariantCulture),
                Logger = loggerFactory.CreateLogger("Airplay2.FakeReceiver." + i.ToString(CultureInfo.InvariantCulture)),
            });
            await fake.StartAsync(cancellationToken).ConfigureAwait(false);
            _fakes.Add(fake);
            _drains.Add(DrainAsync(fake));
            var (airplay, raop, endpoint) = fake.Describe();
            browser.Add(new DiscoveredReceiver(fake.DeviceId, fake.Options.Name, [endpoint.Address], endpoint.Port, AirplayTxtRecord.Parse(airplay), RaopTxtRecord.Parse(raop), DateTimeOffset.UtcNow));
            _logger.LogWarning("Development: fake receiver {Name} ({Id}) listening on {Endpoint}", fake.Options.Name, fake.DeviceId, endpoint);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Development (screenshot setups, <c>POST /api/dev/fake-receivers/{id}/offline?daysAgo=N</c>): the fake leaves the
    /// static browser as if it had gone from the network <paramref name="ago"/> before now; it keeps running, so it can be
    /// forgotten or stays listed as offline. False for an id that is not a listed fake.
    /// </summary>
    public bool TakeOffline(string receiverId, TimeSpan ago)
    {
        var receiver = browser.Snapshot().FirstOrDefault(r => r.Id == receiverId);
        if (receiver is null || !_fakes.Any(f => f.DeviceId == receiverId))
        {
            return false;
        }

        // Updated first (the inventory stores the earlier last-seen), then Lost.
        browser.Add(receiver with { LastSeen = DateTimeOffset.UtcNow - ago });
        browser.Remove(receiverId);
        _logger.LogWarning("Development: fake receiver {Name} ({Id}) taken offline, last seen {Ago} ago", receiver.Name, receiverId, ago);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var fake in _fakes)
        {
            await fake.DisposeAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(_drains).ConfigureAwait(false);
        _fakes.Clear();
    }

    /// <summary>The fake records every event in an unbounded channel; read it so a long session does not grow memory.</summary>
    private async Task DrainAsync(FakeReceiver fake)
    {
        long packets = 0;
        await foreach (var fakeEvent in fake.Events.ReadAllAsync().ConfigureAwait(false))
        {
            switch (fakeEvent)
            {
                case FakeReceiverEvent.AudioPacket:
                    if (++packets % 1250 == 0)
                    {
                        _logger.LogDebug("{Name}: {Packets} audio packets received", fake.Options.Name, packets);
                    }

                    break;
                case FakeReceiverEvent.Volume volume:
                    _logger.LogInformation("{Name}: volume {Db:0.00} dB", fake.Options.Name, volume.Db);
                    break;
                case FakeReceiverEvent.Flush flush:
                    _logger.LogInformation("{Name}: FLUSH seq {Seq} rtptime {Rtp}", fake.Options.Name, flush.Sequence, flush.Timestamp);
                    break;
                case FakeReceiverEvent.Teardown teardown:
                    _logger.LogInformation("{Name}: TEARDOWN ({Kind})", fake.Options.Name, teardown.Session ? "session" : "stream");
                    break;
                case FakeReceiverEvent.Metadata metadata:
                    _logger.LogInformation("{Name}: metadata {Items}", fake.Options.Name, string.Join(", ", metadata.Items.Select(kv => kv.Key + "=" + kv.Value)));
                    break;
            }
        }
    }
}
#endif
