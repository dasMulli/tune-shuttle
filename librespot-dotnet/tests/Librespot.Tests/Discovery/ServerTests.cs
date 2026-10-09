// Tests for src/Librespot.Discovery/Server.cs (port of discovery/src/server.rs; upstream has no #[cfg(test)] there).
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Discovery;
using Librespot.Protocol.Authentication;
using Xunit;

namespace Librespot.Tests.Discovery;

public class ServerTests
{
    private const string DeviceId = "0123456789abcdef0123456789abcdef01234567";
    private const string ClientId = "65b708073fc0480ea92a077233ca87bd";

    // Independent vector (Python: pow / hashlib / hmac) for the DH + key derivation of handle_add_user.
    private const string ServerPrivLe = "01080f161d242b323940474e555c636a71787f868d949ba2a9b0b7bec5ccd3dae1e8eff6fd040b121920272e353c434a51585f666d747b828990979ea5acb3bac1c8cfd6dde4ebf2f900070e151c232a31383f464d545b626970777e858c93";
    private const string ClientPub = "e88ac9ff54c6e75ae2ae560318f9f45ab050a8c7e3bc989f0f05089d0eca9d7c74ec5f71de71f27610877bdb2d46d3e01af1c7b3c50af82455362fe05bac5ecb302bac5bf5fdf2664d7e8fb688dd2bcb72e74ca842bce710447165f8fe6a68b9";
    private const string Shared = "fb2ed304fa0e1a2e900d560cb521c58352c80fe97dae9468c6dd10ea09eb29e3d2a7d36499d051e53bacbf1f3ac178f0f9998203b76794c66e9bc69204414153c58a753fa1122c9557e0ba0b8a0d3656fc0b2fb967f1de0e23b14218934fdf0f";
    private const string ChecksumKey = "1e368b9f02784e96ff91f690b9d234d1b2300dad";
    private const string EncryptionKey16 = "7c39306e6d1829c4724a607cc3aeea51";

    [Fact]
    public void Dh_shared_secret_and_blob_keys_match_reference()
    {
        var server = DhLocalKeys.FromPrivateKey(Convert.FromHexString(ServerPrivLe));
        var shared = server.SharedSecret(Convert.FromHexString(ClientPub));
        Assert.Equal(Shared, Convert.ToHexStringLower(shared));

        var (checksumKey, encryptionKey) = RequestHandler.DeriveBlobKeys(shared);
        Assert.Equal(ChecksumKey, Convert.ToHexStringLower(checksumKey));
        Assert.Equal(EncryptionKey16, Convert.ToHexStringLower(encryptionKey[..16]));
    }

    [Fact]
    public void Get_info_matches_upstream_json_shape()
    {
        var config = NewConfig();
        config.IsGroup = true;
        config.Aliases.Add(new Alias("Kitchen", 7, true));
        var handler = new RequestHandler(config, Channel.CreateUnbounded<DiscoveryEvent>().Writer);

        var response = handler.HandleGetInfo();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;

        // serde_json's json! (no preserve_order) emits sorted keys.
        var keys = root.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Equal(
            [
                "accountReq", "activeUser", "aliases", "availability", "brandDisplayName", "clientID", "deviceID", "deviceType",
                "groupStatus", "libraryVersion", "modelDisplayName", "productID", "publicKey", "remoteName", "resolverVersion",
                "scope", "spotifyError", "status", "statusString", "supported_capabilities", "supported_drm_media_formats",
                "tokenType", "version",
            ],
            keys);

        Assert.Equal(101, root.GetProperty("status").GetInt32());
        Assert.Equal("OK", root.GetProperty("statusString").GetString());
        Assert.Equal(0, root.GetProperty("spotifyError").GetInt32());
        Assert.Equal("2.9.0", root.GetProperty("version").GetString());
        Assert.Equal(DeviceId, root.GetProperty("deviceID").GetString());
        Assert.Equal("Speaker", root.GetProperty("deviceType").GetString());
        Assert.Equal("Test Speaker", root.GetProperty("remoteName").GetString());
        Assert.Equal(LibrespotVersion.SemVer, root.GetProperty("libraryVersion").GetString());
        Assert.Equal("GROUP", root.GetProperty("groupStatus").GetString());
        Assert.Equal("default", root.GetProperty("tokenType").GetString());
        Assert.Equal(ClientId, root.GetProperty("clientID").GetString());
        Assert.Equal(0, root.GetProperty("productID").GetInt32());
        Assert.Equal("streaming", root.GetProperty("scope").GetString());
        Assert.Equal(1, root.GetProperty("supported_capabilities").GetInt32());
        Assert.Equal(0, root.GetProperty("supported_drm_media_formats").GetArrayLength());
        Assert.Equal("PREMIUM", root.GetProperty("accountReq").GetString());
        Assert.Equal(string.Empty, root.GetProperty("activeUser").GetString());
        Assert.Equal("{\"id\":\"7\",\"isGroup\":\"true\",\"name\":\"Kitchen\"}", root.GetProperty("aliases")[0].GetRawText());
        Assert.NotEmpty(Convert.FromBase64String(root.GetProperty("publicKey").GetString()!));
    }

