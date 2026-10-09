using System.Text.Json;
using System.Text.Json.Serialization;
using Airplay2.Sender.Pairing;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Cli;

/// <summary>
/// <see cref="IPairingStore"/> for the CLI: the controller identity and the regular pairings (SPEC-control.md 3.8) in one
/// JSON file, by default <c>airplay2-pairings.json</c> next to the executable. The bridge uses the SQLite store instead.
/// </summary>
/// <remarks>
/// <para>
/// Format (version 1), hex for all key material, like pyatv's credential string [pyatv:hap_pairing.py:310-319]:
/// <code>
/// { "version": 1,
///   "controller": { "id": "&lt;UUID&gt;", "seed": "&lt;32-byte hex&gt;" },
///   "pairings": { "&lt;receiver id&gt;": { "controllerId": "...", "controllerSeed": "...", "accessoryId": "...",
///                                     "accessoryPublicKey": "&lt;32-byte hex&gt;", "createdAt": "..." } } }
/// </code>
/// </para>
/// <para>
/// Every operation reads the file, changes it and replaces it atomically (write to <c>.tmp</c>, flush, rename) while
/// holding an exclusive OS lock on <c>&lt;file&gt;.lock</c>, so two CLI processes never lose each other's pairings.
/// The lock file is left in place: deleting it would let a third process lock a new inode while a second still holds
/// the old one. Reads and deletes of a file that does not exist yet touch nothing (no directory, no lock file), so a
/// CLI installed in a read-only location can still look up a receiver it never paired with. On Unix the file is created with mode 0600 because it holds private key seeds. A file that cannot be
/// parsed is never overwritten; the operation throws <see cref="InvalidDataException"/> instead.
/// </para>
/// </remarks>
public sealed class JsonFilePairingStore : IPairingStore
{
    /// <summary>The default file name.</summary>
    public const string DefaultFileName = "airplay2-pairings.json";

    /// <summary>The file format version this class reads and writes.</summary>
    public const int FormatVersion = 1;

    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonFilePairingStore> _logger;

    /// <summary>Creates a store backed by <paramref name="path"/>; the file and its directory are created on the first write.</summary>
    public JsonFilePairingStore(string path, ILogger<JsonFilePairingStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        FilePath = Path.GetFullPath(path);
        _logger = logger;
    }

    /// <summary><c>airplay2-pairings.json</c> next to the executable.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, DefaultFileName);

    /// <summary>The full path of the JSON file.</summary>
    public string FilePath { get; }

