using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Airplay2.Sender.Pairing;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bridge.Service.Store;

/// <summary>
/// <see cref="IBridgeStore"/> over Microsoft.Data.Sqlite + Dapper. The schema lives in <c>schema.sql</c> (embedded,
/// <c>user_version = 1</c>); later versions come from <see cref="StoreMigrations"/>.
/// </summary>
/// <remarks>
/// <para>
/// Connections: one pooled connection per call, foreign keys switched on by the connection string (the pragma is
/// per connection, so the one in <c>schema.sql</c> alone would not enforce the cascades), journal mode WAL so readers
/// never wait for the writer. Writes run in <c>BEGIN IMMEDIATE</c> transactions behind an in-process async gate: within
/// one process SQLite therefore never sees two writers (Microsoft.Data.Sqlite's own busy retry sleeps the thread, the
/// gate waits asynchronously); a second process is still handled by the busy timeout.
/// </para>
/// <para>
/// Microsoft.Data.Sqlite executes commands synchronously behind its async API; statements here are small and local,
/// which is accepted (docs/ARCHITECTURE.md, "Design decisions": SQLite + Dapper for exactly this size of data).
/// </para>
/// </remarks>
public sealed class SqliteBridgeStore : IBridgeStore
{
    /// <summary>r_i and G default (matches the <c>DEFAULT 50</c> in <c>schema.sql</c>).</summary>
    public const int DefaultLevel = 50;

    private const string MemoryPath = ":memory:";
    private const string ReceiverColumns =
        "id AS Id, name AS Name, model AS Model, manufacturer AS Manufacturer, last_address AS LastAddress, last_port AS LastPort, " +
        "features AS Features, status_flags AS StatusFlags, public_key AS PublicKey, pairing_state AS PairingState, level AS Level, " +
        "offset_frames AS OffsetFrames, quirks AS Quirks, last_seen AS LastSeen";
    private const string NowSql = "strftime('%Y-%m-%dT%H:%M:%fZ', 'now')";

    private readonly string _connectionString;
    private readonly ILogger<SqliteBridgeStore> _logger;
    private readonly IReadOnlyList<StoreMigration> _migrations;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly bool _inMemory;
    private SqliteConnection? _memoryKeepAlive;
    private int _disposed;

    /// <summary>Creates a store for the database file at <paramref name="path"/> (<c>:memory:</c> is allowed for tests).</summary>
    public SqliteBridgeStore(string path, ILogger<SqliteBridgeStore> logger)
        : this(path, logger, StoreMigrations.All)
    {
    }

    /// <summary>Creates a store with an explicit migration list (tests exercise upgrades with synthetic migrations).</summary>
    internal SqliteBridgeStore(string path, ILogger<SqliteBridgeStore> logger, IReadOnlyList<StoreMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        StoreMigrations.Validate(migrations);
        _logger = logger;
        _migrations = migrations;
        _inMemory = path == MemoryPath;

        var builder = new SqliteConnectionStringBuilder
        {
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 30, // seconds; also the busy-retry budget against another process
        };
        if (_inMemory)
        {
            // A named shared-cache memory database lives as long as one connection is open; the keep-alive (opened on first use) holds it.
            builder.DataSource = "bridge-" + Guid.NewGuid().ToString("N");
            builder.Mode = SqliteOpenMode.Memory;
            builder.Cache = SqliteCacheMode.Shared;
        }
        else
        {
            // Private cache: shared-cache mode takes table locks that bypass the busy timeout and defeat WAL.
            builder.DataSource = Path.GetFullPath(path);
            builder.Mode = SqliteOpenMode.ReadWriteCreate;
            builder.Cache = SqliteCacheMode.Private;
        }

        _connectionString = builder.ToString();
        DatabasePath = _inMemory ? MemoryPath : builder.DataSource;
    }

    /// <summary>The full path of the database file, or <c>:memory:</c>.</summary>
    public string DatabasePath { get; }

    /// <summary>The schema version this build creates and migrates to.</summary>
    public int TargetVersion => StoreMigrations.LatestVersion(_migrations);

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

