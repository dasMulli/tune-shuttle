using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;
using Bridge.Contracts;
using Bridge.Service.Store;
using Dapper;
using Librespot.Core;
using Librespot.Protocol.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests;

/// <summary>A temporary SQLite file with a store on it; deletes the file and its WAL/SHM companions afterwards.</summary>
internal sealed class TempDatabase : IAsyncDisposable
{
    private readonly List<SqliteBridgeStore> _stores = [];

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bridge-test-{Guid.NewGuid():N}.db");

    public SqliteBridgeStore Store => _stores[0];

    public static async Task<TempDatabase> CreateAsync(bool initialize = true)
    {
        var db = new TempDatabase();
        var store = db.OpenStore();
        if (initialize)
        {
            await store.InitializeAsync(CancellationToken.None);
        }

        return db;
    }

    /// <summary>Another store instance on the same file (its own connection pool and write gate, like a second process).</summary>
    public SqliteBridgeStore OpenStore(IReadOnlyList<StoreMigration>? migrations = null)
    {
        var store = migrations is null
            ? new SqliteBridgeStore(Path, NullLogger<SqliteBridgeStore>.Instance)
            : new SqliteBridgeStore(Path, NullLogger<SqliteBridgeStore>.Instance, migrations);
        _stores.Add(store);
        return store;
    }

    /// <summary>An unpooled raw connection for assertions behind the store's back.</summary>
    public SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // a pooled handle from a failed test; the temp directory is cleaned eventually
            }
        }
    }
}

