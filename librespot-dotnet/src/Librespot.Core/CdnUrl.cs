// Port of core/src/cdn_url.rs.
// TODO(port: core-spclient): StorageResolveResponse parsing, expiry extraction from the URL token
// (__token__ / verify-style query params), try_get_url rotation. Port the #[cfg(test)] tests.
namespace Librespot.Core;

/// <summary>Upstream <c>MaybeExpiringUrl(String, Option&lt;Date&gt;)</c>.</summary>
public sealed record MaybeExpiringUrl(string Url, Date? Expiry);

/// <summary>Upstream <c>CdnUrlError</c>.</summary>
public sealed class CdnUrlException : LibrespotException
{
    public enum Reason
    {
        /// <summary>All URLs expired (kind Unavailable).</summary>
        Expired,
        /// <summary>Resolved storage is not for CDN (kind Unavailable).</summary>
        Storage,
        /// <summary>No URLs resolved (kind Unavailable).</summary>
        Unresolved,
    }

    public Reason Cause { get; }

    public CdnUrlException(Reason cause, string detail) : base(ErrorKind.Unavailable, detail)
    {
        Cause = cause;
    }
}

/// <summary>Resolved CDN download URLs for one audio file.</summary>
public sealed class CdnUrl
{
    public CdnUrl(FileId fileId)
    {
        FileId = fileId;
        Urls = [];
    }

    public FileId FileId { get; }
    public IReadOnlyList<MaybeExpiringUrl> Urls { get; private set; }

    /// <summary>Upstream <c>resolve_audio(session)</c>: asks spclient for storage-resolve and fills <see cref="Urls"/>.</summary>
    public Task<CdnUrl> ResolveAudioAsync(Session session, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-spclient)");

    /// <summary>The first non-expired URL.</summary>
    public string TryGetUrl() => throw new NotImplementedException("TODO(port: core-spclient)");

    /// <summary>All non-expired URLs.</summary>
    public IReadOnlyList<string> TryGetUrls() => throw new NotImplementedException("TODO(port: core-spclient)");
}
