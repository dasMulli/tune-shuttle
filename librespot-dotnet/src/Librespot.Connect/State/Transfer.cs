// Port of connect/src/state/transfer.rs.
using System.Globalization;
using Librespot.Core;
using Librespot.Protocol;
using Microsoft.Extensions.Logging;
using ContextTrack = Spotify.Player.Proto.ContextTrack;
using ProvidedTrack = Spotify.Connectstate.ProvidedTrack;
using TransferState = Spotify.Player.Proto.Transfer.TransferState;

namespace Librespot.Connect;

internal sealed partial class ConnectState
{
    /// <summary>The track that was playing on the transferring device (the first queued one when playing the queue).</summary>
    /// <exception cref="StateException">The transfer state has no such track.</exception>
    public ProvidedTrack CurrentTrackFromTransfer(TransferState transfer)
    {
        var isPlayingQueue = transfer.Queue?.IsPlayingQueue ?? false;

        ContextTrack? track;
        if (isPlayingQueue)
        {
            Log.LogDebug("transfer track was used from the queue");
            track = transfer.Queue!.Tracks.FirstOrDefault();
        }
        else
        {
            Log.LogDebug("transfer track was the current track");
            track = transfer.Playback?.CurrentTrack;
        }

        if (track is null)
        {
            throw StateException.CouldNotResolveTrackFromTransfer();
        }

        var contextUri = transfer.CurrentSession?.Context is { HasUri: true } context ? context.Uri : null;

        return ContextToProvidedTrack(track, contextUri, null, null, isPlayingQueue ? Provider.Queue : null);
    }

    /// <summary>
    /// Handles the initially transferable data: options, play/pause state, origin, suppressions, context
    /// restrictions and metadata. Takes the consumed parts out of <paramref name="transfer"/> like upstream.
    /// </summary>
    public void HandleInitialTransfer(TransferState transfer, string? ctxUri)
    {
        var currentContextMetadata = _context is { } current ? new Dictionary<string, string>(current.Metadata) : null;
        var player = Player;

        player.IsBuffering = false;

        if (transfer.Options is { } options)
        {
            transfer.Options = null;
            player.Options = options.ToConnectState();
        }

        player.IsPaused = transfer.Playback?.IsPaused ?? false;
        player.IsPlaying = !player.IsPaused;

        player.PlaybackSpeed = transfer.Playback is { HasPlaybackSpeed: true } playback && playback.PlaybackSpeed != 0.0
            ? playback.PlaybackSpeed
            : 1.0;

        ulong? shuffleSeed = null;
        string? initialTrack = null;
        if (transfer.CurrentSession is { } session)
        {
            player.PlayOrigin = session.PlayOrigin?.ToConnectState();
            session.PlayOrigin = null;
            player.Suppressions = session.Suppressions?.ToConnectState();
            session.Suppressions = null;

            // maybe at some point we can use the shuffle seed provided by spotify,
            // but I doubt it, as spotify doesn't use true randomness but rather an algorithm
            // based shuffle
            Log.LogTrace(
                "shuffle_seed: <{SpotifySeed}> (spotify), <{OwnSeed}> (own)",
                RustDebug.Option(session.HasShuffleSeed ? session.ShuffleSeed : null),
                RustDebug.Option(session.Context?.Metadata.GetShuffleSeed()));

            shuffleSeed = session.Context?.Metadata.GetShuffleSeed() is { } seed
                && ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

            initialTrack = session.Context?.Metadata.GetInitialTrack();

            if (session.Context is { } ctx)
            {
                session.Context = null;
                player.Restrictions = ctx.Restrictions?.ToConnectState();
                ctx.Restrictions = null;
                foreach (var (key, value) in ctx.Metadata)
                {
                    player.ContextMetadata[key] = value;
                }
            }
        }

        const string UnknownUri = "spotify:unknown";
        // it's important to always set the url/uri to a value
        // so that the player doesn't go into an inactive state
        var uri = ctxUri ?? UnknownUri;

        player.ContextUrl = $"context://{uri}";
        player.ContextUri = uri;

        if (currentContextMetadata is not null)
        {
            foreach (var (key, value) in currentContextMetadata)
            {
                player.ContextMetadata[key] = value;
            }
        }

        _transferShuffle = shuffleSeed is { } s && initialTrack is not null ? new ShuffleState(s, initialTrack) : null;

        ClearPrevTrack();
        ClearNextTracks();
        UpdateQueueRevision();
    }

    /// <summary>Completes the transfer, loading the queue and updating metadata.</summary>
    public void FinishTransfer(TransferState transfer)
    {
        var track = Player.Track is { } playerTrack ? playerTrack.Clone() : CurrentTrackFromTransfer(transfer);

        var contextTy = CurrentTrack(t => t.Metadata.IsFromAutoplay()) ? ContextType.Autoplay : ContextType.Default;

        SetActiveContext(contextTy);
        _fillUpContext = contextTy;

        var ctx = GetContext(ActiveContext);

        int? currentIndex;
        if (transfer.CurrentSession is { HasCurrentUid: true } session && track.IsQueue())
        {
            var uid = session.CurrentUid;
            currentIndex = TryFindIndexInContext(ctx, c => c.Uid == uid) is { } i ? (i > 0 ? i - 1 : i) : null;
        }
        else
        {
            currentIndex = TryFindIndexInContext(ctx, c => c.Uri == track.Uri || c.Uid == track.Uid);
        }

        Log.LogDebug(
            "active track is <{Uri}> with index {CurrentIndex} in {ActiveContext} context, has {Count} tracks",
            track.Uri,
            RustDebug.Option(currentIndex),
            ActiveContext,
            ctx.Tracks.Count);

        if (Player.Track is null)
        {
            SetTrack(track);
        }

        if (currentIndex is { } index)
        {
            UpdateCurrentIndex(i => i.Track = (uint)index);
        }

        Log.LogDebug(
            "setting up next and prev: index is at {CurrentIndex} while shuffle {Shuffle}",
            RustDebug.Option(currentIndex),
            ShufflingContext);

        var isPlayingQueue = transfer.Queue?.IsPlayingQueue ?? false;
        IReadOnlyList<ContextTrack> queuedTracks = transfer.Queue is { } queue ? queue.Tracks : [];
        for (var i = 0; i < queuedTracks.Count; i++)
        {
            if (isPlayingQueue && i == 0)
            {
                // if we are currently playing from the queue,
                // don't add the first queued item, because we are currently playing that item
                continue;
            }

            ProvidedTrack queuedTrack;
            try
            {
                queuedTrack = ContextToProvidedTrack(queuedTracks[i], ContextUri, null, null, Provider.Queue);
            }
            catch (LibrespotException)
            {
                // upstream: `if let Ok(queued_track) = ..` skips tracks that can't be converted
                continue;
            }

            AddToQueue(queuedTrack, false);
        }

        if (ShufflingContext)
        {
            SetCurrentTrack(currentIndex ?? 0);
            SetShuffle(true);

            var transferShuffle = _transferShuffle;
            _transferShuffle = null;
            if (transferShuffle is null)
            {
                ShuffleNew();
            }
            else
            {
                ShuffleRestore(transferShuffle);
            }
        }
        else
        {
            ResetPlaybackToPosition(currentIndex);
        }

        UpdateRestrictions();
    }
}
