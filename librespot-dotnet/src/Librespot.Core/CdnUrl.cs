// Port of core/src/cdn_url.rs.
using System.Globalization;
using System.Text;
using Google.Protobuf;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using CdnUrlMessage = Spotify.Download.Proto.StorageResolveResponse;
using StorageResolveResult = Spotify.Download.Proto.StorageResolveResponse.Types.Result;

namespace Librespot.Core;

/// <summary>Upstream <c>MaybeExpiringUrl(String, Option&lt;Date&gt;)</c>: a CDN URL and, if known, when it expires (minus a safety margin).</summary>
/// <param name="Url">The CDN URL.</param>
/// <param name="Expiry">The expiry parsed from the URL's token, already reduced by <see cref="CdnUrl.ExpiryMargin"/>; null if unknown.</param>
public sealed record MaybeExpiringUrl(string Url, Date? Expiry);

/// <summary>Upstream <c>MaybeExpiringUrls(Vec&lt;MaybeExpiringUrl&gt;)</c> (the <c>Deref</c> wrapper becomes a list).</summary>
public sealed class MaybeExpiringUrls : List<MaybeExpiringUrl>
{
    /// <summary>An empty list.</summary>
    public MaybeExpiringUrls()
    {
    }

    /// <summary>A list with the given URLs.</summary>
    public MaybeExpiringUrls(IEnumerable<MaybeExpiringUrl> urls) : base(urls)
    {
    }

    /// <summary>
    /// Upstream <c>TryFrom&lt;CdnUrlMessage&gt; for MaybeExpiringUrls</c>: takes the CDN URLs of a storage-resolve
    /// response and, when the response names a file id (expiring URLs), extracts each URL's expiry from its query.
    /// </summary>
    /// <exception cref="CdnUrlException">The resolved storage is not a CDN (<see cref="CdnUrlException.Reason.Storage"/>).</exception>
    /// <exception cref="LibrespotException">A URL could not be parsed (kind FailedPrecondition, upstream <c>url::ParseError</c>).</exception>
    public static MaybeExpiringUrls From(CdnUrlMessage msg)
    {
        if (msg.Result != StorageResolveResult.Cdn)
        {
            throw CdnUrlException.Storage();
        }

        var isExpiring = !msg.Fileid.IsEmpty;

        var result = new MaybeExpiringUrls();
        foreach (var cdnUrl in msg.Cdnurl)
        {
            if (!Uri.TryCreate(cdnUrl, UriKind.Absolute, out _))
            {
                throw LibrespotException.FailedPrecondition($"invalid URL: {cdnUrl}");
            }

            Date? expiry = null;

            if (isExpiring)
            {
                string? expiryStr = null;
                var rawQuery = RawQuery(cdnUrl);
                var pairs = QueryPairs(rawQuery);

                if (Find(pairs, "verify") is { } verify)
                {
                    // https://audio-cf.spotifycdn.com/audio/844ecdb297a87ebfee4399f28892ef85d9ba725f?verify=1750549951-4R3I2w2q7OfNkR%2FGH8qH7xtIKUPlDxywBuADY%2BsvMeU%3D
                    var dash = verify.IndexOf('-');
                    if (dash >= 0)
                    {
                        expiryStr = verify[..dash];
                    }
                }
                else if (Find(pairs, "__token__") is { } token)
                {
                    // "https://audio-ak-spotify-com.akamaized.net/audio/4712bc9e47f7feb4ee3450ef2bb545e1d83c3d54?__token__=exp=1688165560~hmac=4e661527574fab5793adb99cf04e1c2ce12294c71fe1d39ffbfabdcfe8ce3b41",
                    var start = token.IndexOf("exp=", StringComparison.Ordinal);
                    if (start >= 0)
                    {
                        start += 4;
                        if (token.Length >= start)
                        {
                            var slice = token[start..];
                            var end = slice.IndexOf('~');
                            // this is the only valid invariant for akamaized.net
                            expiryStr = end >= 0 ? slice[..end] : slice;
                        }
                    }
                }
                else if (Find(pairs, "Expires") is { } expires)
                {
                    // "https://audio-gm-off.spotifycdn.com/audio/4712bc9e47f7feb4ee3450ef2bb545e1d83c3d54?Expires=1688165560~FullPath~hmac=IIZA28qptl8cuGLq15-SjHKHtLoxzpy_6r_JpAU4MfM=",
                    var end = expires.IndexOf('~');
                    if (end >= 0)
                    {
                        // this is the only valid invariant for spotifycdn.com
                        expiryStr = expires[..end];
                    }
                }
                else if (rawQuery is not null)
                {
                    // "https://audio4-fa.scdn.co/audio/4712bc9e47f7feb4ee3450ef2bb545e1d83c3d54?1688165560_0GKSyXjLaTW1BksFOyI4J7Tf9tZDbBUNNPu9Mt4mhH4=",
                    // this is the only valid invariant for scdn.co
                    var underscore = rawQuery.IndexOf('_');
                    expiryStr = underscore >= 0 ? rawQuery[..underscore] : rawQuery;
                }

                if (expiryStr is not null)
                {
                    if (long.TryParse(expiryStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var expiryParsed))
                    {
                        expiry = ExpiryWithMargin(expiryParsed);
                    }
                    else
                    {
                        CdnUrl.Log.LogWarning("Cannot parse CDN URL expiry timestamp '{ExpiryStr}' from '{CdnUrl}'", expiryStr, cdnUrl);
                    }
                }
                else
                {
                    CdnUrl.Log.LogWarning("Unknown CDN URL format: {CdnUrl}", cdnUrl);
                }
            }

            result.Add(new MaybeExpiringUrl(cdnUrl, expiry));
        }

        return result;
    }

