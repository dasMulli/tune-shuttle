using System.Text.Json;
using System.Text.Json.Serialization;
using Airplay2.Receiver;
using Bridge.Service.Store;

namespace Bridge.Service.Airplay;

/// <summary>
/// The user-changeable receiver settings (SPEC-receiver.md 8.4 <c>settings</c> keys): <c>airplay_enabled</c>,
/// <c>airplay_name</c>, <c>airplay_password</c> and the <c>airplay_identity</c> JSON <c>{deviceId, psi, seed}</c>, created
/// once on first load (R11). Kept apart from <see cref="BridgeSettings"/>: a change here never rebuilds the output.
/// </summary>
/// <param name="Enabled">Offer the receiver (default: <c>Bridge:Airplay:Enabled</c>).</param>
/// <param name="Name">The advertised name (default: the Spotify device name, R11).</param>
/// <param name="Password">The password, or null for the open mode (R7).</param>
/// <param name="Identity">The stored identity.</param>
public sealed record AirplaySettings(bool Enabled, string Name, string? Password, ReceiverIdentity Identity)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Loads the settings (SPEC-receiver.md 8.4): the stored values, else <paramref name="defaults"/> and
    /// <paramref name="deviceName"/>; a missing or unreadable identity is created and stored (R11: once per installation).
    /// </summary>
    public static async Task<AirplaySettings> LoadAsync(IBridgeStore store, AirplayOptions defaults, string deviceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        var enabled = await store.GetSettingAsync(BridgeSettingKeys.AirplayEnabled, cancellationToken).ConfigureAwait(false);
        var name = await store.GetSettingAsync(BridgeSettingKeys.AirplayName, cancellationToken).ConfigureAwait(false);
        var password = await store.GetSettingAsync(BridgeSettingKeys.AirplayPassword, cancellationToken).ConfigureAwait(false);
        var identityJson = await store.GetSettingAsync(BridgeSettingKeys.AirplayIdentity, cancellationToken).ConfigureAwait(false);
        if (!TryParseIdentity(identityJson, out var identity))
        {
            identity = ReceiverIdentity.Create();
            await store.SetSettingAsync(BridgeSettingKeys.AirplayIdentity, SerializeIdentity(identity), cancellationToken).ConfigureAwait(false);
        }

        return new AirplaySettings(
            bool.TryParse(enabled, out var e) ? e : defaults.Enabled,
            string.IsNullOrWhiteSpace(name) ? deviceName : name,
            string.IsNullOrEmpty(password) ? null : password,
            identity);
    }

    /// <summary>Persists <see cref="Enabled"/>, <see cref="Name"/> and <see cref="Password"/> (null removes the password: the open mode); the identity is never rewritten.</summary>
    public async Task SaveAsync(IBridgeStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.SetSettingAsync(BridgeSettingKeys.AirplayEnabled, Enabled ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await store.SetSettingAsync(BridgeSettingKeys.AirplayName, Name, cancellationToken).ConfigureAwait(false);
        await store.SetSettingAsync(BridgeSettingKeys.AirplayPassword, string.IsNullOrEmpty(Password) ? null : Password, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>airplay_identity</c> JSON (SPEC-receiver.md 8.4): <c>{"deviceId": ..., "psi": ..., "seed": "&lt;64 hex&gt;"}</c>.</summary>
    public static string SerializeIdentity(ReceiverIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return JsonSerializer.Serialize(new IdentityJson(identity.DeviceId, identity.Psi, Convert.ToHexString(identity.Seed).ToLowerInvariant()), JsonOptions);
    }

    /// <summary>Parses the <c>airplay_identity</c> JSON; false for null, malformed JSON, a bad MAC or a seed that is not 32 bytes.</summary>
    public static bool TryParseIdentity(string? json, out ReceiverIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        IdentityJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<IdentityJson>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.DeviceId) || string.IsNullOrWhiteSpace(parsed.Psi) || string.IsNullOrWhiteSpace(parsed.Seed)
            || !ReceiverIdentity.IsLocallyAdministeredMac(parsed.DeviceId))
        {
            return false;
        }

        byte[] seed;
        try
        {
            seed = Convert.FromHexString(parsed.Seed);
        }
        catch (FormatException)
        {
            return false;
        }

        if (seed.Length != ReceiverIdentity.SeedSize)
        {
            return false;
        }

        identity = new ReceiverIdentity(parsed.DeviceId, parsed.Psi, seed);
        return true;
    }

    private sealed record IdentityJson(
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("psi")] string Psi,
        [property: JsonPropertyName("seed")] string Seed);
}
