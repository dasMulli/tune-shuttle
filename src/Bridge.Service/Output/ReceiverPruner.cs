namespace Bridge.Service.Output;

/// <summary>
/// The automatic pruning of offline receivers (<see cref="ReceiverJanitor.PruneAsync"/>): first <see cref="StartupDelay"/>
/// after start-up, so discovery has had time to see the receivers that are on the network, then every
/// <see cref="Interval"/>. A failed run, a cancellation that is not the host stopping included (a timeout inside the
/// player), is logged and the next one tries again; only the host stopping ends the loop.
/// </summary>
public sealed class ReceiverPruner : BackgroundService
{
    /// <summary>Delay of the first run after start-up.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    /// <summary>Delay between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _prune;
    private readonly TimeProvider _time;
    private readonly ILogger<ReceiverPruner> _logger;

    /// <summary>Creates the pruner over the janitor.</summary>
    public ReceiverPruner(ReceiverJanitor janitor, TimeProvider time, ILogger<ReceiverPruner> logger)
        : this((janitor ?? throw new ArgumentNullException(nameof(janitor))).PruneAsync, time, logger)
    {
    }

    /// <summary>Creates the pruner over a pruning run (tests).</summary>
    internal ReceiverPruner(Func<CancellationToken, Task<IReadOnlyList<string>>> prune, TimeProvider time, ILogger<ReceiverPruner> logger)
    {
        ArgumentNullException.ThrowIfNull(prune);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _prune = prune;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, _time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var removed = await _prune(stoppingToken).ConfigureAwait(false);
                    if (removed.Count > 0)
                    {
                        _logger.LogInformation("Pruning forgot {Count} offline receiver(s)", removed.Count);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Also an OperationCanceledException of a token other than stoppingToken: an unhandled exception
                    // here would stop the whole host (BackgroundServiceExceptionBehavior.StopHost), not just this run.
                    _logger.LogWarning(ex, "Pruning offline receivers failed; trying again in {Interval}", Interval);
                }

                await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // stopping
        }
    }
}
