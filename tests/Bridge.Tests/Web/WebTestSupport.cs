using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Bridge.Contracts;
using Bridge.Tests.Service;
using Bridge.Web;
using Bridge.Web.Localization;
using Bunit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>DTO builders for the web app tests.</summary>
internal static class Dto
{
    public static readonly ReceiverCapabilitiesDto Caps = new(true, true, false, false, false, true, true, true, true, "0x1C340405D4A00", "0x4");

    public static ReceiverDto Receiver(string id, string name, int level = 50, ReceiverConnectionState state = ReceiverConnectionState.Available, PairingState pairing = PairingState.Transient, string? groupId = null, bool online = true, int offset = 0, string? error = null) =>
        new(id, name, "Model", "Maker", "192.0.2.1", 7000, online, pairing, state, level, offset, groupId, error, Caps);

    public static GroupDto Group(string id, string name, int master, bool active, params (string Id, string Name, int Level)[] members) =>
        new(id, name, master, active, [.. members.Select(m => new GroupMemberDto(m.Id, m.Name, m.Level, ReceiverConnectionState.Available))]);

    public static StatusDto Status(string deviceName = "AirPlay Bridge", bool spotify = false) =>
        new("0.1.0", spotify, spotify ? "martin" : null, deviceName, false, 1, DateTimeOffset.UnixEpoch);

    public static PlaybackDto Playback(bool playing = false, long position = 0, long duration = 0, string? title = null, int volume = 40) =>
        new(playing, title is null ? null : "spotify:track:1", title, title is null ? null : "Artist", title is null ? null : "Album", position, duration, null, volume);

    public static OutputDto NoOutput { get; } = new(null, null, 0, []);
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>An HTTP handler that answers from a function and records every request (method, path, body).</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
        : this((request, body) => Task.FromResult(respond(request, body)))
    {
    }

    public ConcurrentQueue<(HttpMethod Method, string Path, string? Body)> Requests { get; } = new();

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: BridgeApiClient.JsonOptions) };

    public static HttpResponseMessage Problem(HttpStatusCode status, string title, string detail) =>
        new(status) { Content = new StringContent($"{{\"title\":\"{title}\",\"status\":{(int)status},\"detail\":\"{detail}\"}}", System.Text.Encoding.UTF8, "application/problem+json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        Requests.Enqueue((request.Method, path, body));
        return await respond(request, body);
    }
}

/// <summary>Builds the web app's clients against the in-process bridge.</summary>
internal static class WebClients
{
    public static BridgeApiClient Api(BridgeFactory factory) => new(factory.CreateClient());

    /// <summary>The event client over the test server's WebSocket; <paramref name="opened"/> collects every socket it opens.</summary>
    public static BridgeEventClient Events(BridgeFactory factory, BridgeState state, BridgeEventClientOptions? options = null, ConcurrentQueue<WebSocket>? opened = null, ILogger<BridgeEventClient>? logger = null, Func<Task>? beforeConnect = null) =>
        new(
            state,
            BridgeEventClient.EventsUri(factory.Server.BaseAddress),
            logger ?? NullLogger<BridgeEventClient>.Instance,
            options ?? new BridgeEventClientOptions { InitialRetryDelay = TimeSpan.FromMilliseconds(50), MaxRetryDelay = TimeSpan.FromMilliseconds(200) },
            async (uri, ct) =>
            {
                if (beforeConnect is not null)
                {
                    await beforeConnect();
                }

                var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(uri, ct);
                opened?.Enqueue(socket);
                return socket;
            });

    /// <summary>Polls until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? RecordedFake.Patience);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(25);
        }
    }
}

/// <summary>
/// The bUnit setup of the redesigned components (WORK-PLAN 2.1): everything a component needs, with the browser module
/// <c>./js/app.js</c> mocked so no component ever needs a real browser.
/// </summary>
internal static class UiTest
{
    /// <summary>
    /// Registers everything a component needs: BridgeState, a stub API (StubHandler; without <paramref name="respond"/>
    /// every request answers 404), an event client that never connects, BridgeCommands with I18n, I18n in
    /// <paramref name="lang"/> (constructed in that language; the process cultures are not touched: a test that asserts
    /// numbers formatted by the <see cref="Format"/> helpers opens a <see cref="CultureScope"/> for them), BrowserBridge
    /// over a mocked <c>"./js/app.js"</c> module (<see cref="SetupModule"/>). The caller disposes the context.
    /// </summary>
    public static (BunitContext Ctx, BridgeState State, StubHandler Api, I18n L, BunitJSModuleInterop Module) Create(
        string lang = "en",
        bool hoverCapable = true,
        Func<HttpRequestMessage, string?, Task<HttpResponseMessage>>? respond = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddLogging();
        var state = new BridgeState();
        ctx.Services.AddSingleton(state);
        var l = new I18n(lang);
        ctx.Services.AddSingleton(l);
        var module = SetupModule(ctx, lang, hoverCapable);
        ctx.Services.AddSingleton(sp => new BrowserBridge(sp.GetRequiredService<IJSRuntime>()));
        var api = new StubHandler(respond ?? ((_, _) => Task.FromResult(StubHandler.Problem(HttpStatusCode.NotFound, "Not found", "No stub answers this request."))));
        ctx.Services.AddSingleton(new BridgeApiClient(new HttpClient(api) { BaseAddress = new Uri("http://bridge.test/") }));
        ctx.Services.AddSingleton(NeverConnectingEvents(state));
        ctx.Services.AddSingleton<BridgeCommands>();
        return (ctx, state, api, l, module);
    }

