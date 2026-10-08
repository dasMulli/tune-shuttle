// Port of oauth/examples/oauth_sync.rs (sub-command `oauth-sync`).
using Librespot.OAuth;

namespace Librespot.Examples;

/// <summary>
/// Example <c>oauth-sync</c>: obtains a token with the authorization-code flow using the blocking API, then refreshes
/// it. Usage: <c>Librespot.Examples oauth-sync [CLIENT_ID REDIRECT_URI SCOPES]</c>.
/// </summary>
public static class OAuthSync
{
    private const string SpotifyClientId = "65b708073fc0480ea92a077233ca87bd";
    private const string SpotifyRedirectUri = "http://127.0.0.1:8898/login";

    private const string Response =
        "\n<!doctype html>\n<html>\n    <body>\n        <h1>Return to your app!</h1>\n    </body>\n</html>\n";

    /// <summary>Runs the example. <paramref name="args"/>[0] is the sub-command name (upstream: the program name).</summary>
    /// <param name="args">The command line, starting with the sub-command name.</param>
    /// <returns>The process exit code (always 0, as upstream's <c>main</c> returns <c>()</c>).</returns>
    public static int Run(string[] args)
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
            Console.Error.WriteLine($"Usage: {args.FirstOrDefault() ?? "oauth-sync"} [CLIENT_ID REDIRECT_URI SCOPES]");
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
                var token = client.GetAccessToken();
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
                var token = client.RefreshToken(refreshToken);
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
