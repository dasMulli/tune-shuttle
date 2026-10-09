// Port of discovery/src/server.rs.
//
// hyper's HTTP/1 server is replaced by a minimal HTTP/1.1 implementation on a raw TcpListener (HttpListener needs URL
// ACLs on Windows): request line, headers, Content-Length or chunked body, Expect: 100-continue, keep-alive, and
// graceful shutdown (idle connections close, a request in progress is answered first).
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Core.Crypto;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Discovery;

/// <summary>An alias advertised in <c>getInfo</c> (upstream <c>server::Alias</c>).</summary>
/// <param name="Name">The alias name.</param>
/// <param name="Id">The alias id.</param>
/// <param name="IsGroup">Whether the alias is a group.</param>
internal sealed record Alias(string Name, uint Id, bool IsGroup);

/// <summary>Configuration of the zeroconf HTTP server (upstream <c>server::Config</c>).</summary>
internal sealed class ServerConfig
{
    /// <summary>The name shown in Spotify clients.</summary>
    public required string Name { get; set; }

    /// <summary>The device type (icon).</summary>
    public DeviceType DeviceType { get; set; } = DeviceTypeExtensions.Default;

    /// <summary>The device id the credential blob is encrypted for.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Whether the device is a group.</summary>
    public bool IsGroup { get; set; }

    /// <summary>The client id reported in <c>getInfo</c>.</summary>
    public required string ClientId { get; init; }

    /// <summary>The aliases reported in <c>getInfo</c>.</summary>
    public List<Alias> Aliases { get; } = [];
}

/// <summary>A parsed HTTP request (the parts of hyper's <c>Request</c> the handler uses).</summary>
/// <param name="Method">The method, case-sensitive as in hyper.</param>
/// <param name="Path">The path of the request target.</param>
/// <param name="Query">The query string without '?', or <c>null</c>.</param>
/// <param name="Body">The collected body.</param>
internal sealed record HttpRequest(string Method, string Path, string? Query, byte[] Body);

/// <summary>An HTTP response (status + body; hyper adds <c>content-length</c> and <c>date</c>).</summary>
/// <param name="StatusCode">The status code.</param>
/// <param name="Body">The body.</param>
internal sealed record HttpResponse(HttpStatusCode StatusCode, byte[] Body);

