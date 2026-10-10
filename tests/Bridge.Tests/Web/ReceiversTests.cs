using System.Net;
using Airplay2.FakeReceiver;
using Bridge.Contracts;
using Bridge.Tests.Service;
using Bridge.Web;
using Bridge.Web.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>bUnit tests of the Receivers page (route <c>/receivers</c>), with stubbed API and against the service with a fake receiver.</summary>
public sealed class ReceiversTests : ComponentTestBase
{
    [Fact]
    public void Receivers_page_lists_receivers_and_connect_applies_the_answer()
    {
        var api = UseApi((request, _) => PathOf(request) switch
        {
            "/api/receivers/02:FA:CE:00:00:01/connect" => StubHandler.Json(Dto.Receiver("02:FA:CE:00:00:01", "Fake Receiver 1", state: ReceiverConnectionState.Connected)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.StatusChanged(Dto.Status("Wohnzimmer")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("02:FA:CE:00:00:01", "Fake Receiver 1")));
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("02:FA:CE:00:00:02", "Fake Receiver 2", pairing: PairingState.PinRequired, online: false)));

        var cut = _ctx.Render<Receivers>();

        Assert.Contains("Wohnzimmer", cut.Markup, StringComparison.Ordinal);
        var cards = cut.FindAll("article.receiver");
        Assert.Equal(2, cards.Count);
        Assert.Contains("PIN pairing needed", cards[1].TextContent, StringComparison.Ordinal);
        Assert.True(cards[1].QuerySelector("button.primary")!.HasAttribute("disabled")); // offline: no connect

        cut.Find("article[data-receiver-id='02:FA:CE:00:00:01'] button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(api.Requests, r => r.Method == HttpMethod.Post && r.Path == "/api/receivers/02:FA:CE:00:00:01/connect");
            Assert.Contains("Disconnect", cut.Find("article[data-receiver-id='02:FA:CE:00:00:01']").TextContent, StringComparison.Ordinal);
            Assert.Equal("Connected", cut.Find("article[data-receiver-id='02:FA:CE:00:00:01'] .badge").TextContent);
        });
    }

    [Fact]
    public void Receiver_slider_and_offset_send_their_values()
    {
        var api = UseApi((request, body) => PathOf(request) switch
        {
            "/api/receivers/a/volume" => StubHandler.Json(Dto.Receiver("a", "A", level: 73)),
            "/api/receivers/a/offset" => StubHandler.Json(Dto.Receiver("a", "A", offset: -441)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        _state.Apply(new BridgeEvent.ReceiverChanged(Dto.Receiver("a", "A", level: 50)));
        var cut = _ctx.Render<Receivers>();

        cut.Find("input[type=range]").Input("73");
        cut.WaitForAssertion(() =>
        {
            var request = Assert.Single(api.Requests, r => r.Path == "/api/receivers/a/volume");
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Contains("\"level\":73", request.Body, StringComparison.Ordinal);
            Assert.Equal("73", cut.Find(".level output").TextContent);
        });

        // An offset beyond ±2 s is refused before it reaches the service.
        cut.Find("form.offset input").Change("100000");
        cut.Find("form.offset").Submit();
        Assert.Contains("within", cut.Find("article.receiver p.error").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Requests, r => r.Path == "/api/receivers/a/offset");

        cut.Find("form.offset input").Change("-441");
        cut.Find("form.offset").Submit();
        cut.WaitForAssertion(() => Assert.Contains(api.Requests, r => r.Path == "/api/receivers/a/offset" && r.Body!.Contains("-441", StringComparison.Ordinal)));
        Assert.Equal(-441, _state.Receiver("a")!.OffsetFrames);
    }

    [Fact]
    public async Task Receivers_page_against_the_service_connects_a_fake_receiver_and_sets_its_volume()
    {
        await using var fake = await RecordedFake.StartAsync(new FakeReceiverOptions { Name = "Fake Receiver 1" });
        await using var factory = new BridgeFactory();
        factory.Browser.Add(fake.Describe());
        _ctx.Services.AddSingleton(WebClients.Api(factory));
        var events = WebClients.Events(factory, _state);
        _ctx.Services.AddSingleton(events);
        _ctx.Services.AddSingleton<BridgeCommands>();
        events.Start();
        try
        {
            var cut = _ctx.Render<Receivers>();
            cut.WaitForAssertion(() => Assert.Equal("Fake Receiver 1", cut.Find("article.receiver h2").TextContent), RecordedFake.Patience);

            cut.Find("article.receiver button.primary").Click();
            await fake.WaitAsync<FakeReceiverEvent.StreamSetup>();
            cut.WaitForAssertion(() => Assert.Equal("Connected", cut.Find("article.receiver .badge").TextContent), RecordedFake.Patience);

            cut.Find("article.receiver input[type=range]").Input("20");
            await fake.WaitAsync<FakeReceiverEvent.Volume>(v => Math.Abs(v.Db - -24.0) < 0.001); // level 20 -> -30 + 0.3 * 20
            cut.WaitForAssertion(() => Assert.Equal("20", cut.Find("article.receiver .level output").TextContent), RecordedFake.Patience);

            cut.Find("article.receiver button").Click(); // Disconnect
            await fake.WaitAsync<FakeReceiverEvent.Teardown>();
            cut.WaitForAssertion(() => Assert.Equal("Available", cut.Find("article.receiver .badge").TextContent), RecordedFake.Patience);
        }
        finally
        {
            await _ctx.DisposeComponentsAsync();
            await events.DisposeAsync();
        }
    }
}
