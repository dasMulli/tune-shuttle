using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Airplay2.FakeReceiver;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Pairing;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Output;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using TestReceiver = Airplay2.FakeReceiver.FakeReceiver;

namespace Bridge.Tests.Service;

/// <summary>
/// The bridge in-process (WORK-PLAN.md bridge-service "Verify": WebApplicationFactory against a temp database and fake
/// receivers): Spotify and mDNS off, file logging off, a <see cref="StaticReceiverBrowser"/> instead of mDNS, the sender
/// bound to loopback.
/// </summary>
internal sealed class BridgeFactory : WebApplicationFactory<BridgeController>
{
    private readonly bool _ownsDirectory;

    public BridgeFactory(string? dataDirectory = null, StaticReceiverBrowser? browser = null, bool useMdns = false, ILoggerProvider? extraLogging = null, string environment = "Development")
    {
        Environment = environment;
        UseMdns = useMdns;
        ExtraLogging = extraLogging;
        _ownsDirectory = dataDirectory is null;
        DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "bridge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        Browser = browser ?? new StaticReceiverBrowser();
    }

    public string DataDirectory { get; }

    public StaticReceiverBrowser Browser { get; }

    /// <summary>Live tests: the real mDNS browser instead of <see cref="Browser"/>, the sender on every interface.</summary>
    public bool UseMdns { get; }

    public ILoggerProvider? ExtraLogging { get; }

    /// <summary>The host environment (Development by default; Production is what a plain <c>bridge.dll</c> run gets).</summary>
    public string Environment { get; }

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("DataDirectory", DataDirectory);
        builder.UseSetting("Bridge:EnableSpotify", "false");
        builder.UseSetting("Bridge:BrowseReceivers", UseMdns ? "true" : "false");
        builder.UseSetting("Bridge:FileLogging", "false");
        builder.UseSetting("Bridge:LocalAddress", UseMdns ? string.Empty : "127.0.0.1");
        builder.UseSetting("Bridge:ConnectTimeout", "00:00:20");
        builder.UseSetting("Bridge:ReconnectDelay", "00:00:00.500");
        builder.UseSetting("Bridge:PollReceiverVolumeSeconds", "0");
        if (ExtraLogging is not null)
        {
            builder.UseSetting("Logging:LogLevel:Airplay2", "Debug");
            builder.UseSetting("Logging:LogLevel:Bridge", "Debug");
            builder.ConfigureLogging(logging => logging.AddProvider(ExtraLogging));
        }

        if (!UseMdns)
        {
            builder.ConfigureTestServices(services => services.AddSingleton<IReceiverBrowser>(Browser));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDirectory)
        {
            DeleteDirectory(DataDirectory);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_ownsDirectory)
        {
            DeleteDirectory(DataDirectory);
        }
    }

    public static void DeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // a pooled handle lingers on Windows; the temp folder is cleaned up eventually
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A fake receiver whose events are recorded continuously, so a test can wait for one without losing others.</summary>
internal sealed class RecordedFake : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly ConcurrentQueue<FakeReceiverEvent> _events = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Task _pump;

    private RecordedFake(TestReceiver fake)
    {
        Fake = fake;
        _pump = PumpAsync();
    }

    public TestReceiver Fake { get; }

    public string Id => Fake.DeviceId;

    public IReadOnlyList<FakeReceiverEvent> Events => _events.ToArray();

    public static async Task<RecordedFake> StartAsync(FakeReceiverOptions? options = null)
    {
        var fake = new TestReceiver(options ?? new FakeReceiverOptions());
        await fake.StartAsync(CancellationToken.None);
        return new RecordedFake(fake);
    }

    /// <summary>A PIN receiver (flags bit 9, transient refused) with the given PIN.</summary>
    public static Task<RecordedFake> StartPinAsync(string name, string pin) =>
        StartAsync(new FakeReceiverOptions { Name = name, StatusFlags = 0x404 | 0x200, AllowTransientPairing = false, Pin = pin });

    public DiscoveredReceiver Describe()
    {
        var (airplay, raop, endpoint) = Fake.Describe();
        return new DiscoveredReceiver(Fake.DeviceId, Fake.Options.Name, [endpoint.Address], endpoint.Port, AirplayTxtRecord.Parse(airplay), RaopTxtRecord.Parse(raop), DateTimeOffset.UtcNow);
    }

    /// <summary>Waits until an event of type <typeparamref name="T"/> matching <paramref name="match"/> has been recorded (also earlier ones).</summary>
    public async Task<T> WaitAsync<T>(Func<T, bool>? match = null, TimeSpan? timeout = null, int skip = 0)
        where T : FakeReceiverEvent
    {
        var deadline = DateTime.UtcNow + (timeout ?? Patience);
        while (true)
        {
            var found = _events.OfType<T>().Where(e => match?.Invoke(e) ?? true).Skip(skip).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                Assert.Fail($"{Fake.Options.Name}: no {typeof(T).Name} within {(timeout ?? Patience).TotalSeconds} s. Seen: {string.Join(", ", _events.Select(e => e.GetType().Name).Distinct())}");
            }

            await _signal.WaitAsync(left < TimeSpan.FromMilliseconds(200) ? left : TimeSpan.FromMilliseconds(200));
        }
    }

    public int Count<T>(Func<T, bool>? match = null)
        where T : FakeReceiverEvent => _events.OfType<T>().Count(e => match?.Invoke(e) ?? true);

    public async ValueTask DisposeAsync()
    {
        await Fake.DisposeAsync();
        await _pump;
        _signal.Dispose();
    }

    private async Task PumpAsync()
    {
        await foreach (var e in Fake.Events.ReadAllAsync())
        {
            _events.Enqueue(e);
            _signal.Release();
        }
    }
}

