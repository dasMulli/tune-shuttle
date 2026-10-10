using System.Collections.Concurrent;
using System.Threading.Channels;
using Airplay2.Sender.Playback;
using Bridge.Contracts;

namespace Bridge.Service.Output;

/// <summary>What the bridge plays to: a group, a single receiver, or nothing.</summary>
/// <param name="GroupId">The selected group, or null.</param>
/// <param name="ReceiverId">The single selected receiver, or null (exclusive with <paramref name="GroupId"/>).</param>
public sealed record OutputSelection(string? GroupId, string? ReceiverId)
{
    /// <summary>Nothing selected: audio is discarded (docs/ARCHITECTURE.md, "Design decisions").</summary>
    public static OutputSelection None { get; } = new(null, null);

    /// <summary>True when nothing is selected.</summary>
    public bool IsNone => GroupId is null && ReceiverId is null;
}

/// <summary>Connection state of one receiver as the output manager sees it, with the last error.</summary>
/// <param name="State">The state.</param>
/// <param name="Error">Why the last connect failed or the receiver was lost, or null.</param>
public sealed record ReceiverRuntime(ReceiverConnectionState State, string? Error);

/// <summary>The Spotify side as the API reports it.</summary>
/// <param name="Connected">A Spotify session is up.</param>
/// <param name="User">The account's canonical user name.</param>
/// <param name="DeviceName">The Spotify Connect device name currently announced.</param>
public sealed record SpotifyStatus(bool Connected, string? User, string DeviceName);

/// <summary>
/// Process-wide runtime state shared by the output manager, the inventory, the Spotify host and the API: per-receiver
/// connection states, the selected output, the Spotify status and the now-playing view, plus a reconnect signal. Holds no
/// I/O; every member is thread-safe.
/// </summary>
public sealed class RuntimeState
{
    private readonly ConcurrentDictionary<string, ReceiverRuntime> _receivers = new(StringComparer.Ordinal);
    private readonly Channel<string> _reconnects = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile OutputSelection _selection = OutputSelection.None;
    private volatile SpotifyStatus _spotify = new(false, null, string.Empty);
    private volatile PlaybackDto _playback = new(false, null, null, null, null, 0, 0, null, 0);
    private volatile GroupPlayerEvent.Statistics? _statistics;

    /// <summary>The selected output.</summary>
    public OutputSelection Selection
    {
        get => _selection;
        set => _selection = value ?? OutputSelection.None;
    }

    /// <summary>The Spotify status.</summary>
    public SpotifyStatus Spotify
    {
        get => _spotify;
        set => _spotify = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The playback view (mirrors librespot's player events).</summary>
    public PlaybackDto Playback
    {
        get => _playback;
        set => _playback = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The last pacer statistics of the active group player, or null.</summary>
    public GroupPlayerEvent.Statistics? Statistics
    {
        get => _statistics;
        set => _statistics = value;
    }

    /// <summary>Receivers the output manager should (re)connect now: a receiver appeared on the network, a pairing finished.</summary>
    public ChannelReader<string> ReconnectRequests => _reconnects.Reader;

    /// <summary>The runtime state of a receiver, or null when the output manager has nothing to say about it.</summary>
    public ReceiverRuntime? Get(string receiverId) => _receivers.TryGetValue(receiverId, out var r) ? r : null;

    /// <summary>Sets a receiver's state; returns true when it changed.</summary>
    public bool Set(string receiverId, ReceiverConnectionState state, string? error = null)
    {
        var next = new ReceiverRuntime(state, error);
        var changed = true;
        _receivers.AddOrUpdate(receiverId, next, (_, old) =>
        {
            changed = old != next;
            return next;
        });
        return changed;
    }

    /// <summary>Forgets a receiver's state (it is then Available or Offline by discovery alone); returns true when there was one.</summary>
    public bool Clear(string receiverId) => _receivers.TryRemove(receiverId, out _);

    /// <summary>Asks the output manager to retry a receiver now.</summary>
    public void RequestReconnect(string receiverId) => _reconnects.Writer.TryWrite(receiverId);
}
