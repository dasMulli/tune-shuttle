using System.Globalization;
using System.Net;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests;

/// <summary>
/// The receiver the SpeakerALive tests target (docs/DEVELOPMENT.md, "Live tests"). Nothing is hard-coded: the tests return early
/// unless the variable they need is set. <c>AIRPLAY_LIVE_RECEIVER</c> is the receiver's mDNS name or device id (what the CLI's
/// <c>--to</c> takes; the discovery-based tests use it); <c>AIRPLAY_LIVE_RECEIVER_HOST</c> its IP address (the tests that connect
/// without discovery) and <c>AIRPLAY_LIVE_RECEIVER_PORT</c> its RTSP port (default 7000).
/// </summary>
internal static class LiveTarget
{
    public const string ReceiverVariable = "AIRPLAY_LIVE_RECEIVER";
    public const string HostVariable = "AIRPLAY_LIVE_RECEIVER_HOST";
    public const string PortVariable = "AIRPLAY_LIVE_RECEIVER_PORT";

    /// <summary>The RTSP port: <c>AIRPLAY_LIVE_RECEIVER_PORT</c>, else 7000.</summary>
    public static int Port => Value(PortVariable) is { } port ? int.Parse(port, CultureInfo.InvariantCulture) : 7000;

    /// <summary>True with the receiver's name or device id; otherwise writes why the test returns early.</summary>
    public static bool TryReceiver(ITestOutputHelper output, out string receiver)
    {
        receiver = Value(ReceiverVariable) ?? string.Empty;
        if (receiver.Length == 0)
        {
            output.WriteLine($"{ReceiverVariable} not set (the receiver's mDNS name or device id); skipped");
            return false;
        }

        return true;
    }

    /// <summary>True with the receiver's address; otherwise writes why the test returns early.</summary>
    public static bool TryHost(ITestOutputHelper output, out IPAddress host)
    {
        if (Value(HostVariable) is { } value)
        {
            host = IPAddress.Parse(value);
            return true;
        }

        host = IPAddress.None;
        output.WriteLine($"{HostVariable} not set (the receiver's IP address); skipped");
        return false;
    }

    private static string? Value(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value.Trim() : null;
}
