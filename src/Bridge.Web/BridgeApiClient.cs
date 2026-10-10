using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bridge.Contracts;

namespace Bridge.Web;

/// <summary>
/// Typed client for the bridge REST API (docs/airplay2/WORK-PLAN.md section 7: routes from <see cref="ApiRoutes"/>, DTOs
/// from Bridge.Contracts). Every call takes a <see cref="CancellationToken"/>; a non-success response becomes a
/// <see cref="BridgeApiException"/> carrying the RFC 9457 problem details the service returns (404 unknown id, 409 state
/// conflict, 422 validation, 502 receiver error with the RTSP status in <c>detail</c>).
/// </summary>
/// <remarks>
/// Routes are sent relative to the <see cref="HttpClient.BaseAddress"/> (the leading slash of the route constants is
/// dropped), so the app also works when the service is published under a path base.
/// </remarks>
public sealed class BridgeApiClient
{
    /// <summary>The wire format: web defaults (camelCase); enums carry their own string converters in the contracts.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Creates the client over an <see cref="HttpClient"/> whose base address is the bridge's origin.</summary>
    public BridgeApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    // ---- status, receivers ---------------------------------------------------------------------------------------

    /// <summary><c>GET /api/status</c>.</summary>
    public Task<StatusDto> GetStatusAsync(CancellationToken ct) => GetAsync<StatusDto>(ApiRoutes.Status, ct);

    /// <summary><c>GET /api/receivers</c>: discovery merged with the store.</summary>
    public Task<IReadOnlyList<ReceiverDto>> GetReceiversAsync(CancellationToken ct) => GetAsync<IReadOnlyList<ReceiverDto>>(ApiRoutes.Receivers, ct);

    /// <summary><c>GET /api/receivers/{id}</c>.</summary>
    public Task<ReceiverDto> GetReceiverAsync(string id, CancellationToken ct) => GetAsync<ReceiverDto>(Route(ApiRoutes.Receivers + "/{id}", id), ct);

    /// <summary><c>POST /api/receivers/{id}/pair</c>: the receiver shows its PIN; the token goes back with the PIN.</summary>
    public Task<PairingStartedDto> StartPairingAsync(string id, CancellationToken ct) => SendAsync<PairingStartedDto>(HttpMethod.Post, Route(ApiRoutes.ReceiverPair, id), null, ct);

    /// <summary><c>POST /api/receivers/{id}/pair/pin</c>: finishes the PIN pairing (422 on a wrong PIN, 409 on an expired token).</summary>
    public Task<ReceiverDto> FinishPairingAsync(string id, PairingPinRequest request, CancellationToken ct) =>
        SendAsync<ReceiverDto>(HttpMethod.Post, Route(ApiRoutes.ReceiverPairPin, id), request, ct);

    /// <summary><c>DELETE /api/receivers/{id}/pairing</c>: forgets the stored keys.</summary>
    public Task ForgetPairingAsync(string id, CancellationToken ct) => SendAsync(HttpMethod.Delete, Route(ApiRoutes.ReceiverUnpair, id), null, ct);

    /// <summary><c>POST /api/receivers/{id}/connect</c>: plays to this receiver alone (a PIN receiver answers with a <c>pairingRequired</c> event).</summary>
    public Task<ReceiverDto> ConnectAsync(string id, CancellationToken ct) => SendAsync<ReceiverDto>(HttpMethod.Post, Route(ApiRoutes.ReceiverConnect, id), null, ct);

    /// <summary><c>POST /api/receivers/{id}/disconnect</c>.</summary>
    public Task<ReceiverDto> DisconnectAsync(string id, CancellationToken ct) => SendAsync<ReceiverDto>(HttpMethod.Post, Route(ApiRoutes.ReceiverDisconnect, id), null, ct);

    /// <summary><c>PUT /api/receivers/{id}/volume</c>: r_i (inside the active group: its member level).</summary>
    public Task<ReceiverDto> SetReceiverVolumeAsync(string id, int level, CancellationToken ct) =>
        SendAsync<ReceiverDto>(HttpMethod.Put, Route(ApiRoutes.ReceiverVolume, id), new VolumeRequest(level), ct);

    /// <summary><c>PUT /api/receivers/{id}/offset</c>: offset_frames (SPEC-audio.md 3.3).</summary>
    public Task<ReceiverDto> SetReceiverOffsetAsync(string id, int offsetFrames, CancellationToken ct) =>
        SendAsync<ReceiverDto>(HttpMethod.Put, Route(ApiRoutes.ReceiverOffset, id), new OffsetRequest(offsetFrames), ct);

    // ---- groups --------------------------------------------------------------------------------------------------

    /// <summary><c>GET /api/groups</c>.</summary>
    public Task<IReadOnlyList<GroupDto>> GetGroupsAsync(CancellationToken ct) => GetAsync<IReadOnlyList<GroupDto>>(ApiRoutes.Groups, ct);