    /// <summary>
    /// <c>Date::from_timestamp_ms(expiry * 1000)</c> minus <see cref="CdnUrl.ExpiryMargin"/> (saturating); null when the
    /// timestamp is out of range (upstream ignores that error).
    /// </summary>
    private static Date? ExpiryWithMargin(long expirySeconds)
    {
        long ms;
        try
        {
            ms = checked(expirySeconds * 1000);
        }
        catch (OverflowException)
        {
            return null;
        }

        Date expiryAt;
        try
        {
            expiryAt = Date.FromTimestampMs(ms);
        }
        catch (LibrespotException)
        {
            return null;
        }

        var withMargin = expiryAt.Value - DateTimeOffset.MinValue < CdnUrl.ExpiryMargin
            ? DateTimeOffset.MinValue
            : expiryAt.Value - CdnUrl.ExpiryMargin;
        return Date.From(withMargin);
    }

    /// <summary>
    /// The query of an absolute URL as <c>url::Url::query()</c> returns it: everything after the first <c>?</c> up to
    /// the fragment, still percent-encoded; null when there is no <c>?</c>.
    /// </summary>
    private static string? RawQuery(string url)
    {
        var fragment = url.IndexOf('#');
        var withoutFragment = fragment >= 0 ? url[..fragment] : url;
        var question = withoutFragment.IndexOf('?');
        return question >= 0 ? withoutFragment[(question + 1)..] : null;
    }

    /// <summary>
    /// <c>url::Url::query_pairs()</c>: <c>application/x-www-form-urlencoded</c> parsing (split on <c>&amp;</c>, skip
    /// empty sequences, split at the first <c>=</c>, <c>+</c> is a space, percent-decode, lossy UTF-8).
    /// </summary>
    internal static List<KeyValuePair<string, string>> QueryPairs(string? query)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (query is null)
        {
            return pairs;
        }

        foreach (var sequence in query.Split('&'))
        {
            if (sequence.Length == 0)
            {
                continue;
            }

            var eq = sequence.IndexOf('=');
            var name = eq >= 0 ? sequence[..eq] : sequence;
            var value = eq >= 0 ? sequence[(eq + 1)..] : "";
            pairs.Add(new(FormDecode(name), FormDecode(value)));
        }

        return pairs;
    }

    private static string? Find(List<KeyValuePair<string, string>> pairs, string key)
    {
        foreach (var (k, v) in pairs)
        {
            if (k == key)
            {
                return v;
            }
        }

        return null;
    }

    private static string FormDecode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        // the decoded form is never longer than the input
        var output = new byte[bytes.Length];
        var written = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == (byte)'+')
            {
                output[written++] = (byte)' ';
            }
            else if (b == (byte)'%' && i + 2 < bytes.Length && IsHex(bytes[i + 1]) && IsHex(bytes[i + 2]))
            {
                output[written++] = (byte)((HexValue(bytes[i + 1]) << 4) | HexValue(bytes[i + 2]));
                i += 2;
            }
            else
            {
                output[written++] = b;
            }
        }

        return Encoding.UTF8.GetString(output, 0, written);
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => b - 'A' + 10,
    };
}

/// <summary>Upstream <c>CdnUrlError</c>.</summary>
public sealed class CdnUrlException : LibrespotException
{
    /// <summary>The upstream variant.</summary>
    public enum Reason
    {
        /// <summary>"all URLs expired" (kind DeadlineExceeded).</summary>
        Expired,
        /// <summary>"resolved storage is not for CDN" (kind Unavailable).</summary>
        Storage,
        /// <summary>"no URLs resolved" (kind Unavailable).</summary>
        Unresolved,
    }

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    /// <summary>Creates the error; the kind follows upstream <c>impl From&lt;CdnUrlError&gt; for Error</c>.</summary>
    public CdnUrlException(Reason cause, string detail) : base(KindFor(cause), detail)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>CdnUrlError::Expired</c>.</summary>
    public static CdnUrlException Expired() => new(Reason.Expired, "all URLs expired");

