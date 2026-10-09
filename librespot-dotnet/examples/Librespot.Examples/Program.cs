// One console app with a sub-command per upstream example (decision documented in docs/PORT-PLAN.md, package `examples`):
//
//   examples/get_creds.rs          -> get-creds        (Examples/GetCreds.cs)
//   examples/get_token.rs          -> get-token        (Examples/GetToken.cs)
//   examples/play.rs               -> play             (Examples/Play.cs)
//   examples/play_connect.rs       -> play-connect     (Examples/PlayConnect.cs)
//   examples/playlist_tracks.rs    -> playlist-tracks  (Examples/PlaylistTracks.cs)
//   discovery/examples/discovery.rs       -> discovery        (Examples/Discovery.cs)
//   discovery/examples/discovery_group.rs -> discovery-group  (Examples/DiscoveryGroup.cs)
//   oauth/examples/device_auth.rs  -> device-auth      (Examples/DeviceAuth.cs)
//   oauth/examples/oauth_async.rs  -> oauth-async      (Examples/OAuthAsync.cs)
//   oauth/examples/oauth_sync.rs   -> oauth-sync       (Examples/OAuthSync.cs)
//
// Each example receives the full argument list with the sub-command name in args[0], where upstream has the
// program name, so the upstream argument indices and usage lines carry over unchanged.
namespace Librespot.Examples;

/// <summary>Entry point: dispatches <c>args[0]</c> to the example of that name.</summary>
public static class Program
{
    /// <summary>Exit code of an example that hit an upstream <c>unwrap()</c>/<c>expect()</c> (Rust's panic exit code).</summary>
    public const int PanicExitCode = 101;

    /// <summary>Exit code after the first Ctrl-C cancelled the running example.</summary>
    public const int CancelledExitCode = 130;

    private static readonly string[] Commands =
    [
        "get-creds", "get-token", "play", "play-connect", "playlist-tracks",
        "discovery", "discovery-group", "device-auth", "oauth-async", "oauth-sync",
    ];

    /// <summary>Runs the example named by <paramref name="args"/>[0] with the whole argument list.</summary>
    /// <param name="args">The sub-command name followed by the example's own arguments.</param>
    /// <returns>The example's exit code; 2 for an unknown sub-command.</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || !Commands.Contains(args[0]))
        {
            Console.Error.WriteLine("Usage: Librespot.Examples <command> [args...]");
            Console.Error.WriteLine("Commands: " + string.Join(", ", Commands));
            return 2;
        }

        // Port addition: the first Ctrl-C cancels the example (so play-connect can shut its connect device down);
        // a second one terminates the process as upstream's default SIGINT handling does.
        // The handler is unregistered before `cts` is disposed, so a late Ctrl-C never touches a disposed source.
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                cts.Cancel();
            }
        };
        Console.CancelKeyPress += onCancel;

        var ct = cts.Token;
        try
        {
            return args[0] switch
            {
                "get-creds" => await GetCreds.RunAsync(args, ct).ConfigureAwait(false),
                "get-token" => await GetToken.RunAsync(args, ct).ConfigureAwait(false),
                "play" => await Play.RunAsync(args, ct).ConfigureAwait(false),
                "play-connect" => await PlayConnect.RunAsync(args, ct).ConfigureAwait(false),
                "playlist-tracks" => await PlaylistTracks.RunAsync(args, ct).ConfigureAwait(false),
                "discovery" => await DiscoveryExample.RunAsync(args, ct).ConfigureAwait(false),
                "discovery-group" => await DiscoveryGroupExample.RunAsync(args, ct).ConfigureAwait(false),
                "device-auth" => DeviceAuth.Run(args),
                "oauth-async" => await OAuthAsync.RunAsync(args, ct).ConfigureAwait(false),
                "oauth-sync" => OAuthSync.Run(args),
                _ => 2,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CancelledExitCode;
        }
        catch (Exception e)
        {
            // Upstream examples `unwrap()` some results; a failing unwrap panics with exit code 101.
            Console.Error.WriteLine($"thread 'main' panicked: {e.Message}");
            return PanicExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