    /// <summary>How long an operation waits for another process holding the file lock before it throws <see cref="TimeoutException"/>.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public Task<ControllerIdentity> GetControllerIdentityAsync(CancellationToken cancellationToken) =>
        WithFileAsync(file =>
        {
            if (file.Controller is { } stored)
            {
                return (ToIdentity(stored.Id, stored.Seed, "controller"), false);
            }

            var identity = ControllerIdentity.Generate();
            file.Controller = new ControllerEntry(identity.Id, Convert.ToHexString(identity.Ed25519Seed));
            _logger.LogInformation("Created controller identity {ControllerId} in {File}", identity.Id, FilePath);
            return (identity, true);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<PairingCredentials?> GetAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(receiverId);
        if (!File.Exists(FilePath))
        {
            // Nothing stored yet; a concurrent first write is indistinguishable from one that happens after this read.
            return Task.FromResult<PairingCredentials?>(null);
        }

        return WithFileAsync(file =>
        {
            if (!file.Pairings.TryGetValue(receiverId, out var entry))
            {
                return ((PairingCredentials?)null, false);
            }

            var accessoryKey = FromHex(entry.AccessoryPublicKey, 32, $"pairings.{receiverId}.accessoryPublicKey");
            if (string.IsNullOrEmpty(entry.AccessoryId))
            {
                throw Corrupt($"pairings.{receiverId}.accessoryId is empty");
            }

            return (new PairingCredentials(ToIdentity(entry.ControllerId, entry.ControllerSeed, $"pairings.{receiverId}"), entry.AccessoryId, accessoryKey), false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveAsync(string receiverId, PairingCredentials credentials, CancellationToken cancellationToken)
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

        return WithFileAsync(file =>
        {
            file.Pairings[receiverId] = new PairingEntry(
                credentials.Controller.Id,
                Convert.ToHexString(credentials.Controller.Ed25519Seed),
                credentials.AccessoryId,
                Convert.ToHexString(credentials.AccessoryPublicKey),
                DateTimeOffset.UtcNow);
            _logger.LogInformation("Stored pairing for {ReceiverId} (accessory {AccessoryId}) in {File}", receiverId, credentials.AccessoryId, FilePath);
            return (0, true);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(receiverId);
        if (!File.Exists(FilePath))
        {
            return Task.CompletedTask;
        }

        return WithFileAsync(file =>
        {
            var removed = file.Pairings.Remove(receiverId);
            if (removed)
            {
                _logger.LogInformation("Deleted pairing for {ReceiverId} from {File}", receiverId, FilePath);
            }

            return (0, removed);
        }, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on the current file content under the in-process gate and the cross-process
    /// lock; when it reports a change the file is replaced atomically.
    /// </summary>
    private async Task<T> WithFileAsync<T>(Func<PairingFile, (T Result, bool Changed)> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
            var file = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var (result, changed) = operation(file);
            if (changed)
            {
                await WriteAtomicallyAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lockPath = FilePath + ".lock";
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                // FileShare.None is a mandatory lock on Windows and an flock(LOCK_EX) on Unix.
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException e) when (IsLockContention(e) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e) when (IsLockContention(e))
            {
                throw new TimeoutException($"Another process holds {lockPath} for more than {LockTimeout.TotalSeconds:0} s.", e);
            }
        }
    }

    /// <summary>
    /// True when opening the lock file failed only because another handle holds it: ERROR_SHARING_VIOLATION or
    /// ERROR_LOCK_VIOLATION on Windows; on Unix .NET reports a failed <c>flock(LOCK_EX|LOCK_NB)</c> as an
    /// <see cref="IOException"/> whose HResult is the raw errno EWOULDBLOCK/EAGAIN (11 on Linux, 35 on macOS/BSD).
    /// Any other I/O error (path is a directory, disk full, name too long) is not contention and is rethrown at once.
    /// </summary>
    private static bool IsLockContention(IOException e)
    {
        if (e is FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            const int SharingViolation = unchecked((int)0x80070020);
            const int LockViolation = unchecked((int)0x80070021);
            return e.HResult is SharingViolation or LockViolation;
        }

        const int EWouldBlockLinux = 11;
        const int EWouldBlockDarwin = 35;
        return e.HResult == (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsFreeBSD() ? EWouldBlockDarwin : EWouldBlockLinux);
    }

    private async Task<PairingFile> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new PairingFile();
        }

        var bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan().Trim(" \t\r\n"u8).IsEmpty)
        {
            _logger.LogWarning("Pairing file {File} is empty; starting with no pairings", FilePath);
            return new PairingFile();
        }

        PairingFile? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, PairingFileJsonContext.Default.PairingFile);
        }
        catch (JsonException e)
        {
            _logger.LogError(e, "Pairing file {File} is not valid JSON", FilePath);
            throw new InvalidDataException($"Pairing file {FilePath} is not valid JSON; fix or delete it (it holds private keys, so it is never overwritten).", e);
        }

        if (file is null)
        {
            throw Corrupt("the document is null");
        }

        if (file.Version > FormatVersion)
        {
            throw new InvalidDataException($"Pairing file {FilePath} has format version {file.Version}; this build reads up to {FormatVersion}.");
        }

        file.Version = FormatVersion;
        file.Pairings ??= new Dictionary<string, PairingEntry>(StringComparer.Ordinal);
        return file;
    }

    private async Task WriteAtomicallyAsync(PairingFile file, CancellationToken cancellationToken)
    {
        var temp = FilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(temp, options);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, file, PairingFileJsonContext.Default.PairingFile, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    private ControllerIdentity ToIdentity(string? id, string? seedHex, string where)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw Corrupt($"{where} has no id");
        }

        return new ControllerIdentity(id, FromHex(seedHex, 32, where + " seed"));
    }

    private byte[] FromHex(string? hex, int length, string where)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex ?? "");
        }
        catch (FormatException e)
        {
            throw new InvalidDataException($"Pairing file {FilePath}: {where} is not hex.", e);
        }

        return bytes.Length == length ? bytes : throw Corrupt($"{where} has {bytes.Length} bytes, expected {length}");
    }

    private InvalidDataException Corrupt(string what) => new($"Pairing file {FilePath} is corrupt: {what}.");
}

/// <summary>The JSON document of <see cref="JsonFilePairingStore"/>.</summary>
internal sealed class PairingFile
{
    public int Version { get; set; } = JsonFilePairingStore.FormatVersion;

    public ControllerEntry? Controller { get; set; }

    public Dictionary<string, PairingEntry> Pairings { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The installation's controller identity.</summary>
internal sealed record ControllerEntry(string Id, string Seed);

/// <summary>One stored pairing.</summary>
internal sealed record PairingEntry(string ControllerId, string ControllerSeed, string AccessoryId, string AccessoryPublicKey, DateTimeOffset CreatedAt);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PairingFile))]
internal sealed partial class PairingFileJsonContext : JsonSerializerContext;
