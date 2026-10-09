using System.Reflection;
using Airplay2.Sender.Pairing;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Bridge.Service.Store;

/// <summary>A row of <c>receivers</c>.</summary>
public sealed record ReceiverRow(string Id, string Name, string? Model, string? Manufacturer, string? LastAddress, int LastPort, long Features, long StatusFlags, byte[]? PublicKey, string PairingState, int Level, int OffsetFrames, string? Quirks, string? LastSeen);

/// <summary>A row of <c>groups</c> with its members.</summary>
public sealed record GroupRow(string Id, string Name, int MasterLevel, IReadOnlyList<GroupMemberRow> Members);

/// <summary>A row of <c>group_members</c>.</summary>
public sealed record GroupMemberRow(string GroupId, string ReceiverId, int Level, int Position);

/// <summary>A row of <c>spotify_credentials</c>.</summary>
public sealed record SpotifyCredentialRow(string Username, int AuthType, byte[] AuthData);

/// <summary>
/// Persistence for the bridge (docs/PLAN.md "Persistence"): receivers, pairings, groups, settings and the Spotify
/// credential cache. Also implements <see cref="IPairingStore"/> for the sender. All methods are safe to call from
/// any thread; SQLite serialises writes.
/// </summary>
public interface IBridgeStore : IPairingStore, IAsyncDisposable
{
    /// <summary>Creates or migrates the schema.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ReceiverRow>> GetReceiversAsync(CancellationToken cancellationToken);

    Task<ReceiverRow?> GetReceiverAsync(string id, CancellationToken cancellationToken);

    Task UpsertReceiverAsync(ReceiverRow receiver, CancellationToken cancellationToken);

    Task SetReceiverLevelAsync(string id, int level, CancellationToken cancellationToken);

    Task SetReceiverOffsetAsync(string id, int offsetFrames, CancellationToken cancellationToken);

    Task DeleteReceiverAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<GroupRow>> GetGroupsAsync(CancellationToken cancellationToken);

    Task<GroupRow?> GetGroupAsync(string id, CancellationToken cancellationToken);

    Task<GroupRow> CreateGroupAsync(string name, CancellationToken cancellationToken);

    Task RenameGroupAsync(string id, string name, CancellationToken cancellationToken);

    Task DeleteGroupAsync(string id, CancellationToken cancellationToken);

    Task SetGroupMemberAsync(string groupId, string receiverId, int level, CancellationToken cancellationToken);

    Task RemoveGroupMemberAsync(string groupId, string receiverId, CancellationToken cancellationToken);

    Task SetGroupMasterLevelAsync(string groupId, int level, CancellationToken cancellationToken);

    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken);

    Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken);

    Task<SpotifyCredentialRow?> GetSpotifyCredentialsAsync(CancellationToken cancellationToken);

    Task SaveSpotifyCredentialsAsync(SpotifyCredentialRow credentials, CancellationToken cancellationToken);

    Task ClearSpotifyCredentialsAsync(CancellationToken cancellationToken);
}

/// <summary>Setting keys.</summary>
public static class SettingKeys
{
    public const string DeviceName = "device_name";
    public const string SelectedOutputGroup = "selected_output_group";
    public const string SelectedOutputReceiver = "selected_output_receiver";
    public const string LatencyFrames = "latency_frames";
    public const string PollReceiverVolume = "poll_receiver_volume";
    public const string PreferredStream = "preferred_stream";
    public const string ControllerIdentity = "controller_identity";
}

/// <summary><see cref="IBridgeStore"/> over Microsoft.Data.Sqlite + Dapper. The schema lives in <c>schema.sql</c> (embedded).</summary>
public sealed class SqliteBridgeStore : IBridgeStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteBridgeStore> _logger;

    /// <summary>Creates a store for the database file at <paramref name="path"/> (<c>:memory:</c> is allowed for tests).</summary>
    public SqliteBridgeStore(string path, ILogger<SqliteBridgeStore> logger)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString();
        _logger = logger;
    }

    /// <summary>The embedded schema script.</summary>
    public static string SchemaSql
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bridge.Service.Store.schema.sql")
                               ?? throw new InvalidOperationException("schema.sql is not embedded.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var connection = Open();
        var version = await connection.ExecuteScalarAsync<long>("PRAGMA user_version;").ConfigureAwait(false);
        if (version < 1)
        {
            _logger.LogInformation("Creating bridge schema version 1");
            await connection.ExecuteAsync(SchemaSql).ConfigureAwait(false);
        }

        // TODO(impl: bridge-store): migrations for user_version > 1
    }

    /// <inheritdoc />
    public async Task<ControllerIdentity> GetControllerIdentityAsync(CancellationToken cancellationToken)
    {
        var stored = await GetSettingAsync(SettingKeys.ControllerIdentity, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            var parts = stored.Split(':');
            return new ControllerIdentity(parts[0], Convert.FromHexString(parts[1]));
        }

        var identity = ControllerIdentity.Generate();
        await SetSettingAsync(SettingKeys.ControllerIdentity, identity.Id + ":" + Convert.ToHexString(identity.Ed25519Seed), cancellationToken).ConfigureAwait(false);
        return identity;
    }

    /// <inheritdoc />
    public Task<PairingCredentials?> GetAsync(string receiverId, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task SaveAsync(string receiverId, PairingCredentials credentials, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task DeleteAsync(string receiverId, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task<IReadOnlyList<ReceiverRow>> GetReceiversAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task<ReceiverRow?> GetReceiverAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task UpsertReceiverAsync(ReceiverRow receiver, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task SetReceiverLevelAsync(string id, int level, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task SetReceiverOffsetAsync(string id, int offsetFrames, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task DeleteReceiverAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task<IReadOnlyList<GroupRow>> GetGroupsAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task<GroupRow?> GetGroupAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task<GroupRow> CreateGroupAsync(string name, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task RenameGroupAsync(string id, string name, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task DeleteGroupAsync(string id, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task SetGroupMemberAsync(string groupId, string receiverId, int level, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task RemoveGroupMemberAsync(string groupId, string receiverId, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task SetGroupMasterLevelAsync(string groupId, int level, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken)
    {
        using var connection = Open();
        return await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key;", new { key }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken)
    {
        using var connection = Open();
        await connection.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", new { key, value }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SpotifyCredentialRow?> GetSpotifyCredentialsAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task SaveSpotifyCredentialsAsync(SpotifyCredentialRow credentials, CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public Task ClearSpotifyCredentialsAsync(CancellationToken cancellationToken) => throw new NotImplementedException("TODO(impl: bridge-store)");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
