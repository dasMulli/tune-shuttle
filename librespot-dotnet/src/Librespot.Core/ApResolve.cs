// Port of core/src/apresolve.rs.
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core;

/// <summary>Upstream <c>SocketAddress = (String, u16)</c>.</summary>
public readonly record struct SocketAddress(string Host, ushort Port)
{
    /// <summary>Formats as <c>host:port</c>.</summary>
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>The JSON answer of <c>apresolve.spotify.com</c> (upstream <c>ApResolveData</c>): "host:port" strings per endpoint type.</summary>
public sealed record ApResolveData
{
    /// <summary>Access point addresses.</summary>
    [JsonPropertyName("accesspoint")]
    [JsonRequired]
    public IReadOnlyList<string> AccessPoint { get; init; } = [];

    /// <summary>Dealer (websocket) addresses.</summary>
    [JsonPropertyName("dealer")]
    [JsonRequired]
    public IReadOnlyList<string> Dealer { get; init; } = [];

    /// <summary>spclient (HTTP API) addresses.</summary>
    [JsonPropertyName("spclient")]
    [JsonRequired]
    public IReadOnlyList<string> SpClient { get; init; } = [];

    /// <summary>
    /// These addresses probably do some geo-location based traffic management or at least DNS-based load
    /// balancing. They are known to fail when the normal resolvers are up, so that's why they should only be used
    /// as fallback.
    /// </summary>
    internal static ApResolveData Fallback() => new()
    {
        AccessPoint = ["ap.spotify.com:443"],
        Dealer = ["dealer.spotify.com:443"],
        SpClient = ["spclient.wg.spotify.com:443"],
    };
}

/// <summary>Resolves Spotify access point / dealer / spclient endpoints.</summary>
public sealed class ApResolver : SessionComponent
{
    private const string ApResolveUrl = "https://apresolve.spotify.com/?type=accesspoint&type=dealer&type=spclient";

    private static readonly ILogger Log = LibrespotLog.CreateLogger<ApResolver>();

    /// <summary>Upstream <c>AccessPoints</c>: one rotation queue per endpoint type.</summary>
    private sealed class AccessPoints
    {
        public Queue<SocketAddress> AccessPoint { get; init; } = new();
        public Queue<SocketAddress> Dealer { get; init; } = new();
        public Queue<SocketAddress> SpClient { get; init; } = new();

        public bool IsAnyEmpty() => AccessPoint.Count == 0 || Dealer.Count == 0 || SpClient.Count == 0;
    }

    // Guarded by Lock.
    private AccessPoints _data = new();

    // Test seam: when set, used instead of Session.HttpClient (which the session builds itself).
    private readonly HttpClient? _httpClient;

    internal ApResolver(Session session) : this(session, null)
    {
    }

    /// <summary>Builds a resolver that queries apresolve through <paramref name="httpClient"/> (tests inject a fake handler).</summary>
    internal ApResolver(Session session, HttpClient? httpClient) : base(session)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Returns a port if a proxy URL and/or a proxy port was specified. This is useful even when there is no
    /// proxy, but firewalls only allow certain ports (e.g. 443 and not 4070).
    /// </summary>
    public ushort? PortConfig()
    {
        if (Session.Config.Proxy is not null || Session.Config.ApPort is not null)
        {
            return Session.Config.ApPort ?? 443;
        }

        return null;
    }

    /// <summary>Parses "host:port" strings, dropping malformed ones and those not matching <paramref name="filterPort"/>.</summary>
    internal static Queue<SocketAddress> ProcessApStrings(IEnumerable<string> data, ushort? filterPort)
    {
        var result = new Queue<SocketAddress>();
        foreach (var ap in data)
        {
            // rsplitn(2, ':')
            var colon = ap.LastIndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var portText = ap[(colon + 1)..];
            if (portText.StartsWith('+'))
            {
                // Rust's `u16::from_str` accepts a leading plus sign.
                portText = portText[1..];
            }

            if (!ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                continue;
            }

            var host = ap[..colon];
            if (filterPort is { } filter && filter != port)
            {
                continue;
            }

            result.Enqueue(new SocketAddress(host, port));
        }

        return result;
    }

    private AccessPoints ParseResolveToAccessPoints(ApResolveData resolve)
    {
        var filterPort = PortConfig();
        return new AccessPoints
        {
            AccessPoint = ProcessApStrings(resolve.AccessPoint, filterPort),
            Dealer = ProcessApStrings(resolve.Dealer, filterPort),
            SpClient = ProcessApStrings(resolve.SpClient, filterPort),
        };
    }

    /// <summary>Upstream <c>try_apresolve</c>: queries apresolve.spotify.com once.</summary>
    public async Task<ApResolveData> TryApResolveAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApResolveUrl);
        var body = await (_httpClient ?? Session.HttpClient).RequestBodyAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize<ApResolveData>(body)
                ?? throw new JsonException("apresolve returned null");
        }
        catch (JsonException e)
        {
            throw LibrespotException.From(e);
        }
    }

    private async Task ApResolveAsync(CancellationToken cancellationToken)
    {
        ApResolveData data;
        Exception? error = null;
        try
        {
            data = await TryApResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            data = new ApResolveData();
            error = e;
        }

        lock (Lock)
        {
            _data = ParseResolveToAccessPoints(data);

            if (_data.IsAnyEmpty())
            {
                Log.LogWarning("Failed to resolve all access points, using fallbacks");
                if (error is not null)
                {
                    Log.LogWarning("Resolve access points error: {Error}", error.Message);
                }

                var fallback = ParseResolveToAccessPoints(ApResolveData.Fallback());
                foreach (var ap in fallback.AccessPoint)
                {
                    _data.AccessPoint.Enqueue(ap);
                }

                foreach (var ap in fallback.Dealer)
                {
                    _data.Dealer.Enqueue(ap);
                }

                foreach (var ap in fallback.SpClient)
                {
                    _data.SpClient.Enqueue(ap);
                }
            }
        }
    }

    private bool IsAnyEmpty()
    {
        lock (Lock)
        {
            return _data.IsAnyEmpty();
        }
    }

    /// <summary>Upstream <c>resolve(endpoint)</c> with endpoint one of "accesspoint", "dealer", "spclient".</summary>
    /// <exception cref="LibrespotException">
    /// Kind <see cref="ErrorKind.Unimplemented"/> for an unknown endpoint, <see cref="ErrorKind.Unavailable"/> when no
    /// address is left for it.
    /// </exception>
    public async Task<SocketAddress> ResolveAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (IsAnyEmpty())
        {
            await ApResolveAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (Lock)
        {
            // take the first position instead of the last with `pop`, because Spotify returns
            // access points with ports 4070, 443 and 80 in order of preference from highest
            // to lowest.
            var queue = endpoint switch
            {
                "accesspoint" => _data.AccessPoint,
                "dealer" => _data.Dealer,
                "spclient" => _data.SpClient,
                _ => throw LibrespotException.Unimplemented($"No implementation to resolve access point {endpoint}"),
            };

            if (!queue.TryDequeue(out var accessPoint))
            {
                throw LibrespotException.Unavailable($"No access point available for endpoint {endpoint}");
            }

            return accessPoint;
        }
    }
}
