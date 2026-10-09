// Tests for src/Librespot.Discovery/Mdns/MdnsResponder.cs (replaces libmdns; includes libmdns' fsm.rs `mod tests`).
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Librespot.Discovery;
using Librespot.Discovery.Mdns;
using Xunit;

namespace Librespot.Tests.Discovery;

public class MdnsResponderTests
{
    private static readonly DnsName Hostname = DnsName.Parse("test-hostname.local");
    private static readonly IPAddress[] Addresses = [IPAddress.Parse("198.51.100.20")];

    // libmdns fsm.rs `test_service_type_enumeration`.
    [Fact]
    public void test_service_type_enumeration()
    {
        var question = new DnsQuestion(DnsName.Parse("_services._dns-sd._udp.local"), DnsType.Ptr, DnsClass.In, false);
        var serviceData = new MdnsService(DnsName.Parse("test-instance"), DnsName.Parse("_test-service-name._tcp"), 8008, new DnsRecordData.Txt([]));

        var builder = DnsMessage.NewResponse(0);
        MdnsAnswers.HandleQuestion(question, Hostname, [serviceData], GetAddresses, builder);

        var parsed = DnsMessage.Parse(builder.Encode());
        var answer = Assert.Single(parsed.Answers);
        Assert.Equal(DnsName.Parse("_services._dns-sd._udp.local"), answer.Name);
        Assert.Equal(DnsClass.In, answer.Class);
        Assert.Equal(60u, answer.Ttl);
        var ptr = Assert.IsType<DnsRecordData.Ptr>(answer.Data);
        Assert.Equal(DnsName.Parse("_test-service-name._tcp"), ptr.Target);
    }

    [Fact]
    public void Ptr_query_is_answered_with_ptr_srv_txt_and_addresses_by_multicast()
    {
        var svc = SpotifyService();
        var query = Query(new DnsQuestion(DnsName.Parse("_spotify-connect._tcp.local"), DnsType.Ptr), id: 7);

        var (multicast, unicast) = MdnsAnswers.HandlePacket(query, new IPEndPoint(IPAddress.Loopback, 5353), Hostname, [svc], GetAddresses);

        Assert.Null(unicast);
        Assert.NotNull(multicast);
        Assert.Equal(7, multicast.Id);
        Assert.Equal(DnsMessage.FlagResponse | DnsMessage.FlagAuthoritative, multicast.Flags);
        Assert.Collection(
            multicast.Answers,
            r => Assert.Equal(new DnsRecordData.Ptr(svc.Name), r.Data),
            r => Assert.Equal(new DnsRecordData.Srv(0, 0, 4070, Hostname), r.Data),
            r => Assert.Equal(["VERSION=1.0", "CPath=/"], Assert.IsType<DnsRecordData.Txt>(r.Data).Strings()),
            r =>
            {
                Assert.Equal(Hostname, r.Name);
                Assert.Equal(new DnsRecordData.A(Addresses[0]), r.Data);
            });
        Assert.All(multicast.Answers, r => Assert.Equal(MdnsAnswers.DefaultTtl, r.Ttl));
        Assert.Equal(DnsName.Parse("Kitchen._spotify-connect._tcp.local"), svc.Name);
    }

    [Fact]
    public void Qu_bit_moves_the_answer_to_the_unicast_reply()
    {
        var query = Query(new DnsQuestion(DnsName.Parse("_spotify-connect._tcp.local"), DnsType.Ptr, DnsClass.In, UnicastResponse: true));
        var (multicast, unicast) = MdnsAnswers.HandlePacket(query, new IPEndPoint(IPAddress.Loopback, 5353), Hostname, [SpotifyService()], GetAddresses);
        Assert.Null(multicast);
        Assert.Equal(4, unicast!.Answers.Count);
    }

