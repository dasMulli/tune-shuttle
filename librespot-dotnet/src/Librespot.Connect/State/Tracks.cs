// Port of connect/src/state/tracks.rs.
using Google.Protobuf.Collections;
using Librespot.Core;
using Microsoft.Extensions.Logging;
using ProvidedTrack = Spotify.Connectstate.ProvidedTrack;

namespace Librespot.Connect;

internal sealed partial class ConnectState
{
    /// <summary>identifier used as part of the uid</summary>
    public const string IdentifierDelimiter = "delimiter";

    private static ProvidedTrack NewDelimiter(long iteration)
    {
        var delimiter = new ProvidedTrack
        {
            Uri = $"spotify:{IdentifierDelimiter}",
            Uid = $"{IdentifierDelimiter}{iteration}",
            Provider = Provider.Context.ToDisplayString(),
        };
        delimiter.Metadata.SetHidden(true);
        delimiter.Metadata.SetIteration(iteration);

        return delimiter;
    }

    private void PushPrev(ProvidedTrack prev)
    {
        var prevTracks = PrevTracks;
        // add prev track, while preserving a length of 10
        if (prevTracks.Count >= SpotifyMaxPrevTracksSize)
        {
            // todo: O(n), but technically only maximal O(SPOTIFY_MAX_PREV_TRACKS_SIZE) aka O(10)
            prevTracks.RemoveAt(0);
        }

        prevTracks.Add(prev);
    }

    private ProvidedTrack? GetNextTrack()
    {
        if (NextTracks.Count == 0)
        {
            return null;
        }

        // todo: O(n), but technically only maximal O(SPOTIFY_MAX_NEXT_TRACKS_SIZE) aka O(80)
        var next = NextTracks[0];
        NextTracks.RemoveAt(0);
        return next;
    }

    /// <summary>bottom => top, aka the last track of the list is the prev track</summary>
    internal RepeatedField<ProvidedTrack> PrevTracks => Player.PrevTracks;

    /// <summary>top => bottom, aka the first track of the list is the next track</summary>
    internal RepeatedField<ProvidedTrack> NextTracks => Player.NextTracks;

    /// <summary>Sets a random track of the active context as current track.</summary>
    public void SetCurrentTrackRandom()
    {
        var maxTracks = GetContext(ActiveContext).Tracks.Count;
        var rngTrack = Random.Shared.Next(0, maxTracks);
        SetCurrentTrack(rngTrack);
    }

    /// <summary>Sets the track at <paramref name="index"/> of the active context as current track.</summary>
    /// <exception cref="StateException">No active context, or the index is out of range.</exception>
    public void SetCurrentTrack(int index)
    {
        var context = GetContext(ActiveContext);

        if (index < 0 || index >= context.Tracks.Count)
        {
            throw StateException.CanNotFindTrackInContext(index, context.Tracks.Count);
        }

        var newTrack = context.Tracks[index];

        Log.LogDebug("set track to: {Uri} at {Index} of {Count} tracks", newTrack.Uri, index, context.Tracks.Count);

        SetTrack(newTrack.Clone());

        UpdateCurrentIndex(i => i.Track = (uint)index);
    }

