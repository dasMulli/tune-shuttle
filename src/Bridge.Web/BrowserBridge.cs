using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Bridge.Web;

/// <summary>
/// The app's only door to browser APIs (UI-SPEC 2.3): a thin wrapper over the ES module <c>wwwroot/js/app.js</c>, which
/// it imports lazily on the first call. Components inject this service instead of <see cref="IJSRuntime"/>, so bUnit
/// tests mock the module (<c>JSInterop.SetupModule("./js/app.js")</c>) and nothing else. A singleton (one per browser
/// tab); registered by <c>Program.AddBridgeClient</c>.
/// </summary>
public sealed class BrowserBridge(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>The module path passed to <c>import</c>; also what tests mock.</summary>
    public const string ModulePath = "./js/app.js";

    private Task<IJSObjectReference>? _module;

    /// <summary>The stored theme choice: <c>"light"</c>, <c>"dark"</c> or <c>"system"</c>.</summary>
    public async Task<string> GetThemeAsync() => await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<string>("getTheme").ConfigureAwait(false);

    /// <summary>
    /// Raised at the end of every <see cref="SetThemeAsync"/> with the new choice, so every control that shows the
    /// choice (the top-bar theme picker, the Settings Appearance card) follows a choice made in another one. Raised even
    /// when the module call fails, so the controls still agree with each other. Handlers may run on any thread.
    /// </summary>
    public event Action<string>? ThemeChanged;

    /// <summary>
    /// Applies and stores a theme choice (<c>data-theme</c>, the theme-color override, localStorage), then raises
    /// <see cref="ThemeChanged"/>.
    /// </summary>
    public async Task SetThemeAsync(string theme)
    {
        try
        {
            await (await ModuleAsync().ConfigureAwait(false)).InvokeVoidAsync("setTheme", theme).ConfigureAwait(false);
        }
        finally
        {
            ThemeChanged?.Invoke(theme);
        }
    }

    /// <summary>True when the primary pointer can hover (<c>(hover: hover)</c>); false on touch screens.</summary>
    public async Task<bool> HoverCapableAsync() => await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<bool>("hoverCapable").ConfigureAwait(false);

    /// <summary>
    /// The language to use: <c>?lang=</c>, then the stored choice, then the browser languages; <c>""</c> when none is
    /// supported (the caller falls back to <c>"en"</c>).
    /// </summary>
    public async Task<string> GetLangAsync() => await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<string>("getLang").ConfigureAwait(false);

    /// <summary>Stores the language choice (<c>""</c> removes it) and sets <c>&lt;html lang&gt;</c>.</summary>
    public async Task SetLangAsync(string code) => await (await ModuleAsync().ConfigureAwait(false)).InvokeVoidAsync("setLang", code).ConfigureAwait(false);

    /// <summary>
    /// Calls the <see cref="JSInvokableAttribute"/> method <paramref name="method"/> on <paramref name="target"/> for
    /// every document click outside <paramref name="element"/>. Dispose the result to remove the listener.
    /// </summary>
    public async Task<IAsyncDisposable> OnOutsideClickAsync<T>(ElementReference element, DotNetObjectReference<T> target, string method)
        where T : class
    {
        var module = await ModuleAsync().ConfigureAwait(false);
        var handle = await module.InvokeAsync<IJSObjectReference>("onOutsideClick", element, target, method).ConfigureAwait(false);
        return new JsHandle(handle);
    }

    /// <summary>
    /// Keeps the given <c>KeyboardEvent.key</c> values from doing their default action (scrolling the page)
    /// while they are pressed inside <paramref name="element"/>; Blazor's own key handlers still see them. Blazor's
    /// <c>@onkeydown:preventDefault</c> cannot pick keys. Dispose the result to remove the listener.
    /// </summary>
    public async Task<IAsyncDisposable> PreventKeysAsync(ElementReference element, string[] keys)
    {
        var module = await ModuleAsync().ConfigureAwait(false);
        var handle = await module.InvokeAsync<IJSObjectReference>("preventKeys", element, keys).ConfigureAwait(false);
        return new JsHandle(handle);
    }

    /// <summary>
    /// Publishes the rendered height of the element matching <paramref name="selector"/> as the CSS custom property
    /// <paramref name="cssVar"/> on <c>&lt;html&gt;</c> and keeps it current (e.g. <c>("header.topbar", "--topbar-h")</c>).
    /// </summary>
    public async Task MeasureAsync(string selector, string cssVar) => await (await ModuleAsync().ConfigureAwait(false)).InvokeVoidAsync("measure", selector, cssVar).ConfigureAwait(false);

    /// <summary>Remembers the focused element; invoke <c>restore</c> on the result to focus it again.</summary>
    public async Task<IJSObjectReference> RememberActiveAsync() => await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<IJSObjectReference>("rememberActive").ConfigureAwait(false);

    /// <summary>The layout audit of UI-SPEC 5.6 as a JSON string: <c>{ overflow, small, unnamed }</c>.</summary>
    public async Task<string> AuditAsync() => await (await ModuleAsync().ConfigureAwait(false)).InvokeAsync<string>("audit").ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true } module)
        {
            _module = null;
            try
            {
                await module.Result.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // the page is gone; nothing to release
            }
        }
    }

    private Task<IJSObjectReference> ModuleAsync()
    {
        // A failed import (offline, a test without the module mock) is not cached: the next call tries again.
        if (_module is null || _module.IsFaulted || _module.IsCanceled)
        {
            _module = js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
        }

        return _module;
    }

    /// <summary>The <c>{ dispose() }</c> object of <c>onOutsideClick</c> / <c>preventKeys</c> as an <see cref="IAsyncDisposable"/>.</summary>
    private sealed class JsHandle(IJSObjectReference handle) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await handle.InvokeVoidAsync("dispose").ConfigureAwait(false);
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // the page is gone; the listener went with it
            }
        }
    }
}
