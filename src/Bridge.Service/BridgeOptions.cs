using System.Globalization;
using System.Net;
using Bridge.Service.Store;

namespace Bridge.Service;

/// <summary>
/// The <c>Bridge</c> configuration section (appsettings.json, environment variables with the <c>BRIDGE_</c> prefix and
/// <c>__</c> as separator, command line <c>--Bridge:Key value</c>). Values the user changes in the web app live in the
/// <c>settings</c> table and win over these defaults (<see cref="BridgeSettings"/>).
/// </summary>
public sealed class BridgeOptions
{
    /// <summary>Spotify Connect device name until the user sets one in the web app.</summary>
    public string DeviceName { get; set; } = "AirPlay Bridge";

    /// <summary>Sender latency L in frames (SPEC-audio.md 1.5: 77 175 = 1.75 s).</summary>
    public int LatencyFrames { get; set; } = 77175;

    /// <summary>Preferred stream type, <c>Realtime</c> or <c>Buffered</c> (SPEC.md D1; buffered falls back to realtime per receiver capability).</summary>
    public string PreferredStream { get; set; } = "Realtime";

    /// <summary>Seconds between <c>GET_PARAMETER volume</c> polls while playing (SPEC.md D10 fallback); 0 disables the poll.</summary>
    public int PollReceiverVolumeSeconds { get; set; } = 3;

    /// <summary>TCP port of the Spotify Connect Zeroconf HTTP server; 0 picks a free one.</summary>
    public int ZeroconfPort { get; set; }

    /// <summary>Fixed UDP ports for the sender's timing and control sockets (0: OS-chosen).</summary>
    public SenderPortOptions SenderPorts { get; set; } = new();

    /// <summary>Hosts librespot (Spotify Connect). Tests and API-only runs switch it off.</summary>
    public bool EnableSpotify { get; set; } = true;

    /// <summary>Starts the mDNS receiver browser. Tests that inject their own browser switch it off.</summary>
    public bool BrowseReceivers { get; set; } = true;

    /// <summary>Writes log files to <c>&lt;DataDirectory&gt;/logs</c> on Windows (PLAN.md "Logging").</summary>
    public bool FileLogging { get; set; } = true;

    /// <summary>Days of log files to keep.</summary>
    public int LogRetentionDays { get; set; } = 7;

    /// <summary>
    /// Development switch: start this many in-process fake receivers on loopback and show them instead of the LAN
    /// (WORK-PLAN.md web-app: <c>bridge --Bridge:FakeReceivers=2</c>). Only available in Debug builds made from the full
    /// repository (the fake receiver lives under <c>tests/</c>).
    /// </summary>
    public int FakeReceivers { get; set; }

    /// <summary>Local address for the sender's timing/control sockets; empty binds every interface.</summary>
    public string? LocalAddress { get; set; }

    /// <summary>Upper bound for connecting one receiver (connect, GET /info, pairing, SETUP, RECORD, volume).</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>First delay before a lost or failed receiver is connected again; doubles per failure.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Largest delay between two reconnect attempts.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a connect waits for an offline receiver to appear in mDNS before it falls back to its last address.</summary>
    public TimeSpan ResolveTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Offline receivers that carry no user state (no stored pairing, no group, not the selected output, default level and
    /// offset) are forgotten once they have not been seen for this long (<see cref="Output.ReceiverPruner"/>, hourly);
    /// <see cref="TimeSpan.Zero"/> never forgets. The web app's Settings page overrides it (<see cref="ReceiverRetention"/>).
    /// </summary>
    public TimeSpan ForgetOfflineAfter { get; set; } = TimeSpan.FromDays(3);

    /// <summary>The parsed <see cref="LocalAddress"/>, or null.</summary>
    public IPAddress? LocalIPAddress => string.IsNullOrWhiteSpace(LocalAddress) ? null : IPAddress.Parse(LocalAddress);
}

/// <summary>Fixed sender ports (<c>Bridge:SenderPorts</c>).</summary>
public sealed class SenderPortOptions
{
    /// <summary>NTP timing responder port; 0: OS-chosen.</summary>
    public int Timing { get; set; }

    /// <summary>Control (sync/retransmit) port; 0: OS-chosen.</summary>
    public int Control { get; set; }
}

/// <summary>Setting keys the service adds to <see cref="SettingKeys"/> (the <c>settings</c> table is key/value, no migration needed).</summary>
public static class BridgeSettingKeys
{
    /// <summary>The Spotify Connect device id (a UUID), generated once so the device keeps its identity across restarts.</summary>
    public const string SpotifyDeviceId = "spotify_device_id";

    /// <summary>
    /// Prefix of the per-group reference master level: while a group's G is 0 its member levels keep the values they had at
    /// this level, so raising G again restores the relative levels (PLAN.md volume model).
    /// </summary>
    public const string GroupReferenceLevelPrefix = "group_reference_level:";

    /// <summary>How long an offline receiver without user state is kept (<see cref="ReceiverRetention"/>), as a <c>c</c>-format TimeSpan.</summary>
    public const string ForgetOfflineAfter = "forget_offline_after";
}

/// <summary>
/// The "forget offline receivers after" setting (<see cref="BridgeOptions.ForgetOfflineAfter"/>, overridden in the
/// <c>settings</c> table by the Settings page). Kept apart from <see cref="BridgeSettings"/>: changing it rebuilds nothing.
/// </summary>
public static class ReceiverRetention
{
    /// <summary>Longest accepted value in days (ten years).</summary>
    public const double MaxDays = 3650;

