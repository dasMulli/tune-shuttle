using System.Collections.Concurrent;

namespace Bridge.Service;

/// <summary>
/// Fire-and-forget work a component started: each task is observed (failures logged, never lost) and remembered until it
/// completes, so the owner can wait for all of it when it stops.
/// </summary>
/// <param name="logger">The owner's logger.</param>
/// <param name="isExpectedFailure">Failures logged as warnings (network, receiver, store); anything else is a bug and logged as an error with its stack.</param>
internal sealed class BackgroundTasks(ILogger logger, Func<Exception, bool> isExpectedFailure)
{
    /// <summary>Background work may start more background work (a retry, a reconnect); a drain waits this many generations.</summary>
    private const int DrainRounds = 8;

    private readonly ConcurrentDictionary<Task, string> _tasks = new();

    /// <summary>Observes <paramref name="task"/> and keeps it until it completes; the returned task never faults.</summary>
    public Task Track(Task task, string what)
    {
        var observed = ObserveAsync(task, what);
        Add(observed, what);
        return observed;
    }

    /// <summary>Keeps a task whose result a consumer awaits (and so observes) until it completes; returns it unchanged.</summary>
    public Task<T> TrackResult<T>(Task<T> task, string what)
    {
        Add(task, what);
        return task;
    }

    /// <summary>Awaits <paramref name="task"/> and logs its failure; cancellation is a normal end. Never throws.</summary>
    public async Task ObserveAsync(Task task, string what)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // replaced or stopping
        }
        catch (Exception ex) when (isExpectedFailure(ex))
        {
            logger.LogWarning("{What} failed: {Error}", what, ex.Message);
        }
        catch (Exception ex)
        {
            // Background work is observed here and nowhere else: an unexpected failure is a bug, logged with its stack.
            logger.LogError(ex, "{What} failed unexpectedly", what);
        }
    }

    /// <summary>Waits for every tracked task, including the ones they start meanwhile (bounded by <see cref="DrainRounds"/>).</summary>
    public async Task DrainAsync()
    {
        for (var round = 0; round < DrainRounds; round++)
        {
            var pending = _tasks.Keys.ToArray();
            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Tracked tasks are observed by Track's observer or by the consumer of TrackResult's task.
            }
        }

        if (!_tasks.IsEmpty)
        {
            logger.LogWarning("{Count} background task(s) still running after the drain: {What}", _tasks.Count, string.Join(", ", _tasks.Values));
        }
    }

    private void Add(Task task, string what)
    {
        _tasks[task] = what;
        _ = task.ContinueWith(
            static (t, state) => ((ConcurrentDictionary<Task, string>)state!).TryRemove(t, out _),
            _tasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
