using System.Diagnostics.CodeAnalysis;

namespace Airplay2.Sender.Tlv8;

/// <summary>TLV8 item types used by HomeKit pairing (SPEC-control.md 3.2).</summary>
public enum TlvType : byte
{
    Method = 0x00,
    Identifier = 0x01,
    Salt = 0x02,
    PublicKey = 0x03,
    Proof = 0x04,
    EncryptedData = 0x05,
    State = 0x06,
    Error = 0x07,
    RetryDelay = 0x08,
    Certificate = 0x09,
    Signature = 0x0A,
    Permissions = 0x0B,
    FragmentData = 0x0C,
    FragmentLast = 0x0D,
    Name = 0x11,
    Flags = 0x13,
    Separator = 0xFF,
}

/// <summary>Values of the <see cref="TlvType.Method"/> item.</summary>
public enum TlvMethod : byte
{
    PairSetup = 0,
    PairSetupWithAuth = 1,
    PairVerify = 2,
    AddPairing = 3,
    RemovePairing = 4,
    ListPairings = 5,
}

/// <summary>Values of the <see cref="TlvType.Error"/> item.</summary>
public enum TlvError : byte
{
    Unknown = 1,
    Authentication = 2,
    BackOff = 3,
    MaxPeers = 4,
    MaxTries = 5,
    Unavailable = 6,
    Busy = 7,
}

/// <summary>Values of the <see cref="TlvType.Flags"/> item.</summary>
[Flags]
public enum TlvFlags : byte
{
    None = 0,
    Transient = 0x10,
}

/// <summary>
/// An ordered list of TLV8 items. Encoding splits values longer than 255 bytes into consecutive items of the same type;
/// decoding merges consecutive items of the same type back into one value, as every HAP implementation does.
/// </summary>
public sealed class Tlv8Document
{
    /// <summary>Largest value that fits in one TLV8 item.</summary>
    public const int MaxItemLength = 255;

    private readonly List<(TlvType Type, byte[] Value)> _items = [];

    /// <summary>The items in document order (fragments already merged).</summary>
    public IReadOnlyList<(TlvType Type, byte[] Value)> Items => _items;

    /// <summary>Appends an item.</summary>
    public Tlv8Document Add(TlvType type, ReadOnlySpan<byte> value)
    {
        _items.Add((type, value.ToArray()));
        return this;
    }

    /// <summary>Appends a one-byte item.</summary>
    public Tlv8Document Add(TlvType type, byte value) => Add(type, [value]);

    /// <summary>Appends a UTF-8 string item.</summary>
    public Tlv8Document Add(TlvType type, string value) => Add(type, System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>Appends the <c>State</c> item (M1..M6 = 1..6).</summary>
    public Tlv8Document AddState(byte state) => Add(TlvType.State, state);

    /// <summary>Appends the <c>Method</c> item.</summary>
    public Tlv8Document AddMethod(TlvMethod method) => Add(TlvType.Method, (byte)method);

    /// <summary>The first (merged) value of <paramref name="type"/>, or null.</summary>
    public byte[]? Get(TlvType type)
    {
        foreach (var (t, v) in _items)
        {
            if (t == type)
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>Tries to get the merged value of <paramref name="type"/>.</summary>
    public bool TryGet(TlvType type, [NotNullWhen(true)] out byte[]? value)
    {
        value = Get(type);
        return value is not null;
    }

    /// <summary>True when the document has an item of <paramref name="type"/>.</summary>
    public bool Contains(TlvType type) => Get(type) is not null;

    /// <summary>The <c>State</c> byte, or 0 when absent.</summary>
    public byte State => Get(TlvType.State) is { Length: > 0 } s ? s[0] : (byte)0;

    /// <summary>The <c>Error</c> code, or null when the document has no error item.</summary>
    public TlvError? Error => Get(TlvType.Error) is { Length: > 0 } e ? (TlvError)e[0] : null;

    /// <summary>Encodes the document, fragmenting long values.</summary>
    public byte[] Encode()
    {
        var size = 0;
        foreach (var (_, v) in _items)
        {
            size += v.Length == 0 ? 2 : (v.Length + MaxItemLength - 1) / MaxItemLength * 2 + v.Length;
        }

        var output = new byte[size];
        var pos = 0;
        foreach (var (t, v) in _items)
        {
            if (v.Length == 0)
            {
                output[pos++] = (byte)t;
                output[pos++] = 0;
                continue;
            }

            var offset = 0;
            while (offset < v.Length)
            {
                var chunk = Math.Min(MaxItemLength, v.Length - offset);
                output[pos++] = (byte)t;
                output[pos++] = (byte)chunk;
                v.AsSpan(offset, chunk).CopyTo(output.AsSpan(pos));
                pos += chunk;
                offset += chunk;
            }
        }

        return output;
    }

    /// <summary>Decodes a TLV8 byte string, merging consecutive fragments of the same type.</summary>
    /// <exception cref="FormatException">The data is truncated.</exception>
    public static Tlv8Document Decode(ReadOnlySpan<byte> data)
    {
        var doc = new Tlv8Document();
        var pos = 0;
        TlvType? lastType = null;
        while (pos < data.Length)
        {
            if (pos + 2 > data.Length)
            {
                throw new FormatException("Truncated TLV8 item header.");
            }

            var type = (TlvType)data[pos];
            int len = data[pos + 1];
            pos += 2;
            if (pos + len > data.Length)
            {
                throw new FormatException($"Truncated TLV8 value for type 0x{(byte)type:X2}.");
            }

            var value = data.Slice(pos, len);
            pos += len;

            if (lastType == type && doc._items.Count > 0 && doc._items[^1].Value.Length % MaxItemLength == 0 && doc._items[^1].Value.Length > 0)
            {
                // continuation of a fragmented value
                var merged = new byte[doc._items[^1].Value.Length + len];
                doc._items[^1].Value.CopyTo(merged, 0);
                value.CopyTo(merged.AsSpan(doc._items[^1].Value.Length));
                doc._items[^1] = (type, merged);
            }
            else
            {
                doc._items.Add((type, value.ToArray()));
            }

            lastType = type;
        }

        return doc;
    }
}
