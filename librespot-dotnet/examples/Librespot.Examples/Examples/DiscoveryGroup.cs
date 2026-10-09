// Port of discovery/examples/discovery_group.rs (sub-command `discovery-group`).
using System.Security.Cryptography;
using System.Text;
using Librespot.Core;

namespace Librespot.Examples;

/// <summary>
/// Example <c>discovery-group</c>: advertises a group device named "Librespot Group" (type Speaker) via zeroconf and
/// prints every set of credentials a Spotify client sends to it. Usage: <c>Librespot.Examples discovery-group</c>.
/// </summary>
/// <remarks>Named <c>DiscoveryGroupExample</c> for symmetry with <see cref="DiscoveryExample"/>.</remarks>
public static class DiscoveryGroupExample
{
    /// <summary>Runs the example until the discovery stream ends.</summary>
    /// <param name="args">The command line, starting with the sub-command name (unused, as upstream).</param>
    /// <param name="cancellationToken">Stops waiting for credentials.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        _ = args;
        const string name = "Librespot Group";
        var deviceId = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(name)));

        var server = Librespot.Discovery.Discovery.Builder(deviceId, SessionConfig.Default().ClientId)
            .Name(name)
            .DeviceType(DeviceType.Speaker)
            .IsGroup(true)
            .Launch();

        await using (server.ConfigureAwait(false))
        {
            await foreach (var x in server.ReadCredentialsAsync(cancellationToken).ConfigureAwait(false))
            {
                Console.WriteLine($"Received {DiscoveryExampleFormat.Debug(x)}");
            }
        }

        return 0;
    }
}
