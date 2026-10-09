using Airplay2.Sender.Tlv8;
using Xunit;

namespace Airplay2.Sender.Tests.Tlv8;

public class Tlv8Tests
{
    [Fact]
    public void Encodes_transient_m1_like_pyatv()
    {
        var doc = new Tlv8Document().AddMethod(TlvMethod.PairSetup).AddState(1).Add(TlvType.Flags, (byte)TlvFlags.Transient);
        Assert.Equal(Convert.FromHexString("000100060101130110"), doc.Encode());
    }

    [Fact]
    public void Fragments_values_longer_than_255_and_merges_them_back()
    {
        var value = new byte[384];
        for (var i = 0; i < value.Length; i++)
        {
            value[i] = (byte)i;
        }

        var encoded = new Tlv8Document().AddState(2).Add(TlvType.Salt, new byte[16]).Add(TlvType.PublicKey, value).Encode();
        Assert.Equal(3 + 18 + (2 + 255) + (2 + 129), encoded.Length);
        Assert.Equal((byte)TlvType.PublicKey, encoded[3 + 18]);
        Assert.Equal(255, encoded[3 + 18 + 1]);
        Assert.Equal((byte)TlvType.PublicKey, encoded[3 + 18 + 257]);
        Assert.Equal(129, encoded[3 + 18 + 258]);

        var decoded = Tlv8Document.Decode(encoded);
        Assert.Equal(3, decoded.Items.Count);
        Assert.Equal(2, decoded.State);
        Assert.Equal(value, decoded.Get(TlvType.PublicKey));
        Assert.Equal(new byte[16], decoded.Get(TlvType.Salt));
    }

    [Fact]
    public void Exact_255_value_followed_by_same_type_is_still_merged_but_other_type_is_not()
    {
        var a = new byte[255];
        var b = new byte[3];
        var encoded = new Tlv8Document().Add(TlvType.EncryptedData, a).Add(TlvType.State, (byte)5).Encode();
        var decoded = Tlv8Document.Decode(encoded);
        Assert.Equal(2, decoded.Items.Count);
        Assert.Equal(255, decoded.Get(TlvType.EncryptedData)!.Length);
        Assert.Equal(5, decoded.State);

        var raw = encoded.Concat(new Tlv8Document().Add(TlvType.State, b).Encode()).ToArray();
        Assert.Equal(3, Tlv8Document.Decode(raw).Items.Count);
    }

    [Fact]
    public void Empty_value_and_error_item()
    {
        var doc = Tlv8Document.Decode(Convert.FromHexString("0100" + "060104" + "070102"));
        Assert.Equal(Array.Empty<byte>(), doc.Get(TlvType.Identifier));
        Assert.Equal(4, doc.State);
        Assert.Equal(TlvError.Authentication, doc.Error);
        Assert.Equal(Convert.FromHexString("0100060104070102"), doc.Encode());
    }

    [Fact]
    public void Truncated_input_throws()
    {
        Assert.Throws<FormatException>(() => Tlv8Document.Decode(Convert.FromHexString("0305aabb")));
        Assert.Throws<FormatException>(() => Tlv8Document.Decode(Convert.FromHexString("03")));
    }
}
