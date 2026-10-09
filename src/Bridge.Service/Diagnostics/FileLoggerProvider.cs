using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Bridge.Service.Diagnostics;

/// <summary>
/// Daily log files for the Windows Service (docs/PLAN.md "Logging to console (container) and to files (Windows)"):
/// <c>&lt;DataDirectory&gt;/logs/bridge-yyyyMMdd.log</c>, written by one background task from a bounded channel (a slow
/// disk drops the oldest lines instead of blocking the caller), files older than
/// <see cref="BridgeOptions.LogRetentionDays"/> removed at each roll. Inert on other platforms or when
/// <see cref="BridgeOptions.FileLogging"/> is off.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private readonly Channel<string>? _lines;
    private readonly Task _writer = Task.CompletedTask;
    private readonly string _directory;
    private readonly int _retentionDays;
    private int _disposed;

    /// <summary>Creates the provider; starts the writer when file logging applies.</summary>
    public FileLoggerProvider(IOptions<BridgeOptions> options, BridgePaths paths)
        : this(Required(options).Value, Required(paths).LogDirectory)
    {
    }

    private FileLoggerProvider(BridgeOptions options, string directory)
        : this(options.FileLogging && OperatingSystem.IsWindows(), directory, options.LogRetentionDays)
    {
    }

    /// <summary>Creates the provider for a directory (tests).</summary>
    internal FileLoggerProvider(bool enabled, string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = Math.Max(1, retentionDays);
        if (!enabled)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _writer = WriteLoopAsync(_lines.Reader);
    }

    private static T Required<T>(T value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value;
    }

    /// <summary>True when lines are written.</summary>
    public bool IsEnabled => _lines is not null;

    /// <summary>The file a line logged at <paramref name="time"/> goes to.</summary>
    public string FileFor(DateTimeOffset time) => Path.Combine(_directory, "bridge-" + time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        // Synchronous disposal only stops accepting lines; DisposeAsync (what the host calls) also drains the writer.
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lines?.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _writer.ConfigureAwait(false);
    }

    private void Enqueue(string line) => _lines?.Writer.TryWrite(line);

    private async Task WriteLoopAsync(ChannelReader<string> reader)
    {
        StreamWriter? writer = null;
        string? currentPath = null;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var path = FileFor(DateTimeOffset.UtcNow);
                if (path != currentPath)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    writer = OpenWriter(path);
                    currentPath = path;
                    PruneOldFiles();
                }

                while (reader.TryRead(out var line))
                {
                    await writer!.WriteLineAsync(line).ConfigureAwait(false);
                }

                await writer!.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // The disk is gone or full: file logging stops; console logging and the in-memory buffer carry on.
        }
        catch (UnauthorizedAccessException)
        {
            // No write access to the data directory: same as above.
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static StreamWriter OpenWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void PruneOldFiles()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "bridge-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // in use by another process (an editor): try again at the next roll
            }
            catch (UnauthorizedAccessException)
            {
                // not ours to delete
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder(128)
                .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
                .Append(" [").Append(LogBuffer.LevelName(logLevel)).Append("] ")
                .Append(category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            owner.Enqueue(builder.ToString());
        }
    }
}