    /// <summary>Upstream <c>CdnUrlError::Storage</c>.</summary>
    public static CdnUrlException Storage() => new(Reason.Storage, "resolved storage is not for CDN");

    /// <summary>Upstream <c>CdnUrlError::Unresolved</c>.</summary>
    public static CdnUrlException Unresolved() => new(Reason.Unresolved, "no URLs resolved");

    private static ErrorKind KindFor(Reason cause) => cause switch
    {
        Reason.Expired => ErrorKind.DeadlineExceeded,
        _ => ErrorKind.Unavailable,
    };
}

/// <summary>Resolved CDN download URLs for one audio file (upstream <c>CdnUrl</c>).</summary>
public sealed class CdnUrl
{
    /// <summary>Logger shared with <see cref="MaybeExpiringUrls"/> (upstream logs both from the <c>cdn_url</c> module).</summary>
    internal static readonly ILogger Log = LibrespotLog.CreateLogger<CdnUrl>();

    /// <summary>URLs are treated as expired this long before their token expires (upstream <c>CDN_URL_EXPIRY_MARGIN</c>).</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(5 * 60);

    /// <summary>Upstream <c>CdnUrl::new(file_id)</c>: no URLs resolved yet.</summary>
    public CdnUrl(FileId fileId)
        : this(fileId, new MaybeExpiringUrls())
    {
    }

    internal CdnUrl(FileId fileId, MaybeExpiringUrls urls)
    {
        FileId = fileId;
        Urls = urls;
    }

    /// <summary>The audio file these URLs serve.</summary>
    public FileId FileId { get; }

    /// <summary>The resolved URLs (upstream private field <c>urls</c>).</summary>
    public IReadOnlyList<MaybeExpiringUrl> Urls { get; }

    /// <summary>
    /// Upstream <c>resolve_audio(session)</c>: asks spclient's storage-resolve for <see cref="FileId"/> and returns a
    /// new <see cref="CdnUrl"/> holding the CDN URLs.
    /// </summary>
    /// <exception cref="CdnUrlException">The storage is not a CDN.</exception>
    /// <exception cref="LibrespotException">The request failed or the response could not be parsed.</exception>
    public async Task<CdnUrl> ResolveAudioAsync(Session session, CancellationToken cancellationToken = default)
    {
        var fileId = FileId;
        var response = await session.SpClient.GetAudioStorageAsync(fileId, cancellationToken).ConfigureAwait(false);

        CdnUrlMessage msg;
        try
        {
            msg = CdnUrlMessage.Parser.ParseFrom(response);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw LibrespotException.From(e);
        }

        var urls = MaybeExpiringUrls.From(msg);
        var cdnUrl = new CdnUrl(fileId, urls);

        Log.LogTrace("Resolved CDN storage: {CdnUrl}", cdnUrl);

        return cdnUrl;
    }

    /// <summary>Upstream <c>try_get_url</c>: the first URL that has not expired.</summary>
    /// <exception cref="CdnUrlException">No URLs resolved, or all expired.</exception>
    [Obsolete("This function only returns the first valid URL. Use TryGetUrls instead, which allows for fallback logic.")]
    public string TryGetUrl()
    {
        if (Urls.Count == 0)
        {
            throw CdnUrlException.Unresolved();
        }

        var now = Date.NowUtc();
        foreach (var url in Urls)
        {
            if (url.Expiry is not { } expiry || now.CompareTo(expiry) < 0)
            {
                return url.Url;
            }
        }

        throw CdnUrlException.Expired();
    }

    /// <summary>Upstream <c>try_get_urls</c>: all URLs that have not expired, in order.</summary>
    /// <exception cref="CdnUrlException">No URLs resolved (<see cref="CdnUrlException.Reason.Unresolved"/>), or all
    /// expired (<see cref="CdnUrlException.Reason.Expired"/>).</exception>
    public IReadOnlyList<string> TryGetUrls()
    {
        if (Urls.Count == 0)
        {
            throw CdnUrlException.Unresolved();
        }

        var now = Date.NowUtc();
        var urls = new List<string>();
        foreach (var (url, expiry) in Urls)
        {
            if (expiry is not { } e || now.CompareTo(e) < 0)
            {
                urls.Add(url);
            }
        }

        if (urls.Count == 0)
        {
            throw CdnUrlException.Expired();
        }

        return urls;
    }

    /// <summary>Debug rendering (upstream <c>#[derive(Debug)]</c>).</summary>
    public override string ToString() =>
        $"CdnUrl {{ file_id: {FileId}, urls: [{string.Join(", ", Urls.Select(u => $"({u.Url}, {u.Expiry?.Value.ToString("O", CultureInfo.InvariantCulture) ?? "None"})"))}] }}";
}
