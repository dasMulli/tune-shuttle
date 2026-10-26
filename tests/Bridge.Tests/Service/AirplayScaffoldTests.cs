using System.Net.Http.Json;
using System.Text.Json;
using Airplay2.Receiver;
using Bridge.Contracts;
using Bridge.Service;
using Bridge.Service.Airplay;
using Bridge.Service.Output;
using Bridge.Service.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests.Service;

/// <summary>
/// The bridge side of the wave 0 scaffold (SPEC-receiver.md 13): migration 2 and the controller store (8.4), the AirPlay
/// settings with the identity created once (8.4, R11), the nullable DTO additions (9.1), the artwork route's 404 and the
/// registrations of <c>SourceSwitch</c>, <c>AirplaySource</c> and the shared <c>PtpPort</c> (8.6).
/// </summary>
public class AirplayScaffoldTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Controllers_are_stored_replaced_and_removed()
    {
        await using var store = new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance);
        await store.InitializeAsync(Ct);
        var key1 = Enumerable.Repeat((byte)1, 32).ToArray();
        var key2 = Enumerable.Repeat((byte)2, 32).ToArray();

        Assert.Empty(await store.GetAirplayControllersAsync(Ct));
        await store.SaveAirplayControllerAsync("B", key1, Ct);
        await store.SaveAirplayControllerAsync("A", key1, Ct);
        await store.SaveAirplayControllerAsync("B", key2, Ct);

        var rows = await store.GetAirplayControllersAsync(Ct);
        Assert.Equal(["A", "B"], rows.Select(r => r.ControllerId));
        Assert.Equal(key2, rows[1].PublicKey);

        await store.RemoveAirplayControllerAsync("A", Ct);
        await store.RemoveAirplayControllerAsync("missing", Ct);
        Assert.Equal(["B"], (await store.GetAirplayControllersAsync(Ct)).Select(r => r.ControllerId));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAirplayControllerAsync("C", new byte[31], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAirplayControllerAsync(" ", key1, Ct));
    }

    [Fact]
    public async Task Controller_store_adapter_round_trips_through_the_bridge_store()
    {
        await using var store = new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance);
        await store.InitializeAsync(Ct);
        var controllers = new BridgeControllerStore(store);
        var key = Enumerable.Repeat((byte)7, 32).ToArray();

        await controllers.SaveAsync("phone", key, Ct);
        var loaded = await controllers.LoadAsync(Ct);
        Assert.Equal(key, Assert.Single(loaded).Value);

        await controllers.RemoveAsync("phone", Ct);
        Assert.Empty(await controllers.LoadAsync(Ct));
    }

    [Fact]
    public async Task Settings_default_to_the_device_name_and_create_the_identity_once()
    {
        await using var store = new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance);
        await store.InitializeAsync(Ct);
        var defaults = new AirplayOptions();

        var first = await AirplaySettings.LoadAsync(store, defaults, "Wohnzimmer", Ct);
        Assert.True(first.Enabled);
        Assert.Equal("Wohnzimmer", first.Name);
        Assert.Null(first.Password);
        Assert.True(ReceiverIdentity.IsLocallyAdministeredMac(first.Identity.DeviceId));

        var second = await AirplaySettings.LoadAsync(store, defaults, "Other", Ct);
        Assert.Equal(first.Identity, second.Identity with { Seed = first.Identity.Seed }); // same id and psi
        Assert.Equal(first.Identity.Seed, second.Identity.Seed);
        Assert.Equal(first.Identity.PublicKey, second.Identity.PublicKey);

        var stored = await store.GetSettingAsync(BridgeSettingKeys.AirplayIdentity, Ct);
        using var json = JsonDocument.Parse(stored!);
        Assert.Equal(first.Identity.DeviceId, json.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal(first.Identity.Psi, json.RootElement.GetProperty("psi").GetString());
        Assert.Equal(64, json.RootElement.GetProperty("seed").GetString()!.Length);
    }

    [Fact]
    public async Task Settings_round_trip_and_an_empty_password_means_open()
    {
        await using var store = new SqliteBridgeStore(":memory:", NullLogger<SqliteBridgeStore>.Instance);
        await store.InitializeAsync(Ct);
        var defaults = new AirplayOptions { Enabled = false };
        var loaded = await AirplaySettings.LoadAsync(store, defaults, "Device", Ct);
        Assert.False(loaded.Enabled);

        await (loaded with { Enabled = true, Name = "Küche", Password = "secret" }).SaveAsync(store, Ct);
        var saved = await AirplaySettings.LoadAsync(store, defaults, "Device", Ct);
        Assert.True(saved.Enabled);
        Assert.Equal("Küche", saved.Name);
        Assert.Equal("secret", saved.Password);
        Assert.Equal(loaded.Identity.DeviceId, saved.Identity.DeviceId);

        await (saved with { Password = "" }).SaveAsync(store, Ct);
        Assert.Null((await AirplaySettings.LoadAsync(store, defaults, "Device", Ct)).Password);
        Assert.Null(await store.GetSettingAsync(BridgeSettingKeys.AirplayPassword, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"deviceId\":\"12:34:56:78:9A:BC\",\"psi\":\"X\",\"seed\":\"00\"}")]
    [InlineData("{\"deviceId\":\"02:00:00:00:00:01\",\"psi\":\"X\",\"seed\":\"0011\"}")]
    [InlineData("{\"deviceId\":\"02:00:00:00:00:01\",\"psi\":\"X\",\"seed\":\"zz\"}")]
    public void Unusable_identity_json_is_rejected_and_recreated(string? json)
    {
        Assert.False(AirplaySettings.TryParseIdentity(json, out _));

        var identity = ReceiverIdentity.Create();
        Assert.True(AirplaySettings.TryParseIdentity(AirplaySettings.SerializeIdentity(identity), out var parsed));
        Assert.Equal(identity, parsed with { Seed = identity.Seed });
        Assert.Equal(identity.Seed, parsed.Seed);
    }

    [Fact]
    public void Dto_additions_are_optional_and_null_by_default()
    {
        var settings = new SettingsDto("TuneShuttle", 77175, false, "Realtime", 3);
        Assert.Null(settings.AirplayEnabled);
        Assert.Null(settings.AirplayName);
        Assert.Null(settings.AirplayPassword);
        Assert.Null(settings.AirplayPasswordSet);

        var status = new StatusDto("1", false, null, "TuneShuttle", false, 0, DateTimeOffset.UnixEpoch);
        Assert.Null(status.ActiveSource);
        Assert.Null(status.Airplay);
        Assert.Null(new PlaybackDto(false, null, null, null, null, 0, 0, null, 0).Source);
        Assert.Null(new DiagnosticsDto([], [], 0, 0, 0, 0, 0, []).Airplay);

        // A client that predates the fields still reads the new answers, and the new fields survive a round trip.
        var json = JsonSerializer.Serialize(status with { ActiveSource = "airplay", Airplay = new AirplayReceiverDto("streaming", "TuneShuttle", "Phone", "iPhone14,3", false) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<StatusDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("airplay", back!.ActiveSource);
        Assert.Equal("streaming", back.Airplay!.State);
        Assert.Equal("/api/playback/artwork", ApiRoutes.PlaybackArtwork);
    }

    [Fact]
    public void Source_switch_names_sources_as_the_api_does()
    {
        var sources = new SourceSwitch();

        Assert.Equal(AudioSource.None, sources.Owner);
        Assert.Null(sources.Sender);
        Assert.Null(SourceSwitch.ToApiName(AudioSource.None));
        Assert.Equal("spotify", SourceSwitch.ToApiName(AudioSource.Spotify));
        Assert.Equal("airplay", SourceSwitch.ToApiName(AudioSource.Airplay));
        Assert.Equal(OutputManager.SourceAirplay, SourceSwitch.ToApiName(AudioSource.Airplay));
        Assert.Equal("SPEC-receiver.md 8.2, wave 3", Assert.Throws<NotImplementedException>(() => sources.MayWrite(AudioSource.Spotify)).Message);
    }

    [Fact]
    public async Task Bridge_registers_the_scaffold_services_and_answers_404_for_artwork()
    {
        await using var factory = new BridgeFactory();
        using var client = factory.CreateClient();

        Assert.NotNull(factory.Get<SourceSwitch>());
        Assert.NotNull(factory.Get<AirplaySource>());
        var ptp = factory.Get<Airplay2.Sender.Streams.IPtpPort>();
        Assert.False(ptp.IsBound); // wave 0: nothing binds 319/320
        Assert.Equal(ReceiverStatus.Off, factory.Get<AirplaySource>().Status);
        Assert.False(factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BridgeOptions>>().Value.Airplay.Enabled);

        var artwork = await client.GetAsync(ApiRoutes.PlaybackArtwork);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, artwork.StatusCode);

        var status = await client.GetFromJsonAsync<StatusDto>(ApiRoutes.Status);
        Assert.Null(status!.ActiveSource);
        Assert.Null(status.Airplay);
    }
}