    [Fact]
    public async Task Add_user_over_http_yields_credentials_and_sets_active_user()
    {
        var events = Channel.CreateUnbounded<DiscoveryEvent>();
        ushort port = 0;
        var server = new DiscoveryServer(NewConfig(), ref port, events.Writer);
        try
        {
            Assert.NotEqual(0, port);
            using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            var info = await GetInfoAsync(http);
            var serverKey = Convert.FromBase64String(info.GetProperty("publicKey").GetString()!);

            var authData = Enumerable.Range(0, 150).Select(i => (byte)i).ToArray();
            var body = ZeroconfClient.AddUserBody(serverKey, "alice", DeviceId, authData);
            var response = await http.PostAsync("/", new StringContent(body, Encoding.ASCII, "application/x-www-form-urlencoded"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"spotifyError\":0,\"status\":101,\"statusString\":\"OK\"}", await response.Content.ReadAsStringAsync());

            Assert.True(events.Reader.TryRead(out var ev));
            var credentials = Assert.IsType<DiscoveryEvent.Credentials>(ev).Value;
            Assert.Equal("alice", credentials.Username);
            Assert.Equal(AuthenticationType.AuthenticationStoredSpotifyCredentials, credentials.AuthType);
            Assert.Equal(authData, credentials.AuthData);

            // getInfo now reports the active user (same keep-alive connection).
            Assert.Equal("alice", (await GetInfoAsync(http)).GetProperty("activeUser").GetString());
        }
        finally
        {
            await server.ShutdownAsync();
        }
    }

    [Fact]
    public void Add_user_with_bad_mac_answers_error_mac()
    {
        var events = Channel.CreateUnbounded<DiscoveryEvent>();
        var handler = new RequestHandler(NewConfig(), events.Writer);
        using var info = JsonDocument.Parse(handler.HandleGetInfo().Body);
        var serverKey = Convert.FromBase64String(info.RootElement.GetProperty("publicKey").GetString()!);

        var body = ZeroconfClient.AddUserBody(serverKey, "bob", DeviceId, [1, 2, 3], corruptMac: true);
        var response = handler.Handle(new HttpRequest("POST", "/", null, Encoding.ASCII.GetBytes(body)));

        Assert.Equal("{\"spotifyError\":1,\"status\":102,\"statusString\":\"ERROR-MAC\"}", Encoding.UTF8.GetString(response.Body));
        Assert.False(events.Reader.TryRead(out _));
        Assert.Equal(string.Empty, handler.ActiveUser());
    }

    [Theory]
    [InlineData("action=addUser&blob=AAAA&clientKey=AAAA", "userName")]
    [InlineData("action=addUser&userName=x&clientKey=AAAA", "blob")]
    [InlineData("action=addUser&userName=x&blob=AAAA", "clientKey")]
    public void Add_user_missing_params(string body, string key)
    {
        var handler = new RequestHandler(NewConfig(), Channel.CreateUnbounded<DiscoveryEvent>().Writer);
        var error = Assert.Throws<DiscoveryException>(() => handler.Handle(new HttpRequest("POST", "/", null, Encoding.ASCII.GetBytes(body))));
        Assert.Equal(DiscoveryException.Reason.Params, error.Cause);
        Assert.Equal(ErrorKind.InvalidArgument, error.Kind);
        Assert.Equal($"Missing params for key {key}", error.Detail);
    }

    [Fact]
    public void Add_user_short_blob_is_hmac_error()
    {
        var handler = new RequestHandler(NewConfig(), Channel.CreateUnbounded<DiscoveryEvent>().Writer);
        var clientKey = Uri.EscapeDataString(Convert.ToBase64String(DhLocalKeys.Random().PublicKey()));
        var body = $"action=addUser&userName=x&blob={Uri.EscapeDataString(Convert.ToBase64String(new byte[10]))}&clientKey={clientKey}";
        var error = Assert.Throws<DiscoveryException>(() => handler.Handle(new HttpRequest("POST", "/", null, Encoding.ASCII.GetBytes(body))));
        Assert.Equal(DiscoveryException.Reason.HmacError, error.Cause);
        Assert.Equal("Creating SHA1 HMAC failed for base key [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]", error.Detail);
    }

    [Fact]
    public void Add_user_invalid_base64_is_failed_precondition()
    {
        var handler = new RequestHandler(NewConfig(), Channel.CreateUnbounded<DiscoveryEvent>().Writer);
        var error = Assert.Throws<LibrespotException>(() =>
            handler.Handle(new HttpRequest("POST", "/", null, "action=addUser&userName=x&blob=!!!&clientKey=AAAA"u8.ToArray())));
        Assert.Equal(ErrorKind.FailedPrecondition, error.Kind);
    }

    [Theory]
    [InlineData("GET", "action=addUser")]
    [InlineData("POST", "action=getInfo")]
    [InlineData("GET", "action=unknown")]
    [InlineData("GET", null)]
    public void Unknown_routes_are_not_found(string method, string? query)
    {
        var handler = new RequestHandler(NewConfig(), Channel.CreateUnbounded<DiscoveryEvent>().Writer);
        var response = handler.Handle(new HttpRequest(method, "/", query, []));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(response.Body);
    }

    [Fact]
    public async Task Handler_error_drops_the_connection_and_server_keeps_running()
    {
        ushort port = 0;
        var server = new DiscoveryServer(NewConfig(), ref port, Channel.CreateUnbounded<DiscoveryEvent>().Writer);
        try
        {
            using (var http = new System.Net.Http.HttpClient())
            {
                await Assert.ThrowsAsync<HttpRequestException>(() =>
                    http.PostAsync($"http://127.0.0.1:{port}/?action=addUser", new StringContent(string.Empty)));
            }

            using var http2 = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            var response = await http2.GetAsync("/?action=nothing");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(0, response.Content.Headers.ContentLength);
        }
        finally
        {
            await server.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Raw_http_chunked_body_and_connection_close()
    {
        ushort port = 0;
        var server = new DiscoveryServer(NewConfig(), ref port, Channel.CreateUnbounded<DiscoveryEvent>().Writer);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            var request = "POST /?x=1 HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                + "8\r\naction=g\r\n6\r\netInfo\r\n0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

            using var reader = new StreamReader(stream, Encoding.ASCII);
            var text = await reader.ReadToEndAsync(); // server closes after the response

            // POST + getInfo -> 404 (getInfo is GET only), proving the chunked body was parsed into params.
            Assert.StartsWith("HTTP/1.1 404 Not Found\r\n", text, StringComparison.Ordinal);
            Assert.Contains("connection: close\r\n", text, StringComparison.Ordinal);
            Assert.Contains("content-length: 0\r\n", text, StringComparison.Ordinal);
        }
        finally
        {
            await server.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Malformed_request_gets_400()
    {
        ushort port = 0;
        var server = new DiscoveryServer(NewConfig(), ref port, Channel.CreateUnbounded<DiscoveryEvent>().Writer);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var stream = tcp.GetStream();
            await stream.WriteAsync("garbage\r\n\r\n"u8.ToArray());
            using var reader = new StreamReader(stream, Encoding.ASCII);
            Assert.StartsWith("HTTP/1.1 400 Bad Request\r\n", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await server.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Shutdown_closes_idle_keep_alive_connections()
    {
        ushort port = 0;
        var server = new DiscoveryServer(NewConfig(), ref port, Channel.CreateUnbounded<DiscoveryEvent>().Writer);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync("GET /?action=getInfo HTTP/1.1\r\nHost: a\r\n\r\n"u8.ToArray());
        var buffer = new byte[4096];
        Assert.True(await stream.ReadAsync(buffer) > 0);

        await server.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // The idle connection was closed by the server.
        int read;
        do
        {
            read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        while (read > 0);
    }

    [Theory]
    [InlineData("a=1&b=2", "a=1|b=2")]
    [InlineData("a=1&&b", "a=1|b=")]
    [InlineData("x=a+b%20c%2Bd", "x=a b c+d")]
    [InlineData("k%3D=v%3d=w", "k==v==w")]
    [InlineData("bad=%zz%4", "bad=%zz%4")]
    [InlineData("u=%C3%A9", "u=é")]
    public void Form_urlencoded_parsing(string input, string expected)
    {
        var pairs = FormUrlEncoded.Parse(Encoding.ASCII.GetBytes(input)).Select(p => $"{p.Key}={p.Value}");
        Assert.Equal(expected, string.Join('|', pairs));
    }

    private static ServerConfig NewConfig() => new()
    {
        Name = "Test Speaker",
        DeviceType = DeviceType.Speaker,
        DeviceId = DeviceId,
        ClientId = ClientId,
    };

    private static async Task<JsonElement> GetInfoAsync(System.Net.Http.HttpClient http)
    {
        var response = await http.GetAsync("/?action=getInfo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return doc.RootElement.Clone();
    }
}
