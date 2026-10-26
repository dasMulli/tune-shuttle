using System.Globalization;
using System.Net;
using Airplay2.Receiver.Rtsp;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Plist;
using Airplay2.Sender.Rtsp;

namespace Airplay2.Receiver.Discovery;

/// <summary>
/// Builds the <c>GET /info</c> answer (SPEC-receiver.md 3.2): the captured template
/// <c>docs/airplay2/test-vectors/info-response.bplist</c> (as the fake receiver uses it) with <c>name</c>, <c>model</c>,
/// <c>manufacturer</c>, <c>deviceID</c>, <c>pi</c>, <c>psi</c>, <c>pk</c>, <c>features</c>, <c>statusFlags</c>,
/// <c>sourceVersion</c>, <c>protocolVersion</c>, <c>senderAddress</c>, <c>initialVolume</c>, <c>supportedFormats</c> and
/// <c>txtAirPlay</c> replaced or added. The stage-1 <c>POST /info</c> with a <c>txtAirPlay</c> qualifier is answered the same way.
/// </summary>
/// <remarks>
/// The keys are set as shairport-sync's <c>generateInfoPlist</c> sets them (<c>[sps] rtsp.c:1106-1166</c>): integers for
/// <c>features</c>, <c>statusFlags</c> and the <c>supportedFormats</c> entries, a real for <c>initialVolume</c>, data for
/// <c>pk</c> and <c>txtAirPlay</c>, strings for the rest. The control session calls <see cref="Respond"/> for
/// <c>GET /info</c> and <c>POST /info</c> (<see cref="IsInfoRequest"/>); the event channel's <c>updateInfo</c> uses
/// <see cref="Build(ulong, AirplayStatusFlags, double, IPEndPoint)"/> (4.4).
/// </remarks>
public sealed class InfoResponseBuilder
{
    /// <summary>The manifest name of the embedded template (SPEC-receiver.md 3.2).</summary>
    public const string TemplateResourceName = "Airplay2.Receiver.info-response.bplist";

    /// <summary>The <c>/info</c> request URI (SPEC-receiver.md 3.2).</summary>
    public const string InfoUri = "/info";

    private static readonly Lazy<byte[]> EmbeddedTemplate = new(LoadEmbeddedTemplate);

    /// <summary>Creates the builder for a receiver with the embedded template (SPEC-receiver.md 3.2).</summary>
    /// <param name="options">Name, identity, model, manufacturer and password.</param>
    public InfoResponseBuilder(AirplayReceiverOptions options)
        : this(options, DefaultTemplate)
    {
    }

    /// <summary>Creates the builder for a receiver (SPEC-receiver.md 3.2).</summary>
    /// <param name="options">Name, identity, model and manufacturer.</param>
    /// <param name="template">The captured <c>/info</c> template as a binary plist.</param>
    /// <exception cref="FormatException">The template is not a binary plist dictionary.</exception>
    public InfoResponseBuilder(AirplayReceiverOptions options, ReadOnlyMemory<byte> template)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Template = template;

