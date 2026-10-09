// Port of examples/get_token.rs (sub-command `get-token`).
using Librespot.Core;
using Microsoft.Extensions.Logging;

namespace Librespot.Examples;

/// <summary>
/// Example <c>get-token</c>: connects a session with an access token and requests a new token with the Web API
/// playback scopes from the session's token provider.
/// Usage: <c>Librespot.Examples get-token ACCESS_TOKEN [CLIENT_ID]</c>.
/// </summary>
public static class GetToken
{
    /// <summary>Upstream <c>SCOPES</c>.</summary>
    public const string Scopes =
        "streaming,user-read-playback-state,user-modify-playback-state,user-read-currently-playing";

    /// <summary>Runs the example. <paramref name="args"/>[0] is the sub-command name (upstream: the program name).</summary>
    /// <param name="args">The command line, starting with the sub-command name.</param>
    /// <param name="cancellationToken">Cancels the connection attempt and the token request.</param>
    /// <returns>The process exit code (0, as upstream's <c>main</c> returns <c>()</c>).</returns>
    /// <exception cref="LibrespotException">The token request failed (upstream: <c>unwrap()</c> panics).</exception>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // env_logger::Builder::new().parse_filters("librespot=trace"): no RUST_LOG.
        using var loggerFactory = ExampleLogging.Init(fromEnvironment: false, ("librespot", LogLevel.Trace));

        var sessionConfig = SessionConfig.Default();

        if (args.Length == 3)
        {
            // Only special client IDs have sufficient privileges e.g. Spotify's.
            sessionConfig = sessionConfig with { ClientId = args[2] };
        }
        else if (args.Length != 2)
        {
            Console.Error.WriteLine($"Usage: {args.FirstOrDefault() ?? "get-token"} ACCESS_TOKEN [CLIENT_ID]");
            return 0;
        }

        var accessToken = args[1];

        // Now create a new session with that token.
        var session = new Session(sessionConfig, null);
        await using (session.ConfigureAwait(false))
        {
            var credentials = Credentials.WithAccessToken(accessToken);
            Console.WriteLine("Connecting with token..");
            try
            {
                await session.ConnectAsync(credentials, false, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Session username: {ExampleFormat.DebugString(session.Username)}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.WriteLine($"Error connecting: {ExampleFormat.Display(e)}");
                return 0;
            }

            var token = await session.TokenProvider.GetTokenAsync(Scopes, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Got me a token: {token}");
        }

        return 0;
    }
}
