using System.Collections.Concurrent;
using System.Threading.Channels;
using Airplay2.Sender.Discovery;

namespace Bridge.Service.Output;

/// <summary>
/// An <see cref="IReceiverBrowser"/> over a list the host maintains instead of mDNS: the development switch
/// <c>Bridge:FakeReceivers</c> and the tests put in-process fake receivers here (their TXT records and loopback endpoint),
/// so the bridge runs end to end without a LAN.
/// </summary>
public sealed class StaticReceiverBrowser : IReceiverBrowser
{
    private readonly ConcurrentDictionary<string, DiscoveredReceiver> _receivers = new(StringComparer.Ordinal);
    private readonly Channel<ReceiverEvent> _events = Channel.CreateBounded<ReceiverEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <inheritdoc />
    public ChannelReader<ReceiverEvent> Events => _events.Reader;

    /// <summary>Adds or replaces a receiver and raises Found/Updated.</summary>
    public void Add(DiscoveredReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        DiscoveredReceiver? previous = null;
        _receivers.AddOrUpdate(receiver.Id, receiver, (_, old) =>
        {
            previous = old;
            return receiver;
        });
        _events.Writer.TryWrite(previous is null ? new ReceiverEvent.Found(receiver) : new ReceiverEvent.Updated(receiver, previous));
    }

    /// <summary>Removes a receiver and raises Lost.</summary>
    public void Remove(string receiverId)
    {
        if (_receivers.TryRemove(receiverId, out var receiver))
        {
            _events.Writer.TryWrite(new ReceiverEvent.Lost(receiver));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredReceiver> Snapshot() => _receivers.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task RefreshAsync(TimeSpan settle, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task<DiscoveredReceiver?> FindAsync(string id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var match = _receivers.Values.FirstOrDefault(r => r.Id == id || string.Equals(r.Name, id, StringComparison.OrdinalIgnoreCase));
            if (match is not null || DateTime.UtcNow >= deadline)
            {
                return match;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
