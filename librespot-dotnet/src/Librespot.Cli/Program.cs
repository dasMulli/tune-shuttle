// Port of src/main.rs (entry point only). TODO(port: cli): getopts option table (hand-written parser, keep the
// exact long/short option names and help text), Setup struct, get_setup(), setup_logging, list_backends,
// the main loop (discovery -> session -> spirc, reconnects, Ctrl-C handling), and src/player_event_handler.rs
// (EventHandler.cs + run_program_on_sink_events).
using Librespot.Core;

namespace Librespot.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-V"))
        {
            Console.WriteLine(GetVersionString());
            return 0;
        }

        Console.Error.WriteLine("librespot-dotnet: the CLI is not ported yet (TODO(port: cli)).");
        Console.Error.WriteLine(GetVersionString());
        return 1;
    }

    /// <summary>Upstream <c>get_version_string()</c>.</summary>
    public static string GetVersionString() =>
        $"librespot {LibrespotVersion.SemVer} {LibrespotVersion.ShaShort} (Built on {LibrespotVersion.BuildDate}, Build ID: {LibrespotVersion.BuildId}, Profile: {BuildProfile})";

    private static string BuildProfile =>
#if DEBUG
        "debug";
#else
        "release";
#endif
}
