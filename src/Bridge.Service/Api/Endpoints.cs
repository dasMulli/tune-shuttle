using Bridge.Contracts;

namespace Bridge.Service.Api;

/// <summary>
/// The REST + WebSocket surface (docs/airplay2/WORK-PLAN.md "Bridge API"). Every handler delegates to
/// <see cref="BridgeController"/>; this file only maps routes and status codes.
/// </summary>
public static class Endpoints
{
    /// <summary>Maps all routes.</summary>
    public static IEndpointRouteBuilder MapBridgeApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(ApiRoutes.Base);

        api.MapGet("/receivers", (BridgeController c, CancellationToken ct) => c.GetReceiversAsync(ct));
        api.MapGet("/receivers/{id}", (string id, BridgeController c, CancellationToken ct) => c.GetReceiverAsync(id, ct));
        api.MapPost("/receivers/{id}/pair", (string id, BridgeController c, CancellationToken ct) => c.StartPairingAsync(id, ct));
        api.MapPost("/receivers/{id}/pair/pin", (string id, PairingPinRequest body, BridgeController c, CancellationToken ct) => c.FinishPairingAsync(id, body, ct));
        api.MapDelete("/receivers/{id}/pairing", (string id, BridgeController c, CancellationToken ct) => c.DeletePairingAsync(id, ct));
        api.MapPost("/receivers/{id}/connect", (string id, BridgeController c, CancellationToken ct) => c.ConnectReceiverAsync(id, ct));
        api.MapPost("/receivers/{id}/disconnect", (string id, BridgeController c, CancellationToken ct) => c.DisconnectReceiverAsync(id, ct));
        api.MapPut("/receivers/{id}/volume", (string id, VolumeRequest body, BridgeController c, CancellationToken ct) => c.SetReceiverVolumeAsync(id, body, ct));
        api.MapPut("/receivers/{id}/offset", (string id, OffsetRequest body, BridgeController c, CancellationToken ct) => c.SetReceiverOffsetAsync(id, body, ct));

        api.MapGet("/groups", (BridgeController c, CancellationToken ct) => c.GetGroupsAsync(ct));
        api.MapPost("/groups", (GroupRequest body, BridgeController c, CancellationToken ct) => c.CreateGroupAsync(body, ct));
        api.MapGet("/groups/{id}", (string id, BridgeController c, CancellationToken ct) => c.GetGroupAsync(id, ct));
        api.MapPut("/groups/{id}", (string id, GroupRequest body, BridgeController c, CancellationToken ct) => c.RenameGroupAsync(id, body, ct));
        api.MapDelete("/groups/{id}", (string id, BridgeController c, CancellationToken ct) => c.DeleteGroupAsync(id, ct));
        api.MapPost("/groups/{id}/members", (string id, GroupMemberRequest body, BridgeController c, CancellationToken ct) => c.AddGroupMemberAsync(id, body, ct));
        api.MapDelete("/groups/{id}/members/{receiverId}", (string id, string receiverId, BridgeController c, CancellationToken ct) => c.RemoveGroupMemberAsync(id, receiverId, ct));
        api.MapPut("/groups/{id}/volume", (string id, VolumeRequest body, BridgeController c, CancellationToken ct) => c.SetGroupVolumeAsync(id, body, ct));
        api.MapPost("/groups/{id}/activate", (string id, BridgeController c, CancellationToken ct) => c.ActivateGroupAsync(id, ct));

        api.MapGet("/output", (BridgeController c, CancellationToken ct) => c.GetOutputAsync(ct));
        api.MapPut("/output", (OutputRequest body, BridgeController c, CancellationToken ct) => c.SetOutputAsync(body, ct));
        api.MapGet("/playback", (BridgeController c, CancellationToken ct) => c.GetPlaybackAsync(ct));
        api.MapPost("/playback", (PlaybackCommandRequest body, BridgeController c, CancellationToken ct) => c.PlaybackCommandAsync(body, ct));
        api.MapGet("/settings", (BridgeController c, CancellationToken ct) => c.GetSettingsAsync(ct));
        api.MapPut("/settings", (SettingsDto body, BridgeController c, CancellationToken ct) => c.SetSettingsAsync(body, ct));
        api.MapGet("/status", (BridgeController c, CancellationToken ct) => c.GetStatusAsync(ct));
        api.MapGet("/diagnostics", (BridgeController c, CancellationToken ct) => c.GetDiagnosticsAsync(ct));
        api.MapDelete("/spotify", (BridgeController c, CancellationToken ct) => c.ForgetSpotifyAsync(ct));

        app.Map(ApiRoutes.Events, async (HttpContext http, EventHub hub, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await hub.ServeAsync(socket, ct).ConfigureAwait(false);
        });

        return app;
    }
}

/// <summary>Application logic behind the routes; one instance per process (singleton), async and thread-safe.</summary>
public sealed class BridgeController
{
    private static Task<IResult> NotImplemented() => Task.FromResult(Results.StatusCode(StatusCodes.Status501NotImplemented));

    public Task<IResult> GetReceiversAsync(CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> GetReceiverAsync(string id, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> StartPairingAsync(string id, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> FinishPairingAsync(string id, PairingPinRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> DeletePairingAsync(string id, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> ConnectReceiverAsync(string id, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> DisconnectReceiverAsync(string id, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> SetReceiverVolumeAsync(string id, VolumeRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> SetReceiverOffsetAsync(string id, OffsetRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> GetGroupsAsync(CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> CreateGroupAsync(GroupRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> GetGroupAsync(string id, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> RenameGroupAsync(string id, GroupRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> DeleteGroupAsync(string id, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> AddGroupMemberAsync(string id, GroupMemberRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> RemoveGroupMemberAsync(string id, string receiverId, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> SetGroupVolumeAsync(string id, VolumeRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> ActivateGroupAsync(string id, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> GetOutputAsync(CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> SetOutputAsync(OutputRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> GetPlaybackAsync(CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> PlaybackCommandAsync(PlaybackCommandRequest body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> GetSettingsAsync(CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> SetSettingsAsync(SettingsDto body, CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> GetStatusAsync(CancellationToken ct) => Task.FromResult(Results.Ok(new StatusDto(
        typeof(BridgeController).Assembly.GetName().Version?.ToString() ?? "0", false, null, "airplay2-bridge", false, 0, Program.StartedAt)));

    public Task<IResult> GetDiagnosticsAsync(CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)

    public Task<IResult> ForgetSpotifyAsync(CancellationToken ct) => NotImplemented(); // TODO(impl: bridge-service)
}

/// <summary>Fans <see cref="BridgeEvent"/>s out to every connected WebSocket as JSON (one message per event).</summary>
public sealed class EventHub
{
    /// <summary>Publishes an event to all clients.</summary>
    public void Publish(BridgeEvent bridgeEvent)
    {
        // TODO(impl: bridge-service)
    }

    /// <summary>Serves one client until it disconnects.</summary>
    public Task ServeAsync(System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(impl: bridge-service)");
}
