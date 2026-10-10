using System.Net;
using System.Net.Sockets;
using Librespot.Discovery.Mdns;
using Microsoft.Extensions.Logging;

namespace Airplay2.Sender.Discovery;

/// <summary>The outcome of feeding the cache: events to publish and follow-up questions to send.</summary>
/// <param name="Events">Found/Updated/Lost events in the order they were detected.</param>
/// <param name="Questions">Follow-up questions (SRV/TXT for a known instance, A/AAAA for an SRV target, TTL refresh).</param>
/// <param name="Changed">Whether the published receiver set (or a receiver's <c>LastSeen</c>) changed.</param>
internal sealed record CacheUpdate(IReadOnlyList<ReceiverEvent> Events, IReadOnlyList<DnsQuestion> Questions, bool Changed)
{
    public static readonly CacheUpdate None = new([], [], false);
}

/// <summary>
/// The record cache of the receiver browser (SPEC-control.md 1.1), free of sockets and clocks so it can be driven by
/// tests: it keeps the PTR/SRV/TXT/A/AAAA records that belong to <c>_airplay._tcp</c> and <c>_raop._tcp</c> instances,
/// applies the RFC 6762 cache rules (TTL, goodbye with TTL 0, cache-flush bit), merges the two services of one device
/// and diffs the result into <see cref="ReceiverEvent"/>s. Not thread-safe: the browser serialises access.
/// </summary>
/// <remarks>
/// Rules and their sources:
/// <list type="bullet">
/// <item>Addresses come only from A/AAAA records of the SRV target; the UDP source is never used, because answers may
/// come from a reflector (SPEC-control.md 1.1: in the captured browse every answer came from the reflector).</item>
/// <item>A record with TTL 0 is a goodbye (RFC 6762 section 10.1): it expires one second later and the grace period
/// does not apply, so <see cref="ReceiverEvent.Lost"/> follows on the next sweep.</item>
/// <item>A record with the cache-flush bit replaces records of the same name and type that are older than one second;
/// those expire one second later (RFC 6762 section 10.2). The bit is ignored on legacy unicast answers, which must not
/// carry it (RFC 6762 section 6.7).</item>
/// <item>Answers to a legacy unicast query (sent from an ephemeral port) carry TTLs capped at 10 seconds
/// (RFC 6762 section 6.7; observed live on 2026-10-09: the reflector answers our ephemeral-port query with TTL 10, the
/// same records multicast with 120/4500). Such records get <see cref="LegacyTtlFloor"/> as TTL so they survive until the
/// next periodic query instead of flapping between Lost and Found. A legacy answer never shortens a record already
/// cached for longer (from a multicast answer): the browser sends every query both ways, so otherwise the cached TTL
/// would depend on which answer arrived last and settle at the floor.</item>
/// <item>Records still alive at 80 %, 85 %, 90 % and 95 % of their TTL, plus a per-record random 0-2 %, are re-queried
/// (RFC 6762 section 5.2).</item>
/// <item>An IPv6 link-local AAAA record is cached per arrival interface, so each scoped address keeps its scope id
/// (RFC 6762 section 10.2 notes that a multi-homed host sees the same records on several links).</item>
/// <item>The <c>_raop._tcp</c> instance <c>&lt;deviceid without colons&gt;@&lt;name&gt;</c> is merged into the
/// <c>_airplay._tcp</c> receiver with the same <c>deviceid</c> (pyatv splits the RAOP name on '@'
/// [pyatv:pyatv/protocols/raop/__init__.py:438-441]); without a <c>deviceid</c>, by the name after the '@'.</item>
/// </list>
/// </remarks>
internal sealed class ReceiverCache
{
    /// <summary>The <c>_airplay._tcp.local</c> service type name.</summary>
    public static readonly DnsName AirplayType = DnsName.Parse(AirplayServiceTypes.Airplay);

    /// <summary>The <c>_raop._tcp.local</c> service type name.</summary>
    public static readonly DnsName RaopType = DnsName.Parse(AirplayServiceTypes.Raop);

    /// <summary>Minimum spacing of two follow-up questions for the same name and type.</summary>
    public static readonly TimeSpan FollowUpSpacing = TimeSpan.FromSeconds(1);

