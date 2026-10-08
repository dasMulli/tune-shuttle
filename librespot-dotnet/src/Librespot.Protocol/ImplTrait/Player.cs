// Port of protocol/src/impl_trait/player.rs.
//
// Conversions between the proto2 `spotify.player.proto` types (context_player_options.proto, restrictions.proto,
// play_origin.proto, suppressions.proto) and their proto3 twins in `spotify.connectstate` (player.proto).
// Upstream implements `From` in both directions; here they are extension methods named ToConnectState() /
// ToPlayerProto(). `special_fields` (unknown fields) are not carried over: the C# runtime keeps unknown fields
// per message instance and they are not meaningful across different message types.
using Google.Protobuf.Collections;
using ConnectState = Spotify.Connectstate;
using PlayerProto = Spotify.Player.Proto;

namespace Librespot.Protocol;

public static class PlayerProtoConversions
{
    private static void CopyMap<TIn, TOut>(MapField<string, TIn> source, MapField<string, TOut> target, Func<TIn, TOut> convert)
    {
        foreach (var (k, v) in source)
        {
            target[k] = convert(v);
        }
    }

    /// <summary>Upstream <c>From&lt;ContextPlayerOptions&gt; for player::ContextPlayerOptions</c>.</summary>
    public static ConnectState.ContextPlayerOptions ToConnectState(this PlayerProto.ContextPlayerOptions value)
    {
        var result = new ConnectState.ContextPlayerOptions
        {
            ShufflingContext = value.HasShufflingContext && value.ShufflingContext,
            RepeatingContext = value.HasRepeatingContext && value.RepeatingContext,
            RepeatingTrack = value.HasRepeatingTrack && value.RepeatingTrack,
        };
        result.Modes.Add(value.Modes);
        if (value.HasPlaybackSpeed)
        {
            result.PlaybackSpeed = value.PlaybackSpeed;
        }

        return result;
    }

    /// <summary>Upstream <c>From&lt;player::Restrictions&gt; for Restrictions</c>.</summary>
    public static PlayerProto.Restrictions ToPlayerProto(this ConnectState.Restrictions value)
    {
        var r = new PlayerProto.Restrictions();
        r.DisallowPausingReasons.Add(value.DisallowPausingReasons);
        r.DisallowResumingReasons.Add(value.DisallowResumingReasons);
        r.DisallowSeekingReasons.Add(value.DisallowSeekingReasons);
        r.DisallowPeekingPrevReasons.Add(value.DisallowPeekingPrevReasons);
        r.DisallowPeekingNextReasons.Add(value.DisallowPeekingNextReasons);
        r.DisallowSkippingPrevReasons.Add(value.DisallowSkippingPrevReasons);
        r.DisallowSkippingNextReasons.Add(value.DisallowSkippingNextReasons);
        r.DisallowTogglingRepeatContextReasons.Add(value.DisallowTogglingRepeatContextReasons);
        r.DisallowTogglingRepeatTrackReasons.Add(value.DisallowTogglingRepeatTrackReasons);
        r.DisallowTogglingShuffleReasons.Add(value.DisallowTogglingShuffleReasons);
        r.DisallowSetQueueReasons.Add(value.DisallowSetQueueReasons);
        r.DisallowInterruptingPlaybackReasons.Add(value.DisallowInterruptingPlaybackReasons);
        r.DisallowTransferringPlaybackReasons.Add(value.DisallowTransferringPlaybackReasons);
        r.DisallowRemoteControlReasons.Add(value.DisallowRemoteControlReasons);
        r.DisallowInsertingIntoNextTracksReasons.Add(value.DisallowInsertingIntoNextTracksReasons);
        r.DisallowInsertingIntoContextTracksReasons.Add(value.DisallowInsertingIntoContextTracksReasons);
        r.DisallowReorderingInNextTracksReasons.Add(value.DisallowReorderingInNextTracksReasons);
        r.DisallowReorderingInContextTracksReasons.Add(value.DisallowReorderingInContextTracksReasons);
        r.DisallowRemovingFromNextTracksReasons.Add(value.DisallowRemovingFromNextTracksReasons);
        r.DisallowRemovingFromContextTracksReasons.Add(value.DisallowRemovingFromContextTracksReasons);
        r.DisallowUpdatingContextReasons.Add(value.DisallowUpdatingContextReasons);
        r.DisallowAddToQueueReasons.Add(value.DisallowAddToQueueReasons);
        r.DisallowSettingPlaybackSpeed.Add(value.DisallowSettingPlaybackSpeedReasons);
        CopyMap(value.DisallowSettingModes, r.DisallowSettingModes, ToPlayerProto);
        CopyMap(value.DisallowSignals, r.DisallowSignals, ToPlayerProto);
        return r;
    }