/// <summary>Answers the zeroconf requests (upstream <c>RequestHandler</c>).</summary>
internal sealed class RequestHandler
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<DiscoveryServer>();

    // serde_json escapes only '"', '\\' and control characters; it does not HTML-escape.
    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ServerConfig _config;
    private readonly Lock _usernameLock = new();
    private readonly DhLocalKeys _keys;
    private readonly ChannelWriter<DiscoveryEvent> _eventTx;
    private string? _username;

    /// <summary>Upstream <c>RequestHandler::new</c> (random DH keys); <paramref name="keys"/> is for tests.</summary>
    public RequestHandler(ServerConfig config, ChannelWriter<DiscoveryEvent> eventTx, DhLocalKeys? keys = null)
    {
        _config = config;
        _keys = keys ?? DhLocalKeys.Random();
        _eventTx = eventTx;
    }

    /// <summary>The user of the last successful <c>addUser</c>, or the empty string.</summary>
    public string ActiveUser()
    {
        lock (_usernameLock)
        {
            return _username ?? string.Empty;
        }
    }

    /// <summary>Upstream <c>handle_get_info</c>.</summary>
    public HttpResponse HandleGetInfo()
    {
        var publicKey = Convert.ToBase64String(_keys.PublicKey());
        var deviceType = _config.DeviceType.ToDisplayString();
        var activeUser = ActiveUser();

        // options based on zeroconf guide, search for `groupStatus` on page
        var groupStatus = _config.IsGroup ? "GROUP" : "NONE";

        // See: https://developer.spotify.com/documentation/commercial-hardware/implementation/guides/zeroconf/
        // Keys are written in the order serde_json's BTreeMap-backed `json!` emits them (sorted).
        var body = WriteJson(w =>
        {
            // undocumented but should still work
            w.WriteString("accountReq", "PREMIUM");
            w.WriteString("activeUser", activeUser);
            w.WriteStartArray("aliases");
            foreach (var alias in _config.Aliases)
            {
                w.WriteStartObject();
                w.WriteString("id", alias.Id.ToString(CultureInfo.InvariantCulture));
                w.WriteString("isGroup", alias.IsGroup ? "true" : "false");
                w.WriteString("name", alias.Name);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("availability", string.Empty);
            w.WriteString("brandDisplayName", "librespot");
            w.WriteString("clientID", _config.ClientId);
            w.WriteString("deviceID", _config.DeviceId);
            w.WriteString("deviceType", deviceType);
            // valid values are "GROUP" and "NONE"
            w.WriteString("groupStatus", groupStatus);
            w.WriteString("libraryVersion", LibrespotVersion.SemVer);
            w.WriteString("modelDisplayName", "librespot");
            w.WriteNumber("productID", 0);
            // valid value seen in the wild: "empty"
            w.WriteString("publicKey", publicKey);
            w.WriteString("remoteName", _config.Name);
            w.WriteString("resolverVersion", "1");
            // Other known scope: client-authorization-universal
            // Comma-separated.
            w.WriteString("scope", "streaming");
            w.WriteNumber("spotifyError", 0);
            w.WriteNumber("status", 101);
            w.WriteString("statusString", "OK");
            // TODO: bitmask but what are the flags?  (upstream comment)
            w.WriteNumber("supported_capabilities", 1);
            w.WriteStartArray("supported_drm_media_formats");
            w.WriteEndArray();
            // valid value documented & seen in the wild: "accesstoken"
            // Using it will cause clients to fail to connect.
            w.WriteString("tokenType", "default");
            // departing from the Spotify documentation, Google Cast uses "5.0.0"
            w.WriteString("version", "2.9.0");
            // others seen-in-the-wild:
            // - "deviceAPI_isGroup": False
        });

        return new HttpResponse(HttpStatusCode.OK, body);
    }

    /// <summary>Upstream <c>handle_add_user</c>.</summary>
    /// <exception cref="DiscoveryException">A parameter is missing or the blob is too short.</exception>
    /// <exception cref="LibrespotException">Invalid base64, an undecodable credential blob, or a closed event channel.</exception>
    public HttpResponse HandleAddUser(IReadOnlyDictionary<string, string> parameters)
    {
        const string usernameKey = "userName";
        var username = parameters.GetValueOrDefault(usernameKey) ?? throw DiscoveryException.Params(usernameKey);

        const string blobKey = "blob";
        var encryptedBlobText = parameters.GetValueOrDefault(blobKey) ?? throw DiscoveryException.Params(blobKey);

        const string clientKeyKey = "clientKey";
        var clientKeyText = parameters.GetValueOrDefault(clientKeyKey) ?? throw DiscoveryException.Params(clientKeyKey);

        var encryptedBlob = DecodeBase64(encryptedBlobText);

        var clientKey = DecodeBase64(clientKeyText);
        var sharedKey = _keys.SharedSecret(clientKey);

        var encryptedBlobLen = encryptedBlob.Length;
        // Upstream checks `< 16` and then panics on the slice below for 16..=35 bytes (which drops the connection, like
        // the error does); both cases are reported as HmacError here.
        if (encryptedBlobLen < 16 + 20)
        {
            throw DiscoveryException.Hmac(encryptedBlob);
        }

        var iv = encryptedBlob.AsSpan(0, 16);
        var encrypted = encryptedBlob.AsSpan(16, encryptedBlobLen - 16 - 20);
        var cksum = encryptedBlob.AsSpan(encryptedBlobLen - 20, 20);

        var (checksumKey, encryptionKey) = DeriveBlobKeys(sharedKey);

        var mac = HMACSHA1.HashData(checksumKey, encrypted);
        if (!CryptographicOperations.FixedTimeEquals(mac, cksum))
        {
            Log.LogWarning("Login error for user {Username}: MAC mismatch", RustDebug(username));
            var result = WriteJson(w =>
            {
                w.WriteNumber("spotifyError", 1);
                w.WriteNumber("status", 102);
                w.WriteString("statusString", "ERROR-MAC");
            });

            return new HttpResponse(HttpStatusCode.OK, result);
        }

        var decrypted = encrypted.ToArray();
        AesCtr.Transform(encryptionKey.AsSpan(0, 16), iv, 0, decrypted);

        var credentials = Credentials.WithBlob(username, decrypted, Encoding.UTF8.GetBytes(_config.DeviceId));

        lock (_usernameLock)
        {
            if (!_eventTx.TryWrite(new DiscoveryEvent.Credentials(credentials)))
            {
                throw LibrespotException.Internal("channel closed");
            }

            _username = username;
        }

        var ok = WriteJson(w =>
        {
            w.WriteNumber("spotifyError", 0);
            w.WriteNumber("status", 101);
            w.WriteString("statusString", "OK");
        });

        return new HttpResponse(HttpStatusCode.OK, ok);
    }

    /// <summary>
    /// The key derivation of upstream <c>handle_add_user</c>: <c>base_key = SHA1(shared)[..16]</c>,
    /// <c>checksum_key = HMAC-SHA1(base_key, "checksum")</c>, <c>encryption_key = HMAC-SHA1(base_key, "encryption")</c>
    /// (the full 20 bytes; AES uses the first 16).
    /// </summary>
    internal static (byte[] ChecksumKey, byte[] EncryptionKey) DeriveBlobKeys(ReadOnlySpan<byte> sharedKey)
    {
        var baseKey = SHA1.HashData(sharedKey).AsSpan(0, 16);
        return (HMACSHA1.HashData(baseKey, "checksum"u8), HMACSHA1.HashData(baseKey, "encryption"u8));
    }

    /// <summary>Upstream <c>not_found</c>: 404 with an empty body.</summary>
    public static HttpResponse NotFound() => new(HttpStatusCode.NotFound, []);

    /// <summary>
    /// Upstream <c>handle</c>: merges query and form-body parameters (body wins) and dispatches on method + <c>action</c>.
    /// </summary>
    public HttpResponse Handle(HttpRequest request)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (request.Query is not null)
        {
            foreach (var (key, value) in FormUrlEncoded.Parse(Encoding.UTF8.GetBytes(request.Query)))
            {
                parameters[key] = value;
            }
        }

        if (request.Method != "GET")
        {
            Log.LogDebug("{Method} {Path} {Params}", request.Method, RustDebug(request.Path), FormatParams(parameters));
        }

        foreach (var (key, value) in FormUrlEncoded.Parse(request.Body))
        {
            parameters[key] = value;
        }

        var action = parameters.GetValueOrDefault("action");

        return (request.Method, action) switch
        {
            ("GET", "getInfo") => HandleGetInfo(),
            ("POST", "addUser") => HandleAddUser(parameters),
            _ => NotFound(),
        };
    }

    private static byte[] DecodeBase64(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException e)
        {
            throw LibrespotException.From(e);
        }
    }

    private static byte[] WriteJson(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // Rust `{:?}` of the BTreeMap: {"key": "value", ...}
    private static string FormatParams(IEnumerable<KeyValuePair<string, string>> parameters) =>
        "{" + string.Join(", ", parameters.Select(p => $"{RustDebug(p.Key)}: {RustDebug(p.Value)}")) + "}";

    // Rust `{:?}` of a string.
    private static string RustDebug(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary><c>application/x-www-form-urlencoded</c> parsing (the <c>form_urlencoded::parse</c> crate function).</summary>
internal static class FormUrlEncoded
{
    /// <summary>Splits on '&amp;', skips empty pairs, splits each at the first '=', decodes '+' and percent escapes (UTF-8, lossy).</summary>
    public static IEnumerable<(string Key, string Value)> Parse(byte[] input)
    {
        var start = 0;
        while (start < input.Length)
        {
            var end = Array.IndexOf(input, (byte)'&', start);
            if (end < 0)
            {
                end = input.Length;
            }

            if (end > start)
            {
                var pair = input.AsSpan(start, end - start);
                var eq = pair.IndexOf((byte)'=');
                yield return eq < 0
                    ? (Decode(pair), string.Empty)
                    : (Decode(pair[..eq]), Decode(pair[(eq + 1)..]));
            }

            start = end + 1;
        }
    }

    private static string Decode(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        var n = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            if (b == (byte)'+')
            {
                output[n++] = (byte)' ';
            }
            else if (b == (byte)'%' && i + 2 < input.Length && IsHex(input[i + 1]) && IsHex(input[i + 2]))
            {
                output[n++] = (byte)((HexValue(input[i + 1]) << 4) | HexValue(input[i + 2]));
                i += 2;
            }
            else
            {
                output[n++] = b;
            }
        }

        return Encoding.UTF8.GetString(output, 0, n);
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => b - 'A' + 10,
    };
}

/// <summary>The zeroconf HTTP server (upstream <c>DiscoveryServer</c>).</summary>
internal sealed class DiscoveryServer
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<DiscoveryServer>();

    private readonly RequestHandler _discovery;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _closeTx = new();
    private readonly Lock _connectionsLock = new();
    private readonly HashSet<Task> _connections = [];
    private readonly Task _taskHandle;
    private int _shutdown;
    private int _closeTxDisposed;

    /// <summary>
    /// Upstream <c>DiscoveryServer::new</c>: binds the listener (0.0.0.0 on Windows, dual-stack [::] elsewhere) on
    /// <paramref name="port"/>, which is updated to the actual port, and starts accepting connections.
    /// </summary>
    /// <exception cref="LibrespotException">Binding failed.</exception>
    public DiscoveryServer(ServerConfig config, ref ushort port, ChannelWriter<DiscoveryEvent> eventTx, DhLocalKeys? keys = null)
    {
        _discovery = new RequestHandler(config, eventTx, keys);

        // this creates a dual stack socket on non-windows systems
        var address = OperatingSystem.IsWindows() ? IPAddress.Any : IPAddress.IPv6Any;

        TcpListener listener;
        try
        {
            listener = new TcpListener(address, port);
            if (!OperatingSystem.IsWindows())
            {
                listener.Server.DualMode = true;
            }

            listener.Start();
        }
        catch (SocketException e)
        {
            Log.LogWarning("Discovery server failed to start: {Error}", e.Message);
            throw LibrespotException.From(e);
        }

        _listener = listener;
        port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        Port = port;
        Log.LogDebug("Zeroconf server listening on 0.0.0.0:{Port}", port);

        _taskHandle = Task.Run(RunAsync);
    }

    /// <summary>The port the server listens on.</summary>
    public ushort Port { get; }

    /// <summary>The request handler (for tests).</summary>
    internal RequestHandler Handler => _discovery;

    /// <summary>Upstream <c>shutdown</c>: stop accepting, let open connections finish, then return.</summary>
    public async Task ShutdownAsync()
    {
        Log.LogDebug("Shutting down discovery server");
        if (Interlocked.Exchange(ref _shutdown, 1) == 0 && _taskHandle.IsCompleted)
        {
            Log.LogWarning("Discovery server unexpectedly disappeared");
            DisposeCloseTx();
            return;
        }

        try
        {
            await _closeTx.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // An earlier ShutdownAsync already completed.
        }

        await _taskHandle.ConfigureAwait(false);
        DisposeCloseTx();
        Log.LogDebug("Discovery server stopped");
    }

    /// <summary>Signals shutdown without waiting (upstream: dropping the <c>DiscoveryServer</c> drops <c>close_tx</c>).</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) == 0)
        {
            _closeTx.Cancel();
        }
    }

    // Upstream drops close_tx with the server; here it is disposed once the accept loop and all connections are done.
    private void DisposeCloseTx()
    {
        if (Interlocked.Exchange(ref _closeTxDisposed, 1) == 0)
        {
            _closeTx.Dispose();
        }
    }

    private async Task RunAsync()
    {
        var token = _closeTx.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // `Ok((stream, _)) = listener.accept()` in upstream's select! ignores accept errors.
                    continue;
                }

                var connection = ServeConnectionAsync(client, token);
                lock (_connectionsLock)
                {
                    _connections.Add(connection);
                }

                _ = connection.ContinueWith(
                    t =>
                    {
                        lock (_connectionsLock)
                        {
                            _connections.Remove(t);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            _listener.Stop();
        }

        // graceful.shutdown().await
        Task[] pending;
        lock (_connectionsLock)
        {
            pending = [.. _connections];
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    /// <summary>One HTTP/1.1 connection (hyper <c>serve_connection</c> watched by <c>GracefulShutdown</c>).</summary>
    private async Task ServeConnectionAsync(TcpClient client, CancellationToken graceful)
    {
        await Task.Yield();
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new HttpConnectionReader(stream);
                while (true)
                {
                    HttpConnectionReader.Parsed? parsed;
                    try
                    {
                        parsed = await reader.ReadRequestAsync(graceful).ConfigureAwait(false);
                    }
                    catch (HttpConnectionReader.BadRequestException)
                    {
                        await HttpConnectionReader.WriteResponseAsync(stream, new HttpResponse(HttpStatusCode.BadRequest, []), "1.1", keepAlive: false)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (parsed is null)
                    {
                        return; // EOF or shutdown while idle
                    }

                    HttpResponse response;
                    try
                    {
                        response = _discovery.Handle(parsed.Request);
                    }
                    catch (Exception e)
                    {
                        // Upstream logs the error and the connection task then panics on `Result::unwrap`, which drops
                        // the connection without a response.
                        Log.LogError("could not handle discovery request: {Error}", e.Message);
                        return;
                    }

                    var keepAlive = parsed.KeepAlive && !graceful.IsCancellationRequested;
                    await HttpConnectionReader.WriteResponseAsync(stream, response, parsed.Version, keepAlive).ConfigureAwait(false);
                    if (!keepAlive)
                    {
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                // Errors are logged in the service_fn (upstream ignores connection errors)
                Log.LogTrace("discovery connection closed: {Error}", e.Message);
            }
        }
    }
}

/// <summary>Minimal HTTP/1.x request reader and response writer for <see cref="DiscoveryServer"/>.</summary>
internal sealed class HttpConnectionReader(Stream stream)
{
    /// <summary>hyper's default maximum buffer size for the request head (8 KiB + 100 * 4 KiB).</summary>
    public const int MaxHeadSize = 8192 + (4096 * 100);

    /// <summary>Upper bound for a request body; the zeroconf requests are a few KiB.</summary>
    public const int MaxBodySize = 16 * 1024 * 1024;

    /// <summary>Time allowed to receive the rest of a request once its first byte arrived.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Stream _stream = stream;
    private byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    /// <summary>A parsed request with its connection semantics.</summary>
    public sealed record Parsed(HttpRequest Request, string Version, bool KeepAlive);

    /// <summary>A malformed request (hyper answers 400 and closes).</summary>
    public sealed class BadRequestException(string message) : Exception(message);

    /// <summary>
    /// Reads the next request. Returns <c>null</c> on EOF, or when <paramref name="graceful"/> fires before the first
    /// byte of a request arrived.
    /// </summary>
    public async Task<Parsed?> ReadRequestAsync(CancellationToken graceful)
    {
        if (_start == _end)
        {
            _start = _end = 0;
            int read;
            try
            {
                read = await _stream.ReadAsync(_buffer.AsMemory(), graceful).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (read == 0)
            {
                return null;
            }

            _end = read;
        }

        using var timeout = new CancellationTokenSource(RequestTimeout);
        var token = timeout.Token;

        // Request head
        int headEnd;
        while ((headEnd = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (_end - _start > MaxHeadSize)
            {
                throw new BadRequestException("request head too large");
            }

            if (!await FillAsync(token).ConfigureAwait(false))
            {
                return null;
            }
        }

        var head = Encoding.Latin1.GetString(_buffer, _start, headEnd);
        _start += headEnd + 4;

        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[0].Length == 0 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new BadRequestException("invalid request line");
        }

        var method = requestLine[0];
        var target = requestLine[1];
        var version = requestLine[2]["HTTP/".Length..];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new BadRequestException("invalid header line");
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }

        // Target: origin-form ("/path?query") or absolute-form ("http://host/path?query")
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            target = absolute.PathAndQuery;
        }

        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var query = question < 0 ? null : target[(question + 1)..];

        var connection = headers.GetValueOrDefault("Connection") ?? string.Empty;
        var keepAlive = version == "1.0"
            ? connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase)
            : !connection.Contains("close", StringComparison.OrdinalIgnoreCase);

        // Body
        byte[] body;
        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
            && transferEncoding.EndsWith("chunked", StringComparison.OrdinalIgnoreCase))
        {
            await SendContinueIfExpectedAsync(headers, version, token).ConfigureAwait(false);
            body = await ReadChunkedAsync(token).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Content-Length", out var contentLength))
        {
            if (!int.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > MaxBodySize)
            {
                throw new BadRequestException("invalid content-length");
            }

            if (length > 0)
            {
                await SendContinueIfExpectedAsync(headers, version, token).ConfigureAwait(false);
            }

            body = await ReadExactAsync(length, token).ConfigureAwait(false);
        }
        else
        {
            body = [];
        }

        return new Parsed(new HttpRequest(method, path, query, body), version, keepAlive);
    }

    /// <summary>Writes a response the way hyper does: status line, <c>content-length</c>, <c>date</c>, body.</summary>
    public static async Task WriteResponseAsync(Stream stream, HttpResponse response, string version, bool keepAlive)
    {
        var builder = new StringBuilder();
        builder.Append(version == "1.0" ? "HTTP/1.0 " : "HTTP/1.1 ")
            .Append((int)response.StatusCode).Append(' ').Append(ReasonPhrase(response.StatusCode)).Append("\r\n");
        if (!keepAlive && version != "1.0")
        {
            builder.Append("connection: close\r\n");
        }
        else if (keepAlive && version == "1.0")
        {
            builder.Append("connection: keep-alive\r\n");
        }

        builder.Append("content-length: ").Append(response.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append("date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n\r\n");

        var head = Encoding.ASCII.GetBytes(builder.ToString());
        var packet = new byte[head.Length + response.Body.Length];
        head.CopyTo(packet, 0);
        response.Body.CopyTo(packet, head.Length);

        using var timeout = new CancellationTokenSource(RequestTimeout);
        await stream.WriteAsync(packet, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private static string ReasonPhrase(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => "OK",
        HttpStatusCode.NotFound => "Not Found",
        HttpStatusCode.BadRequest => "Bad Request",
        _ => status.ToString(),
    };

    private async Task SendContinueIfExpectedAsync(Dictionary<string, string> headers, string version, CancellationToken token)
    {
        if (version != "1.0"
            && headers.TryGetValue("Expect", out var expect)
            && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase)
            && _start == _end)
        {
            await _stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> ReadChunkedAsync(CancellationToken token)
    {
        var body = new ArrayBufferWriter<byte>();
        while (true)
        {
            var sizeLine = await ReadLineAsync(token).ConfigureAwait(false);
            var semicolon = sizeLine.IndexOf(';');
            var sizeText = (semicolon < 0 ? sizeLine : sizeLine[..semicolon]).Trim();
            if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size)
                || size < 0 || body.WrittenCount + size > MaxBodySize)
            {
                throw new BadRequestException("invalid chunk size");
            }

            if (size == 0)
            {
                // trailers
                while ((await ReadLineAsync(token).ConfigureAwait(false)).Length > 0)
                {
                }

                return body.WrittenSpan.ToArray();
            }

            body.Write(await ReadExactAsync(size, token).ConfigureAwait(false));
            if ((await ReadLineAsync(token).ConfigureAwait(false)).Length != 0)
            {
                throw new BadRequestException("invalid chunk terminator");
            }
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken token)
    {
        int lineEnd;
        while ((lineEnd = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n"u8)) < 0)
        {
            if (_end - _start > MaxHeadSize)
            {
                throw new BadRequestException("line too long");
            }

            if (!await FillAsync(token).ConfigureAwait(false))
            {
                throw new BadRequestException("unexpected end of request");
            }
        }

        var line = Encoding.Latin1.GetString(_buffer, _start, lineEnd);
        _start += lineEnd + 2;
        return line;
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken token)
    {
        var result = new byte[count];
        var copied = Math.Min(count, _end - _start);
        _buffer.AsSpan(_start, copied).CopyTo(result);
        _start += copied;

        while (copied < count)
        {
            var read = await _stream.ReadAsync(result.AsMemory(copied), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new BadRequestException("unexpected end of body");
            }

            copied += read;
        }

        return result;
    }

    private async Task<bool> FillAsync(CancellationToken token)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), token).ConfigureAwait(false);
        if (read == 0)
        {
            return false;
        }

        _end += read;
        return true;
    }
}
