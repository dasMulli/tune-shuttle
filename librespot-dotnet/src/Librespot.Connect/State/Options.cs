// Port of connect/src/state/options.rs.
using ContextPlayerOptions = Spotify.Connectstate.ContextPlayerOptions;

namespace Librespot.Connect;

/// <summary>A shuffle seed and the track that was first when shuffling (upstream <c>ShuffleState</c>).</summary>
internal sealed record ShuffleState(ulong Seed, string InitialTrack);

internal sealed partial class ConnectState
{
    private void AddOptionsIfEmpty() => Player.Options ??= new ContextPlayerOptions();

    /// <summary>Upstream <c>set_repeat_context</c>.</summary>
    public void SetRepeatContext(bool repeat)
    {
        AddOptionsIfEmpty();
        Player.Options.RepeatingContext = repeat;
    }

    /// <summary>Upstream <c>set_repeat_track</c>.</summary>
    public void SetRepeatTrack(bool repeat)
    {
        AddOptionsIfEmpty();
        Player.Options.RepeatingTrack = repeat;
    }

    /// <summary>Upstream <c>set_shuffle</c>.</summary>
    public void SetShuffle(bool shuffle)
    {
        AddOptionsIfEmpty();
        Player.Options.ShufflingContext = shuffle;
    }

    /// <summary>Disables shuffle, repeat track and repeat context.</summary>
    public void ResetOptions()
    {
        SetShuffle(false);
        SetRepeatTrack(false);
        SetRepeatContext(false);
    }

    private void ValidateShuffleAllowed()
    {
        var reasons = Player.Restrictions?.DisallowTogglingShuffleReasons;
        if (reasons is { Count: > 0 })
        {
            throw StateException.CurrentlyDisallowed("shuffle", reasons[0]);
        }
    }

    /// <summary>Shuffles the default context with a previously used seed and initial track.</summary>
    public void ShuffleRestore(ShuffleState shuffleState)
    {
        ValidateShuffleAllowed();

        Shuffle(shuffleState.Seed, shuffleState.InitialTrack);
    }

    /// <summary>Shuffles the default context with a new random seed, keeping the current track first.</summary>
    public void ShuffleNew()
    {
        ValidateShuffleAllowed();

        var newSeed = (ulong)Random.Shared.NextInt64(100_000_000_000, 1_000_000_000_000);
        var currentTrack = CurrentTrack(t => t.Uri);

        Shuffle(newSeed, currentTrack);
    }

    private void Shuffle(ulong seed, string initialTrack)
    {
        ClearPrevTrack();
        ClearNextTracks();

        ResetContext(new ResetContextKind.DefaultIndex());

        var ctx = GetContext(ContextType.Default);
        ctx.Tracks.ShuffleWithSeed(seed, f => f.Uri == initialTrack);

        ctx.Metadata.SetInitialTrack(initialTrack);
        ctx.Metadata.SetShuffleSeed(seed);

        FillUpNextTracks();
    }

    /// <summary>Whether the context is shuffled.</summary>
    public bool ShufflingContext => Player.Options?.ShufflingContext ?? false;

    /// <summary>Whether the context is repeated.</summary>
    public bool RepeatContext => Player.Options?.RepeatingContext ?? false;

    /// <summary>Whether the current track is repeated.</summary>
    public bool RepeatTrack => Player.Options?.RepeatingTrack ?? false;
}
