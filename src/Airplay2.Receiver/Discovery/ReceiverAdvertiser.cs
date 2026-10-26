using System.Net;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Airplay2.Receiver.Discovery;

/// <summary>What <see cref="ReceiverAdvertiser"/> registers (SPEC-receiver.md 3.1): both services with their instance names, port and TXT entries.</summary>
/// <param name="Name">The <c>_airplay._tcp</c> instance name (the receiver's name).</param>
/// <param name="RaopInstanceName">The <c>_raop._tcp</c> instance name (<see cref="ReceiverTxtRecords.RaopInstanceName"/>).</param>
/// <param name="Port">The RTSP port both services point at.</param>
/// <param name="AirplayTxt">The <c>_airplay._tcp</c> TXT entries.</param>
/// <param name="RaopTxt">The <c>_raop._tcp</c> TXT entries.</param>
public sealed record ReceiverAdvertisement(string Name, string RaopInstanceName, int Port, IReadOnlyList<string> AirplayTxt, IReadOnlyList<string> RaopTxt)
{
    /// <summary>
    /// True when <paramref name="other"/> registers exactly the same records: names, port and TXT entries in order
    /// (SPEC-receiver.md 3.3: an unchanged advertisement is not re-registered). Record equality compares the lists by reference.
    /// </summary>
    public bool SameRecordsAs(ReceiverAdvertisement? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(RaopInstanceName, other.RaopInstanceName, StringComparison.Ordinal)
        && Port == other.Port
        && AirplayTxt.SequenceEqual(other.AirplayTxt, StringComparer.Ordinal)
        && RaopTxt.SequenceEqual(other.RaopTxt, StringComparer.Ordinal);
}

/// <summary>
/// Advertises the receiver through one <c>Librespot.Discovery.Mdns.MdnsResponder</c> instance it owns (SPEC-receiver.md 3.1;
/// the Spotify Zeroconf responder stays separate): registers <c>_airplay._tcp</c> and <c>_raop._tcp</c>, re-registers
/// (goodbye, then announce) on a name or password change and on nothing else (3.3), sends goodbye records on stop.
/// </summary>
/// <remarks>
/// <see cref="StartAsync"/>, <see cref="UpdateAsync"/> and <see cref="StopAsync"/> are serialised with one
/// <c>SemaphoreSlim</c>. The responder binds the endpoint's port with address reuse, so it coexists with the Spotify
/// responder on 5353. Tests pass a private <see cref="MdnsEndpoint"/> (an administratively scoped group, hop limit 1) so
/// nothing they announce reaches the LAN's mDNS (SPEC-receiver.md 11.1).
/// </remarks>
public sealed class ReceiverAdvertiser : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MdnsResponder? _responder;
    private MdnsResponder.Registration? _airplay;
    private MdnsResponder.Registration? _raop;
    private ReceiverAdvertisement? _current;
    private bool _disposed;

