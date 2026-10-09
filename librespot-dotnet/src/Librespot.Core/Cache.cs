// Port of core/src/cache.rs.
using System.Globalization;
using System.Text.Json;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core;

/// <summary>Upstream <c>CacheError::Path</c> ("audio cache location is not configured"), kind FailedPrecondition.</summary>
public sealed class CacheException() : LibrespotException(ErrorKind.FailedPrecondition, "audio cache location is not configured");

/// <summary>
/// Some kind of data structure that holds some paths, the size of these files and a timestamp.
/// It keeps track of the file sizes and is able to pop the path with the oldest timestamp if
/// a given limit is exceeded.
/// </summary>
/// <remarks>
/// Upstream keeps a <c>PriorityQueue&lt;PathBuf, Reverse&lt;SystemTime&gt;&gt;</c> (with change-priority and remove by
/// key). The port uses a <see cref="SortedSet{T}"/> ordered by (timestamp, path) plus a timestamp lookup, which gives
/// the same operations in O(log n). Ties between equal timestamps pop in ordinal path order (unspecified upstream).
/// Not thread-safe; <see cref="FsSizeLimiter"/> guards it with a lock.
/// </remarks>
internal sealed class SizeLimiter
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Cache>();

    private static readonly Comparer<(DateTime Accessed, string Path)> QueueOrder = Comparer<(DateTime Accessed, string Path)>.Create(
        static (a, b) =>
        {
            var c = a.Accessed.CompareTo(b.Accessed);
            return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
        });

    private readonly SortedSet<(DateTime Accessed, string Path)> _queue = new(QueueOrder);
    private readonly Dictionary<string, DateTime> _priorities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _sizes = new(StringComparer.Ordinal);
    private readonly ulong _sizeLimit;
    private ulong _inUse;

    /// <summary>Creates a new instance with the given size limit.</summary>
    public SizeLimiter(ulong limit)
    {
        _sizeLimit = limit;
    }

    /// <summary>Adds an entry to this data structure. If this file is already contained, it will be updated accordingly.</summary>
    public void Add(string file, ulong size, DateTime accessed)
    {
        _inUse += size;
        Push(file, accessed);
        if (_sizes.TryGetValue(file, out var oldSize))
        {
            // It's important that decreasing happens after
            // increasing the size, to prevent an overflow.
            _inUse -= oldSize;
        }

        _sizes[file] = size;
    }

    /// <summary>Returns true if the limit is exceeded.</summary>
    public bool ExceedsLimit() => _inUse > _sizeLimit;

    /// <summary>
    /// Returns the least recently accessed file if the size of the cache exceeds the limit.
    /// The entry is removed from the data structure, but the caller is responsible to delete the file in the file system.
    /// </summary>
    public string? Pop()
    {
        if (!ExceedsLimit())
        {
            return null;
        }

        if (_queue.Count == 0)
        {
            Log.LogError("in_use was > 0, so the queue should have contained an item.");
            return null;
        }

        var (_, next) = _queue.Min;
        _queue.Remove(_queue.Min);
        _priorities.Remove(next);

        if (_sizes.Remove(next, out var size))
        {
            _inUse -= size;
        }
        else
        {
            Log.LogError("`queue` and `sizes` should have the same keys.");
        }

        return next;
    }

    /// <summary>Updates the timestamp of an existing element. Returns <c>true</c> if the item did exist.</summary>
    public bool Update(string file, DateTime accessTime)
    {
        if (!_priorities.TryGetValue(file, out var old))
        {
            return false;
        }

        _queue.Remove((old, file));
        _queue.Add((accessTime, file));
        _priorities[file] = accessTime;
        return true;
    }

    /// <summary>Removes an element with the specified path. Returns <c>true</c> if the item did exist.</summary>
    public bool Remove(string file)
    {
        if (!_priorities.Remove(file, out var old))
        {
            return false;
        }

        _queue.Remove((old, file));

        if (_sizes.Remove(file, out var size))
        {
            _inUse -= size;
        }
        else
        {
            Log.LogError("`queue` and `sizes` should have the same keys.");
        }

        return true;
    }

    /// <summary>priority_queue's <c>push</c>: inserts, or changes the priority of an existing item.</summary>
    private void Push(string file, DateTime accessed)
    {
        if (_priorities.TryGetValue(file, out var old))
        {
            _queue.Remove((old, file));
        }

        _queue.Add((accessed, file));
        _priorities[file] = accessed;
    }
}

