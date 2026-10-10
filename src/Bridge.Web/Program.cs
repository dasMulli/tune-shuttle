using Bridge.Web.Localization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Bridge.Web;

/// <summary>
/// Blazor WebAssembly entry point. The app talks to the bridge at its own origin: REST under <c>/api</c> and the event
/// WebSocket at <c>/ws/events</c> (docs/ARCHITECTURE.md, "Bridge API").
/// </summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");
        builder.Logging.SetMinimumLevel(builder.HostEnvironment.IsDevelopment() ? LogLevel.Debug : LogLevel.Information);
        AddBridgeClient(builder.Services, new Uri(builder.HostEnvironment.BaseAddress));

        var host = builder.Build();
        await StartLanguageAsync(host.Services).ConfigureAwait(false);
        host.Services.GetRequiredService<BridgeEventClient>().Start();
        await host.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Registers the client side: the REST client, the state container, the event client, the message catalog, the
    /// browser bridge and the commands, all singletons (the browser runs one app instance per tab). Also used by the
    /// component tests, which pass their own <paramref name="i18n"/> (default: a new catalog in English).
    /// </summary>
    public static IServiceCollection AddBridgeClient(IServiceCollection services, Uri baseAddress, Func<IServiceProvider, BridgeEventClient>? eventClient = null, I18n? i18n = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baseAddress);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(i18n ?? new I18n());
        services.AddSingleton(sp => new BrowserBridge(sp.GetRequiredService<IJSRuntime>()));
        services.AddSingleton(sp => new BridgeApiClient(new HttpClient { BaseAddress = baseAddress }));
        services.AddSingleton(sp => new BridgeState(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(eventClient ?? (sp => new BridgeEventClient(
            sp.GetRequiredService<BridgeState>(),
            BridgeEventClient.EventsUri(baseAddress),
            sp.GetRequiredService<ILogger<BridgeEventClient>>(),
            time: sp.GetRequiredService<TimeProvider>())));
        services.AddSingleton<BridgeCommands>();
        return services;
    }

    /// <summary>
    /// Picks the language before the first render (UI-SPEC 3.2): <c>window.bridgeLang.get()</c> of index.html answers
    /// <c>?lang=</c>, the stored choice or the browser languages (<c>""</c> → English); the catalog switches to it, which
    /// also sets the default-thread cultures, and <c>&lt;html lang&gt;</c> follows. A failing call (no bootstrap script) leaves
    /// English.
    /// </summary>
    private static async Task StartLanguageAsync(IServiceProvider services)
    {
        var i18n = services.GetRequiredService<I18n>();
        var js = services.GetRequiredService<IJSRuntime>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Program));
        string? code = null;
        try
        {
            code = await js.InvokeAsync<string?>("bridgeLang.get").ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            logger.LogWarning("Could not read the language choice, using {Language}: {Error}", I18n.DefaultCulture, ex.Message);
        }

        i18n.SetLanguage(code);
        try
        {
            await js.InvokeVoidAsync("bridgeLang.setDocument", i18n.Code).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            logger.LogWarning("Could not set the document language: {Error}", ex.Message);
        }
    }
}
