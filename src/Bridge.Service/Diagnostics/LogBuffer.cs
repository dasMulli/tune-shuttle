using Bridge.Contracts;

namespace Bridge.Service.Diagnostics;

/// <summary>
/// Logger provider for the diagnostics page (WORK-PLAN.md bridge-service "diagnostics (log ring buffer ...)"): keeps the
/// most recent lines in memory and hands every line to a listener (the <see cref="Api.EventHub"/>, which streams them to
/// web clients that subscribed to the log). Has no dependencies, so it can be created before the logger factory.
/// </summary>
public sealed class LogBuffer : ILoggerProvider
{
    /// <summary>Lines kept.</summary>
    public const int Capacity = 500;

    private readonly Lock _lock = new();
    private readonly Queue<BridgeEvent.LogLine> _lines = new(Capacity);
    private volatile Action<BridgeEvent.LogLine>? _listener;

    /// <summary>Receives each line as it is logged (Information and above). Must not block and must not log.</summary>
    public Action<BridgeEvent.LogLine>? Listener
    {
        get => _listener;
        set => _listener = value;
    }

    /// <summary>The most recent lines, oldest first.</summary>
    public IReadOnlyList<BridgeEvent.LogLine> Recent(int count = Capacity)
    {
        lock (_lock)
        {
            return _lines.Skip(Math.Max(0, _lines.Count - count)).ToList();
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() => _listener = null;

    /// <summary>Short level names as the log files and the web app show them.</summary>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };

    private void Add(BridgeEvent.LogLine line)
    {
        lock (_lock)
        {
            if (_lines.Count == Capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
        }

        _listener?.Invoke(line);
    }

    private sealed class BufferLogger(LogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // Debug and Trace stay out of the ring: wire-level detail would push the useful lines out within seconds.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = message + " | " + exception.GetType().Name + ": " + exception.Message;
            }

            owner.Add(new BridgeEvent.LogLine(DateTimeOffset.UtcNow, LevelName(logLevel), category, message));
        }
    }
}