    /// <summary>Upstream <c>From&lt;Restrictions&gt; for player::Restrictions</c>.</summary>
    public static ConnectState.Restrictions ToConnectState(this PlayerProto.Restrictions value)
    {
        var r = new ConnectState.Restrictions();
        r.DisallowPausingReasons.Add(value.DisallowPausingReasons);
        r.DisallowResumingReasons.Add(value.DisallowResumingReasons);
        r.DisallowSeekingReasons.Add(value.DisallowSeekingReasons);
        r.DisallowPeekingPrevReasons.Add(value.DisallowPeekingPrevReasons);
        r.DisallowPeekingNextReasons.Add(value.DisallowPeekingNextReasons);
        r.DisallowSkippingPrevReasons.Add(value.DisallowSkippingPrevReasons);
        r.DisallowSkippingNextReasons.Add(value.DisallowSkippingNextReasons);
        r.DisallowTogglingRepeatContextReasons.Add(value.DisallowTogglingRepeatContextReasons);
        r.DisallowTogglingRepeatTrackReasons.Add(value.DisallowTogglingRepeatTrackReasons);
        r.DisallowTogglingShuffleReasons.Add(value.DisallowTogglingShuffleReasons);
        r.DisallowSetQueueReasons.Add(value.DisallowSetQueueReasons);
        r.DisallowInterruptingPlaybackReasons.Add(value.DisallowInterruptingPlaybackReasons);
        r.DisallowTransferringPlaybackReasons.Add(value.DisallowTransferringPlaybackReasons);
        r.DisallowRemoteControlReasons.Add(value.DisallowRemoteControlReasons);
        r.DisallowInsertingIntoNextTracksReasons.Add(value.DisallowInsertingIntoNextTracksReasons);
        r.DisallowInsertingIntoContextTracksReasons.Add(value.DisallowInsertingIntoContextTracksReasons);
        r.DisallowReorderingInNextTracksReasons.Add(value.DisallowReorderingInNextTracksReasons);
        r.DisallowReorderingInContextTracksReasons.Add(value.DisallowReorderingInContextTracksReasons);
        r.DisallowRemovingFromNextTracksReasons.Add(value.DisallowRemovingFromNextTracksReasons);
        r.DisallowRemovingFromContextTracksReasons.Add(value.DisallowRemovingFromContextTracksReasons);
        r.DisallowUpdatingContextReasons.Add(value.DisallowUpdatingContextReasons);
        r.DisallowAddToQueueReasons.Add(value.DisallowAddToQueueReasons);
        r.DisallowSettingPlaybackSpeedReasons.Add(value.DisallowSettingPlaybackSpeed);
        CopyMap(value.DisallowSettingModes, r.DisallowSettingModes, ToConnectState);
        CopyMap(value.DisallowSignals, r.DisallowSignals, ToConnectState);
        // disallow_playing_reasons / disallow_stopping_reasons: upstream leaves these empty.
        return r;
    }

    /// <summary>Upstream <c>From&lt;player::ModeRestrictions&gt; for ModeRestrictions</c>.</summary>
    public static PlayerProto.ModeRestrictions ToPlayerProto(this ConnectState.ModeRestrictions value)
    {
        var r = new PlayerProto.ModeRestrictions();
        CopyMap(value.Values, r.Values, ToPlayerProto);
        return r;
    }

    /// <summary>Upstream <c>From&lt;ModeRestrictions&gt; for player::ModeRestrictions</c>.</summary>
    public static ConnectState.ModeRestrictions ToConnectState(this PlayerProto.ModeRestrictions value)
    {
        var r = new ConnectState.ModeRestrictions();
        CopyMap(value.Values, r.Values, ToConnectState);
        return r;
    }

    /// <summary>Upstream <c>From&lt;player::RestrictionReasons&gt; for RestrictionReasons</c>.</summary>
    public static PlayerProto.RestrictionReasons ToPlayerProto(this ConnectState.RestrictionReasons value)
    {
        var r = new PlayerProto.RestrictionReasons();
        r.Reasons.Add(value.Reasons);
        return r;
    }

    /// <summary>Upstream <c>From&lt;RestrictionReasons&gt; for player::RestrictionReasons</c>.</summary>
    public static ConnectState.RestrictionReasons ToConnectState(this PlayerProto.RestrictionReasons value)
    {
        var r = new ConnectState.RestrictionReasons();
        r.Reasons.Add(value.Reasons);
        return r;
    }

    /// <summary>Upstream <c>From&lt;PlayOrigin&gt; for player::PlayOrigin</c>.</summary>
    public static ConnectState.PlayOrigin ToConnectState(this PlayerProto.PlayOrigin value)
    {
        var r = new ConnectState.PlayOrigin
        {
            FeatureIdentifier = value.HasFeatureIdentifier ? value.FeatureIdentifier : "",
            FeatureVersion = value.HasFeatureVersion ? value.FeatureVersion : "",
            ViewUri = value.HasViewUri ? value.ViewUri : "",
            ExternalReferrer = value.HasExternalReferrer ? value.ExternalReferrer : "",
            ReferrerIdentifier = value.HasReferrerIdentifier ? value.ReferrerIdentifier : "",
            DeviceIdentifier = value.HasDeviceIdentifier ? value.DeviceIdentifier : "",
            RestrictionIdentifier = value.HasRestrictionIdentifier ? value.RestrictionIdentifier : "",
        };
        r.FeatureClasses.Add(value.FeatureClasses);
        return r;
    }

    /// <summary>Upstream <c>From&lt;Suppressions&gt; for player::Suppressions</c>.</summary>
    public static ConnectState.Suppressions ToConnectState(this PlayerProto.Suppressions value)
    {
        var r = new ConnectState.Suppressions();
        r.Providers.Add(value.Providers);
        return r;
    }
}