    /// <summary>Follow-up questions per name and type between two periodic browses (<see cref="ResetFollowUps"/>).</summary>
    public const int FollowUpAttempts = 3;

    // RFC 6762 5.2: refresh queries at 80, 85, 90 and 95 % of the TTL, each with a random variation of up to 2 %.
    private const double RefreshJitter = 0.02;

    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(1);
    private static readonly double[] RefreshPoints = [0.80, 0.85, 0.90, 0.95];

    private readonly Dictionary<(DnsName Name, DnsType Type), List<Entry>> _records = [];
    private readonly Dictionary<(DnsName Name, DnsType Type), (DateTimeOffset Last, int Count)> _asked = [];
    private readonly ILogger _logger;
    private Dictionary<string, DiscoveredReceiver> _published = new(StringComparer.OrdinalIgnoreCase);

    // Whether the last rebuild left an instance incomplete (it called Ask): the sweep then has to rebuild to retry the
    // follow-up questions even when nothing expired.
    private bool _pending;

    /// <summary>Creates a cache.</summary>
    /// <param name="keepIpv6">Whether AAAA records are kept (and asked for).</param>
    /// <param name="expiryGrace">Grace period after a record's TTL before it is dropped (not applied to goodbyes and flushes).</param>
    /// <param name="legacyTtlFloor">Minimum TTL of records learned from a legacy unicast answer.</param>
    /// <param name="logger">Logger for wire-level detail.</param>
    public ReceiverCache(bool keepIpv6, TimeSpan expiryGrace, TimeSpan legacyTtlFloor, ILogger logger)
    {
        KeepIpv6 = keepIpv6;
        ExpiryGrace = expiryGrace < TimeSpan.Zero ? TimeSpan.Zero : expiryGrace;
        LegacyTtlFloor = legacyTtlFloor;
        _logger = logger;
    }

    /// <summary>Whether AAAA records are kept.</summary>
    public bool KeepIpv6 { get; }

    /// <summary>Grace period after a record's TTL.</summary>
    public TimeSpan ExpiryGrace { get; }

    /// <summary>Minimum TTL of records learned from a legacy unicast answer.</summary>
    public TimeSpan LegacyTtlFloor { get; }

    /// <summary>The receivers currently resolved.</summary>
    public IReadOnlyList<DiscoveredReceiver> Current => [.. _published.Values];

    /// <summary>
    /// Feeds one received message. Queries and non-standard opcodes are ignored; answers and additionals of a response
    /// are cached when they belong to a receiver.
    /// </summary>
    /// <param name="message">The parsed packet.</param>
    /// <param name="interfaceIndex">Index of the interface it arrived on (scope of IPv6 link-local addresses), or 0.</param>
    /// <param name="legacyUnicast">True when it arrived on an ephemeral-port socket (a legacy unicast answer, TTL capped at 10 s).</param>
    /// <param name="now">The current time.</param>
    public CacheUpdate Apply(DnsMessage message, int interfaceIndex, bool legacyUnicast, DateTimeOffset now)
    {
        if (!message.IsResponse || message.Opcode != 0)
        {
            return CacheUpdate.None;
        }

        var records = message.Answers.Concat(message.Additionals).ToList();
        var touched = false;

        // Pass 1: service records. PTR for the two types, SRV/TXT of their instances.
        foreach (var record in records)
        {
            if (record.Class != DnsClass.In)
            {
                continue;
            }

            var relevant = record.Type switch
            {
                DnsType.Ptr => record.Name.Equals(AirplayType) || record.Name.Equals(RaopType),
                DnsType.Srv or DnsType.Txt => IsInstanceOf(record.Name, AirplayType) || IsInstanceOf(record.Name, RaopType),
                _ => false,
            };

            if (relevant && (record.Type != DnsType.Ptr || record.Data is DnsRecordData.Ptr { Target: var t } && (IsInstanceOf(t, AirplayType) || IsInstanceOf(t, RaopType))))
            {
                touched |= Insert(record, interfaceIndex, legacyUnicast, now);
            }
        }

        // Pass 2: addresses, only for host names an SRV record points at (the cache would otherwise collect every host
        // on the LAN that announces itself).
        var targets = SrvTargets();
        foreach (var record in records)
        {
            if (record.Class != DnsClass.In || !targets.Contains(record.Name))
            {
                continue;
            }

            if (record.Type == DnsType.A || (record.Type == DnsType.Aaaa && KeepIpv6))
            {
                touched |= Insert(record, interfaceIndex, legacyUnicast, now);
            }
        }

        return touched ? Rebuild(now, includeRefresh: false) : CacheUpdate.None;
    }

