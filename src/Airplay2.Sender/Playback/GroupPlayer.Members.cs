using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Playback;

/// <content>
/// Members: the D5 start-up sequence, removal and loss, and the per-member loops (feedback, progress keep-alive, volume
/// sender with coalescing, volume poll fallback, event channel, connection watch).
/// </content>
public sealed partial class GroupPlayer
{
    /// <inheritdoc />
    /// <remarks>
    /// SPEC.md D5 (SPEC-control.md 4, SPEC-audio.md 3.2): the factory's session must be paired (GET /info and pairing are
    /// the factory's). Then session SETUP (timing protocol and port from the transport, <c>groupUUID</c>,
    /// <c>groupContainsGroupLeader</c>, <c>isMultiSelectAirPlay</c>, the PTP clock id) → event channel → RECORD →
    /// (PTP: the receiver becomes a grandmaster peer, <c>SETPEERS</c>) → stream SETUP (type 96, <c>shk</c> per D4,
    /// our control port, <c>latencyMin/Max</c> around L) → <c>SET_PARAMETER volume</c> (after the stream SETUP: the srcvers-366 speaker
    /// ignores it before, probe P3) → transport registration in CONNECTED (initial <c>0x90</c> sync + marker with the next
    /// packet, no catch-up) → text/artwork/progress of the current track, sent in the background (best effort: the srcvers-366 speaker
    /// answers DMAP text with 500 after ~10 s, SPEC-control.md 5.2, and the call must not wait for that). On failure
    /// everything set up is torn down and the session disposed. <c>RECORD</c>'s <c>Audio-Latency</c> becomes part of the
    /// member's <c>offset_frames</c> (SPEC.md C2, never a change of L). Under PTP the grandmaster peer is taken from the
    /// receiver's <c>timingPeerInfo.Addresses</c> in the SETUP answer (<see cref="PtpSessionWiring.SelectPeerAddress"/>,
    /// falling back to the RTSP address), and <c>SETPEERS</c> carries that peer and our control-connection address.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The receiver is already an active member, or the factory returned an unpaired session.</exception>
    public async Task AddReceiverAsync(DiscoveredReceiver receiver, int level, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_members.TryGetValue(receiver.Id, out var existing))
        {
            if (existing.State != GroupMemberState.Lost)
            {
                throw new InvalidOperationException($"{receiver.Name} is already a member of this group.");
            }

            await RemoveReceiverAsync(receiver.Id, cancellationToken).ConfigureAwait(false);
        }

