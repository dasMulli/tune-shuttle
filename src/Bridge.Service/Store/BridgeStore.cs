using Airplay2.Sender.Pairing;

namespace Bridge.Service.Store;

/// <summary>A row of <c>receivers</c> (docs/airplay2/WORK-PLAN.md section 8).</summary>
/// <param name="Id">The receiver's <c>deviceid</c> (primary key).</param>
/// <param name="Name">User-visible name.</param>
/// <param name="Model">TXT <c>model</c>.</param>
/// <param name="Manufacturer">TXT <c>manufacturer</c>.</param>
/// <param name="LastAddress">The last A/AAAA address used.</param>
/// <param name="LastPort">The last control port (7000 on both LAN receivers).</param>
/// <param name="Features">The 64-bit feature word as a signed integer (bit 63 makes it negative).</param>
/// <param name="StatusFlags">The 32-bit status flags.</param>
/// <param name="PublicKey">TXT <c>pk</c>; a change means the receiver was reset (SPEC-control.md 3.8).</param>
/// <param name="PairingState">One of <see cref="PairingStates"/>.</param>
/// <param name="Level">r_i, 0..100, used when the receiver is the output on its own.</param>
/// <param name="OffsetFrames">Per-receiver latency trim (SPEC-audio.md 3.3).</param>
/// <param name="Quirks">JSON object of <c>ReceiverQuirks</c> overrides, or null.</param>
/// <param name="LastSeen">ISO-8601 UTC time of the last discovery record, or null.</param>
public sealed record ReceiverRow(string Id, string Name, string? Model, string? Manufacturer, string? LastAddress, int LastPort, long Features, long StatusFlags, byte[]? PublicKey, string PairingState, int Level, int OffsetFrames, string? Quirks, string? LastSeen);

/// <summary>A row of <c>groups</c> with its members ordered by <see cref="GroupMemberRow.Position"/>.</summary>
/// <param name="Id">UUID, also sent as <c>groupUUID</c> in the session SETUP.</param>
/// <param name="Name">User-visible name.</param>
/// <param name="MasterLevel">G, 0..100.</param>
/// <param name="Members">The members in display order.</param>
public sealed record GroupRow(string Id, string Name, int MasterLevel, IReadOnlyList<GroupMemberRow> Members);

/// <summary>A row of <c>group_members</c>.</summary>
/// <param name="GroupId">The group.</param>
/// <param name="ReceiverId">The receiver.</param>
/// <param name="Level">r_i inside this group, 0..100.</param>
/// <param name="Position">Display order inside the group (ascending; gaps are allowed).</param>
public sealed record GroupMemberRow(string GroupId, string ReceiverId, int Level, int Position);

/// <summary>A row of <c>spotify_credentials</c>: the reusable librespot credentials (username, auth type, blob).</summary>
/// <param name="Username">The Spotify username.</param>
/// <param name="AuthType">librespot <c>AuthenticationType</c> as an integer.</param>
/// <param name="AuthData">The opaque credential blob.</param>
public sealed record SpotifyCredentialRow(string Username, int AuthType, byte[] AuthData);

/// <summary>
/// Persistence for the bridge (docs/PLAN.md "Persistence"): receivers, pairings, groups, settings and the Spotify
/// credential cache. Also implements <see cref="IPairingStore"/> for the sender. All methods are safe to call from
/// any thread; SQLite serialises writes.
/// </summary>
/// <remarks>
/// Error conventions: setters that address one existing row (<see cref="SetReceiverLevelAsync"/>,
/// <see cref="SetReceiverOffsetAsync"/>, <see cref="RenameGroupAsync"/>, <see cref="SetGroupMemberAsync"/>,
/// <see cref="SetGroupMasterLevelAsync"/>) throw <see cref="KeyNotFoundException"/> for an unknown id (the API maps it to
/// 404); deletes and removes are idempotent; levels outside 0..100 and empty ids or names throw
/// <see cref="ArgumentException"/> (the API maps it to 422).
/// </remarks>
public interface IBridgeStore : IPairingStore, IAsyncDisposable
{
    /// <summary>Creates or migrates the schema (<c>PRAGMA user_version</c>) and switches the file to WAL. Idempotent; safe to run concurrently.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>All receivers ordered by name.</summary>
    Task<IReadOnlyList<ReceiverRow>> GetReceiversAsync(CancellationToken cancellationToken);

    /// <summary>One receiver, or null.</summary>
    Task<ReceiverRow?> GetReceiverAsync(string id, CancellationToken cancellationToken);

    /// <summary>Inserts the receiver or overwrites every column of the existing row (<c>created_at</c> is kept).</summary>
    Task UpsertReceiverAsync(ReceiverRow receiver, CancellationToken cancellationToken);

    /// <summary>Sets r_i of a receiver used on its own. Throws <see cref="KeyNotFoundException"/> for an unknown id.</summary>
    Task SetReceiverLevelAsync(string id, int level, CancellationToken cancellationToken);

