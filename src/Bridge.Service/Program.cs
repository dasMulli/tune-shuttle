using Airplay2.Sender.Discovery;
using Airplay2.Sender.Streams;
using Bridge.Service.Airplay;
using Bridge.Service.Api;
using Bridge.Service.Diagnostics;
using Bridge.Service.Output;
using Bridge.Service.Spotify;
using Bridge.Service.Store;
using Librespot.Playback;
using Microsoft.Extensions.Options;

namespace Bridge.Service;

/// <summary>
/// Host entry point (docs/ARCHITECTURE.md, "Deployment"): ASP.NET Core generic host with Windows Service and systemd integration,
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
        var app = Build(args);
        await app.Services.GetRequiredService<IBridgeStore>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Creates the application: services, middleware and routes.</summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddEnvironmentVariables("BRIDGE_");
        builder.Configuration.AddCommandLine(args); // command line wins over the BRIDGE_ variables (install-service.ps1 passes the endpoint)
        builder.Host.UseWindowsService(o => o.ServiceName = "TuneShuttle");
        builder.Host.UseSystemd();

        // The web app's files: a published layout (artifacts/bridge, the container) has them in wwwroot/. A run from the
        // build output (dotnet run, bin/<config>/net10.0/bridge.dll) has no wwwroot/ and finds them through the
        // bridge.staticwebassets.runtime.json manifest, which ASP.NET reads on its own only in Development; read it in
        // every environment. Without the manifest (published layouts) this does nothing.
        if (!builder.Environment.IsDevelopment())
        {
            builder.WebHost.UseStaticWebAssets();
        }

        AddBridgeServices(builder.Services, builder.Configuration);

        var app = builder.Build();

        _ = app.Services.GetRequiredService<EventHub>(); // attaches the log buffer before the first request

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseBlazorFrameworkFiles();
        app.UseStaticFiles();
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapBridgeApi();
        app.MapFallbackToFile("index.html");
        return app;
    }

    /// <summary>Registers the bridge's services (also used by tests, which replace the browser and the options).</summary>
    public static void AddBridgeServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<BridgeOptions>().Bind(configuration.GetSection("Bridge"));
        services.AddProblemDetails();
        services.AddOpenApi();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BridgePaths>();

        // Logging: the in-memory ring for the diagnostics page and, on Windows, daily files (docs/ARCHITECTURE.md, "Deployment").
        services.AddSingleton<LogBuffer>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<LogBuffer>());
        services.AddSingleton<FileLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<FileLoggerProvider>());

        services.AddSingleton<IBridgeStore>(sp => new SqliteBridgeStore(sp.GetRequiredService<BridgePaths>().DatabasePath, sp.GetRequiredService<ILogger<SqliteBridgeStore>>()));
        services.AddSingleton<EventHub>();
        services.AddSingleton<RuntimeState>();
        services.AddSingleton<IReceiverBrowser>(sp => new MdnsReceiverBrowser(new ReceiverBrowserOptions(), sp.GetRequiredService<ILogger<MdnsReceiverBrowser>>()));
        services.AddSingleton<AirplaySenderProvider>();
        services.AddSingleton(sp => new AirplaySink(AudioFormat.S16, sp.GetRequiredService<ILogger<AirplaySink>>()) { Sources = sp.GetRequiredService<SourceSwitch>() });
        services.AddSingleton<DtoFactory>();
        services.AddSingleton<ReceiverInventory>();
        services.AddSingleton<PairingCoordinator>();
        services.AddSingleton<OutputManager>();
        services.AddSingleton<ReceiverJanitor>();
        services.AddSingleton<IPlaybackOutput>(sp => sp.GetRequiredService<OutputManager>());
        services.AddSingleton<IArtworkLoader, HttpArtworkLoader>();
        services.AddSingleton<SpotifyHost>();
        services.AddSingleton<BridgeController>();

        // The AirPlay receiver (SPEC-receiver.md 8.1-8.3): one PtpPort per process for both roles (6.1), the source switch
        // in front of the output (8.2) and the hosted receiver (8.3).
        services.AddSingleton<IPtpPort>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<BridgeOptions>>().Value;
            return new PtpPort(
                new PtpPortOptions { BindAddress = o.LocalIPAddress, EventPort = o.Airplay.PtpEventPort, GeneralPort = o.Airplay.PtpGeneralPort },
                sp.GetRequiredService<ILogger<PtpPort>>());
        });
        // The switch's evictions and replays reach the sources through delegates resolved at call time (8.2): the sources
        // and DtoFactory depend on the switch, so it cannot take them in its constructor.
        services.AddSingleton(sp => new SourceSwitch(
            sp.GetRequiredService<RuntimeState>(),
            sp.GetRequiredService<EventHub>(),
            sp.GetRequiredService<ILogger<SourceSwitch>>(),
            new SourceSwitchHooks
            {
                EvictSpotifyAsync = ct => sp.GetRequiredService<AirplaySink>().EvictAsync(() => sp.GetRequiredService<SpotifyHost>().TryCommand("pause"), ct),
                EvictAirplayAsync = (reason, ct) => sp.GetRequiredService<AirplaySource>().EndSessionAsync(reason, ct),
                StartSpotifyIfRunningAsync = ct => sp.GetRequiredService<AirplaySink>().StartIfRunning().WaitAsync(ct),
                ResumeAirplayIfPlayingAsync = ct => sp.GetRequiredService<AirplaySource>().ResumeIfPlayingAsync(ct),
                StatusAsync = ct => sp.GetRequiredService<DtoFactory>().StatusAsync(ct),
            }));
        services.AddSingleton<AirplaySource>();

        AddFakeReceivers(services, configuration);

        // Start order: receivers are browsed first, then the output is restored, then Spotify comes up (it reads the
        // output's level), then the AirPlay receiver; the host stops them in reverse, so the AirPlay session ends and
        // Spotify pauses before the receivers are torn down.
        services.AddHostedService(sp => sp.GetRequiredService<ReceiverInventory>());
        services.AddHostedService(sp => sp.GetRequiredService<OutputManager>());
        services.AddHostedService(sp => sp.GetRequiredService<SpotifyHost>());
        services.AddHostedService(sp => sp.GetRequiredService<AirplaySource>());
        services.AddHostedService<ReceiverPruner>();
    }

    private static void AddFakeReceivers(IServiceCollection services, IConfiguration configuration)
    {
        var count = configuration.GetValue<int>("Bridge:FakeReceivers");
        if (count <= 0)
        {
            return;
        }

#if BRIDGE_FAKE_RECEIVERS
        services.AddSingleton<StaticReceiverBrowser>();
        services.AddSingleton<IReceiverBrowser>(sp => sp.GetRequiredService<StaticReceiverBrowser>());
        services.Configure<BridgeOptions>(o => o.BrowseReceivers = false);
        services.AddSingleton<DevFakeReceivers>();
        services.AddHostedService(sp => sp.GetRequiredService<DevFakeReceivers>());
#else
        services.AddHostedService(sp => new FakeReceiversUnavailable(sp.GetRequiredService<ILogger<FakeReceiversUnavailable>>()));
#endif
    }

#if !BRIDGE_FAKE_RECEIVERS
    /// <summary>Logs that <c>Bridge:FakeReceivers</c> needs a Debug build from the full repository.</summary>
    private sealed class FakeReceiversUnavailable(ILogger<FakeReceiversUnavailable> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogWarning("Bridge:FakeReceivers is set, but this build has no fake receiver (Debug builds from the repository only)");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
#endif
}
