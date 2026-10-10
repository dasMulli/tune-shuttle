using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Airplay2.FakeReceiver;
using Bridge.Contracts;
using Bridge.Tests.Service;
using Bridge.Web;
using Bridge.Web.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// The web app's <see cref="BridgeApiClient"/>, <see cref="BridgeEventClient"/> and <see cref="BridgeState"/> against the
/// bridge in-process (WebApplicationFactory, temp database, in-process fake receivers), WORK-PLAN.md web-app "Verify".
/// </summary>
public sealed class WebApiClientTests
{
    [Fact]
    public async Task Status_settings_playback_diagnostics_and_problem_details()
    {
        await using var factory = new BridgeFactory();
        var api = WebClients.Api(factory);
        var ct = CancellationToken.None;

        var status = await api.GetStatusAsync(ct);
        Assert.Equal("TuneShuttle", status.DeviceName);
        Assert.False(status.SpotifyConnected);

        var settings = await api.GetSettingsAsync(ct);
        var saved = await api.SetSettingsAsync(settings with { DeviceName = "Küche", PreferredStream = "buffered" }, ct);
        Assert.Equal("Küche", saved.DeviceName);
        Assert.Equal("Buffered", saved.PreferredStream); // normalized by the service
        Assert.Equal("Küche", (await api.GetStatusAsync(ct)).DeviceName);

        var invalid = await Assert.ThrowsAsync<BridgeApiException>(() => api.SetSettingsAsync(settings with { LatencyFrames = 5 }, ct));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("Invalid request", invalid.Title);
        Assert.True(invalid.IsProblemDetails); // the service's own problem details, shown as sent (UI-SPEC 3.5)
        Assert.Contains("latency", invalid.Detail, StringComparison.OrdinalIgnoreCase);

        var unknown = await Assert.ThrowsAsync<BridgeApiException>(() => api.GetReceiverAsync("no:such:id", ct));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("no:such:id", unknown.Detail, StringComparison.Ordinal);

        var playback = await api.GetPlaybackAsync(ct);
        Assert.False(playback.Playing);
        var noSpotify = await Assert.ThrowsAsync<BridgeApiException>(() => api.SendPlaybackCommandAsync("play", ct));
        Assert.Equal(HttpStatusCode.Conflict, noSpotify.StatusCode);

        var diagnostics = await api.GetDiagnosticsAsync(ct);
        Assert.NotEmpty(diagnostics.OpenQuestionsPending);

        await api.ForgetSpotifyAsync(ct);

        var snapshot = await api.GetSnapshotAsync(ct);
        Assert.Equal("Küche", snapshot.Status.DeviceName);
        Assert.Empty(snapshot.Receivers);
        Assert.Null(snapshot.Output.GroupId);
    }

    [Fact]
    public async Task Receiver_and_group_routes_drive_the_fake_receivers()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var b = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake B" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        factory.Browser.Add(b.Describe());
        var api = WebClients.Api(factory);
        var ct = CancellationToken.None;

        var receivers = await api.GetReceiversAsync(ct);
        Assert.Equal(["Fake A", "Fake B"], receivers.Select(r => r.Name));
        Assert.Equal("Fake A", (await api.GetReceiverAsync(a.Id, ct)).Name);

