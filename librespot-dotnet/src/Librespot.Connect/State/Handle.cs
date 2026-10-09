// Port of connect/src/state/handle.rs.
using Librespot.Core.Dealer;

namespace Librespot.Connect;

internal sealed partial class ConnectState
{
    /// <summary>Enables (new shuffle) or disables (restore the context order at the current track) shuffle.</summary>
    public void HandleShuffle(bool shuffle)
    {
        SetShuffle(shuffle);

        if (shuffle)
        {
            ShuffleNew();
            return;
        }

        ResetContext(new ResetContextKind.DefaultIndex());

        if (Player.Track is null)
        {
            return;
        }

        if (CurrentTrack(t => t.Metadata.GetContextIndex()) is { } currentIndex)
        {
            ResetPlaybackToPosition(currentIndex);
        }
        else
        {
            var ctx = GetContext(ContextType.Default);
            var index = FindIndexInContext(ctx, c => CurrentTrack(t => c.Uri == t.Uri));
            ResetPlaybackToPosition(index);
        }
    }

    /// <summary>Replaces the next and previous tracks with the ones of a <c>set_queue</c> command.</summary>
    public void HandleSetQueue(SetQueueCommand setQueue)
    {
        // upstream moves the command's tracks into the state; clone them so the command stays untouched
        SetNextTracks(setQueue.NextTracks.Select(t => t.Clone()).ToList());
        SetPrevTracks(setQueue.PrevTracks.Select(t => t.Clone()).ToList());
        UpdateQueueRevision();
    }

    /// <summary>Sets repeat context and rebuilds prev/next tracks around the current track.</summary>
    public void HandleSetRepeatContext(bool repeat)
    {
        SetRepeatContext(repeat);

        if (repeat && _fillUpContext == ContextType.Autoplay)
        {
            _fillUpContext = ContextType.Default;
        }

        var ctx = GetContext(ContextType.Default);
        var currentTrack = FindIndexInContext(ctx, t => CurrentTrack(c => c.Uri) == t.Uri);
        ResetPlaybackToPosition(currentTrack);
    }
}