    // ---------------------------------------------------------------- schema

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            // journal_mode is persistent in the file but cannot be changed inside a transaction, so it is set here, outside.
            var journalMode = await connection.ExecuteScalarAsync<string>(Command("PRAGMA journal_mode = WAL;", null, null, cancellationToken)).ConfigureAwait(false);
            if (!_inMemory && !string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("SQLite refused WAL for {Database}; journal mode is {JournalMode}", DatabasePath, journalMode);
            }

            var version = await ReadUserVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
            if (version > TargetVersion)
            {
                throw new InvalidOperationException(
                    $"The database {DatabasePath} has schema version {version}, newer than this build's {TargetVersion}. Use a newer bridge or another data directory.");
            }

            if (version < StoreMigrations.BaseVersion)
            {
                // schema.sql is idempotent (CREATE ... IF NOT EXISTS) and sets user_version last, so a crash halfway or a
                // second process doing the same at the same time leaves a state this code completes on the next run.
                _logger.LogInformation("Creating bridge schema version {Version} in {Database}", StoreMigrations.BaseVersion, DatabasePath);
                await connection.ExecuteAsync(Command(SchemaSql, null, null, cancellationToken)).ConfigureAwait(false);
                version = StoreMigrations.BaseVersion;
            }

            foreach (var migration in _migrations)
            {
                if (migration.Version <= version)
                {
                    continue;
                }

                await using var transaction = connection.BeginTransaction(deferred: false);
                var current = await ReadUserVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                if (current >= migration.Version)
                {
                    version = current; // another process got there first
                    continue;
                }

                _logger.LogInformation("Migrating bridge schema {From} -> {To}: {Description}", current, migration.Version, migration.Description);
                await connection.ExecuteAsync(Command(migration.Sql, null, transaction, cancellationToken)).ConfigureAwait(false);
                await connection.ExecuteAsync(Command(
                    "PRAGMA user_version = " + migration.Version.ToString(CultureInfo.InvariantCulture) + ";", null, transaction, cancellationToken)).ConfigureAwait(false);
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                version = migration.Version;
            }

            _logger.LogDebug("Bridge store {Database} at schema version {Version}", DatabasePath, version);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        (int)await connection.ExecuteScalarAsync<long>(Command("PRAGMA user_version;", null, transaction, cancellationToken)).ConfigureAwait(false);

    // ---------------------------------------------------------------- IPairingStore

