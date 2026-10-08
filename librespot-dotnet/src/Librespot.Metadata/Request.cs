// Port of metadata/src/request.rs.
using Librespot.Core;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Metadata;

/// <summary>
/// Upstream <c>trait MercuryRequest</c> with its single default method: requests a metadata protobuf over Mercury,
/// decorating the URI with the user's country and product type. Upstream <c>RequestResult</c> is
/// <c>Task&lt;byte[]&gt;</c> here.
/// </summary>
internal static class MercuryRequest
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger(typeof(MercuryRequest).FullName!);

    /// <summary>Upstream <c>MercuryRequest::request</c>.</summary>
    /// <exception cref="MetadataException">The response carried no payload (<see cref="MetadataException.Reason.Empty"/>).</exception>
    public static async Task<byte[]> RequestAsync(Session session, string uri, CancellationToken cancellationToken = default)
    {
        var separator = uri.Contains('?') ? "&" : "?";
        var metricsUri = $"{uri}{separator}country={session.Country}";

        if (session.GetUserAttribute("type") is { } product)
        {
            metricsUri += $"&product={product}";
        }

        Log.LogTrace("Requesting {MetricsUri}", metricsUri);

        var response = await session.Mercury.GetAsync(metricsUri, cancellationToken).ConfigureAwait(false);
        if (response.Payload.Count > 0)
        {
            var data = response.Payload[0];
            if (Log.IsEnabled(LogLevel.Trace))
            {
                Log.LogTrace("Received metadata: {Data}", Convert.ToHexStringLower(data));
            }

            return data;
        }

        throw MetadataException.Empty();
    }
}
