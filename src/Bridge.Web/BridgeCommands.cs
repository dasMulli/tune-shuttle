using System.Net;
using Bridge.Contracts;
using Microsoft.Extensions.Logging;

namespace Bridge.Web;

/// <summary>
/// The actions the pages trigger: each calls <see cref="BridgeApiClient"/>, applies the answer to <see cref="BridgeState"/>
/// at once (the matching WebSocket event arrives shortly after and carries the same data), and turns failures into a
/// banner notice with the service's problem details. Methods return false (or null) on failure and never throw for API,
/// network or timeout errors, so a click handler cannot crash the app; cancellation still propagates.
/// </summary>
public sealed class BridgeCommands
{
    private readonly BridgeApiClient _api;
    private readonly BridgeState _state;
    private readonly ILogger<BridgeCommands> _logger;

    /// <summary>Creates the commands.</summary>
    public BridgeCommands(BridgeApiClient api, BridgeState state, ILogger<BridgeCommands> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _state = state;
        _logger = logger;
    }

    /// <summary>The API client (pages that only read, such as diagnostics and settings, call it directly).</summary>
    public BridgeApiClient Api => _api;

    /// <summary>Loads everything over REST into the state (start-up; skipped by the state once the WebSocket snapshot is in).</summary>
    public Task<bool> RefreshAsync(CancellationToken ct) => RunAsync("Loading the bridge state", async () =>
    {
        _state.Load(await _api.GetSnapshotAsync(ct).ConfigureAwait(false));
    });

    // ---- receivers -----------------------------------------------------------------------------------------------

    /// <summary>Connects a receiver as the single output.</summary>
    public Task<bool> ConnectAsync(string receiverId, CancellationToken ct) => RunAsync($"Connecting {_state.NameOf(receiverId)}", async () =>
    {
        var receiver = await _api.ConnectAsync(receiverId, ct).ConfigureAwait(false);
        _state.ApplyReceiver(receiver);
        if (receiver.State == ReceiverConnectionState.Error && receiver.Pairing != PairingState.PinRequired)
        {
            _state.Notify(NoticeKind.Error, $"{receiver.Name}: {receiver.Error ?? "the connection failed."}");
        }
    });

    /// <summary>Disconnects a receiver.</summary>
    public Task<bool> DisconnectAsync(string receiverId, CancellationToken ct) => RunAsync($"Disconnecting {_state.NameOf(receiverId)}", async () =>
        _state.ApplyReceiver(await _api.DisconnectAsync(receiverId, ct).ConfigureAwait(false)));

    /// <summary>Sets r_i of a receiver (its member level inside the active group).</summary>
    public Task<bool> SetReceiverLevelAsync(string receiverId, int level, CancellationToken ct) => RunAsync($"Setting the volume of {_state.NameOf(receiverId)}", async () =>
        _state.ApplyReceiver(await _api.SetReceiverVolumeAsync(receiverId, Math.Clamp(level, 0, 100), ct).ConfigureAwait(false)));

    /// <summary>Sets a receiver's latency trim in frames (±2 s).</summary>
    public Task<bool> SetOffsetAsync(string receiverId, int offsetFrames, CancellationToken ct) => RunAsync($"Setting the offset of {_state.NameOf(receiverId)}", async () =>
        _state.ApplyReceiver(await _api.SetReceiverOffsetAsync(receiverId, offsetFrames, ct).ConfigureAwait(false)));

    /// <summary>Starts a PIN pairing: the receiver shows its PIN and the dialog opens.</summary>
    public Task<bool> StartPairingAsync(string receiverId, CancellationToken ct) => RunAsync($"Starting the pairing with {_state.NameOf(receiverId)}", async () =>
    {
        var started = await _api.StartPairingAsync(receiverId, ct).ConfigureAwait(false);
        _state.OpenPairing(new PairingPrompt(receiverId, started.PairingToken, started.Message, PairingPromptOrigin.User));
    });

    /// <summary>
    /// Sends the PIN. Returns the outcome so the dialog can react: a wrong PIN (422) or an expired token (409) consume the
    /// pairing on the service side, so the dialog offers to start again.
    /// </summary>
    public async Task<PinResult> SubmitPinAsync(PairingPrompt prompt, string pin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrWhiteSpace(pin))
        {
            return new PinResult(PinOutcome.Invalid, "Enter the PIN the receiver shows.");
        }