    /// <summary>Creates the advertiser; nothing is announced until <see cref="StartAsync"/>.</summary>
    /// <param name="addresses">The addresses to publish, or null for every interface's (SPEC-receiver.md 8.5 <c>Bridge:LocalAddress</c>).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="endpoint">The mDNS groups, port and hop limit; null for <see cref="MdnsEndpoint.Default"/> (tests pass a private one, 11.1).</param>
    /// <param name="hostname">The SRV target host name (single label, <c>.local</c> is appended); null for the machine name.</param>
    public ReceiverAdvertiser(IReadOnlyList<IPAddress>? addresses = null, ILogger? logger = null, MdnsEndpoint? endpoint = null, string? hostname = null)
    {
        Addresses = addresses;
        Endpoint = endpoint ?? MdnsEndpoint.Default;
        Hostname = hostname;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The published addresses, or null for every interface's.</summary>
    public IReadOnlyList<IPAddress>? Addresses { get; }

    /// <summary>The mDNS endpoint the responder binds, joins and sends to (SPEC-receiver.md 3.1, 11.1).</summary>
    public MdnsEndpoint Endpoint { get; }

    /// <summary>The SRV target host name, or null for the machine name.</summary>
    public string? Hostname { get; }

    /// <summary>The advertisement currently registered, or null while stopped (SPEC-receiver.md 3.1).</summary>
    public ReceiverAdvertisement? Current => Volatile.Read(ref _current);

    /// <summary>True while both services are registered.</summary>
    public bool IsRunning => Current is not null;

    /// <summary>
    /// Registers both services and announces them (SPEC-receiver.md 3.1). Calling it while running behaves like
    /// <see cref="UpdateAsync"/>.
    /// </summary>
    /// <exception cref="System.Net.Sockets.SocketException">The responder could not bind the endpoint's IPv4 port.</exception>
    public async Task StartAsync(ReceiverAdvertisement advertisement, CancellationToken cancellationToken)
    {
        Validate(advertisement);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_responder is not null)
            {
                await ReplaceAsync(advertisement, cancellationToken).ConfigureAwait(false);
                return;
            }

            var responder = MdnsResponder.Spawn(Addresses ?? [], publishIpv6Addresses: true, hostname: Hostname, endpoint: Endpoint);
            try
            {
                await RegisterAsync(responder, advertisement, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await UnregisterAsync().ConfigureAwait(false);
                await responder.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _responder = responder;
            _logger.LogInformation(
                "Advertising AirPlay receiver {Name} ({Raop}) on port {Port}",
                advertisement.Name, advertisement.RaopInstanceName, advertisement.Port);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-registers both services after a name or password change: goodbye, then announce (SPEC-receiver.md 3.3). An
    /// advertisement with the same records (<see cref="ReceiverAdvertisement.SameRecordsAs"/>) changes nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The advertiser is not running.</exception>
    public async Task UpdateAsync(ReceiverAdvertisement advertisement, CancellationToken cancellationToken)
    {
        Validate(advertisement);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_responder is null)
            {
                throw new InvalidOperationException("The receiver advertiser is not running; call StartAsync first (SPEC-receiver.md 3.3).");
            }

            await ReplaceAsync(advertisement, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends goodbye records and stops the responder (SPEC-receiver.md 3.3 <c>enabled → false</c>); a no-op while stopped.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the responder when it is running (goodbye records, SPEC-receiver.md 3.3).</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogDebug("Receiver advertiser disposed");
    }

    private static void Validate(ReceiverAdvertisement advertisement)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        ArgumentException.ThrowIfNullOrWhiteSpace(advertisement.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(advertisement.RaopInstanceName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(advertisement.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(advertisement.Port, ushort.MaxValue);
        ArgumentNullException.ThrowIfNull(advertisement.AirplayTxt);
        ArgumentNullException.ThrowIfNull(advertisement.RaopTxt);
    }

    /// <summary>Goodbye for the current records, then announce the new ones (3.3); nothing when the records are unchanged.</summary>
    private async Task ReplaceAsync(ReceiverAdvertisement advertisement, CancellationToken cancellationToken)
    {
        var previous = _current;
        if (advertisement.SameRecordsAs(previous))
        {
            _logger.LogDebug("AirPlay receiver advertisement unchanged; not re-registered");
            return;
        }

        await UnregisterAsync().ConfigureAwait(false);
        await RegisterAsync(_responder!, advertisement, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Re-registered AirPlay receiver {Previous} as {Name} on port {Port}",
            previous?.Name, advertisement.Name, advertisement.Port);
    }

    private async Task RegisterAsync(MdnsResponder responder, ReceiverAdvertisement advertisement, CancellationToken cancellationToken)
    {
        var port = (ushort)advertisement.Port;
        _airplay = await responder.RegisterAsync(ReceiverTxtRecords.AirplayServiceType, advertisement.Name, port, advertisement.AirplayTxt, cancellationToken: cancellationToken).ConfigureAwait(false);
        _raop = await responder.RegisterAsync(ReceiverTxtRecords.RaopServiceType, advertisement.RaopInstanceName, port, advertisement.RaopTxt, cancellationToken: cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _current, advertisement);
    }

    /// <summary>Unregisters both services; each <c>Registration</c> sends its goodbye (TTL 0) when disposed.</summary>
    private async Task UnregisterAsync()
    {
        Volatile.Write(ref _current, null);
        var raop = Interlocked.Exchange(ref _raop, null);
        var airplay = Interlocked.Exchange(ref _airplay, null);
        if (raop is not null)
        {
            await raop.DisposeAsync().ConfigureAwait(false);
        }

        if (airplay is not null)
        {
            await airplay.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task StopCoreAsync()
    {
        var responder = _responder;
        if (responder is null)
        {
            return;
        }

        var name = _current?.Name;
        try
        {
            await UnregisterAsync().ConfigureAwait(false);
        }
        finally
        {
            _responder = null;
            await responder.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("Stopped advertising AirPlay receiver {Name}", name);
    }
}
