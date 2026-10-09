namespace Bridge.Web;

/// <summary>
/// Sends the latest value of a fast-changing control (a volume slider while it is dragged) without flooding the bridge:
/// at most one request is in flight; values that arrive meanwhile replace each other and only the newest is sent when
/// the request finishes. The service coalesces receiver volume changes the same way (SPEC-control.md 5.1), so the last
/// position always wins and intermediate ones may be skipped.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public sealed class CoalescingSender<T>
{
    private readonly Func<T, CancellationToken, Task> _send;
    private readonly Lock _gate = new();
    private T _latest = default!;
    private bool _pending;
    private bool _active;
    private Task _running = Task.CompletedTask;

    /// <summary>Creates the sender.</summary>
    /// <param name="send">Sends one value; its exceptions surface from the <see cref="SubmitAsync"/> call whose run failed.</param>
    public CoalescingSender(Func<T, CancellationToken, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
    }

    /// <summary>True while a value is being sent or waits to be sent.</summary>
    public bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _pending || _active;
            }
        }
    }

    /// <summary>Values actually sent.</summary>
    public int Sent { get; private set; }

    /// <summary>
    /// Queues <paramref name="value"/> and returns the task of the run that will send it (or a newer value). Awaiting it
    /// observes the run's failure.
    /// </summary>
    public Task SubmitAsync(T value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _latest = value;
            _pending = true;
            if (!_active)
            {
                // The run decides to stop under this lock, so a value queued here is never left behind.
                _active = true;
                _running = RunAsync(cancellationToken);
            }

            return _running;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Yield(); // let a burst of input events land before the first send
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (!_pending)
                {
                    _active = false;
                    return;
                }

                value = _latest;
                _pending = false;
            }

            try
            {
                await _send(value, cancellationToken).ConfigureAwait(false);
                Sent++;
            }
            catch
            {
                lock (_gate)
                {
                    _pending = false; // a failed run drops what queued behind it; the next input starts afresh
                    _active = false;
                }

                throw;
            }
        }
    }
}
