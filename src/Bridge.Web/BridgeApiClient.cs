using System.Net.Http.Json;
using Bridge.Contracts;

namespace Bridge.Web;

/// <summary>Typed client for the bridge REST API (routes from <see cref="ApiRoutes"/>, DTOs from Bridge.Contracts).</summary>
public sealed class BridgeApiClient(HttpClient http)
{
    private HttpClient Http { get; } = http;

    /// <summary><c>GET /api/status</c>.</summary>
    public Task<StatusDto?> GetStatusAsync(CancellationToken ct) => Http.GetFromJsonAsync<StatusDto>(ApiRoutes.Status, ct);

    /// <summary><c>GET /api/receivers</c>.</summary>
    public Task<IReadOnlyList<ReceiverDto>?> GetReceiversAsync(CancellationToken ct) => Http.GetFromJsonAsync<IReadOnlyList<ReceiverDto>>(ApiRoutes.Receivers, ct);

    /// <summary><c>GET /api/groups</c>.</summary>
    public Task<IReadOnlyList<GroupDto>?> GetGroupsAsync(CancellationToken ct) => Http.GetFromJsonAsync<IReadOnlyList<GroupDto>>(ApiRoutes.Groups, ct);

    // TODO(impl: web-app): the remaining endpoints (pairing, connect, groups, volume, output, playback, settings, diagnostics)
}
