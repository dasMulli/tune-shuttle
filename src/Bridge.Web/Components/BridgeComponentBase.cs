using Bridge.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Bridge.Web.Components;

/// <summary>
/// Base of the pages and components that show <see cref="BridgeState"/>: re-renders on <see cref="BridgeState.Changed"/>
/// and on <see cref="I18n.Changed"/> (a language switch re-renders the whole app without a reload), exposes the catalog as
/// <see cref="L"/>, offers a <see cref="Lifetime"/> token that is cancelled when the component goes away, and <see cref="Observe"/> for
/// work that is not awaited by a handler (timers), so no task fails unobserved.
/// </summary>
public abstract class BridgeComponentBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    // Captured once: a CancellationToken stays valid (and answers IsCancellationRequested) after its source is
    // disposed, whereas _lifetime.Token would throw ObjectDisposedException, e.g. inside a catch filter after an await.
    private readonly CancellationToken _lifetimeToken;
    private bool _disposed;

    /// <summary>Creates the component and its lifetime token.</summary>
    protected BridgeComponentBase()
    {
        _lifetimeToken = _lifetime.Token;
    }

    /// <summary>The state container.</summary>
    [Inject]
    protected BridgeState State { get; set; } = default!;

    /// <summary>The actions.</summary>
    [Inject]
    protected BridgeCommands Commands { get; set; } = default!;

    /// <summary>The message catalog in the current language (<c>L["key"]</c>, <c>L.Format(…)</c>, <c>L.Plural(…)</c>).</summary>
    [Inject]
    protected I18n L { get; set; } = default!;

    /// <summary>Logger factory.</summary>
    [Inject]
    protected ILoggerFactory LoggerFactory { get; set; } = default!;

    /// <summary>Cancelled when the component is disposed.</summary>
    protected CancellationToken Lifetime => _lifetimeToken;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        State.Changed -= OnStateChanged;
        L.Changed -= OnLanguageSwitched;
        _lifetime.Cancel();
        Dispose(true);
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
        L.Changed += OnLanguageSwitched;
    }

    /// <summary>Called on the renderer's context after the state changed, before the component re-renders.</summary>
    protected virtual void OnBridgeStateChanged()
    {
    }

    /// <summary>Called on the renderer's context after the language changed, before the component re-renders.</summary>
    protected virtual void OnLanguageChanged()
    {
    }

    /// <summary>Releases what a derived component holds (called once, after <see cref="Lifetime"/> is cancelled).</summary>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Logs a failure of <paramref name="task"/> instead of leaving it unobserved; cancellation is ignored.</summary>
    protected void Observe(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        var logger = LoggerFactory.CreateLogger(GetType());
        _ = task.ContinueWith(
            static (t, state) =>
            {
                if (t.Exception?.GetBaseException() is { } ex and not OperationCanceledException)
                {
                    ((ILogger)state!).LogError(ex, "Background work of a component failed");
                }
            },
            logger,
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Runs <paramref name="tick"/> every <paramref name="period"/> of the state's clock (<see cref="BridgeState.Now"/>)
    /// until the component goes away (observed).
    /// </summary>
    protected void Every(TimeSpan period, Func<CancellationToken, Task> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        Observe(LoopAsync(period, State.Time, tick, Lifetime));
    }

    /// <summary>
    /// Redraws every <see cref="Format.LastSeenRefresh"/> while an offline receiver carries a last-seen time: the
    /// relative phrase ("last seen 5 minutes ago", <see cref="Format.LastSeen"/>) ages with the clock, not with events.
    /// </summary>
    protected void RedrawLastSeenPhrases() =>
        Every(Format.LastSeenRefresh, async _ =>
        {
            if (State.Receivers.Any(r => !r.Online && r.LastSeen is not null))
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }
        });

    private void OnStateChanged()
    {
        if (!_disposed)
        {
            Observe(InvokeAsync(() =>
            {
                OnBridgeStateChanged();
                StateHasChanged();
            }));
        }
    }

    private void OnLanguageSwitched()
    {
        if (!_disposed)
        {
            Observe(InvokeAsync(() =>
            {
                OnLanguageChanged();
                StateHasChanged();
            }));
        }
    }

    private static async Task LoopAsync(TimeSpan period, TimeProvider time, Func<CancellationToken, Task> tick, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(period, time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await tick(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the component went away
        }
    }
}
