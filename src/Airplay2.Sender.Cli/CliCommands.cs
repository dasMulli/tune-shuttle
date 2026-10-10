using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Cli;

/// <summary>
/// The command implementations behind <see cref="Program"/> (README.md, "The CLI"), over replaceable <see cref="CliServices"/>. Every command that sets up a session tears it down
/// (SPEC.md D7) also on failure and on Ctrl+C; audio is never sent without <c>--i-know</c>.
/// </summary>
public sealed class CliCommands(CliServices services)
{
    /// <summary>The live-test level ceiling (docs/DEVELOPMENT.md, "Live-device rules").</summary>
    public const int LiveLevelCeiling = 20;

    /// <summary>The live-test audio budget per run (docs/DEVELOPMENT.md, "Live-device rules").</summary>
    public static readonly TimeSpan LiveAudioBudget = TimeSpan.FromSeconds(3);

    /// <summary>The name we announce in session SETUPs (the host name is appended).</summary>
    public static string SenderName => $"airplay2 CLI ({Environment.MachineName})";

    /// <summary>The services.</summary>
    public CliServices Services { get; } = services;

    private TextWriter Out => Services.Out;

    private TextWriter Error => Services.Error;

    /// <summary>
    /// <c>play</c>: refuses with <see cref="ExitCodes.AudioRefused"/> before touching anything when <paramref name="iKnow"/>
    /// is false. Otherwise opens the source, resolves every receiver, creates one group player through
    /// <see cref="AirplaySender"/> (transport chosen from the receivers' quirks), adds the receivers at
    /// <paramref name="level"/> (D5 each), streams the source in real time (the player's <c>Write</c> blocks, so a producer
    /// thread feeds it), waits until the receivers have played the tail (queue empty plus L), and tears everything down.
    /// </summary>
    public async Task<int> PlayAsync(string file, IReadOnlyList<string> to, int level, int latency, bool iKnow, bool sendMetadata, ILoggerFactory logging, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(logging);
        if (!iKnow)
        {
            // The guard comes first: no file, no store, no socket (docs/DEVELOPMENT.md, "Live-device rules").
            await Error.WriteLineAsync("Refusing to send audio without --i-know. Live rule (docs/DEVELOPMENT.md, \"Live-device rules\"): only the live receiver (AIRPLAY_LIVE_RECEIVER), a 3-second quiet tone (tone:3), --level 15, with the user's go-ahead. Nothing was sent.").ConfigureAwait(false);
            return ExitCodes.AudioRefused;
        }

        if (level is < 0 or > 100)
        {
            await Error.WriteLineAsync($"--level must be 0..100 (got {level}).").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        if (latency is < RealtimeStreamConstants.FramesPerPacket or > 5 * RealtimeStreamConstants.SampleRate)
        {
            await Error.WriteLineAsync($"--latency must be {RealtimeStreamConstants.FramesPerPacket}..{5 * RealtimeStreamConstants.SampleRate} frames (got {latency}; SPEC.md D1 default 77175).").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var names = to.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            await Error.WriteLineAsync("Name at least one receiver with --to.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        IPcmSource source;
        try
        {
            source = PcmSource.Open(file);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            await Error.WriteLineAsync($"Cannot play '{file}': {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        using (source)
        {
            var logger = logging.CreateLogger("airplay2.play");
            var duration = source.TotalFrames < 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(source.TotalFrames / (double)RealtimeStreamConstants.SampleRate);
            if (level > LiveLevelCeiling)
            {
                logger.LogWarning("Level {Level} is above the live-test ceiling of {Ceiling} (docs/DEVELOPMENT.md, \"Live-device rules\")", level, LiveLevelCeiling);
            }

            if (duration is null || duration > LiveAudioBudget)
            {
                logger.LogWarning("The source is {Duration}, longer than the 3-second live-test budget (docs/DEVELOPMENT.md, \"Live-device rules\"); only play it with the user present", duration is { } d ? $"{d.TotalSeconds:0.0} s" : "of unknown length");
            }

            await using var directory = Services.CreateDirectory(logging);
            var receivers = new List<DiscoveredReceiver>();
            foreach (var name in names)
            {
                if (await ResolveAsync(directory, name, ct).ConfigureAwait(false) is not { } receiver)
                {
                    return ExitCodes.Failure;
                }

                receivers.Add(receiver);
            }

            var store = Services.CreatePairingStore(logging);
            await using var sender = await CreateSenderAsync(SenderName, store, logging, ct).ConfigureAwait(false);
            return await PlayCoreAsync(sender, receivers, source, level, latency, sendMetadata, logging, logger, ct).ConfigureAwait(false);
        }
    }

    /// <summary><c>discover</c>: browses for <paramref name="seconds"/> and prints every receiver with its decoded capabilities (SPEC-control.md 1.6, 1.7).</summary>
    public async Task<int> DiscoverAsync(int seconds, ILoggerFactory logging, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logging);
        if (seconds is < 1 or > 600)
        {
            await Error.WriteLineAsync("--seconds must be 1..600.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        await using var directory = Services.CreateDirectory(logging);
        var receivers = await directory.BrowseAsync(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        if (receivers.Count == 0)
        {
            await Out.WriteLineAsync($"No AirPlay receivers seen within {seconds} s.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        foreach (var receiver in receivers.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            await Out.WriteLineAsync(Describe(receiver, Services.SenderOptions)).ConfigureAwait(false);
        }

        await Out.WriteLineAsync($"{receivers.Count} receiver(s).").ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    /// <summary><c>info</c>: unauthenticated <c>GET /info</c> (SPEC-control.md 1.8) by IP address or receiver name; prints the plist.</summary>
    public async Task<int> InfoAsync(string host, int port, bool withQualifier, ILoggerFactory logging, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(logging);
        if (port is < 1 or > ushort.MaxValue)
        {
            await Error.WriteLineAsync("--port must be 1..65535.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        IPEndPoint endpoint;
        if (IPAddress.TryParse(host, out var address))
        {
            endpoint = new IPEndPoint(address, port);
        }
        else
        {
            await using var directory = Services.CreateDirectory(logging);
            if (await ResolveAsync(directory, host, ct).ConfigureAwait(false) is not { } receiver)
            {
                return ExitCodes.Failure;
            }

            var target = receiver.AddressOf(AddressFamily.InterNetwork) ?? receiver.Addresses[0];
            endpoint = new IPEndPoint(target, receiver.Port);
        }

        try
        {
            await using var connection = await RtspConnection.ConnectAsync(endpoint, Services.SenderOptions.Connection, logging.CreateLogger<RtspConnection>(), ct).ConfigureAwait(false);
            var response = await connection.SendExpectOkAsync(AirplaySender.InfoRequest(withQualifier), ct).ConfigureAwait(false);
            await Out.WriteLineAsync($"GET /info{(withQualifier ? " (txtAirPlay qualifier)" : string.Empty)} {endpoint}: {response.StatusCode} {response.ReasonPhrase}, Server: {response.Header(RtspHeaders.Server) ?? "-"}, {response.Body.Length} bytes").ConfigureAwait(false);
            var plist = response.PlistBody();
            var text = new StringBuilder();
            AppendPlist(text, plist, 1);
            await Out.WriteAsync(text.ToString()).ConfigureAwait(false);
            return ExitCodes.Ok;
        }
        catch (Exception ex) when (IsReceiverError(ex))
        {
            await Error.WriteLineAsync($"GET /info {endpoint} failed: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// <c>pair</c>: what the receiver's TXT records require (SPEC-control.md 1.6). Transient receivers: pair (pair-verify
    /// with a stored pairing first), then an encrypted <c>GET /info</c> as the check; nothing new is stored. PIN or password
    /// receivers: the PIN flow (<see cref="AirplaySender.BeginPinPairingAsync"/> / <see cref="AirplaySender.CompletePinPairingAsync"/>)
    /// with <paramref name="pin"/> or a prompt, stored in the pairing file. <paramref name="forget"/> deletes the stored pairing.
    /// </summary>
    public async Task<int> PairAsync(string name, string? pin, bool forget, ILoggerFactory logging, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(logging);
        await using var directory = Services.CreateDirectory(logging);
        if (await ResolveAsync(directory, name, ct).ConfigureAwait(false) is not { } receiver)
        {
            return ExitCodes.Failure;
        }

        var store = Services.CreatePairingStore(logging);
        if (forget)
        {
            await store.DeleteAsync(receiver.Id, ct).ConfigureAwait(false);
            await Out.WriteLineAsync($"Stored pairing for {receiver.Name} ({receiver.Id}) deleted.").ConfigureAwait(false);
            return ExitCodes.Ok;
        }

        var requirement = receiver.Capabilities.Pairing;
        if (requirement == PairingRequirement.Disabled)
        {
            await Error.WriteLineAsync($"{receiver.Name} admits only members of its HomeKit home (acl=1 or act=2); pairing is not possible (SPEC-control.md 1.6).").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        await using var sender = await CreateSenderAsync(SenderName, store, logging, ct).ConfigureAwait(false);
        Func<CancellationToken, Task<string>> prompt = c => AskPinAsync(receiver, requirement, pin, c);
        try
        {
            var stored = await store.GetAsync(receiver.Id, ct).ConfigureAwait(false);
            if (requirement == PairingRequirement.Transient || stored is not null)
            {
                // Pair-verify with the stored pairing, transient pairing, or (if refused) the PIN flow through the prompt.
                await using var session = await sender.ConnectAsync(receiver, prompt, ct).ConfigureAwait(false);
                var info = await session.GetInfoAsync(withQualifier: false, ct).ConfigureAwait(false);
                var kind = session.Pairing!.Kind;
                await Out.WriteLineAsync($"Paired with {receiver.Name}: {kind}{(kind == PairingKind.Transient ? " (nothing to store; every connection pairs again)" : " (stored pairing verified)")}; encrypted GET /info answered {info.Count} keys.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            var started = await sender.BeginPinPairingAsync(receiver, ct).ConfigureAwait(false);
            var code = await prompt(ct).ConfigureAwait(false);
            var credentials = await sender.CompletePinPairingAsync(started, code, ct).ConfigureAwait(false);
            await Out.WriteLineAsync($"Paired with {receiver.Name}: accessory {credentials.AccessoryId}; stored and verified.").ConfigureAwait(false);
            return ExitCodes.Ok;
        }
        catch (PairingException ex)
        {
            await Error.WriteLineAsync($"Pairing with {receiver.Name} failed ({ex.Failure}{(ex.TlvError is { } e ? $", TLV {e}" : string.Empty)}{(ex.StatusCode is { } s ? $", status {s}" : string.Empty)}): {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        catch (Exception ex) when (IsReceiverError(ex))
        {
            await Error.WriteLineAsync($"Pairing with {receiver.Name} failed: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// <c>volume</c>: opens a session without audio (D5 up to the stream SETUP, because the srcvers-366 speaker applies volume only after
    /// it, probe P3; for a PTP-timed receiver with the sender's grandmaster and SETPEERS), reads <c>GET_PARAMETER volume</c>,
    /// sets <paramref name="level"/> when given and reads it back, then tears down (D7), also on Ctrl+C
    /// (<see cref="ExitCodes.Interrupted"/>). The receiver keeps the level for the next session (probe P10).
    /// </summary>
    public async Task<int> VolumeAsync(string name, int? level, ILoggerFactory logging, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(logging);
        if (level is < 0 or > 100)
        {
            await Error.WriteLineAsync($"The level must be 0..100 (got {level}).").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        await using var directory = Services.CreateDirectory(logging);
        if (await ResolveAsync(directory, name, ct).ConfigureAwait(false) is not { } receiver)
        {
            return ExitCodes.Failure;
        }

        var store = Services.CreatePairingStore(logging);
        await using var sender = await CreateSenderAsync(SenderName, store, logging, ct).ConfigureAwait(false);
        SilentSession? silent = null;
        try
        {
            silent = await OpenSilentSessionAsync(sender, receiver, ct).ConfigureAwait(false);
            var session = silent.Session;
            var before = await session.GetVolumeAsync(ct).ConfigureAwait(false);
            await Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{receiver.Name}: volume {before:0.0##} dB = level {VolumeMapping.DbToLevel(before)}")).ConfigureAwait(false);
            if (level is { } wanted)
            {
                await session.SetVolumeAsync(VolumeMapping.LevelToDb(wanted), ct).ConfigureAwait(false);
                var after = await session.GetVolumeAsync(ct).ConfigureAwait(false);
                await Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{receiver.Name}: set level {wanted} ({VolumeMapping.LevelToDb(wanted):0.0##} dB); reads back {after:0.0##} dB = level {VolumeMapping.DbToLevel(after)}")).ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Error.WriteLineAsync("Interrupted; tearing down.").ConfigureAwait(false);
            return ExitCodes.Interrupted;
        }
        catch (Exception ex) when (IsReceiverError(ex) || ex is PairingException)
        {
            await Error.WriteLineAsync($"Volume on {receiver.Name} failed: {ex.Message}").ConfigureAwait(false);
            if (sender.TimingProtocolFor(receiver) == TimingProtocol.Ntp && TimingHint(sender) is { } hint)
            {
                await Error.WriteLineAsync(hint).ConfigureAwait(false);
            }

            return ExitCodes.Failure;
        }
        finally
        {
            if (silent is not null)
            {
                await silent.CloseAsync(logging.CreateLogger("airplay2.volume")).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Formats one discovered receiver with the decoded TXT records (SPEC-control.md 1.3-1.7) and the timing protocol the
    /// sender would use with <paramref name="options"/> (its quirk overlay and default, <see cref="AirplaySender.ResolveQuirks"/>).
    /// </summary>
    public static string Describe(DiscoveredReceiver receiver, AirplaySenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        options ??= new AirplaySenderOptions();
        var a = receiver.Airplay;
        var c = receiver.Capabilities;
        var quirks = AirplaySender.ResolveQuirks(options, receiver);
        var defaultTiming = options.DefaultTimingProtocol;
        var features = (ulong)a.Features;
        var bits = Enumerable.Range(0, 64).Where(i => ((features >> i) & 1) != 0);
        var flagBits = Enumerable.Range(0, 32).Where(i => (((uint)a.Flags >> i) & 1) != 0);
        static string YesNo(bool value) => value ? "yes" : "no";
        static string Upper(TimingProtocol protocol) => protocol.ToString().ToUpperInvariant();
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{receiver.Name}  [{receiver.Id}]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  address   {string.Join(", ", receiver.Addresses.Select(x => x.ToString()))} port {receiver.Port}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  model     {a.Model ?? "?"} ({a.Manufacturer ?? "?"}), srcvers {a.SourceVersion ?? "?"}, fw {a.FirmwareVersion ?? "?"}, protovers {a.ProtocolVersion ?? "?"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  features  0x{features:X} (bits {string.Join(", ", bits)}); flags 0x{(uint)a.Flags:X} (bits {string.Join(", ", flagBits)}); acl {a.AccessControlLevel?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  pairing   {c.Pairing}; AirPlay 2 {YesNo(c.IsAirPlay2)}; MFi pair-setup bit {YesNo(c.MayNeedPairSetupWithAuth)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  streams   realtime {YesNo(c.SupportsRealtimeStream)}, buffered {YesNo(c.SupportsBufferedStream)}, ALAC {YesNo(c.SupportsAlac)}; PTP clock {YesNo(c.HasPtpClock)}, NTP bit {YesNo(c.PrefersNtp)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  metadata  text {YesNo(c.WantsTextMetadata)}, artwork {YesNo(c.WantsArtwork)}, progress {YesNo(c.WantsProgress)}, now-playing commands {YesNo(c.UsesNowPlayingCommands)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  timing    {(quirks.TimingProtocol is { } t ? $"{Upper(t)} (receiver quirk, probe P1)" : $"{Upper(defaultTiming)} (default, SPEC.md D1)")}");
        sb.Append(CultureInfo.InvariantCulture, $"  group     gid {a.GroupId ?? "-"}, gcgl {(a.GroupContainsGroupLeader ? 1 : 0)}{(receiver.Raop is null ? "; no _raop record" : string.Empty)}");
        return sb.ToString();
    }

    /// <summary>Creates the sender with a stable identity derived from the store's controller identity.</summary>
    internal async Task<AirplaySender> CreateSenderAsync(string name, IPairingStore store, ILoggerFactory logging, CancellationToken ct)
    {
        var controller = await store.GetControllerIdentityAsync(ct).ConfigureAwait(false);
        var options = Services.SenderOptions with { Identity = AirplaySender.DeriveIdentity(name, controller) };
        return new AirplaySender(options, store, logging);
    }

    /// <summary>Resolves a receiver name or id through the directory; prints the error and returns null when not found.</summary>
    internal async Task<DiscoveredReceiver?> ResolveAsync(IReceiverDirectory directory, string name, CancellationToken ct)
    {
        var receiver = await directory.FindAsync(name, Services.LookupTimeout, ct).ConfigureAwait(false);
        if (receiver is null)
        {
            await Error.WriteLineAsync($"Receiver '{name}' not found within {Services.LookupTimeout.TotalSeconds:0} s (try 'airplay2 discover').").ConfigureAwait(false);
        }
        else if (receiver.Addresses.Count == 0)
        {
            await Error.WriteLineAsync($"Receiver '{name}' has no address in its mDNS records.").ConfigureAwait(false);
            return null;
        }

        return receiver;
    }

    /// <summary>True for the failures a receiver or the network can cause (reported, not crashed on).</summary>
    internal static bool IsReceiverError(Exception ex) =>
        ex is IOException or SocketException or TimeoutException or RtspStatusException or InvalidDataException or FormatException or PtpMasterException;

    /// <summary>
    /// Session up to the stream SETUP without audio (D5), with the shared timing/control sockets of <paramref name="sender"/>.
    /// A PTP-timed receiver gets the sender's grandmaster (<see cref="AirplaySender.EnsurePtpMasterAsync"/>) as
    /// <c>timingPeerInfo</c> and becomes its peer through SETPEERS after RECORD, as a PTP group member does.
    /// </summary>
    internal static async Task<SilentSession> OpenSilentSessionAsync(AirplaySender sender, DiscoveredReceiver receiver, CancellationToken ct)
    {
        await sender.StartAsync(ct).ConfigureAwait(false);
        var protocol = sender.TimingProtocolFor(receiver);
        var ptp = protocol == TimingProtocol.Ptp ? await sender.EnsurePtpMasterAsync(ct).ConfigureAwait(false) : null;
        var session = await sender.ConnectAsync(receiver, pinPrompt: null, ct).ConfigureAwait(false);
        var silent = new SilentSession(session, ptp);
        try
        {
            var setup = new SessionSetupRequest(
                sender.Options.Identity,
                protocol,
                protocol == TimingProtocol.Ntp ? sender.Timing.Port : 0,
                Guid.NewGuid().ToString().ToUpperInvariant(),
                GroupUuid: null,
                GroupContainsGroupLeader: false,
                IsMultiSelectAirPlay: false,
                TimingPeerAddresses: null,
                PtpClockId: ptp?.ClockId);
            var result = await session.SetupSessionAsync(setup, ct).ConfigureAwait(false);
            await session.ConnectEventChannelAsync(ct).ConfigureAwait(false);
            await session.RecordAsync(ct).ConfigureAwait(false);
            if (ptp is not null)
            {
                silent.PtpPeer = await PtpSessionWiring.AttachAsync(session, ptp, result, null, ct).ConfigureAwait(false);
            }

            await session.SetupStreamAsync(new StreamSetupRequest(StreamType.Realtime, session.Pairing!.DefaultStreamKey, sender.Control.Port), ct).ConfigureAwait(false);
            return silent;
        }
        catch
        {
            await silent.CloseAsync(null).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The diagnosis for a failed NTP start-up during which not one timing request reached our responder. Observed live
    /// (2026-10-09, SPEC.md section 4, CLI note): the srcvers-366 speaker sends its 0xD2 requests while it holds the stream SETUP answer
    /// and, when none is answered, answers the stream SETUP with 400 after ~32 s; the cause was an inbound Windows Firewall
    /// block rule for <c>airplay2.exe</c>. Null when timing requests did arrive.
    /// </summary>
    internal static string? TimingHint(AirplaySender sender) =>
        sender.Timing.Port != 0 && sender.Timing.RepliesSent == 0
            ? $"No NTP timing request reached our timing port UDP {sender.Timing.Port}. Some receivers (srcvers 366.0, SPEC.md section 4) hold the stream SETUP until timing works and then refuse it (400). Check that inbound UDP to {Environment.ProcessPath ?? "this program"} is allowed (Windows Firewall: an inbound rule for the executable, or the UDP ports)."
            : null;

    /// <summary>TEARDOWN (D7) bounded by 10 s; failures are logged, never thrown.</summary>
    internal static async Task TeardownQuietlyAsync(IReceiverSession session, ILogger? logger)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await session.TeardownAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsReceiverError(ex) || ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            logger?.LogWarning("TEARDOWN of {Receiver} failed: {Error}", session.Receiver.Name, ex.Message);
        }
    }

    private static void AppendPlist(StringBuilder sb, object? value, int indent)
    {
        var pad = new string(' ', indent * 2);
        switch (value)
        {
            case PlistDictionary dict:
                foreach (var (key, item) in dict)
                {
                    if (item is PlistDictionary or List<object?>)
                    {
                        sb.Append(pad).Append(key).AppendLine(":");
                        AppendPlist(sb, item, indent + 1);
                    }
                    else if (key == "txtAirPlay" && item is byte[] txt)
                    {
                        sb.Append(pad).Append(key).Append(CultureInfo.InvariantCulture, $": {txt.Length} bytes of TXT strings: ").AppendLine(string.Join(" ", PascalStrings(txt)));
                    }
                    else
                    {
                        sb.Append(pad).Append(key).Append(": ").AppendLine(Scalar(item));
                    }
                }

                break;
            case List<object?> list:
                foreach (var item in list)
                {
                    if (item is PlistDictionary or List<object?>)
                    {
                        sb.Append(pad).AppendLine("-");
                        AppendPlist(sb, item, indent + 1);
                    }
                    else
                    {
                        sb.Append(pad).Append("- ").AppendLine(Scalar(item));
                    }
                }

                break;
            default:
                sb.Append(pad).AppendLine(Scalar(value));
                break;
        }
    }

    private static string Scalar(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        byte[] data => data.Length <= 32 ? $"<{Convert.ToHexString(data)}>" : $"<{data.Length} bytes>",
        long l when l > 0xFFFF => string.Create(CultureInfo.InvariantCulture, $"{l} (0x{l:X})"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static IEnumerable<string> PascalStrings(byte[] data)
    {
        for (var i = 0; i < data.Length;)
        {
            var length = data[i];
            var end = Math.Min(data.Length, i + 1 + length);
            yield return Encoding.UTF8.GetString(data, i + 1, end - i - 1);
            i = end;
        }
    }

    private async Task<string> AskPinAsync(DiscoveredReceiver receiver, PairingRequirement requirement, string? pin, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(pin))
        {
            return pin.Trim();
        }

        var question = requirement == PairingRequirement.Password
            ? $"Enter the AirPlay password of {receiver.Name}: "
            : $"Enter the PIN shown by {receiver.Name}: ";
        var answer = await Services.Prompt(question, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new PairingException(PairingFailure.Authentication, "No PIN was entered.");
        }

        return answer.Trim();
    }

    private async Task<int> PlayCoreAsync(AirplaySender sender, List<DiscoveredReceiver> receivers, IPcmSource source, int level, int latency, bool sendMetadata, ILoggerFactory logging, ILogger logger, CancellationToken ct)
    {
        // No silence auto-pause: the source ends once, DrainAsync waits for the last frames, and disposing the player tears
        // the members down. With the default 5 s timeout, waiting past the drain turned the end into a pause (FLUSH), whose
        // answer the srcvers-366 speaker delays by 2-30 s (SPEC.md P19), before the TEARDOWN.
        var options = sender.Options.Player with { LatencyFrames = latency, SilenceTimeout = TimeSpan.Zero };
        IGroupPlayer player;
        try
        {
            player = await sender.CreateGroupPlayerAsync(receivers, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsReceiverError(ex))
        {
            await Error.WriteLineAsync($"Cannot create the group player: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        using var eventsStop = new CancellationTokenSource();
        var events = LogEventsAsync(player, receivers, logger, eventsStop.Token);
        var joined = new List<DiscoveredReceiver>();
        var producer = Task.CompletedTask;
        using var producerStop = new CancellationTokenSource();
        var exit = ExitCodes.Ok;
        Task? nowPlaying = null;
        try
        {
            foreach (var receiver in receivers)
            {
                try
                {
                    await player.AddReceiverAsync(receiver, level, ct).ConfigureAwait(false);
                    joined.Add(receiver);
                }
                catch (Exception ex) when (IsReceiverError(ex) || ex is PairingException || ex is InvalidOperationException)
                {
                    await Error.WriteLineAsync($"{receiver.Name} could not join: {ex.Message}").ConfigureAwait(false);
                    exit = ExitCodes.Failure;
                }
            }

            if (joined.Count == 0)
            {
                await Error.WriteLineAsync("No receiver joined; nothing was played.").ConfigureAwait(false);
                if (sender.ChooseTransport(receivers).Timing == TimingProtocol.Ntp && TimingHint(sender) is { } hint)
                {
                    await Error.WriteLineAsync(hint).ConfigureAwait(false);
                }

                return ExitCodes.Failure;
            }

            var length = source.TotalFrames < 0 ? "unknown length" : string.Create(CultureInfo.InvariantCulture, $"{source.TotalFrames / (double)RealtimeStreamConstants.SampleRate:0.0} s");
            await Out.WriteLineAsync($"Playing {source.Title} ({length}) to {string.Join(", ", joined.Select(r => r.Name))} at level {level}, L = {latency} frames").ConfigureAwait(false);
            await player.StartAsync(ct).ConfigureAwait(false);
            if (sendMetadata)
            {
                // SPEC-audio.md 7: the track starts with the next frame written; announce it, then queue it. Best effort and
                // concurrent: the srcvers-366 speaker answers DMAP with 500 after up to 10 s (SPEC.md P9).
                var metadata = new TrackMetadata(source.Title, "airplay2 CLI", null, source.TotalFrames < 0 ? null : TimeSpan.FromSeconds(source.TotalFrames / (double)RealtimeStreamConstants.SampleRate));
                nowPlaying = player.SetNowPlayingAsync(metadata, null, null, TimeSpan.Zero, ct);
            }

            var watch = Stopwatch.StartNew();
            producer = StartProducer(player, source, producerStop.Token);
            await producer.WaitAsync(ct).ConfigureAwait(false);
            await DrainAsync(player, latency, ct).ConfigureAwait(false);

            // The now-playing task (DMAP, artwork, progress) is awaited in finally, after the player is disposed: the
            // The srcvers-366 speaker answers DMAP with 500 only after seconds (SPEC.md P9), and the end of playback does not wait for it.
            var states = player.Members;
            var lost = joined.Where(r => !states.TryGetValue(r.Id, out var s) || s == GroupMemberState.Lost).Select(r => r.Name).ToList();
            await Out.WriteLineAsync(Statistics(player, watch.Elapsed)).ConfigureAwait(false);
            if (lost.Count > 0)
            {
                await Error.WriteLineAsync($"Lost during playback: {string.Join(", ", lost)}").ConfigureAwait(false);
                exit = ExitCodes.Failure;
            }

            return exit;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Error.WriteLineAsync("Interrupted; tearing down.").ConfigureAwait(false);
            return ExitCodes.Interrupted;
        }
        catch (Exception ex) when (IsReceiverError(ex) || ex is InvalidOperationException)
        {
            await Error.WriteLineAsync($"Playback failed: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        finally
        {
            await producerStop.CancelAsync().ConfigureAwait(false);

            // TEARDOWN of every member (D7); disposing also releases a producer blocked in Write.
            await player.DisposeAsync().ConfigureAwait(false);
            await AwaitQuietlyAsync(producer, logger, "producer").ConfigureAwait(false);
            if (nowPlaying is not null)
            {
                await AwaitQuietlyAsync(nowPlaying, logger, "now-playing metadata").ConfigureAwait(false);
            }

            await eventsStop.CancelAsync().ConfigureAwait(false);
            await AwaitQuietlyAsync(events, logger, "events").ConfigureAwait(false);
        }
    }

    /// <summary>Feeds the player from a dedicated thread: <see cref="IGroupPlayer.Write"/> blocks by design (SPEC-audio.md 8.2).</summary>
    private static Task StartProducer(IGroupPlayer player, IPcmSource source, CancellationToken stop)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var buffer = new short[4410 * 2]; // 100 ms per Write
                while (!stop.IsCancellationRequested)
                {
                    var frames = source.Read(buffer);
                    if (frames == 0)
                    {
                        break;
                    }

                    player.Write(buffer.AsSpan(0, frames * 2));
                }

                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "airplay2 play producer",
        };
        thread.Start();
        return done.Task;
    }

    /// <summary>Waits until the ring is empty, then L plus a margin so the receivers play the last frames before TEARDOWN.</summary>
    private static async Task DrainAsync(IGroupPlayer player, int latency, CancellationToken ct)
    {
        if (player is GroupPlayer concrete)
        {
            var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 15);
            while (concrete.QueuedFrames > 0 && Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        await Task.Delay(TimeSpan.FromSeconds((latency / (double)RealtimeStreamConstants.SampleRate) + 0.5), ct).ConfigureAwait(false);
    }

    private static string Statistics(IGroupPlayer player, TimeSpan elapsed)
    {
        if (player is not GroupPlayer g)
        {
            return $"Done after {elapsed.TotalSeconds:0.0} s.";
        }

        var retransmits = g.Transport is RealtimeStreamTransport rt ? rt.Control.RetransmitRequestsReceived : 0;
        return string.Create(CultureInfo.InvariantCulture, $"Done after {elapsed.TotalSeconds:0.0} s: {g.PacketsSent} packets, {g.SilencePackets} silence, {g.LateWakeups} late wakeups, {g.Stalls} stalls, {retransmits} retransmit requests; members {string.Join(", ", g.Members.Select(m => $"{m.Key}={m.Value}"))}");
    }

    private static async Task LogEventsAsync(IGroupPlayer player, List<DiscoveredReceiver> receivers, ILogger logger, CancellationToken ct)
    {
        string NameOf(string id) => receivers.FirstOrDefault(r => r.Id == id)?.Name ?? id;
        try
        {
            await foreach (var e in player.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (e)
                {
                    case GroupPlayerEvent.MemberStateChanged s:
                        logger.LogInformation("{Receiver}: {State}{Reason}", NameOf(s.ReceiverId), s.State, s.Reason is null ? string.Empty : " (" + s.Reason + ")");
                        break;
                    case GroupPlayerEvent.ReceiverVolumeReported v:
                        logger.LogInformation("{Receiver} reports level {Level}", NameOf(v.ReceiverId), v.Level);
                        break;
                    case GroupPlayerEvent.RemoteCommand c:
                        logger.LogInformation("{Receiver} button: {Command}", NameOf(c.ReceiverId), c.Command);
                        break;
                    case GroupPlayerEvent.Statistics st:
                        logger.LogDebug("statistics: {Members} members, {Packets} packets, {Silence} silence, {Late} late wakeups, {Retransmits} retransmit requests", st.Members, st.PacketsSent, st.SilencePackets, st.LateWakeups, st.RetransmitRequests);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped after TEARDOWN
        }
    }

    private static async Task AwaitQuietlyAsync(Task task, ILogger logger, string what)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug("{What} ended with {Error}", what, ex.Message);
        }
    }

    /// <summary>A session opened by <see cref="OpenSilentSessionAsync"/>: the session and, under PTP, its grandmaster peer.</summary>
    internal sealed class SilentSession(IReceiverSession session, IPtpMaster? ptp)
    {
        /// <summary>The session (stream SETUP done, no audio).</summary>
        public IReceiverSession Session { get; } = session;

        /// <summary>The receiver's address as a peer of the grandmaster, once SETPEERS succeeded.</summary>
        public IPAddress? PtpPeer { get; set; }

        /// <summary>TEARDOWN (D7, bounded), closes the connection and removes the PTP peer again.</summary>
        public async Task CloseAsync(ILogger? logger)
        {
            await TeardownQuietlyAsync(Session, logger).ConfigureAwait(false);
            await Session.DisposeAsync().ConfigureAwait(false);
            if (PtpPeer is { } peer)
            {
                ptp?.RemovePeer(peer);
            }
        }
    }
}
