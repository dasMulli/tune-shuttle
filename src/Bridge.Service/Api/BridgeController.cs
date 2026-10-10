using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtsp;
using Bridge.Contracts;
using Bridge.Service.Diagnostics;
using Bridge.Service.Output;
using Bridge.Service.Spotify;
using Bridge.Service.Store;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Api;

/// <summary>
/// Application logic behind the routes (docs/ARCHITECTURE.md, "Bridge API"); one instance per process (singleton), async and
/// thread-safe. Errors are RFC 9457 problem details: 404 unknown id, 409 state conflicts, 422 validation, 502 receiver
/// errors with the RTSP status in <c>detail</c>.
/// </summary>
public sealed class BridgeController(
    IBridgeStore store,
    DtoFactory dtos,
    OutputManager output,
    PairingCoordinator pairing,
    ReceiverInventory inventory,
    ReceiverJanitor janitor,
    SpotifyHost spotify,
    RuntimeState runtime,
    AirplaySenderProvider senders,
    EventHub hub,
    LogBuffer logs,
    IOptions<BridgeOptions> options,
    ILogger<BridgeController> logger)
{
    /// <summary>Longest accepted device or group name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Probes of SPEC.md section 4 that still need the user (a microphone, button presses, a second receiver).</summary>
    public static readonly IReadOnlyList<string> PendingProbes =
    [
        "P4: volume changes made on the receiver's own buttons (event channel or poll)",
        "P7: measured output delay per L with a microphone",
        "P20: mixed group (SpeakerA + Apple receiver) offsets",
    ];

    private static readonly string[] PlaybackCommands = ["play", "pause", "next", "prev"];

    /// <summary><c>GET /receivers</c>.</summary>
    public Task<IResult> GetReceiversAsync(CancellationToken ct) => RunAsync(async () => Results.Ok(await dtos.ReceiversAsync(ct).ConfigureAwait(false)));

    /// <summary><c>GET /receivers/{id}</c>.</summary>
    public Task<IResult> GetReceiverAsync(string id, CancellationToken ct) => RunAsync(async () =>
        await dtos.ReceiverAsync(id, ct).ConfigureAwait(false) is { } dto ? Results.Ok(dto) : NotFound($"Unknown receiver '{id}'."));

    /// <summary>
    /// <c>DELETE /receivers/{id}</c>: forgets an offline receiver with its pairing, group memberships and settings (404
    /// unknown, 409 online: an online receiver is back from mDNS within seconds).
    /// </summary>
    public Task<IResult> ForgetReceiverAsync(string id, CancellationToken ct) => RunAsync(async () =>
    {
        await janitor.ForgetAsync(id, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    /// <summary><c>DELETE /receivers?offline=true</c>: forgets every offline receiver; answers the ids removed.</summary>
    public Task<IResult> ForgetReceiversAsync(bool? offline, CancellationToken ct) => RunAsync(async () =>
    {
        if (offline != true)
        {
            throw new ArgumentException("Only offline receivers can be forgotten in bulk; pass offline=true.", nameof(offline));
        }

        return Results.Ok(new ForgottenReceiversDto(await janitor.ForgetOfflineAsync(ct).ConfigureAwait(false)));
    });

    /// <summary><c>POST /receivers/{id}/pair</c>: the receiver shows its PIN.</summary>
    public Task<IResult> StartPairingAsync(string id, CancellationToken ct) => RunAsync(async () => Results.Ok(await pairing.StartAsync(id, ct).ConfigureAwait(false)));

    /// <summary><c>POST /receivers/{id}/pair/pin</c>: M3-M6 + verify, keys stored.</summary>
    public Task<IResult> FinishPairingAsync(string id, PairingPinRequest body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return Results.Ok(await pairing.FinishAsync(id, body, ct).ConfigureAwait(false));
        }
        catch (PairingException ex) when (ex.Failure == PairingFailure.Authentication)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Wrong PIN", "The receiver rejected the PIN; start the pairing again.");
        }
    });

    /// <summary><c>DELETE /receivers/{id}/pairing</c>.</summary>
    public Task<IResult> DeletePairingAsync(string id, CancellationToken ct) => RunAsync(async () =>
    {
        await pairing.ForgetAsync(id, ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    /// <summary><c>POST /receivers/{id}/connect</c>.</summary>
    public Task<IResult> ConnectReceiverAsync(string id, CancellationToken ct) => RunAsync(async () => Results.Ok(await output.ConnectReceiverAsync(id, ct).ConfigureAwait(false)));

    /// <summary><c>POST /receivers/{id}/disconnect</c>.</summary>
    public Task<IResult> DisconnectReceiverAsync(string id, CancellationToken ct) => RunAsync(async () => Results.Ok(await output.DisconnectReceiverAsync(id, ct).ConfigureAwait(false)));

    /// <summary><c>PUT /receivers/{id}/volume</c>: r_i.</summary>
    public Task<IResult> SetReceiverVolumeAsync(string id, VolumeRequest body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        ValidateLevel(body.Level);
        return Results.Ok(await output.SetReceiverLevelAsync(id, body.Level, OutputManager.SourceUser, ct).ConfigureAwait(false));
    });

    /// <summary><c>PUT /receivers/{id}/offset</c>: offset_frames.</summary>
    public Task<IResult> SetReceiverOffsetAsync(string id, OffsetRequest body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Math.Abs(body.OffsetFrames) > OutputManager.MaxOffsetFrames)
        {
            throw new ArgumentException($"The offset must be within ±{OutputManager.MaxOffsetFrames} frames (2 s).", nameof(body));
        }

        return Results.Ok(await output.SetOffsetAsync(id, body.OffsetFrames, ct).ConfigureAwait(false));
    });

    /// <summary><c>GET /groups</c>.</summary>
    public Task<IResult> GetGroupsAsync(CancellationToken ct) => RunAsync(async () => Results.Ok(await dtos.GroupsAsync(ct).ConfigureAwait(false)));

    /// <summary><c>POST /groups</c>.</summary>
    public Task<IResult> CreateGroupAsync(GroupRequest body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        var name = ValidateName(body.Name, "group name");
        var group = await store.CreateGroupAsync(name, ct).ConfigureAwait(false);
        var dto = await RequireGroupAsync(group.Id, ct).ConfigureAwait(false);
        hub.Publish(new BridgeEvent.GroupChanged(dto));
        logger.LogInformation("Group {Name} created ({Id})", name, group.Id);
        return Results.Created(ApiRoutes.Groups + "/" + group.Id, dto);
    });

    /// <summary><c>GET /groups/{id}</c>.</summary>
    public Task<IResult> GetGroupAsync(string id, CancellationToken ct) => RunAsync(async () => Results.Ok(await RequireGroupAsync(id, ct).ConfigureAwait(false)));

    /// <summary><c>PUT /groups/{id}</c>: rename.</summary>
    public Task<IResult> RenameGroupAsync(string id, GroupRequest body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        var name = ValidateName(body.Name, "group name");
        await store.RenameGroupAsync(id, name, ct).ConfigureAwait(false);
        var dto = await RequireGroupAsync(id, ct).ConfigureAwait(false);
        hub.Publish(new BridgeEvent.GroupChanged(dto));
        return Results.Ok(dto);
    });

    /// <summary><c>DELETE /groups/{id}</c>: an active group is deselected first (its receivers are torn down).</summary>
    public Task<IResult> DeleteGroupAsync(string id, CancellationToken ct) => RunAsync(async () =>
    {
        var group = await store.GetGroupAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Unknown group '{id}'.");
        await output.GroupDeletingAsync(id, ct).ConfigureAwait(false);
        await store.DeleteGroupAsync(id, ct).ConfigureAwait(false);
        hub.Publish(new BridgeEvent.GroupRemoved(id));
        foreach (var member in group.Members)
        {
            await inventory.PublishAsync(member.ReceiverId, ct).ConfigureAwait(false);
        }

        logger.LogInformation("Group {Name} deleted", group.Name);
        return Results.NoContent();
    });

    /// <summary><c>POST /groups/{id}/members</c>: adds (or re-levels) a member; an active group connects it now.</summary>
    public Task<IResult> AddGroupMemberAsync(string id, GroupMemberRequest body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(body.ReceiverId))
        {
            throw new ArgumentException("The receiver id is empty.", nameof(body));
        }

        if (body.Level is { } level)
        {
            ValidateLevel(level);
        }

        _ = await store.GetGroupAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Unknown group '{id}'.");
        var row = await inventory.RequireRowAsync(body.ReceiverId, ct).ConfigureAwait(false);
        await store.SetGroupMemberAsync(id, row.Id, body.Level ?? row.Level, ct).ConfigureAwait(false);
        await output.MemberAddedAsync(id, row.Id, ct).ConfigureAwait(false);
        var dto = await RequireGroupAsync(id, ct).ConfigureAwait(false);
        hub.Publish(new BridgeEvent.GroupChanged(dto));
        await inventory.PublishAsync(row.Id, ct).ConfigureAwait(false);
        return Results.Ok(dto);
    });

    /// <summary><c>DELETE /groups/{id}/members/{receiverId}</c>.</summary>
    public Task<IResult> RemoveGroupMemberAsync(string id, string receiverId, CancellationToken ct) => RunAsync(async () =>
    {
        _ = await store.GetGroupAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Unknown group '{id}'.");
        await store.RemoveGroupMemberAsync(id, receiverId, ct).ConfigureAwait(false);
        await output.MemberRemovedAsync(id, receiverId, ct).ConfigureAwait(false);
        var dto = await RequireGroupAsync(id, ct).ConfigureAwait(false);
        hub.Publish(new BridgeEvent.GroupChanged(dto));
        await inventory.PublishAsync(receiverId, ct).ConfigureAwait(false);
        return Results.Ok(dto);
    });

    /// <summary><c>PUT /groups/{id}/volume</c>: G; scales the members.</summary>
    public Task<IResult> SetGroupVolumeAsync(string id, VolumeRequest body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        ValidateLevel(body.Level);
        return Results.Ok(await output.SetGroupLevelAsync(id, body.Level, OutputManager.SourceUser, ct).ConfigureAwait(false));
    });

    /// <summary><c>POST /groups/{id}/activate</c>: select as output.</summary>
    public Task<IResult> ActivateGroupAsync(string id, CancellationToken ct) => RunAsync(async () =>
        Results.Ok(await output.SelectAsync(new OutputSelection(id, null), ct).ConfigureAwait(false)));

    /// <summary><c>GET /output</c>.</summary>
    public Task<IResult> GetOutputAsync(CancellationToken ct) => RunAsync(async () => Results.Ok(await dtos.OutputAsync(ct).ConfigureAwait(false)));

    /// <summary><c>PUT /output</c>: a group, a receiver, or neither (nothing plays; audio is discarded).</summary>
    public Task<IResult> SetOutputAsync(OutputRequest body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        var selection = new OutputSelection(Blank(body.GroupId), Blank(body.ReceiverId));
        return Results.Ok(await output.SelectAsync(selection, ct).ConfigureAwait(false));
    });

    /// <summary><c>GET /playback</c>.</summary>
    public Task<IResult> GetPlaybackAsync(CancellationToken ct) => Task.FromResult(Results.Ok(runtime.Playback));

    /// <summary><c>POST /playback</c>: forwarded to Spirc.</summary>
    public Task<IResult> PlaybackCommandAsync(PlaybackCommandRequest body, CancellationToken ct) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(body);
        var command = body.Command?.Trim().ToLowerInvariant();
        if (command is null || !PlaybackCommands.Contains(command))
        {
            throw new ArgumentException($"Unknown command '{body.Command}'; use one of {string.Join(", ", PlaybackCommands)}.", nameof(body));
        }

        return Task.FromResult(spotify.TryCommand(command)
            ? Results.Ok(runtime.Playback)
            : Problem(StatusCodes.Status409Conflict, "Spotify is not connected", "Select the bridge in a Spotify app first."));
    });

    /// <summary><c>GET /settings</c>.</summary>
    public Task<IResult> GetSettingsAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var settings = await BridgeSettings.LoadAsync(store, options.Value, ct).ConfigureAwait(false);
        var retention = await ReceiverRetention.LoadAsync(store, options.Value, ct).ConfigureAwait(false);
        return Results.Ok(new SettingsDto(settings.DeviceName, settings.LatencyFrames, settings.PollReceiverVolume, settings.PreferredStream, ReceiverRetention.ToDays(retention)));
    });

    /// <summary>
    /// <c>PUT /settings</c>: a new device name relaunches Spotify Connect; L, stream and poll rebuild the output; the
    /// forget-offline period applies from the next pruning run (null keeps it).
    /// </summary>
    public Task<IResult> SetSettingsAsync(SettingsDto body, CancellationToken ct) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(body);
        var name = ValidateName(body.DeviceName, "device name");
        if (body.LatencyFrames is < BridgeSettings.MinLatencyFrames or > BridgeSettings.MaxLatencyFrames)
        {
            throw new ArgumentException($"The latency must be between {BridgeSettings.MinLatencyFrames} and {BridgeSettings.MaxLatencyFrames} frames.", nameof(body));
        }

        var stream = BridgeSettings.NormalizeStream(body.PreferredStream)
            ?? throw new ArgumentException("The preferred stream is 'Realtime' or 'Buffered'.", nameof(body));
        if (body.ForgetOfflineAfterDays is { } days && (double.IsNaN(days) || days is < 0 or > ReceiverRetention.MaxDays))
        {
            throw new ArgumentException($"Forgetting offline receivers takes 0 (never) to {ReceiverRetention.MaxDays.ToString(CultureInfo.InvariantCulture)} days.", nameof(body));
        }

        var previous = await BridgeSettings.LoadAsync(store, options.Value, ct).ConfigureAwait(false);
        var current = new BridgeSettings(name, body.LatencyFrames, body.PollReceiverVolume, stream);
        await current.SaveAsync(store, ct).ConfigureAwait(false);
        if (body.ForgetOfflineAfterDays is { } forgetDays)
        {
            await ReceiverRetention.SaveDaysAsync(store, forgetDays, ct).ConfigureAwait(false);
        }

        var retention = await ReceiverRetention.LoadAsync(store, options.Value, ct).ConfigureAwait(false);
        logger.LogInformation("Settings: {Settings}, forget offline receivers after {Retention}", current, retention > TimeSpan.Zero ? retention.ToString() : "never");
        if (previous.DeviceName != current.DeviceName)
        {
            runtime.Spotify = runtime.Spotify with { DeviceName = current.DeviceName };
            spotify.RequestRestart();
        }

        await output.ApplySettingsAsync(previous, current, ct).ConfigureAwait(false);
        hub.Publish(new BridgeEvent.StatusChanged(await dtos.StatusAsync(ct).ConfigureAwait(false)));
        return Results.Ok(new SettingsDto(current.DeviceName, current.LatencyFrames, current.PollReceiverVolume, current.PreferredStream, ReceiverRetention.ToDays(retention)));
    });

    /// <summary><c>GET /status</c>.</summary>
    public Task<IResult> GetStatusAsync(CancellationToken ct) => RunAsync(async () => Results.Ok(await dtos.StatusAsync(ct).ConfigureAwait(false)));

    /// <summary><c>GET /diagnostics</c>: recent log, local addresses, sender ports and pacer statistics.</summary>
    public Task<IResult> GetDiagnosticsAsync(CancellationToken ct) => RunAsync(() =>
    {
        var sender = senders.Current;
        var player = output.ActivePlayer as GroupPlayer;
        var statistics = runtime.Statistics;
        var lines = logs.Recent(200)
            .Select(l => l.At.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + l.Level + " " + l.Category + ": " + l.Message)
            .ToList();
        var dto = new DiagnosticsDto(
            lines,
            LocalAddresses(),
            sender?.Timing.Port ?? 0,
            sender?.Control.Port ?? 0,
            player?.PacketsSent ?? statistics?.PacketsSent ?? 0,
            sender?.Control.RetransmitRequestsReceived ?? statistics?.RetransmitRequests ?? 0,
            player?.LateWakeups ?? statistics?.LateWakeups ?? 0,
            PendingProbes);
        return Task.FromResult(Results.Ok(dto));
    });

    /// <summary><c>DELETE /spotify</c>: forget the cached credentials.</summary>
    public Task<IResult> ForgetSpotifyAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await spotify.ForgetAsync(ct).ConfigureAwait(false);
        return Results.NoContent();
    });

    /// <summary>The snapshot a new WebSocket client gets first.</summary>
    public Task<IReadOnlyList<BridgeEvent>> GetSnapshotAsync(CancellationToken ct) => dtos.SnapshotAsync(ct);

    /// <summary>The non-loopback unicast addresses of the interfaces that are up (where receivers must reach us).</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => n.Name + ": " + a.Address))
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ValidateLevel(int level)
    {
        if (level is < 0 or > 100)
        {
            throw new ArgumentException("The level must be between 0 and 100.", nameof(level));
        }
    }

    private static string ValidateName(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"The {what} is empty.", nameof(name));
        }

        name = name.Trim();
        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"The {what} is longer than {MaxNameLength} characters.", nameof(name));
        }

        return name;
    }

    private static IResult NotFound(string detail) => Problem(StatusCodes.Status404NotFound, "Not found", detail);

    private static IResult Problem(int status, string title, string detail) => Results.Problem(detail: detail, statusCode: status, title: title);

    private async Task<GroupDto> RequireGroupAsync(string id, CancellationToken ct) =>
        await dtos.GroupAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Unknown group '{id}'.");

    /// <summary>Runs a handler and maps the exceptions of the layers below to problem details (section 7 "Errors").</summary>
    private async Task<IResult> RunAsync(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler().ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "Invalid request", ex.Message);
        }
        catch (RtspStatusException ex)
        {
            logger.LogWarning("Receiver error: {Error}", ex.Message);
            return Problem(StatusCodes.Status502BadGateway, "Receiver error", $"RTSP {ex.Response.StatusCode} {ex.Response.ReasonPhrase}: {ex.Message}");
        }
        catch (PairingException ex)
        {
            logger.LogWarning("Pairing failed: {Error}", ex.Message);
            return Problem(StatusCodes.Status502BadGateway, "Pairing failed", $"{ex.Failure}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or SocketException)
        {
            logger.LogWarning("Receiver unreachable: {Error}", ex.Message);
            return Problem(StatusCodes.Status502BadGateway, "Receiver unreachable", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(StatusCodes.Status409Conflict, "Conflict", ex.Message);
        }
    }
}