/// <summary>Upstream <c>FsSizeLimiter</c>: a <see cref="SizeLimiter"/> over a directory tree, behind a lock.</summary>
internal sealed class FsSizeLimiter
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Cache>();

    private readonly object _lock = new();
    private readonly SizeLimiter _limiter;

    private FsSizeLimiter(SizeLimiter limiter)
    {
        _limiter = limiter;
    }

    /// <summary>Returns access time and file size of a given path.</summary>
    private static (DateTime AccessTime, ulong Size) GetMetadata(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException($"No such file: {file.FullName}", file.FullName);
        }

        // Upstream takes the first available of access, modification and creation time, else now. .NET always reports
        // the access time (falling back internally on file systems that do not track it).
        var accessTime = file.LastAccessTimeUtc;
        if (accessTime == DateTime.FromFileTimeUtc(0))
        {
            accessTime = file.LastWriteTimeUtc != DateTime.FromFileTimeUtc(0) ? file.LastWriteTimeUtc
                : file.CreationTimeUtc != DateTime.FromFileTimeUtc(0) ? file.CreationTimeUtc
                : DateTime.UtcNow;
        }

        return (accessTime, (ulong)file.Length);
    }

    /// <summary>Recursively search a directory for files and add them to the <c>limiter</c> struct.</summary>
    private static void InitDir(SizeLimiter limiter, string path)
    {
        IEnumerator<FileSystemInfo> listDir;
        try
        {
            listDir = new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.LogWarning("Could not read directory \"{Path}\" in cache dir: {Error}", path, e.Message);
            return;
        }

        using (listDir)
        {
            while (true)
            {
                FileSystemInfo entry;
                try
                {
                    if (!listDir.MoveNext())
                    {
                        break;
                    }

                    entry = listDir.Current;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    Log.LogWarning("Could not directory \"{Path}\" in cache dir: {Error}", path, e.Message);
                    return;
                }

                switch (entry)
                {
                    case DirectoryInfo:
                    case { LinkTarget: not null }:
                        InitDir(limiter, entry.FullName);
                        break;
                    case FileInfo file:
                        try
                        {
                            var (accessTime, size) = GetMetadata(file);
                            limiter.Add(file.FullName, size, accessTime);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            Log.LogWarning("Could not read file \"{Path}\" in cache dir: {Error}", file.FullName, e.Message);
                        }

                        break;
                    default:
                        Log.LogWarning(
                            "File \"{Path}\" in cache dir has unsupported type {Type}",
                            entry.FullName,
                            entry.Attributes);
                        break;
                }
            }
        }
    }

    public void Add(string file, ulong size)
    {
        lock (_lock)
        {
            _limiter.Add(file, size, DateTime.UtcNow);
        }
    }

    public bool Touch(string file)
    {
        lock (_lock)
        {
            return _limiter.Update(file, DateTime.UtcNow);
        }
    }

    public bool Remove(string file)
    {
        lock (_lock)
        {
            return _limiter.Remove(file);
        }
    }

    private static void PruneInternal(Func<string?> pop)
    {
        var first = true;
        var count = 0;
        Exception? lastError = null;

        while (pop() is { } file)
        {
            if (first)
            {
                Log.LogDebug("Cache dir exceeds limit, removing least recently used files.");
                first = false;
            }

            try
            {
                RemoveFile(file);
                count++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.LogWarning("Could not remove file \"{Path}\" from cache dir: {Error}", file, e.Message);
                lastError = e;
            }
        }

        if (count > 0)
        {
            Log.LogInformation("Removed {Count} cache files.", count);
        }

        if (lastError is not null)
        {
            throw LibrespotException.From(lastError);
        }
    }

    public void Prune() => PruneInternal(() =>
    {
        lock (_lock)
        {
            return _limiter.Pop();
        }
    });

    public static FsSizeLimiter Create(string path, ulong limit)
    {
        var limiter = new SizeLimiter(limit);

        InitDir(limiter, path);
        PruneInternal(limiter.Pop);

        return new FsSizeLimiter(limiter);
    }

    /// <summary><c>fs::remove_file</c>: unlike <see cref="File.Delete"/>, a missing file is an error.</summary>
    internal static void RemoveFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No such file: {path}", path);
        }

        File.Delete(path);
    }
}

