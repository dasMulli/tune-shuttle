using System.Net;
using Bridge.Contracts;
using Bridge.Web.Localization;
using Microsoft.Extensions.Logging;

namespace Bridge.Web;

/// <summary>
/// The actions the pages trigger: each calls <see cref="BridgeApiClient"/>, applies the answer to <see cref="BridgeState"/>
/// at once (the matching WebSocket event arrives shortly after and carries the same data), and turns failures into a
/// notice with the service's problem details. Methods return false (or null) on failure and never throw for API,
/// network or timeout errors, so a click handler cannot crash the app; cancellation still propagates. Notice texts come
/// from the catalog (<c>notices.*</c> around an <c>actions.*</c> phrase, UI-SPEC 4.9) in the language current when the
/// command runs; service-originated text (problem details) stays as the service sends it.
/// </summary>
public sealed class BridgeCommands
{
    private readonly BridgeApiClient _api;
    private static readonly Lazy<I18n> s_english = new(() => new I18n(I18n.DefaultCulture));

    private readonly BridgeState _state;
    private readonly I18n _l;
    private readonly ILogger<BridgeCommands> _logger;

    /// <summary>Creates the commands.</summary>
    public BridgeCommands(BridgeApiClient api, BridgeState state, I18n i18n, ILogger<BridgeCommands> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(i18n);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _state = state;
        _l = i18n;
        _logger = logger;
    }

    /// <summary>The API client (pages that only read, such as diagnostics and settings, call it directly).</summary>
    public BridgeApiClient Api => _api;

    /// <summary>Loads everything over REST into the state (start-up; skipped by the state once the WebSocket snapshot is in).</summary>
    public Task<bool> RefreshAsync(CancellationToken ct) => RunAsync(_l["actions.refresh"], async () =>
    {
        _state.Load(await _api.GetSnapshotAsync(ct).ConfigureAwait(false));
    });

    // ---- receivers -----------------------------------------------------------------------------------------------

    /// <summary>Connects a receiver as the single output.</summary>
    public Task<bool> ConnectAsync(string receiverId, CancellationToken ct) => RunAsync(_l.Format("actions.connect", ("name", _state.NameOf(receiverId))), async () =>
    {
        var receiver = await _api.ConnectAsync(receiverId, ct).ConfigureAwait(false);
        _state.ApplyReceiver(receiver);
        if (receiver.State == ReceiverConnectionState.Error && receiver.Pairing != PairingState.PinRequired)
        {
            _state.Notify(NoticeKind.Error, _l.Format("notices.connectFailed", ("name", receiver.Name), ("error", receiver.Error ?? _l["notices.connectFailedDefault"])));
        }
    });

    /// <summary>Disconnects a receiver.</summary>
    public Task<bool> DisconnectAsync(string receiverId, CancellationToken ct) => RunAsync(_l.Format("actions.disconnect", ("name", _state.NameOf(receiverId))), async () =>
        _state.ApplyReceiver(await _api.DisconnectAsync(receiverId, ct).ConfigureAwait(false)));

    /// <summary>Sets r_i of a receiver (its member level inside the active group).</summary>
    public Task<bool> SetReceiverLevelAsync(string receiverId, int level, CancellationToken ct) => RunAsync(_l.Format("actions.setVolume", ("name", _state.NameOf(receiverId))), async () =>
        _state.ApplyReceiver(await _api.SetReceiverVolumeAsync(receiverId, Math.Clamp(level, 0, 100), ct).ConfigureAwait(false)));

    /// <summary>Sets a receiver's latency trim in frames (±2 s).</summary>
    public Task<bool> SetOffsetAsync(string receiverId, int offsetFrames, CancellationToken ct) => RunAsync(_l.Format("actions.setOffset", ("name", _state.NameOf(receiverId))), async () =>
        _state.ApplyReceiver(await _api.SetReceiverOffsetAsync(receiverId, offsetFrames, ct).ConfigureAwait(false)));

    /// <summary>Starts a PIN pairing: the receiver shows its PIN and the dialog opens.</summary>
    public Task<bool> StartPairingAsync(string receiverId, CancellationToken ct) => RunAsync(_l.Format("actions.startPairing", ("name", _state.NameOf(receiverId))), async () =>
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
            return new PinResult(PinOutcome.Invalid, _l["pairing.invalid"]);
        }

