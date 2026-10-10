using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Cli;

/// <summary>How a command logs.</summary>
/// <param name="Verbose">Console at Debug instead of Information (the decrypted wire log is at Debug).</param>
/// <param name="LogFile">Also write everything at Debug to this file (appended), or null.</param>
public sealed record CliLogOptions(bool Verbose, string? LogFile);

/// <summary>Console logging plus the optional Debug log file (WORK-PLAN.md section 4: every live run logs the complete decrypted wire exchange).</summary>
public static class CliLogging
{
    /// <summary>Creates the logger factory for <paramref name="options"/>.</summary>
    public static ILoggerFactory Create(CliLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
            });
            builder.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(null, options.Verbose ? LogLevel.Debug : LogLevel.Information);
            if (options.LogFile is { } file)
            {
                builder.AddProvider(new FileLoggerProvider(file));
            }
        });
    }

    /// <summary>
    /// The repository's <c>artifacts/live</c> directory (WORK-PLAN.md section 4), found by walking up from the current
    /// directory and then from the executable to <c>TuneShuttle.sln</c>; <c>./artifacts/live</c> when neither has it.
    /// </summary>
    public static string LiveLogDirectory()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")))
                {
                    return Path.Combine(dir.FullName, "artifacts", "live");
                }
            }
        }

        return Path.Combine(Environment.CurrentDirectory, "artifacts", "live");
    }
}

/// <summary>Appends every log line (all levels) to one file; thread-safe; the file is flushed per line so a crash loses nothing.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    /// <summary>Opens (appends to) <paramref name="path"/>, creating its directory.</summary>
    public FileLoggerProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        FilePath = full;
        _writer = new StreamWriter(new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Write($"--- {DateTimeOffset.Now:O} airplay2 {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
    }

    /// <summary>The file.</summary>
    public string FilePath { get; }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Writes one raw line.</summary>
    public void Write(string line)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _writer.WriteLine(line);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            owner.Write(string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:HH:mm:ss.fff} {Short(logLevel)} {category}: {text}{(exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message)}"));
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            _ => "crit",
        };
    }
}

/// <summary>Forwards to a caller-owned factory and additionally to a log file it owns; disposing closes only the file.</summary>
public sealed class TeeLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly FileLoggerProvider? _file;

    /// <summary>Creates the tee; <paramref name="logFile"/> null forwards only.</summary>
    public TeeLoggerFactory(ILoggerFactory inner, string? logFile)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _file = logFile is null ? null : new FileLoggerProvider(logFile);
    }

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        var inner = _inner.CreateLogger(categoryName);
        return _file is null ? inner : new Tee(inner, _file.CreateLogger(categoryName));
    }

    /// <inheritdoc />
    public void Dispose() => _file?.Dispose();

    private sealed class Tee(ILogger first, ILogger second) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => first.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => first.IsEnabled(logLevel) || second.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (first.IsEnabled(logLevel))
            {
                first.Log(logLevel, eventId, state, exception, formatter);
            }

            second.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
