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
// TODO(port: examples): the sub-commands themselves.
namespace Librespot.Examples;

public static class Program
{
    private static readonly string[] Commands =
    [
        "get-creds", "get-token", "play", "play-connect", "playlist-tracks",
        "discovery", "discovery-group", "device-auth", "oauth-async", "oauth-sync",
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || !Commands.Contains(args[0]))
        {
            Console.Error.WriteLine("Usage: Librespot.Examples <command> [args...]");
            Console.Error.WriteLine("Commands: " + string.Join(", ", Commands));
            return 2;
        }

        await Task.CompletedTask;
        Console.Error.WriteLine($"example '{args[0]}' is not ported yet (TODO(port: examples))");
        return 1;
    }
}
