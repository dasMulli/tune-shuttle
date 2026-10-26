using System.Text;
using Airplay2.Sender.Discovery;

namespace Airplay2.Receiver.Discovery;

/// <summary>
/// Builds the TXT records of the two advertised services (SPEC-receiver.md 3.1): shairport-sync's sets with our
/// identity, name, feature word (R15, or the ALAC-only variant of R3) and status flags (R7).
/// </summary>
/// <remarks>
/// The entries come in the order SPEC-receiver.md 3.1 lists them. The TXT records never carry bit 17
/// (<c>ReceiverSessionActive</c>): stream start and end change <c>/info</c> <c>statusFlags</c> only (3.1, 4.6), so the
/// builders mask it out of the <c>flags</c>/<c>sf</c> values.
/// </remarks>
public static class ReceiverTxtRecords
{
    /// <summary>The <c>_airplay._tcp</c> service type (SPEC-receiver.md 3.1).</summary>
    public const string AirplayServiceType = "_airplay._tcp";

    /// <summary>The <c>_raop._tcp</c> service type (SPEC-receiver.md 3.1).</summary>
    public const string RaopServiceType = "_raop._tcp";

    /// <summary>The longest TXT entry a DNS character-string can carry (RFC 6763 6.1): 255 bytes.</summary>
    public const int MaxEntryBytes = byte.MaxValue;

