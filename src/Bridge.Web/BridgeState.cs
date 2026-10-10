using Bridge.Contracts;

namespace Bridge.Web;

/// <summary>State of the event connection to the bridge (<c>/ws/events</c>).</summary>
public enum EventConnectionState
{
    /// <summary>Not connected yet, or waiting before the next attempt.</summary>
    Disconnected,

    /// <summary>An attempt is under way.</summary>
    Connecting,

    /// <summary>Connected; events flow.</summary>
    Connected,
}

/// <summary>Kind of a notice shown in the app's banner area.</summary>
public enum NoticeKind
{
    /// <summary>Something worked (a pairing was stored, settings saved).</summary>
    Success,

    /// <summary>Plain information.</summary>
    Info,

    /// <summary>Something failed (an API error).</summary>
    Error,
}

/// <summary>Where a PIN prompt came from.</summary>
public enum PairingPromptOrigin
{
    /// <summary>The user pressed "Pair" (<c>POST /receivers/{id}/pair</c>).</summary>
    User,

    /// <summary>A connect needed a PIN and the service announced it (<c>pairingRequired</c>).</summary>
    Service,
}

/// <summary>An open PIN pairing: the receiver shows its PIN and the dialog asks for it.</summary>
/// <param name="ReceiverId">The receiver.</param>
/// <param name="Token">The pairing token to send back with the PIN.</param>
/// <param name="Message">What to tell the user.</param>
/// <param name="Origin">Who started it.</param>
public sealed record PairingPrompt(string ReceiverId, string Token, string Message, PairingPromptOrigin Origin);

/// <summary>A banner message.</summary>
/// <param name="Id">Unique id (for dismissing).</param>
/// <param name="Kind">Kind.</param>
/// <param name="Text">Text.</param>
/// <param name="At">When it was raised.</param>
public sealed record Notice(long Id, NoticeKind Kind, string Text, DateTimeOffset At);

/// <summary>
/// The web app's single source of truth (WORK-PLAN.md web-app: "a <c>BridgeState</c> container"): receivers, groups, the
/// selected output, playback, status, the live log, the open PIN prompt, banner notices and the event connection state.
/// It is fed by <see cref="BridgeEventClient"/> (every <see cref="BridgeEvent"/>), by REST answers the pages apply right
/// away (so an action shows before its event arrives), and by a REST <see cref="BridgeSnapshot"/> at start-up. Pages
/// subscribe to <see cref="Changed"/> and re-render. Thread-safe (one lock; <see cref="Changed"/> is raised outside it),
/// although the browser runs it on one thread.
/// </summary>
public sealed class BridgeState
{
    /// <summary>Log lines kept for the diagnostics page.</summary>
    public const int MaxLogLines = 500;

    /// <summary>Notices kept at most (the oldest is dropped).</summary>
    public const int MaxNotices = 5;

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ReceiverDto> _receivers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupDto> _groups = new(StringComparer.Ordinal);
    private readonly LinkedList<BridgeEvent.LogLine> _log = new();
    private readonly HashSet<BridgeEvent.LogLine> _logIndex = [];
    private readonly List<Notice> _notices = [];
    private IReadOnlyList<ReceiverDto>? _receiverList;
    private IReadOnlyList<GroupDto>? _groupList;
    private HashSet<string>? _staleReceivers;
    private HashSet<string>? _staleGroups;
    private bool _liveSnapshot;
    private long _nextNoticeId;