    /// <summary>
    /// Move to the next track: updates the current track to the next track, adds the old track to prev tracks and
    /// fills up the next tracks from the current context. Returns the new index, or null when there is no next track.
    /// </summary>
    public uint? NextTrack()
    {
        // when we skip in repeat track, we don't repeat the current track anymore
        if (RepeatTrack)
        {
            SetRepeatTrack(false);
        }

        var oldTrack = Player.Track;
        Player.Track = null;

        // only add songs from our context to our previous tracks
        if (oldTrack is not null && (oldTrack.IsContext() || oldTrack.IsAutoplay()))
        {
            PushPrev(oldTrack);
        }

        ProvidedTrack? newTrack;
        while (true)
        {
            var next = GetNextTrack();
            if (next is not null && next.Uid.StartsWith(IdentifierDelimiter, StringComparison.Ordinal))
            {
                PushPrev(next);
                continue;
            }

            if (next is not null && next.IsUnavailable())
            {
                continue;
            }

            newTrack = next;
            break;
        }

        if (newTrack is null)
        {
            return null;
        }

        FillUpNextTracks();

        uint? updateIndex;
        if (newTrack.IsQueue())
        {
            updateIndex = null;
        }
        else if (newTrack.IsAutoplay())
        {
            SetActiveContext(ContextType.Autoplay);
            updateIndex = null;
        }
        else if (newTrack.Metadata.GetContextIndex() is { } newIndex)
        {
            updateIndex = (uint)newIndex;
        }
        else
        {
            Log.LogError("the given context track had no set context_index");
            updateIndex = null;
        }

        if (updateIndex is { } index)
        {
            UpdateCurrentIndex(i => i.Track = index);
        }
        else
        {
            Player.Index = null;
        }

        SetTrack(newTrack);
        UpdateRestrictions();

        return Player.Index?.Track ?? 0;
    }

    /// <summary>
    /// Move to the prev track: updates the current track to the prev track, adds the old track to next tracks (when
    /// from the context) and fills up the prev tracks from the current context. Returns the new current track, or
    /// null when there is no previous track.
    /// </summary>
    public ProvidedTrack? PrevTrack()
    {
        var oldTrack = Player.Track;
        Player.Track = null;

        if (oldTrack is not null && (oldTrack.IsContext() || oldTrack.IsAutoplay()))
        {
            // todo: O(n)
            NextTracks.Insert(0, oldTrack);
        }

        // handle possible delimiter
        if (PrevTracks.Count > 0 && PrevTracks[^1].Uid.StartsWith(IdentifierDelimiter, StringComparison.Ordinal))
        {
            var delimiter = PrevTracks[^1];
            PrevTracks.RemoveAt(PrevTracks.Count - 1);

            var nextTracks = NextTracks;
            if (nextTracks.Count >= SpotifyMaxNextTracksSize)
            {
                nextTracks.RemoveAt(nextTracks.Count - 1);
            }

            // todo: O(n)
            nextTracks.Insert(0, delimiter);
        }

        while (NextTracks.Count > SpotifyMaxNextTracksSize)
        {
            NextTracks.RemoveAt(NextTracks.Count - 1);
        }

        if (PrevTracks.Count == 0)
        {
            return null;
        }

        var newTrack = PrevTracks[^1];
        PrevTracks.RemoveAt(PrevTracks.Count - 1);

        if (ActiveContext == ContextType.Autoplay && newTrack.IsContext())
        {
            // transition back to default context
            SetActiveContext(ContextType.Default);
        }

        FillUpNextTracks();
        SetTrack(newTrack);

        if ((Player.Index?.Track ?? 0) == 0)
        {
            Log.LogWarning("prev: trying to skip into negative, index update skipped");
        }
        else
        {
            UpdateCurrentIndex(i => i.Track -= 1);
        }

        UpdateRestrictions();

        return Player.Track;
    }

    /// <summary>
    /// Reads the current track (upstream <c>current_track(|t| ..)</c>). Like upstream's <c>MessageField</c> deref, an
    /// absent track is presented as an empty <see cref="ProvidedTrack"/>; check <c>Player.Track is null</c> for
    /// upstream's <c>MessageField::is_none</c>.
    /// </summary>
    public TResult CurrentTrack<TResult>(Func<ProvidedTrack, TResult> access) => access(Player.Track ?? new ProvidedTrack());

    /// <summary>Upstream <c>set_track</c>.</summary>
    public void SetTrack(ProvidedTrack track) => Player.Track = track;