    /// <summary>Drops expired records, re-asks for records near the end of their TTL and diffs the receiver set.</summary>
    public CacheUpdate Sweep(DateTimeOffset now)
    {
        var removed = false;
        foreach (var (key, list) in _records.ToList())
        {
            var count = list.RemoveAll(e => now >= e.RemoveAt(ExpiryGrace));
            if (count > 0)
            {
                removed = true;
                _logger.LogDebug("mDNS cache: {Count} {Type} record(s) of {Name} expired", count, key.Type, key.Name);
            }

            if (list.Count == 0)
            {
                _records.Remove(key);
            }
        }

        if (!removed && !_pending)
        {
            // Nothing expired and every instance is complete: the receiver set cannot have changed, only refreshes are due.
            var refresh = new List<DnsQuestion>();
            AddRefreshQuestions(refresh, now);
            return refresh.Count > 0 ? new CacheUpdate([], refresh, false) : CacheUpdate.None;
        }

        var update = Rebuild(now, includeRefresh: true);
        return removed || update.Events.Count > 0 || update.Questions.Count > 0 ? update : CacheUpdate.None;
    }

    /// <summary>Allows new follow-up questions for every name (called on each periodic browse).</summary>
    public void ResetFollowUps() => _asked.Clear();

    /// <summary>Whether <paramref name="name"/> is <c>&lt;one label&gt;.&lt;type&gt;</c>.</summary>
    internal static bool IsInstanceOf(DnsName name, DnsName type)
    {
        if (name.Labels.Count != type.Labels.Count + 1)
        {
            return false;
        }

        return new DnsName(name.Labels.Skip(1)).Equals(type);
    }

    private bool Insert(DnsRecord record, int interfaceIndex, bool legacyUnicast, DateTimeOffset now)
    {
        var key = (record.Name, record.Type);
        _records.TryGetValue(key, out var list);
        var scoped = IsLinkLocal(record);
        var existing = list?.FindIndex(e => e.Record.Data.Equals(record.Data) && (!scoped || e.InterfaceIndex == interfaceIndex)) ?? -1;

        if (record.Ttl == 0)
        {
            // Goodbye (RFC 6762 10.1): the record expires one second later, without grace. Not deleted at once because a
            // responder sends its goodbyes per interface and address family, and an answer it built just before
            // unregistering can arrive after the first goodbye (seen with librespot's responder on a multi-NIC host);
            // the delay absorbs that instead of reporting Lost, Found, Lost.
            if (list is not null && existing >= 0)
            {
                var entry = list[existing];
                if (!entry.Flushed || entry.Expires > now + FlushDelay)
                {
                    _logger.LogDebug("mDNS goodbye: {Type} {Name}", record.Type, record.Name);
                    entry.Flushed = true;
                    entry.Expires = now + FlushDelay;
                }
            }

            return false;
        }

        var ttl = TimeSpan.FromSeconds(record.Ttl);
        if (legacyUnicast && ttl < LegacyTtlFloor)
        {
            ttl = LegacyTtlFloor;
        }

        if (list is null)
        {
            list = [];
            _records[key] = list;
        }

        if (record.CacheFlush && !legacyUnicast)
        {
            // RFC 6762 10.2: records of this name and type received more than a second ago expire in one second. Not for a
            // legacy unicast answer: it must not carry the bit (RFC 6762 6.7), and a TTL-capped answer flushing records
            // learned by multicast would cut their lifetime to the legacy floor.
            foreach (var other in list)
            {
                if (!other.Record.Data.Equals(record.Data) && other.Received < now - FlushDelay && !other.Flushed)
                {
                    other.Flushed = true;
                    other.Expires = now + FlushDelay < other.Expires ? now + FlushDelay : other.Expires;
                }
            }
        }

        _asked.Remove(key);
        if (existing >= 0)
        {
            var entry = list[existing];
            if (legacyUnicast && !entry.Flushed && entry.Expires >= now + ttl)
            {
                // A legacy unicast answer (TTL capped at 10 s, RFC 6762 6.7) never shortens a record learned by
                // multicast with a longer TTL; the record is unchanged, so there is nothing to rebuild.
                return false;
            }

            entry.Record = record;
            entry.Received = now;
            entry.Ttl = ttl;
            entry.Expires = now + ttl;
            entry.Flushed = false;
            entry.RefreshStage = 0;
            entry.InterfaceIndex = interfaceIndex;
        }
        else
        {
            _logger.LogDebug("mDNS record: {Type} {Name} ttl {Ttl}s{Legacy}: {Data}", record.Type, record.Name, record.Ttl, legacyUnicast ? " (legacy unicast)" : string.Empty, record.Data);
            list.Add(new Entry(record, now, ttl, interfaceIndex, Random.Shared.NextDouble() * RefreshJitter));
        }

        return true;
    }

