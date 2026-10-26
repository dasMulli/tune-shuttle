using System.Buffers.Binary;
using System.Globalization;
using Airplay2.Sender.Discovery;

namespace Airplay2.Receiver.Discovery;

/// <summary>
/// The receiver's feature word and the constants advertised with it (SPEC-receiver.md R3, R15): one place for the
/// word so the probes can change it once, plus the bit helpers the TXT and <c>/info</c> builders use.
/// </summary>
public static class ReceiverFeatures
{
    /// <summary>
    /// The feature word (SPEC-receiver.md R15): shairport-sync's default plus the metadata bits 15-17; bits 9, 11, 14, 15,
    /// 16, 17, 18, 19, 20, 22, 30, 38, 40, 41, 47, 48. TXT <c>features=0x405FCA00,0x18340</c>.
    /// </summary>
    public const ulong Default = 0x00018340405FCA00UL;

    /// <summary>
    /// The ALAC-only variant used when no AAC decoder loads (SPEC-receiver.md R3, R15, RQ11): <see cref="Default"/> with
    /// bit 20 (<c>ReceiveAudioAacLc</c>) cleared, TXT <c>features=0x404FCA00,0x18340</c>.
    /// </summary>
    public const ulong AlacOnly = Default & ~(1UL << 20);

    /// <summary>The bit cleared by <see cref="AlacOnly"/>.</summary>
    public const int AacBit = 20;

    /// <summary><c>/info</c> <c>supportedFormats.audioStream</c> (SPEC-receiver.md R3): realtime ALAC/44100/16/2.</summary>
    public const uint AudioStreamFormats = 0x40000;

    /// <summary><c>/info</c> <c>supportedFormats.bufferStream</c> with AAC (SPEC-receiver.md R3): buffered ALAC/44100/16/2 and AAC-LC/44100/2.</summary>
    public const uint BufferStreamFormats = 0x440000;

    /// <summary><c>/info</c> <c>supportedFormats.bufferStream</c> of the ALAC-only variant (SPEC-receiver.md R3).</summary>
    public const uint BufferStreamFormatsAlacOnly = 0x40000;

    /// <summary>TXT <c>model</c> / <c>am</c> and <c>/info</c> <c>model</c> (SPEC-receiver.md R15).</summary>
    public const string Model = "TuneShuttle1,1";

    /// <summary>TXT and <c>/info</c> <c>manufacturer</c> (SPEC-receiver.md R15).</summary>
    public const string Manufacturer = "TuneShuttle";

    /// <summary>TXT <c>srcvers</c> / <c>vs</c> and <c>/info</c> <c>sourceVersion</c> (SPEC-receiver.md R15).</summary>
    public const string SourceVersion = "366.0";

    /// <summary>TXT <c>protovers</c> and <c>/info</c> <c>protocolVersion</c> (SPEC-receiver.md R15).</summary>
    public const string ProtocolVersion = "1.1";

    /// <summary>The word for the decoder situation (SPEC-receiver.md R3, R4): <see cref="Default"/> with an AAC decoder, else <see cref="AlacOnly"/>.</summary>
    public static ulong For(bool aacAvailable) => aacAvailable ? Default : AlacOnly;

    /// <summary><c>supportedFormats.bufferStream</c> for the decoder situation (SPEC-receiver.md R3).</summary>
    public static uint BufferStreamFormatsFor(bool aacAvailable) => aacAvailable ? BufferStreamFormats : BufferStreamFormatsAlacOnly;

    /// <summary>The <c>_raop._tcp</c> <c>cn</c> value for the decoder situation (SPEC-receiver.md 3.1, R3): <c>0,1,2</c> with AAC, else <c>0,1</c>.</summary>
    public static string RaopCompressionFor(bool aacAvailable) => aacAvailable ? "0,1,2" : "0,1";

    /// <summary>True when <paramref name="bit"/> (0..63) is set in <paramref name="features"/>.</summary>
    public static bool Has(ulong features, int bit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, 63);
        return (features & (1UL << bit)) != 0;
    }

    /// <summary>True when every bit of <paramref name="flag"/> is set in <paramref name="features"/>.</summary>
    public static bool Has(ulong features, AirplayFeatures flag) => (features & (ulong)flag) == (ulong)flag;

    /// <summary>The set bits, ascending (the list of R15).</summary>
    public static IReadOnlyList<int> Bits(ulong features)
    {
        var bits = new List<int>();
        for (var bit = 0; bit < 64; bit++)
        {
            if ((features & (1UL << bit)) != 0)
            {
                bits.Add(bit);
            }
        }

        return bits;
    }

    /// <summary>
    /// The TXT form (SPEC-control.md 1.3, SPEC-receiver.md R15): the low 32 bits, a comma, the high 32 bits, each as
    /// <c>0x</c> plus upper-case hex without leading zeros in the high word: <c>0x405FCA00,0x18340</c>.
    /// </summary>
    public static string ToTxt(ulong features)
    {
        var low = (uint)features;
        var high = (uint)(features >> 32);
        return "0x" + low.ToString("X8", CultureInfo.InvariantCulture) + ",0x" + high.ToString("X", CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>fex</c> TXT value (SPEC-receiver.md 3.1): the word as 8 little-endian bytes, base64 without padding.</summary>
    public static string ToFex(ulong features)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, features);
        return Convert.ToBase64String(bytes).TrimEnd('=');
    }

    /// <summary>
    /// The status flags (SPEC-receiver.md R7, 4.6): bit 2 (<c>AudioLink</c>) always, bit 7 (<c>PasswordRequired</c>) with a
    /// password, bit 17 (<c>ReceiverSessionActive</c>) while a stream is set up (reflected in <c>/info</c> only).
    /// </summary>
    public static AirplayStatusFlags StatusFlagsFor(bool passwordSet, bool sessionActive)
    {
        var flags = AirplayStatusFlags.AudioLink;
        if (passwordSet)
        {
            flags |= AirplayStatusFlags.PasswordRequired;
        }

        if (sessionActive)
        {
            flags |= AirplayStatusFlags.ReceiverSessionActive;
        }

        return flags;
    }

    /// <summary>The TXT form of status flags (SPEC-control.md 1.4): <c>0x4</c>, <c>0x84</c>.</summary>
    public static string StatusFlagsToTxt(AirplayStatusFlags flags) => "0x" + ((uint)flags).ToString("X", CultureInfo.InvariantCulture);
}
