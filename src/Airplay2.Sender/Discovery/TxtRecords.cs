using System.Globalization;

namespace Airplay2.Sender.Discovery;

/// <summary>The parsed <c>_airplay._tcp</c> TXT record (SPEC-control.md 1.2). Unknown keys stay in <see cref="Raw"/>.</summary>
public sealed record AirplayTxtRecord(
    IReadOnlyDictionary<string, string> Raw,
    AirplayFeatures Features,
    AirplayStatusFlags Flags,
    byte[]? PublicKey,
    string? PairingId,
    string? PublicPairingId,
    string? GroupId,
    bool GroupContainsGroupLeader,
    bool? IsGroupLeader,
    string? SourceVersion,
    string? Model,
    string? Manufacturer,
    string? SerialNumber,
    string? DeviceId,
    int? AccessControlLevel,
    int? AccessControlType,
    string? RequiredSenderFeatures,
    string? FirmwareVersion,
    string? ProtocolVersion,
    bool PasswordRequired)
{
    /// <summary>Parses the TXT strings (<c>key=value</c> entries, keys compared case-insensitively).</summary>
    public static AirplayTxtRecord Parse(IEnumerable<string> entries)
    {
        var raw = TxtParsing.ToDictionary(entries);
        return new AirplayTxtRecord(
            raw,
            AirplayTxtValues.ParseFeatures(raw.GetValueOrDefault("features")) ?? AirplayFeatures.None,
            AirplayTxtValues.ParseFlags(raw.GetValueOrDefault("flags")) ?? AirplayStatusFlags.None,
            TxtParsing.HexBytes(raw.GetValueOrDefault("pk")),
            raw.GetValueOrDefault("pi"),
            raw.GetValueOrDefault("psi"),
            raw.GetValueOrDefault("gid"),
            raw.GetValueOrDefault("gcgl") == "1",
            raw.TryGetValue("igl", out var igl) ? igl == "1" : null,
            raw.GetValueOrDefault("srcvers"),
            raw.GetValueOrDefault("model"),
            raw.GetValueOrDefault("manufacturer"),
            raw.GetValueOrDefault("serialNumber") ?? raw.GetValueOrDefault("serialnumber"),
            raw.GetValueOrDefault("deviceid"),
            TxtParsing.Int(raw.GetValueOrDefault("acl")),
            TxtParsing.Int(raw.GetValueOrDefault("act")),
            raw.GetValueOrDefault("rsf"),
            raw.GetValueOrDefault("fv"),
            raw.GetValueOrDefault("protovers"),
            string.Equals(raw.GetValueOrDefault("pw"), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the feature word has <paramref name="feature"/> set.</summary>
    public bool Has(AirplayFeatures feature) => (Features & feature) == feature;

    /// <summary>True when the flags have <paramref name="flag"/> set.</summary>
    public bool Has(AirplayStatusFlags flag) => (Flags & flag) == flag;
}

/// <summary>The parsed <c>_raop._tcp</c> TXT record (SPEC-control.md 1.5).</summary>
public sealed record RaopTxtRecord(
    IReadOnlyDictionary<string, string> Raw,
    IReadOnlyList<int> EncryptionTypes,
    IReadOnlyList<int> Codecs,
    IReadOnlyList<int> MetadataTypes,
    string? Transport,
    AirplayFeatures Features,
    AirplayStatusFlags Flags,
    string? Version,
    string? Model,
    int SampleRate,
    int SampleSize,
    int Channels,
    bool PasswordRequired,
    byte[]? PublicKey)
{
    /// <summary>Codec id of PCM in <c>cn</c>.</summary>
    public const int CodecPcm = 0;

    /// <summary>Codec id of ALAC in <c>cn</c>.</summary>
    public const int CodecAlac = 1;

    /// <summary>Codec id of AAC in <c>cn</c>.</summary>
    public const int CodecAac = 2;

    /// <summary>Metadata type ids in <c>md</c>.</summary>
    public const int MetadataText = 0, MetadataArtwork = 1, MetadataProgress = 2;

    /// <summary>Parses the TXT strings.</summary>
    public static RaopTxtRecord Parse(IEnumerable<string> entries)
    {
        var raw = TxtParsing.ToDictionary(entries);
        return new RaopTxtRecord(
            raw,
            TxtParsing.IntList(raw.GetValueOrDefault("et")),
            TxtParsing.IntList(raw.GetValueOrDefault("cn")),
            TxtParsing.IntList(raw.GetValueOrDefault("md")),
            raw.GetValueOrDefault("tp"),
            AirplayTxtValues.ParseFeatures(raw.GetValueOrDefault("ft")) ?? AirplayFeatures.None,
            AirplayTxtValues.ParseFlags(raw.GetValueOrDefault("sf")) ?? AirplayStatusFlags.None,
            raw.GetValueOrDefault("vs"),
            raw.GetValueOrDefault("am"),
            TxtParsing.Int(raw.GetValueOrDefault("sr")) ?? 44100,
            TxtParsing.Int(raw.GetValueOrDefault("ss")) ?? 16,
            TxtParsing.Int(raw.GetValueOrDefault("ch")) ?? 2,
            string.Equals(raw.GetValueOrDefault("pw"), "true", StringComparison.OrdinalIgnoreCase),
            TxtParsing.HexBytes(raw.GetValueOrDefault("pk")));
    }
}

internal static class TxtParsing
{
    public static IReadOnlyDictionary<string, string> ToDictionary(IEnumerable<string> entries)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var eq = entry.IndexOf('=');
            if (eq < 0)
            {
                dict.TryAdd(entry, string.Empty);
            }
            else
            {
                dict.TryAdd(entry[..eq], entry[(eq + 1)..]);
            }
        }

        return dict;
    }

    public static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static IReadOnlyList<int> IntList(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return [];
        }

        var list = new List<int>();
        foreach (var part in s.Split(','))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                list.Add(v);
            }
        }

        return list;
    }

    public static byte[]? HexBytes(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