    /// <summary><c>GET /api/groups/{id}</c>.</summary>
    public Task<GroupDto> GetGroupAsync(string id, CancellationToken ct) => GetAsync<GroupDto>(Route(ApiRoutes.Group, id), ct);

    /// <summary><c>POST /api/groups</c>.</summary>
    public Task<GroupDto> CreateGroupAsync(string name, CancellationToken ct) => SendAsync<GroupDto>(HttpMethod.Post, ApiRoutes.Groups, new GroupRequest(name), ct);

    /// <summary><c>PUT /api/groups/{id}</c>: rename.</summary>
    public Task<GroupDto> RenameGroupAsync(string id, string name, CancellationToken ct) => SendAsync<GroupDto>(HttpMethod.Put, Route(ApiRoutes.Group, id), new GroupRequest(name), ct);

    /// <summary><c>DELETE /api/groups/{id}</c> (an active group is deselected first).</summary>
    public Task DeleteGroupAsync(string id, CancellationToken ct) => SendAsync(HttpMethod.Delete, Route(ApiRoutes.Group, id), null, ct);

    /// <summary><c>POST /api/groups/{id}/members</c>: adds a member, or sets the level of an existing one.</summary>
    public Task<GroupDto> AddGroupMemberAsync(string id, string receiverId, int? level, CancellationToken ct) =>
        SendAsync<GroupDto>(HttpMethod.Post, Route(ApiRoutes.GroupMembers, id), new GroupMemberRequest(receiverId, level), ct);

    /// <summary><c>DELETE /api/groups/{id}/members/{receiverId}</c>.</summary>
    public Task<GroupDto> RemoveGroupMemberAsync(string id, string receiverId, CancellationToken ct) =>
        SendAsync<GroupDto>(HttpMethod.Delete, Route(ApiRoutes.GroupMember, id, receiverId), null, ct);

    /// <summary><c>PUT /api/groups/{id}/volume</c>: G; the service scales every r_i by G'/G.</summary>
    public Task<GroupDto> SetGroupVolumeAsync(string id, int level, CancellationToken ct) =>
        SendAsync<GroupDto>(HttpMethod.Put, Route(ApiRoutes.GroupVolume, id), new VolumeRequest(level), ct);

    /// <summary><c>POST /api/groups/{id}/activate</c>: selects the group as the output.</summary>
    public Task<OutputDto> ActivateGroupAsync(string id, CancellationToken ct) => SendAsync<OutputDto>(HttpMethod.Post, Route(ApiRoutes.GroupActivate, id), null, ct);

    // ---- output, playback, settings, diagnostics -----------------------------------------------------------------

    /// <summary><c>GET /api/output</c>.</summary>
    public Task<OutputDto> GetOutputAsync(CancellationToken ct) => GetAsync<OutputDto>(ApiRoutes.Output, ct);

    /// <summary><c>PUT /api/output</c>: a group, a receiver, or neither (audio is then discarded).</summary>
    public Task<OutputDto> SetOutputAsync(OutputRequest request, CancellationToken ct) => SendAsync<OutputDto>(HttpMethod.Put, ApiRoutes.Output, request, ct);

    /// <summary><c>GET /api/playback</c>.</summary>
    public Task<PlaybackDto> GetPlaybackAsync(CancellationToken ct) => GetAsync<PlaybackDto>(ApiRoutes.Playback, ct);

    /// <summary><c>POST /api/playback</c>: <c>play</c>, <c>pause</c>, <c>next</c> or <c>prev</c>, forwarded to Spotify (409 while no Spotify session).</summary>
    public Task<PlaybackDto> SendPlaybackCommandAsync(string command, CancellationToken ct) =>
        SendAsync<PlaybackDto>(HttpMethod.Post, ApiRoutes.Playback, new PlaybackCommandRequest(command), ct);

    /// <summary><c>GET /api/settings</c>.</summary>
    public Task<SettingsDto> GetSettingsAsync(CancellationToken ct) => GetAsync<SettingsDto>(ApiRoutes.Settings, ct);