    /// <summary>
    /// Mocks the <c>./js/app.js</c> module of <see cref="BrowserBridge"/>: <c>getTheme</c> answers "system",
    /// <c>hoverCapable</c> <paramref name="hoverCapable"/>, <c>getLang</c> <paramref name="lang"/>, <c>audit</c> "{}";
    /// <c>setTheme</c>, <c>setLang</c>, <c>setDocumentLang</c>, <c>measure</c> complete at once and are recorded; <c>onOutsideClick</c> and
    /// <c>rememberActive</c> answer nested module mocks with <c>dispose</c> / <c>restore</c>. Also serves
    /// <c>ElementReference.FocusAsync</c> (<c>Blazor._internal.domWrapper.focus</c>). Every call is recorded in the
    /// returned module's <c>Invocations</c> (nested handles record in their own).
    /// </summary>
    public static BunitJSModuleInterop SetupModule(BunitContext ctx, string lang = "en", bool hoverCapable = true)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var module = ctx.JSInterop.SetupModule(BrowserBridge.ModulePath);
        module.SetupVoid("setTheme", _ => true).SetVoidResult();
        module.Setup<string>("getTheme").SetResult("system");
        module.Setup<bool>("hoverCapable").SetResult(hoverCapable);
        module.Setup<string>("getLang").SetResult(lang);
        module.SetupVoid("setLang", _ => true).SetVoidResult();
        module.SetupVoid("setDocumentLang", _ => true).SetVoidResult();
        module.SetupVoid("measure", _ => true).SetVoidResult();
        module.SetupModule(invocation => invocation.Identifier == "onOutsideClick").SetupVoid("dispose").SetVoidResult();
        module.SetupModule(invocation => invocation.Identifier == "rememberActive").SetupVoid("restore").SetVoidResult();
        module.Setup<string>("audit").SetResult("{}");
        ctx.JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true).SetVoidResult();
        return module;
    }

    /// <summary>An event client for component tests: registered so DI resolves, never started, would fail to connect.</summary>
    public static BridgeEventClient NeverConnectingEvents(BridgeState state) => new(
        state,
        new Uri("ws://bridge.test/ws/events"),
        NullLogger<BridgeEventClient>.Instance,
        connect: (_, _) => Task.FromException<WebSocket>(new WebSocketException("not started in component tests")));
}

/// <summary>
/// Switches the culture of the calling flow to the culture of an app language (<see cref="I18n.Culture"/>): sets the
/// flow's own AsyncLocal <see cref="CultureInfo.CurrentCulture"/>/<see cref="CultureInfo.CurrentUICulture"/> only, so
/// tests running in parallel are not affected, and restores them on dispose. <see cref="I18n.SetLanguage"/> sets the
/// process-wide default-thread cultures instead (which this flow does not read inside the scope); the scope snapshots
/// those too and restores them on dispose, so a test that calls SetLanguage leaves the process as it found it. Such a
/// test belongs to <see cref="ProcessCultureCollection"/>. Usage: <c>using var _ = new CultureScope("de");</c>.
/// </summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public CultureScope(string code)
    {
        Culture = new I18n(code).Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    /// <summary>The culture in force inside the scope (de-DE for "de", …).</summary>
    public CultureInfo Culture { get; }

    /// <summary>
    /// Runs <paramref name="read"/> on a new thread that does not inherit the caller's ExecutionContext: a flow without an
    /// AsyncLocal culture, like every flow of the app, so it reads the default-thread cultures that
    /// <see cref="I18n.SetLanguage"/> sets.
    /// </summary>
    public static T InFreshFlow<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = read();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.UnsafeStart(); // no ExecutionContext capture: the thread has no AsyncLocal culture
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        }

        return result;
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}

/// <summary>
/// The test classes that call <see cref="I18n.SetLanguage"/> (directly or through <c>LanguageSelect</c>), which changes
/// the process-wide default-thread cultures: they run alone, after the parallel tests, so no other test reads a
/// culture they set.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessCultureCollection
{
    /// <summary>The collection's name, for <c>[Collection(ProcessCultureCollection.Name)]</c>.</summary>
    public const string Name = "Process culture";
}