    /// <summary>Replaces the next tracks (mobile sends <c>set_queue</c> instead of <c>add_to_queue</c>).</summary>
    public void SetNextTracks(List<ProvidedTrack> tracks)
    {
        // mobile only sends a set_queue command instead of an add_to_queue command
        // in addition to handling the mobile add_to_queue handling, this should also handle
        // a mass queue addition
        foreach (var t in tracks.Where(t => t.Metadata.IsFromQueue()))
        {
            t.SetProvider(Provider.Queue);
            // technically we could preserve the queue-uid here,
            // but it seems to work without that, so we just override it
            t.Uid = $"q{_queueCount}";
            _queueCount++;
        }

        // when you drag 'n drop the current track in the queue view into the "Next from: ..."
        // section, it is only send as an empty item with just the provider and metadata, so we have
        // to provide set the uri from the current track manually
        foreach (var t in tracks.Where(t => t.Uri.Length == 0))
        {
            t.Uri = CurrentTrack(ct => ct.Uri);
        }

        NextTracks.Clear();
        NextTracks.Add(tracks);
    }

    /// <summary>Upstream <c>set_prev_tracks</c>.</summary>
    public void SetPrevTracks(List<ProvidedTrack> tracks)
    {
        PrevTracks.Clear();
        PrevTracks.Add(tracks);
    }

    /// <summary>Upstream <c>clear_prev_track</c>.</summary>
    public void ClearPrevTrack() => PrevTracks.Clear();

    /// <summary>
    /// Remove all queued tracks: keeps the tracks from the context in the next tracks and fills them up from the
    /// current context afterwards.
    /// </summary>
    public void ClearQueue()
    {
        var retained = NextTracks.Where(t => !t.IsQueue()).ToList();
        NextTracks.Clear();
        NextTracks.Add(retained);

        FillUpNextTracks();
        UpdateQueueRevision();
    }

    /// <summary>Removes the next tracks after the queued tracks at the front.</summary>
    public void ClearNextTracks()
    {
        // respect queued track and don't throw them out of our next played tracks
        var firstNonQueuedTrack = -1;
        for (var i = 0; i < NextTracks.Count; i++)
        {
            if (!NextTracks[i].IsQueue())
            {
                firstNonQueuedTrack = i;
                break;
            }
        }

        if (firstNonQueuedTrack >= 0)
        {
            while (NextTracks.Count > firstNonQueuedTrack)
            {
                NextTracks.RemoveAt(NextTracks.Count - 1);
            }
        }
    }

    /// <summary>
    /// Fills the next tracks up to <see cref="SpotifyMaxNextTracksSize"/> from the fill-up context, inserting
    /// delimiters when the context repeats or transitions to autoplay.
    /// </summary>
    public void FillUpNextTracks()
    {
        var ctx = GetContext(_fillUpContext);
        var newIndex = (int)ctx.Index.Track;
        var iteration = ctx.Index.Page;

        while (NextTracks.Count < SpotifyMaxNextTracksSize)
        {
            ctx = GetContext(_fillUpContext);
            var ct = newIndex < ctx.Tracks.Count ? ctx.Tracks[newIndex] : null;

            ProvidedTrack track;
            if (ct is null)
            {
                if (RepeatContext)
                {
                    var delimiter = NewDelimiter(iteration);
                    iteration += 1;
                    newIndex = 0;
                    track = delimiter;
                }
                else if (_fillUpContext != ContextType.Autoplay && _autoplayContext is not null && !RepeatContext)
                {
                    UpdateContextIndex(_fillUpContext, newIndex);

                    // transition to autoplay as fill up context
                    _fillUpContext = ContextType.Autoplay;
                    newIndex = (int)GetContext(ContextType.Autoplay).Index.Track;

                    // add delimiter to only display the current context
                    track = NewDelimiter(iteration);
                }
                else if (_autoplayContext is not null)
                {
                    var autoplayTracks = GetContext(ContextType.Autoplay).Tracks;
                    if (newIndex >= autoplayTracks.Count)
                    {
                        break;
                    }

                    track = autoplayTracks[newIndex].Clone();
                    newIndex += 1;
                }
                else
                {
                    break;
                }
            }
            else if (ct.IsUnavailable() || IsSkipTrack(ct, iteration))
            {
                Log.LogDebug("skipped track {Uri} during fillup as it's unavailable or should be skipped", ct.Uri);
                newIndex += 1;
                continue;
            }
            else
            {
                newIndex += 1;
                track = ct.Clone();
            }

            NextTracks.Add(track);
        }

        Log.LogDebug("finished filling up next_tracks ({Count})", NextTracks.Count);

        UpdateContextIndex(_fillUpContext, newIndex);

        // the web-player needs a revision update, otherwise the queue isn't updated in the ui
        UpdateQueueRevision();
    }

