using System.Net.WebSockets;
using Bridge.Web;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bridge.Tests.Web;

/// <summary>
/// Fixture of the bUnit component tests (WORK-PLAN.md web-app "Verify": bUnit 2.11.3 supports .NET 10), shared by the
/// per-page test classes. The API is a stub handler that records requests; the state is fed directly, as the event
/// client would.
/// </summary>
public abstract class ComponentTestBase : IAsyncDisposable
{
    private protected readonly BunitContext _ctx = new();
    private protected readonly BridgeState _state = new();

    protected ComponentTestBase()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddLogging();
        _ctx.Services.AddSingleton(_state);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _ctx.DisposeAsync();
    }

    private protected StubHandler UseApi(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        _ctx.Services.AddSingleton(new BridgeApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://bridge.test/") }));
        _ctx.Services.AddSingleton(new BridgeEventClient(
            _state,
            new Uri("ws://bridge.test/ws/events"),
            NullLogger<BridgeEventClient>.Instance,
            connect: (_, _) => Task.FromException<WebSocket>(new WebSocketException("not started in component tests"))));
        _ctx.Services.AddSingleton<BridgeCommands>();
        return handler;
    }

    private protected StubHandler UseApi(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) => UseApi((r, b) => Task.FromResult(respond(r, b)));

    private protected static string PathOf(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
}