    /// <summary>The effective value: the stored one, else the configuration. Never negative.</summary>
    public static async Task<TimeSpan> LoadAsync(IBridgeStore store, BridgeOptions defaults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(defaults);
        var stored = await store.GetSettingAsync(BridgeSettingKeys.ForgetOfflineAfter, cancellationToken).ConfigureAwait(false);
        var value = TimeSpan.TryParse(stored, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaults.ForgetOfflineAfter;
        return value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    /// <summary>
    /// Stores a value given in days (the API's unit), rounded like <see cref="ToDays"/> so the stored value is the one the
    /// API reports (0.0004 days is stored as 0, never, not as 35 seconds that GET /settings would show as 0). Throws
    /// <see cref="ArgumentException"/> outside 0..<see cref="MaxDays"/>.
    /// </summary>
    public static Task SaveDaysAsync(IBridgeStore store, double days, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (double.IsNaN(days) || days is < 0 or > MaxDays)
        {
            throw new ArgumentException($"Forgetting offline receivers takes 0 (never) to {MaxDays.ToString(CultureInfo.InvariantCulture)} days.", nameof(days));
        }

        return store.SetSettingAsync(BridgeSettingKeys.ForgetOfflineAfter, TimeSpan.FromDays(RoundDays(days)).ToString("c", CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>A value in days for the API, rounded to a thousandth (a minute and a half) so the JSON stays readable.</summary>
    public static double ToDays(TimeSpan value) => RoundDays(value.TotalDays);

    private static double RoundDays(double days) => Math.Round(days, 3);
}

/// <summary>The user-changeable settings, resolved from the store with the configuration as default.</summary>
/// <param name="DeviceName">Spotify Connect device name.</param>
/// <param name="LatencyFrames">L in frames.</param>
/// <param name="PollReceiverVolume">Poll the receivers' volume while playing.</param>
/// <param name="PreferredStream">"Realtime" or "Buffered".</param>
public sealed record BridgeSettings(string DeviceName, int LatencyFrames, bool PollReceiverVolume, string PreferredStream)
{
    /// <summary>Smallest accepted L (0.5 s).</summary>
    public const int MinLatencyFrames = 22050;

    /// <summary>Largest accepted L (4 s).</summary>
    public const int MaxLatencyFrames = 176400;

    /// <summary>Loads the settings.</summary>
    public static async Task<BridgeSettings> LoadAsync(IBridgeStore store, BridgeOptions defaults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(defaults);
        var name = await store.GetSettingAsync(SettingKeys.DeviceName, cancellationToken).ConfigureAwait(false);
        var latency = await store.GetSettingAsync(SettingKeys.LatencyFrames, cancellationToken).ConfigureAwait(false);
        var poll = await store.GetSettingAsync(SettingKeys.PollReceiverVolume, cancellationToken).ConfigureAwait(false);
        var stream = await store.GetSettingAsync(SettingKeys.PreferredStream, cancellationToken).ConfigureAwait(false);
        return new BridgeSettings(
            string.IsNullOrWhiteSpace(name) ? defaults.DeviceName : name,
            int.TryParse(latency, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : defaults.LatencyFrames,
            bool.TryParse(poll, out var p) ? p : defaults.PollReceiverVolumeSeconds > 0,
            NormalizeStream(stream) ?? NormalizeStream(defaults.PreferredStream) ?? "Realtime");
    }

    /// <summary>"Realtime"/"Buffered" for any casing of the two names, else null.</summary>
    public static string? NormalizeStream(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "REALTIME" => "Realtime",
        "BUFFERED" => "Buffered",
        _ => null,
    };

    /// <summary>Persists the settings.</summary>
    public async Task SaveAsync(IBridgeStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.SetSettingAsync(SettingKeys.DeviceName, DeviceName, cancellationToken).ConfigureAwait(false);
        await store.SetSettingAsync(SettingKeys.LatencyFrames, LatencyFrames.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await store.SetSettingAsync(SettingKeys.PollReceiverVolume, PollReceiverVolume ? "true" : "false", cancellationToken).ConfigureAwait(false);
        await store.SetSettingAsync(SettingKeys.PreferredStream, PreferredStream, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Where the bridge keeps its files (PLAN.md "Deployment": a configurable data directory, a volume in the container).</summary>
public sealed class BridgePaths
{
    /// <summary>Resolves <c>DataDirectory</c> from the configuration (default <c>&lt;app&gt;/data</c>) and creates it.</summary>
    public BridgePaths(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration["DataDirectory"];
        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "data") : configured);
        Directory.CreateDirectory(DataDirectory);
    }

    /// <summary>The data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>The SQLite file.</summary>
    public string DatabasePath => Path.Combine(DataDirectory, "bridge.db");

    /// <summary>The log directory.</summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>librespot's temporary directory (audio file downloads in flight).</summary>
    public string TempDirectory => Path.Combine(DataDirectory, "tmp");
}
