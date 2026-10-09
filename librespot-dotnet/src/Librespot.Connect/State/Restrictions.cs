// Port of connect/src/state/restrictions.rs.
using Google.Protobuf.Collections;
using Restrictions = Spotify.Connectstate.Restrictions;

namespace Librespot.Connect;

internal sealed partial class ConnectState
{
    /// <summary>Resets the context and player restrictions to empty ones.</summary>
    public void ClearRestrictions()
    {
        var player = Player;

        player.ContextRestrictions = new Restrictions();
        player.Restrictions = new Restrictions();
    }

    /// <summary>Updates the player restrictions from the playing state, the previous tracks and the current provider.</summary>
    public void UpdateRestrictions()
    {
        const string NoPrev = "no previous tracks";
        const string Autoplay = "autoplay";

        var prevTracksIsEmpty = PrevTracks.Count == 0;

        var isPaused = IsPause;
        var isPlaying = IsPlaying;

        var player = Player;
        if (player.Restrictions is { } current)
        {
            if (isPlaying)
            {
                current.DisallowPausingReasons.Clear();
                SetReasons(current.DisallowResumingReasons, "not_paused");
            }

            if (isPaused)
            {
                current.DisallowResumingReasons.Clear();
                SetReasons(current.DisallowPausingReasons, "not_playing");
            }
        }

        player.Restrictions ??= new Restrictions();

        var restrictions = player.Restrictions;
        if (prevTracksIsEmpty)
        {
            SetReasons(restrictions.DisallowPeekingPrevReasons, NoPrev);
            SetReasons(restrictions.DisallowSkippingPrevReasons, NoPrev);
        }
        else
        {
            restrictions.DisallowPeekingPrevReasons.Clear();
            restrictions.DisallowSkippingPrevReasons.Clear();
        }

        if (player.Track?.IsAutoplay() ?? false)
        {
            SetReasons(restrictions.DisallowTogglingShuffleReasons, Autoplay);
            SetReasons(restrictions.DisallowTogglingRepeatContextReasons, Autoplay);
            SetReasons(restrictions.DisallowTogglingRepeatTrackReasons, Autoplay);
        }
        else
        {
            restrictions.DisallowTogglingShuffleReasons.Clear();
            restrictions.DisallowTogglingRepeatContextReasons.Clear();
            restrictions.DisallowTogglingRepeatTrackReasons.Clear();
        }
    }

    /// <summary>Upstream <c>field = vec![reason.to_string()]</c>.</summary>
    private static void SetReasons(RepeatedField<string> field, string reason)
    {
        field.Clear();
        field.Add(reason);
    }
}
