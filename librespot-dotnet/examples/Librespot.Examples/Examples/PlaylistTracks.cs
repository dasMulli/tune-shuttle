// Port of examples/playlist_tracks.rs (sub-command `playlist-tracks`).
using Librespot.Core;
using Librespot.Metadata;

namespace Librespot.Examples;

/// <summary>
/// Example <c>playlist-tracks</c>: connects a session with an access token, fetches a playlist and prints it and the
/// name of every track in it. Usage: <c>Librespot.Examples playlist-tracks ACCESS_TOKEN PLAYLIST</c>.
/// </summary>
public static class PlaylistTracks
{
    /// <summary>Runs the example. <paramref name="args"/>[0] is the sub-command name (upstream: the program name).</summary>
    /// <param name="args">The command line, starting with the sub-command name.</param>
    /// <param name="cancellationToken">Cancels the connection attempt and the metadata requests.</param>
    /// <returns>The process exit code: 0, or 1 when PLAYLIST is not a URI or the session cannot connect.</returns>
    /// <exception cref="LibrespotException">A metadata request failed (upstream: <c>unwrap()</c> panics).</exception>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // env_logger::init(): filters from RUST_LOG only.
        using var loggerFactory = ExampleLogging.Init(fromEnvironment: true);
        var sessionConfig = SessionConfig.Default();

        if (args.Length != 3)
        {
            Console.Error.WriteLine($"Usage: {args.FirstOrDefault() ?? "playlist-tracks"} ACCESS_TOKEN PLAYLIST");
            return 0;
        }

        var credentials = Credentials.WithAccessToken(args[1]);

        SpotifyUri plistUri;
        try
        {
            plistUri = SpotifyUri.FromUri(args[2]);
        }
        catch (LibrespotException)
        {
            Console.Error.WriteLine("PLAYLIST should be a playlist URI such as: \"spotify:playlist:37i9dQZF1DXec50AjHrNTq\"");
            return 1;
        }

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

            var plist = await Playlist.GetAsync(session, plistUri, cancellationToken).ConfigureAwait(false);
            Console.WriteLine(plist);
            foreach (var trackId in plist.Tracks())
            {
                var plistTrack = await Track.GetAsync(session, trackId, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"track: {plistTrack.Name} ");
            }
        }

        return 0;
    }
}