    private static bool IsLinkLocal(DnsRecord record) => record.Data is DnsRecordData.Aaaa { Address.IsIPv6LinkLocal: true };

    private HashSet<DnsName> SrvTargets()
    {
        var set = new HashSet<DnsName>();
        foreach (var (key, list) in _records)
        {
            if (key.Type == DnsType.Srv)
            {
                foreach (var e in list)
                {
                    set.Add(((DnsRecordData.Srv)e.Record.Data).Target);
                }
            }
        }

        return set;
    }

    private Entry? Newest(DnsName name, DnsType type)
    {
        if (!_records.TryGetValue((name, type), out var list) || list.Count == 0)
        {
            return null;
        }

        // A flushed record only lingers for a second; prefer the replacement.
        return list.OrderBy(e => e.Flushed).ThenByDescending(e => e.Received).First();
    }

    private IEnumerable<Entry> All(DnsName name, DnsType type) =>
        _records.TryGetValue((name, type), out var list) ? list : [];

    private CacheUpdate Rebuild(DateTimeOffset now, bool includeRefresh)
    {
        var questions = new List<DnsQuestion>();
        var built = new Dictionary<string, DiscoveredReceiver>(StringComparer.OrdinalIgnoreCase);
        _pending = false;

        var raopInstances = All(RaopType, DnsType.Ptr).Select(e => ((DnsRecordData.Ptr)e.Record.Data).Target).Distinct().ToList();
        var usedRaop = new HashSet<DnsName>();
        var instanceSeen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in All(AirplayType, DnsType.Ptr).Select(e => ((DnsRecordData.Ptr)e.Record.Data).Target).Distinct().ToList())
        {
            if (Resolve(instance, raopInstances, usedRaop, questions, now) is not (var receiver, var seen))
            {
                continue;
            }

            // Two instance names for one device (a rename whose goodbye we missed): keep the one whose own SRV/TXT records
            // are fresher (seen). The address records are shared by both, so they cannot decide.
            if (!instanceSeen.TryGetValue(receiver.Id, out var otherSeen) || otherSeen < seen)
            {
                built[receiver.Id] = receiver;
                instanceSeen[receiver.Id] = seen;
            }
        }

        // RAOP instances that belong to no resolved _airplay instance: ask for their TXT so the merge is ready when the
        // _airplay record arrives. AirPlay-1-only devices are not reported (docs/ARCHITECTURE.md, "Design decisions": no AirPlay 1 fallback).
        foreach (var raop in raopInstances.Where(r => !usedRaop.Contains(r)))
        {
            if (Newest(raop, DnsType.Txt) is null)
            {
                Ask(questions, raop, DnsType.Txt, now);
            }
        }

        if (includeRefresh)
        {
            AddRefreshQuestions(questions, now);
        }

        var events = new List<ReceiverEvent>();
        var changed = false;
        foreach (var (id, receiver) in built)
        {
            if (!_published.TryGetValue(id, out var previous))
            {
                events.Add(new ReceiverEvent.Found(receiver));
                changed = true;
            }
            else if (!SameContent(previous, receiver))
            {
                events.Add(new ReceiverEvent.Updated(receiver, previous));
                changed = true;
            }
            else if (previous.LastSeen != receiver.LastSeen)
            {
                changed = true;
            }
        }

