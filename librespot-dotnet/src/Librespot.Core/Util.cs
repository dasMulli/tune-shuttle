// Port of core/src/util.rs.
//
// keep_flushing (a future that keeps flushing a futures `Sink`) is not ported: its only consumer is the dealer's
// websocket loop, and System.Net.WebSockets.ClientWebSocket has no buffered sink that needs flushing.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace Librespot.Core;

/// <summary>
/// An abortable spawned task (upstream <c>tokio::task::JoinHandle&lt;T&gt;</c>). .NET tasks cannot be aborted from
/// the outside, so the handle owns the <see cref="CancellationTokenSource"/> whose token the task observes;
/// <see cref="Abort"/> cancels it.
/// </summary>
/// <typeparam name="T">The task's result type.</typeparam>
public sealed class TaskHandle<T>
{
    private readonly CancellationTokenSource _cts;

    private TaskHandle(Task<T> task, CancellationTokenSource cts)
    {
        Task = task;
        _cts = cts;
    }

    /// <summary>The running task.</summary>
    public Task<T> Task { get; }

    /// <summary>Whether the task has completed (upstream <c>JoinHandle::is_finished</c>).</summary>
    public bool IsFinished => Task.IsCompleted;

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool (upstream <c>tokio::spawn</c>). The token passed to
    /// <paramref name="work"/> is cancelled by <see cref="Abort"/> or when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static TaskHandle<T> Spawn(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task = System.Threading.Tasks.Task.Run(() => work(cts.Token), cts.Token);

        // The linked source registers a callback on the caller's (often long-lived) token; release it once the task
        // is done so respawned tasks do not pile up registrations. Abort tolerates the disposed source.
        _ = task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            cts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return new TaskHandle<T>(task, cts);
    }

    /// <summary>Requests cancellation of the task (upstream <c>JoinHandle::abort</c>).</summary>
    public void Abort()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        // An aborted task's exception (usually the cancellation itself) is never awaited by anyone; observe it so
        // it does not surface as an UnobservedTaskException.
        _ = Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Awaits the task (upstream <c>impl Future for JoinHandle</c>).</summary>
    public System.Runtime.CompilerServices.TaskAwaiter<T> GetAwaiter() => Task.GetAwaiter();
}

/// <summary>
/// Upstream <c>CancelOnDrop&lt;T&gt;</c>: awaitable wrapper around a <see cref="TaskHandle{T}"/> that aborts the task
/// when disposed (the C# replacement for <c>Drop</c>).
/// </summary>
/// <typeparam name="T">The task's result type.</typeparam>
public sealed class CancelOnDrop<T>(TaskHandle<T> handle) : IDisposable
{
    /// <summary>The wrapped handle.</summary>
    public TaskHandle<T> Handle { get; } = handle;

    /// <summary>Awaits the wrapped task.</summary>
    public System.Runtime.CompilerServices.TaskAwaiter<T> GetAwaiter() => Handle.GetAwaiter();

    /// <summary>Aborts the wrapped task.</summary>
    public void Dispose() => Handle.Abort();
}

/// <summary>
/// Upstream <c>TimeoutOnDrop&lt;T&gt;</c>: awaitable wrapper around a <see cref="TaskHandle{T}"/>. When disposed while
/// the task is still running, the task gets <c>timeout</c> to finish on its own in the background and is aborted
/// afterwards.
/// </summary>
/// <typeparam name="T">The task's result type.</typeparam>
public sealed class TimeoutOnDrop<T> : IDisposable
{
    private TaskHandle<T>? _handle;
    private readonly TimeSpan _timeout;

    /// <summary>Upstream <c>TimeoutOnDrop::new(handle, timeout)</c>.</summary>
    public TimeoutOnDrop(TaskHandle<T> handle, TimeSpan timeout)
    {
        _handle = handle;
        _timeout = timeout;
    }

    /// <summary>Takes the handle out, so disposing no longer affects it (upstream <c>take</c>).</summary>
    public TaskHandle<T>? Take() => Interlocked.Exchange(ref _handle, null);

    /// <summary>Awaits the wrapped task; afterwards disposing is a no-op (upstream <c>impl Future</c>).</summary>
    public async Task<T> WaitAsync()
    {
        var handle = _handle ?? throw new InvalidOperationException("Polled after ready");
        var result = await handle.Task.ConfigureAwait(false);
        _handle = null;
        return result;
    }

