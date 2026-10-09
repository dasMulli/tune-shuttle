// New code (no upstream .rs file): the DNS wire format needed by the mDNS responder. Replaces the `dns_parser`
// module that the `libmdns` crate (used by discovery/src/lib.rs `launch_libmdns`) vendors.
//
// Deliberate differences to libmdns' dns_parser:
//   * names are compared case-insensitively (RFC 6762 section 16; libmdns compares the encoded bytes);
//   * names are compressed on encode (RFC 1035 section 4.1.4; libmdns writes them uncompressed);
//   * a name is a list of labels, so a label may contain '.' (libmdns splits the instance name at every '.').
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Librespot.Discovery.Mdns;

/// <summary>DNS resource record / query types used by the responder (RFC 1035, 2782, 3596).</summary>
internal enum DnsType : ushort
{
    /// <summary>IPv4 host address.</summary>
    A = 1,

    /// <summary>Domain name pointer.</summary>
    Ptr = 12,

    /// <summary>Text strings.</summary>
    Txt = 16,

    /// <summary>IPv6 host address.</summary>
    Aaaa = 28,

    /// <summary>Service locator.</summary>
    Srv = 33,

    /// <summary>Query type <c>*</c> (any record).</summary>
    Any = 255,
}

/// <summary>DNS class values (RFC 1035 section 3.2.4).</summary>
internal static class DnsClass
{
    /// <summary>The Internet class.</summary>
    public const ushort In = 1;

    /// <summary>Query class <c>*</c>.</summary>
    public const ushort Any = 255;
}

/// <summary>A malformed DNS packet.</summary>
internal sealed class DnsFormatException(string message) : Exception(message);

/// <summary>A domain name as a list of labels. Equality ignores ASCII case.</summary>
internal sealed class DnsName : IEquatable<DnsName>
{
    /// <summary>Maximum length of a single label in bytes.</summary>
    public const int MaxLabelLength = 63;

    /// <summary>Maximum length of an encoded name in bytes.</summary>
    public const int MaxNameLength = 255;

    /// <summary>Creates a name from its labels (no label may be empty or longer than 63 UTF-8 bytes).</summary>
    public DnsName(IEnumerable<string> labels)
    {
        Labels = labels.ToArray();
        var encoded = 1;
        foreach (var label in Labels)
        {
            var length = Encoding.UTF8.GetByteCount(label);
            if (length is 0 or > MaxLabelLength)
            {
                throw new ArgumentException($"invalid DNS label \"{label}\"", nameof(labels));
            }

            encoded += length + 1;
        }

        if (encoded > MaxNameLength)
        {
            throw new ArgumentException("DNS name too long", nameof(labels));
        }
    }

    /// <summary>The labels, most specific first.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>Parses a dotted name (<c>_spotify-connect._tcp.local</c>); a trailing dot is ignored.</summary>
    public static DnsName Parse(string dotted) => new(dotted.TrimEnd('.').Split('.'));

    /// <summary>Returns <c>label.this</c>.</summary>
    public DnsName Prepend(string label) => new(Labels.Prepend(label));

