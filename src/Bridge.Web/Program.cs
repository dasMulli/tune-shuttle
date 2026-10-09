using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bridge.Web;

/// <summary>
/// Blazor WebAssembly entry point. The app talks to the bridge at its own origin: REST under <c>/api</c> and the event
/// WebSocket at <c>/ws/events</c> (WORK-PLAN.md section 7).
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
        host.Services.GetRequiredService<BridgeEventClient>().Start();
        await host.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Registers the client side: the REST client, the state container, the event client and the commands, all
    /// singletons (the browser runs one app instance per tab). Also used by the component tests.
    /// </summary>
    public static IServiceCollection AddBridgeClient(IServiceCollection services, Uri baseAddress, Func<IServiceProvider, BridgeEventClient>? eventClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baseAddress);
        services.AddSingleton(TimeProvider.System);
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
}