    /// <summary>Awaits the wrapped task.</summary>
    public System.Runtime.CompilerServices.TaskAwaiter<T> GetAwaiter() => WaitAsync().GetAwaiter();

    /// <summary>
    /// Upstream <c>Drop</c>: if the task has not finished yet, give it <c>timeout</c> to finish in the background and
    /// abort it afterwards.
    /// </summary>
    public void Dispose()
    {
        var handle = Take();
        if (handle is null || handle.IsFinished)
        {
            // Already taken or already finished.
            return;
        }

        _ = WaitThenAbortAsync(handle, _timeout);
    }

    private static async Task WaitThenAbortAsync(TaskHandle<T> handle, TimeSpan timeout)
    {
        try
        {
            await handle.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            handle.Abort();
        }
        catch (Exception)
        {
            // The task failed on its own; upstream discards the JoinHandle result here as well.
        }
    }
}

/// <summary>Monotonic, wrapping sequence-number generator (upstream <c>SeqGenerator&lt;T&gt;</c>).</summary>
public struct SeqGenerator<T> where T : IBinaryInteger<T>
{
    private T _value;

    public SeqGenerator(T value)
    {
        _value = value;
    }

    /// <summary>Returns the current value and advances by one (wrapping on overflow).</summary>
    public T Get()
    {
        var current = _value;
        unchecked
        {
            _value = current + T.One;
        }

        return current;
    }
}

/// <summary>URL helpers of upstream <c>util</c>.</summary>
public static class UrlUtil
{
    /// <summary>Upstream <c>get_next_query_separator</c>: "?" when the URL has no query yet, otherwise "&amp;".</summary>
    public static string GetNextQuerySeparator(string url) => url.Contains('?') ? "&" : "?";
}

/// <summary>Hashcash solver used by the login5 and client-token challenges.</summary>
public static class HashCash
{
    /// <summary>After this many seconds the challenge expires (upstream <c>TIMEOUT</c>).</summary>
    public const int TimeoutSeconds = 5;

    /// <summary>
    /// Upstream <c>solve_hash_cash(ctx, prefix, length, dst)</c>: with <c>target</c> = bytes 12..20 of SHA-1(ctx) as a
    /// big-endian i64, finds the first <c>counter</c> (from 0) such that bytes 12..20 of
    /// SHA-1(prefix || BE(target + counter) || BE(counter)) have at least <paramref name="length"/> trailing zero
    /// bits, and writes the 16-byte suffix <c>BE(target + counter) || BE(counter)</c> to <paramref name="dst"/>.
    /// </summary>
    /// <returns>The time it took to solve the challenge (login5 reports it back to the server).</returns>
    /// <exception cref="LibrespotException">Kind <see cref="ErrorKind.DeadlineExceeded"/> after <see cref="TimeoutSeconds"/> seconds.</exception>
    public static TimeSpan Solve(ReadOnlySpan<byte> ctx, ReadOnlySpan<byte> prefix, int length, Span<byte> dst)
    {
        // Upstream `copy_from_slice` panics on a length mismatch; fail fast instead of after the search.
        ArgumentOutOfRangeException.ThrowIfNotEqual(dst.Length, 16, nameof(dst));

        var start = Stopwatch.GetTimestamp();

        Span<byte> md = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(ctx, md);

        long counter = 0;
        var target = BinaryPrimitives.ReadInt64BigEndian(md[12..20]);

        Span<byte> input = new byte[prefix.Length + 16];
        prefix.CopyTo(input);
        var suffix = input[prefix.Length..];

        while (true)
        {
            if (Stopwatch.GetElapsedTime(start).TotalSeconds >= TimeoutSeconds)
            {
                throw LibrespotException.DeadlineExceeded($"{TimeoutSeconds} seconds expired");
            }

            BinaryPrimitives.WriteInt64BigEndian(suffix, unchecked(target + counter));
            BinaryPrimitives.WriteInt64BigEndian(suffix[8..], counter);

            SHA1.HashData(input, md);

            if (BitOperations.TrailingZeroCount(BinaryPrimitives.ReadInt64BigEndian(md[12..20])) >= (uint)length)
            {
                break;
            }

            counter++;
        }

        suffix.CopyTo(dst);

        return Stopwatch.GetElapsedTime(start);
    }
}