    /// <summary>Sets the per-receiver latency trim. Throws <see cref="KeyNotFoundException"/> for an unknown id.</summary>
    Task SetReceiverOffsetAsync(string id, int offsetFrames, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a receiver's <c>last_seen</c> forward to <paramref name="lastSeen"/> (never back); false when the row is
    /// unknown or already newer. Discovery writes the whole row only when a record changes, so the inventory touches it
    /// when a receiver goes away and the pruning job while receivers stay online.
    /// </summary>
    Task<bool> TouchReceiverAsync(string id, DateTimeOffset lastSeen, CancellationToken cancellationToken);

    /// <summary>Deletes a receiver with its pairing and group memberships (cascade) and clears it as the selected output.</summary>
    Task DeleteReceiverAsync(string id, CancellationToken cancellationToken);

    /// <summary>All groups with their members, in creation order.</summary>
    Task<IReadOnlyList<GroupRow>> GetGroupsAsync(CancellationToken cancellationToken);

    /// <summary>One group with its members, or null.</summary>
    Task<GroupRow?> GetGroupAsync(string id, CancellationToken cancellationToken);

    /// <summary>Creates an empty group with a new upper-case UUID and the default master level.</summary>
    Task<GroupRow> CreateGroupAsync(string name, CancellationToken cancellationToken);

    /// <summary>Renames a group. Throws <see cref="KeyNotFoundException"/> for an unknown id.</summary>
    Task RenameGroupAsync(string id, string name, CancellationToken cancellationToken);

    /// <summary>Deletes a group with its members (cascade) and clears it as the selected output.</summary>
    Task DeleteGroupAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a receiver to a group (appended at the end) or updates its level when it is already a member. Throws
    /// <see cref="KeyNotFoundException"/> when the group or the receiver does not exist.
    /// </summary>
    Task SetGroupMemberAsync(string groupId, string receiverId, int level, CancellationToken cancellationToken);

    /// <summary>Removes a receiver from a group; no-op when it is not a member.</summary>
    Task RemoveGroupMemberAsync(string groupId, string receiverId, CancellationToken cancellationToken);

    /// <summary>Sets G of a group (the member levels are scaled by the caller). Throws <see cref="KeyNotFoundException"/> for an unknown id.</summary>
    Task SetGroupMasterLevelAsync(string groupId, int level, CancellationToken cancellationToken);

    /// <summary>A setting value (<see cref="SettingKeys"/>), or null when unset.</summary>
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken);

    /// <summary>Sets a setting; null removes it.</summary>
    Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken);

    /// <summary>The cached Spotify credentials (the most recently saved), or null.</summary>
    Task<SpotifyCredentialRow?> GetSpotifyCredentialsAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the cached Spotify credentials (one account is cached at a time).</summary>
    Task SaveSpotifyCredentialsAsync(SpotifyCredentialRow credentials, CancellationToken cancellationToken);

    /// <summary>Forgets the cached Spotify credentials.</summary>
    Task ClearSpotifyCredentialsAsync(CancellationToken cancellationToken);
}

/// <summary>Setting keys.</summary>
public static class SettingKeys
{
    /// <summary>The Spotify Connect device name.</summary>
    public const string DeviceName = "device_name";

    /// <summary>The group selected as output (exclusive with <see cref="SelectedOutputReceiver"/>).</summary>
    public const string SelectedOutputGroup = "selected_output_group";

    /// <summary>The single receiver selected as output.</summary>
    public const string SelectedOutputReceiver = "selected_output_receiver";

    /// <summary>Sender latency L in frames.</summary>
    public const string LatencyFrames = "latency_frames";

    /// <summary>"true"/"false": poll receiver volume as a fallback to the event channel.</summary>
    public const string PollReceiverVolume = "poll_receiver_volume";

    /// <summary>Preferred stream type ("realtime" or "buffered").</summary>
    public const string PreferredStream = "preferred_stream";

    /// <summary>The controller identity: <c>&lt;id&gt;:&lt;hex Ed25519 seed&gt;</c> (SPEC-control.md 3.6, 3.8).</summary>
    public const string ControllerIdentity = "controller_identity";
}

/// <summary>Values of <c>receivers.pairing_state</c>.</summary>
public static class PairingStates
{
    /// <summary>Transient pairing on every connection; nothing stored.</summary>
    public const string Transient = "transient";

    /// <summary>The receiver needs a PIN (or password) pairing that has not been done, or was dropped after a failed pair-verify.</summary>
    public const string PinRequired = "pin_required";

    /// <summary>A regular pairing is stored in <c>pairings</c>.</summary>
    public const string Paired = "paired";

    /// <summary>Only HomeKit home members may connect (<c>acl=1</c> or <c>act=2</c>).</summary>
    public const string Disabled = "disabled";

    /// <summary>True for one of the four values.</summary>
    public static bool IsValid(string? value) => value is Transient or PinRequired or Paired or Disabled;
}
