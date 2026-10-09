namespace Airplay2.Sender.Session;

/// <summary>
/// Quirk defaults per receiver model, from the live probes recorded in SPEC.md section 4. The keys are the TXT
/// <c>model</c> values (SPEC-control.md 1.7).
/// </summary>
internal static class KnownReceiverQuirks
{
    /// <summary>The Test Speaker ABCD's TXT <c>model</c> (SPEC-control.md 1.7).</summary>
    public const string SpeakerAspeakera = "Test Speaker ABCD";

    /// <summary>shairport-sync's TXT <c>model</c> (<c>model=%s</c> of <c>config.model</c> [sps:rtsp.c:1071]; 5.5.2 in the test container advertises <c>ShairportSync</c> in TXT and <c>GET /info</c>, artifacts/live/2026-10-09-rtsp-get-info-shairport.log).</summary>
    public const string ShairportSync = "ShairportSync";

    private static readonly Dictionary<string, ReceiverQuirks> ByModel = new(StringComparer.OrdinalIgnoreCase)
    {
        // Probe P1, control part (2026-10-09): the NTP session SETUP is answered 200 with eventPort (and timingPort 7001),
        // 0xD2 timing requests arrive at our timing port right at the stream SETUP: the realtime stream runs under NTP
        // (SPEC.md D1 stands). The SpeakerA also accepts a PTP session SETUP, which is not used.
        [SpeakerAspeakera] = new ReceiverQuirks { TimingProtocol = TimingProtocol.Ntp },

        // shairport-sync 5.5.2 refuses NTP sessions with 200 and an empty body (no eventPort), not 501
        // (SPEC-control.md 4.2 note, live 2026-10-09).
        [ShairportSync] = new ReceiverQuirks { TimingProtocol = TimingProtocol.Ptp },
    };

    /// <summary>The quirks for <paramref name="model"/>, or <see cref="ReceiverQuirks.Default"/>.</summary>
    public static ReceiverQuirks For(string? model) =>
        model is not null && ByModel.TryGetValue(model, out var quirks) ? quirks : ReceiverQuirks.Default;
}
