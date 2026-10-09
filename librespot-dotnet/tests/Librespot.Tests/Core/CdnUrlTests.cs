// Tests for core/src/cdn_url.rs (port of its #[cfg(test)] module plus extra coverage).
using Google.Protobuf;
using Librespot.Core;
using Spotify.Download.Proto;
using Xunit;

namespace Librespot.Tests.Core;

public class CdnUrlTests
{
    private static StorageResolveResponse CdnMessage(bool expiring, params string[] urls)
    {
        var msg = new StorageResolveResponse { Result = StorageResolveResponse.Types.Result.Cdn };
        msg.Cdnurl.Add(urls);
        if (expiring)
        {
            msg.Fileid = ByteString.CopyFrom(0);
        }

        return msg;
    }

    [Fact]
    public void test_maybe_expiring_urls()
    {
        const long timestamp = 1688165560;
        var msg = CdnMessage(
            expiring: true,
            $"https://audio-cf.spotifycdn.com/audio/844ecdb297a87ebfee4399f28892ef85d9ba725f?verify={timestamp}-4R3I2w2q7OfNkR%2FGH8qH7xtIKUPlDxywBuADY%2BsvMeU%3D",
            $"https://audio-ak-spotify-com.akamaized.net/audio/foo?__token__=exp={timestamp}~hmac=4e661527574fab5793adb99cf04e1c2ce12294c71fe1d39ffbfabdcfe8ce3b41",
            $"https://audio-gm-off.spotifycdn.com/audio/foo?Expires={timestamp}~FullPath~hmac=IIZA28qptl8cuGLq15-SjHKHtLoxzpy_6r_JpAU4MfM=",
            $"https://audio4-fa.scdn.co/audio/foo?{timestamp}_0GKSyXjLaTW1BksFOyI4J7Tf9tZDbBUNNPu9Mt4mhH4=",
            "https://audio4-fa.scdn.co/foo?baz");

        var urls = MaybeExpiringUrls.From(msg);
        Assert.Equal(5, urls.Count);
        Assert.NotNull(urls[0].Expiry);
        Assert.NotNull(urls[1].Expiry);
        Assert.NotNull(urls[2].Expiry);
        Assert.NotNull(urls[3].Expiry);
        Assert.Null(urls[4].Expiry);

        var timestampMargin = TimeSpan.FromSeconds(timestamp) - CdnUrl.ExpiryMargin;
        Assert.Equal((long)timestampMargin.TotalMilliseconds, urls[0].Expiry!.Value.AsTimestampMs());

        // every format yields the same expiry
        Assert.All(urls.Take(4), u => Assert.Equal(urls[0].Expiry, u.Expiry));
    }

    [Fact]
    public void Expiry_margin_is_five_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CdnUrl.ExpiryMargin);
    }

    [Fact]
    public void Non_expiring_response_has_no_expiries()
    {
        var urls = MaybeExpiringUrls.From(CdnMessage(expiring: false, "https://audio-cf.spotifycdn.com/audio/x?verify=1688165560-abc"));
        Assert.Single(urls);
        Assert.Null(urls[0].Expiry);
    }

    [Fact]
    public void Akamai_token_without_tilde_uses_the_rest()
    {
        var urls = MaybeExpiringUrls.From(CdnMessage(expiring: true, "https://a.akamaized.net/audio/foo?__token__=exp=1688165560"));
        Assert.Equal((1688165560L - 300) * 1000, urls[0].Expiry!.Value.AsTimestampMs());
    }

    [Fact]
    public void Unparsable_expiry_and_unknown_format_give_no_expiry()
    {
        var urls = MaybeExpiringUrls.From(CdnMessage(
            expiring: true,
            "https://a.spotifycdn.com/audio/foo?verify=abc-def",
            "https://a.spotifycdn.com/audio/foo"));
        Assert.Null(urls[0].Expiry);
        Assert.Null(urls[1].Expiry);
    }

    [Fact]
    public void Non_cdn_storage_is_rejected()
    {
        var msg = new StorageResolveResponse { Result = StorageResolveResponse.Types.Result.Storage };
        var e = Assert.Throws<CdnUrlException>(() => MaybeExpiringUrls.From(msg));
        Assert.Equal(CdnUrlException.Reason.Storage, e.Cause);
        Assert.Equal(ErrorKind.Unavailable, e.Kind);
        Assert.Equal("resolved storage is not for CDN", e.Detail);
    }

    [Fact]
    public void Invalid_url_is_an_error()
    {
        var e = Assert.ThrowsAny<LibrespotException>(() => MaybeExpiringUrls.From(CdnMessage(expiring: true, "not a url")));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
    }

    [Fact]
    public void Try_get_urls_filters_expired_urls()
    {
        var past = Date.From(DateTimeOffset.UtcNow.AddMinutes(-1));
        var future = Date.From(DateTimeOffset.UtcNow.AddHours(1));
        var cdnUrl = new CdnUrl(default, new MaybeExpiringUrls(
        [
            new MaybeExpiringUrl("https://expired/", past),
            new MaybeExpiringUrl("https://valid/", future),
            new MaybeExpiringUrl("https://forever/", null),
        ]));

        Assert.Equal(["https://valid/", "https://forever/"], cdnUrl.TryGetUrls());
#pragma warning disable CS0618 // deprecated upstream as well
        Assert.Equal("https://valid/", cdnUrl.TryGetUrl());
#pragma warning restore CS0618
    }

    [Fact]
    public void Try_get_urls_errors()
    {
        var unresolved = Assert.Throws<CdnUrlException>(() => new CdnUrl(default).TryGetUrls());
        Assert.Equal(CdnUrlException.Reason.Unresolved, unresolved.Cause);
        Assert.Equal(ErrorKind.Unavailable, unresolved.Kind);
        Assert.Equal("no URLs resolved", unresolved.Detail);

        var past = Date.From(DateTimeOffset.UtcNow.AddMinutes(-1));
        var expired = Assert.Throws<CdnUrlException>(() =>
            new CdnUrl(default, new MaybeExpiringUrls([new MaybeExpiringUrl("https://expired/", past)])).TryGetUrls());
        Assert.Equal(CdnUrlException.Reason.Expired, expired.Cause);
        Assert.Equal(ErrorKind.DeadlineExceeded, expired.Kind);
        Assert.Equal("all URLs expired", expired.Detail);
    }

    [Fact]
    public void Query_pairs_decode_like_form_urlencoded()
    {
        var pairs = MaybeExpiringUrls.QueryPairs("a=1+2&&b=%2F%3d&c&d=x=y&e=%zz");
        Assert.Equal(
            [new("a", "1 2"), new("b", "/="), new("c", ""), new("d", "x=y"), new("e", "%zz")],
            pairs);
        Assert.Empty(MaybeExpiringUrls.QueryPairs(null));
    }
}