    /// <summary>The <c>_raop._tcp</c> instance name: <c>&lt;deviceid without colons&gt;@&lt;name&gt;</c> (SPEC-receiver.md 3.1).</summary>
    public static string RaopInstanceName(ReceiverIdentity identity, string name)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return identity.DeviceIdCompact + "@" + name;
    }

    /// <summary>True when <paramref name="options"/> configure a password (SPEC-receiver.md R7: null or empty is the open mode).</summary>
    public static bool HasPassword(AirplayReceiverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrEmpty(options.Password);
    }

    /// <summary>
    /// The TXT status flags for <paramref name="options"/> (SPEC-receiver.md 3.1, R7): <c>0x4</c>, or <c>0x84</c> with a
    /// password; never bit 17 (4.6).
    /// </summary>
    public static AirplayStatusFlags TxtFlagsFor(AirplayReceiverOptions options) => ReceiverFeatures.StatusFlagsFor(HasPassword(options), sessionActive: false);

    /// <summary>
    /// The <c>_airplay._tcp</c> TXT entries as <c>key=value</c> strings (SPEC-receiver.md 3.1): <c>acl</c>, <c>deviceid</c>,
    /// <c>features</c>, <c>flags</c>, <c>gid</c>, <c>gcgl</c>, <c>model</c>, <c>manufacturer</c>, <c>protovers</c>, <c>pi</c>,
    /// <c>psi</c>, <c>pk</c>, <c>srcvers</c>, <c>fex</c>.
    /// </summary>
    /// <param name="options">Identity, model and manufacturer.</param>
    /// <param name="features">The feature word in effect (<see cref="ReceiverFeatures.For"/>).</param>
    /// <param name="flags">The status flags (<see cref="TxtFlagsFor"/>); bit 17 is masked out.</param>
    public static IReadOnlyList<string> Airplay(AirplayReceiverOptions options, ulong features, AirplayStatusFlags flags)
    {
        ArgumentNullException.ThrowIfNull(options);
        var identity = options.Identity;
        var deviceId = identity.DeviceId;
        return
        [
            "acl=0",
            "deviceid=" + deviceId,
            "features=" + ReceiverFeatures.ToTxt(features),
            "flags=" + ReceiverFeatures.StatusFlagsToTxt(TxtFlags(flags)),
            "gid=" + deviceId,
            "gcgl=0",
            "model=" + options.Model,
            "manufacturer=" + options.Manufacturer,
            "protovers=" + ReceiverFeatures.ProtocolVersion,
            "pi=" + deviceId,
            "psi=" + identity.Psi,
            "pk=" + identity.PublicKeyHex,
            "srcvers=" + ReceiverFeatures.SourceVersion,
            "fex=" + ReceiverFeatures.ToFex(features),
        ];
    }

    /// <summary>
    /// The <c>_raop._tcp</c> TXT entries (SPEC-receiver.md 3.1): <c>cn</c> (<see cref="ReceiverFeatures.RaopCompressionFor"/>),
    /// <c>da</c>, <c>et</c>, <c>ft</c>, <c>md</c>, <c>am</c>, <c>pk</c>, <c>sf</c>, <c>tp</c>, <c>vn</c>, <c>vs</c>, <c>ss</c>,
    /// <c>sr</c>, <c>ch</c>, <c>txtvers</c>, <c>pw</c>.
    /// </summary>
    /// <param name="options">Identity, model and password.</param>
    /// <param name="features">The feature word in effect; bit 20 selects <c>cn=0,1,2</c> or the ALAC-only <c>cn=0,1</c> (R3).</param>
    /// <param name="flags">The status flags (<see cref="TxtFlagsFor"/>); bit 17 is masked out.</param>
    public static IReadOnlyList<string> Raop(AirplayReceiverOptions options, ulong features, AirplayStatusFlags flags)
    {
        ArgumentNullException.ThrowIfNull(options);
        var aac = ReceiverFeatures.Has(features, ReceiverFeatures.AacBit);
        return
        [
            "cn=" + ReceiverFeatures.RaopCompressionFor(aac),
            "da=true",
            "et=0",
            "ft=" + ReceiverFeatures.ToTxt(features),
            "md=0,1,2",
            "am=" + options.Model,
            "pk=" + options.Identity.PublicKeyHex,
            "sf=" + ReceiverFeatures.StatusFlagsToTxt(TxtFlags(flags)),
            "tp=UDP",
            "vn=65537",
            "vs=" + ReceiverFeatures.SourceVersion,
            "ss=16",
            "sr=44100",
            "ch=2",
            "txtvers=1",
            "pw=" + (HasPassword(options) ? "true" : "false"),
        ];
    }

    /// <summary>
    /// The <c>_airplay._tcp</c> TXT as one data blob for <c>/info</c> <c>txtAirPlay</c> (SPEC-receiver.md 3.2): length-prefixed
    /// entries, the wire form of a TXT record (RFC 6763 6.1; <c>[sps] rtsp.c:962-993</c> <c>add_pstring_to_malloc</c>).
    /// </summary>
    /// <exception cref="ArgumentException">An entry is longer than <see cref="MaxEntryBytes"/> bytes in UTF-8.</exception>
    public static byte[] ToBlob(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var output = new MemoryStream(512);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(entries));
            var bytes = Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > MaxEntryBytes)
            {
                throw new ArgumentException($"A TXT entry of {bytes.Length} bytes does not fit; at most {MaxEntryBytes} do (RFC 6763 6.1).", nameof(entries));
            }

            output.WriteByte((byte)bytes.Length);
            output.Write(bytes);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Both services for <paramref name="options"/> on <paramref name="port"/> (SPEC-receiver.md 3.1): the instance names,
    /// the TXT entries with the TXT flags of R7 (<see cref="TxtFlagsFor"/>) and the given feature word.
    /// </summary>
    /// <param name="options">Name, identity, password, model and manufacturer.</param>
    /// <param name="port">The RTSP port both services point at (the bound port, never 0).</param>
    /// <param name="features">The feature word in effect (<see cref="ReceiverFeatures.For"/>).</param>
    public static ReceiverAdvertisement Advertisement(AirplayReceiverOptions options, int port, ulong features)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        var flags = TxtFlagsFor(options);
        return new ReceiverAdvertisement(
            options.Name,
            RaopInstanceName(options.Identity, options.Name),
            port,
            Airplay(options, features, flags),
            Raop(options, features, flags));
    }

    private static AirplayStatusFlags TxtFlags(AirplayStatusFlags flags) => flags & ~AirplayStatusFlags.ReceiverSessionActive;
}