    [Fact]
    public void Srv_txt_a_and_any_queries()
    {
        var svc = SpotifyService();
        var from = new IPEndPoint(IPAddress.Loopback, 5353);

        var srv = MdnsAnswers.HandlePacket(Query(new DnsQuestion(svc.Name, DnsType.Srv)), from, Hostname, [svc], GetAddresses).Multicast!;
        Assert.Equal([DnsType.Srv, DnsType.A], srv.Answers.Select(a => a.Type));

        var txt = MdnsAnswers.HandlePacket(Query(new DnsQuestion(svc.Name, DnsType.Txt)), from, Hostname, [svc], GetAddresses).Multicast!;
        Assert.Equal([DnsType.Txt], txt.Answers.Select(a => a.Type));

        var a = MdnsAnswers.HandlePacket(Query(new DnsQuestion(DnsName.Parse("TEST-HOSTNAME.local"), DnsType.A)), from, Hostname, [svc], GetAddresses).Multicast!;
        Assert.Equal([DnsType.A], a.Answers.Select(r => r.Type));

        var any = MdnsAnswers.HandlePacket(Query(new DnsQuestion(svc.Type, DnsType.Any)), from, Hostname, [svc], GetAddresses).Multicast!;
        Assert.Equal([DnsType.Ptr, DnsType.Srv, DnsType.Txt, DnsType.A], any.Answers.Select(r => r.Type));

        // Unknown names, other classes and other types get no answer.
        AssertNoReply(MdnsAnswers.HandlePacket(Query(new DnsQuestion(DnsName.Parse("_other._tcp.local"), DnsType.Ptr)), from, Hostname, [svc], GetAddresses));
        AssertNoReply(MdnsAnswers.HandlePacket(Query(new DnsQuestion(svc.Type, DnsType.Ptr, Class: 3)), from, Hostname, [svc], GetAddresses));
        AssertNoReply(MdnsAnswers.HandlePacket(Query(new DnsQuestion(svc.Name, (DnsType)47)), from, Hostname, [svc], GetAddresses));
    }

    [Fact]
    public void Responses_and_truncated_queries_are_ignored()
    {
        var from = new IPEndPoint(IPAddress.Loopback, 5353);
        var response = Query(new DnsQuestion(DnsName.Parse("_spotify-connect._tcp.local"), DnsType.Ptr));
        response.Flags = DnsMessage.FlagResponse;
        AssertNoReply(MdnsAnswers.HandlePacket(response, from, Hostname, [SpotifyService()], GetAddresses));

        var truncated = Query(new DnsQuestion(DnsName.Parse("_spotify-connect._tcp.local"), DnsType.Ptr));
        truncated.Flags = DnsMessage.FlagTruncated;
        AssertNoReply(MdnsAnswers.HandlePacket(truncated, from, Hostname, [SpotifyService()], GetAddresses));
    }

    [Fact]
    public void Announcement_and_goodbye()
    {
        var svc = SpotifyService();

        var hello = MdnsAnswers.BuildUnsolicited(svc, Hostname, 60, GetAddresses);
        Assert.Equal(0, hello.Id);
        Assert.Equal([DnsType.Ptr, DnsType.Srv, DnsType.Txt, DnsType.A], hello.Answers.Select(a => a.Type));

        var goodbye = MdnsAnswers.BuildUnsolicited(svc, Hostname, 0, null);
        Assert.Equal([DnsType.Ptr, DnsType.Srv, DnsType.Txt], goodbye.Answers.Select(a => a.Type));
        Assert.All(goodbye.Answers, a => Assert.Equal(0u, a.Ttl));
    }

    [Fact]
    public void Hostname_gets_local_suffix()
    {
        Assert.Equal(DnsName.Parse("box.local"), new MdnsServices("box").Hostname);
        Assert.Equal(DnsName.Parse("box.local"), new MdnsServices("box.local").Hostname);
    }