    /// <inheritdoc />
    public async Task<ControllerIdentity> GetControllerIdentityAsync(CancellationToken cancellationToken)
    {
        var stored = await GetSettingAsync(SettingKeys.ControllerIdentity, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            return ParseControllerIdentity(stored);
        }

        // One controller per installation (SPEC-control.md 3.6). Insert-if-absent and read back inside one write
        // transaction, so two concurrent first callers end up with the same identity.
        var candidate = ControllerIdentity.Generate();
        var value = await WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(Command(
                "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value WHERE settings.value IS NULL;",
                new { key = SettingKeys.ControllerIdentity, value = FormatControllerIdentity(candidate) }, transaction, cancellationToken)).ConfigureAwait(false);
            return await connection.ExecuteScalarAsync<string>(Command(
                "SELECT value FROM settings WHERE key = @key;", new { key = SettingKeys.ControllerIdentity }, transaction, cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        var identity = ParseControllerIdentity(value ?? throw new InvalidOperationException("Controller identity vanished during creation."));
        if (identity.Id == candidate.Id)
        {
            _logger.LogInformation("Created controller identity {ControllerId}", identity.Id);
        }

        return identity;
    }

    internal static string FormatControllerIdentity(ControllerIdentity identity) => identity.Id + ":" + Convert.ToHexString(identity.Ed25519Seed);

    internal static ControllerIdentity ParseControllerIdentity(string stored)
    {
        var separator = stored.LastIndexOf(':');
        if (separator <= 0)
        {
            throw new InvalidDataException("Stored controller identity is malformed (expected '<id>:<hex seed>').");
        }

        byte[] seed;
        try
        {
            seed = Convert.FromHexString(stored.AsSpan(separator + 1));
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("Stored controller identity has a malformed seed.", e);
        }

        if (seed.Length != 32)
        {
            throw new InvalidDataException($"Stored controller seed has {seed.Length} bytes, expected 32.");
        }

        return new ControllerIdentity(stored[..separator], seed);
    }

    /// <inheritdoc />
    public async Task<PairingCredentials?> GetAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(receiverId);
        var row = await ReadAsync((connection, transaction) => connection.QuerySingleOrDefaultAsync<PairingRecord>(Command(
            "SELECT controller_id AS ControllerId, controller_seed AS ControllerSeed, accessory_id AS AccessoryId, accessory_public_key AS AccessoryPublicKey " +
            "FROM pairings WHERE receiver_id = @receiverId;", new { receiverId }, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        if (row.ControllerSeed.Length != 32 || row.AccessoryPublicKey.Length != 32 || row.ControllerId.Length == 0 || row.AccessoryId.Length == 0)
        {
            throw new InvalidDataException($"Stored pairing for {receiverId} is corrupt (seed {row.ControllerSeed.Length} bytes, accessory key {row.AccessoryPublicKey.Length} bytes); delete it and pair again.");
        }

        return new PairingCredentials(new ControllerIdentity(row.ControllerId, row.ControllerSeed), row.AccessoryId, row.AccessoryPublicKey);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>pairings.receiver_id</c> references <c>receivers</c>; when the sender pairs with a receiver the bridge has not
    /// stored yet, a minimal receivers row (name = id) is created so the pairing is not lost. The receiver's
    /// <c>pairing_state</c> becomes <see cref="PairingStates.Paired"/>.
    /// </remarks>
    public async Task SaveAsync(string receiverId, PairingCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(receiverId);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(credentials.Controller);
        ArgumentException.ThrowIfNullOrEmpty(credentials.Controller.Id, nameof(credentials));
        ArgumentException.ThrowIfNullOrEmpty(credentials.AccessoryId, nameof(credentials));
        if (credentials.Controller.Ed25519Seed is not { Length: 32 })
        {
            throw new ArgumentException("The controller seed must be 32 bytes (SPEC-control.md 3.8).", nameof(credentials));
        }

        if (credentials.AccessoryPublicKey is not { Length: 32 })
        {
            throw new ArgumentException("The accessory public key must be 32 bytes (SPEC-control.md 3.8).", nameof(credentials));
        }

        await WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(Command(
                "INSERT INTO receivers (id, name, pairing_state) VALUES (@receiverId, @receiverId, @paired) " +
                "ON CONFLICT(id) DO UPDATE SET pairing_state = excluded.pairing_state;",
                new { receiverId, paired = PairingStates.Paired }, transaction, cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(Command(
                "INSERT INTO pairings (receiver_id, controller_id, controller_seed, accessory_id, accessory_public_key) " +
                "VALUES (@receiverId, @controllerId, @controllerSeed, @accessoryId, @accessoryPublicKey) " +
                "ON CONFLICT(receiver_id) DO UPDATE SET controller_id = excluded.controller_id, controller_seed = excluded.controller_seed, " +
                "accessory_id = excluded.accessory_id, accessory_public_key = excluded.accessory_public_key, created_at = " + NowSql + ";",
                new
                {
                    receiverId,
                    controllerId = credentials.Controller.Id,
                    controllerSeed = credentials.Controller.Ed25519Seed,
                    accessoryId = credentials.AccessoryId,
                    accessoryPublicKey = credentials.AccessoryPublicKey,
                }, transaction, cancellationToken)).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Stored pairing for {ReceiverId} (accessory {AccessoryId}, controller {ControllerId})", receiverId, credentials.AccessoryId, credentials.Controller.Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A stored pairing exists only for receivers that need the PIN flow, so a <see cref="PairingStates.Paired"/> receiver
    /// goes back to <see cref="PairingStates.PinRequired"/> (SPEC-control.md 3.8: "on Error/470 drop the stored pairing and
    /// show the PIN flow again").
    /// </remarks>
    public async Task DeleteAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(receiverId);
        var deleted = await WriteAsync(async (connection, transaction) =>
        {
            var count = await connection.ExecuteAsync(Command("DELETE FROM pairings WHERE receiver_id = @receiverId;", new { receiverId }, transaction, cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(Command(
                "UPDATE receivers SET pairing_state = @pinRequired WHERE id = @receiverId AND pairing_state = @paired;",
                new { receiverId, pinRequired = PairingStates.PinRequired, paired = PairingStates.Paired }, transaction, cancellationToken)).ConfigureAwait(false);
            return count;
        }, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            _logger.LogInformation("Deleted pairing for {ReceiverId}", receiverId);
        }
    }

    // ---------------------------------------------------------------- receivers

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReceiverRow>> GetReceiversAsync(CancellationToken cancellationToken)
    {
        var rows = await ReadAsync((connection, transaction) => connection.QueryAsync<ReceiverRecord>(Command(
            "SELECT " + ReceiverColumns + " FROM receivers ORDER BY name COLLATE NOCASE, id;", null, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.ToRow()).ToList();
    }

    /// <inheritdoc />
    public async Task<ReceiverRow?> GetReceiverAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var row = await ReadAsync((connection, transaction) => connection.QuerySingleOrDefaultAsync<ReceiverRecord>(Command(
            "SELECT " + ReceiverColumns + " FROM receivers WHERE id = @id;", new { id }, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
        return row?.ToRow();
    }

    /// <inheritdoc />
    public async Task UpsertReceiverAsync(ReceiverRow receiver, CancellationToken cancellationToken)
    {
        ValidateReceiver(receiver);
        await WriteAsync((connection, transaction) => connection.ExecuteAsync(Command(
            "INSERT INTO receivers (id, name, model, manufacturer, last_address, last_port, features, status_flags, public_key, pairing_state, level, offset_frames, quirks, last_seen) " +
            "VALUES (@Id, @Name, @Model, @Manufacturer, @LastAddress, @LastPort, @Features, @StatusFlags, @PublicKey, @PairingState, @Level, @OffsetFrames, @Quirks, @LastSeen) " +
            "ON CONFLICT(id) DO UPDATE SET name = excluded.name, model = excluded.model, manufacturer = excluded.manufacturer, " +
            "last_address = excluded.last_address, last_port = excluded.last_port, features = excluded.features, status_flags = excluded.status_flags, " +
            "public_key = excluded.public_key, pairing_state = excluded.pairing_state, level = excluded.level, offset_frames = excluded.offset_frames, " +
            "quirks = excluded.quirks, last_seen = excluded.last_seen;",
            receiver, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Upserted receiver {ReceiverId} ({Name})", receiver.Id, receiver.Name);
    }

    private static void ValidateReceiver(ReceiverRow receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (string.IsNullOrEmpty(receiver.Id))
        {
            throw new ArgumentException("Receiver id must not be empty.", nameof(receiver));
        }

        if (string.IsNullOrWhiteSpace(receiver.Name))
        {
            throw new ArgumentException("Receiver name must not be empty.", nameof(receiver));
        }

        if (!PairingStates.IsValid(receiver.PairingState))
        {
            throw new ArgumentException($"Unknown pairing state '{receiver.PairingState}'.", nameof(receiver));
        }

        if (receiver.LastPort is < 0 or > 65535)
        {
            throw new ArgumentException($"Port {receiver.LastPort} is out of range.", nameof(receiver));
        }

        if (receiver.StatusFlags is < 0 or > uint.MaxValue)
        {
            throw new ArgumentException($"Status flags 0x{receiver.StatusFlags:X} exceed 32 bits.", nameof(receiver));
        }

        CheckLevel(receiver.Level, nameof(receiver));
        if (receiver.Quirks is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(receiver.Quirks);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("Receiver quirks must be a JSON object.", nameof(receiver));
                }
            }
            catch (JsonException e)
            {
                throw new ArgumentException("Receiver quirks are not valid JSON.", nameof(receiver), e);
            }
        }
    }

    /// <inheritdoc />
    public async Task SetReceiverLevelAsync(string id, int level, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        CheckLevel(level, nameof(level));
        await UpdateExistingAsync("UPDATE receivers SET level = @level WHERE id = @id;", new { id, level }, "receiver", id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetReceiverOffsetAsync(string id, int offsetFrames, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await UpdateExistingAsync("UPDATE receivers SET offset_frames = @offsetFrames WHERE id = @id;", new { id, offsetFrames }, "receiver", id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TouchReceiverAsync(string id, DateTimeOffset lastSeen, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        // The timestamps share one fixed-width UTC format (Mapping.FormatTimestamp), so they compare as text.
        var value = Api.Mapping.FormatTimestamp(lastSeen);
        var changed = await WriteAsync((connection, transaction) => connection.ExecuteAsync(Command(
            "UPDATE receivers SET last_seen = @value WHERE id = @id AND (last_seen IS NULL OR last_seen < @value);", new { id, value }, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
        return changed > 0;
    }

    /// <inheritdoc />
    public async Task DeleteReceiverAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var deleted = await WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(Command(
                "DELETE FROM settings WHERE key = @key AND value = @id;", new { key = SettingKeys.SelectedOutputReceiver, id }, transaction, cancellationToken)).ConfigureAwait(false);
            return await connection.ExecuteAsync(Command("DELETE FROM receivers WHERE id = @id;", new { id }, transaction, cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            _logger.LogInformation("Deleted receiver {ReceiverId} with its pairing and group memberships", id);
        }
    }

    // ---------------------------------------------------------------- groups

    /// <inheritdoc />
    public Task<IReadOnlyList<GroupRow>> GetGroupsAsync(CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<GroupRow>>(async (connection, transaction) =>
        {
            var groups = await connection.QueryAsync<GroupRecord>(Command(
                "SELECT id AS Id, name AS Name, master_level AS MasterLevel FROM groups ORDER BY created_at, rowid;", null, transaction, cancellationToken)).ConfigureAwait(false);
            var members = (await connection.QueryAsync<GroupMemberRecord>(Command(
                    "SELECT group_id AS GroupId, receiver_id AS ReceiverId, level AS Level, position AS Position FROM group_members ORDER BY group_id, position, rowid;",
                    null, transaction, cancellationToken)).ConfigureAwait(false))
                .ToLookup(m => m.GroupId, StringComparer.Ordinal);
            return groups.Select(g => g.ToRow(members[g.Id])).ToList();
        }, cancellationToken);

    /// <inheritdoc />
    public Task<GroupRow?> GetGroupAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return ReadAsync((connection, transaction) => ReadGroupAsync(connection, transaction, id, cancellationToken), cancellationToken);
    }

    private static async Task<GroupRow?> ReadGroupAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken cancellationToken)
    {
        var group = await connection.QuerySingleOrDefaultAsync<GroupRecord>(Command(
            "SELECT id AS Id, name AS Name, master_level AS MasterLevel FROM groups WHERE id = @id;", new { id }, transaction, cancellationToken)).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var members = await connection.QueryAsync<GroupMemberRecord>(Command(
            "SELECT group_id AS GroupId, receiver_id AS ReceiverId, level AS Level, position AS Position FROM group_members WHERE group_id = @id ORDER BY position, rowid;",
            new { id }, transaction, cancellationToken)).ConfigureAwait(false);
        return group.ToRow(members);
    }

    /// <inheritdoc />
    public async Task<GroupRow> CreateGroupAsync(string name, CancellationToken cancellationToken)
    {
        var trimmed = CheckName(name, nameof(name));

        // groupUUID in the session SETUP is a UUID string (SPEC-control.md 4.2); Apple senders write NSUUID's
        // upper-case form, and shairport-sync copies it verbatim into its gid TXT value [sps:rtsp.c:2501-2511].
        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        await WriteAsync((connection, transaction) => connection.ExecuteAsync(Command(
            "INSERT INTO groups (id, name, master_level) VALUES (@id, @name, @level);", new { id, name = trimmed, level = DefaultLevel }, transaction, cancellationToken)),
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created group {GroupId} ({Name})", id, trimmed);
        return new GroupRow(id, trimmed, DefaultLevel, []);
    }

    /// <inheritdoc />
    public async Task RenameGroupAsync(string id, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var trimmed = CheckName(name, nameof(name));
        await UpdateExistingAsync("UPDATE groups SET name = @name WHERE id = @id;", new { id, name = trimmed }, "group", id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteGroupAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var deleted = await WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(Command(
                "DELETE FROM settings WHERE key = @key AND value = @id;", new { key = SettingKeys.SelectedOutputGroup, id }, transaction, cancellationToken)).ConfigureAwait(false);
            return await connection.ExecuteAsync(Command("DELETE FROM groups WHERE id = @id;", new { id }, transaction, cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            _logger.LogInformation("Deleted group {GroupId}", id);
        }
    }

    /// <inheritdoc />
    public async Task SetGroupMemberAsync(string groupId, string receiverId, int level, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        ArgumentException.ThrowIfNullOrEmpty(receiverId);
        CheckLevel(level, nameof(level));
        await WriteAsync(async (connection, transaction) =>
        {
            var groupExists = await connection.ExecuteScalarAsync<long>(Command("SELECT COUNT(*) FROM groups WHERE id = @groupId;", new { groupId }, transaction, cancellationToken)).ConfigureAwait(false);
            if (groupExists == 0)
            {
                throw new KeyNotFoundException($"Unknown group '{groupId}'.");
            }

            var receiverExists = await connection.ExecuteScalarAsync<long>(Command("SELECT COUNT(*) FROM receivers WHERE id = @receiverId;", new { receiverId }, transaction, cancellationToken)).ConfigureAwait(false);
            if (receiverExists == 0)
            {
                throw new KeyNotFoundException($"Unknown receiver '{receiverId}'.");
            }

            return await connection.ExecuteAsync(Command(
                "INSERT INTO group_members (group_id, receiver_id, level, position) " +
                "VALUES (@groupId, @receiverId, @level, (SELECT COALESCE(MAX(position) + 1, 0) FROM group_members WHERE group_id = @groupId)) " +
                "ON CONFLICT(group_id, receiver_id) DO UPDATE SET level = excluded.level;",
                new { groupId, receiverId, level }, transaction, cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Group {GroupId} member {ReceiverId} at level {Level}", groupId, receiverId, level);
    }

    /// <inheritdoc />
    public async Task RemoveGroupMemberAsync(string groupId, string receiverId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        ArgumentException.ThrowIfNullOrEmpty(receiverId);
        await WriteAsync((connection, transaction) => connection.ExecuteAsync(Command(
            "DELETE FROM group_members WHERE group_id = @groupId AND receiver_id = @receiverId;", new { groupId, receiverId }, transaction, cancellationToken)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetGroupMasterLevelAsync(string groupId, int level, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        CheckLevel(level, nameof(level));
        await UpdateExistingAsync("UPDATE groups SET master_level = @level WHERE id = @groupId;", new { groupId, level }, "group", groupId, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- settings

    /// <inheritdoc />
    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return ReadAsync((connection, transaction) => connection.ExecuteScalarAsync<string?>(Command(
            "SELECT value FROM settings WHERE key = @key;", new { key }, transaction, cancellationToken)), cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await WriteAsync((connection, transaction) => connection.ExecuteAsync(Command(
            value is null
                ? "DELETE FROM settings WHERE key = @key;"
                : "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            new { key, value }, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Spotify credentials

    /// <inheritdoc />
    public async Task<SpotifyCredentialRow?> GetSpotifyCredentialsAsync(CancellationToken cancellationToken)
    {
        var row = await ReadAsync((connection, transaction) => connection.QueryFirstOrDefaultAsync<SpotifyCredentialRecord>(Command(
            "SELECT username AS Username, auth_type AS AuthType, auth_data AS AuthData FROM spotify_credentials ORDER BY updated_at DESC, rowid DESC LIMIT 1;",
            null, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
        return row is null ? null : new SpotifyCredentialRow(row.Username, checked((int)row.AuthType), row.AuthData);
    }

    /// <inheritdoc />
    public async Task SaveSpotifyCredentialsAsync(SpotifyCredentialRow credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrEmpty(credentials.Username))
        {
            throw new ArgumentException("Spotify credentials need a username.", nameof(credentials));
        }

        if (credentials.AuthData is null || credentials.AuthData.Length == 0)
        {
            throw new ArgumentException("Spotify credentials need auth data.", nameof(credentials));
        }

        await WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(Command("DELETE FROM spotify_credentials WHERE username <> @Username;", credentials, transaction, cancellationToken)).ConfigureAwait(false);
            return await connection.ExecuteAsync(Command(
                "INSERT INTO spotify_credentials (username, auth_type, auth_data) VALUES (@Username, @AuthType, @AuthData) " +
                "ON CONFLICT(username) DO UPDATE SET auth_type = excluded.auth_type, auth_data = excluded.auth_data, updated_at = " + NowSql + ";",
                credentials, transaction, cancellationToken)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Cached Spotify credentials for {Username}", credentials.Username);
    }

    /// <inheritdoc />
    public async Task ClearSpotifyCredentialsAsync(CancellationToken cancellationToken)
    {
        var deleted = await WriteAsync((connection, transaction) => connection.ExecuteAsync(Command(
            "DELETE FROM spotify_credentials;", null, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            _logger.LogInformation("Forgot cached Spotify credentials");
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Only this store's pool: ClearAllPools would close other stores' connections (tests run in parallel).
        await using (var probe = new SqliteConnection(_connectionString))
        {
            SqliteConnection.ClearPool(probe);
        }

        if (_memoryKeepAlive is not null)
        {
            await _memoryKeepAlive.DisposeAsync().ConfigureAwait(false);
            _memoryKeepAlive = null;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_inMemory && _memoryKeepAlive is null)
        {
            // Opened on first use (every in-memory OpenAsync runs behind _writeGate, so this cannot race): a named
            // shared-cache memory database lives only while one connection is open, so the keep-alive holds it.
            var keepAlive = new SqliteConnection(_connectionString);
            try
            {
                await keepAlive.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await keepAlive.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _memoryKeepAlive = keepAlive;
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs <paramref name="body"/> in a deferred transaction (one consistent WAL snapshot for multi-query reads).</summary>
    private async Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> body, CancellationToken cancellationToken)
    {
        // Shared-cache memory databases take table locks; serialise them with the writers instead of busy-looping.
        if (_inMemory)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            var result = await body(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (_inMemory)
            {
                _writeGate.Release();
            }
        }
    }

    /// <summary>Runs <paramref name="body"/> in a <c>BEGIN IMMEDIATE</c> transaction behind the in-process write gate.</summary>
    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> body, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var result = await body(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task UpdateExistingAsync(string sql, object parameters, string kind, string id, CancellationToken cancellationToken)
    {
        var changed = await WriteAsync((connection, transaction) => connection.ExecuteAsync(Command(sql, parameters, transaction, cancellationToken)), cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            throw new KeyNotFoundException($"Unknown {kind} '{id}'.");
        }
    }

    private static CommandDefinition Command(string sql, object? parameters, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        new(sql, parameters, transaction, cancellationToken: cancellationToken);

    private static void CheckLevel(int level, string parameterName)
    {
        if (level is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(parameterName, level, "Levels are 0..100.");
        }
    }

    private static string CheckName(string name, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Name must not be empty.", parameterName);
        }

        return trimmed;
    }

    // Dapper materialises into these mutable records (column aliases match the property names; SQLite returns every
    // INTEGER as Int64, which Dapper converts for properties but not for positional record constructors).
    private sealed class ReceiverRecord
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public string? Model { get; set; }

        public string? Manufacturer { get; set; }

        public string? LastAddress { get; set; }

        public long LastPort { get; set; }

        public long Features { get; set; }

        public long StatusFlags { get; set; }

        public byte[]? PublicKey { get; set; }

        public string PairingState { get; set; } = PairingStates.Transient;

        public long Level { get; set; }

        public long OffsetFrames { get; set; }

        public string? Quirks { get; set; }

        public string? LastSeen { get; set; }

        public ReceiverRow ToRow() => new(Id, Name, Model, Manufacturer, LastAddress, (int)LastPort, Features, StatusFlags, PublicKey, PairingState, (int)Level, (int)OffsetFrames, Quirks, LastSeen);
    }

    private sealed class GroupRecord
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public long MasterLevel { get; set; }

        public GroupRow ToRow(IEnumerable<GroupMemberRecord> members) =>
            new(Id, Name, (int)MasterLevel, members.Select(m => new GroupMemberRow(m.GroupId, m.ReceiverId, (int)m.Level, (int)m.Position)).ToList());
    }

    private sealed class GroupMemberRecord
    {
        public string GroupId { get; set; } = "";

        public string ReceiverId { get; set; } = "";

        public long Level { get; set; }

        public long Position { get; set; }
    }

    private sealed class PairingRecord
    {
        public string ControllerId { get; set; } = "";

        public byte[] ControllerSeed { get; set; } = [];

        public string AccessoryId { get; set; } = "";

        public byte[] AccessoryPublicKey { get; set; } = [];
    }

    private sealed class SpotifyCredentialRecord
    {
        public string Username { get; set; } = "";

        public long AuthType { get; set; }

        public byte[] AuthData { get; set; } = [];
    }
}
