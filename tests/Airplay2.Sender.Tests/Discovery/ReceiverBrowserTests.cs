using System.Net;
using System.Net.Sockets;
using Airplay2.Sender.Discovery;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Sender.Tests.Discovery;

/// <summary>
/// The browser's cache, merge and event logic (SPEC-control.md 1.1) fed with the re-encoded LAN capture and a manual
/// clock. No sockets: <see cref="MdnsReceiverBrowser.Ingest"/> and <see cref="MdnsReceiverBrowser.Sweep"/> are driven
/// directly.
/// </summary>
public class ReceiverBrowserTests
{
    private const int InterfaceIndex = 7;

    private static (MdnsReceiverBrowser Browser, ManualTimeProvider Clock) Create(ReceiverBrowserOptions? options = null)
    {
        var clock = new ManualTimeProvider();
        return (new MdnsReceiverBrowser(options ?? new ReceiverBrowserOptions(), NullLogger<MdnsReceiverBrowser>.Instance, clock), clock);
    }

    private static List<ReceiverEvent> Drain(MdnsReceiverBrowser browser)
    {
        var list = new List<ReceiverEvent>();
        while (browser.Events.TryRead(out var e))
        {
            list.Add(e);
        }

        return list;
    }

    private static IReadOnlyList<DnsQuestion> Feed(MdnsReceiverBrowser browser, byte[] packet, bool legacy = false) =>
        browser.Ingest(packet, BrowseProbe.Reflector, InterfaceIndex, legacy);

    [Fact]
    public async Task Reflected_capture_yields_both_receivers_with_record_addresses_never_the_reflector()
    {
        var (browser, _) = Create();
        await using var _ = browser;
        foreach (var instance in BrowseProbe.Instances.Values)
        {
            Feed(browser, BrowseProbe.Response(instance));
        }

        var receivers = browser.Snapshot().OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(2, receivers.Count);
        Assert.DoesNotContain(receivers.SelectMany(r => r.Addresses), a => a.Equals(BrowseProbe.Reflector.Address));

        var speakerA = receivers.Single(r => r.Id == BrowseProbe.SpeakerAId);
        Assert.Equal("Test Speaker ABCD", speakerA.Name);
        Assert.Equal(7000, speakerA.Port);
        Assert.Equal(IPAddress.Parse("192.0.2.26"), speakerA.Addresses[0]);
        Assert.Equal(IPAddress.Parse("192.0.2.26"), speakerA.AddressOf(AddressFamily.InterNetwork));
        Assert.Equal(3, speakerA.Addresses.Count);
        Assert.Equal(IPAddress.Parse("2001:db8:0:30:1a:2bff:fe3c:4d5e"), speakerA.Addresses[1]);
        Assert.True(speakerA.Addresses[2].IsIPv6LinkLocal);
        Assert.Equal(InterfaceIndex, speakerA.Addresses[2].ScopeId); // link-local scoped to the arrival interface

        // merged with the _raop record by deviceid
        Assert.NotNull(speakerA.Raop);
        Assert.Equal(new[] { 0, 4 }, speakerA.Raop!.EncryptionTypes);
        Assert.Equal("UDP", speakerA.Raop.Transport);

        // SPEC-control.md 1.7 conclusions for receiver A (the srcvers-366 speaker)
        var caps = speakerA.Capabilities;
        Assert.True(caps.IsAirPlay2);
        Assert.Equal(PairingRequirement.Transient, caps.Pairing);
        Assert.True(caps.SupportsRealtimeStream);
        Assert.True(caps.SupportsBufferedStream);
        Assert.True(caps.HasPtpClock);
        Assert.True(caps.SupportsAlac);
        Assert.True(caps.WantsTextMetadata && caps.WantsArtwork && caps.WantsProgress);
        Assert.False(caps.UsesNowPlayingCommands);

        // the LG TV: no _raop record, PIN required (flags bit 9)
        var lg = receivers.Single(r => r.Id == BrowseProbe.LgId);
        Assert.Null(lg.Raop);
        Assert.Equal(PairingRequirement.Pin, lg.Capabilities.Pairing);
        Assert.Equal(new[] { IPAddress.Parse("198.51.100.200") }, lg.Addresses);

        var events = Drain(browser);
        Assert.Equal(2, events.OfType<ReceiverEvent.Found>().Count());
    }