    /// <summary>The uri of the track that plays next (the current one when repeating the track), or null.</summary>
    public SpotifyUri? PreviewNextTrack()
    {
        string next;
        if (RepeatTrack)
        {
            next = CurrentTrack(t => t.Uri);
        }
        else if (NextTracks.Count > 0)
        {
            next = NextTracks[0].Uri;
        }
        else
        {
            return null;
        }

        try
        {
            return SpotifyUri.FromUri(next);
        }
        catch (LibrespotException)
        {
            return null;
        }
    }

    /// <summary>Whether there are next tracks (at least <paramref name="min"/> when given).</summary>
    public bool HasNextTracks(int? min) => min is { } m ? NextTracks.Count >= m : NextTracks.Count > 0;

    /// <summary>The uris of the previous tracks followed by the current track.</summary>
    public List<string> RecentTrackUris()
    {
        var prev = PrevTracks.Select(t => t.Uri).ToList();

        prev.Add(CurrentTrack(t => t.Uri));
        return prev;
    }

    /// <summary>Removes the track from prev and next tracks and skips it in future fill-ups.</summary>
    public void MarkUnavailable(SpotifyUri id)
    {
        var uri = id.ToUri();

        Log.LogDebug("marking {Uri} as unavailable", uri);

        RemoveAll(NextTracks, uri);
        foreach (var nextTrack in NextTracks)
        {
            MarkAsUnavailableForMatch(nextTrack, uri);
        }

        RemoveAll(PrevTracks, uri);
        foreach (var prevTrack in PrevTracks)
        {
            MarkAsUnavailableForMatch(prevTrack, uri);
        }

        _unavailableUri.Add(uri);
        FillUpNextTracks();
        UpdateQueueRevision();

        static void RemoveAll(RepeatedField<ProvidedTrack> tracks, string uri)
        {
            for (var pos = tracks.Count - 1; pos >= 0; pos--)
            {
                if (tracks[pos].Uri == uri)
                {
                    tracks.RemoveAt(pos);
                }
            }
        }
    }

    /// <summary>Inserts the track after the already queued tracks.</summary>
    public void AddToQueue(ProvidedTrack track, bool revUpdate)
    {
        track.Uid = $"q{_queueCount}";
        _queueCount++;

        track.SetProvider(Provider.Queue);
        if (!track.Metadata.IsFromQueue())
        {
            track.Metadata.SetFromQueue(true);
        }

        var nextTracks = NextTracks;
        var nextNotQueuedTrack = -1;
        for (var i = 0; i < nextTracks.Count; i++)
        {
            if (!nextTracks[i].IsQueue())
            {
                nextNotQueuedTrack = i;
                break;
            }
        }

        if (nextNotQueuedTrack >= 0)
        {
            nextTracks.Insert(nextNotQueuedTrack, track);
        }
        else
        {
            nextTracks.Add(track);
        }

        while (nextTracks.Count > SpotifyMaxNextTracksSize)
        {
            nextTracks.RemoveAt(nextTracks.Count - 1);
        }

        if (revUpdate)
        {
            UpdateQueueRevision();
        }

        UpdateRestrictions();
    }
}
