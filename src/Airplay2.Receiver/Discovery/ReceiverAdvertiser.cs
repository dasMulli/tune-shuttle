using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Discovery;

/// <summary>What <see cref="ReceiverAdvertiser"/> registers (SPEC-receiver.md 3.1): both services with their instance names, port and TXT entries.</summary>
/// <param name="Name">The <c>_airplay._tcp</c> instance name (the receiver's name).</param>
/// <param name="RaopInstanceName">The <c>_raop._tcp</c> instance name (<see cref="ReceiverTxtRecords.RaopInstanceName"/>).</param>
/// <param name="Port">The RTSP port both services point at.</param>
/// <param name="AirplayTxt">The <c>_airplay._tcp</c> TXT entries.</param>
/// <param name="RaopTxt">The <c>_raop._tcp</c> TXT entries.</param>
public sealed record ReceiverAdvertisement(string Name, string RaopInstanceName, int Port, IReadOnlyList<string> AirplayTxt, IReadOnlyList<string> RaopTxt);

/// <summary>
/// Advertises the receiver through one <c>Librespot.Discovery.Mdns.MdnsResponder</c> instance it owns (SPEC-receiver.md 3.1;
/// the Spotify Zeroconf responder stays separate): registers <c>_airplay._tcp</c> and <c>_raop._tcp</c>, re-registers
/// (goodbye, then announce) on a name or password change and on nothing else (3.3), sends goodbye records on stop.
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the responder wiring is the wave 1 <c>advertise</c> package (SPEC-receiver.md 13).
/// </remarks>
public sealed class ReceiverAdvertiser : IAsyncDisposable
{
    private readonly ILogger _logger;

    /// <summary>Creates the advertiser; nothing is announced until <see cref="StartAsync"/>.</summary>
    /// <param name="addresses">The addresses to publish, or null for every interface's (SPEC-receiver.md 8.5 <c>Bridge:LocalAddress</c>).</param>
    /// <param name="logger">Logger.</param>
    public ReceiverAdvertiser(IReadOnlyList<IPAddress>? addresses = null, ILogger? logger = null)
    {
        Addresses = addresses;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The published addresses, or null for every interface's.</summary>
    public IReadOnlyList<IPAddress>? Addresses { get; }

    /// <summary>Registers both services and announces them (SPEC-receiver.md 3.1).</summary>
    public Task StartAsync(ReceiverAdvertisement advertisement, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 3.1, wave 1");

    /// <summary>Re-registers both services after a name or password change: goodbye, then announce (SPEC-receiver.md 3.3).</summary>
    public Task UpdateAsync(ReceiverAdvertisement advertisement, CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 3.3, wave 1");

    /// <summary>Sends goodbye records and stops the responder (SPEC-receiver.md 3.3 <c>enabled → false</c>).</summary>
    public Task StopAsync(CancellationToken cancellationToken) => throw new NotImplementedException("SPEC-receiver.md 3.3, wave 1");

    /// <summary>Stops the responder when it is running.</summary>
    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("Receiver advertiser disposed");
        return ValueTask.CompletedTask;
    }
}