    /// <summary>Creates an empty state.</summary>
    public BridgeState(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after every change (outside the lock). Components call <c>InvokeAsync(StateHasChanged)</c>.</summary>
    public event Action? Changed;

    /// <summary>Incremented on every change (tests and change detection).</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Incremented only when <see cref="Log"/> changes (a line appended, the log cleared), so the diagnostics page can
    /// keep its formatted lines across the many renders that other events cause.
    /// </summary>
    public long LogVersion { get; private set; }

    /// <summary>The service status, or null before the first answer.</summary>
    public StatusDto? Status { get; private set; }

    /// <summary>The selected output (nothing selected until the first answer).</summary>
    public OutputDto Output { get; private set; } = new(null, null, 0, []);

    /// <summary>The playback view, or null before the first answer.</summary>
    public PlaybackDto? Playback { get; private set; }

    /// <summary>When <see cref="Playback"/> arrived (the position is extrapolated from it while playing).</summary>
    public DateTimeOffset PlaybackReceivedAt { get; private set; }

    /// <summary>The event connection.</summary>
    public EventConnectionState Connection { get; private set; } = EventConnectionState.Disconnected;

    /// <summary>Why the event connection is down, or null.</summary>
    public string? ConnectionError { get; private set; }

    /// <summary>When the next reconnect attempt is due while <see cref="Connection"/> is <see cref="EventConnectionState.Disconnected"/>.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>
    /// True once a full data set has arrived: a REST snapshot (<see cref="Load"/>) or the end of the WebSocket snapshot
    /// (its <c>outputChanged</c>). Status and log lines alone do not count, so the pages never show "no receivers yet"
    /// between the first snapshot event and the receivers.
    /// </summary>
    public bool Loaded { get; private set; }

    /// <summary>The open PIN prompt, or null.</summary>
    public PairingPrompt? Pairing { get; private set; }

    /// <summary>The banner notices, oldest first.</summary>
    public IReadOnlyList<Notice> Notices
    {
        get
        {
            lock (_gate)
            {
                return [.. _notices];
            }
        }
    }

    /// <summary>All receivers, by name.</summary>
    public IReadOnlyList<ReceiverDto> Receivers
    {
        get
        {
            lock (_gate)
            {
                return _receiverList ??= [.. _receivers.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>All groups, by name.</summary>
    public IReadOnlyList<GroupDto> Groups
    {
        get
        {
            lock (_gate)
            {
                return _groupList ??= [.. _groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>The live log (oldest first, at most <see cref="MaxLogLines"/>).</summary>
    public IReadOnlyList<BridgeEvent.LogLine> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>One receiver, or null.</summary>
    public ReceiverDto? Receiver(string id)
    {
        lock (_gate)
        {
            return _receivers.GetValueOrDefault(id);
        }
    }

    /// <summary>One group, or null.</summary>
    public GroupDto? Group(string id)
    {
        lock (_gate)
        {
            return _groups.GetValueOrDefault(id);
        }
    }

    /// <summary>A receiver's display name (its id when unknown).</summary>
    public string NameOf(string receiverId)
    {
        lock (_gate)
        {
            return _receivers.TryGetValue(receiverId, out var r) ? r.Name : receiverId;
        }
    }

    /// <summary>True when the receiver plays as (part of) the selected output.</summary>
    public bool IsInOutput(string receiverId)
    {
        lock (_gate)
        {
            return Output.Members.Any(m => m.ReceiverId == receiverId);
        }
    }

    /// <summary>
    /// The r_i the receiver's slider shows: inside the active group its member level (that is what
    /// <c>PUT /receivers/{id}/volume</c> sets there), otherwise the receiver's own level.
    /// </summary>
    public int LevelOf(ReceiverDto receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_gate)
        {
            if (Output.GroupId is { } groupId && _groups.TryGetValue(groupId, out var group)
                && group.Members.FirstOrDefault(m => m.ReceiverId == receiver.Id) is { } member)
            {
                return member.Level;
            }

            return receiver.Level;
        }
    }

    /// <summary>The playback position now: extrapolated from the last report while playing, capped at the duration.</summary>
    public long PlaybackPositionMs()
    {
        lock (_gate)
        {
            if (Playback is not { } playback)
            {
                return 0;
            }

            var position = playback.PositionMs;
            if (playback.Playing)
            {
                position += (long)(_time.GetUtcNow() - PlaybackReceivedAt).TotalMilliseconds;
            }

            return playback.DurationMs > 0 ? Math.Clamp(position, 0, playback.DurationMs) : Math.Max(0, position);
        }
    }

    // ---- feeding -------------------------------------------------------------------------------------------------

    /// <summary>Applies one event from <c>/ws/events</c>.</summary>
    public void Apply(BridgeEvent bridgeEvent)
    {
        ArgumentNullException.ThrowIfNull(bridgeEvent);
        lock (_gate)
        {
            switch (bridgeEvent)
            {
                case BridgeEvent.ReceiverChanged e:
                    SetReceiver(e.Receiver);
                    _staleReceivers?.Remove(e.Receiver.Id);
                    break;
                case BridgeEvent.ReceiverRemoved e:
                    _receivers.Remove(e.Id);
                    _receiverList = null;
                    break;
                case BridgeEvent.GroupChanged e:
                    SetGroup(e.Group);
                    _staleGroups?.Remove(e.Group.Id);
                    break;
                case BridgeEvent.GroupRemoved e:
                    _groups.Remove(e.Id);
                    _groupList = null;
                    break;
                case BridgeEvent.OutputChanged e:
                    SetOutput(e.Output);
                    EndResync(); // the snapshot sends receivers and groups before the output (WORK-PLAN.md section 7)
                    break;
                case BridgeEvent.PlaybackChanged e:
                    SetPlayback(e.Playback);
                    break;
                case BridgeEvent.VolumeChanged e:
                    ApplyVolume(e.GroupId, e.ReceiverId, e.Level);
                    break;
                case BridgeEvent.PairingRequired e:
                    Pairing = new PairingPrompt(e.ReceiverId, e.PairingToken, $"{NameOfLocked(e.ReceiverId)} needs a PIN pairing. Enter the PIN it shows now.", PairingPromptOrigin.Service);
                    break;
                case BridgeEvent.StatusChanged e:
                    Status = e.Status;
                    break;
                case BridgeEvent.LogLine e:
                    AppendLog(e);
                    break;
                case BridgeEvent.Pong:
                    return; // keep-alive only; nothing changed
                default:
                    return;
            }

            // While a snapshot comes in (_staleReceivers set by BeginResync) the data is complete only at its outputChanged,
            // whose EndResync clears _staleReceivers; outside a snapshot every data event lands on a complete set.
            if (_staleReceivers is null && bridgeEvent is not (BridgeEvent.StatusChanged or BridgeEvent.LogLine))
            {
                Loaded = true;
            }

            Version++;
        }

        RaiseChanged();
    }

    /// <summary>
    /// Applies a REST snapshot. Ignored (returns false) once the WebSocket snapshot has arrived on the current connection,
    /// since that one is at least as new.
    /// </summary>
    public bool Load(BridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_liveSnapshot)
            {
                return false;
            }

            _receivers.Clear();
            foreach (var receiver in snapshot.Receivers)
            {
                _receivers[receiver.Id] = receiver;
            }

            _groups.Clear();
            foreach (var group in snapshot.Groups)
            {
                _groups[group.Id] = group;
            }

            _receiverList = null;
            _groupList = null;
            Status = snapshot.Status;
            SetOutput(snapshot.Output);
            SetPlayback(snapshot.Playback);
            Loaded = true;
            Version++;
        }

        RaiseChanged();
        return true;
    }

    /// <summary>A new event connection is up: everything not repeated by its snapshot is removed when the snapshot ends.</summary>
    public void BeginResync()
    {
        lock (_gate)
        {
            _staleReceivers = [.. _receivers.Keys];
            _staleGroups = [.. _groups.Keys];
            _liveSnapshot = false;
        }
    }

    /// <summary>Records the event connection's state.</summary>
    public void SetConnection(EventConnectionState state, string? error = null, DateTimeOffset? nextAttemptAt = null)
    {
        lock (_gate)
        {
            if (Connection == state && ConnectionError == error && NextAttemptAt == nextAttemptAt)
            {
                return;
            }

            Connection = state;
            ConnectionError = error;
            NextAttemptAt = nextAttemptAt;
            if (state != EventConnectionState.Connected)
            {
                _liveSnapshot = false;
                _staleReceivers = null;
                _staleGroups = null;
            }

            Version++;
        }

        RaiseChanged();
    }

    /// <summary>Applies a receiver returned by a REST call.</summary>
    public void ApplyReceiver(ReceiverDto receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        Mutate(() => SetReceiver(receiver));
    }

    /// <summary>Applies a group returned by a REST call.</summary>
    public void ApplyGroup(GroupDto group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Mutate(() => SetGroup(group));
    }

    /// <summary>Removes a group (after a successful delete).</summary>
    public void RemoveGroup(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        Mutate(() =>
        {
            _groups.Remove(groupId);
            _groupList = null;
        });
    }

    /// <summary>Applies an output returned by a REST call.</summary>
    public void ApplyOutput(OutputDto output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Mutate(() => SetOutput(output));
    }

    /// <summary>Applies a playback view returned by a REST call.</summary>
    public void ApplyPlayback(PlaybackDto playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        Mutate(() => SetPlayback(playback));
    }

    /// <summary>
    /// Applies r_i of a member of <paramref name="groupId"/> answered by <c>PUT /receivers/{id}/volume</c> (the same
    /// update as the <c>volumeChanged</c> that follows), so the group's slider settles without another request.
    /// </summary>
    public void ApplyMemberLevel(string groupId, string receiverId, int level)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(receiverId);
        Mutate(() => ApplyVolume(groupId, receiverId, Math.Clamp(level, 0, 100)));
    }

    /// <summary>Opens the PIN dialog for a pairing the user started.</summary>
    public void OpenPairing(PairingPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Mutate(() => Pairing = prompt);
    }

    /// <summary>Closes the PIN dialog if it still shows <paramref name="token"/> (null: whatever it shows).</summary>
    public void ClosePairing(string? token = null)
    {
        Mutate(() =>
        {
            if (token is null || Pairing?.Token == token)
            {
                Pairing = null;
            }
        });
    }

    /// <summary>Adds a banner notice (the oldest goes when there are more than <see cref="MaxNotices"/>).</summary>
    public Notice Notify(NoticeKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Notice notice = null!;
        Mutate(() =>
        {
            notice = new Notice(++_nextNoticeId, kind, text, _time.GetUtcNow());
            _notices.Add(notice);
            while (_notices.Count > MaxNotices)
            {
                _notices.RemoveAt(0);
            }
        });
        return notice;
    }

    /// <summary>Removes a notice.</summary>
    public void Dismiss(long noticeId) => Mutate(() => _notices.RemoveAll(n => n.Id == noticeId));

    /// <summary>Empties the live log view.</summary>
    public void ClearLog() => Mutate(() =>
    {
        _log.Clear();
        _logIndex.Clear();
        LogVersion++;
    });

    // ---- internals (called under the lock) -----------------------------------------------------------------------

    private void Mutate(Action change)
    {
        lock (_gate)
        {
            change();
            Version++;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private string NameOfLocked(string receiverId) => _receivers.TryGetValue(receiverId, out var r) ? r.Name : receiverId;

    private void SetReceiver(ReceiverDto receiver)
    {
        _receivers[receiver.Id] = receiver;
        _receiverList = null;
    }

    private void SetGroup(GroupDto group)
    {
        _groups[group.Id] = group;
        _groupList = null;
    }

    private void SetOutput(OutputDto output)
    {
        Output = output;

        // The service publishes groupChanged for the old and the new group as well; keep Active consistent meanwhile.
        foreach (var (id, group) in _groups.ToList())
        {
            var active = id == output.GroupId;
            if (group.Active != active)
            {
                _groups[id] = group with { Active = active };
                _groupList = null;
            }
        }
    }

    private void SetPlayback(PlaybackDto playback)
    {
        Playback = playback;
        PlaybackReceivedAt = _time.GetUtcNow();
    }

    private void EndResync()
    {
        if (_staleReceivers is not null)
        {
            foreach (var id in _staleReceivers)
            {
                _receivers.Remove(id);
            }

            _receiverList = null;
        }

        if (_staleGroups is not null)
        {
            foreach (var id in _staleGroups)
            {
                _groups.Remove(id);
            }

            _groupList = null;
        }

        _staleReceivers = null;
        _staleGroups = null;
        if (Connection == EventConnectionState.Connected)
        {
            _liveSnapshot = true;
        }
    }

    /// <summary>
    /// <c>volumeChanged</c>: G of a group (no receiver id), r_i of a member of the active group (both ids), or the level
    /// of a receiver outside a group (receiver id only). The service follows with the full DTOs; this makes the sliders
    /// move at once, including for changes made on the receiver itself (SPEC-audio.md 6.2).
    /// </summary>
    private void ApplyVolume(string? groupId, string? receiverId, int level)
    {
        if (receiverId is null)
        {
            if (groupId is not null && _groups.TryGetValue(groupId, out var group))
            {
                _groups[groupId] = group with { MasterLevel = level };
                _groupList = null;
            }

            if (groupId is not null && Output.GroupId == groupId)
            {
                Output = Output with { MasterLevel = level };
            }

            return;
        }

        if (groupId is not null)
        {
            if (_groups.TryGetValue(groupId, out var group))
            {
                _groups[groupId] = group with { Members = WithLevel(group.Members, receiverId, level) };
                _groupList = null;
            }
        }
        else if (_receivers.TryGetValue(receiverId, out var receiver))
        {
            _receivers[receiverId] = receiver with { Level = level };
            _receiverList = null;
        }

        if (Output.Members.Any(m => m.ReceiverId == receiverId) && (groupId is null ? Output.ReceiverId == receiverId : Output.GroupId == groupId))
        {
            Output = Output with
            {
                Members = WithLevel(Output.Members, receiverId, level),
                MasterLevel = Output.ReceiverId == receiverId ? level : Output.MasterLevel,
            };
        }
    }

    private static IReadOnlyList<GroupMemberDto> WithLevel(IReadOnlyList<GroupMemberDto> members, string receiverId, int level) =>
        [.. members.Select(m => m.ReceiverId == receiverId ? m with { Level = level } : m)];

    private void AppendLog(BridgeEvent.LogLine line)
    {
        // A resubscription (after a reconnect) replays the recent history: skip lines we already have.
        if (!_logIndex.Add(line))
        {
            return;
        }

        _log.AddLast(line);
        while (_log.Count > MaxLogLines)
        {
            _logIndex.Remove(_log.First!.Value);
            _log.RemoveFirst();
        }

        LogVersion++;
    }
}