    [Fact]
    public async Task Raop_record_arriving_later_updates_the_receiver()
    {
        var (browser, clock) = Create();
        await using var _ = browser;
        Feed(browser, BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay]));
        var found = Assert.IsType<ReceiverEvent.Found>(Assert.Single(Drain(browser)));
        Assert.Null(found.Receiver.Raop);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        Feed(browser, BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerARaop]));
        var updated = Assert.IsType<ReceiverEvent.Updated>(Assert.Single(Drain(browser)));
        Assert.NotNull(updated.Receiver.Raop);
        Assert.Null(updated.Previous.Raop);
        Assert.Single(browser.Snapshot());
    }

    [Fact]
    public async Task Raop_record_before_airplay_is_merged_on_found()
    {
        var (browser, _) = Create();
        await using var _ = browser;
        Feed(browser, BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerARaop]));
        Assert.Empty(Drain(browser)); // AirPlay-1-only devices are not reported
        Feed(browser, BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay]));
        var found = Assert.IsType<ReceiverEvent.Found>(Assert.Single(Drain(browser)));
        Assert.NotNull(found.Receiver.Raop);
    }

    [Fact]
    public async Task Ipv6_disabled_drops_aaaa_and_an_aaaa_only_receiver_asks_for_a()
    {
        var (browser, _) = Create(new ReceiverBrowserOptions { EnableIPv6 = false });
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        Feed(browser, BrowseProbe.Response(speakerA));
        Assert.Equal(new[] { IPAddress.Parse("192.0.2.26") }, Assert.Single(browser.Snapshot()).Addresses);

        var (browser2, _) = Create(new ReceiverBrowserOptions { EnableIPv6 = false });
        await using var __ = browser2;
        var v6Only = speakerA with { Addresses = [.. speakerA.Addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)] };
        var questions = Feed(browser2, BrowseProbe.Response(v6Only));
        Assert.Empty(browser2.Snapshot());
        var q = Assert.Single(questions);
        Assert.Equal(DnsType.A, q.Type);
        Assert.Equal(DnsName.Parse(BrowseProbe.SpeakerAHost), q.Name);
    }

    [Fact]
    public async Task Aaaa_only_receiver_is_found_with_ipv6_enabled()
    {
        var (browser, _) = Create();
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        var v6Only = speakerA with { Addresses = [IPAddress.Parse("2001:db8:0:30:1a:2bff:fe3c:4d5e")] };
        Feed(browser, BrowseProbe.Response(v6Only));
        var receiver = Assert.Single(browser.Snapshot());
        Assert.Equal(AddressFamily.InterNetworkV6, Assert.Single(receiver.Addresses).AddressFamily);
        Assert.Null(receiver.AddressOf(AddressFamily.InterNetwork));
    }

    [Fact]
    public async Task Ttl_expiry_with_grace_raises_lost_and_refresh_queries_come_first()
    {
        var (browser, clock) = Create(new ReceiverBrowserOptions { ExpiryGrace = TimeSpan.FromSeconds(10) });
        await using var _ = browser;
        Feed(browser, BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay]));
        Drain(browser);

        // 80 % (+ up to 2 % jitter) of the 120 s SRV/A/AAAA TTL: refresh questions, receiver still there
        clock.Advance(TimeSpan.FromSeconds(99));
        var refresh = browser.Sweep();
        Assert.Contains(refresh, q => q.Type == DnsType.Srv && q.Name.Equals(BrowseProbe.InstanceName(BrowseProbe.SpeakerAAirplay)));
        Assert.Contains(refresh, q => q.Type == DnsType.A && q.Name.Equals(DnsName.Parse(BrowseProbe.SpeakerAHost)));
        Assert.Empty(browser.Sweep()); // asked once per stage
        clock.Advance(TimeSpan.FromSeconds(12)); // t = 111 s, past 85 % + jitter
        Assert.NotEmpty(browser.Sweep());

        // past the TTL but inside the grace period: still present
        clock.Advance(TimeSpan.FromSeconds(14)); // t = 125 s
        browser.Sweep();
        Assert.Single(browser.Snapshot());
        Assert.Empty(Drain(browser));

        // past TTL + grace
        clock.Advance(TimeSpan.FromSeconds(6)); // t = 131 s
        browser.Sweep();
        Assert.Empty(browser.Snapshot());
        var lost = Assert.IsType<ReceiverEvent.Lost>(Assert.Single(Drain(browser)));
        Assert.Equal(BrowseProbe.SpeakerAId, lost.Receiver.Id);
    }

    [Fact]
    public async Task Answer_refresh_resets_the_ttl()
    {
        var (browser, clock) = Create();
        await using var _ = browser;
        var packet = BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay]);
        Feed(browser, packet);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(100));
            Feed(browser, packet);
            browser.Sweep();
        }

        Assert.Single(browser.Snapshot());
        Assert.Single(Drain(browser)); // only the first Found
    }

    [Fact]
    public async Task Goodbye_removes_the_receiver_one_second_later_without_grace()
    {
        var (browser, clock) = Create(new ReceiverBrowserOptions { ExpiryGrace = TimeSpan.FromSeconds(10) });
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        Feed(browser, BrowseProbe.Response(speakerA));
        Drain(browser);

        clock.Advance(TimeSpan.FromSeconds(5));
        var goodbye = BrowseProbe.Records(speakerA, includeAddresses: false).Select(r => r with { Ttl = 0 }).ToArray();
        Feed(browser, BrowseProbe.Encode(goodbye));
        Assert.Single(browser.Snapshot()); // RFC 6762 10.1: expires in one second
        Assert.Empty(Drain(browser));

        clock.Advance(TimeSpan.FromMilliseconds(1100));
        browser.Sweep();
        Assert.Empty(browser.Snapshot());
        Assert.IsType<ReceiverEvent.Lost>(Assert.Single(Drain(browser)));
    }

    [Fact]
    public async Task Late_answer_after_a_goodbye_on_another_interface_does_not_flap()
    {
        // A responder with several interfaces/families: goodbye on one, a stale answer on another, then the other goodbye.
        var (browser, clock) = Create();
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        var answer = BrowseProbe.Response(speakerA);
        var goodbye = BrowseProbe.Encode(BrowseProbe.Records(speakerA, includeAddresses: false).Select(r => r with { Ttl = 0 }).ToArray());
        Feed(browser, answer);
        Drain(browser);

        clock.Advance(TimeSpan.FromSeconds(5));
        Feed(browser, goodbye);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Feed(browser, answer);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Feed(browser, goodbye);
        browser.Sweep();
        Assert.Empty(Drain(browser));

        clock.Advance(TimeSpan.FromSeconds(1));
        browser.Sweep();
        Assert.IsType<ReceiverEvent.Lost>(Assert.Single(Drain(browser)));
        Assert.Empty(browser.Snapshot());
    }

    [Fact]
    public async Task Changed_gid_with_cache_flush_raises_updated_and_the_old_txt_goes_away()
    {
        var (browser, clock) = Create();
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        Feed(browser, BrowseProbe.Response(speakerA));
        Drain(browser);

        // a group SETUP changes gid (SPEC-control.md 1.2); the receiver re-announces its TXT with the cache-flush bit
        clock.Advance(TimeSpan.FromSeconds(30));
        const string group = "6B1F8A1E-2C3D-4E5F-8091-A2B3C4D5E6F7";
        var txt = speakerA.Txt.Select(t => t.StartsWith("gid=", StringComparison.Ordinal) ? "gid=" + group : t).ToList();
        Feed(browser, BrowseProbe.Response(speakerA, txt: txt));
        var updated = Assert.IsType<ReceiverEvent.Updated>(Assert.Single(Drain(browser)));
        Assert.Equal(group, updated.Receiver.Airplay.GroupId);
        Assert.Equal(BrowseProbe.SpeakerAId, updated.Previous.Airplay.GroupId);

        // the superseded TXT expires a second later (RFC 6762 10.2) without another event
        clock.Advance(TimeSpan.FromSeconds(2));
        browser.Sweep();
        Assert.Empty(Drain(browser));
        Assert.Equal(group, Assert.Single(browser.Snapshot()).Airplay.GroupId);
    }

    [Fact]
    public async Task Identical_answer_raises_no_event()
    {
        var (browser, clock) = Create();
        await using var _ = browser;
        var packet = BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay]);
        Feed(browser, packet);
        clock.Advance(TimeSpan.FromSeconds(30));
        Feed(browser, packet);
        Assert.Single(Drain(browser));
        Assert.Equal(clock.Now, Assert.Single(browser.Snapshot()).LastSeen);
    }

    [Fact]
    public async Task Ptr_only_answer_asks_for_srv_and_txt_with_rate_limit()
    {
        var (browser, clock) = Create();
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        var ptr = BrowseProbe.Records(speakerA).First();
        var questions = Feed(browser, BrowseProbe.Encode(ptr));
        var instance = BrowseProbe.InstanceName(BrowseProbe.SpeakerAAirplay);
        Assert.Equal(2, questions.Count);
        Assert.Contains(questions, q => q.Type == DnsType.Srv && q.Name.Equals(instance));
        Assert.Contains(questions, q => q.Type == DnsType.Txt && q.Name.Equals(instance));

        // within a second: no repeat
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Empty(browser.Sweep());

        // SRV + TXT without addresses: ask for A and AAAA of the target
        clock.Advance(TimeSpan.FromSeconds(1));
        var srvTxt = BrowseProbe.Records(speakerA, includeAddresses: false).Skip(1).ToArray();
        var follow = Feed(browser, BrowseProbe.Encode(srvTxt));
        Assert.Equal(new[] { DnsType.A, DnsType.Aaaa }, follow.Select(q => q.Type).Order().ToArray());
        Assert.Empty(browser.Snapshot());

        // at most FollowUpAttempts per name and type between two periodic browses
        var asked = 1;
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            asked += browser.Sweep().Count(q => q.Type == DnsType.A);
        }

        Assert.Equal(ReceiverCache.FollowUpAttempts, asked);
    }

    [Fact]
    public async Task Address_records_before_the_srv_in_one_packet_are_used()
    {
        var (browser, _) = Create();
        await using var _ = browser;
        var records = BrowseProbe.Records(BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay]).Reverse().ToArray();
        Feed(browser, BrowseProbe.Encode(records));
        Assert.Single(browser.Snapshot());
    }

    [Fact]
    public async Task Unrelated_hosts_and_services_are_not_cached()
    {
        var (browser, _) = Create();
        await using var _ = browser;
        var other = new DnsRecord(DnsName.Parse("printer.local"), DnsType.A, DnsClass.In, true, 120, new DnsRecordData.A(IPAddress.Parse("198.51.100.9")));
        var spotify = new DnsRecord(DnsName.Parse("_spotify-connect._tcp.local"), DnsType.Ptr, DnsClass.In, false, 4500, new DnsRecordData.Ptr(DnsName.Parse("x._spotify-connect._tcp.local")));
        Assert.Empty(Feed(browser, BrowseProbe.Encode(other, spotify)));
        Assert.Empty(browser.Snapshot());
        Assert.Empty(Drain(browser));
    }

    [Fact]
    public async Task Legacy_unicast_ttl_of_10_seconds_is_raised_to_two_query_intervals()
    {
        var options = new ReceiverBrowserOptions { QueryInterval = TimeSpan.FromSeconds(30), ExpiryGrace = TimeSpan.Zero };
        var (browser, clock) = Create(options);
        await using var _ = browser;
        Feed(browser, BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay], ttlOverride: 10), legacy: true);
        clock.Advance(TimeSpan.FromSeconds(45));
        browser.Sweep();
        Assert.Single(browser.Snapshot());
        clock.Advance(TimeSpan.FromSeconds(16)); // 61 s > 2 x 30 s
        browser.Sweep();
        Assert.Empty(browser.Snapshot());

        // the same TTL over multicast is honoured as is
        var (browser2, clock2) = Create(options);
        await using var __ = browser2;
        Feed(browser2, BrowseProbe.Response(BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay], ttlOverride: 10), legacy: false);
        clock2.Advance(TimeSpan.FromSeconds(11));
        browser2.Sweep();
        Assert.Empty(browser2.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_unicast_answer_never_shortens_a_multicast_ttl(bool legacyFirst)
    {
        // The browser sends every query from 5353 and from the ephemeral queriers, so the same records arrive with the
        // multicast TTL and with the legacy cap of 10 s, in either order. The legacy packet keeps the cache-flush bits of the
        // capture (a non-compliant responder) to show that they do not flush the multicast records either.
        var options = new ReceiverBrowserOptions { QueryInterval = TimeSpan.FromSeconds(30), ExpiryGrace = TimeSpan.Zero };
        var (browser, clock) = Create(options);
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        var multicast = BrowseProbe.Response(speakerA, ttlOverride: 4500);
        var legacy = BrowseProbe.Response(speakerA, ttlOverride: 10);
        Feed(browser, legacyFirst ? legacy : multicast, legacy: legacyFirst);
        Feed(browser, legacyFirst ? multicast : legacy, legacy: !legacyFirst);
        Assert.Single(browser.Snapshot());

        // no refresh question before 80 % of 4500 s, although the legacy floor (60 s) passes many times
        for (var t = 10; t < 3600; t += 10)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Empty(browser.Sweep());
            if (t % 600 == 0)
            {
                // the periodic browse keeps getting both answers
                Feed(browser, legacy, legacy: true);
            }
        }

        Assert.Single(browser.Snapshot());
        clock.Advance(TimeSpan.FromSeconds(100)); // t = 3690 s, 82 %: past 80 % + jitter
        Assert.Contains(browser.Sweep(), q => q.Type == DnsType.Srv);
    }

    [Fact]
    public async Task Link_local_address_heard_on_two_interfaces_is_listed_once_per_scope()
    {
        var (browser, clock) = Create();
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        var packet = BrowseProbe.Response(speakerA);
        browser.Ingest(packet, BrowseProbe.Reflector, 7, legacyUnicast: false);
        clock.Advance(TimeSpan.FromSeconds(1));
        browser.Ingest(packet, BrowseProbe.Reflector, 12, legacyUnicast: false);
        var first = Assert.Single(browser.Snapshot()).Addresses;
        Assert.Equal(new long[] { 7, 12 }, first.Where(a => a.IsIPv6LinkLocal).Select(a => a.ScopeId).ToArray());

        // re-arrival on the first interface changes nothing: no scope flip, no Updated event
        Drain(browser);
        clock.Advance(TimeSpan.FromSeconds(1));
        browser.Ingest(packet, BrowseProbe.Reflector, 7, legacyUnicast: false);
        Assert.Equal(first, Assert.Single(browser.Snapshot()).Addresses);
        Assert.Empty(Drain(browser));
    }

    [Fact]
    public async Task Queries_and_garbage_are_ignored()
    {
        var (browser, _) = Create();
        await using var _ = browser;
        Assert.Empty(Feed(browser, MdnsReceiverBrowser.BrowseQuery().Encode()));
        Assert.Empty(Feed(browser, [0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0xC0]));
        Assert.Empty(Feed(browser, [1, 2, 3]));
        Assert.Empty(browser.Snapshot());
    }

    [Fact]
    public void Browse_query_asks_ptr_for_both_types()
    {
        var parsed = DnsMessage.Parse(MdnsReceiverBrowser.BrowseQuery().Encode());
        Assert.False(parsed.IsResponse);
        Assert.Equal(
            new[] { "_airplay._tcp.local", "_raop._tcp.local" },
            parsed.Questions.Select(q => q.Name.ToString()).ToArray());
        Assert.All(parsed.Questions, q => Assert.Equal(DnsType.Ptr, q.Type));
    }

    [Fact]
    public async Task Find_matches_id_with_or_without_colons_and_the_name()
    {
        var (browser, _) = Create();
        await using var _ = browser;
        foreach (var instance in BrowseProbe.Instances.Values)
        {
            Feed(browser, BrowseProbe.Response(instance));
        }

        var snapshot = browser.Snapshot();
        Assert.Equal(BrowseProbe.SpeakerAId, MdnsReceiverBrowser.Match(snapshot, "02:1a:2b:3c:4d:5e")?.Id);
        Assert.Equal(BrowseProbe.SpeakerAId, MdnsReceiverBrowser.Match(snapshot, "021A2B3C4D5E")?.Id);
        Assert.Equal(BrowseProbe.SpeakerAId, MdnsReceiverBrowser.Match(snapshot, "test speaker abcd")?.Id);
        Assert.Null(MdnsReceiverBrowser.Match(snapshot, "Kitchen"));
    }

    [Fact]
    public async Task Renamed_instance_with_the_same_deviceid_reports_one_receiver()
    {
        var (browser, clock) = Create();
        await using var _ = browser;
        var speakerA = BrowseProbe.Instances[BrowseProbe.SpeakerAAirplay];
        Feed(browser, BrowseProbe.Response(speakerA));
        clock.Advance(TimeSpan.FromSeconds(3));
        var renamed = speakerA with { Name = "Living Room._airplay._tcp.local" };
        Feed(browser, BrowseProbe.Response(renamed));
        var receiver = Assert.Single(browser.Snapshot());
        Assert.Equal("Living Room", receiver.Name);
        var events = Drain(browser);
        Assert.IsType<ReceiverEvent.Found>(events[0]);
        Assert.Equal("Test Speaker ABCD", Assert.IsType<ReceiverEvent.Updated>(events[1]).Previous.Name);
    }

    [Fact]
    public void Instance_names_must_be_one_label_under_the_type()
    {
        Assert.True(ReceiverCache.IsInstanceOf(BrowseProbe.InstanceName("A.B._airplay._tcp.local"), ReceiverCache.AirplayType));
        Assert.False(ReceiverCache.IsInstanceOf(DnsName.Parse("_airplay._tcp.local"), ReceiverCache.AirplayType));
        Assert.False(ReceiverCache.IsInstanceOf(DnsName.Parse("a.b._airplay._tcp.local"), ReceiverCache.AirplayType));
        Assert.False(ReceiverCache.IsInstanceOf(DnsName.Parse("a._raop._tcp.local"), ReceiverCache.AirplayType));
    }
}