        foreach (var (id, previous) in _published)
        {
            if (!built.ContainsKey(id))
            {
                events.Add(new ReceiverEvent.Lost(previous));
                changed = true;
            }
        }

        _published = built;
        return new CacheUpdate(events, questions, changed);
    }

    /// <summary>Resolves one <c>_airplay._tcp</c> instance; also returns when its own SRV/TXT records were last received.</summary>
    private (DiscoveredReceiver Receiver, DateTimeOffset OwnSeen)? Resolve(DnsName instance, List<DnsName> raopInstances, HashSet<DnsName> usedRaop, List<DnsQuestion> questions, DateTimeOffset now)
    {
        var srv = Newest(instance, DnsType.Srv);
        var txt = Newest(instance, DnsType.Txt);
        if (srv is null)
        {
            Ask(questions, instance, DnsType.Srv, now);
        }

        if (txt is null)
        {
            Ask(questions, instance, DnsType.Txt, now);
        }

        if (srv is null || txt is null)
        {
            return null;
        }

        var srvData = (DnsRecordData.Srv)srv.Record.Data;
        var addresses = Addresses(srvData.Target, out var addressEntries);
        if (addresses.Count == 0)
        {
            Ask(questions, srvData.Target, DnsType.A, now);
            if (KeepIpv6)
            {
                Ask(questions, srvData.Target, DnsType.Aaaa, now);
            }

            return null;
        }

        AirplayTxtRecord airplay;
        try
        {
            airplay = AirplayTxtRecord.Parse(((DnsRecordData.Txt)txt.Record.Data).Strings());
        }
        catch (DnsFormatException e)
        {
            _logger.LogWarning("Ignoring malformed TXT record of {Instance}: {Error}", instance, e.Message);
            return null;
        }

        var name = instance.Labels[0];
        var id = string.IsNullOrWhiteSpace(airplay.DeviceId) ? name : airplay.DeviceId;
        var lastSeen = new[] { srv.Received, txt.Received }.Concat(addressEntries.Select(e => e.Received)).Max();

        RaopTxtRecord? raop = null;
        var raopInstance = MatchRaop(raopInstances, airplay.DeviceId, name);
        if (raopInstance is not null)
        {
            usedRaop.Add(raopInstance);
            var raopTxt = Newest(raopInstance, DnsType.Txt);
            if (raopTxt is null)
            {
                Ask(questions, raopInstance, DnsType.Txt, now);
            }
            else
            {
                try
                {
                    raop = RaopTxtRecord.Parse(((DnsRecordData.Txt)raopTxt.Record.Data).Strings());
                    lastSeen = raopTxt.Received > lastSeen ? raopTxt.Received : lastSeen;
                }
                catch (DnsFormatException e)
                {
                    _logger.LogWarning("Ignoring malformed TXT record of {Instance}: {Error}", raopInstance, e.Message);
                }
            }
        }

        var ownSeen = srv.Received > txt.Received ? srv.Received : txt.Received;
        return (new DiscoveredReceiver(id, name, addresses, srvData.Port, airplay, raop, lastSeen), ownSeen);
    }

    private static DnsName? MatchRaop(List<DnsName> raopInstances, string? deviceId, string name)
    {
        var compactId = deviceId?.Replace(":", string.Empty, StringComparison.Ordinal);
        DnsName? byName = null;
        foreach (var raop in raopInstances)
        {
            var label = raop.Labels[0];
            var at = label.IndexOf('@', StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(compactId) && string.Equals(label[..at], compactId, StringComparison.OrdinalIgnoreCase))
            {
                return raop;
            }

            if (string.Equals(label[(at + 1)..], name, StringComparison.Ordinal))
            {
                byName = raop;
            }
        }

        // Fall back to the name only when the _airplay record has no deviceid to compare with.
        return string.IsNullOrEmpty(compactId) ? byName : null;
    }

    /// <summary>A/AAAA addresses of <paramref name="host"/>: IPv4 first, then routable IPv6, then link-local IPv6 (scoped to the arrival interface).</summary>
    private List<IPAddress> Addresses(DnsName host, out List<Entry> entries)
    {
        entries = [.. All(host, DnsType.A).OrderBy(e => e.Flushed).ThenByDescending(e => e.Received)];
        if (KeepIpv6)
        {
            entries.AddRange(All(host, DnsType.Aaaa).OrderBy(e => e.Flushed).ThenByDescending(e => e.Received));
        }

        var result = new List<IPAddress>();
        foreach (var e in entries)
        {
            var address = e.Record.Data switch
            {
                DnsRecordData.A a => a.Address,
                DnsRecordData.Aaaa aaaa => aaaa.Address,
                _ => null,
            };

            if (address is null)
            {
                continue;
            }

            if (address.IsIPv6LinkLocal && e.InterfaceIndex > 0)
            {
                address = new IPAddress(address.GetAddressBytes(), e.InterfaceIndex);
            }

            if (!result.Contains(address))
            {
                result.Add(address);
            }
        }

        // Stable order: IPv4, IPv6 global/ULA, IPv6 link-local by scope id (one per interface it was heard on; unusable
        // when the answer was reflected from another link).
        return [.. result.Select((a, i) => (a, i)).OrderBy(x => Rank(x.a)).ThenBy(x => x.a.IsIPv6LinkLocal ? x.a.ScopeId : 0).ThenBy(x => x.i).Select(x => x.a)];

        static int Rank(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork ? 0 : a.IsIPv6LinkLocal ? 2 : 1;
    }

    private void AddRefreshQuestions(List<DnsQuestion> questions, DateTimeOffset now)
    {
        foreach (var (key, list) in _records)
        {
            if (key.Type == DnsType.Ptr)
            {
                continue; // the periodic browse refreshes PTR records
            }

            foreach (var e in list)
            {
                if (e.Flushed)
                {
                    continue;
                }

                var elapsed = ((now - e.Received) / e.Ttl) - e.Jitter;
                var stage = 0;
                while (stage < RefreshPoints.Length && elapsed >= RefreshPoints[stage])
                {
                    stage++;
                }

                if (stage > e.RefreshStage)
                {
                    e.RefreshStage = stage;
                    if (!questions.Any(q => q.Name.Equals(key.Name) && q.Type == key.Type))
                    {
                        questions.Add(new DnsQuestion(key.Name, key.Type));
                        _asked[key] = (now, 1);
                    }
                }
            }
        }
    }

    private void Ask(List<DnsQuestion> questions, DnsName name, DnsType type, DateTimeOffset now)
    {
        _pending = true;
        var key = (name, type);
        if (_asked.TryGetValue(key, out var asked) && (asked.Count >= FollowUpAttempts || now - asked.Last < FollowUpSpacing))
        {
            return;
        }

        _asked[key] = (now, asked.Count + 1);
        questions.Add(new DnsQuestion(name, type));
    }

    private static bool SameContent(DiscoveredReceiver a, DiscoveredReceiver b) =>
        a.Name == b.Name
        && a.Port == b.Port
        && a.Addresses.SequenceEqual(b.Addresses)
        && SameTxt(a.Airplay.Raw, b.Airplay.Raw)
        && SameTxt(a.Raop?.Raw, b.Raop?.Raw);

    private static bool SameTxt(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
    }

    /// <summary>One cached record.</summary>
    private sealed class Entry(DnsRecord record, DateTimeOffset received, TimeSpan ttl, int interfaceIndex, double jitter)
    {
        public DnsRecord Record { get; set; } = record;

        public DateTimeOffset Received { get; set; } = received;

        public TimeSpan Ttl { get; set; } = ttl;

        public DateTimeOffset Expires { get; set; } = received + ttl;

        /// <summary>Superseded by a cache-flush record, or said goodbye; leaves at <see cref="Expires"/> without grace.</summary>
        public bool Flushed { get; set; }

        /// <summary>Number of refresh points (80/85/90/95 % of the TTL) already asked for.</summary>
        public int RefreshStage { get; set; }

        public int InterfaceIndex { get; set; } = interfaceIndex;

        /// <summary>Random 0-2 % added to every refresh point of this record (RFC 6762 5.2).</summary>
        public double Jitter { get; } = jitter;

        public DateTimeOffset RemoveAt(TimeSpan grace) => Flushed ? Expires : Expires + grace;
    }
}