        // Validated once here so a broken template fails at receiver start, not at the first GET /info.
        _ = DecodeTemplate();
    }

    /// <summary>The embedded copy of <c>docs/airplay2/test-vectors/info-response.bplist</c> (SPEC-receiver.md 3.2).</summary>
    public static ReadOnlyMemory<byte> DefaultTemplate => EmbeddedTemplate.Value;

    /// <summary>The receiver options the keys come from.</summary>
    public AirplayReceiverOptions Options { get; }

    /// <summary>The template.</summary>
    public ReadOnlyMemory<byte> Template { get; }

    /// <summary>True for <c>GET /info</c> and the stage-1 <c>POST /info</c> (SPEC-receiver.md 3.2), the requests <see cref="Respond"/> answers.</summary>
    public static bool IsInfoRequest(RtspIncomingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.Uri;
        var query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }

        return path == InfoUri
            && (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) || string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Answers <c>GET /info</c> or the stage-1 <c>POST /info</c> on any connection, paired or not (SPEC-receiver.md 3.2): a bare
    /// request and a stage-1 request whose bplist body carries a <c>qualifier</c> array starting with a string (iOS sends
    /// <c>[txtAirPlay]</c>) get the same dictionary; a body that is not such a plist is answered 400, as shairport-sync's
    /// stage-1 <c>user_fail</c> (<c>[sps] rtsp.c:1172-1240</c>).
    /// </summary>
    /// <param name="request">The request (<see cref="IsInfoRequest"/>).</param>
    /// <param name="features">The feature word in effect (R15 or the ALAC-only variant of R3).</param>
    /// <param name="statusFlags">The status flags (R7, bit 17 while a stream is set up, 4.6).</param>
    /// <param name="initialVolumeDb">The current master level in dB (R9).</param>
    /// <param name="sender">The requesting connection's remote endpoint for <c>senderAddress</c>.</param>
    public RtspServerResponse Respond(RtspIncomingRequest request, ulong features, AirplayStatusFlags statusFlags, double initialVolumeDb, IPEndPoint sender)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sender);
        if (!request.Body.IsEmpty && !HasStageOneQualifier(request))
        {
            return RtspServerResponse.Status(RtspStatus.BadRequest);
        }

        return RtspServerResponse.Plist(Build(features, statusFlags, initialVolumeDb, sender));
    }

    /// <summary>
    /// The <c>/info</c> dictionary with the <c>txtAirPlay</c> blob built from <see cref="Options"/> (SPEC-receiver.md 3.1-3.2):
    /// the <c>_airplay._tcp</c> TXT of <see cref="ReceiverTxtRecords.Airplay"/> with the TXT flags of R7 (no bit 17).
    /// </summary>
    /// <param name="features">The feature word in effect (R15 or the ALAC-only variant of R3).</param>
    /// <param name="statusFlags">The status flags (R7, bit 17 while a stream is set up, 4.6).</param>
    /// <param name="initialVolumeDb">The current master level in dB (R9).</param>
    /// <param name="sender">The requesting connection's remote endpoint for <c>senderAddress</c>.</param>
    public PlistDictionary Build(ulong features, AirplayStatusFlags statusFlags, double initialVolumeDb, IPEndPoint sender)
    {
        var txt = ReceiverTxtRecords.Airplay(Options, features, ReceiverTxtRecords.TxtFlagsFor(Options));
        return Build(features, statusFlags, initialVolumeDb, sender, ReceiverTxtRecords.ToBlob(txt));
    }

    /// <summary>
    /// The <c>/info</c> dictionary for one request (SPEC-receiver.md 3.2); also the <c>value</c> of the event channel's
    /// <c>updateInfo</c> (4.4).
    /// </summary>
    /// <param name="features">The feature word in effect (R15 or the ALAC-only variant of R3).</param>
    /// <param name="statusFlags">The status flags (R7, bit 17 while a stream is set up, 4.6).</param>
    /// <param name="initialVolumeDb">The current master level in dB (R9).</param>
    /// <param name="sender">The requesting connection's remote endpoint for <c>senderAddress</c>.</param>
    /// <param name="txtAirPlay">The <c>_airplay._tcp</c> TXT blob (<see cref="ReceiverTxtRecords.ToBlob"/>).</param>
    public PlistDictionary Build(ulong features, AirplayStatusFlags statusFlags, double initialVolumeDb, IPEndPoint sender, byte[] txtAirPlay)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(txtAirPlay);
        var identity = Options.Identity;
        var aac = ReceiverFeatures.Has(features, ReceiverFeatures.AacBit);
        var info = DecodeTemplate();
        info["name"] = Options.Name;
        info["model"] = Options.Model;
        info["manufacturer"] = Options.Manufacturer;
        info["deviceID"] = identity.DeviceId;
        info["pi"] = identity.DeviceId;
        info["psi"] = identity.Psi;
        info["pk"] = identity.PublicKey;
        info["features"] = unchecked((long)features);
        info["statusFlags"] = (long)(uint)statusFlags;
        info["sourceVersion"] = ReceiverFeatures.SourceVersion;
        info["protocolVersion"] = ReceiverFeatures.ProtocolVersion;
        info["senderAddress"] = SenderAddress(sender);
        info["initialVolume"] = initialVolumeDb;
        info["supportedFormats"] = new PlistDictionary
        {
            ["audioStream"] = (long)ReceiverFeatures.AudioStreamFormats,
            ["bufferStream"] = (long)ReceiverFeatures.BufferStreamFormatsFor(aac),
        };
        info["txtAirPlay"] = txtAirPlay;
        return info;
    }

    /// <summary>
    /// The <c>senderAddress</c> value (SPEC-receiver.md 3.2): <c>&lt;ip&gt;:&lt;port&gt;</c> of the requesting connection as
    /// shairport-sync formats it (<c>"%s:%u"</c>, <c>[sps] rtsp.c:1133-1136</c>); an IPv4-mapped IPv6 address is written as IPv4.
    /// </summary>
    public static string SenderAddress(IPEndPoint sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var address = sender.Address.IsIPv4MappedToIPv6 ? sender.Address.MapToIPv4() : sender.Address;
        return address + ":" + sender.Port.ToString(CultureInfo.InvariantCulture);
    }

    private PlistDictionary DecodeTemplate() => BinaryPlist.DecodeDictionary(Template.Span);

    private static bool HasStageOneQualifier(RtspIncomingRequest request)
    {
        try
        {
            return request.PlistBody().GetArray("qualifier") is [string, ..];
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] LoadEmbeddedTemplate()
    {
        using var stream = typeof(InfoResponseBuilder).Assembly.GetManifestResourceStream(TemplateResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {TemplateResourceName} is missing (SPEC-receiver.md 3.2).");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