        level = Math.Clamp(level, 0, 100);
        await Transport.StartAsync(cancellationToken).ConfigureAwait(false);
        var session = await SessionFactory(receiver, cancellationToken).ConfigureAwait(false);
        var sessionUp = false;
        IPAddress? timingPeer = null;
        var realtime = Transport as RealtimeStreamTransport;
        try
        {
            var pairing = session.Pairing ?? throw new InvalidOperationException($"The session factory returned an unpaired session for {receiver.Name}; GET /info and pairing come first (SPEC.md D5).");
            var quirks = (session as ReceiverSession)?.Quirks ?? ReceiverQuirks.ForReceiver(receiver);
            if (quirks.TimingProtocol is { } wanted && wanted != Transport.TimingProtocol)
            {
                LogTimingMismatch(receiver.Name, wanted, Transport.TimingProtocol);
            }

            var address = Normalize(session.Connection.RemoteEndPoint.Address);
            var ptpClock = realtime?.Ptp?.ClockId ?? (Transport as BufferedStreamTransport)?.Ptp.ClockId;
            var setupRequest = new SessionSetupRequest(
                Options.Sender,
                Transport.TimingProtocol,
                Transport.TimingPort,
                Guid.NewGuid().ToString().ToUpperInvariant(),
                Options.GroupUuid,
                Options.GroupContainsGroupLeader,
                Options.IsMultiSelectAirPlay,
                TimingPeerAddresses: null,
                PtpClockId: Transport.TimingProtocol == TimingProtocol.Ptp ? ptpClock : null);
            sessionUp = true; // from here on a TEARDOWN is owed, even if the SETUP itself fails half-way
            var setup = await session.SetupSessionAsync(setupRequest, cancellationToken).ConfigureAwait(false);
            var ptpPeer = address;
            if (Transport.TimingProtocol == TimingProtocol.Ptp)
            {
                // SPEC-audio.md 4.4: the peer address comes from the receiver's timingPeerInfo in the SETUP answer.
                ptpPeer = PtpSessionWiring.SelectPeerAddress(setup.TimingPeerAddresses, address, session.Connection.LocalAddress);
            }

            if (Transport.TimingProtocol == TimingProtocol.Ptp && realtime is not null)
            {
                // SPEC-audio.md 4.4: the receiver follows our grandmaster; it must get Sync/Follow_Up unicast before audio.
                timingPeer = ptpPeer;
                realtime.AddTimingPeer(ptpPeer);
            }

            var events = await session.ConnectEventChannelAsync(cancellationToken).ConfigureAwait(false);
            var audioLatency = await session.RecordAsync(cancellationToken).ConfigureAwait(false);
            if (Transport.TimingProtocol == TimingProtocol.Ptp)
            {
                // SPEC-control.md 4.6: the receiver's own address first, then ours.
                await session.SetPeersAsync([ptpPeer, Normalize(session.Connection.LocalAddress)], cancellationToken).ConfigureAwait(false);
            }

            var key = Options.RandomStreamKey ? RandomNumberGenerator.GetBytes(32) : pairing.DefaultStreamKey;
            var streamRequest = new StreamSetupRequest(
                Transport.Type,
                key,
                Transport.ControlPort,
                LatencyMin: Math.Min(11025, Options.LatencyFrames),
                LatencyMax: Math.Max(88200, Options.LatencyFrames));
            var stream = await session.SetupStreamAsync(streamRequest, cancellationToken).ConfigureAwait(false);

            lock (_volumeLock)
            {
                Volume.SetReceiverLevel(receiver.Id, level);
            }

            await session.SetVolumeAsync(VolumeMapping.LevelToDb(level), cancellationToken).ConfigureAwait(false);

            // SPEC-audio.md 1.5/3.3: a published output latency is compensated per receiver, never by changing L. A receiver
            // whose output lags by d frames must be told to play d frames earlier, i.e. a negative offset (see the spec note).
            var offset = quirks.OffsetFrames - audioLatency;
            var endpoint = new ReceiverStreamEndpoint(new IPEndPoint(address, stream.DataPort), new IPEndPoint(address, stream.ControlPort), key, offset, quirks.RetransmitOnDataPort);
            var member = new Member(receiver, session, events, endpoint, timingPeer, level);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _members[receiver.Id] = member;
                lock (_sync)
                {
                    // Under _sync, where the pacer decides whom the next packet promotes (SPEC-audio.md 3.1 step 4).
                    Transport.AddReceiver(receiver.Id, endpoint);
                    member.InTransport = true;
                    member.PendingStream = true;
                    _promotionsPending = 1;
                    SetState(member, GroupMemberState.Connected, null);
                }
            }
            finally
            {
                _gate.Release();
            }

            LogMemberAdded(receiver.Name, setup.EventPort, stream.DataPort, stream.ControlPort, audioLatency, offset, level);
            member.Loops = RunMemberLoopsAsync(member);

            // The master level may have moved while the member was being set up.
            int current;
            lock (_volumeLock)
            {
                current = Volume.Levels.TryGetValue(receiver.Id, out var l) ? l : level;
            }

            if (current != level)
            {
                Track(SendLevelBestEffortAsync(member, current, force: false, CancellationToken.None), "volume after join");
            }

            // Mid-track join (SPEC-audio.md 7): the current track's items with the startup display lead.
            NowPlayingTrack? track;
            uint speaker;
            lock (_sync)
            {
                track = _nowPlaying.Track;
                speaker = Timeline.FrameAtSpeaker(Clock.NowNanos);
            }

