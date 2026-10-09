using Bridge.Contracts;
using Bridge.Service.Store;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests;

public class StoreTests
{
    [Fact]
    public async Task Schema_creates_the_planned_tables()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bridge-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteBridgeStore(path, NullLogger<SqliteBridgeStore>.Instance);
            await store.InitializeAsync(CancellationToken.None);

            using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            var tables = (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;")).ToList();
            Assert.Equal(["group_members", "groups", "pairings", "receivers", "settings", "spotify_credentials"], tables);
            Assert.Equal(1L, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));

            // idempotent
            await store.InitializeAsync(CancellationToken.None);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Controller_identity_is_created_once_and_persisted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bridge-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteBridgeStore(path, NullLogger<SqliteBridgeStore>.Instance);
            await store.InitializeAsync(CancellationToken.None);
            var first = await store.GetControllerIdentityAsync(CancellationToken.None);
            var second = await store.GetControllerIdentityAsync(CancellationToken.None);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.Ed25519Seed, second.Ed25519Seed);
            Assert.Equal(32, first.Ed25519Seed.Length);
            Assert.Null(await store.GetSettingAsync("missing", CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

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