        // Single receiver: connect, r_i, offset, disconnect.
        var connected = await api.ConnectAsync(a.Id, ct);
        Assert.Equal(ReceiverConnectionState.Connected, connected.State);
        await a.WaitAsync<FakeReceiverEvent.StreamSetup>();
        Assert.Equal(20, (await api.SetReceiverVolumeAsync(a.Id, 20, ct)).Level);
        await a.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -24.0) < 0.001);
        Assert.Equal(441, (await api.SetReceiverOffsetAsync(a.Id, 441, ct)).OffsetFrames);
        var tooFar = await Assert.ThrowsAsync<BridgeApiException>(() => api.SetReceiverOffsetAsync(a.Id, 1_000_000, ct));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooFar.StatusCode);
        Assert.Equal(a.Id, (await api.GetOutputAsync(ct)).ReceiverId);
        Assert.Equal(ReceiverConnectionState.Available, (await api.DisconnectAsync(a.Id, ct)).State);

        // Groups: create, rename, members, G scales r_i, activate, stop, delete.
        var group = await api.CreateGroupAsync("Downstairs", ct);
        Assert.Equal("Downstairs", group.Name);
        group = await api.RenameGroupAsync(group.Id, "Erdgeschoss", ct);
        Assert.Equal("Erdgeschoss", (await api.GetGroupAsync(group.Id, ct)).Name);
        await api.AddGroupMemberAsync(group.Id, a.Id, 40, ct);
        group = await api.AddGroupMemberAsync(group.Id, b.Id, 60, ct);
        Assert.Equal([40, 60], group.Members.OrderBy(m => m.Name).Select(m => m.Level));
        var before = group;
        group = await api.SetGroupVolumeAsync(group.Id, 25, ct);
        Assert.Equal(25, group.MasterLevel);
        var expected = GroupVolumePreview.Preview(before, 25).ToDictionary(m => m.ReceiverId, m => m.Level);
        Assert.All(group.Members, m => Assert.Equal(expected[m.ReceiverId], m.Level)); // the preview equals the service

        // G' = 0 keeps the stored r_i (the service records the reference G and mutes); the preview shows the same.
        var muted = await api.SetGroupVolumeAsync(group.Id, 0, ct);
        Assert.Equal(0, muted.MasterLevel);
        var mutedPreview = GroupVolumePreview.Preview(group, 0).ToDictionary(m => m.ReceiverId, m => m.Level);
        Assert.All(muted.Members, m => Assert.Equal(mutedPreview[m.ReceiverId], m.Level));
        Assert.All(muted.Members, m => Assert.Equal(expected[m.ReceiverId], m.Level));
        group = await api.SetGroupVolumeAsync(group.Id, 25, ct);
        Assert.All(group.Members, m => Assert.Equal(expected[m.ReceiverId], m.Level)); // back at the reference G

        var output = await api.ActivateGroupAsync(group.Id, ct);
        Assert.Equal(group.Id, output.GroupId);
        await a.WaitAsync<FakeReceiverEvent.StreamSetup>(skip: 1);
        await b.WaitAsync<FakeReceiverEvent.StreamSetup>();
        Assert.Contains(await api.GetGroupsAsync(ct), g => g.Id == group.Id && g.Active);

        group = await api.RemoveGroupMemberAsync(group.Id, b.Id, ct);
        Assert.Equal(a.Id, Assert.Single(group.Members).ReceiverId);
        output = await api.SetOutputAsync(new OutputRequest(null, null), ct);
        Assert.Null(output.GroupId);
        await api.DeleteGroupAsync(group.Id, ct);
        Assert.Empty(await api.GetGroupsAsync(ct));
        var deleted = await Assert.ThrowsAsync<BridgeApiException>(() => api.GetGroupAsync(group.Id, ct));
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
    }

    [Fact]
    public async Task Event_client_fills_the_state_follows_changes_and_streams_the_log()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        var api = WebClients.Api(factory);
        var state = new BridgeState();
        await using var events = WebClients.Events(factory, state);
        events.Start();

        // The snapshot: status, receivers, groups, output, playback.
        await WebClients.WaitUntilAsync(() => state.Connection == EventConnectionState.Connected && state.Receiver(a.Id) is not null && state.Playback is not null, "the snapshot");
        Assert.Equal("TuneShuttle", state.Status!.DeviceName);

        // A change made by someone else (another tab, the API) arrives by itself.
        var commands = new BridgeCommands(api, new BridgeState(), new I18n(), NullLogger<BridgeCommands>.Instance);
        Assert.True(await commands.ConnectAsync(a.Id, CancellationToken.None));
        await WebClients.WaitUntilAsync(() => state.Receiver(a.Id)?.State == ReceiverConnectionState.Connected && state.Output.ReceiverId == a.Id, "receiverChanged + outputChanged");
        Assert.True(await commands.SetReceiverLevelAsync(a.Id, 33, CancellationToken.None));
        await WebClients.WaitUntilAsync(() => state.LevelOf(state.Receiver(a.Id)!) == 33 && state.Output.MasterLevel == 33, "volumeChanged");
        var group = await commands.CreateGroupAsync("Live", CancellationToken.None);
        Assert.NotNull(group);
        await WebClients.WaitUntilAsync(() => state.Group(group.Id) is not null, "groupChanged");
        Assert.True(await commands.DeleteGroupAsync(group.Id, CancellationToken.None));
        await WebClients.WaitUntilAsync(() => state.Group(group.Id) is null, "groupRemoved");

        // The diagnostics page subscribes to the log: history first, then live lines.
        await events.SetLogSubscriptionAsync(true, CancellationToken.None);
        await WebClients.WaitUntilAsync(() => state.Log.Count > 0, "the log history");
        Assert.True(await commands.SaveSettingsAsync(new SettingsDto("Logged", 77175, false, "Realtime"), CancellationToken.None) is not null);
        await WebClients.WaitUntilAsync(() => state.Log.Any(l => l.Message.Contains("Settings", StringComparison.Ordinal)), "a live log line");
        await WebClients.WaitUntilAsync(() => state.Status?.DeviceName == "Logged", "statusChanged");
        await events.SetLogSubscriptionAsync(false, CancellationToken.None);
        Assert.False(events.LogSubscribed);
    }

    [Fact]
    public async Task Event_client_reconnects_resubscribes_and_resyncs_after_the_connection_drops()
    {
        await using var a = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake A" });
        await using var b = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake B" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(a.Describe());
        factory.Browser.Add(b.Describe());
        var api = WebClients.Api(factory);
        var group = await api.CreateGroupAsync("Short-lived", CancellationToken.None);
        var state = new BridgeState();
        var sockets = new ConcurrentQueue<WebSocket>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        await using var events = WebClients.Events(factory, state, opened: sockets, beforeConnect: () => Volatile.Read(ref gate).Task);
        events.Start();
        await events.SetLogSubscriptionAsync(true, CancellationToken.None);
        await WebClients.WaitUntilAsync(() => state.Receivers.Count == 2 && state.Group(group.Id) is not null && state.Connection == EventConnectionState.Connected, "the first snapshot");
        Assert.True(state.Receiver(b.Id)!.Online);

        // The connection drops and stays down while the group is deleted and B leaves the network.
        Volatile.Write(ref gate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Assert.True(sockets.TryPeek(out var first));
        var connections = events.Connections;
        first.Abort();
        await WebClients.WaitUntilAsync(() => state.Connection != EventConnectionState.Connected, "the client noticing the drop");
        await api.DeleteGroupAsync(group.Id, CancellationToken.None);
        factory.Browser.Remove(b.Id);
        Assert.NotNull(state.Group(group.Id)); // missed: no event reached the client
        Volatile.Read(ref gate).SetResult();
        await WebClients.WaitUntilAsync(() => events.Connections > connections && state.Connection == EventConnectionState.Connected, "the reconnect");

        // The new snapshot does not repeat the group (removed by the resync) and shows B offline (the store keeps it).
        await WebClients.WaitUntilAsync(() => state.Group(group.Id) is null, "the group removed by the resync");
        await WebClients.WaitUntilAsync(() => state.Receiver(b.Id) is { Online: false }, "B offline");
        Assert.True(state.Receiver(a.Id)!.Online);

        // The log subscription was sent again on the new connection.
        state.ClearLog();
        await api.SetSettingsAsync(new SettingsDto("After reconnect", 77175, false, "Realtime"), CancellationToken.None);
        await WebClients.WaitUntilAsync(() => state.Log.Any(l => l.Message.Contains("After reconnect", StringComparison.Ordinal)), "log lines after the reconnect");
    }

    [Fact]
    public async Task Event_client_start_says_connecting_before_the_first_attempt_runs()
    {
        // No offline flash: the state is Connecting when Start returns, before the loop's first attempt (which yields).
        var state = new BridgeState();
        var connect = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var events = new BridgeEventClient(
            state,
            new Uri("ws://localhost/ws/events"),
            NullLogger<BridgeEventClient>.Instance,
            connect: (_, ct) => connect.Task.WaitAsync(ct));
        Assert.Equal(EventConnectionState.Disconnected, state.Connection);
        Assert.Null(state.NextAttemptAt);

        events.Start();

        Assert.Equal(EventConnectionState.Connecting, state.Connection);
        Assert.Null(state.ConnectionError);
        events.Start(); // idempotent
        Assert.Equal(EventConnectionState.Connecting, state.Connection);
    }

    [Fact]
    public async Task Event_client_announces_connecting_once_then_stays_paused_through_the_retries_until_connected()
    {
        // The banner is the live region of these states: a retry must not flip paused -> connecting -> paused (two
        // announcements per back-off cycle for as long as the bridge is down).
        var state = new BridgeState();
        var seen = new ConcurrentQueue<string>();
        state.Changed += () => seen.Enqueue(state.Connection switch
        {
            EventConnectionState.Connected => "connected",
            EventConnectionState.Connecting => "connecting",
            _ when state.NextAttemptAt is not null => "paused",
            _ => "offline",
        });
        var attempts = 0;
        var socket = new ScriptedWebSocket();
        await using var events = new BridgeEventClient(
            state,
            new Uri("ws://localhost/ws/events"),
            NullLogger<BridgeEventClient>.Instance,
            new BridgeEventClientOptions { InitialRetryDelay = TimeSpan.FromMilliseconds(10), MaxRetryDelay = TimeSpan.FromMilliseconds(20) },
            (_, _) => Interlocked.Increment(ref attempts) <= 3
                ? Task.FromException<WebSocket>(new WebSocketException("connection refused"))
                : Task.FromResult<WebSocket>(socket));
        events.Start();

        await WebClients.WaitUntilAsync(() => state.Connection == EventConnectionState.Connected, "the connection after three failures", TimeSpan.FromSeconds(10));
        Assert.Equal(4, Volatile.Read(ref attempts));
        var kinds = seen.ToArray();
        var distinct = kinds.Where((kind, i) => i == 0 || kinds[i - 1] != kind).TakeWhile(kind => kind != "connected").Append("connected");
        Assert.Equal(["connecting", "paused", "connected"], distinct);
        Assert.Single(kinds.TakeWhile(kind => kind != "connected"), kind => kind == "connecting");
    }

    [Fact]
    public async Task Event_client_drops_a_silent_connection_and_backs_off_while_the_server_is_unreachable()
    {
        var state = new BridgeState();
        var attempts = 0;
        var silent = new SilentWebSocket();
        await using var events = new BridgeEventClient(
            state,
            new Uri("ws://localhost/ws/events"),
            NullLogger<BridgeEventClient>.Instance,
            new BridgeEventClientOptions
            {
                InitialRetryDelay = TimeSpan.FromMilliseconds(20),
                MaxRetryDelay = TimeSpan.FromMilliseconds(50),
                PingInterval = TimeSpan.FromMilliseconds(100),
                PongTimeout = TimeSpan.FromMilliseconds(100),
            },
            (uri, ct) => Interlocked.Increment(ref attempts) switch
            {
                1 => Task.FromResult<WebSocket>(silent),
                _ => Task.FromException<WebSocket>(new WebSocketException("connection refused")),
            });
        events.Start();

        // The silent socket gets pings, then is aborted by the watchdog; the client keeps retrying.
        await WebClients.WaitUntilAsync(() => silent.Aborted, "the watchdog abort", TimeSpan.FromSeconds(10));
        Assert.True(silent.PingsSent > 0);
        await WebClients.WaitUntilAsync(() => Volatile.Read(ref attempts) >= 4, "repeated attempts", TimeSpan.FromSeconds(10));
        await WebClients.WaitUntilAsync(() => state.ConnectionError?.Contains("refused", StringComparison.Ordinal) == true, "the error in the state", TimeSpan.FromSeconds(10));
        Assert.NotEqual(EventConnectionState.Connected, state.Connection);
    }

    [Fact]
    public async Task Event_client_keeps_applying_events_after_a_changed_handler_throws()
    {
        var state = new BridgeState();
        var thrown = 0;
        state.Changed += () =>
        {
            if (state.Receiver("a") is not null && Interlocked.CompareExchange(ref thrown, 1, 0) == 0)
            {
                throw new FormatException("a broken page");
            }
        };
        var socket = new ScriptedWebSocket(
            Event(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A"))),
            Event(new BridgeEvent.ReceiverChanged(Dto.Receiver("b", "B"))),
            Event(new BridgeEvent.OutputChanged(Dto.NoOutput)));
        var logger = new ListLogger<BridgeEventClient>();
        await using var events = ScriptedClient(state, socket, logger);
        events.Start();

        await WebClients.WaitUntilAsync(() => state.Receiver("b") is not null && state.Loaded, "the events after the failing handler", TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref thrown));
        Assert.NotNull(state.Receiver("a"));
        Assert.Equal(EventConnectionState.Connected, state.Connection);
        Assert.Equal(1, events.Connections);
        Assert.False(socket.Aborted);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is FormatException);
    }

    [Fact]
    public async Task Event_client_ignores_unknown_event_types_quietly_and_broken_events_with_a_warning()
    {
        var state = new BridgeState();
        var socket = new ScriptedWebSocket(
            """{"type":"somethingNew","detail":1}""",
            "{not json",
            Event(new BridgeEvent.StatusChanged(Dto.Status("After"))));
        var logger = new ListLogger<BridgeEventClient>();
        await using var events = ScriptedClient(state, socket, logger);
        events.Start();

        await WebClients.WaitUntilAsync(() => state.Status?.DeviceName == "After", "the event after the ignored ones", TimeSpan.FromSeconds(10));
        Assert.Equal(1, events.Connections);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("unknown type", StringComparison.Ordinal));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pin_flow_through_the_web_app_pairs_and_connects()
    {
        await using var pin = await RecordedFake.StartPinAsync("PIN Fake", "4711");
        await using var factory = new BridgeFactory();
        factory.Browser.Add(pin.Describe());
        var state = new BridgeState();
        var commands = new BridgeCommands(WebClients.Api(factory), state, new I18n(), NullLogger<BridgeCommands>.Instance);
        await using var events = WebClients.Events(factory, state);
        events.Start();
        await WebClients.WaitUntilAsync(() => state.Receiver(pin.Id)?.Pairing == PairingState.PinRequired, "the PIN receiver");

        // Connect needs a PIN: the service announces pairingRequired, the state opens the prompt.
        Assert.True(await commands.ConnectAsync(pin.Id, CancellationToken.None));
        await WebClients.WaitUntilAsync(() => state.Pairing is not null, "the PIN prompt");
        var prompt = state.Pairing!;
        Assert.Equal(PairingPromptOrigin.Service, prompt.Origin);
        Assert.DoesNotContain(state.Notices, n => n.Kind == NoticeKind.Error); // the PIN case is not an error banner

        // Wrong PIN: rejected; the token is consumed, so the same token is then "expired".
        Assert.Equal(PinOutcome.Rejected, (await commands.SubmitPinAsync(prompt, "0000", CancellationToken.None)).Outcome);
        Assert.Equal(PinOutcome.Expired, (await commands.SubmitPinAsync(prompt, "4711", CancellationToken.None)).Outcome);
        Assert.Equal(PinOutcome.Invalid, (await commands.SubmitPinAsync(prompt, " ", CancellationToken.None)).Outcome);

        // Start again (the dialog's button), then the right PIN.
        Assert.True(await commands.StartPairingAsync(pin.Id, CancellationToken.None));
        var again = state.Pairing!;
        Assert.NotEqual(prompt.Token, again.Token);
        Assert.Equal(PairingPromptOrigin.User, again.Origin);
        Assert.Equal(PinOutcome.Paired, (await commands.SubmitPinAsync(again, "4711", CancellationToken.None)).Outcome);
        Assert.Null(state.Pairing);
        Assert.Equal(PairingState.Paired, state.Receiver(pin.Id)!.Pairing);
        await WebClients.WaitUntilAsync(() => state.Receiver(pin.Id)?.State == ReceiverConnectionState.Connected, "the reconnect after pairing");
        Assert.Contains(state.Notices, n => n.Kind == NoticeKind.Success);

        Assert.True(await commands.ForgetPairingAsync(pin.Id, CancellationToken.None));
        Assert.Equal(PairingState.PinRequired, state.Receiver(pin.Id)!.Pairing);
    }

    [Fact]
    public async Task Commands_turn_api_errors_into_notices()
    {
        await using var factory = new BridgeFactory();
        var state = new BridgeState();
        var commands = new BridgeCommands(WebClients.Api(factory), state, new I18n(), NullLogger<BridgeCommands>.Instance);

        Assert.False(await commands.ConnectAsync("no:such:id", CancellationToken.None));
        var notice = Assert.Single(state.Notices);
        Assert.Equal(NoticeKind.Error, notice.Kind);
        Assert.Contains("Not found", notice.Text, StringComparison.Ordinal);
        Assert.Null(await commands.CreateGroupAsync("   ", CancellationToken.None));
        Assert.Contains(state.Notices, n => n.Text.Contains("Invalid request", StringComparison.Ordinal));
        Assert.True(await commands.RefreshAsync(CancellationToken.None));
        Assert.True(state.Loaded);
    }

    private static string Event(BridgeEvent bridgeEvent) => System.Text.Json.JsonSerializer.Serialize(bridgeEvent, BridgeApiClient.JsonOptions);

    /// <summary>An event client whose first connection is <paramref name="socket"/>; later attempts fail.</summary>
    private static BridgeEventClient ScriptedClient(BridgeState state, WebSocket socket, ILogger<BridgeEventClient> logger)
    {
        var attempts = 0;
        return new BridgeEventClient(
            state,
            new Uri("ws://localhost/ws/events"),
            logger,
            new BridgeEventClientOptions { InitialRetryDelay = TimeSpan.FromMilliseconds(20), MaxRetryDelay = TimeSpan.FromMilliseconds(50) },
            (_, _) => Interlocked.Increment(ref attempts) == 1
                ? Task.FromResult(socket)
                : Task.FromException<WebSocket>(new WebSocketException("connection refused")));
    }

    /// <summary>A logger that keeps what it is given.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    /// <summary>A WebSocket that delivers the given text messages, then nothing until aborted.</summary>
    private sealed class ScriptedWebSocket(params string[] messages) : WebSocket
    {
        private readonly ConcurrentQueue<byte[]> _messages = new(messages.Select(m => System.Text.Encoding.UTF8.GetBytes(m)));
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Aborted => _aborted.Task.IsCompleted;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort() => _aborted.TrySetResult();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose() => Abort();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_messages.TryDequeue(out var message))
            {
                message.CopyTo(buffer.AsSpan()); // the test messages fit the client's first buffer
                return new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, endOfMessage: true);
            }

            await _aborted.Task.WaitAsync(cancellationToken);
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "aborted");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A WebSocket that accepts sends and never delivers anything until aborted.</summary>
    private sealed class SilentWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pings;

        public bool Aborted => _aborted.Task.IsCompleted;

        public int PingsSent => Volatile.Read(ref _pings);

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort() => _aborted.TrySetResult();

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose() => Abort();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await _aborted.Task.WaitAsync(cancellationToken);
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "aborted");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (System.Text.Encoding.UTF8.GetString(buffer).Contains("ping", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _pings);
            }

            return Task.CompletedTask;
        }
    }
}
