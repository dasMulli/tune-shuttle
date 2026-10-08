// Port of oauth/examples/oauth_async.rs (sub-command `oauth-async`).
using Librespot.OAuth;

namespace Librespot.Examples;

/// <summary>
/// Example <c>oauth-async</c>: obtains a token with the authorization-code flow using the async API, then refreshes
/// it. Usage: <c>Librespot.Examples oauth-async [CLIENT_ID REDIRECT_URI SCOPES]</c>.
/// </summary>
public static class OAuthAsync
{
    private const string SpotifyClientId = "65b708073fc0480ea92a077233ca87bd";
    private const string SpotifyRedirectUri = "http://127.0.0.1:8898/login";

    private const string Response =
        "\n<!doctype html>\n<html>\n    <body>\n        <h1>Return to your app!</h1>\n    </body>\n</html>\n";

    /// <summary>Runs the example. <paramref name="args"/>[0] is the sub-command name (upstream: the program name).</summary>
    /// <param name="args">The command line, starting with the sub-command name.</param>
    /// <param name="cancellationToken">Cancels the flow (e.g. on Ctrl-C).</param>
    /// <returns>The process exit code (always 0, as upstream's <c>main</c> returns <c>()</c>).</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // env_logger with the filter "librespot=trace".
        using var loggerFactory = OAuthExampleLogging.CreateTraceFactory();

        string clientId;
        string redirectUri;
        string[] scopes;
        if (args.Length == 4)
        {
            // You can use your own client ID, along with it's associated redirect URI.
            (clientId, redirectUri, scopes) = (args[1], args[2], args[3].Split(','));
        }
        else if (args.Length == 1)
        {
            (clientId, redirectUri, scopes) = (SpotifyClientId, SpotifyRedirectUri, ["streaming"]);
        }
        else
        {
            Console.Error.WriteLine($"Usage: {args.FirstOrDefault() ?? "oauth-async"} [CLIENT_ID REDIRECT_URI SCOPES]");
            return 0;
        }

        OAuthClient client;
        try
        {
            client = new OAuthClientBuilder(clientId, redirectUri, scopes)
                .OpenInBrowser()
                .WithCustomMessage(Response)
                .Build();
        }
        catch (OAuthException err)
        {
            Console.Error.WriteLine($"Unable to build an OAuth client: {err.Message}");
            return 0;
        }

        using (client)
        {
            string refreshToken;
            try
            {
                var token = await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"OAuth Token: {token}");
                refreshToken = token.RefreshToken;
            }
            catch (OAuthException err)
            {
                Console.WriteLine($"Unable to get OAuth Token: {err.Message}");
                return 0;
            }

            try
            {
                var token = await client.RefreshTokenAsync(refreshToken, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"New refreshed OAuth Token: {token}");
            }
            catch (OAuthException err)
            {
                Console.WriteLine($"Unable to get refreshed OAuth Token: {err.Message}");
            }
        }

        return 0;
    }
}
