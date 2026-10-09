using System.Text.Json;
using Airplay2.Sender.Cli;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Pairing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests;

public sealed class JsonFilePairingStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"airplay2-pairings-{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_directory, "sub", JsonFilePairingStore.DefaultFileName);

    private JsonFilePairingStore NewStore() => new(FilePath, NullLogger<JsonFilePairingStore>.Instance);

    private static PairingCredentials Credentials(ControllerIdentity controller) =>
        new(controller, "02:1A:2B:3C:4D:5E", Ed25519KeyPair.Generate().PublicKey);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Missing_file_means_no_pairings_and_is_not_created_by_reads()
    {
        var store = NewStore();
        Assert.Null(await store.GetAsync("X", Ct));
        await store.DeleteAsync("X", Ct);
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".lock"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(FilePath)));
    }

    [Fact]
    public async Task Controller_identity_is_created_once_and_survives_a_restart()
    {
        var first = await NewStore().GetControllerIdentityAsync(Ct);
        var again = await NewStore().GetControllerIdentityAsync(Ct);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Ed25519Seed, again.Ed25519Seed);
        Assert.Equal(first.KeyPair.PublicKey, again.KeyPair.PublicKey);
    }

    [Fact]
    public async Task Pairing_round_trip_with_real_credentials()
    {
        var store = NewStore();
        var controller = await store.GetControllerIdentityAsync(Ct);
        var credentials = Credentials(controller);
        await store.SaveAsync("R1", credentials, Ct);
        await store.SaveAsync("R2", Credentials(controller), Ct);

        var loaded = await NewStore().GetAsync("R1", Ct);
        Assert.NotNull(loaded);
        Assert.Equal(controller.Id, loaded!.Controller.Id);
        Assert.Equal(controller.Ed25519Seed, loaded.Controller.Ed25519Seed);
        Assert.Equal(credentials.AccessoryId, loaded.AccessoryId);
        Assert.Equal(credentials.AccessoryPublicKey, loaded.AccessoryPublicKey);
        var message = "M3"u8.ToArray();
        Assert.True(Ed25519KeyPair.Verify(controller.KeyPair.PublicKey, message, loaded.Controller.KeyPair.Sign(message)));

        await store.DeleteAsync("R1", Ct);
        Assert.Null(await store.GetAsync("R1", Ct));
        Assert.NotNull(await store.GetAsync("R2", Ct));
        Assert.Equal(controller.Id, (await NewStore().GetControllerIdentityAsync(Ct)).Id);

        // Hex key material, no temp file left behind.
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(64, json.RootElement.GetProperty("controller").GetProperty("seed").GetString()!.Length);
        Assert.Equal(64, json.RootElement.GetProperty("pairings").GetProperty("R2").GetProperty("accessoryPublicKey").GetString()!.Length);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public async Task Invalid_credentials_are_rejected()
    {
        var store = NewStore();
        var good = Credentials(ControllerIdentity.Generate());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("R", good with { AccessoryPublicKey = [1, 2] }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("R", good with { AccessoryId = "" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("", good, Ct));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Corrupt_file_is_reported_and_left_untouched()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, "{ not json");
        var store = NewStore();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetControllerIdentityAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("R", Credentials(ControllerIdentity.Generate()), Ct));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(FilePath));

        await File.WriteAllTextAsync(FilePath, """{"version":1,"controller":{"id":"C","seed":"0102"},"pairings":{}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetControllerIdentityAsync(Ct));

        await File.WriteAllTextAsync(FilePath, """{"version":99,"pairings":{}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync("R", Ct));
    }

    [Fact]
    public async Task Concurrent_writers_from_several_instances_lose_nothing()
    {
        var controller = await NewStore().GetControllerIdentityAsync(Ct);
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore()).ToArray();
        const int count = 40;
        await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() => stores[i % stores.Length].SaveAsync($"R{i}", Credentials(controller), Ct))));

        var reader = NewStore();
        for (var i = 0; i < count; i++)
        {
            Assert.NotNull(await reader.GetAsync($"R{i}", Ct));
        }
    }

    [Fact]
    public async Task Lock_held_elsewhere_times_out()
    {
        var store = new JsonFilePairingStore(FilePath, NullLogger<JsonFilePairingStore>.Instance) { LockTimeout = TimeSpan.FromMilliseconds(200) };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using (new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => store.GetControllerIdentityAsync(Ct));
        }

        Assert.NotNull(await store.GetControllerIdentityAsync(Ct));
        Assert.Null(await store.GetAsync("R", Ct));
    }
}
