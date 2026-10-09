using Bridge.Service.Api;
using Bridge.Service.Store;

namespace Bridge.Service;

/// <summary>
/// Host entry point (docs/PLAN.md "Deployment"): ASP.NET Core generic host with Windows Service and systemd integration,
/// configuration from appsettings.json and environment variables (prefix <c>BRIDGE_</c>), the SQLite file in a
/// configurable data directory, the Blazor web app served from the same process.
/// </summary>
public static class Program
{
    /// <summary>Process start time for the status endpoint.</summary>
    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Builds and runs the host.</summary>
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddEnvironmentVariables("BRIDGE_");
        builder.Host.UseWindowsService(o => o.ServiceName = "AirplayBridge");
        builder.Host.UseSystemd();

        var dataDir = builder.Configuration["DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "bridge.db");

        builder.Services.AddSingleton<IBridgeStore>(sp => new SqliteBridgeStore(dbPath, sp.GetRequiredService<ILogger<SqliteBridgeStore>>()));
        builder.Services.AddSingleton<BridgeController>();
        builder.Services.AddSingleton<EventHub>();
        // TODO(impl: bridge-service): AirplaySender, AirplaySink, the librespot session host (BackgroundService), the output manager,
        // file logging on Windows (PLAN.md), OpenAPI in Development.

        var app = builder.Build();
        await app.Services.GetRequiredService<IBridgeStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        app.UseWebSockets();
        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles();
        app.MapBridgeApi();
        app.MapFallbackToFile("index.html");

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
