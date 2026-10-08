// Tests for src/Librespot.OAuth/OAuth.cs (port of oauth/src/lib.rs, including its `mod test`).
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Librespot.OAuth;
using Xunit;

namespace Librespot.Tests.OAuth;

public class OAuthTests
{
    private const string ClientId = "65b708073fc0480ea92a077233ca87bd";

    // ---- upstream `mod test` ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1/foo")] // No port
    [InlineData("http://127.0.0.1:/foo")]
    [InlineData("http://[::1]/foo")]
    [InlineData("https://127.0.0.1/foo")] // Not http
    public async Task get_socket_address_none(string uri)
    {
        Assert.Null(await OAuthHelpers.GetSocketAddressAsync(uri, CancellationToken.None));
    }

    [Fact]
    public async Task get_socket_address_some()
    {
        var localhostV4 = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234);
        var localhostV6 = new IPEndPoint(IPAddress.IPv6Loopback, 8888);
        var addrV4 = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 1234);
        var addrV6 = new IPEndPoint(IPAddress.Parse("2001:4860:4860::8888"), 8888);

        // Loopback addresses
        Assert.Equal(localhostV4, await OAuthHelpers.GetSocketAddressAsync("http://127.0.0.1:1234/foo", default));
        Assert.Equal(localhostV6, await OAuthHelpers.GetSocketAddressAsync("http://[0:0:0:0:0:0:0:1]:8888/foo", default));
        Assert.Equal(localhostV6, await OAuthHelpers.GetSocketAddressAsync("http://[::1]:8888/foo", default));

        // Non-loopback addresses
        Assert.Equal(addrV4, await OAuthHelpers.GetSocketAddressAsync("http://8.8.8.8:1234/foo", default));
        Assert.Equal(addrV6, await OAuthHelpers.GetSocketAddressAsync("http://[2001:4860:4860::8888]:8888/foo", default));
    }

    [Fact]
    public async Task get_socket_address_explicit_default_port_is_none()
    {
        // url::Url::port() is None for the scheme's default port.
        Assert.Null(await OAuthHelpers.GetSocketAddressAsync("http://127.0.0.1:80/foo", default));
    }

    // ---- get_code ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:8898/login?code=abc&state=xyz", "abc")]
    [InlineData("http://localhost/login?state=xyz&code=AQB-x_9.z", "AQB-x_9.z")]
    [InlineData("http://localhost/?code=a%2Bb+c", "a+b c")]
    [InlineData("http://localhost/?&&code=first&code=second", "first")]
    [InlineData("http://localhost/?code=", "")]
    [InlineData("http://localhost/?code=caf%C3%A9#fragment", "café")]
    public void get_code_finds_code(string url, string expected)
    {
        Assert.Equal(expected, OAuthHelpers.GetCode(url));
    }

    [Fact]
    public void get_code_without_code_param()
    {
        var e = Assert.Throws<OAuthException>(() => OAuthHelpers.GetCode("http://localhost/login?state=xyz&codes=1"));
        Assert.Equal(OAuthErrorKind.AuthCodeNotFound, e.Kind);
        Assert.Equal("Auth code param not found in URI http://localhost/login?state=xyz&codes=1", e.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/login?code=abc")]
    public void get_code_bad_uri(string url)
    {
        var e = Assert.Throws<OAuthException>(() => OAuthHelpers.GetCode(url));
        Assert.Equal(OAuthErrorKind.AuthCodeBadUri, e.Kind);
        Assert.StartsWith($"Unable to parse redirect URI {url} (", e.Message);
    }

    // ---- builders ---------------------------------------------------------------------------------------------------

    [Fact]
    public void builder_defaults()
    {
        using var client = new OAuthClientBuilder(ClientId, "http://127.0.0.1:8898/login", ["streaming"]).Build();
        Assert.Equal(ClientId, client.ClientId);
        Assert.Equal("http://127.0.0.1:8898/login", client.RedirectUri.OriginalString);
        Assert.Equal(["streaming"], client.Scopes);
        Assert.False(client.ShouldOpenUrl);
        Assert.Equal("Go back to your terminal :)", client.Message);
    }

    [Fact]
    public void builder_options()
    {
        using var client = new OAuthClientBuilder(ClientId, "http://127.0.0.1:8898/login", ["a", "b"])
            .OpenInBrowser()
            .WithCustomMessage("<h1>hi</h1>")
            .Build();
        Assert.True(client.ShouldOpenUrl);
        Assert.Equal("<h1>hi</h1>", client.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:8898/login")]
    [InlineData("/login")]
    public void builder_rejects_invalid_redirect_uri(string redirect)
    {
        var e = Assert.Throws<OAuthException>(() => new OAuthClientBuilder(ClientId, redirect, ["streaming"]).Build());
        Assert.Equal(OAuthErrorKind.InvalidRedirectUri, e.Kind);
        Assert.StartsWith($"Invalid Redirect URI {redirect} (", e.Message);
    }

    [Fact]
    public void builder_accepts_non_http_redirect_uri()
    {
        using var client = new OAuthClientBuilder(ClientId, "myapp://callback", ["streaming"]).Build();
        Assert.Equal("myapp://callback", client.RedirectUri.OriginalString);
    }

    [Fact]
    public void device_builder()
    {
        using var client = new DeviceAuthClientBuilder(ClientId, ["streaming", "user-read-email"]).Build();
        Assert.Equal(ClientId, client.ClientId);
        Assert.Equal(["streaming", "user-read-email"], client.Scopes);
        Assert.False(client.ShouldOpenUrl);
        using var opened = new DeviceAuthClientBuilder(ClientId, []).OpenInBrowser().Build();
        Assert.True(opened.ShouldOpenUrl);
    }

    // ---- PKCE, authorize URL, form encoding -----------------------------------------------------------------------

    [Fact]
    public void pkce_s256_rfc7636_appendix_b()
    {
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.ChallengeS256("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void pkce_random_verifier_shape()
    {
        var (challenge, verifier) = Pkce.NewRandomSha256();
        Assert.Equal(43, verifier.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", verifier);
        Assert.Equal(Pkce.ChallengeS256(verifier), challenge);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", OAuthHelpers.NewCsrfToken());
    }

    [Fact]
    public void authorize_url_layout()
    {
        var url = OAuthHelpers.BuildAuthorizeUrl(
            ClientId,
            "http://127.0.0.1:8898/login",
            ["streaming", "user-read-email"],
            "STATE",
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
        Assert.Equal(
            "https://accounts.spotify.com/authorize?response_type=code&client_id=65b708073fc0480ea92a077233ca87bd" +
            "&state=STATE&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256" +
            "&redirect_uri=http%3A%2F%2F127.0.0.1%3A8898%2Flogin&scope=streaming+user-read-email",
            url);
    }

    [Fact]
    public void authorize_url_without_scopes_has_no_scope_param()
    {
        var url = OAuthHelpers.BuildAuthorizeUrl(ClientId, "http://127.0.0.1:8898/login", [], "S", "C");
        Assert.DoesNotContain("scope=", url);
    }

    [Fact]
    public void form_urlencode_matches_url_crate()
    {
        Assert.Equal(
            "k=a+b*-._%7E%21%2F%3A%C3%A9&x%3Dy=",
            OAuthHelpers.FormUrlEncode([new("k", "a b*-._~!/:é"), new("x=y", "")]));
    }

    [Theory]
    [InlineData("bearer", "Bearer")]
    [InlineData("Bearer", "Bearer")]
    [InlineData("MAC", "Mac")]
    [InlineData("PoP", "Extension(\"pop\")")]
    public void token_type_debug(string raw, string expected)
    {
        Assert.Equal(expected, OAuthHelpers.TokenTypeDebug(raw));
    }

    // ---- local redirect listener ----------------------------------------------------------------------------------

    [Fact]
    public async Task listener_returns_code_and_answers_with_message()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var server = OAuthHelpers.AcceptAuthCodeAsync(listener, "Go back to your terminal :)", cts.Token);
        using var http = new System.Net.Http.HttpClient();
        var body = await http.GetStringAsync($"http://127.0.0.1:{port}/login?code=abc&state=xyz", cts.Token);

        Assert.Equal("abc", await server);
        Assert.Equal("Go back to your terminal :)", body);
    }

    [Fact]
    public async Task listener_reports_missing_code_after_responding()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var server = OAuthHelpers.AcceptAuthCodeAsync(listener, "café", cts.Token);
        using var http = new System.Net.Http.HttpClient();
        var bytes = await http.GetByteArrayAsync($"http://127.0.0.1:{port}/login?error=access_denied", cts.Token);

        var e = await Assert.ThrowsAsync<OAuthException>(() => server);
        Assert.Equal(OAuthErrorKind.AuthCodeNotFound, e.Kind);
        Assert.Equal("Auth code param not found in URI http://localhost/login?error=access_denied", e.Message);
        Assert.Equal(Encoding.UTF8.GetBytes("café"), bytes); // content-length counts bytes
    }

    [Fact]
    public async Task listener_rejects_malformed_request_line()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var server = OAuthHelpers.AcceptAuthCodeAsync(listener, "x", cts.Token);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            await client.GetStream().WriteAsync("GARBAGE\r\n"u8.ToArray(), cts.Token);
            var e = await Assert.ThrowsAsync<OAuthException>(() => server);
            Assert.Equal(OAuthErrorKind.AuthCodeListenerParse, e.Kind);
        }
    }

    [Fact]
    public async Task listener_bind_failure()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var endpoint = (IPEndPoint)occupied.LocalEndpoint;

        var e = await Assert.ThrowsAsync<OAuthException>(
            () => OAuthHelpers.GetAuthCodeListenerAsync(endpoint, "x", CancellationToken.None));
        Assert.Equal(OAuthErrorKind.AuthCodeListenerBind, e.Kind);
        Assert.StartsWith($"Failed to bind server to {endpoint} (", e.Message);
    }

    [Fact]
    public async Task listener_bound_from_redirect_uri()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var addr = await OAuthHelpers.GetSocketAddressAsync($"http://127.0.0.1:{port}/login", cts.Token);
        Assert.NotNull(addr);
        var server = OAuthHelpers.GetAuthCodeListenerAsync(addr, "ok", cts.Token);

        using var http = new System.Net.Http.HttpClient();
        string body = "";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                body = await http.GetStringAsync($"http://127.0.0.1:{port}/login?state=s&code=xyz", cts.Token);
                break;
            }
            catch (HttpRequestException) when (attempt < 50 && !server.IsCompleted)
            {
                await Task.Delay(20, cts.Token);
            }
        }

        Assert.Equal("xyz", await server);
        Assert.Equal("ok", body);
    }

    [Fact]
    public async Task stdin_reads_redirect_url()
    {
        var output = new StringWriter();
        var code = await OAuthHelpers.GetAuthCodeStdinAsync(
            new StringReader("  https://example.com/cb?code=fromstdin&state=s  \n"), output, default);
        Assert.Equal("fromstdin", code);
        Assert.Equal("Provide redirect URL" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task stdin_end_of_input_is_bad_uri()
    {
        var e = await Assert.ThrowsAsync<OAuthException>(
            () => OAuthHelpers.GetAuthCodeStdinAsync(new StringReader(""), TextWriter.Null, default));
        Assert.Equal(OAuthErrorKind.AuthCodeBadUri, e.Kind);
    }

    // ---- authorization code flow end to end (stdin + fake token endpoint) -----------------------------------------

    [Fact]
    public async Task get_access_token_exchanges_code_with_pkce_verifier()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"access_token":"AT","token_type":"bearer","expires_in":3600,"refresh_token":"RT","scope":"streaming user-read-email"}"""));
        var output = new StringWriter();
        using var client = new OAuthClient(ClientId, new Uri("https://example.com/cb"), ["streaming"], false, "m", handler)
        {
            Input = new StringReader("https://example.com/cb?code=THECODE&state=s\n"),
            Output = output,
        };

        var before = DateTimeOffset.UtcNow;
        var token = await client.GetAccessTokenAsync();

        Assert.Equal("AT", token.AccessToken);
        Assert.Equal("RT", token.RefreshToken);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(["streaming", "user-read-email"], token.Scopes);
        Assert.InRange(token.ExpiresAt, before.AddSeconds(3600), DateTimeOffset.UtcNow.AddSeconds(3600));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://accounts.spotify.com/api/token", request.Url);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("application/json", request.Accept);

        var form = request.Body.Split('&');
        Assert.Equal("grant_type=authorization_code", form[0]);
        Assert.Equal("code=THECODE", form[1]);
        Assert.StartsWith("code_verifier=", form[2]);
        Assert.Equal($"client_id={ClientId}", form[3]);
        Assert.Equal("redirect_uri=https%3A%2F%2Fexample.com%2Fcb", form[4]);
        var verifier = form[2]["code_verifier=".Length..];

        // The printed authorize URL carries the matching S256 challenge.
        var printed = output.ToString();
        Assert.Contains("Browse to: https://accounts.spotify.com/authorize?response_type=code&client_id=" + ClientId, printed);
        Assert.Contains($"&code_challenge={Pkce.ChallengeS256(verifier)}&code_challenge_method=S256", printed);
        Assert.Contains("&redirect_uri=https%3A%2F%2Fexample.com%2Fcb&scope=streaming", printed);
        Assert.Contains("Provide redirect URL", printed);
    }

    [Fact]
    public async Task exchange_server_error()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Invalid authorization code"}"""));
        using var client = new OAuthClient(ClientId, new Uri("https://example.com/cb"), ["streaming"], false, "m", handler)
        {
            Input = new StringReader("https://example.com/cb?code=X\n"),
            Output = TextWriter.Null,
        };

        var e = await Assert.ThrowsAsync<OAuthException>(() => client.GetAccessTokenAsync());
        Assert.Equal(OAuthErrorKind.ExchangeCode, e.Kind);
        Assert.Equal(
            "Failed to exchange code for access token (Server returned error response: invalid_grant: Invalid authorization code)",
            e.Message);
    }

    [Fact]
    public async Task refresh_token_request_and_defaults()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"NEW","token_type":"Bearer"}"""));
        using var client = new OAuthClient(ClientId, new Uri("http://127.0.0.1:8898/login"), ["streaming"], false, "m", handler);

        var before = DateTimeOffset.UtcNow;
        var token = await client.RefreshTokenAsync("RT");

        Assert.Equal("NEW", token.AccessToken);
        Assert.Equal("", token.RefreshToken);
        Assert.Equal(["streaming"], token.Scopes); // default scopes when the response omits them
        Assert.InRange(token.ExpiresAt, before.AddSeconds(3600), DateTimeOffset.UtcNow.AddSeconds(3600));
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"grant_type=refresh_token&refresh_token=RT&client_id={ClientId}", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "text/html", "<html/>", "Other error: unexpected response Content-Type: \"text/html\", should be `application/json`")]
    [InlineData(HttpStatusCode.OK, "application/json", "", "Other error: server returned empty response body")]
    [InlineData(HttpStatusCode.OK, "application/json", """{"access_token":"x"}""", "Failed to parse server response")]
    [InlineData(HttpStatusCode.OK, "application/json", """{"access_token":null,"token_type":"bearer"}""", "Failed to parse server response")]
    [InlineData(HttpStatusCode.Unauthorized, "application/json", "", "Other error: server returned empty error response")]
    [InlineData(HttpStatusCode.BadRequest, "text/plain", "nope", "Failed to parse server response")]
    [InlineData(HttpStatusCode.BadRequest, "application/json", """{"error":"invalid_client","error_uri":"https://x"}""", "Server returned error response: invalid_client (see https://x)")]
    public async Task refresh_endpoint_errors(HttpStatusCode status, string contentType, string body, string expected)
    {
        var handler = new FakeHandler(_ => Raw(status, contentType, body));
        using var client = new OAuthClient(ClientId, new Uri("http://127.0.0.1:8898/login"), [], false, "m", handler);

        var e = await Assert.ThrowsAsync<OAuthException>(() => client.RefreshTokenAsync("RT"));
        Assert.Equal(OAuthErrorKind.ExchangeCode, e.Kind);
        Assert.Equal($"Failed to exchange code for access token ({expected})", e.Message);
    }

    [Fact]
    public async Task refresh_transport_error()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = new OAuthClient(ClientId, new Uri("http://127.0.0.1:8898/login"), [], false, "m", handler);

        var e = await Assert.ThrowsAsync<OAuthException>(() => client.RefreshTokenAsync("RT"));
        Assert.Equal("Failed to exchange code for access token (Request failed)", e.Message);
    }

    // ---- device authorization flow --------------------------------------------------------------------------------

    [Fact]
    public async Task request_device_code()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK,
            """{"device_code":"DC","user_code":"ABCD-EFGH","verification_url":"https://spotify.com/pair","expires_in":600}"""));
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var client = new DeviceAuthClient(ClientId, ["streaming", "user-read-email"], false, handler) { Now = () => now };

        var auth = await client.RequestDeviceCodeAsync();

        Assert.Equal("DC", auth.DeviceCode);
        Assert.Equal("ABCD-EFGH", auth.UserCode);
        Assert.Equal("https://spotify.com/pair", auth.VerificationUri);
        Assert.Null(auth.VerificationUriComplete);
        Assert.Equal("https://spotify.com/pair", auth.Url);
        Assert.Equal(TimeSpan.FromSeconds(5), auth.Interval); // oauth2 default
        Assert.Equal(now.AddSeconds(600), auth.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(600), auth.ExpiresIn);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://accounts.spotify.com/oauth2/device/authorize", request.Url);
        Assert.Equal($"scope=streaming+user-read-email&client_id={ClientId}", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"unauthorized_client"}""", "Server returned error response: unauthorized_client")]
    [InlineData(HttpStatusCode.OK, """{"device_code":"DC","user_code":"U","expires_in":600}""", "Failed to parse server response")]
    [InlineData(HttpStatusCode.OK, """{"device_code":"DC","user_code":"U","verification_uri":"not a url","expires_in":600}""", "Failed to parse server response")]
    public async Task request_device_code_errors(HttpStatusCode status, string body, string expected)
    {
        var handler = new FakeHandler(_ => Json(status, body));
        using var client = new DeviceAuthClient(ClientId, [], false, handler);

        var e = await Assert.ThrowsAsync<OAuthException>(() => client.RequestDeviceCodeAsync());
        Assert.Equal(OAuthErrorKind.DeviceCode, e.Kind);
        Assert.Equal($"Failed to obtain device code ({expected})", e.Message);
        Assert.Equal($"client_id={ClientId}", Assert.Single(handler.Requests).Body); // no scope param without scopes
    }

    [Fact]
    public async Task poll_honours_pending_slow_down_and_backoff()
    {
        var responses = new Queue<Func<HttpResponseMessage>>(
        [
            () => Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            () => Json(HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
            () => throw new HttpRequestException("network down"),
            () => Json(HttpStatusCode.OK, """{"access_token":"AT","token_type":"Bearer","refresh_token":"RT","expires_in":10}"""),
        ]);
        var handler = new FakeHandler(_ => responses.Dequeue()());
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sleeps = new List<TimeSpan>();
        using var client = new DeviceAuthClient(ClientId, ["streaming"], false, handler)
        {
            Now = () => now,
            Sleep = (d, _) =>
            {
                sleeps.Add(d);
                now += d;
                return Task.CompletedTask;
            },
        };
        var auth = new DeviceAuthorization("DC", "U", "https://spotify.com/pair", null, TimeSpan.FromSeconds(2), now.AddSeconds(600), TimeSpan.FromSeconds(600));

        var token = await client.PollForTokenAsync(auth);

        // pending keeps 2 s, slow_down adds 5 s, a transport error doubles to at most 10 s; the first poll is immediate.
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10)], sleeps);
        Assert.Equal("AT", token.AccessToken);
        Assert.Equal("RT", token.RefreshToken);
        Assert.Equal(["streaming"], token.Scopes);
        Assert.Equal(now.AddSeconds(10), token.ExpiresAt);
        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(
            $"grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&device_code=DC&client_id={ClientId}", r.Body));
    }

    [Fact]
    public async Task poll_gives_up_after_expiry()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""));
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var client = new DeviceAuthClient(ClientId, [], false, handler)
        {
            Now = () => now,
            Sleep = (d, _) =>
            {
                now += d;
                return Task.CompletedTask;
            },
        };
        var auth = new DeviceAuthorization("DC", "U", "https://spotify.com/pair", null, TimeSpan.FromSeconds(5), now.AddSeconds(12), TimeSpan.FromSeconds(12));

        var e = await Assert.ThrowsAsync<OAuthException>(() => client.PollForTokenAsync(auth));
        Assert.Equal(OAuthErrorKind.ExchangeDeviceCode, e.Kind);
        Assert.Equal(
            "Failed to exchange device code for access token (Server returned error response: expired_token: This device code has expired.)",
            e.Message);
        Assert.Equal(3, handler.Requests.Count); // t = 0, 5, 10; t = 15 is past the deadline
    }

    [Fact]
    public async Task poll_stops_on_access_denied()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":"access_denied","error_description":"The user denied the request"}"""));
        using var client = new DeviceAuthClient(ClientId, [], false, handler) { Sleep = (_, _) => Task.CompletedTask };
        var auth = new DeviceAuthorization("DC", "U", "https://spotify.com/pair", null, TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(10));

        var e = await Assert.ThrowsAsync<OAuthException>(() => client.PollForTokenAsync(auth));
        Assert.Equal(
            "Failed to exchange device code for access token (Server returned error response: access_denied: The user denied the request)",
            e.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task device_get_access_token_announces_url_and_code()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            Json(HttpStatusCode.OK, """{"device_code":"DC","user_code":"ABCD","verification_uri":"https://spotify.com/pair","verification_uri_complete":"https://spotify.com/pair?code=ABCD","expires_in":600,"interval":1}"""),
            Json(HttpStatusCode.OK, """{"access_token":"AT","token_type":"Bearer","refresh_token":"RT"}"""),
        ]);
        var handler = new FakeHandler(_ => responses.Dequeue());
        var output = new StringWriter();
        using var client = new DeviceAuthClient(ClientId, ["streaming"], false, handler) { Output = output };

        var token = await client.GetAccessTokenAsync();

        Assert.Equal("AT", token.AccessToken);
        Assert.Equal(
            "Browse to: https://spotify.com/pair?code=ABCD" + Environment.NewLine +
            "If prompted, enter code: ABCD" + Environment.NewLine,
            output.ToString());
    }

    [Fact]
    public void token_to_string_lists_scopes()
    {
        var token = new OAuthToken("a", "r", DateTimeOffset.UnixEpoch, "Bearer", ["x", "y"]);
        Assert.Equal(
            "OAuthToken { AccessToken = a, RefreshToken = r, ExpiresAt = 1970-01-01T00:00:00.0000000+00:00, TokenType = Bearer, Scopes = [x, y] }",
            token.ToString());
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => Raw(status, "application/json", body);

    private static HttpResponseMessage Raw(HttpStatusCode status, string contentType, string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed record RecordedRequest(string Url, string Body, string? ContentType, string? Accept);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri!.ToString(),
                body,
                request.Content?.Headers.ContentType?.ToString(),
                request.Headers.Accept.ToString()));
            return respond(request);
        }
    }
}
