using Airplay2.Sender.Discovery;

namespace Airplay2.Receiver.Discovery;

/// <summary>
/// Builds the TXT records of the two advertised services (SPEC-receiver.md 3.1): shairport-sync's sets with our
/// identity, name, feature word (R15, or the ALAC-only variant of R3) and status flags (R7).
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the records are the wave 1 <c>advertise</c> package (SPEC-receiver.md 13).
/// </remarks>
public static class ReceiverTxtRecords
{
    /// <summary>The <c>_airplay._tcp</c> service type (SPEC-receiver.md 3.1).</summary>
    public const string AirplayServiceType = "_airplay._tcp";

    /// <summary>The <c>_raop._tcp</c> service type (SPEC-receiver.md 3.1).</summary>
    public const string RaopServiceType = "_raop._tcp";

    /// <summary>The <c>_raop._tcp</c> instance name: <c>&lt;deviceid without colons&gt;@&lt;name&gt;</c> (SPEC-receiver.md 3.1).</summary>
    public static string RaopInstanceName(ReceiverIdentity identity, string name)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return identity.DeviceIdCompact + "@" + name;
    }

    /// <summary>
    /// The <c>_airplay._tcp</c> TXT entries as <c>key=value</c> strings (SPEC-receiver.md 3.1): <c>acl</c>, <c>deviceid</c>,
    /// <c>features</c>, <c>flags</c>, <c>gid</c>, <c>gcgl</c>, <c>model</c>, <c>manufacturer</c>, <c>protovers</c>, <c>pi</c>,
    /// <c>psi</c>, <c>pk</c>, <c>srcvers</c>, <c>fex</c>.
    /// </summary>
    public static IReadOnlyList<string> Airplay(AirplayReceiverOptions options, ulong features, AirplayStatusFlags flags) => throw new NotImplementedException("SPEC-receiver.md 3.1, wave 1");

    /// <summary>
    /// The <c>_raop._tcp</c> TXT entries (SPEC-receiver.md 3.1): <c>cn</c> (<see cref="ReceiverFeatures.RaopCompressionFor"/>),
    /// <c>da</c>, <c>et</c>, <c>ft</c>, <c>md</c>, <c>am</c>, <c>pk</c>, <c>sf</c>, <c>tp</c>, <c>vn</c>, <c>vs</c>, <c>ss</c>,
    /// <c>sr</c>, <c>ch</c>, <c>txtvers</c>, <c>pw</c>.
    /// </summary>
    public static IReadOnlyList<string> Raop(AirplayReceiverOptions options, ulong features, AirplayStatusFlags flags) => throw new NotImplementedException("SPEC-receiver.md 3.1, wave 1");

    /// <summary>The <c>_airplay._tcp</c> TXT as one data blob for <c>/info</c> <c>txtAirPlay</c> (SPEC-receiver.md 3.2): length-prefixed entries.</summary>
    public static byte[] ToBlob(IReadOnlyList<string> entries) => throw new NotImplementedException("SPEC-receiver.md 3.2, wave 1");
}
