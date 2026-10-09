// Port of examples/get_creds.rs (sub-command `get-creds`).
using Librespot.Core;
using Librespot.OAuth;
using Microsoft.Extensions.Logging;

namespace Librespot.Examples;

/// <summary>
/// Example <c>get-creds</c>: obtains an access token with the OAuth authorization-code flow (opening the browser),
/// connects a session with it and stores the reusable credentials in the cache directory.
/// Usage: <c>Librespot.Examples get-creds [CREDENTIALS_DIR]</c> (default <c>.cache</c>).
/// </summary>
public static class GetCreds
{
    /// <summary>Runs the example. <paramref name="args"/>[0] is the sub-command name (upstream: the program name).</summary>
    /// <param name="args">The command line, starting with the sub-command name.</param>
    /// <param name="cancellationToken">Cancels the OAuth flow and the connection attempt.</param>
    /// <returns>
    /// The process exit code: 0 on success, 1 when the cache cannot be created, the OAuth flow fails or the session
    /// cannot connect (upstream: <c>exit(1)</c> or <c>main</c> returning <c>Err</c>).
    /// </returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        using var loggerFactory = ExampleLogging.Init(fromEnvironment: true, ("librespot", LogLevel.Information));

        var credsPath = ".cache";
        if (args.Length == 2)
        {
            credsPath = args[1];
        }

        var sessionConfig = SessionConfig.Default();

        Cache cache;
        OAuthClient client;
        try
        {
            cache = new Cache(credsPath, null, null, null);
            client = new OAuthClientBuilder(sessionConfig.ClientId, "http://127.0.0.1:8898/login", ["streaming"])
                .OpenInBrowser()
                .Build();
        }
        catch (Exception e) when (e is LibrespotException or OAuthException)
        {
            // Upstream: `?` in `main`, which prints the error and exits with code 1.
            Console.Error.WriteLine($"Error: {ExampleFormat.Display(e)}");
            return 1;
        }

        Credentials credentials;
        using (client)
        {
            try
            {
                // Upstream calls the blocking `get_access_token`; the async variant is used inside async code.
                var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                credentials = Credentials.WithAccessToken(token.AccessToken);
            }
            catch (OAuthException e)
            {
                Console.Error.WriteLine($"Error performing OAuth: {e.Message}");
                return 1;
            }
        }

        var session = new Session(sessionConfig, cache);
        await using (session.ConfigureAwait(false))
        {
            try
            {
                await session.ConnectAsync(credentials, true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Error connecting: {ExampleFormat.Display(e)}");
                return 1;
            }
        }

        Console.WriteLine($"Credentials file stored in {credsPath}");

        return 0;
    }
}
