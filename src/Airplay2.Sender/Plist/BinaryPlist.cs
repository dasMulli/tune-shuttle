using System.Buffers.Binary;
using System.Text;

namespace Airplay2.Sender.Plist;

/// <summary>A bplist UID value (object 0x8x). Not used by AirPlay but round-tripped for completeness.</summary>
public readonly record struct PlistUid(ulong Value);

/// <summary>
/// An ordered string-keyed dictionary: the object model for bplist dictionaries. Insertion order is preserved so a
/// re-encoded plist keeps the receiver's key order and so the sender emits keys in a stable order.
/// </summary>
public sealed class PlistDictionary : OrderedDictionary<string, object?>
{
    /// <summary>Creates an empty dictionary.</summary>
    public PlistDictionary()
    {
    }

    /// <summary>Creates a dictionary from pairs, in order.</summary>
    public PlistDictionary(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        foreach (var (k, v) in pairs)
        {
            Add(k, v);
        }
    }

    /// <summary>Gets a string value or null when absent or of another type.</summary>
    public string? GetString(string key) => TryGetValue(key, out var v) ? v as string : null;

    /// <summary>Gets an integer value (any integer type stored) or null.</summary>
    public long? GetInt64(string key) => TryGetValue(key, out var v) ? v switch
    {
        long l => l,
        ulong ul when ul <= long.MaxValue => (long)ul,
        int i => i,
        uint ui => ui,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        _ => null,
    } : null;

    /// <summary>Gets a boolean value or null.</summary>
    public bool? GetBool(string key) => TryGetValue(key, out var v) && v is bool b ? b : null;

    /// <summary>Gets a real value (stored real or integer) or null.</summary>
    public double? GetDouble(string key) => TryGetValue(key, out var v) ? v switch
    {
        double d => d,
        float f => f,
        _ => GetInt64(key),
    } : null;

    /// <summary>Gets a data value or null.</summary>
    public byte[]? GetData(string key) => TryGetValue(key, out var v) ? v as byte[] : null;

    /// <summary>Gets a nested dictionary or null.</summary>
    public PlistDictionary? GetDictionary(string key) => TryGetValue(key, out var v) ? v as PlistDictionary : null;

    /// <summary>Gets an array or null.</summary>
    public List<object?>? GetArray(string key) => TryGetValue(key, out var v) ? v as List<object?> : null;
}

/// <summary>
/// Reader and writer for Apple's binary property list format (<c>bplist00</c>), the body encoding of every AirPlay 2
/// SETUP/TEARDOWN/feedback/command message (SPEC-control.md 2.3). Object model: <see cref="PlistDictionary"/>,
/// <see cref="List{T}"/> of object, string, bool, long/ulong (integers), double (real), byte[] (data),
/// <see cref="DateTime"/> (date, UTC), <see cref="PlistUid"/>, null.
/// </summary>
public static class BinaryPlist
{
    private static readonly byte[] Magic = "bplist00"u8.ToArray();
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>True when the data starts with the bplist magic.</summary>
    public static bool IsBinaryPlist(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Magic);

    /// <summary>Encodes an object graph as a binary plist.</summary>
    public static byte[] Encode(object? root)
    {
        var writer = new Writer();
        return writer.Write(root);
    }

    /// <summary>Decodes a binary plist into its object graph.</summary>
    /// <exception cref="FormatException">The data is not a well-formed bplist00.</exception>
    public static object? Decode(ReadOnlySpan<byte> data) => new Reader(data).ReadRoot();

    /// <summary>Decodes a binary plist whose root must be a dictionary.</summary>
    public static PlistDictionary DecodeDictionary(ReadOnlySpan<byte> data) =>
        Decode(data) as PlistDictionary ?? throw new FormatException("bplist root is not a dictionary.");

    private sealed class Writer
    {
        private readonly List<object?> _objects = [];
        private readonly Dictionary<object, int> _refs = new(ReferenceEqualityComparer.Instance);
        private readonly MemoryStream _out = new();
        private int _refSize;

