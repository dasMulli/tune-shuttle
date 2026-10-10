using Bridge.Web;
using Bridge.Web.Localization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Bridge.Tests.Web;

/// <summary>
/// Fixture of the bUnit component tests (WORK-PLAN.md web-app "Verify": bUnit 2.11.3 supports .NET 10), shared by the
/// per-page test classes. The API is a stub handler that records requests; the state is fed directly, as the event
/// client would. The catalog is English (<c>_l.SetLanguage("de")</c> switches it; that also changes the process-wide
/// default-thread cultures, so a class that does it joins <see cref="ProcessCultureCollection"/>, and one that asserts
/// numbers of the <see cref="Format"/> helpers opens a <see cref="CultureScope"/>) and the browser module is mocked as in
/// <see cref="UiTest.SetupModule"/> (<c>_module</c> records the calls).
/// </summary>
public abstract class ComponentTestBase : IAsyncDisposable
{
    private protected readonly BunitContext _ctx = new();
    private protected readonly BridgeState _state = new();
    private protected readonly I18n _l = new(I18n.DefaultCulture);
    private protected readonly BunitJSModuleInterop _module;

    protected ComponentTestBase()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddLogging();
        _ctx.Services.AddSingleton(_state);
        _ctx.Services.AddSingleton(_l);
        _module = UiTest.SetupModule(_ctx);
        _ctx.Services.AddSingleton(sp => new BrowserBridge(sp.GetRequiredService<IJSRuntime>()));
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
        _ctx.Services.AddSingleton(UiTest.NeverConnectingEvents(_state));
        _ctx.Services.AddSingleton<BridgeCommands>();
        return handler;
    }

    private protected StubHandler UseApi(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) => UseApi((r, b) => Task.FromResult(respond(r, b)));

    private protected static string PathOf(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
}