    /// <summary>
    /// Live: binds port 5353, registers a service and queries it over multicast loopback.
    /// Set LIBRESPOT_LIVE_TESTS=1 to run (needs a multicast-capable interface).
    /// </summary>
    [Fact]
    public async Task Live_responder_answers_multicast_query()
    {
        if (Environment.GetEnvironmentVariable("LIBRESPOT_LIVE_TESTS") != "1")
        {
            return;
        }

        var instance = "LibrespotTest" + Environment.ProcessId;
        await using var responder = MdnsResponder.Spawn([], hostname: "librespot-test");
        await using var registration = await responder.RegisterAsync(DnsSdBackends.DnsSdServiceName, instance, 4321, DnsSdBackends.TxtRecord);

        var query = Query(new DnsQuestion(DnsName.Parse("_spotify-connect._tcp.local"), DnsType.Ptr, DnsClass.In, UnicastResponse: true));

        // Send through every up IPv4 interface explicitly (the OS default multicast interface may be one the responder
        // does not listen on, e.g. a down adapter's link-local address); one answer is enough.
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

        foreach (var local in localAddresses)
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(local, 0));
            client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            await client.SendToAsync(query.Encode(), SocketFlags.None, new IPEndPoint(MdnsResponder.GroupV4, MdnsResponder.MdnsPort));

            var buffer = new byte[9000];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                while (true)
                {
                    var result = await client.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                    if (!DnsMessage.TryParse(buffer.AsSpan(0, result.ReceivedBytes), out var reply, out _))
                    {
                        continue;
                    }

                    var srv = reply.Answers.FirstOrDefault(a => a.Data is DnsRecordData.Srv && a.Name.Labels[0] == instance);
                    if (srv is not null)
                    {
                        Assert.Equal(4321, ((DnsRecordData.Srv)srv.Data).Port);
                        Assert.Equal(DnsName.Parse("librespot-test.local"), ((DnsRecordData.Srv)srv.Data).Target);
                        Assert.Contains(reply.Answers, a => a.Data is DnsRecordData.A x && x.Address.Equals(local));
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // try the next interface
            }
        }

        Assert.Fail("no mDNS answer on any interface");
    }

    [Fact]
    public void Addresses_are_enumerated_only_when_an_address_record_is_added()
    {
        var svc = SpotifyService();
        var from = new IPEndPoint(IPAddress.Loopback, 5353);
        var calls = 0;
        IReadOnlyList<IPAddress> Counting()
        {
            calls++;
            return Addresses;
        }

        // Irrelevant questions, responses and TXT queries never touch the interface list (libmdns: only add_ip_rr does).
        AssertNoReply(MdnsAnswers.HandlePacket(Query(new DnsQuestion(DnsName.Parse("_other._tcp.local"), DnsType.Ptr)), from, Hostname, [svc], Counting));
        MdnsAnswers.HandlePacket(Query(new DnsQuestion(svc.Name, DnsType.Txt)), from, Hostname, [svc], Counting);
        Assert.Equal(0, calls);

        // Several address-producing questions in one packet enumerate once.
        var query = Query(new DnsQuestion(svc.Type, DnsType.Ptr));
        query.Questions.Add(new DnsQuestion(svc.Name, DnsType.Srv));
        query.Questions.Add(new DnsQuestion(Hostname, DnsType.A, DnsClass.In, UnicastResponse: true));
        MdnsAnswers.HandlePacket(query, from, Hostname, [svc], Counting);
        Assert.Equal(1, calls);
    }

    private static IReadOnlyList<IPAddress> GetAddresses() => Addresses;

    private static void AssertNoReply((DnsMessage? Multicast, DnsMessage? Unicast) reply)
    {
        Assert.Null(reply.Multicast);
        Assert.Null(reply.Unicast);
    }

    private static MdnsService SpotifyService() =>
        MdnsService.Create(DnsSdBackends.DnsSdServiceName, "Kitchen", 4070, DnsSdBackends.TxtRecord);

    private static DnsMessage Query(DnsQuestion question, ushort id = 0)
    {
        var message = new DnsMessage { Id = id };
        message.Questions.Add(question);
        return message;
    }
}
