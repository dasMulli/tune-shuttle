using Librespot.Metadata.Audio;

namespace Bridge.Service.Spotify;

/// <summary>Fetches a track's cover for the receivers (SPEC-control.md 5.3: <c>image/jpeg</c> or <c>image/png</c> bytes).</summary>
public interface IArtworkLoader
{
    /// <summary>The cover URL the bridge uses for an item (also shown by the web app), or null.</summary>
    string? UrlOf(AudioItem item);

    /// <summary>Downloads the cover; null when there is none or it could not be loaded.</summary>
    Task<Artwork?> LoadAsync(AudioItem item, CancellationToken cancellationToken);
}

/// <summary>A downloaded image.</summary>
/// <param name="Data">The bytes.</param>
/// <param name="ContentType">The MIME type.</param>
public sealed record Artwork(byte[] Data, string ContentType);

/// <summary>
/// Downloads covers from Spotify's image CDN over HTTPS. Picks the image closest to 600 px wide (receivers show small
/// covers; the largest Spotify size is 640 px), at most <see cref="MaxBytes"/>, bounded by <see cref="Timeout"/>.
/// </summary>
public sealed class HttpArtworkLoader : IArtworkLoader, IDisposable
{
    /// <summary>Largest image accepted (receivers have small buffers for <c>SET_PARAMETER</c> bodies).</summary>
    public const int MaxBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>Creates the loader with its own <see cref="HttpClient"/>.</summary>
    public HttpArtworkLoader(ILogger<HttpArtworkLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout };
    }

    /// <summary>Download timeout.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string? UrlOf(AudioItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Covers
            .Where(c => !string.IsNullOrEmpty(c.Url))
            .OrderBy(c => c.Width <= 0 ? int.MaxValue : Math.Abs(c.Width - 600))
            .Select(c => c.Url)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<Artwork?> LoadAsync(AudioItem item, CancellationToken cancellationToken)
    {
        var url = UrlOf(item);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                _logger.LogDebug("Cover {Url} is too large ({Length} bytes)", url, response.Content.Headers.ContentLength);
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (data.Length is 0 or > MaxBytes)
            {
                return null;
            }

            var type = response.Content.Headers.ContentType?.MediaType;
            type = type is "image/png" ? "image/png" : SniffType(data);
            return new Artwork(data, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Cover for {Track} could not be loaded: {Error}", item.Name, ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>PNG by its signature, otherwise JPEG (what Spotify's CDN serves).</summary>
    private static string SniffType(byte[] data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G' ? "image/png" : "image/jpeg";
}