            if (track is not null)
            {
                // Best effort and in the background: the member is registered and streaming already.
                Track(SendNowPlayingToAsync(member, track, speaker, member.Stop.Token), "metadata after join");
            }
        }
        catch (Exception ex)
        {
            LogMemberFailed(ex, receiver.Name);
            if (_members.TryGetValue(receiver.Id, out var registered) && ReferenceEquals(registered.Session, session))
            {
                Transport.RemoveReceiver(receiver.Id);
                await RemoveMemberCoreAsync(registered, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                if (sessionUp)
                {
                    await TeardownQuietlyAsync(session, receiver.Name).ConfigureAwait(false);
                }

                if (timingPeer is not null)
                {
                    realtime?.RemoveTimingPeer(timingPeer);
                }

                await session.DisposeAsync().ConfigureAwait(false);
                lock (_volumeLock)
                {
                    Volume.RemoveReceiver(receiver.Id);
                }
            }

            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>Unknown ids are ignored. The TEARDOWN (D7) is bounded by <see cref="GroupPlayerOptions.TeardownTimeout"/>.</remarks>
    public async Task RemoveReceiverAsync(string receiverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receiverId);
        if (!_members.TryGetValue(receiverId, out var member))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Transport.RemoveReceiver(receiverId);
            member.InTransport = false;
        }
        finally
        {
            _gate.Release();
        }

        await RemoveMemberCoreAsync(member, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the member's loops, sends TEARDOWN, closes the session and forgets the member. The transport registration must already be gone.</summary>
    private async Task RemoveMemberCoreAsync(Member member, CancellationToken cancellationToken)
    {
        if (!member.TryMarkRemoved())
        {
            return;
        }

        member.InTransport = false;
        await member.Stop.CancelAsync().ConfigureAwait(false);
        if (member.Loops is { } loops)
        {
            await AwaitQuietlyAsync(loops).ConfigureAwait(false);
        }

        if (!member.Session.Connection.Closed.IsCompleted)
        {
            await TeardownQuietlyAsync(member.Session, member.Receiver.Name, cancellationToken).ConfigureAwait(false);
        }

        if (member.TimingPeer is { } peer && Transport is RealtimeStreamTransport realtime)
        {
            realtime.RemoveTimingPeer(peer);
        }

        await member.Session.DisposeAsync().ConfigureAwait(false);
        _members.TryRemove(new KeyValuePair<string, Member>(member.Id, member));
        _states.TryRemove(member.Id, out _);
        lock (_volumeLock)
        {
            Volume.RemoveReceiver(member.Id);
        }

        member.FailVolumeWaiters(new InvalidOperationException($"{member.Receiver.Name} was removed from the group."));
        Publish(new GroupPlayerEvent.MemberStateChanged(member.Id, GroupMemberState.Lost, "removed"));
        LogMemberRemoved(member.Receiver.Name);
    }

    private async Task TeardownQuietlyAsync(IReceiverSession session, string name, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.TeardownTimeout);
        try
        {
            await session.TeardownAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or RtspStatusException or ObjectDisposedException or InvalidOperationException)
        {
            LogTeardownFailed(name, ex.Message);
        }
    }

    /// <summary>
    /// A member stops counting: its transport registration goes (the group keeps playing), its loops stop, an event is
    /// published. The session is closed by <see cref="RemoveReceiverAsync"/> or <see cref="DisposeAsync"/>.
    /// </summary>
    private void MarkLost(Member member, string reason)
    {
        if (!member.TryMarkLost() || member.IsRemoved)
        {
            return;
        }

        Transport.RemoveReceiver(member.Id);
        member.InTransport = false;
        SetState(member, GroupMemberState.Lost, reason);
        member.Stop.Cancel();
        member.FailVolumeWaiters(new InvalidOperationException($"{member.Receiver.Name} is lost: {reason}"));
        LogMemberLost(member.Receiver.Name, reason);
    }

    private void SetState(Member member, GroupMemberState state, string? reason)
    {
        if (member.IsRemoved)
        {
            return;
        }

        member.State = state;
        _states[member.Id] = state;
        Publish(new GroupPlayerEvent.MemberStateChanged(member.Id, state, reason));
    }

    /// <summary>FLUSH every active member with the next values (SPEC-control.md 4.8), back to CONNECTED, volume re-sent (5.1).</summary>
    private async Task FlushMembersAsync(ushort sequence, uint timestamp, CancellationToken cancellationToken)
    {
        var flushes = new List<Task>();
        foreach (var member in _members.Values)
        {
            if (member.State != GroupMemberState.Lost && member.InTransport)
            {
                flushes.Add(FlushMemberAsync(member, sequence, timestamp, cancellationToken));
            }
        }

        await Task.WhenAll(flushes).ConfigureAwait(false);
    }

    private async Task FlushMemberAsync(Member member, ushort sequence, uint timestamp, CancellationToken cancellationToken)
    {
        try
        {
            await member.Session.FlushAsync(sequence, timestamp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or RtspStatusException or ObjectDisposedException)
        {
            LogFlushFailed(member.Receiver.Name, ex.Message);
            if (member.Session.Connection.Closed.IsCompleted)
            {
                MarkLost(member, "FLUSH failed: " + ex.Message);
                return;
            }
        }

        lock (_sync)
        {
            if (member.State == GroupMemberState.Lost || !member.InTransport)
            {
                return;
            }

            Transport.Resync(member.Id);
            member.PendingStream = true;
            _promotionsPending = 1;
            SetState(member, GroupMemberState.Flushed, null);
        }

        Track(SendLevelBestEffortAsync(member, member.CurrentDesiredLevel, force: true, CancellationToken.None), "volume after FLUSH");
    }

    /// <summary>
    /// Under <see cref="_sync"/>, before the timeline is re-anchored after a stop: every active member goes back to
    /// CONNECTED in the transport so the first packet of the new anchor carries its <c>0x90</c> sync and the marker
    /// (SPEC-audio.md 3.4, 8.4). Idempotent; covers members whose FLUSH was cancelled or failed (they never reached
    /// FLUSHED and would otherwise keep the old <c>(now_frame, T)</c> relation).
    /// </summary>
    private void ResyncActiveMembers()
    {
        foreach (var member in _members.Values)
        {
            if (member.State == GroupMemberState.Lost || !member.InTransport)
            {
                continue;
            }

            Transport.Resync(member.Id);
            member.PendingStream = true;
            _promotionsPending = 1;
            if (member.State == GroupMemberState.Streaming)
            {
                SetState(member, GroupMemberState.Connected, "re-anchored without a completed FLUSH");
            }
        }
    }

    private async Task RunMemberLoopsAsync(Member member)
    {
        await Task.Yield();
        var stop = member.Stop.Token;
        var loops = new List<Task>
        {
            GuardAsync(member, "feedback", FeedbackLoopAsync(member, stop)),
            GuardAsync(member, "volume", VolumeLoopAsync(member, stop)),
            GuardAsync(member, "connection watch", WatchConnectionAsync(member, stop)),
            GuardAsync(member, "progress", ProgressLoopAsync(member, stop)),
        };
        if (member.Events is { } events)
        {
            loops.Add(GuardAsync(member, "event channel", EventLoopAsync(member, events, stop)));
        }

        if (Options.VolumePollInterval is { } poll)
        {
            loops.Add(GuardAsync(member, "volume poll", VolumePollLoopAsync(member, poll, stop)));
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task GuardAsync(Member member, string what, Task loop)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (member.Stop.IsCancellationRequested)
        {
            // member removed or lost
        }
        catch (Exception ex)
        {
            LogLoopFailed(ex, what, member.Receiver.Name);
        }
    }

    /// <summary>SPEC-control.md 4.10: <c>POST /feedback</c> every 2 s; 3 consecutive failures or a closed connection: lost.</summary>
    private async Task FeedbackLoopAsync(Member member, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Options.FeedbackInterval);
        var failures = 0;
        while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
        {
            try
            {
                await member.Session.FeedbackAsync(stop).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or RtspStatusException or ObjectDisposedException)
            {
                failures++;
                if (failures >= ReceiverSession.FeedbackFailureLimit || member.Session.Connection.Closed.IsCompleted)
                {
                    MarkLost(member, $"POST /feedback failed {failures} time(s): {ex.Message}");
                    return;
                }
            }
        }
    }

    /// <summary>SPEC-control.md 6.5: EOF on the control connection means the receiver ended the session.</summary>
    private async Task WatchConnectionAsync(Member member, CancellationToken stop)
    {
        var reason = await member.Session.Connection.Closed.WaitAsync(stop).ConfigureAwait(false);
        MarkLost(member, "control connection closed: " + reason);
    }

    /// <summary>SPEC-audio.md 7 / SPEC-control.md 5.4: progress alone every 25 s while playing (Apple TV/HomePod keep-alive).</summary>
    private async Task ProgressLoopAsync(Member member, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Options.ProgressInterval);
        while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
        {
            await SendProgressAsync(member, stop).ConfigureAwait(false);
        }
    }

    private async Task ResendProgressAsync(CancellationToken cancellationToken)
    {
        var sends = new List<Task>();
        foreach (var member in _members.Values)
        {
            sends.Add(SendProgressAsync(member, cancellationToken));
        }

        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task SendProgressAsync(Member member, CancellationToken cancellationToken)
    {
        if (member.State == GroupMemberState.Lost || !member.HadTrack || !member.Receiver.Capabilities.WantsProgress)
        {
            return;
        }

        NowPlayingTrack? track;
        uint speaker;
        bool started;
        lock (_sync)
        {
            track = _nowPlaying.Track;
            speaker = Timeline.FrameAtSpeaker(Clock.NowNanos);
            started = _started;
        }

        if (track is null || !started)
        {
            return;
        }

        try
        {
            await member.Session.SetProgressAsync(NowPlayingTracker.Progress(track, speaker, member.DisplayLead), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or RtspStatusException or ObjectDisposedException)
        {
            LogMetadataFailed("progress", member.Receiver.Name, ex.Message);
        }
    }

    /// <summary>Text, artwork and progress of <paramref name="track"/> to one member, each only when its TXT asks for it; best effort.</summary>
    private async Task SendNowPlayingToAsync(Member member, NowPlayingTrack track, uint speaker, CancellationToken cancellationToken)
    {
        var lead = member.HadTrack ? NowPlayingTracker.SwitchDisplayLead : NowPlayingTracker.StartupDisplayLead;
        member.DisplayLead = lead;
        member.HadTrack = true;
        var wants = member.Receiver.Capabilities;
        var item = "DMAP";
        try
        {
            if (wants.WantsTextMetadata)
            {
                await member.Session.SetMetadataAsync(track.Metadata, track.Start, cancellationToken).ConfigureAwait(false);
            }

            item = "artwork";
            if (wants.WantsArtwork && track.Artwork is { } artwork && track.ArtworkContentType is { } type)
            {
                await member.Session.SetArtworkAsync(artwork, type, track.Start, cancellationToken).ConfigureAwait(false);
            }

            item = "progress";
            if (wants.WantsProgress)
            {
                await member.Session.SetProgressAsync(NowPlayingTracker.Progress(track, speaker, lead), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or RtspStatusException or ObjectDisposedException or ArgumentException)
        {
            LogMetadataFailed(item, member.Receiver.Name, ex.Message);
        }
    }

    /// <summary>
    /// The receiver's event channel (SPEC-control.md 4.3, 6.5; SPEC-audio.md 6.2): <c>updateInfo</c> with
    /// <c>initialVolume</c> is a device-side level; <c>sendMediaRemoteCommand</c> becomes <see cref="GroupPlayerEvent.RemoteCommand"/>;
    /// unknown messages are logged verbatim (C-7.4); a close by the receiver means the member is lost.
    /// </summary>
    private async Task EventLoopAsync(Member member, IEventChannel channel, CancellationToken stop)
    {
        // Messages already queued when the member starts were sent while it was being set up, before our first
        // SET_PARAMETER volume: shairport-sync sends updateInfo with its previous volume right when the channel connects
        // ([sps] ap2_event_message_handler.c:109-139, SPEC-audio.md 6.2). Their initialVolume is not a device-side change.
        var queuedAtStart = channel.Messages.CanCount ? channel.Messages.Count : 0;
        await foreach (var message in channel.Messages.ReadAllAsync(stop).ConfigureAwait(false))
        {
            var stale = queuedAtStart-- > 0;
            switch (message)
            {
                case ReceiverEventMessage.UpdateInfo update:
                    LogEvent(member.Receiver.Name, stale ? "updateInfo (from before the session's first volume; level ignored)" : "updateInfo");
                    if (!stale && update.Value.GetDouble("initialVolume") is { } db)
                    {
                        OnReceiverVolume(member, db, "updateInfo");
                    }

                    break;
                case ReceiverEventMessage.MediaRemoteCommand command:
                    LogEvent(member.Receiver.Name, "sendMediaRemoteCommand " + command.Command);
                    Publish(new GroupPlayerEvent.RemoteCommand(member.Id, command.Command));
                    break;
                case ReceiverEventMessage.Unknown unknown:
                    LogUnknownEvent(member.Receiver.Name, unknown.Request.Method, unknown.Request.Uri, unknown.Type ?? "(none)", unknown.Body?.Count ?? 0);
                    break;
                case ReceiverEventMessage.Closed closed:
                    MarkLost(member, "event channel closed: " + closed.Reason);
                    return;
            }
        }
    }

    /// <summary>D10 fallback: <c>GET_PARAMETER volume</c> every few seconds while playing (the srcvers-366 speaker sends no volume events, P4/P13).</summary>
    private async Task VolumePollLoopAsync(Member member, TimeSpan interval, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
        {
            if (!IsStarted || member.State == GroupMemberState.Lost || member.VolumeChangePending)
            {
                continue;
            }

            double db;
            await member.RtspVolumeGate.WaitAsync(stop).ConfigureAwait(false);
            try
            {
                db = await member.Session.GetVolumeAsync(stop).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                LogPollUnsupported(member.Receiver.Name, ex.Message);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or RtspStatusException or ObjectDisposedException)
            {
                LogPollFailed(member.Receiver.Name, ex.Message);
                continue;
            }
            finally
            {
                member.RtspVolumeGate.Release();
            }

            OnReceiverVolume(member, db, "GET_PARAMETER");
        }
    }

    /// <summary>SPEC-audio.md 6.3: a level the receiver reports updates its r_i only, unless it is our own change in flight.</summary>
    private void OnReceiverVolume(Member member, double db, string source)
    {
        var level = VolumeMapping.DbToLevel(db);
        if (!member.TryAcceptReportedLevel(level))
        {
            return;
        }

        lock (_volumeLock)
        {
            Volume.ReceiverReported(member.Id, level);
        }

        LogReceiverVolume(member.Receiver.Name, level, db, source);
        Publish(new GroupPlayerEvent.ReceiverVolumeReported(member.Id, level));
    }

    private async Task SendLevelBestEffortAsync(Member member, int level, bool force, CancellationToken cancellationToken)
    {
        try
        {
            await RequestVolumeAsync(member, level, force, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // the member's volume sender stopped (removed or lost)
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or RtspStatusException or InvalidOperationException or ObjectDisposedException)
        {
            LogVolumeFailed(member.Receiver.Name, level, ex.Message);
        }
    }

    /// <summary>Queues a level for the member's volume sender and completes when a request carrying it (or a later one) was answered.</summary>
    private static Task RequestVolumeAsync(Member member, int level, bool force, CancellationToken cancellationToken)
    {
        var task = member.QueueLevel(level, force);
        member.VolumeSignal.Writer.TryWrite(true);
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// SPEC.md D10 / SPEC-audio.md 6.1, 6.3: one <c>SET_PARAMETER volume</c> at a time per receiver, at most one per
    /// <see cref="GroupPlayerOptions.VolumeMinInterval"/>; intermediate levels queued meanwhile collapse into the latest.
    /// </summary>
    private async Task VolumeLoopAsync(Member member, CancellationToken stop)
    {
        try
        {
            while (await member.VolumeSignal.Reader.WaitToReadAsync(stop).ConfigureAwait(false))
            {
                member.VolumeSignal.Reader.TryRead(out _);
                while (member.TakePendingLevel(out var level, out var generation, out var force))
                {
                    Exception? error = null;
                    await member.RtspVolumeGate.WaitAsync(stop).ConfigureAwait(false);
                    try
                    {
                        if (force || level != member.SentLevel)
                        {
                            await member.Session.SetVolumeAsync(VolumeMapping.LevelToDb(level), stop).ConfigureAwait(false);
                            LogVolumeSent(member.Receiver.Name, level, VolumeMapping.LevelToDb(level));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or RtspStatusException or ObjectDisposedException)
                    {
                        error = ex;
                        LogVolumeFailed(member.Receiver.Name, level, ex.Message);
                    }
                    finally
                    {
                        member.RtspVolumeGate.Release();
                    }

                    member.CompleteLevel(level, generation, error);
                    await Task.Delay(Options.VolumeMinInterval, stop).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            member.FailVolumeWaiters(new OperationCanceledException("The receiver's volume sender stopped."));
        }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>One receiver of the group.</summary>
    private sealed class Member(DiscoveredReceiver receiver, IReceiverSession session, IEventChannel? events, ReceiverStreamEndpoint endpoint, IPAddress? timingPeer, int level)
    {
        private readonly Lock _volume = new();
        private readonly List<(long Generation, TaskCompletionSource Waiter)> _waiters = [];
        private int _desiredLevel = level;
        private long _desiredGeneration;
        private long _sentGeneration;
        private bool _force;
        private int _lost;
        private int _removed;
        private volatile GroupMemberState _state = GroupMemberState.Connected;
        private volatile bool _inTransport;
        private volatile bool _pendingStream;
        private volatile bool _hadTrack;

        public string Id => Receiver.Id;

        public DiscoveredReceiver Receiver { get; } = receiver;

        public IReceiverSession Session { get; } = session;

        public IEventChannel? Events { get; } = events;

        public ReceiverStreamEndpoint Endpoint { get; } = endpoint;

        public IPAddress? TimingPeer { get; } = timingPeer;

        public CancellationTokenSource Stop { get; } = new();

        public Task? Loops { get; set; }

        public GroupMemberState State
        {
            get => _state;
            set => _state = value;
        }

        /// <summary>Registered with the transport.</summary>
        public bool InTransport
        {
            get => _inTransport;
            set => _inTransport = value;
        }

        /// <summary>CONNECTED in the transport; the next packet carries its initial sync and marker (set and taken under the player's <c>_sync</c>).</summary>
        public bool PendingStream
        {
            get => _pendingStream;
            set => _pendingStream = value;
        }

        public bool HadTrack
        {
            get => _hadTrack;
            set => _hadTrack = value;
        }

        public bool IsRemoved => Volatile.Read(ref _removed) != 0;

        /// <summary>True for the first caller only.</summary>
        public bool TryMarkLost() => Interlocked.Exchange(ref _lost, 1) == 0;

        /// <summary>True for the first caller only.</summary>
        public bool TryMarkRemoved() => Interlocked.Exchange(ref _removed, 1) == 0;

        public uint DisplayLead { get; set; } = NowPlayingTracker.StartupDisplayLead;

        /// <summary>Serialises this member's volume set and get requests.</summary>
        public SemaphoreSlim RtspVolumeGate { get; } = new(1, 1);

        /// <summary>Wakes the volume sender (capacity 1: signals collapse).</summary>
        public Channel<bool> VolumeSignal { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

        /// <summary>The level last confirmed on the wire (set by us, or reported by the receiver).</summary>
        public int SentLevel { get; private set; } = level;

        public int CurrentDesiredLevel
        {
            get
            {
                lock (_volume)
                {
                    return _desiredLevel;
                }
            }
        }

        public bool VolumeChangePending
        {
            get
            {
                lock (_volume)
                {
                    return _desiredGeneration != _sentGeneration;
                }
            }
        }

        public Task QueueLevel(int level, bool force)
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_volume)
            {
                _desiredLevel = level;
                _force |= force;
                _waiters.Add((++_desiredGeneration, waiter));
            }

            return waiter.Task;
        }

        public bool TakePendingLevel(out int level, out long generation, out bool force)
        {
            lock (_volume)
            {
                level = _desiredLevel;
                generation = _desiredGeneration;
                force = _force;
                _force = false;
                return generation != _sentGeneration;
            }
        }

        public void CompleteLevel(int level, long generation, Exception? error)
        {
            List<TaskCompletionSource> done = [];
            lock (_volume)
            {
                _sentGeneration = generation;
                if (error is null)
                {
                    SentLevel = level;
                }

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Generation <= generation)
                    {
                        done.Add(_waiters[i].Waiter);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var waiter in done)
            {
                if (error is null)
                {
                    waiter.TrySetResult();
                }
                else
                {
                    waiter.TrySetException(error);
                }
            }
        }

        /// <summary>A level the receiver reports: accepted unless our own change is in flight or it is what we sent.</summary>
        public bool TryAcceptReportedLevel(int level)
        {
            lock (_volume)
            {
                if (_desiredGeneration != _sentGeneration || level == SentLevel)
                {
                    return false;
                }

                SentLevel = level;
                _desiredLevel = level;
                return true;
            }
        }

        public void FailVolumeWaiters(Exception error)
        {
            List<TaskCompletionSource> pending;
            lock (_volume)
            {
                pending = [.. _waiters.Select(w => w.Waiter)];
                _waiters.Clear();
                _sentGeneration = _desiredGeneration;
            }

            foreach (var waiter in pending)
            {
                waiter.TrySetException(error);
            }
        }
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "{Receiver} prefers {Wanted} timing but the group's transport runs {Actual}")]
    private partial void LogTimingMismatch(string receiver, TimingProtocol wanted, TimingProtocol actual);

    [LoggerMessage(EventId = 41, Level = LogLevel.Information, Message = "{Receiver} joined (CONNECTED): eventPort {EventPort}, data {DataPort}, control {ControlPort}, Audio-Latency {AudioLatency}, offset {Offset} frames, level {Level}")]
    private partial void LogMemberAdded(string receiver, int eventPort, int dataPort, int controlPort, int audioLatency, int offset, int level);

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning, Message = "Adding {Receiver} failed; what was set up is torn down")]
    private partial void LogMemberFailed(Exception exception, string receiver);

    [LoggerMessage(EventId = 43, Level = LogLevel.Information, Message = "{Receiver} removed from the group")]
    private partial void LogMemberRemoved(string receiver);

    [LoggerMessage(EventId = 44, Level = LogLevel.Warning, Message = "TEARDOWN of {Receiver} failed: {Error}")]
    private partial void LogTeardownFailed(string receiver, string error);

    [LoggerMessage(EventId = 45, Level = LogLevel.Warning, Message = "{Receiver} lost: {Reason}; the group plays on without it")]
    private partial void LogMemberLost(string receiver, string reason);

    [LoggerMessage(EventId = 46, Level = LogLevel.Warning, Message = "FLUSH to {Receiver} failed: {Error}")]
    private partial void LogFlushFailed(string receiver, string error);

    [LoggerMessage(EventId = 47, Level = LogLevel.Error, Message = "The {What} loop of {Receiver} failed")]
    private partial void LogLoopFailed(Exception exception, string what, string receiver);

    [LoggerMessage(EventId = 48, Level = LogLevel.Warning, Message = "SET_PARAMETER {Item} to {Receiver} failed: {Error}")]
    private partial void LogMetadataFailed(string item, string receiver, string error);

    [LoggerMessage(EventId = 49, Level = LogLevel.Debug, Message = "Event from {Receiver}: {What}")]
    private partial void LogEvent(string receiver, string what);

    [LoggerMessage(EventId = 50, Level = LogLevel.Information, Message = "Unknown event-channel message from {Receiver}: {Method} {Uri}, type {Type}, {Keys} keys (C-7.4)")]
    private partial void LogUnknownEvent(string receiver, string method, string uri, string type, int keys);

    [LoggerMessage(EventId = 51, Level = LogLevel.Information, Message = "{Receiver} answers GET_PARAMETER volume without a volume line ({Error}); polling stops")]
    private partial void LogPollUnsupported(string receiver, string error);

    [LoggerMessage(EventId = 52, Level = LogLevel.Debug, Message = "Volume poll of {Receiver} failed: {Error}")]
    private partial void LogPollFailed(string receiver, string error);

    [LoggerMessage(EventId = 53, Level = LogLevel.Information, Message = "{Receiver} reports level {Level} ({Db} dB, via {Source}); only its r_i changes")]
    private partial void LogReceiverVolume(string receiver, int level, double db, string source);

    [LoggerMessage(EventId = 54, Level = LogLevel.Warning, Message = "Setting level {Level} on {Receiver} failed: {Error}")]
    private partial void LogVolumeFailed(string receiver, int level, string error);

    [LoggerMessage(EventId = 55, Level = LogLevel.Debug, Message = "Level {Level} ({Db} dB) sent to {Receiver}")]
    private partial void LogVolumeSent(string receiver, int level, double db);
}