/// <summary>A cache for volume, credentials and audio files (upstream <c>Cache</c>).</summary>
public sealed class Cache
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Cache>();

    private readonly string? _credentialsLocation;
    private readonly string? _volumeLocation;
    private readonly string? _audioLocation;
    private readonly FsSizeLimiter? _sizeLimiter;

    /// <summary>
    /// Creates the configured directories and, when <paramref name="sizeLimit"/> is set, indexes the audio cache and
    /// prunes it down to the limit.
    /// </summary>
    /// <param name="credentialsPath">Directory holding <c>credentials.json</c>, or null to disable.</param>
    /// <param name="volumePath">Directory holding <c>volume</c>, or null to disable.</param>
    /// <param name="audioPath">Directory holding the audio files, or null to disable.</param>
    /// <param name="sizeLimit">Maximum total size of cached audio files, or null for unlimited.</param>
    /// <exception cref="LibrespotException">A directory could not be created, or pruning failed.</exception>
    public Cache(string? credentialsPath, string? volumePath, string? audioPath, ulong? sizeLimit)
    {
        try
        {
            if (credentialsPath is not null)
            {
                Directory.CreateDirectory(credentialsPath);
                _credentialsLocation = Path.Combine(credentialsPath, "credentials.json");
            }

            if (volumePath is not null)
            {
                Directory.CreateDirectory(volumePath);
                _volumeLocation = Path.Combine(volumePath, "volume");
            }

            if (audioPath is not null)
            {
                Directory.CreateDirectory(audioPath);

                if (sizeLimit is { } limit)
                {
                    _sizeLimiter = FsSizeLimiter.Create(audioPath, limit);
                }

                _audioLocation = audioPath;
            }
        }
        catch (Exception e) when (e is not LibrespotException)
        {
            throw LibrespotException.From(e);
        }
    }

    /// <summary>Upstream <c>credentials()</c>: the cached credentials, or null when absent or unreadable.</summary>
    public Credentials? Credentials()
    {
        if (_credentialsLocation is not { } location)
        {
            return null;
        }

        try
        {
            using var file = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!OperatingSystem.IsWindows() && (System.IO.File.GetUnixFileMode(file.SafeFileHandle) & UnixFileMode.OtherRead) != 0)
            {
                Log.LogWarning(
                    "credential file \"{Location}\" is currently world readable, consider using  chmod 600 \"{Location}\" to fix this",
                    location,
                    location);
            }

            return JsonSerializer.Deserialize<Credentials>(file) ?? throw new JsonException("invalid type: null, expected struct Credentials");
        }
        // FormatException: the converter's base64 decode of auth_data (a serde error upstream).
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or FormatException)
        {
            // If the file did not exist, the file was probably not written
            // before. Otherwise, log the error.
            var error = LibrespotException.From(e);
            if (error.Kind != ErrorKind.NotFound)
            {
                Log.LogWarning("Error reading credentials from cache: {Error}", error.Message);
            }

            return null;
        }
    }

    /// <summary>Upstream <c>save_credentials(cred)</c>: writes <c>credentials.json</c> (mode 0600 on Unix); failures are logged.</summary>
    public void SaveCredentials(Credentials credentials)
    {
        if (_credentialsLocation is not { } location)
        {
            return;
        }

        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var file = new FileStream(location, options);
            JsonSerializer.Serialize(file, credentials);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.LogWarning("Cannot save credentials to cache: {Error}", LibrespotException.From(e).Message);
        }
    }

    /// <summary>Upstream <c>volume()</c>: the cached volume, or null when absent or unreadable.</summary>
    public ushort? Volume()
    {
        if (_volumeLocation is not { } location)
        {
            return null;
        }

        try
        {
            var contents = System.IO.File.ReadAllText(location);

            // Rust's u16::from_str: optional '+', then ASCII digits only.
            var digits = contents.StartsWith('+') ? contents[1..] : contents;
            return ushort.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            var error = LibrespotException.From(e);
            if (error.Kind != ErrorKind.NotFound)
            {
                Log.LogWarning("Error reading volume from cache: {Error}", error.Message);
            }

            return null;
        }
    }

    /// <summary>Upstream <c>save_volume(volume)</c>; failures are logged.</summary>
    public void SaveVolume(ushort volume)
    {
        if (_volumeLocation is not { } location)
        {
            return;
        }

        try
        {
            System.IO.File.WriteAllText(location, volume.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning("Cannot save volume to cache: {Error}", LibrespotException.From(e).Message);
        }
    }

    /// <summary>Upstream <c>file_path(file)</c>: <c>&lt;audio dir&gt;/&lt;first 2 hex digits&gt;/&lt;remaining hex digits&gt;</c>.</summary>
    public string? FilePath(FileId file)
    {
        if (_audioLocation is not { } location)
        {
            return null;
        }

        var name = file.ToBase16();
        return Path.Combine(location, name[..2], name[2..]);
    }

    /// <summary>
    /// Upstream <c>file(file)</c>: opens a cached audio file for reading (and marks it as recently used), or null when
    /// it is not cached.
    /// </summary>
    public FileStream? File(FileId file)
    {
        if (FilePath(file) is not { } path)
        {
            return null;
        }

        FileStream stream;
        try
        {
            // FileShare.Delete: like a Unix file handle, an open file does not block pruning or removal.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (e is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Log.LogWarning("Error reading file from cache: {Error}", LibrespotException.From(e).Message);
            }

            return null;
        }

        if (_sizeLimiter is { } limiter && !limiter.Touch(path))
        {
            Log.LogError("limiter could not touch \"{Path}\"", path);
        }

        return stream;
    }

    /// <summary>
    /// Upstream <c>save_file(file, contents)</c>: copies <paramref name="contents"/> into the cache and prunes it to
    /// the size limit. Returns the path of the cached file.
    /// </summary>
    /// <exception cref="CacheException">No audio cache is configured, or the file could not be written.</exception>
    /// <exception cref="LibrespotException">Pruning failed.</exception>
    public string SaveFile(FileId file, Stream contents)
    {
        if (FilePath(file) is { } path && Path.GetDirectoryName(path) is { } parent)
        {
            long? size = null;
            try
            {
                Directory.CreateDirectory(parent);
                using var target = new FileStream(path, FileMode.Create, FileAccess.Write);
                contents.CopyTo(target);
                size = target.Position;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Upstream ignores the I/O error here and reports CacheError::Path below.
            }

            if (size is { } written)
            {
                if (_sizeLimiter is { } limiter)
                {
                    limiter.Add(path, (ulong)written);
                    limiter.Prune();
                }

                return path;
            }
        }

        throw new CacheException();
    }

    /// <summary>Upstream <c>remove_file(file)</c>: deletes a cached audio file.</summary>
    /// <exception cref="CacheException">No audio cache is configured.</exception>
    /// <exception cref="LibrespotException">The file does not exist or could not be deleted.</exception>
    public void RemoveFile(FileId file)
    {
        var path = FilePath(file) ?? throw new CacheException();

        try
        {
            FsSizeLimiter.RemoveFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw LibrespotException.From(e);
        }

        _sizeLimiter?.Remove(path);
    }
}
