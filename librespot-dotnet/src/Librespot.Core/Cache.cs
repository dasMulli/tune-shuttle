// Port of core/src/cache.rs.
// TODO(port: core-session): credentials.json + volume files, audio file cache with SizeLimiter (LRU by mtime,
// PriorityQueue<PathBuf, Reverse<SystemTime>> -> PriorityQueue<string, DateTime>), unix permission bits.
// Port the #[cfg(test)] tests for SizeLimiter.
namespace Librespot.Core;

/// <summary>Upstream <c>CacheError::Path</c> ("audio cache location is not configured"), kind FailedPrecondition.</summary>
public sealed class CacheException() : LibrespotException(ErrorKind.FailedPrecondition, "audio cache location is not configured");

/// <summary>On-disk cache for credentials, volume and audio files.</summary>
public sealed class Cache
{
    /// <param name="credentialsPath">Directory holding <c>credentials.json</c>, or null to disable.</param>
    /// <param name="volumePath">Directory holding <c>volume</c>, or null to disable.</param>
    /// <param name="audioPath">Directory holding <c>files/</c>, or null to disable.</param>
    /// <param name="sizeLimit">Maximum total size of cached audio files, or null for unlimited.</param>
    public Cache(string? credentialsPath, string? volumePath, string? audioPath, ulong? sizeLimit) =>
        throw new NotImplementedException("TODO(port: core-session)");

    public Credentials? Credentials() => throw new NotImplementedException("TODO(port: core-session)");
    public void SaveCredentials(Credentials credentials) => throw new NotImplementedException("TODO(port: core-session)");
    public ushort? Volume() => throw new NotImplementedException("TODO(port: core-session)");
    public void SaveVolume(ushort volume) => throw new NotImplementedException("TODO(port: core-session)");
    public string? FilePath(FileId file) => throw new NotImplementedException("TODO(port: core-session)");
    public FileStream? File(FileId file) => throw new NotImplementedException("TODO(port: core-session)");
    public string SaveFile(FileId file, Stream contents) => throw new NotImplementedException("TODO(port: core-session)");
    public void RemoveFile(FileId file) => throw new NotImplementedException("TODO(port: core-session)");
}
