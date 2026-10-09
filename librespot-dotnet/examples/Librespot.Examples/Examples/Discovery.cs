// Port of discovery/examples/discovery.rs (sub-command `discovery`).
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.Reflection;
using Librespot.Core;
using Librespot.Protocol.Authentication;

namespace Librespot.Examples;

/// <summary>
/// Example <c>discovery</c>: advertises a device named "Librespot" (type Computer) via zeroconf and prints every
/// set of credentials a Spotify client sends to it. Usage: <c>Librespot.Examples discovery</c>.
/// </summary>
/// <remarks>Named <c>DiscoveryExample</c> so it does not shadow the <c>Librespot.Discovery</c> namespace.</remarks>
public static class DiscoveryExample
{
    /// <summary>Runs the example until the discovery stream ends.</summary>
    /// <param name="args">The command line, starting with the sub-command name (unused, as upstream).</param>
    /// <param name="cancellationToken">Stops waiting for credentials.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        _ = args;
        const string name = "Librespot";
        var deviceId = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(name)));

        var server = Librespot.Discovery.Discovery.Builder(deviceId, SessionConfig.Default().ClientId)
            .Name(name)
            .DeviceType(DeviceType.Computer)
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

/// <summary>Formats values like Rust's <c>{:?}</c> for the discovery examples.</summary>
internal static class DiscoveryExampleFormat
{
    /// <summary>
    /// Rust <c>Debug</c> of <c>Credentials</c>: the proto variant name for <c>auth_type</c> (rust-protobuf keeps the
    /// <c>.proto</c> names) and an escaped username. The string escaping covers quotes, backslashes and control
    /// characters; Rust additionally escapes non-printable Unicode, so exotic usernames may print differently.
    /// </summary>
    public static string Debug(Credentials c) =>
        $"Credentials {{ username: {(c.Username is null ? "None" : $"Some({DebugString(c.Username)})")}, auth_type: {ProtoName(c.AuthType)}, auth_data: [{string.Join(", ", c.AuthData)}] }}";

    // Google.Protobuf records the .proto name in [OriginalName]; unknown values print as the number.
    private static string ProtoName(AuthenticationType value) =>
        typeof(AuthenticationType).GetField(value.ToString())?.GetCustomAttribute<OriginalNameAttribute>()?.Name
        ?? ((int)value).ToString(CultureInfo.InvariantCulture);

    // Rust `<str as Debug>::fmt` (char::escape_debug) for the ASCII range.
    private static string DebugString(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            _ = ch switch
            {
                '"' => sb.Append("\\\""),
                '\\' => sb.Append("\\\\"),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                '\t' => sb.Append("\\t"),
                '\0' => sb.Append("\\0"),
                _ when char.IsControl(ch) => sb.Append(CultureInfo.InvariantCulture, $"\\u{{{(int)ch:x}}}"),
                _ => sb.Append(ch),
            };
        }

        return sb.Append('"').ToString();
    }
}
