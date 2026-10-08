using Google.Protobuf;
using Librespot.Protocol;
using Spotify.Player.Proto;
using Xunit;

namespace Librespot.Tests.Protocol;

public class ProtocolTests
{
    [Fact]
    public void Generated_messages_roundtrip()
    {
        var hello = new Librespot.Protocol.Keyexchange.ClientHello
        {
            BuildInfo = new Librespot.Protocol.Keyexchange.BuildInfo
            {
                Product = Librespot.Protocol.Keyexchange.Product.Client,
                Platform = Librespot.Protocol.Keyexchange.Platform.Win32X86,
                Version = 124200290,
            },
            ClientNonce = ByteString.CopyFrom(new byte[16]),
        };
        hello.CryptosuitesSupported.Add(Librespot.Protocol.Keyexchange.Cryptosuite.Shannon);

        var bytes = hello.ToByteArray();
        var back = Librespot.Protocol.Keyexchange.ClientHello.Parser.ParseFrom(bytes);
        Assert.Equal(hello, back);
    }

    [Fact]
    public void ContextPage_from_uris()
    {
        var page = ContextPage.FromUris(["spotify:track:a", "spotify:track:b"]);
        Assert.Equal(2, page.Tracks.Count);
        Assert.Equal("spotify:track:b", page.Tracks[1].Uri);
    }

    [Fact]
    public void Restrictions_convert_both_ways()
    {
        var r = new Restrictions();
        r.DisallowPausingReasons.Add("x");
        r.DisallowSettingModes["m"] = new ModeRestrictions { Values = { ["k"] = new RestrictionReasons { Reasons = { "why" } } } };

        var cs = r.ToConnectState();
        Assert.Equal(["x"], cs.DisallowPausingReasons);
        Assert.Equal("why", cs.DisallowSettingModes["m"].Values["k"].Reasons[0]);

        var back = cs.ToPlayerProto();
        Assert.Equal(r, back);
    }

    [Fact]
    public void PlayOrigin_proto2_optional_to_proto3()
    {
        var po = new PlayOrigin { FeatureIdentifier = "f" };
        var cs = po.ToConnectState();
        Assert.Equal("f", cs.FeatureIdentifier);
        Assert.Equal("", cs.ViewUri);
    }

    [Fact]
    public void Context_byte_hash_is_stable()
    {
        var a = new Context { Uri = "spotify:album:x" };
        var b = new Context { Uri = "spotify:album:x" };
        Assert.Equal(a.ByteHash(), b.ByteHash());
    }
}
