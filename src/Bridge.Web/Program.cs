using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Bridge.Web;

/// <summary>Blazor WebAssembly entry point. The app talks to the bridge at its own origin (REST + WebSocket).</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.RootComponents.Add<App>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");
        builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
        builder.Services.AddSingleton<BridgeApiClient>();
        // TODO(impl: web-app): BridgeEventClient (WebSocket), state container, pages
        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
