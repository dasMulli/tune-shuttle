// Port of examples/play_connect.rs (sub-command `play-connect`).
using Librespot.Connect;
using Librespot.Core;
using Librespot.OAuth;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Mixer;
using Microsoft.Extensions.Logging;

namespace Librespot.Examples;

/// <summary>
/// Example <c>play-connect</c>: starts a Spotify Connect device with cached credentials (or credentials from the
/// OAuth flow), activates it and plays the user's saved tracks ("collection") until the process is stopped.
/// Usage: <c>Librespot.Examples play-connect</c>.
/// </summary>
public static class PlayConnect
{
    /// <summary>Upstream <c>CACHE</c>: the credentials and volume directory.</summary>
    public const string CacheDir = ".cache";

    /// <summary>Upstream <c>CACHE_FILES</c>: the audio file cache directory.</summary>
    public const string CacheFiles = ".cache/files";

    /// <summary>Runs the example until the connect device stops (or <paramref name="cancellationToken"/> fires).</summary>
    /// <param name="args">The command line, starting with the sub-command name (unused, as upstream).</param>
    /// <param name="cancellationToken">
    /// Cancels the setup and stops the connect device's event loop (port addition: upstream runs until the process is
    /// killed); the loop's cleanup (disconnect, connect state deletion) still runs.
    /// </param>
    /// <returns>The process exit code: 0, or 1 when any step fails (upstream: <c>main</c> returning <c>Err</c>).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired (port addition).</exception>
    /// <exception cref="InvalidOperationException">No audio backend or mixer is available (upstream: <c>unwrap()</c> panics).</exception>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        _ = args;
        using var loggerFactory = ExampleLogging.Init(fromEnvironment: true, ("librespot", LogLevel.Debug));

        var sessionConfig = SessionConfig.Default();
        var playerConfig = new PlayerConfig();
        var audioFormat = AudioFormatExtensions.Default;
        var connectConfig = new ConnectConfig();
        var mixerConfig = new MixerConfig();
        var requestOptions = new LoadRequestOptions();

        var sinkBuilder = AudioBackends.Find(null)
            ?? throw new InvalidOperationException("called `Option::unwrap()` on a `None` value");
        var mixerBuilder = Mixers.Find(null)
            ?? throw new InvalidOperationException("called `Option::unwrap()` on a `None` value");

        try
        {
            var cache = new Cache(CacheDir, CacheDir, CacheFiles, null);

            // Upstream: `cache.credentials().ok_or(Error::unavailable("credentials not cached")).or_else(oauth)`;
            // the "not cached" error is discarded in favour of the OAuth flow.
            Credentials credentials;
            if (cache.Credentials() is { } cached)
            {
                credentials = cached;
            }
            else
            {
                using var client = new OAuthClientBuilder(sessionConfig.ClientId, "http://127.0.0.1:8898/login", ["streaming"])
                    .OpenInBrowser()
                    .Build();

                // Upstream calls the blocking `get_access_token`; the async variant is used inside async code.
                var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                credentials = Credentials.WithAccessToken(token.AccessToken);
            }

            var session = new Session(sessionConfig, cache);
            await using (session.ConfigureAwait(false))
            {
                var mixer = mixerBuilder(mixerConfig);

                using var player = Player.Create(
                    playerConfig,
                    session,
                    mixer.GetSoftVolume(),
                    () => sinkBuilder(null, audioFormat));

                var (spirc, spircTask) = await Spirc.CreateAsync(
                    connectConfig,
                    session,
                    credentials,
                    player,
                    mixer,
                    cancellationToken,
                    loopCancellation: cancellationToken).ConfigureAwait(false);

                // these calls can be seen as "queued"
                spirc.Activate();
                spirc.Load(LoadRequest.FromContextUri($"spotify:user:{session.Username}:collection", requestOptions));
                spirc.Play();

                // starting the connect device and processing the previously "queued" calls
                await spircTask.ConfigureAwait(false);

                // Port addition: the spirc loop ends normally on Ctrl-C; surface the cancellation so Program maps the
                // run to its cancelled exit code like the other sub-commands.
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Upstream: `?` in `main`, which prints the error and exits with code 1.
            Console.Error.WriteLine($"Error: {ExampleFormat.Display(e)}");
            return 1;
        }

        return 0;
    }
}