        try
        {
            var receiver = await _api.FinishPairingAsync(prompt.ReceiverId, new PairingPinRequest(prompt.Token, pin.Trim()), ct).ConfigureAwait(false);
            _state.ApplyReceiver(receiver);
            _state.ClosePairing(prompt.Token);
            _state.Notify(NoticeKind.Success, $"Paired with {receiver.Name}.");
            return new PinResult(PinOutcome.Paired, null);
        }
        catch (BridgeApiException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            return new PinResult(PinOutcome.Rejected, ex.Detail);
        }
        catch (BridgeApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return new PinResult(PinOutcome.Expired, ex.Detail);
        }
        catch (Exception ex) when (IsFailure(ex, ct))
        {
            _logger.LogWarning("Pairing with {Receiver} failed: {Error}", prompt.ReceiverId, ex.Message);
            return new PinResult(PinOutcome.Failed, Describe(ex));
        }
    }

    /// <summary>Forgets a stored pairing.</summary>
    public Task<bool> ForgetPairingAsync(string receiverId, CancellationToken ct) => RunAsync($"Forgetting the pairing of {_state.NameOf(receiverId)}", async () =>
    {
        await _api.ForgetPairingAsync(receiverId, ct).ConfigureAwait(false);
        _state.ApplyReceiver(await _api.GetReceiverAsync(receiverId, ct).ConfigureAwait(false));
    });

    // ---- groups --------------------------------------------------------------------------------------------------

    /// <summary>Creates a group; returns it, or null on failure.</summary>
    public async Task<GroupDto?> CreateGroupAsync(string name, CancellationToken ct)
    {
        GroupDto? created = null;
        await RunAsync($"Creating the group {name}", async () =>
        {
            created = await _api.CreateGroupAsync(name, ct).ConfigureAwait(false);
            _state.ApplyGroup(created);
        }).ConfigureAwait(false);
        return created;
    }

    /// <summary>Renames a group.</summary>
    public Task<bool> RenameGroupAsync(string groupId, string name, CancellationToken ct) => RunAsync("Renaming the group", async () =>
        _state.ApplyGroup(await _api.RenameGroupAsync(groupId, name, ct).ConfigureAwait(false)));

    /// <summary>Deletes a group.</summary>
    public Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct) => RunAsync("Deleting the group", async () =>
    {
        await _api.DeleteGroupAsync(groupId, ct).ConfigureAwait(false);
        _state.RemoveGroup(groupId);
    });

    /// <summary>Adds a member (with the receiver's own level unless given).</summary>
    public Task<bool> AddMemberAsync(string groupId, string receiverId, int? level, CancellationToken ct) => RunAsync($"Adding {_state.NameOf(receiverId)}", async () =>
        _state.ApplyGroup(await _api.AddGroupMemberAsync(groupId, receiverId, level, ct).ConfigureAwait(false)));

    /// <summary>Removes a member.</summary>
    public Task<bool> RemoveMemberAsync(string groupId, string receiverId, CancellationToken ct) => RunAsync($"Removing {_state.NameOf(receiverId)}", async () =>
        _state.ApplyGroup(await _api.RemoveGroupMemberAsync(groupId, receiverId, ct).ConfigureAwait(false)));

    /// <summary>
    /// Sets a member's r_i. In the active group this is the receiver's volume route (it reaches the receiver at once); in
    /// an inactive group the member is re-added with the new level (stored for the next activation).
    /// </summary>
    public Task<bool> SetMemberLevelAsync(string groupId, string receiverId, int level, CancellationToken ct) => RunAsync($"Setting the volume of {_state.NameOf(receiverId)}", async () =>
    {
        level = Math.Clamp(level, 0, 100);
        if (_state.Output.GroupId == groupId)
        {
            // The answer is the receiver; the member level is what was sent (volumeChanged and groupChanged settle the rest).
            _state.ApplyReceiver(await _api.SetReceiverVolumeAsync(receiverId, level, ct).ConfigureAwait(false));
            _state.ApplyMemberLevel(groupId, receiverId, level);
        }
        else
        {
            _state.ApplyGroup(await _api.AddGroupMemberAsync(groupId, receiverId, level, ct).ConfigureAwait(false));
        }
    });

    /// <summary>Sets G of a group; the service scales the members.</summary>
    public Task<bool> SetGroupLevelAsync(string groupId, int level, CancellationToken ct) => RunAsync("Setting the group volume", async () =>
        _state.ApplyGroup(await _api.SetGroupVolumeAsync(groupId, Math.Clamp(level, 0, 100), ct).ConfigureAwait(false)));

    /// <summary>Selects a group as the output.</summary>
    public Task<bool> ActivateGroupAsync(string groupId, CancellationToken ct) => RunAsync("Activating the group", async () =>
        _state.ApplyOutput(await _api.ActivateGroupAsync(groupId, ct).ConfigureAwait(false)));

    /// <summary>Deselects the output (audio is discarded until something is selected).</summary>
    public Task<bool> ClearOutputAsync(CancellationToken ct) => RunAsync("Stopping the output", async () =>
        _state.ApplyOutput(await _api.SetOutputAsync(new OutputRequest(null, null), ct).ConfigureAwait(false)));

    // ---- playback, settings --------------------------------------------------------------------------------------

    /// <summary>Sends play, pause, next or prev to Spotify.</summary>
    public Task<bool> PlaybackAsync(string command, CancellationToken ct) => RunAsync($"Sending {command}", async () =>
        _state.ApplyPlayback(await _api.SendPlaybackCommandAsync(command, ct).ConfigureAwait(false)));

    /// <summary>Saves the settings; returns them as stored, or null on failure.</summary>
    public async Task<SettingsDto?> SaveSettingsAsync(SettingsDto settings, CancellationToken ct)
    {
        SettingsDto? saved = null;
        var ok = await RunAsync("Saving the settings", async () => saved = await _api.SetSettingsAsync(settings, ct).ConfigureAwait(false)).ConfigureAwait(false);
        if (ok)
        {
            _state.Notify(NoticeKind.Success, "Settings saved.");
        }

        return saved;
    }

    /// <summary>Forgets the cached Spotify credentials.</summary>
    public async Task<bool> ForgetSpotifyAsync(CancellationToken ct)
    {
        var ok = await RunAsync("Forgetting the Spotify login", () => _api.ForgetSpotifyAsync(ct)).ConfigureAwait(false);
        if (ok)
        {
            _state.Notify(NoticeKind.Info, "The Spotify login is forgotten; select the bridge in a Spotify app to log in again.");
        }

        return ok;
    }

    /// <summary>A one-line description of a failure for a notice.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        BridgeApiException api => $"{api.Title}: {api.Detail}",
        HttpRequestException => "The bridge is not reachable: " + ex.Message,
        TaskCanceledException or TimeoutException => "The bridge did not answer in time.",
        _ => ex.Message,
    };

    private async Task<bool> RunAsync(string what, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsFailure(ex, CancellationToken.None))
        {
            _logger.LogWarning("{What} failed: {Error}", what, ex.Message);
            _state.Notify(NoticeKind.Error, $"{what} failed. {Describe(ex)}");
            return false;
        }
    }

    private static bool IsFailure(Exception ex, CancellationToken ct) =>
        ex is BridgeApiException or HttpRequestException or TimeoutException or System.Text.Json.JsonException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested && ex.InnerException is TimeoutException);
}

/// <summary>Outcome of sending a PIN.</summary>
public enum PinOutcome
{
    /// <summary>Paired; the keys are stored.</summary>
    Paired,

    /// <summary>Nothing sent (empty PIN).</summary>
    Invalid,

    /// <summary>The receiver rejected the PIN; the pairing must be started again.</summary>
    Rejected,

    /// <summary>The pairing token is unknown or expired; start again.</summary>
    Expired,

    /// <summary>Another failure (receiver unreachable, network).</summary>
    Failed,
}

/// <summary>Result of <see cref="BridgeCommands.SubmitPinAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">The service's explanation, or null.</param>
public sealed record PinResult(PinOutcome Outcome, string? Message);