        try
        {
            var receiver = await _api.FinishPairingAsync(prompt.ReceiverId, new PairingPinRequest(prompt.Token, pin.Trim()), ct).ConfigureAwait(false);
            _state.ApplyReceiver(receiver);
            _state.ClosePairing(prompt.Token);
            _state.Notify(NoticeKind.Success, _l.Format("notices.paired", ("name", receiver.Name)));
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
            return new PinResult(PinOutcome.Failed, Describe(ex, _l));
        }
    }

    /// <summary>Forgets a stored pairing.</summary>
    public Task<bool> ForgetPairingAsync(string receiverId, CancellationToken ct) => RunAsync(_l.Format("actions.forgetPairing", ("name", _state.NameOf(receiverId))), async () =>
    {
        await _api.ForgetPairingAsync(receiverId, ct).ConfigureAwait(false);
        _state.ApplyReceiver(await _api.GetReceiverAsync(receiverId, ct).ConfigureAwait(false));
    });

    // ---- groups --------------------------------------------------------------------------------------------------

    /// <summary>Creates a group; returns it, or null on failure.</summary>
    public async Task<GroupDto?> CreateGroupAsync(string name, CancellationToken ct)
    {
        GroupDto? created = null;
        await RunAsync(_l.Format("actions.createGroup", ("name", name)), async () =>
        {
            created = await _api.CreateGroupAsync(name, ct).ConfigureAwait(false);
            _state.ApplyGroup(created);
        }).ConfigureAwait(false);
        return created;
    }

    /// <summary>Renames a group.</summary>
    public Task<bool> RenameGroupAsync(string groupId, string name, CancellationToken ct) => RunAsync(_l["actions.renameGroup"], async () =>
        _state.ApplyGroup(await _api.RenameGroupAsync(groupId, name, ct).ConfigureAwait(false)));

    /// <summary>Deletes a group.</summary>
    public Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct) => RunAsync(_l["actions.deleteGroup"], async () =>
    {
        await _api.DeleteGroupAsync(groupId, ct).ConfigureAwait(false);
        _state.RemoveGroup(groupId);
    });

    /// <summary>Adds a member (with the receiver's own level unless given).</summary>
    public Task<bool> AddMemberAsync(string groupId, string receiverId, int? level, CancellationToken ct) => RunAsync(_l.Format("actions.addMember", ("name", _state.NameOf(receiverId))), async () =>
        _state.ApplyGroup(await _api.AddGroupMemberAsync(groupId, receiverId, level, ct).ConfigureAwait(false)));

    /// <summary>Removes a member.</summary>
    public Task<bool> RemoveMemberAsync(string groupId, string receiverId, CancellationToken ct) => RunAsync(_l.Format("actions.removeMember", ("name", _state.NameOf(receiverId))), async () =>
        _state.ApplyGroup(await _api.RemoveGroupMemberAsync(groupId, receiverId, ct).ConfigureAwait(false)));

    /// <summary>
    /// Sets a member's r_i. In the active group this is the receiver's volume route (it reaches the receiver at once); in
    /// an inactive group the member is re-added with the new level (stored for the next activation).
    /// </summary>
    public Task<bool> SetMemberLevelAsync(string groupId, string receiverId, int level, CancellationToken ct) => RunAsync(_l.Format("actions.setVolume", ("name", _state.NameOf(receiverId))), async () =>
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
    public Task<bool> SetGroupLevelAsync(string groupId, int level, CancellationToken ct) => RunAsync(_l["actions.setGroupVolume"], async () =>
        _state.ApplyGroup(await _api.SetGroupVolumeAsync(groupId, Math.Clamp(level, 0, 100), ct).ConfigureAwait(false)));

    /// <summary>Selects a group as the output.</summary>
    public Task<bool> ActivateGroupAsync(string groupId, CancellationToken ct) => RunAsync(_l["actions.activateGroup"], async () =>
        _state.ApplyOutput(await _api.ActivateGroupAsync(groupId, ct).ConfigureAwait(false)));

    /// <summary>Deselects the output (audio is discarded until something is selected).</summary>
    public Task<bool> ClearOutputAsync(CancellationToken ct) => RunAsync(_l["actions.stopOutput"], async () =>
        _state.ApplyOutput(await _api.SetOutputAsync(new OutputRequest(null, null), ct).ConfigureAwait(false)));

    // ---- playback, settings --------------------------------------------------------------------------------------

    /// <summary>
    /// Sends <c>"play"</c>, <c>"pause"</c>, <c>"next"</c> or <c>"prev"</c> to Spotify. Any other value throws
    /// <see cref="ArgumentOutOfRangeException"/> (the four transport buttons are the only callers), so no raw command word
    /// ends up in a notice sentence.
    /// </summary>
    public Task<bool> PlaybackAsync(string command, CancellationToken ct)
    {
        var what = command switch
        {
            "play" => _l["actions.playbackPlay"],
            "pause" => _l["actions.playbackPause"],
            "next" => _l["actions.playbackNext"],
            "prev" => _l["actions.playbackPrevious"],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Expected play, pause, next or prev."),
        };
        return RunAsync(what, async () =>
            _state.ApplyPlayback(await _api.SendPlaybackCommandAsync(command, ct).ConfigureAwait(false)));
    }

    /// <summary>Saves the settings; returns them as stored, or null on failure.</summary>
    public async Task<SettingsDto?> SaveSettingsAsync(SettingsDto settings, CancellationToken ct)
    {
        SettingsDto? saved = null;
        var ok = await RunAsync(_l["actions.saveSettings"], async () => saved = await _api.SetSettingsAsync(settings, ct).ConfigureAwait(false)).ConfigureAwait(false);
        if (ok)
        {
            _state.Notify(NoticeKind.Success, _l["notices.settingsSaved"]);
        }

        return saved;
    }

    /// <summary>Forgets the cached Spotify credentials.</summary>
    public async Task<bool> ForgetSpotifyAsync(CancellationToken ct)
    {
        var ok = await RunAsync(_l["actions.forgetSpotify"], () => _api.ForgetSpotifyAsync(ct)).ConfigureAwait(false);
        if (ok)
        {
            _state.Notify(NoticeKind.Info, _l["notices.spotifyForgotten"]);
        }

        return ok;
    }

    /// <summary>
    /// A one-line description of a failure for a notice or an inline error: the service's problem details as it sends
    /// them (title and detail, untranslated, UI-SPEC 3.5), otherwise notices.unreachable / notices.timeout /
    /// notices.unreadable (also a success answer whose body is unreadable or empty) / notices.httpError (an error status
    /// without problem details, a proxy's error page, say) from <paramref name="i18n"/> (pages pass their <c>L</c>;
    /// English when null). Client and runtime
    /// exception text (the browser's "TypeError: Failed to fetch", JSON parser messages) is never shown; callers log it.
    /// </summary>
    public static string Describe(Exception ex, I18n? i18n = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var l = i18n ?? s_english.Value;
        return ex switch
        {
            BridgeApiException { IsProblemDetails: true } api => $"{api.Title}: {api.Detail}",
            BridgeApiException { StatusCode: var s } when (int)s is >= 200 and < 300 => l["notices.unreadable"],
            BridgeApiException api => l.Format("notices.httpError", ("status", (int)api.StatusCode)),
            HttpRequestException => l["notices.unreachable"],
            TaskCanceledException or TimeoutException => l["notices.timeout"],
            _ => l["notices.unreadable"],
        };
    }

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
            _state.Notify(NoticeKind.Error, _l.Format("notices.failed", ("what", what), ("detail", Describe(ex, _l))));
            return false;
        }
    }

    /// <summary>
    /// True for the exceptions a request can fail with that the app turns into catalog text (<see cref="Describe"/>):
    /// the service's problem details, an unreachable bridge, a body that does not parse, and a timeout, which
    /// <see cref="HttpClient"/> reports as a <see cref="TaskCanceledException"/> with a <see cref="TimeoutException"/>
    /// inside while <paramref name="ct"/> (the caller's own token) is still live. A cancellation of <paramref name="ct"/>
    /// is not a failure: the caller went away. Pages use it as their catch filter (Settings, Diagnostics).
    /// </summary>
    internal static bool IsFailure(Exception ex, CancellationToken ct) =>
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