public class StoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    internal static ReceiverRow Receiver(string id, string name = "Speaker", int level = 50) =>
        new(id, name, "model", "SpeakerA", "192.0.2.26", 7000, unchecked((long)0x0001C340445F8A00UL), 0x404, [1, 2, 3], PairingStates.Transient, level, 0, null, "2026-10-09T12:00:00.000Z");

    private static PairingCredentials RealCredentials()
    {
        var accessory = Ed25519KeyPair.Generate();
        return new PairingCredentials(ControllerIdentity.Generate(), "02:1A:2B:3C:4D:5E", accessory.PublicKey);
    }

    // ------------------------------------------------------------------ schema

    [Fact]
    public async Task Schema_creates_the_planned_tables_in_wal_mode()
    {
        await using var db = await TempDatabase.CreateAsync();

        using var connection = db.OpenRaw();
        var tables = (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;")).ToList();
        Assert.Equal(["group_members", "groups", "pairings", "receivers", "settings", "spotify_credentials"], tables);
        Assert.Equal((long)db.Store.TargetVersion, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
        Assert.Equal(1, db.Store.TargetVersion);
        Assert.Equal("wal", await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task InitializeAsync_is_idempotent_and_safe_concurrently()
    {
        await using var db = await TempDatabase.CreateAsync();
        await db.Store.UpsertReceiverAsync(Receiver("A"), Ct);

        await db.Store.InitializeAsync(Ct);
        var second = db.OpenStore();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? db.Store : second).InitializeAsync(Ct)));

        Assert.NotNull(await second.GetReceiverAsync("A", Ct));
        using var connection = db.OpenRaw();
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
    }

    [Fact]
    public async Task Concurrent_first_initialization_of_a_new_file_succeeds()
    {
        await using var db = await TempDatabase.CreateAsync(initialize: false);
        var stores = new[] { db.Store, db.OpenStore(), db.OpenStore() };
        await Task.WhenAll(stores.Select(s => Task.Run(() => s.InitializeAsync(Ct))));
        await stores[2].SetSettingAsync("k", "v", Ct);
        Assert.Equal("v", await stores[0].GetSettingAsync("k", Ct));
    }

    // ------------------------------------------------------------------ migrations

    private static readonly StoreMigration AddColumn = new(2, "receivers.room", "ALTER TABLE receivers ADD COLUMN room TEXT;");
    private static readonly StoreMigration AddIndex = new(3, "group_members(receiver_id) index", "CREATE INDEX IF NOT EXISTS ix_group_members_receiver ON group_members(receiver_id);");

    [Fact]
    public async Task Migration_from_version_1_to_current_keeps_data()
    {
        await using var db = await TempDatabase.CreateAsync(initialize: false);

        // A version-1 file as the scaffold wrote it: schema.sql applied directly, with data in every table.
        using (var connection = db.OpenRaw())
        {
            await connection.ExecuteAsync(SqliteBridgeStore.SchemaSql);
            await connection.ExecuteAsync(
                "INSERT INTO receivers (id, name, level) VALUES ('R1', 'Kitchen', 33);" +
                "INSERT INTO groups (id, name, master_level) VALUES ('G1', 'Down', 70);" +
                "INSERT INTO group_members (group_id, receiver_id, level, position) VALUES ('G1', 'R1', 40, 0);" +
                "INSERT INTO settings (key, value) VALUES ('device_name', 'Bridge');");
            Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
        }

        // The real migration list (current version) first, then a synthetic list that goes two versions further.
        await db.Store.InitializeAsync(Ct);
        Assert.Equal(33, (await db.Store.GetReceiverAsync("R1", Ct))!.Level);

        var upgraded = db.OpenStore([AddColumn, AddIndex]);
        await upgraded.InitializeAsync(Ct);
        await upgraded.InitializeAsync(Ct);

        using (var connection = db.OpenRaw())
        {
            Assert.Equal(3L, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
            var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('receivers');")).ToList();
            Assert.Contains("room", columns);
            Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_group_members_receiver';"));
        }

        var receiver = await upgraded.GetReceiverAsync("R1", Ct);
        Assert.Equal("Kitchen", receiver!.Name);
        var group = await upgraded.GetGroupAsync("G1", Ct);
        Assert.Equal(70, group!.MasterLevel);
        Assert.Equal(40, Assert.Single(group.Members).Level);
        Assert.Equal("Bridge", await upgraded.GetSettingAsync(SettingKeys.DeviceName, Ct));

        // The original build now sees a newer database and refuses to touch it.
        var old = db.OpenStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.InitializeAsync(Ct));
    }

    [Fact]
    public async Task Fresh_database_runs_all_migrations()
    {
        await using var db = await TempDatabase.CreateAsync(initialize: false);
        var store = db.OpenStore([AddColumn, AddIndex]);
        await store.InitializeAsync(Ct);
        Assert.Equal(3, store.TargetVersion);
        using var connection = db.OpenRaw();
        Assert.Equal(3L, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
    }

    [Fact]
    public async Task Failing_migration_rolls_back_and_keeps_the_previous_version()
    {
        await using var db = await TempDatabase.CreateAsync();
        var broken = new StoreMigration(3, "broken", "CREATE TABLE half_done (x INTEGER); INSERT INTO no_such_table VALUES (1);");
        var store = db.OpenStore([AddColumn, broken]);
        await Assert.ThrowsAsync<SqliteException>(() => store.InitializeAsync(Ct));

        using var connection = db.OpenRaw();
        Assert.Equal(2L, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'half_done';"));
    }

    [Fact]
    public void Migration_lists_must_be_contiguous_from_version_2()
    {
        Assert.Throws<ArgumentException>(() => new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance, [AddIndex]));
        Assert.Throws<ArgumentException>(() => new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance, [AddColumn, AddColumn]));
        Assert.DoesNotContain(StoreMigrations.All, m => m.Version <= StoreMigrations.BaseVersion);
    }

    // ------------------------------------------------------------------ receivers

    [Fact]
    public async Task Receivers_crud()
    {
        await using var db = await TempDatabase.CreateAsync();
        var store = db.Store;

        Assert.Empty(await store.GetReceiversAsync(Ct));
        Assert.Null(await store.GetReceiverAsync("nope", Ct));

        var SpeakerA = Receiver("02:1A:2B:3C:4D:5E", "Test Speaker ABCD") with { Quirks = """{"sendAuthSetup":false}""" };
        await store.UpsertReceiverAsync(SpeakerA, Ct);
        await store.UpsertReceiverAsync(Receiver("AA", "attic"), Ct);

        var loaded = await store.GetReceiverAsync(SpeakerA.Id, Ct);
        Assert.NotNull(loaded);
        Assert.Equal(SpeakerA with { PublicKey = loaded!.PublicKey }, loaded);
        Assert.Equal(SpeakerA.PublicKey, loaded.PublicKey);
        Assert.Equal(unchecked((long)0x0001C340445F8A00UL), loaded.Features);

        // ordered by name, case-insensitive
        Assert.Equal(["attic", "Test Speaker ABCD"], (await store.GetReceiversAsync(Ct)).Select(r => r.Name));

        // upsert overwrites every column but keeps created_at
        string createdAt;
        using (var connection = db.OpenRaw())
        {
            createdAt = (await connection.ExecuteScalarAsync<string>("SELECT created_at FROM receivers WHERE id = 'AA';"))!;
        }

        var highBit = Receiver("AA", "Attic 2", level: 10) with { Features = long.MinValue | 1, PublicKey = null, LastAddress = "fe80::1", PairingState = PairingStates.PinRequired };
        await store.UpsertReceiverAsync(highBit, Ct);
        var updated = await store.GetReceiverAsync("AA", Ct);
        Assert.Equal(highBit, updated);
        using (var connection = db.OpenRaw())
        {
            Assert.Equal(createdAt, await connection.ExecuteScalarAsync<string>("SELECT created_at FROM receivers WHERE id = 'AA';"));
        }

        await store.SetReceiverLevelAsync("AA", 77, Ct);
        await store.SetReceiverOffsetAsync("AA", -352, Ct);
        updated = await store.GetReceiverAsync("AA", Ct);
        Assert.Equal(77, updated!.Level);
        Assert.Equal(-352, updated.OffsetFrames);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SetReceiverLevelAsync("missing", 5, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SetReceiverOffsetAsync("missing", 5, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetReceiverLevelAsync("AA", 101, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpsertReceiverAsync(Receiver("X", level: -1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertReceiverAsync(Receiver("X") with { PairingState = "weird" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertReceiverAsync(Receiver("X") with { Quirks = "[1]" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertReceiverAsync(Receiver("X") with { Quirks = "{nope" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertReceiverAsync(Receiver("X", name: " "), Ct));

        await store.DeleteReceiverAsync("AA", Ct);
        await store.DeleteReceiverAsync("AA", Ct); // idempotent
        Assert.Null(await store.GetReceiverAsync("AA", Ct));
        Assert.Single(await store.GetReceiversAsync(Ct));
    }

    // ------------------------------------------------------------------ groups

    [Fact]
    public async Task Groups_and_members_crud()
    {
        await using var db = await TempDatabase.CreateAsync();
        var store = db.Store;
        await store.UpsertReceiverAsync(Receiver("R1", "One"), Ct);
        await store.UpsertReceiverAsync(Receiver("R2", "Two"), Ct);
        await store.UpsertReceiverAsync(Receiver("R3", "Three"), Ct);

        var created = await store.CreateGroupAsync("  Downstairs ", Ct);
        Assert.True(Guid.TryParse(created.Id, out _));
        Assert.Equal(created.Id.ToUpperInvariant(), created.Id);
        Assert.Equal("Downstairs", created.Name);
        Assert.Equal(SqliteBridgeStore.DefaultLevel, created.MasterLevel);
        Assert.Empty(created.Members);
        Assert.Equal(created, (await store.GetGroupAsync(created.Id, Ct))! with { Members = created.Members });

        var other = await store.CreateGroupAsync("Upstairs", Ct);
        Assert.Equal([created.Id, other.Id], (await store.GetGroupsAsync(Ct)).Select(g => g.Id));

        await store.RenameGroupAsync(created.Id, "Ground floor", Ct);
        await store.SetGroupMasterLevelAsync(created.Id, 80, Ct);
        await store.SetGroupMemberAsync(created.Id, "R2", 30, Ct);
        await store.SetGroupMemberAsync(created.Id, "R1", 60, Ct);
        await store.SetGroupMemberAsync(created.Id, "R3", 90, Ct);
        await store.SetGroupMemberAsync(created.Id, "R2", 35, Ct); // update keeps the position
        await store.SetGroupMemberAsync(other.Id, "R1", 10, Ct);   // a receiver may be in several groups

        var group = (await store.GetGroupAsync(created.Id, Ct))!;
        Assert.Equal("Ground floor", group.Name);
        Assert.Equal(80, group.MasterLevel);
        Assert.Equal(
            [new GroupMemberRow(created.Id, "R2", 35, 0), new GroupMemberRow(created.Id, "R1", 60, 1), new GroupMemberRow(created.Id, "R3", 90, 2)],
            group.Members);

        await store.RemoveGroupMemberAsync(created.Id, "R1", Ct);
        await store.RemoveGroupMemberAsync(created.Id, "R1", Ct); // idempotent
        await store.SetGroupMemberAsync(created.Id, "R1", 50, Ct); // appended at the end again
        group = (await store.GetGroupAsync(created.Id, Ct))!;
        Assert.Equal(["R2", "R3", "R1"], group.Members.Select(m => m.ReceiverId));

        var all = await store.GetGroupsAsync(Ct);
        Assert.Equal(group, all[0] with { Members = group.Members });
        Assert.Equal(group.Members, all[0].Members);
        Assert.Equal([new GroupMemberRow(other.Id, "R1", 10, 0)], all[1].Members);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.RenameGroupAsync("missing", "x", Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SetGroupMasterLevelAsync("missing", 1, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SetGroupMemberAsync("missing", "R1", 1, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SetGroupMemberAsync(created.Id, "missing", 1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetGroupMemberAsync(created.Id, "R1", 101, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetGroupMasterLevelAsync(created.Id, -1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateGroupAsync("   ", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RenameGroupAsync(created.Id, "", Ct));

        await store.DeleteGroupAsync(other.Id, Ct);
        await store.DeleteGroupAsync(other.Id, Ct); // idempotent
        Assert.Null(await store.GetGroupAsync(other.Id, Ct));
        Assert.Single(await store.GetGroupsAsync(Ct));
    }

    // ------------------------------------------------------------------ cascades

    [Fact]
    public async Task Deleting_a_receiver_cascades_to_pairing_memberships_and_selected_output()
    {
        await using var db = await TempDatabase.CreateAsync();
        var store = db.Store;
        await store.UpsertReceiverAsync(Receiver("R1"), Ct);
        await store.UpsertReceiverAsync(Receiver("R2"), Ct);
        var group = await store.CreateGroupAsync("G", Ct);
        await store.SetGroupMemberAsync(group.Id, "R1", 50, Ct);
        await store.SetGroupMemberAsync(group.Id, "R2", 50, Ct);
        await store.SaveAsync("R1", RealCredentials(), Ct);
        await store.SetSettingAsync(SettingKeys.SelectedOutputReceiver, "R1", Ct);

        await store.DeleteReceiverAsync("R1", Ct);

        Assert.Null(await store.GetAsync("R1", Ct));
        Assert.Equal(["R2"], (await store.GetGroupAsync(group.Id, Ct))!.Members.Select(m => m.ReceiverId));
        Assert.Null(await store.GetSettingAsync(SettingKeys.SelectedOutputReceiver, Ct));
        using var connection = db.OpenRaw();
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pairings;"));
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM group_members;"));
    }

    [Fact]
    public async Task Deleting_a_group_cascades_to_members_and_selected_output_but_keeps_receivers()
    {
        await using var db = await TempDatabase.CreateAsync();
        var store = db.Store;
        await store.UpsertReceiverAsync(Receiver("R1"), Ct);
        var group = await store.CreateGroupAsync("G", Ct);
        var other = await store.CreateGroupAsync("H", Ct);
        await store.SetGroupMemberAsync(group.Id, "R1", 50, Ct);
        await store.SetGroupMemberAsync(other.Id, "R1", 50, Ct);
        await store.SetSettingAsync(SettingKeys.SelectedOutputGroup, group.Id, Ct);

        await store.DeleteGroupAsync(group.Id, Ct);

        Assert.NotNull(await store.GetReceiverAsync("R1", Ct));
        Assert.Null(await store.GetSettingAsync(SettingKeys.SelectedOutputGroup, Ct));
        Assert.Single((await store.GetGroupAsync(other.Id, Ct))!.Members);
        using var connection = db.OpenRaw();
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM group_members WHERE group_id = @id;", new { id = group.Id }));
    }

    [Fact]
    public async Task Selected_output_of_another_group_survives_a_delete()
    {
        await using var db = await TempDatabase.CreateAsync();
        var keep = await db.Store.CreateGroupAsync("Keep", Ct);
        var drop = await db.Store.CreateGroupAsync("Drop", Ct);
        await db.Store.SetSettingAsync(SettingKeys.SelectedOutputGroup, keep.Id, Ct);
        await db.Store.DeleteGroupAsync(drop.Id, Ct);
        Assert.Equal(keep.Id, await db.Store.GetSettingAsync(SettingKeys.SelectedOutputGroup, Ct));
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public async Task Settings_set_overwrite_and_remove()
    {
        await using var db = await TempDatabase.CreateAsync();
        var store = db.Store;
        Assert.Null(await store.GetSettingAsync("missing", Ct));
        await store.SetSettingAsync(SettingKeys.DeviceName, "Bridge", Ct);
        await store.SetSettingAsync(SettingKeys.DeviceName, "Wohnzimmer", Ct);
        await store.SetSettingAsync(SettingKeys.LatencyFrames, "77175", Ct);
        Assert.Equal("Wohnzimmer", await store.GetSettingAsync(SettingKeys.DeviceName, Ct));
        Assert.Equal("77175", await store.GetSettingAsync(SettingKeys.LatencyFrames, Ct));
        await store.SetSettingAsync(SettingKeys.DeviceName, null, Ct);
        Assert.Null(await store.GetSettingAsync(SettingKeys.DeviceName, Ct));
        await store.SetSettingAsync(SettingKeys.DeviceName, "", Ct);
        Assert.Equal("", await store.GetSettingAsync(SettingKeys.DeviceName, Ct));
    }

    // ------------------------------------------------------------------ Spotify credentials

    [Fact]
    public async Task Spotify_credential_blob_round_trips()
    {
        await using var db = await TempDatabase.CreateAsync();
        var store = db.Store;
        Assert.Null(await store.GetSpotifyCredentialsAsync(Ct));

        // A reusable credential blob as librespot produces it: arbitrary bytes, zeros included.
        var blob = new byte[300];
        Random.Shared.NextBytes(blob);
        blob[0] = 0;
        blob[^1] = 0;
        var credentials = new Credentials { Username = "spotify-user", AuthType = AuthenticationType.AuthenticationStoredSpotifyCredentials, AuthData = blob };
        await store.SaveSpotifyCredentialsAsync(new SpotifyCredentialRow(credentials.Username!, (int)credentials.AuthType, credentials.AuthData), Ct);

        var loaded = await store.GetSpotifyCredentialsAsync(Ct);
        Assert.NotNull(loaded);
        var restored = new Credentials { Username = loaded!.Username, AuthType = (AuthenticationType)loaded.AuthType, AuthData = loaded.AuthData };
        Assert.Equal(credentials.Username, restored.Username);
        Assert.Equal(credentials.AuthType, restored.AuthType);
        Assert.Equal(blob, restored.AuthData);

        // Same user again replaces; another user replaces the whole cache (one account at a time).
        await store.SaveSpotifyCredentialsAsync(new SpotifyCredentialRow("spotify-user", 1, [9, 9]), Ct);
        Assert.Equal([9, 9], (await store.GetSpotifyCredentialsAsync(Ct))!.AuthData);
        await store.SaveSpotifyCredentialsAsync(new SpotifyCredentialRow("other", 1, [7]), Ct);
        Assert.Equal("other", (await store.GetSpotifyCredentialsAsync(Ct))!.Username);
        using (var connection = db.OpenRaw())
        {
            Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM spotify_credentials;"));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSpotifyCredentialsAsync(new SpotifyCredentialRow("", 1, [1]), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSpotifyCredentialsAsync(new SpotifyCredentialRow("u", 1, []), Ct));

        await store.ClearSpotifyCredentialsAsync(Ct);
        await store.ClearSpotifyCredentialsAsync(Ct);
        Assert.Null(await store.GetSpotifyCredentialsAsync(Ct));
    }

    // ------------------------------------------------------------------ pairing store

    [Fact]
    public async Task Controller_identity_is_created_once_and_persisted()
    {
        await using var db = await TempDatabase.CreateAsync();
        var first = await db.Store.GetControllerIdentityAsync(Ct);
        var second = await db.Store.GetControllerIdentityAsync(Ct);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Ed25519Seed, second.Ed25519Seed);
        Assert.Equal(32, first.Ed25519Seed.Length);

        // survives a new store instance (a restart) and yields the same Ed25519 key pair
        var reopened = await db.OpenStore().GetControllerIdentityAsync(Ct);
        Assert.Equal(first.Id, reopened.Id);
        Assert.Equal(first.KeyPair.PublicKey, reopened.KeyPair.PublicKey);
    }

    [Fact]
    public async Task Concurrent_first_callers_get_the_same_controller_identity()
    {
        await using var db = await TempDatabase.CreateAsync();
        var stores = new[] { db.Store, db.OpenStore(), db.OpenStore() };
        var identities = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => stores[i % 3].GetControllerIdentityAsync(Ct))));
        Assert.Single(identities.Select(i => i.Id).Distinct());
        Assert.Single(identities.Select(i => Convert.ToHexString(i.Ed25519Seed)).Distinct());
    }

    [Fact]
    public async Task Malformed_controller_identity_is_reported_not_replaced()
    {
        await using var db = await TempDatabase.CreateAsync();
        await db.Store.SetSettingAsync(SettingKeys.ControllerIdentity, "ABC:1234", Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => db.Store.GetControllerIdentityAsync(Ct));
        Assert.Equal("ABC:1234", await db.Store.GetSettingAsync(SettingKeys.ControllerIdentity, Ct));
    }

    [Fact]
    public async Task Pairing_round_trip_with_real_credentials()
    {
        await using var db = await TempDatabase.CreateAsync();
        var store = db.Store;
        var controller = await store.GetControllerIdentityAsync(Ct);
        var accessory = Ed25519KeyPair.Generate();
        var credentials = new PairingCredentials(controller, "02:1A:2B:3C:4D:5E", accessory.PublicKey);
        await store.UpsertReceiverAsync(Receiver("R1") with { PairingState = PairingStates.PinRequired }, Ct);

        Assert.Null(await store.GetAsync("R1", Ct));
        await store.SaveAsync("R1", credentials, Ct);

        var loaded = await db.OpenStore().GetAsync("R1", Ct);
        Assert.NotNull(loaded);
        Assert.Equal(controller.Id, loaded!.Controller.Id);
        Assert.Equal(controller.Ed25519Seed, loaded.Controller.Ed25519Seed);
        Assert.Equal(credentials.AccessoryId, loaded.AccessoryId);
        Assert.Equal(accessory.PublicKey, loaded.AccessoryPublicKey);

        // The restored keys still work: our LTSK signs, the stored accessory LTPK verifies the accessory's signature.
        var message = "pair-verify transcript"u8.ToArray();
        Assert.True(Ed25519KeyPair.Verify(controller.KeyPair.PublicKey, message, loaded.Controller.KeyPair.Sign(message)));
        Assert.True(Ed25519KeyPair.Verify(loaded.AccessoryPublicKey, message, accessory.Sign(message)));
        Assert.Equal(PairingStates.Paired, (await store.GetReceiverAsync("R1", Ct))!.PairingState);

        // Re-pairing replaces the stored keys.
        var replacement = RealCredentials();
        await store.SaveAsync("R1", replacement, Ct);
        Assert.Equal(replacement.AccessoryPublicKey, (await store.GetAsync("R1", Ct))!.AccessoryPublicKey);

        // Pair-verify failure path: delete -> PIN flow again.
        await store.DeleteAsync("R1", Ct);
        await store.DeleteAsync("R1", Ct);
        Assert.Null(await store.GetAsync("R1", Ct));
        Assert.Equal(PairingStates.PinRequired, (await store.GetReceiverAsync("R1", Ct))!.PairingState);
    }

    [Fact]
    public async Task Pairing_for_an_unknown_receiver_creates_a_placeholder_row()
    {
        await using var db = await TempDatabase.CreateAsync();
        await db.Store.SaveAsync("CLI:ONLY", RealCredentials(), Ct);
        var row = await db.Store.GetReceiverAsync("CLI:ONLY", Ct);
        Assert.NotNull(row);
        Assert.Equal("CLI:ONLY", row!.Name);
        Assert.Equal(PairingStates.Paired, row.PairingState);
        Assert.NotNull(await db.Store.GetAsync("CLI:ONLY", Ct));
    }

    [Fact]
    public async Task Pairing_validation_and_corruption()
    {
        await using var db = await TempDatabase.CreateAsync();
        var good = RealCredentials();
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.SaveAsync("R", good with { AccessoryPublicKey = new byte[31] }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.SaveAsync("R", good with { Controller = good.Controller with { Ed25519Seed = new byte[64] } }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.SaveAsync("R", good with { AccessoryId = "" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.SaveAsync("", good, Ct));
        Assert.Null(await db.Store.GetReceiverAsync("R", Ct));

        using (var connection = db.OpenRaw())
        {
            await connection.ExecuteAsync(
                "INSERT INTO receivers (id, name) VALUES ('BAD', 'bad');" +
                "INSERT INTO pairings (receiver_id, controller_id, controller_seed, accessory_id, accessory_public_key) VALUES ('BAD', 'C', x'0102', 'A', x'03');");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => db.Store.GetAsync("BAD", Ct));
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task Concurrent_writers_on_one_file_lose_nothing()
    {
        await using var db = await TempDatabase.CreateAsync();
        var stores = new[] { db.Store, db.OpenStore(), db.OpenStore() };
        foreach (var s in stores.Skip(1))
        {
            await s.InitializeAsync(Ct);
        }

        var group = await db.Store.CreateGroupAsync("All", Ct);
        const int count = 60;
        await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            var store = stores[i % stores.Length];
            var id = $"R{i:D3}";
            await store.UpsertReceiverAsync(Receiver(id, $"Speaker {i}"), Ct);
            await store.SetGroupMemberAsync(group.Id, id, i % 101, Ct);
            await store.SetReceiverLevelAsync(id, i % 101, Ct);
            await store.SetSettingAsync($"k{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture), Ct);
            _ = await store.GetGroupsAsync(Ct);
        })));

        var receivers = await db.Store.GetReceiversAsync(Ct);
        Assert.Equal(count, receivers.Count);
        Assert.All(receivers, r => Assert.Equal(int.Parse(r.Id[1..], System.Globalization.CultureInfo.InvariantCulture) % 101, r.Level));
        var members = (await db.Store.GetGroupAsync(group.Id, Ct))!.Members;
        Assert.Equal(count, members.Count);
        Assert.Equal(count, members.Select(m => m.Position).Distinct().Count());
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), await db.Store.GetSettingAsync($"k{i}", Ct));
        }
    }

    // ------------------------------------------------------------------ in-memory and lifetime

    [Fact]
    public async Task In_memory_store_works_and_enforces_foreign_keys()
    {
        await using var store = new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance);
        await store.InitializeAsync(Ct);
        await store.UpsertReceiverAsync(Receiver("M1"), Ct);
        var group = await store.CreateGroupAsync("G", Ct);
        await store.SetGroupMemberAsync(group.Id, "M1", 20, Ct);
        await store.SaveAsync("M1", RealCredentials(), Ct);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SetSettingAsync($"s{i}", "v", Ct)));
        await store.DeleteReceiverAsync("M1", Ct);
        Assert.Empty((await store.GetGroupAsync(group.Id, Ct))!.Members);
        Assert.Null(await store.GetAsync("M1", Ct));

        // two memory stores are independent
        await using var other = new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance);
        await other.InitializeAsync(Ct);
        Assert.Empty(await other.GetGroupsAsync(Ct));
    }

    [Fact]
    public async Task Disposed_store_throws()
    {
        var store = new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance);
        await store.InitializeAsync(Ct);
        await store.DisposeAsync();
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetReceiversAsync(Ct));
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        await using var db = await TempDatabase.CreateAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Store.UpsertReceiverAsync(Receiver("C"), cts.Token));
        Assert.Null(await db.Store.GetReceiverAsync("C", Ct));
    }

    // ------------------------------------------------------------------ contracts

    [Fact]
    public void Bridge_events_serialize_with_a_type_discriminator()
    {
        BridgeEvent e = new BridgeEvent.VolumeChanged("g1", null, 42, "spotify");
        var json = System.Text.Json.JsonSerializer.Serialize(e);
        Assert.Contains("\"type\":\"volumeChanged\"", json, StringComparison.Ordinal);
        var back = System.Text.Json.JsonSerializer.Deserialize<BridgeEvent>(json);
        var vc = Assert.IsType<BridgeEvent.VolumeChanged>(back);
        Assert.Equal(42, vc.Level);
    }
}
