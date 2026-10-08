// Port of oauth/examples/device_auth.rs (sub-command `device-auth`).
using Librespot.OAuth;

namespace Librespot.Examples;

/// <summary>
/// Example <c>device-auth</c>: obtains a token with the device-authorization flow using the blocking API, then
/// refreshes it. Usage: <c>Librespot.Examples device-auth [CLIENT_ID SCOPES]</c>.
/// </summary>
public static class DeviceAuth
{
    private const string SpotifyClientId = "65b708073fc0480ea92a077233ca87bd";

    /// <summary>Runs the example. <paramref name="args"/>[0] is the sub-command name (upstream: the program name).</summary>
    /// <param name="args">The command line, starting with the sub-command name.</param>
    /// <returns>The process exit code (always 0, as upstream's <c>main</c> returns <c>()</c>).</returns>
    public static int Run(string[] args)
    {
        // env_logger with the filter "librespot=trace".
        using var loggerFactory = OAuthExampleLogging.CreateTraceFactory();

        string clientId;
        string[] scopes;
        if (args.Length == 3)
        {
            // Spotify only enables the device flow for some client IDs, and refuses
            // the rest with `unauthorized_client`.
            (clientId, scopes) = (args[1], args[2].Split(','));
        }
        else if (args.Length == 1)
        {
            (clientId, scopes) = (SpotifyClientId, ["streaming"]);
        }
        else
        {
            Console.Error.WriteLine($"Usage: {args.FirstOrDefault() ?? "device-auth"} [CLIENT_ID SCOPES]");
            return 0;
        }

        // Upstream's `build()` returns a Result ("Unable to build a device auth client: {err}"); the C# builder
        // cannot fail because the endpoint URLs are constants.
        using var client = new DeviceAuthClientBuilder(clientId, scopes).Build();

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

        return 0;
    }
}