    /// <summary>
    /// Truncates <paramref name="label"/> to at most 63 UTF-8 bytes without splitting a character, so a user-chosen
    /// device name always fits into one label.
    /// </summary>
    public static string TruncateLabel(string label)
    {
        if (Encoding.UTF8.GetByteCount(label) <= MaxLabelLength)
        {
            return label;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in label.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxLabelLength)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <inheritdoc/>
    public bool Equals(DnsName? other)
    {
        if (other is null || other.Labels.Count != Labels.Count)
        {
            return false;
        }

        for (var i = 0; i < Labels.Count; i++)
        {
            if (!LabelEquals(Labels[i], other.Labels[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as DnsName);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var label in Labels)
        {
            hash.Add(AsciiLower(label), StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>The dotted form (labels joined with '.').</summary>
    public override string ToString() => string.Join('.', Labels);

    /// <summary>The key used for name compression: the lower-cased labels with their lengths.</summary>
    internal static string SuffixKey(IReadOnlyList<string> labels, int start)
    {
        var builder = new StringBuilder();
        for (var i = start; i < labels.Count; i++)
        {
            var lower = AsciiLower(labels[i]);
            builder.Append(lower.Length).Append(':').Append(lower);
        }

        return builder.ToString();
    }

    private static bool LabelEquals(string a, string b) => string.Equals(AsciiLower(a), AsciiLower(b), StringComparison.Ordinal);

    // DNS case-insensitivity covers ASCII only (RFC 4343).
    private static string AsciiLower(string s) => string.Create(s.Length, s, static (span, src) =>
    {
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
    });
}

/// <summary>A question (RFC 1035 section 4.1.2). <see cref="UnicastResponse"/> is the mDNS QU bit (RFC 6762 section 5.4).</summary>
internal sealed record DnsQuestion(DnsName Name, DnsType Type, ushort Class = DnsClass.In, bool UnicastResponse = false);

/// <summary>Typed RDATA of a resource record.</summary>
internal abstract record DnsRecordData
{
    private DnsRecordData()
    {
    }

    /// <summary>An IPv4 address.</summary>
    public sealed record A(IPAddress Address) : DnsRecordData;

    /// <summary>An IPv6 address.</summary>
    public sealed record Aaaa(IPAddress Address) : DnsRecordData;

    /// <summary>A pointer to another name.</summary>
    public sealed record Ptr(DnsName Target) : DnsRecordData;

    /// <summary>A service locator.</summary>
    public sealed record Srv(ushort Priority, ushort Weight, ushort Port, DnsName Target) : DnsRecordData;

    /// <summary>TXT RDATA, kept as raw bytes (a sequence of length-prefixed strings).</summary>
    public sealed record Txt(byte[] Raw) : DnsRecordData
    {
        /// <summary>Encodes the strings as length-prefixed entries; an empty list becomes a single zero byte (as libmdns).</summary>
        public static Txt FromStrings(IEnumerable<string> entries)
        {
            var buffer = new List<byte>();
            foreach (var entry in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(entry);
                if (bytes.Length > 255)
                {
                    throw new ArgumentException($"\"{entry}\" is too long for a TXT record", nameof(entries));
                }

                buffer.Add((byte)bytes.Length);
                buffer.AddRange(bytes);
            }

            return new Txt(buffer.Count == 0 ? [0] : buffer.ToArray());
        }

        /// <summary>Decodes the length-prefixed entries (empty entries are skipped).</summary>
        public IReadOnlyList<string> Strings()
        {
            var result = new List<string>();
            var i = 0;
            while (i < Raw.Length)
            {
                var length = Raw[i++];
                if (i + length > Raw.Length)
                {
                    throw new DnsFormatException("TXT entry exceeds RDATA");
                }

                if (length > 0)
                {
                    result.Add(Encoding.UTF8.GetString(Raw, i, length));
                }

                i += length;
            }

            return result;
        }

        /// <inheritdoc/>
        public bool Equals(Txt? other) => other is not null && Raw.AsSpan().SequenceEqual(other.Raw);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(Raw);
            return hash.ToHashCode();
        }
    }

    /// <summary>RDATA of a type this responder does not interpret.</summary>
    public sealed record Unknown(byte[] Raw) : DnsRecordData
    {
        /// <inheritdoc/>
        public bool Equals(Unknown? other) => other is not null && Raw.AsSpan().SequenceEqual(other.Raw);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(Raw);
            return hash.ToHashCode();
        }
    }
}

/// <summary>A resource record (RFC 1035 section 4.1.3). <see cref="CacheFlush"/> is the mDNS cache-flush bit.</summary>
internal sealed record DnsRecord(DnsName Name, DnsType Type, ushort Class, bool CacheFlush, uint Ttl, DnsRecordData Data)
{
    /// <summary>Creates an IN-class record whose type follows from <paramref name="data"/>.</summary>
    public static DnsRecord Create(DnsName name, uint ttl, DnsRecordData data) => new(name, TypeOf(data), DnsClass.In, false, ttl, data);

    private static DnsType TypeOf(DnsRecordData data) => data switch
    {
        DnsRecordData.A => DnsType.A,
        DnsRecordData.Aaaa => DnsType.Aaaa,
        DnsRecordData.Ptr => DnsType.Ptr,
        DnsRecordData.Srv => DnsType.Srv,
        DnsRecordData.Txt => DnsType.Txt,
        _ => throw new ArgumentException("record type must be given explicitly for unknown RDATA", nameof(data)),
    };
}

/// <summary>A DNS message (header + four sections) with encode/decode.</summary>
internal sealed class DnsMessage
{
    /// <summary>QR: the message is a response.</summary>
    public const ushort FlagResponse = 0x8000;

    /// <summary>AA: authoritative answer.</summary>
    public const ushort FlagAuthoritative = 0x0400;

    /// <summary>TC: truncated.</summary>
    public const ushort FlagTruncated = 0x0200;

    private const ushort OpcodeMask = 0x7800;
    private const ushort TopBit = 0x8000;
    private const int MaxPointerJumps = 128;

    /// <summary>The message id.</summary>
    public ushort Id { get; set; }

    /// <summary>The raw header flags (QR, opcode, AA, TC, RD, RA, Z, RCODE).</summary>
    public ushort Flags { get; set; }

    /// <summary>Whether QR is set.</summary>
    public bool IsResponse => (Flags & FlagResponse) != 0;

    /// <summary>Whether TC is set.</summary>
    public bool IsTruncated => (Flags & FlagTruncated) != 0;

    /// <summary>The opcode (0 = standard query).</summary>
    public int Opcode => (Flags & OpcodeMask) >> 11;

    /// <summary>The question section.</summary>
    public List<DnsQuestion> Questions { get; } = [];

    /// <summary>The answer section.</summary>
    public List<DnsRecord> Answers { get; } = [];

    /// <summary>The authority section.</summary>
    public List<DnsRecord> Authorities { get; } = [];

    /// <summary>The additional section.</summary>
    public List<DnsRecord> Additionals { get; } = [];

    /// <summary>Whether all four sections are empty (libmdns <c>Builder::is_empty</c>).</summary>
    public bool IsEmpty => Questions.Count == 0 && Answers.Count == 0 && Authorities.Count == 0 && Additionals.Count == 0;

    /// <summary>A response header as libmdns' <c>Builder::new_response(id, recursion: false, authoritative: true)</c>.</summary>
    public static DnsMessage NewResponse(ushort id) => new() { Id = id, Flags = FlagResponse | FlagAuthoritative };

    /// <summary>Encodes the message, compressing names.</summary>
    public byte[] Encode()
    {
        var writer = new Writer();
        writer.U16(Id);
        writer.U16(Flags);
        writer.U16(checked((ushort)Questions.Count));
        writer.U16(checked((ushort)Answers.Count));
        writer.U16(checked((ushort)Authorities.Count));
        writer.U16(checked((ushort)Additionals.Count));

        foreach (var q in Questions)
        {
            writer.Name(q.Name);
            writer.U16((ushort)q.Type);
            writer.U16((ushort)(q.Class | (q.UnicastResponse ? TopBit : 0)));
        }

        foreach (var section in new[] { Answers, Authorities, Additionals })
        {
            foreach (var record in section)
            {
                writer.Record(record);
            }
        }

        return writer.ToArray();
    }

    /// <summary>Decodes a message. Throws <see cref="DnsFormatException"/> when it is malformed.</summary>
    public static DnsMessage Parse(ReadOnlySpan<byte> packet)
    {
        var reader = new Reader(packet);
        var message = new DnsMessage { Id = reader.U16(), Flags = reader.U16() };
        int qd = reader.U16(), an = reader.U16(), ns = reader.U16(), ar = reader.U16();

        for (var i = 0; i < qd; i++)
        {
            var name = reader.Name();
            var type = (DnsType)reader.U16();
            var cls = reader.U16();
            message.Questions.Add(new DnsQuestion(name, type, (ushort)(cls & ~TopBit), (cls & TopBit) != 0));
        }

        ReadRecords(ref reader, an, message.Answers);
        ReadRecords(ref reader, ns, message.Authorities);
        ReadRecords(ref reader, ar, message.Additionals);
        return message;
    }

    /// <summary>Like <see cref="Parse"/>, returning <c>false</c> (and the reason) instead of throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, [NotNullWhen(true)] out DnsMessage? message, [NotNullWhen(false)] out string? error)
    {
        try
        {
            message = Parse(packet);
            error = null;
            return true;
        }
        catch (DnsFormatException e)
        {
            message = null;
            error = e.Message;
            return false;
        }
    }

    private static void ReadRecords(ref Reader reader, int count, List<DnsRecord> into)
    {
        for (var i = 0; i < count; i++)
        {
            var name = reader.Name();
            var type = (DnsType)reader.U16();
            var cls = reader.U16();
            var ttl = reader.U32();
            var length = reader.U16();
            var end = reader.Position + length;
            if (end > reader.Length)
            {
                throw new DnsFormatException("RDATA exceeds packet");
            }

            DnsRecordData data = type switch
            {
                DnsType.A when length == 4 => new DnsRecordData.A(new IPAddress(reader.Bytes(4))),
                DnsType.Aaaa when length == 16 => new DnsRecordData.Aaaa(new IPAddress(reader.Bytes(16))),
                DnsType.Ptr => new DnsRecordData.Ptr(reader.Name()),
                DnsType.Srv => new DnsRecordData.Srv(reader.U16(), reader.U16(), reader.U16(), reader.Name()),
                DnsType.Txt => new DnsRecordData.Txt(reader.Bytes(length)),
                DnsType.A or DnsType.Aaaa => throw new DnsFormatException($"bad address length {length}"),
                _ => new DnsRecordData.Unknown(reader.Bytes(length)),
            };

            if (reader.Position != end)
            {
                throw new DnsFormatException($"RDATA length mismatch for {type}");
            }

            into.Add(new DnsRecord(name, type, (ushort)(cls & ~TopBit), (cls & TopBit) != 0, ttl, data));
        }
    }

    private sealed class Writer
    {
        private readonly List<byte> _buffer = new(512);
        private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);

        public void U16(ushort value)
        {
            _buffer.Add((byte)(value >> 8));
            _buffer.Add((byte)value);
        }

        public void U32(uint value)
        {
            U16((ushort)(value >> 16));
            U16((ushort)value);
        }

        public void Name(DnsName name)
        {
            var labels = name.Labels;
            for (var i = 0; i < labels.Count; i++)
            {
                var key = DnsName.SuffixKey(labels, i);
                if (_names.TryGetValue(key, out var offset))
                {
                    U16((ushort)(0xC000 | offset));
                    return;
                }

                if (_buffer.Count < 0x3FFF)
                {
                    _names[key] = _buffer.Count;
                }

                var bytes = Encoding.UTF8.GetBytes(labels[i]);
                _buffer.Add((byte)bytes.Length);
                _buffer.AddRange(bytes);
            }

            _buffer.Add(0);
        }

        public void Record(DnsRecord record)
        {
            Name(record.Name);
            U16((ushort)record.Type);
            U16((ushort)(record.Class | (record.CacheFlush ? TopBit : 0)));
            U32(record.Ttl);

            var lengthOffset = _buffer.Count;
            U16(0);
            var start = _buffer.Count;
            switch (record.Data)
            {
                case DnsRecordData.A a:
                    RequireFamily(a.Address, AddressFamily.InterNetwork);
                    _buffer.AddRange(a.Address.GetAddressBytes());
                    break;
                case DnsRecordData.Aaaa aaaa:
                    RequireFamily(aaaa.Address, AddressFamily.InterNetworkV6);
                    _buffer.AddRange(aaaa.Address.GetAddressBytes());
                    break;
                case DnsRecordData.Ptr ptr:
                    Name(ptr.Target);
                    break;
                case DnsRecordData.Srv srv:
                    U16(srv.Priority);
                    U16(srv.Weight);
                    U16(srv.Port);
                    Name(srv.Target);
                    break;
                case DnsRecordData.Txt txt:
                    _buffer.AddRange(txt.Raw);
                    break;
                case DnsRecordData.Unknown unknown:
                    _buffer.AddRange(unknown.Raw);
                    break;
            }

            var length = checked((ushort)(_buffer.Count - start));
            _buffer[lengthOffset] = (byte)(length >> 8);
            _buffer[lengthOffset + 1] = (byte)length;
        }

        public byte[] ToArray() => _buffer.ToArray();

        private static void RequireFamily(IPAddress address, AddressFamily family)
        {
            if (address.AddressFamily != family)
            {
                throw new ArgumentException($"{address} is not an {family} address");
            }
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> packet)
    {
        private readonly ReadOnlySpan<byte> _packet = packet;

        public int Position { get; private set; }

        public readonly int Length => _packet.Length;

        public ushort U16()
        {
            Require(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(_packet[Position..]);
            Position += 2;
            return value;
        }

        public uint U32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_packet[Position..]);
            Position += 4;
            return value;
        }

        public byte[] Bytes(int count)
        {
            Require(count);
            var value = _packet.Slice(Position, count).ToArray();
            Position += count;
            return value;
        }

        public DnsName Name()
        {
            var labels = new List<string>();
            var position = Position;
            var resumeAt = -1;
            var jumps = 0;
            var encodedLength = 1;

            while (true)
            {
                if (position >= _packet.Length)
                {
                    throw new DnsFormatException("name exceeds packet");
                }

                var length = _packet[position];
                if ((length & 0xC0) == 0xC0)
                {
                    if (position + 1 >= _packet.Length)
                    {
                        throw new DnsFormatException("truncated compression pointer");
                    }

                    var target = ((length & 0x3F) << 8) | _packet[position + 1];
                    if (target >= position || ++jumps > MaxPointerJumps)
                    {
                        throw new DnsFormatException("invalid compression pointer");
                    }

                    if (resumeAt < 0)
                    {
                        resumeAt = position + 2;
                    }

                    position = target;
                    continue;
                }

                if ((length & 0xC0) != 0)
                {
                    throw new DnsFormatException($"unsupported label type 0x{length:x2}");
                }

                position++;
                if (length == 0)
                {
                    break;
                }

                if (position + length > _packet.Length)
                {
                    throw new DnsFormatException("label exceeds packet");
                }

                encodedLength += length + 1;
                if (encodedLength > DnsName.MaxNameLength)
                {
                    throw new DnsFormatException("name too long");
                }

                labels.Add(Encoding.UTF8.GetString(_packet.Slice(position, length)));
                position += length;
            }

            Position = resumeAt >= 0 ? resumeAt : position;
            try
            {
                return new DnsName(labels);
            }
            catch (ArgumentException e)
            {
                // e.g. a label of invalid UTF-8 that grows past 63 bytes once decoded
                throw new DnsFormatException(e.Message);
            }
        }

        private readonly void Require(int count)
        {
            if (count < 0 || Position + count > _packet.Length)
            {
                throw new DnsFormatException("unexpected end of packet");
            }
        }
    }
}
