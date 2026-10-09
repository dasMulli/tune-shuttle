using System.Net;
using System.Text.Json;
using Librespot.Discovery.Mdns;

namespace Airplay2.Sender.Tests.Discovery;

/// <summary>
/// The LAN browse captured on 2026-10-09 (<c>docs/airplay2/probes/browse.json</c>), re-encoded as mDNS responses with the
/// librespot DNS encoder, in the shape the reflector at 203.0.113.1 sent them (answers with the cache-flush bit on the
/// unique records; TTLs 4500 for PTR/TXT and 120 for SRV/A/AAAA, as observed live the same day).
/// </summary>
internal static class BrowseProbe
{
    /// <summary>The reflector every answer came from (SPEC-control.md 1.1).</summary>
    public static readonly IPEndPoint Reflector = new(IPAddress.Parse("203.0.113.1"), 5353);

    public const string SpeakerAAirplay = "Test Speaker ABCD._airplay._tcp.local";
    public const string SpeakerARaop = "021A2B3C4D5E@Test Speaker ABCD._raop._tcp.local";
    public const string SpeakerAHost = "speakera-021a2b3c4d5d.local";
    public const string SpeakerAId = "02:1A:2B:3C:4D:5E";
    public const string LgId = "02:4C:47:00:00:01";

    private static readonly Lazy<IReadOnlyDictionary<string, Instance>> Loaded = new(Load);

    /// <summary>The instances keyed by their full name.</summary>
    public static IReadOnlyDictionary<string, Instance> Instances => Loaded.Value;

    /// <summary>One response carrying PTR, SRV, TXT and the addresses of <paramref name="instance"/>.</summary>
    public static byte[] Response(Instance instance, bool includeAddresses = true, uint? ttlOverride = null, IEnumerable<string>? txt = null)
    {
        var message = DnsMessage.NewResponse(0);
        foreach (var record in Records(instance, includeAddresses, ttlOverride, txt))
        {
            message.Answers.Add(record);
        }

        return message.Encode();
    }

    /// <summary>The records of one instance.</summary>
    public static IEnumerable<DnsRecord> Records(Instance instance, bool includeAddresses = true, uint? ttlOverride = null, IEnumerable<string>? txt = null)
    {
        var name = InstanceName(instance.Name);
        var target = DnsName.Parse(instance.Target);
        yield return new DnsRecord(DnsName.Parse(instance.Type), DnsType.Ptr, DnsClass.In, false, ttlOverride ?? 4500, new DnsRecordData.Ptr(name));
        yield return new DnsRecord(name, DnsType.Srv, DnsClass.In, true, ttlOverride ?? 120, new DnsRecordData.Srv(0, 0, instance.Port, target));
        yield return new DnsRecord(name, DnsType.Txt, DnsClass.In, true, ttlOverride ?? 4500, DnsRecordData.Txt.FromStrings(txt ?? instance.Txt));
        if (includeAddresses)
        {
            foreach (var address in instance.Addresses.Distinct())
            {
                DnsRecordData data = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? new DnsRecordData.A(address) : new DnsRecordData.Aaaa(address);
                yield return new DnsRecord(target, data is DnsRecordData.A ? DnsType.A : DnsType.Aaaa, DnsClass.In, true, ttlOverride ?? 120, data);
            }
        }
    }

    /// <summary>Encodes records as one response.</summary>
    public static byte[] Encode(params DnsRecord[] records)
    {
        var message = DnsMessage.NewResponse(0);
        message.Answers.AddRange(records);
        return message.Encode();
    }

    /// <summary>The instance name as a DNS name: the first label is the instance (it may contain dots and spaces).</summary>
    public static DnsName InstanceName(string full)
    {
        foreach (var type in new[] { "._airplay._tcp.local", "._raop._tcp.local" })
        {
            if (full.EndsWith(type, StringComparison.Ordinal))
            {
                return DnsName.Parse(type[1..]).Prepend(full[..^type.Length]);
            }
        }

        throw new ArgumentException(full);
    }

    private static Dictionary<string, Instance> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LocateProbe()));
        var result = new Dictionary<string, Instance>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var value = property.Value;
            var srv = value.GetProperty("srv");
            result[property.Name] = new Instance(
                property.Name,
                value.GetProperty("type").GetString()!,
                (ushort)srv.GetProperty("port").GetInt32(),
                srv.GetProperty("target").GetString()!,
                [.. value.GetProperty("txt").EnumerateArray().Select(e => e.GetString()!)],
                [.. value.GetProperty("addresses").EnumerateArray().Select(e => IPAddress.Parse(e.GetString()!))]);
        }

        return result;
    }

    private static string LocateProbe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "airplay2", "probes", "browse.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("docs/airplay2/probes/browse.json not found above " + AppContext.BaseDirectory);
    }

    /// <summary>One captured service instance.</summary>
    public sealed record Instance(string Name, string Type, ushort Port, string Target, IReadOnlyList<string> Txt, IReadOnlyList<IPAddress> Addresses);
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
