using Bridge.Contracts;

namespace Bridge.Service.Api;

/// <summary>
/// The REST + WebSocket surface (docs/airplay2/WORK-PLAN.md section 7 "Bridge API surface"). Every handler delegates to
/// <see cref="BridgeController"/>; this file only maps routes.
/// </summary>
public static class Endpoints
{
    /// <summary>Maps all routes.</summary>
    public static IEndpointRouteBuilder MapBridgeApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(ApiRoutes.Base);

        api.MapGet("/receivers", (BridgeController c, CancellationToken ct) => c.GetReceiversAsync(ct));
        api.MapDelete("/receivers", (bool? offline, BridgeController c, CancellationToken ct) => c.ForgetReceiversAsync(offline, ct));
        api.MapGet("/receivers/{id}", (string id, BridgeController c, CancellationToken ct) => c.GetReceiverAsync(id, ct));
        api.MapDelete("/receivers/{id}", (string id, BridgeController c, CancellationToken ct) => c.ForgetReceiverAsync(id, ct));
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

#if BRIDGE_FAKE_RECEIVERS
        // Development only (Debug builds with Bridge:FakeReceivers): screenshot setups take a fake receiver offline.
        api.MapPost("/dev/fake-receivers/{id}/offline", (string id, double? daysAgo, IServiceProvider services) =>
            services.GetService<Output.DevFakeReceivers>() is { } fakes && fakes.TakeOffline(id, TimeSpan.FromDays(Math.Max(0, daysAgo ?? 0)))
                ? Results.NoContent()
                : Results.NotFound());
#endif

        app.Map(ApiRoutes.Events, async (HttpContext http, EventHub hub, BridgeController c, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await hub.ServeAsync(socket, c.GetSnapshotAsync, ct).ConfigureAwait(false);
        });

        return app;
    }
}
