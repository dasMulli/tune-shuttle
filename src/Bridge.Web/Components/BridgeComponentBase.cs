using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace Bridge.Web.Components;

/// <summary>
/// Base of the pages and components that show <see cref="BridgeState"/>: re-renders on <see cref="BridgeState.Changed"/>,
/// offers a <see cref="Lifetime"/> token that is cancelled when the component goes away, and <see cref="Observe"/> for
/// work that is not awaited by a handler (timers), so no task fails unobserved.
/// </summary>
public abstract class BridgeComponentBase : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    /// <summary>The state container.</summary>
    [Inject]
    protected BridgeState State { get; set; } = default!;

    /// <summary>The actions.</summary>
    [Inject]
    protected BridgeCommands Commands { get; set; } = default!;

    /// <summary>Logger factory.</summary>
    [Inject]
    protected ILoggerFactory LoggerFactory { get; set; } = default!;

    /// <summary>Cancelled when the component is disposed.</summary>
    protected CancellationToken Lifetime => _lifetime.Token;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        State.Changed -= OnStateChanged;
        _lifetime.Cancel();
        Dispose(true);
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
    }

    /// <summary>Called on the renderer's context after the state changed, before the component re-renders.</summary>
    protected virtual void OnBridgeStateChanged()
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

    /// <summary>Runs <paramref name="tick"/> every <paramref name="period"/> until the component goes away (observed).</summary>
    protected void Every(TimeSpan period, Func<CancellationToken, Task> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        Observe(LoopAsync(period, tick, Lifetime));
    }

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

    private static async Task LoopAsync(TimeSpan period, Func<CancellationToken, Task> tick, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(period);
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
