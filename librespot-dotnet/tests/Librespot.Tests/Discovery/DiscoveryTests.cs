// Tests for src/Librespot.Discovery/Discovery.cs (port of discovery/src/lib.rs; upstream has no #[cfg(test)]). Avahi.cs
// (not ported) is covered by Find_backends_like_upstream ("avahi" is Unavailable).
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Librespot.Core;
using Librespot.Discovery;
using Xunit;
using DiscoveryService = Librespot.Discovery.Discovery;

namespace Librespot.Tests.Discovery;

public class DiscoveryTests
{
    private const string DeviceId = "aabbccddeeff00112233445566778899aabbccdd";

    [Fact]
    public void Find_backends_like_upstream()
    {
        Assert.NotNull(DnsSdBackends.Find(null));
        Assert.NotNull(DnsSdBackends.Find("libmdns"));

        var unavailable = Assert.ThrowsAny<LibrespotException>(() => DnsSdBackends.Find("avahi"));
        Assert.Equal(ErrorKind.Unavailable, unavailable.Kind);
        Assert.Equal("librespot built without 'avahi' support", unavailable.Detail);

        Assert.Equal(ErrorKind.Unavailable, Assert.ThrowsAny<LibrespotException>(() => DnsSdBackends.Find("dns-sd")).Kind);

        var unknown = Assert.ThrowsAny<LibrespotException>(() => DnsSdBackends.Find("bonjour"));
        Assert.Equal(ErrorKind.NotFound, unknown.Kind);
        Assert.Equal("unknown zeroconf backend 'bonjour'", unknown.Detail);
    }

    [Fact]
    public void Discovery_error_messages_and_kinds()
    {
        Assert.Equal(ErrorKind.Unavailable, DiscoveryException.DnsSd(new IOException("boom")).Kind);
        Assert.Equal("Setting up dns-sd failed: boom", DiscoveryException.DnsSd(new IOException("boom")).Detail);
        Assert.Equal("Service unavailable { Creating SHA1 block cipher failed }", DiscoveryException.Aes(new ArgumentException()).Message);
        Assert.Equal(ErrorKind.Unavailable, DiscoveryException.HttpServer(new IOException("x")).Kind);
        Assert.Equal(ErrorKind.InvalidArgument, DiscoveryException.Hmac([1, 2]).Kind);
        Assert.Equal("Creating SHA1 HMAC failed for base key [1, 2]", DiscoveryException.Hmac([1, 2]).Detail);
        Assert.Equal(ErrorKind.InvalidArgument, DiscoveryException.Params("blob").Kind);
    }

    [Fact]
    public async Task Launch_advertises_and_yields_credentials_then_ends_on_zeroconf_error()
    {
        var backend = new FakeBackend();
        var discovery = DiscoveryService.Builder(DeviceId, "client")
            .Name("Living Room")
            .DeviceType(DeviceType.Avr)
            .AddAlias("Alias", 3, false)
            .ZeroconfIp([IPAddress.Loopback])
            .ZeroconfBackend(backend.Launch)
            .Launch();
        try
        {
            Assert.Equal("Living Room", backend.Name);
            Assert.Equal(discovery.Port, backend.Port);
            Assert.Equal([IPAddress.Loopback], backend.Ips);

            using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{discovery.Port}/") };
            using var info = JsonDocument.Parse(await http.GetStringAsync("/?action=getInfo"));
            Assert.Equal("AVR", info.RootElement.GetProperty("deviceType").GetString());
            Assert.Equal("Living Room", info.RootElement.GetProperty("remoteName").GetString());
            Assert.Equal("Alias", info.RootElement.GetProperty("aliases")[0].GetProperty("name").GetString());

            var serverKey = Convert.FromBase64String(info.RootElement.GetProperty("publicKey").GetString()!);
            var body = ZeroconfClient.AddUserBody(serverKey, "carol", DeviceId, [9, 8, 7]);
            var response = await http.PostAsync("/", new StringContent(body, Encoding.ASCII, "application/x-www-form-urlencoded"));
            response.EnsureSuccessStatusCode();

            await using var credentials = discovery.ReadCredentialsAsync().GetAsyncEnumerator();
            Assert.True(await credentials.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("carol", credentials.Current.Username);
            Assert.Equal(new byte[] { 9, 8, 7 }, credentials.Current.AuthData);

            // A fatal zeroconf error terminates the credential stream.
            backend.Events!.TryWrite(new DiscoveryEvent.ZeroconfError(DiscoveryException.DnsSd(new IOException("gone"))));
            Assert.False(await credentials.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await discovery.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, backend.ShutdownCount);

        // Idempotent.
        await discovery.DisposeAsync();
        Assert.Equal(1, backend.ShutdownCount);
    }

    [Fact]
    public async Task Shutdown_ends_the_event_stream()
    {
        var backend = new FakeBackend();
        var discovery = DiscoveryService.Builder(DeviceId, "client").ZeroconfBackend(backend.Launch).Launch();
        var events = discovery.Events().GetAsyncEnumerator();
        var next = events.MoveNextAsync().AsTask();

        await discovery.ShutdownAsync();

        Assert.False(await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Librespot", backend.Name); // upstream default name
    }

    [Fact]
    public void Failing_backend_fails_launch()
    {
        var builder = DiscoveryService.Builder(DeviceId, "client")
            .ZeroconfBackend((_, _, _, _) => throw LibrespotException.Unavailable("no mdns"));
        Assert.Equal("no mdns", Assert.ThrowsAny<LibrespotException>(() => builder.Launch()).Detail);
    }

    [Fact]
    public async Task Dns_sd_handle_from_task_cancels_and_awaits()
    {
        var cts = new CancellationTokenSource();
        var stopped = false;
        var task = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }
        });

        var handle = DnsSdHandle.FromTask(task, cts);
        await handle.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(stopped);
        await handle.DisposeAsync(); // second shutdown is a no-op
    }

    private sealed class FakeBackend
    {
        public string? Name { get; private set; }

        public IReadOnlyList<IPAddress>? Ips { get; private set; }

        public ushort Port { get; private set; }

        public ChannelWriter<DiscoveryEvent>? Events { get; private set; }

        public int ShutdownCount { get; private set; }

        public DnsSdHandle Launch(string name, IReadOnlyList<IPAddress> ips, ushort port, ChannelWriter<DiscoveryEvent> events)
        {
            (Name, Ips, Port, Events) = (name, ips, port, events);
            return new DnsSdHandle(() =>
            {
                ShutdownCount++;
                return Task.CompletedTask;
            });
        }
    }
}
