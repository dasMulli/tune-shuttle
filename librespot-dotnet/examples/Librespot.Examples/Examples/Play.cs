// Port of examples/play.rs (sub-command `play`).
using Librespot.Core;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Mixer;

namespace Librespot.Examples;

/// <summary>
/// Example <c>play</c>: connects a session with an access token and plays one track on the default audio backend
/// until it ends. Usage: <c>Librespot.Examples play ACCESS_TOKEN TRACK</c> (TRACK is a base62 track id).
/// </summary>
public static class Play
{
    /// <summary>Runs the example. <paramref name="args"/>[0] is the sub-command name (upstream: the program name).</summary>
    /// <param name="args">The command line, starting with the sub-command name.</param>
    /// <param name="cancellationToken">Cancels the connection attempt and stops waiting for the end of the track.</param>
    /// <returns>The process exit code: 0, or 1 when the session cannot connect.</returns>
    /// <exception cref="LibrespotException">TRACK is not a valid base62 id (upstream: <c>unwrap()</c> panics).</exception>
    /// <exception cref="InvalidOperationException">No audio backend is available (upstream: <c>unwrap()</c> panics).</exception>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var sessionConfig = SessionConfig.Default();
        var playerConfig = new PlayerConfig();
        var audioFormat = AudioFormatExtensions.Default;

        if (args.Length != 3)
        {
            Console.Error.WriteLine($"Usage: {args.FirstOrDefault() ?? "play"} ACCESS_TOKEN TRACK");
            return 0;
        }

        var credentials = Credentials.WithAccessToken(args[1]);

        SpotifyUri track = new SpotifyUri.Track(SpotifyId.FromBase62(args[2]));

        var backend = AudioBackends.Find(null)
            ?? throw new InvalidOperationException("called `Option::unwrap()` on a `None` value");

        Console.WriteLine("Connecting...");
        var session = new Session(sessionConfig, null);
        await using (session.ConfigureAwait(false))
        {
            try
            {
                await session.ConnectAsync(credentials, false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.WriteLine($"Error connecting: {ExampleFormat.Display(e)}");
                return 1;
            }

            using var player = Player.Create(playerConfig, session, new NoOpVolume(), () => backend(null, audioFormat));

            player.Load(track, true, 0);

            Console.WriteLine("Playing...");

            await player.AwaitEndOfTrackAsync(cancellationToken).ConfigureAwait(false);

            Console.WriteLine("Done");
        }

        return 0;
    }
}
