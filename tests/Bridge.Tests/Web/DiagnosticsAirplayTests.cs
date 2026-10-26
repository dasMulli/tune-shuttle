using System.Net;
using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Pages;
using Bunit;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// bUnit tests of the AirPlay receiver card of the Diagnostics page (SPEC-receiver.md 9.2, 11.1; UI-SPEC 4.7): every fact
/// of a full DTO, the off state with dashes when the DTOs carry no AirPlay part, culture formatting through
/// <see cref="Format"/>, and the 5 s refresh. In <see cref="ProcessCultureCollection"/>: the tests switch the culture.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed class DiagnosticsAirplayTests : ComponentTestBase
{
    private static readonly DiagnosticsDto Stats = new([], ["Ethernet: 192.0.2.10"], 6002, 6001, 10, 0, 0, []);

    private static readonly AirplayDiagnosticsDto Receiver = new(
        RtspPort: 7000,
        PtpBound: true,
        PtpOffsetNs: 1_234_567,
        StreamType: "buffered",
        Format: "AAC-LC/44100/2",
        BufferedMs: 1834,
        StreamOffsetMs: 0,
        LateFrames: 12,
        MissingFrames: 3456,
        DriftCorrections: 7,
        DecoderStatus: "libavcodec 61");

    private static readonly AirplayReceiverDto Streaming = new("streaming", "TuneShuttle", "Test Phone", "TestModel1,1", true);

    [Fact]
    public void Card_renders_every_fact_of_a_full_dto()
    {
        using var scope = new CultureScope("en");
        UseApi((request, _) => PathOf(request) == "/api/diagnostics"
            ? StubHandler.Json(Stats with { Airplay = Receiver })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = Streaming }));
        var cut = _ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() => Assert.Equal("1,834 ms buffered · offset 0 ms", Line(cut, "airplay-buffer")));
        Assert.Equal("AirPlay receiver", cut.Find("#diagnostics-airplay").TextContent);
        Assert.Equal("diagnostics-airplay", cut.Find("#diagnostics-airplay").ParentElement!.ParentElement!.GetAttribute("aria-labelledby"));
        Assert.Contains("card", Card(cut).ClassList);
        Assert.Equal("Streaming", Fact(cut, "State"));
        Assert.Equal("Test Phone", Fact(cut, "Sender"));
        Assert.Equal("libavcodec 61", Fact(cut, "Decoder"));
        Assert.Equal("buffered · AAC-LC/44100/2", Line(cut, "airplay-stream"));
        Assert.Equal("12 late · 3,456 missing · 7 drift", Line(cut, "airplay-late"));
        Assert.Equal("PTP offset 1.235 ms", Line(cut, "airplay-ptp"));

        // Four fact cards and the log: the receiver card sits before the log.
        Assert.Equal(["diagnostics-status", "diagnostics-network", "diagnostics-probes", "diagnostics-airplay", "diagnostics-log"],
            cut.FindAll(".cards > section.card").Select(s => s.GetAttribute("aria-labelledby")));
    }

    [Fact]
    public void Absent_airplay_dtos_render_the_off_state_with_dashes()
    {
        using var scope = new CultureScope("en");
        UseApi((request, _) => PathOf(request) == "/api/diagnostics"
            ? StubHandler.Json(Stats)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status()));
        var cut = _ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".retransmit-ratio")));
        Assert.Equal("Off", Fact(cut, "State"));
        Assert.Equal("–", Fact(cut, "Sender"));
        Assert.Equal("–", Fact(cut, "Decoder"));

        // Without DiagnosticsDto.Airplay the four value lines are one dash, not templates full of dashes.
        Assert.Equal("–", Line(cut, "airplay-none"));
        Assert.Empty(Card(cut).QuerySelectorAll(".airplay-stream, .airplay-buffer, .airplay-late, .airplay-ptp"));
    }

    [Fact]
    public void A_failed_diagnostics_request_before_the_status_keeps_the_card_loading()
    {
        using var scope = new CultureScope("en");
        UseApi((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var cut = _ctx.Render<Diagnostics>();

        // The Network card shows the error and Retry; the receiver card does not pretend the receiver is off.
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("section[aria-labelledby=diagnostics-network] button")));
        Assert.Equal("Loading…", Card(cut).QuerySelector(".meta")!.TextContent.Trim());
        Assert.Empty(Card(cut).QuerySelectorAll("dt"));

        // The status snapshot brings the state and sender; the diagnostics part stays a dash.
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = Streaming }));
        cut.WaitForAssertion(() => Assert.Equal("Streaming", Fact(cut, "State")));
        Assert.Equal("–", Line(cut, "airplay-none"));
    }

    [Fact]
    public void Rows_follow_the_spec_order_and_the_lines_are_meta()
    {
        using var scope = new CultureScope("en");
        UseApi((request, _) => PathOf(request) == "/api/diagnostics"
            ? StubHandler.Json(Stats with { Airplay = Receiver })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = Streaming }));
        var cut = _ctx.Render<Diagnostics>();
        cut.WaitForAssertion(() => Assert.Equal("PTP offset 1.235 ms", Line(cut, "airplay-ptp")));

        // SPEC-receiver.md 9.2: state, sender, stream, buffer, late, PTP, decoder.
        Assert.Equal(["airplay-state", "airplay-sender", "airplay-stream", "airplay-buffer", "airplay-late", "airplay-ptp", "airplay-decoder"],
            Card(cut).QuerySelectorAll("dd, p").Select(e => e.ClassList.Single(c => c.StartsWith("airplay-", StringComparison.Ordinal))));
        Assert.All(Card(cut).QuerySelectorAll("p"), p => Assert.Contains("meta", p.ClassList));
    }

    [Fact]
    public void Negative_offsets_use_the_minus_sign()
    {
        using var scope = new CultureScope("en");
        Assert.Equal("−2.500", Diagnostics.SignedMilliseconds(-2_500_000));
        Assert.Equal("2.500", Diagnostics.SignedMilliseconds(2_500_000));
        Assert.Equal("0.000", Diagnostics.SignedMilliseconds(-100));
    }

    [Fact]
    public void A_receiver_without_stream_or_ptp_port_shows_dashes_and_anchor_only_timing()
    {
        using var scope = new CultureScope("en");
        UseApi((request, _) => PathOf(request) == "/api/diagnostics"
            ? StubHandler.Json(Stats with
            {
                Airplay = Receiver with { PtpBound = false, PtpOffsetNs = null, StreamType = null, Format = null, DecoderStatus = null },
            })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = new AirplayReceiverDto("advertising", "TuneShuttle", null, null, false) }));
        var cut = _ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() => Assert.Equal("PTP port not bound (anchor-only timing)", Line(cut, "airplay-ptp")));
        Assert.Equal("Advertising", Fact(cut, "State"));
        Assert.Equal("–", Fact(cut, "Sender"));
        Assert.Equal("–", Fact(cut, "Decoder"));
        Assert.Equal("– · –", Line(cut, "airplay-stream"));
    }

    [Fact]
    public void A_bound_ptp_port_without_an_estimate_shows_a_dash()
    {
        using var scope = new CultureScope("en");
        UseApi((request, _) => PathOf(request) == "/api/diagnostics"
            ? StubHandler.Json(Stats with { Airplay = Receiver with { PtpOffsetNs = null, Format = null } })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = Streaming }));
        var cut = _ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() => Assert.Equal("PTP offset – ms", Line(cut, "airplay-ptp")));
        Assert.Equal("buffered · –", Line(cut, "airplay-stream"));
    }

    [Fact]
    public async Task Numbers_and_state_words_follow_the_culture()
    {
        using var scope = new CultureScope("de");
        var (ctx, state, _, _, _) = UiTest.Create("de", respond: (request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/api/diagnostics"
                ? StubHandler.Json(Stats with { Airplay = Receiver with { PtpOffsetNs = -2_500_000, StreamOffsetMs = 1450 } })
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var context = ctx;
        state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = Streaming }));
        var cut = ctx.Render<Diagnostics>();

        cut.WaitForAssertion(() => Assert.Equal("1.834 ms gepuffert · Versatz 1.450 ms", Spaces(Line(cut, "airplay-buffer"))));
        Assert.Equal("AirPlay-Empfänger", cut.Find("#diagnostics-airplay").TextContent);
        Assert.Equal("12 verspätet · 3.456 fehlend · 7 Drift", Spaces(Line(cut, "airplay-late")));
        Assert.Equal("PTP-Versatz −2,500 ms", Spaces(Line(cut, "airplay-ptp")));
        Assert.Equal("Spielt", Fact(cut, "Zustand"));

        string[] states = ["off", "starting", "advertising", "connected", "streaming", "error"];
        string[] words = ["Aus", "Startet", "Sichtbar", "Verbunden", "Spielt", "Fehler"];
        for (var i = 0; i < states.Length; i++)
        {
            state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = Streaming with { State = states[i] } }));
            var expected = words[i];
            cut.WaitForAssertion(() => Assert.Equal(expected, Fact(cut, "Zustand")));
        }

        // A state this build does not know is shown as sent (UI-SPEC 3.5).
        state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = Streaming with { State = "future" } }));
        cut.WaitForAssertion(() => Assert.Equal("future", Fact(cut, "Zustand")));
    }

    [Fact]
    public void Refresh_updates_the_values()
    {
        using var scope = new CultureScope("en");
        var calls = 0;
        UseApi((request, _) => PathOf(request) != "/api/diagnostics"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Interlocked.Increment(ref calls) == 1
                ? StubHandler.Json(Stats with { Airplay = Receiver })
                : StubHandler.Json(Stats with { Airplay = Receiver with { LateFrames = 13, BufferedMs = 1700, PtpOffsetNs = 2_000_000 } }));
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = Streaming }));
        var cut = _ctx.Render<Diagnostics>();
        cut.WaitForAssertion(() => Assert.Equal("12 late · 3,456 missing · 7 drift", Line(cut, "airplay-late")));

        // The page's 5 s loop brings the next answer.
        cut.WaitForAssertion(() => Assert.Equal("13 late · 3,456 missing · 7 drift", Line(cut, "airplay-late")), Diagnostics.RefreshInterval * 3);
        Assert.Equal("1,700 ms buffered · offset 0 ms", Line(cut, "airplay-buffer"));
        Assert.Equal("PTP offset 2.000 ms", Line(cut, "airplay-ptp"));

        // The status part follows the status events.
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status() with { Airplay = new AirplayReceiverDto("connected", "TuneShuttle", "Other Phone", null, true) }));
        cut.WaitForAssertion(() => Assert.Equal("Connected", Fact(cut, "State")));
        Assert.Equal("Other Phone", Fact(cut, "Sender"));
    }

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<Diagnostics> cut) => cut.Find("#diagnostics-airplay").ParentElement!.ParentElement!;

    /// <summary>The text of the card's value line with the class <paramref name="name"/>.</summary>
    private static string Line(IRenderedComponent<Diagnostics> cut, string name) =>
        Card(cut).QuerySelector("." + name)?.TextContent.Trim() ?? throw new Xunit.Sdk.XunitException($"No line .{name} in the AirPlay card");

    /// <summary>The text of the <c>dd</c> that follows the AirPlay card's <c>dt</c> reading <paramref name="label"/>.</summary>
    private static string Fact(IRenderedComponent<Diagnostics> cut, string label)
    {
        var terms = Card(cut).QuerySelectorAll("dt");
        var term = terms.SingleOrDefault(dt => dt.TextContent == label)
            ?? throw new Xunit.Sdk.XunitException($"No fact \"{label}\" in: {string.Join(", ", terms.Select(dt => dt.TextContent))}");
        return term.NextElementSibling!.TextContent.Trim();
    }

    private static string Spaces(string text) => text.Replace(' ', ' ').Replace(' ', ' ');
}