    /// <summary><c>PUT /api/settings</c>; returns the settings as stored (the stream name normalized).</summary>
    public Task<SettingsDto> SetSettingsAsync(SettingsDto settings, CancellationToken ct) => SendAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.Settings, settings, ct);

    /// <summary><c>GET /api/diagnostics</c>.</summary>
    public Task<DiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct) => GetAsync<DiagnosticsDto>(ApiRoutes.Diagnostics, ct);

    /// <summary><c>DELETE /api/spotify</c>: forgets the cached Spotify credentials.</summary>
    public Task ForgetSpotifyAsync(CancellationToken ct) => SendAsync(HttpMethod.Delete, ApiRoutes.Spotify, null, ct);

    /// <summary>Everything the pages show, fetched over REST (start-up, and the fallback while the WebSocket is down).</summary>
    public async Task<BridgeSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var status = GetStatusAsync(ct);
        var receivers = GetReceiversAsync(ct);
        var groups = GetGroupsAsync(ct);
        var output = GetOutputAsync(ct);
        var playback = GetPlaybackAsync(ct);
        await Task.WhenAll(status, receivers, groups, output, playback).ConfigureAwait(false);
        return new BridgeSnapshot(
            await status.ConfigureAwait(false),
            await receivers.ConfigureAwait(false),
            await groups.ConfigureAwait(false),
            await output.ConfigureAwait(false),
            await playback.ConfigureAwait(false));
    }

    /// <summary>Fills <c>{id}</c> (and <c>{receiverId}</c>) of a route template, escaped (receiver ids contain colons).</summary>
    public static string Route(string template, string id, string? receiverId = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrEmpty(id);
        var route = template.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
        if (receiverId is not null)
        {
            route = route.Replace("{receiverId}", Uri.EscapeDataString(receiverId), StringComparison.Ordinal);
        }

        return route;
    }

    private static string Relative(string route) => route.TrimStart('/');

    private async Task<T> GetAsync<T>(string route, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Relative(route));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync<T>(request, response, ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var request = CreateRequest(method, route, body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync<T>(request, response, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpMethod method, string route, object? body, CancellationToken ct)
    {
        using var request = CreateRequest(method, route, body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(request, response, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string route, object? body)
    {
        var request = new HttpRequestMessage(method, Relative(route));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        }

        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(request, response, ct).ConfigureAwait(false);
        T? value;
        try
        {
            value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new BridgeApiException(response.StatusCode, "Unreadable response", $"{request.Method} {request.RequestUri}: {ex.Message}", ex);
        }

        return value ?? throw new BridgeApiException(response.StatusCode, "Empty response", $"{request.Method} {request.RequestUri} returned no body.");
    }

    private static async Task EnsureSuccessAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? title = null;
        string? detail = null;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    title = document.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    detail = document.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                }
            }
            catch (JsonException)
            {
                detail = text.Length > 300 ? text[..300] : text; // not problem details (a proxy's error page, say)
            }
        }

        throw new BridgeApiException(
            response.StatusCode,
            title ?? response.ReasonPhrase ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
            detail ?? $"{request.Method} {request.RequestUri} failed with {(int)response.StatusCode}.",
            isProblemDetails: title is not null);
    }
}

/// <summary>Everything the pages show, as one REST round.</summary>
/// <param name="Status">Service status.</param>
/// <param name="Receivers">All receivers.</param>
/// <param name="Groups">All groups.</param>
/// <param name="Output">The selected output.</param>
/// <param name="Playback">The playback view.</param>
public sealed record BridgeSnapshot(StatusDto Status, IReadOnlyList<ReceiverDto> Receivers, IReadOnlyList<GroupDto> Groups, OutputDto Output, PlaybackDto Playback);

/// <summary>A non-success answer of the bridge API, with the RFC 9457 problem details' title and detail.</summary>
public sealed class BridgeApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="statusCode">The HTTP status of the answer.</param>
    /// <param name="title">The problem's title, or the client's own wording when the service sent no problem details.</param>
    /// <param name="detail">The problem's detail, or the client's own wording.</param>
    /// <param name="inner">The exception that caused this one.</param>
    /// <param name="isProblemDetails">True when <paramref name="title"/> and <paramref name="detail"/> come from a problem-details body the service sent.</param>
    public BridgeApiException(HttpStatusCode statusCode, string title, string detail, Exception? inner = null, bool isProblemDetails = false)
        : base($"{title}: {detail}", inner)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        IsProblemDetails = isProblemDetails;
    }

    /// <summary>Creates an exception without status (required by the exception pattern).</summary>
    public BridgeApiException()
        : this(HttpStatusCode.InternalServerError, "Error", "Unknown error.")
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    public BridgeApiException(string message)
        : this(HttpStatusCode.InternalServerError, "Error", message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public BridgeApiException(string message, Exception innerException)
        : this(HttpStatusCode.InternalServerError, "Error", message, innerException)
    {
    }

    /// <summary>The HTTP status.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// True when the service answered with RFC 9457 problem details (a title the app shows as sent, UI-SPEC 3.5); false
    /// for what the client composed itself (an unreadable or empty body, a proxy's error page, a bare status).
    /// </summary>
    public bool IsProblemDetails { get; }

    /// <summary>The problem's title ("Wrong PIN", "Receiver error", ...).</summary>
    public string Title { get; }

    /// <summary>The problem's detail (for 502: the RTSP status and message).</summary>
    public string Detail { get; }
}