/// <summary>The bridge's WebSocket from a test: reads <see cref="BridgeEvent"/>s in the background.</summary>
internal sealed class EventClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ConcurrentQueue<BridgeEvent> _events = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;

    private EventClient(WebSocket socket)
    {
        _socket = socket;
        _reader = ReadAsync();
    }

    public IReadOnlyList<BridgeEvent> Events => _events.ToArray();

    public static async Task<EventClient> ConnectAsync(BridgeFactory factory)
    {
        var client = factory.Server.CreateWebSocketClient();
        var socket = await client.ConnectAsync(new Uri(factory.Server.BaseAddress, ApiRoutes.Events.TrimStart('/')).ToWebSocketUri(), CancellationToken.None);
        return new EventClient(socket);
    }

    public Task SendAsync(BridgeClientMessage message) =>
        _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, EventHub.JsonOptions), WebSocketMessageType.Text, true, CancellationToken.None);

    public async Task<T> WaitAsync<T>(Func<T, bool>? match = null, TimeSpan? timeout = null)
        where T : BridgeEvent
    {
        var deadline = DateTime.UtcNow + (timeout ?? RecordedFake.Patience);
        while (true)
        {
            var found = _events.OfType<T>().FirstOrDefault(e => match?.Invoke(e) ?? true);
            if (found is not null)
            {
                return found;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                Assert.Fail($"No {typeof(T).Name} event within {(timeout ?? RecordedFake.Patience).TotalSeconds} s. Seen: {string.Join(", ", _events.Select(e => e.GetType().Name).Distinct())}");
            }

            await _signal.WaitAsync(left < TimeSpan.FromMilliseconds(200) ? left : TimeSpan.FromMilliseconds(200));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        await _stop.CancelAsync();
        try
        {
            await _reader;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }

        _socket.Dispose();
        _stop.Dispose();
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[256 * 1024];
        while (!_stop.IsCancellationRequested)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(length), _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                length += result.Count;
            }
            while (!result.EndOfMessage);

            var bridgeEvent = JsonSerializer.Deserialize<BridgeEvent>(buffer.AsSpan(0, length), EventHub.JsonOptions);
            if (bridgeEvent is not null)
            {
                _events.Enqueue(bridgeEvent);
                _signal.Release();
            }
        }
    }
}

/// <summary>HTTP helpers.</summary>
internal static class Http
{
    public static readonly JsonSerializerOptions Json = EventHub.JsonOptions;

    public static Uri ToWebSocketUri(this Uri uri) => new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" }.Uri;

    public static async Task<T> GetJsonAsync<T>(this HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<T> SendJsonAsync<T>(this HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var response = await client.SendJsonAsync(method, url, body);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        return client.SendAsync(request);
    }

    public static string Receiver(string route, string id) => route.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);

    public static string Group(string route, string id, string? receiverId = null)
    {
        var url = route.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
        return receiverId is null ? url : url.Replace("{receiverId}", Uri.EscapeDataString(receiverId), StringComparison.Ordinal);
    }

    /// <summary>Polls until <paramref name="condition"/> holds for the receiver's DTO.</summary>
    public static async Task<ReceiverDto> WaitForReceiverAsync(this HttpClient client, string id, Func<ReceiverDto, bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? RecordedFake.Patience);
        while (true)
        {
            var dto = await client.GetJsonAsync<ReceiverDto>(Receiver(ApiRoutes.Receivers + "/{id}", id));
            if (condition(dto))
            {
                return dto;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Receiver {id} did not reach the expected state; last: {dto}");
            }

            await Task.Delay(100);
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
    }
}

/// <summary>An <see cref="IPairingStore"/> in memory (for tests that build a sender without the bridge).</summary>
internal sealed class MemoryPairingStore : IPairingStore
{
    private readonly ConcurrentDictionary<string, PairingCredentials> _pairings = new();
    private readonly ControllerIdentity _controller = ControllerIdentity.Generate();

    public Task<ControllerIdentity> GetControllerIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(_controller);

    public Task<PairingCredentials?> GetAsync(string receiverId, CancellationToken cancellationToken) =>
        Task.FromResult(_pairings.TryGetValue(receiverId, out var p) ? p : null);

    public Task SaveAsync(string receiverId, PairingCredentials credentials, CancellationToken cancellationToken)
    {
        _pairings[receiverId] = credentials;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string receiverId, CancellationToken cancellationToken)
    {
        _pairings.TryRemove(receiverId, out _);
        return Task.CompletedTask;
    }
}
