using Airplay2.Sender.Discovery;

namespace Airplay2.Sender.Session;

/// <summary>
/// Known receiver quirks, from the live probes recorded in SPEC.md section 4. Each rule is keyed on TXT facts that
/// describe the receiver's AirPlay stack (SPEC-control.md 1.7), not on a product name: <c>model</c> only for the open
/// shairport-sync stack, otherwise <c>srcvers</c>. The first matching rule wins.
/// </summary>
internal static class KnownReceiverQuirks
{
    /// <summary>shairport-sync's TXT <c>model</c> (<c>model=%s</c> of <c>config.model</c> [sps:rtsp.c:1071]; 5.5.2 in the test container advertises <c>ShairportSync</c> in TXT and <c>GET /info</c>, artifacts/live/2026-10-09-rtsp-get-info-shairport.log).</summary>
    public const string ShairportSyncModel = "ShairportSync";

    /// <summary>
    /// The TXT <c>srcvers</c> of the srcvers-366 speaker (receiver A, features <c>0x445F8A00,0x1C340</c>, SPEC-control.md
    /// 1.7). shairport-sync reports the same value (<c>config.srcvers</c> [sps:shairport.c:569]), so its rule comes first.
    /// </summary>
    public const string Srcvers366 = "366.0";

    /// <summary>
    /// shairport-sync 5.5.2 refuses NTP sessions with 200 and an empty body (no eventPort), not 501 (SPEC-control.md 4.2
    /// note, live 2026-10-09).
    /// </summary>
    public static ReceiverQuirks ShairportSync { get; } = new() { TimingProtocol = TimingProtocol.Ptp };

    /// <summary>
    /// srcvers 366.0: probe P1, control part (2026-10-09): the NTP session SETUP is answered 200 with eventPort (and
    /// timingPort 7001), 0xD2 timing requests arrive at our timing port right at the stream SETUP: the realtime stream runs
    /// under NTP (SPEC.md D1 stands). The receiver also accepts a PTP session SETUP, which is not used. The same stack
    /// answers DMAP text with 500 and is slow to answer FLUSH (SPEC.md P9, SPEC-control.md 4.8 note); both are handled
    /// for every receiver (best-effort metadata, long request timeouts), so they need no switch here.
    /// </summary>
    public static ReceiverQuirks SpeakerSrcvers366 { get; } = new() { TimingProtocol = TimingProtocol.Ntp };

    /// <summary>The quirks for a receiver's TXT record, or <see cref="ReceiverQuirks.Default"/>.</summary>
    public static ReceiverQuirks For(AirplayTxtRecord txt)
    {
        ArgumentNullException.ThrowIfNull(txt);
        return For(txt.Model, txt.SourceVersion);
    }

    /// <summary>The quirks for the TXT <c>model</c> and <c>srcvers</c>, or <see cref="ReceiverQuirks.Default"/>.</summary>
    public static ReceiverQuirks For(string? model, string? sourceVersion)
    {
        if (string.Equals(model, ShairportSyncModel, StringComparison.OrdinalIgnoreCase))
        {
            return ShairportSync;
        }

        if (string.Equals(sourceVersion?.Trim(), Srcvers366, StringComparison.Ordinal))
        {
            return SpeakerSrcvers366;
        }

        return ReceiverQuirks.Default;
    }
}
