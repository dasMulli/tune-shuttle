using System.Net;
using Airplay2.Sender.Discovery;
using Airplay2.Sender.Plist;

namespace Airplay2.Receiver.Discovery;

/// <summary>
/// Builds the <c>GET /info</c> answer (SPEC-receiver.md 3.2): the captured template
/// <c>docs/airplay2/test-vectors/info-response.bplist</c> (as the fake receiver uses it) with <c>name</c>, <c>model</c>,
/// <c>manufacturer</c>, <c>deviceID</c>, <c>pi</c>, <c>psi</c>, <c>pk</c>, <c>features</c>, <c>statusFlags</c>,
/// <c>sourceVersion</c>, <c>protocolVersion</c>, <c>senderAddress</c>, <c>initialVolume</c>, <c>supportedFormats</c> and
/// <c>txtAirPlay</c> replaced or added. The stage-1 <c>POST /info</c> with a <c>txtAirPlay</c> qualifier is answered the same way.
/// </summary>
/// <remarks>
/// Wave 0 scaffold: the surface compiles; the builder is the wave 1 <c>advertise</c> package (SPEC-receiver.md 13).
/// </remarks>
public sealed class InfoResponseBuilder
{
    /// <summary>Creates the builder for a receiver (SPEC-receiver.md 3.2).</summary>
    /// <param name="options">Name, identity, model and manufacturer.</param>
    /// <param name="template">The captured <c>/info</c> template as a binary plist.</param>
    public InfoResponseBuilder(AirplayReceiverOptions options, ReadOnlyMemory<byte> template)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Template = template;
    }

    /// <summary>The receiver options the keys come from.</summary>
    public AirplayReceiverOptions Options { get; }

    /// <summary>The template.</summary>
    public ReadOnlyMemory<byte> Template { get; }

    /// <summary>
    /// The <c>/info</c> dictionary for one request (SPEC-receiver.md 3.2); also the <c>value</c> of the event channel's
    /// <c>updateInfo</c> (4.4).
    /// </summary>
    /// <param name="features">The feature word in effect (R15 or the ALAC-only variant of R3).</param>
    /// <param name="statusFlags">The status flags (R7, bit 17 while a stream is set up, 4.6).</param>
    /// <param name="initialVolumeDb">The current master level in dB (R9).</param>
    /// <param name="sender">The requesting connection's remote endpoint for <c>senderAddress</c>.</param>
    /// <param name="txtAirPlay">The <c>_airplay._tcp</c> TXT blob (<see cref="ReceiverTxtRecords.ToBlob"/>).</param>
    public PlistDictionary Build(ulong features, AirplayStatusFlags statusFlags, double initialVolumeDb, IPEndPoint sender, byte[] txtAirPlay) => throw new NotImplementedException("SPEC-receiver.md 3.2, wave 1");
}
