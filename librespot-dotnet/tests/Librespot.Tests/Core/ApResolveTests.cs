using System.Net.Http;
using System.Text;
using System.Text.Json;
using Librespot.Core;
using Xunit;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace Librespot.Tests.Core;

public class ApResolveTests
{
    [Fact]
    public void Process_ap_strings_keeps_order_and_drops_malformed()
    {
        var result = ApResolver.ProcessApStrings(
            ["ap-gew4.spotify.com:4070", "ap-gew4.spotify.com:443", "no-port", "bad:port", "ap-gew4.spotify.com:80", "[::1]:443"],
            null);

        Assert.Equal(
            [
                new SocketAddress("ap-gew4.spotify.com", 4070),
                new SocketAddress("ap-gew4.spotify.com", 443),
                new SocketAddress("ap-gew4.spotify.com", 80),
                new SocketAddress("[::1]", 443),
            ],
            result.ToArray());
    }

    [Fact]
    public void Process_ap_strings_filters_by_port()
    {
        var result = ApResolver.ProcessApStrings(["a:4070", "b:443", "c:80", "d:443"], 443);
        Assert.Equal([new SocketAddress("b", 443), new SocketAddress("d", 443)], result.ToArray());
    }

    [Fact]
    public void Resolve_data_parses_apresolve_json()
    {
        const string json = """
            {"accesspoint":["ap-gue1.spotify.com:4070"],"dealer":["gue1-dealer.spotify.com:443"],"spclient":["gue1-spclient.spotify.com:443"]}
            """;
        var data = JsonSerializer.Deserialize<ApResolveData>(json)!;
        Assert.Equal(["ap-gue1.spotify.com:4070"], data.AccessPoint);
        Assert.Equal(["gue1-dealer.spotify.com:443"], data.Dealer);
        Assert.Equal(["gue1-spclient.spotify.com:443"], data.SpClient);
    }

    [Fact]
    public void Resolve_data_requires_all_fields_like_serde()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ApResolveData>("""{"accesspoint":[]}"""));
    }

    [Fact]
    public void Socket_address_formats_as_host_port()
    {
        Assert.Equal("ap.spotify.com:443", new SocketAddress("ap.spotify.com", 443).ToString());
    }

    private const string FullJson = """
        {"accesspoint":["ap-a.spotify.com:4070","ap-b.spotify.com:443","ap-c.spotify.com:80"],"dealer":["dealer-a.spotify.com:443"],"spclient":["spclient-a.spotify.com:443"]}
        """;

    private static (ApResolver Resolver, ScriptedHandler Handler) Resolver(SessionConfig config, params Func<HttpResponseMessage>[] responses)
    {
        var handler = new ScriptedHandler(responses);
        var http = new Librespot.Core.HttpClient(null, TimeProvider.System, handler);
        return (new ApResolver(new Session(config, null), http), handler);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Resolve_pops_addresses_in_order()
    {
        var (resolver, handler) = Resolver(SessionConfig.Default(), () => Json(FullJson));

        Assert.Equal(new SocketAddress("ap-a.spotify.com", 4070), await resolver.ResolveAsync("accesspoint"));
        Assert.Equal(new SocketAddress("ap-b.spotify.com", 443), await resolver.ResolveAsync("accesspoint"));
        Assert.Equal(new SocketAddress("dealer-a.spotify.com", 443), await resolver.ResolveAsync("dealer"));
        Assert.Equal(1, handler.Calls);

        // The dealer queue is now empty, so the next resolve queries apresolve again and starts over.
        Assert.Equal(new SocketAddress("ap-a.spotify.com", 4070), await resolver.ResolveAsync("accesspoint"));
        Assert.Equal(2, handler.Calls);
        Assert.Equal("https://apresolve.spotify.com/?type=accesspoint&type=dealer&type=spclient", handler.LastUri?.ToString());
    }

    [Fact]
    public async Task Resolve_appends_fallbacks_when_a_list_is_empty()
    {
        var (resolver, _) = Resolver(SessionConfig.Default(), () => Json("""{"accesspoint":[],"dealer":[],"spclient":[]}"""));

        Assert.Equal(new SocketAddress("ap.spotify.com", 443), await resolver.ResolveAsync("accesspoint"));
        Assert.Equal(new SocketAddress("dealer.spotify.com", 443), await resolver.ResolveAsync("dealer"));
        Assert.Equal(new SocketAddress("spclient.wg.spotify.com", 443), await resolver.ResolveAsync("spclient"));
    }

    [Fact]
    public async Task Resolve_appends_fallbacks_after_the_resolved_ones()
    {
        var (resolver, _) = Resolver(
            SessionConfig.Default(),
            () => Json("""{"accesspoint":["ap-a.spotify.com:4070"],"dealer":[],"spclient":["spclient-a.spotify.com:443"]}"""));

        Assert.Equal(new SocketAddress("ap-a.spotify.com", 4070), await resolver.ResolveAsync("accesspoint"));
        Assert.Equal(new SocketAddress("ap.spotify.com", 443), await resolver.ResolveAsync("accesspoint"));
    }

    [Fact]
    public async Task Resolve_uses_fallbacks_when_apresolve_fails()
    {
        var (resolver, _) = Resolver(SessionConfig.Default(), () => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Equal(new SocketAddress("ap.spotify.com", 443), await resolver.ResolveAsync("accesspoint"));
        Assert.Equal(new SocketAddress("spclient.wg.spotify.com", 443), await resolver.ResolveAsync("spclient"));
    }

    [Fact]
    public async Task Resolve_unknown_endpoint_is_unimplemented()
    {
        var (resolver, _) = Resolver(SessionConfig.Default(), () => Json(FullJson));

        var error = await Assert.ThrowsAsync<LibrespotException>(() => resolver.ResolveAsync("nope"));
        Assert.Equal(ErrorKind.Unimplemented, error.Kind);
    }

    [Fact]
    public async Task Resolve_is_unavailable_when_the_port_filter_drops_everything()
    {
        // ApPort 4070 filters out every address, the 443 fallbacks included.
        var config = SessionConfig.Default() with { ApPort = 4070 };
        var (resolver, _) = Resolver(config, () => Json("""{"accesspoint":["a:443"],"dealer":["d:443"],"spclient":["s:443"]}"""));

        var error = await Assert.ThrowsAsync<LibrespotException>(() => resolver.ResolveAsync("accesspoint"));
        Assert.Equal(ErrorKind.Unavailable, error.Kind);
    }

    private sealed class ScriptedHandler(Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _next;

        public int Calls => _next;

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            var response = responses[Math.Min(_next++, responses.Length - 1)]();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    [Fact(Skip = "network")]
    public async Task Resolve_accesspoint_returns_host_and_port()
    {
        var session = new Session(SessionConfig.Default(), null);
        var ap = await session.ApResolver.ResolveAsync("accesspoint");
        Assert.False(string.IsNullOrEmpty(ap.Host));
        Assert.NotEqual(0, ap.Port);
    }
}