        public byte[] Write(object? root)
        {
            Flatten(root);
            var count = _objects.Count;
            _refSize = ByteWidth((ulong)count);
            _out.Write(Magic);
            var offsets = new long[count];
            for (var i = 0; i < count; i++)
            {
                offsets[i] = _out.Position;
                WriteObject(_objects[i]);
            }

            var tableOffset = _out.Position;
            var offsetSize = ByteWidth((ulong)tableOffset);
            foreach (var off in offsets)
            {
                WriteSized((ulong)off, offsetSize);
            }

            Span<byte> trailer = stackalloc byte[32];
            trailer.Clear();
            trailer[6] = (byte)offsetSize;
            trailer[7] = (byte)_refSize;
            BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)count);
            BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);
            BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)tableOffset);
            _out.Write(trailer);
            return _out.ToArray();
        }

        private int Flatten(object? value)
        {
            // Containers and reference-type scalars are de-duplicated by reference only; value equality is not needed
            // for correctness and keeps the writer predictable.
            if (value is not null && !(value is string || value is byte[] || value is PlistDictionary || value is List<object?> || value is object?[]))
            {
                _objects.Add(value);
                return _objects.Count - 1;
            }

            if (value is not null && _refs.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var index = _objects.Count;
            _objects.Add(value);
            if (value is not null)
            {
                _refs[value] = index;
            }

            switch (value)
            {
                case PlistDictionary dict:
                    var keyRefs = new int[dict.Count];
                    var valueRefs = new int[dict.Count];
                    var i = 0;
                    foreach (var (k, v) in dict)
                    {
                        keyRefs[i] = Flatten(k);
                        valueRefs[i] = Flatten(v);
                        i++;
                    }

                    _objects[index] = new DictRefs(keyRefs, valueRefs);
                    break;
                case List<object?> list:
                    var refs = new int[list.Count];
                    for (var j = 0; j < list.Count; j++)
                    {
                        refs[j] = Flatten(list[j]);
                    }

                    _objects[index] = new ArrayRefs(refs);
                    break;
                case object?[] array:
                    var arefs = new int[array.Length];
                    for (var j = 0; j < array.Length; j++)
                    {
                        arefs[j] = Flatten(array[j]);
                    }

                    _objects[index] = new ArrayRefs(arefs);
                    break;
            }

            return index;
        }

        private sealed record DictRefs(int[] Keys, int[] Values);

        private sealed record ArrayRefs(int[] Items);

        private void WriteObject(object? value)
        {
            switch (value)
            {
                case null:
                    _out.WriteByte(0x00);
                    break;
                case bool b:
                    _out.WriteByte(b ? (byte)0x09 : (byte)0x08);
                    break;
                case byte or sbyte or short or ushort or int or uint or long:
                    WriteInteger(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case ulong ul:
                    if (ul <= long.MaxValue)
                    {
                        WriteInteger((long)ul);
                    }
                    else
                    {
                        // 16-byte integer: high 8 bytes zero, low 8 bytes the value (how plistlib and CoreFoundation store uint64 above int64.Max)
                        _out.WriteByte(0x14);
                        Span<byte> buf = stackalloc byte[16];
                        buf.Clear();
                        BinaryPrimitives.WriteUInt64BigEndian(buf[8..], ul);
                        _out.Write(buf);
                    }

                    break;
                case float f:
                    _out.WriteByte(0x22);
                    Span<byte> fb = stackalloc byte[4];
                    BinaryPrimitives.WriteSingleBigEndian(fb, f);
                    _out.Write(fb);
                    break;
                case double d:
                    _out.WriteByte(0x23);
                    Span<byte> db = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(db, d);
                    _out.Write(db);
                    break;
                case DateTime dt:
                    _out.WriteByte(0x33);
                    Span<byte> tb = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(tb, (dt.ToUniversalTime() - AppleEpoch).TotalSeconds);
                    _out.Write(tb);
                    break;
                case byte[] data:
                    WriteMarker(0x40, data.Length);
                    _out.Write(data);
                    break;
                case string s:
                    WriteString(s);
                    break;
                case PlistUid uid:
                    var ub = ToMinimalBigEndian(uid.Value);
                    _out.WriteByte((byte)(0x80 | (ub.Length - 1)));
                    _out.Write(ub);
                    break;
                case ArrayRefs arr:
                    WriteMarker(0xA0, arr.Items.Length);
                    foreach (var r in arr.Items)
                    {
                        WriteSized((ulong)r, _refSize);
                    }

                    break;
                case DictRefs dict:
                    WriteMarker(0xD0, dict.Keys.Length);
                    foreach (var r in dict.Keys)
                    {
                        WriteSized((ulong)r, _refSize);
                    }

                    foreach (var r in dict.Values)
                    {
                        WriteSized((ulong)r, _refSize);
                    }

                    break;
                default:
                    throw new NotSupportedException($"Cannot encode {value.GetType()} as a plist value.");
            }
        }

        private void WriteInteger(long v)
        {
            if (v < 0)
            {
                _out.WriteByte(0x13);
                Span<byte> b8 = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(b8, v);
                _out.Write(b8);
            }
            else if (v <= byte.MaxValue)
            {
                _out.WriteByte(0x10);
                _out.WriteByte((byte)v);
            }
            else if (v <= ushort.MaxValue)
            {
                _out.WriteByte(0x11);
                Span<byte> b2 = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(b2, (ushort)v);
                _out.Write(b2);
            }
            else if (v <= uint.MaxValue)
            {
                _out.WriteByte(0x12);
                Span<byte> b4 = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(b4, (uint)v);
                _out.Write(b4);
            }
            else
            {
                _out.WriteByte(0x13);
                Span<byte> b8 = stackalloc byte[8];
                BinaryPrimitives.WriteInt64BigEndian(b8, v);
                _out.Write(b8);
            }
        }

        private void WriteString(string s)
        {
            var ascii = true;
            foreach (var c in s)
            {
                if (c > 0x7F)
                {
                    ascii = false;
                    break;
                }
            }

            if (ascii)
            {
                WriteMarker(0x50, s.Length);
                _out.Write(Encoding.ASCII.GetBytes(s));
            }
            else
            {
                // UTF-16BE, length in code units
                var bytes = Encoding.BigEndianUnicode.GetBytes(s);
                WriteMarker(0x60, bytes.Length / 2);
                _out.Write(bytes);
            }
        }

        private void WriteMarker(byte marker, int count)
        {
            if (count < 15)
            {
                _out.WriteByte((byte)(marker | count));
            }
            else
            {
                _out.WriteByte((byte)(marker | 0x0F));
                WriteInteger(count);
            }
        }

        private void WriteSized(ulong value, int size)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buf, value);
            _out.Write(buf[(8 - size)..]);
        }

        private static int ByteWidth(ulong value) => value switch
        {
            <= byte.MaxValue => 1,
            <= ushort.MaxValue => 2,
            <= uint.MaxValue => 4,
            _ => 8,
        };

        private static byte[] ToMinimalBigEndian(ulong value)
        {
            var width = ByteWidth(value);
            var buf = new byte[width];
            for (var i = width - 1; i >= 0; i--)
            {
                buf[i] = (byte)value;
                value >>= 8;
            }

            return buf;
        }
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _offsetSize;
        private readonly int _refSize;
        private readonly long _objectCount;
        private readonly long _topObject;
        private readonly long _tableOffset;
        private int _depth;

        public Reader(ReadOnlySpan<byte> data)
        {
            if (!IsBinaryPlist(data) || data.Length < 8 + 32)
            {
                throw new FormatException("Not a bplist00 document.");
            }

            _data = data;
            var trailer = data[^32..];
            _offsetSize = trailer[6];
            _refSize = trailer[7];
            _objectCount = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
            _topObject = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
            _tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
            if (_offsetSize is < 1 or > 8 || _refSize is < 1 or > 8 || _objectCount <= 0 || _topObject >= _objectCount ||
                _tableOffset < 8 || _tableOffset + _objectCount * _offsetSize > data.Length - 32)
            {
                throw new FormatException("bplist trailer is inconsistent.");
            }
        }

        public object? ReadRoot() => ReadObject(_topObject);

        private long ObjectOffset(long index)
        {
            if (index < 0 || index >= _objectCount)
            {
                throw new FormatException($"bplist object reference {index} out of range.");
            }

            var off = ReadSized(_tableOffset + index * _offsetSize, _offsetSize);
            if (off >= (ulong)(_data.Length - 32))
            {
                throw new FormatException("bplist object offset out of range.");
            }

            return (long)off;
        }

        private ulong ReadSized(long position, int size)
        {
            ulong v = 0;
            for (var i = 0; i < size; i++)
            {
                v = (v << 8) | _data[(int)position + i];
            }

            return v;
        }

        private object? ReadObject(long index)
        {
            if (++_depth > 512)
            {
                throw new FormatException("bplist nesting too deep (cycle?).");
            }

            try
            {
                var pos = ObjectOffset(index);
                var marker = _data[(int)pos];
                var type = marker & 0xF0;
                var info = marker & 0x0F;
                pos++;
                switch (type)
                {
                    case 0x00:
                        return info switch
                        {
                            0x00 => null,
                            0x08 => false,
                            0x09 => true,
                            0x0F => null, // fill
                            _ => throw new FormatException($"Unknown bplist marker 0x{marker:X2}."),
                        };
                    case 0x10:
                    {
                        var size = 1 << info;
                        if (size == 16)
                        {
                            var hi = ReadSized(pos, 8);
                            var lo = ReadSized(pos + 8, 8);
                            if (hi == 0)
                            {
                                return lo <= long.MaxValue ? (long)lo : lo;
                            }

                            throw new FormatException("128-bit bplist integers are not supported.");
                        }

                        var raw = ReadSized(pos, size);
                        return size == 8 ? (long)raw : (long)raw;
                    }

                    case 0x20:
                        return info switch
                        {
                            2 => (double)BinaryPrimitives.ReadSingleBigEndian(_data.Slice((int)pos, 4)),
                            3 => BinaryPrimitives.ReadDoubleBigEndian(_data.Slice((int)pos, 8)),
                            _ => throw new FormatException("Unknown bplist real size."),
                        };
                    case 0x30:
                        return AppleEpoch.AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(_data.Slice((int)pos, 8)));
                    case 0x40:
                    {
                        var count = ReadCount(ref pos, info);
                        return _data.Slice((int)pos, count).ToArray();
                    }

                    case 0x50:
                    {
                        var count = ReadCount(ref pos, info);
                        return Encoding.ASCII.GetString(_data.Slice((int)pos, count));
                    }

                    case 0x60:
                    {
                        var count = ReadCount(ref pos, info);
                        return Encoding.BigEndianUnicode.GetString(_data.Slice((int)pos, count * 2));
                    }

                    case 0x80:
                        return new PlistUid(ReadSized(pos, info + 1));
                    case 0xA0:
                    case 0xC0:
                    {
                        var count = ReadCount(ref pos, info);
                        var list = new List<object?>(count);
                        for (var i = 0; i < count; i++)
                        {
                            list.Add(ReadObject((long)ReadSized(pos + i * _refSize, _refSize)));
                        }

                        return list;
                    }

                    case 0xD0:
                    {
                        var count = ReadCount(ref pos, info);
                        var dict = new PlistDictionary();
                        for (var i = 0; i < count; i++)
                        {
                            var key = ReadObject((long)ReadSized(pos + i * _refSize, _refSize)) as string
                                      ?? throw new FormatException("bplist dictionary key is not a string.");
                            var value = ReadObject((long)ReadSized(pos + (count + i) * _refSize, _refSize));
                            dict[key] = value;
                        }

                        return dict;
                    }

                    default:
                        throw new FormatException($"Unknown bplist marker 0x{marker:X2}.");
                }
            }
            finally
            {
                _depth--;
            }
        }

        private int ReadCount(ref long pos, int info)
        {
            if (info != 0x0F)
            {
                return info;
            }

            var marker = _data[(int)pos];
            if ((marker & 0xF0) != 0x10)
            {
                throw new FormatException("bplist extended count is not an integer.");
            }

            var size = 1 << (marker & 0x0F);
            pos++;
            var count = ReadSized(pos, size);
            pos += size;
            if (count > int.MaxValue || (long)count > _data.Length)
            {
                throw new FormatException("bplist count out of range.");
            }

            return (int)count;
        }
    }
}
