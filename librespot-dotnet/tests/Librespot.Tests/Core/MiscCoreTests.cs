using System.Text.Json;
using Librespot.Core;
using Librespot.Protocol.Authentication;
using Xunit;

namespace Librespot.Tests.Core;

public class MiscCoreTests
{
    [Fact]
    public void Credentials_json_matches_rust_shape_and_accepts_legacy_key()
    {
        var creds = Credentials.WithPassword("user", "pass");
        var json = JsonSerializer.Serialize(creds);
        Assert.Equal("{\"username\":\"user\",\"auth_type\":0,\"auth_data\":\"cGFzcw==\"}", json);

        var back = JsonSerializer.Deserialize<Credentials>(json);
        Assert.Equal(creds, back);

        var legacy = JsonSerializer.Deserialize<Credentials>("{\"username\":null,\"auth_type\":3,\"encoded_auth_blob\":\"dG9r\"}");
        Assert.NotNull(legacy);
        Assert.Null(legacy.Username);
        Assert.Equal(AuthenticationType.AuthenticationSpotifyToken, legacy.AuthType);
        Assert.Equal("tok"u8.ToArray(), legacy.AuthData);
    }

    [Fact]
    public void DiffieHellman_agrees_on_shared_secret()
    {
        var a = DhLocalKeys.Random();
        var b = DhLocalKeys.Random();
        Assert.Equal(a.SharedSecret(b.PublicKey()), b.SharedSecret(a.PublicKey()));
        Assert.True(a.PublicKey().Length is > 0 and <= 96);
    }

    [Fact]
    public void DiffieHellman_known_private_key_gives_generator_power()
    {
        // private key 1 => public key == generator (2)
        var k = DhLocalKeys.FromPrivateKey([1]);
        Assert.Equal(new byte[] { 2 }, k.PublicKey());
        // private key 3 => 2^3 = 8
        Assert.Equal(new byte[] { 8 }, DhLocalKeys.FromPrivateKey([3]).PublicKey());
    }

    [Fact]
    public void DeviceType_parses_and_formats_like_upstream()
    {
        Assert.Equal(DeviceType.AudioDongle, DeviceTypeExtensions.Parse("AudioDongle"));
        Assert.Null(DeviceTypeExtensions.Parse("fridge"));
        Assert.Equal("TV", DeviceType.Tv.ToDisplayString());
        Assert.Equal(Spotify.Connectstate.Devices.DeviceType.CastVideo, DeviceType.CastAudio.ToProto());
    }

    [Fact]
    public void SessionConfig_default_picks_client_id_by_platform()
    {
        Assert.Equal(Platform.AndroidClientId, SessionConfig.DefaultForOs("android").ClientId);
        Assert.Equal(Platform.IosClientId, SessionConfig.DefaultForOs("ios").ClientId);
        Assert.Equal(Platform.KeymasterClientId, SessionConfig.DefaultForOs("windows").ClientId);
        Assert.True(Guid.TryParse(SessionConfig.Default().DeviceId, out _));
    }

    [Fact]
    public void Session_data_accessors_work_without_connection()
    {
        var session = new Session(SessionConfig.Default(), null);
        Assert.Equal(32, session.SessionId.Length);
        session.SetUserAttribute("autoplay", "1");
        Assert.True(session.Autoplay);
        Assert.False(session.FilterExplicitContent);
        Assert.Null(session.SetUserAttribute("k", "v"));
        Assert.Equal("v", session.SetUserAttribute("k", "w"));
        Assert.False(session.IsInvalid);
        session.Shutdown();
        Assert.True(session.IsInvalid);
        Assert.Throws<SessionException>(() => session.SendPacket(PacketType.Pong, new byte[4]));
    }

    [Fact]
    public void Error_mapping_classifies_framework_exceptions()
    {
        Assert.Equal(ErrorKind.Cancelled, LibrespotException.From(new OperationCanceledException()).Kind);
        Assert.Equal(ErrorKind.NotFound, LibrespotException.From(new FileNotFoundException()).Kind);
        Assert.Equal(ErrorKind.FailedPrecondition, LibrespotException.From(new JsonException()).Kind);
        var pbEx = Assert.ThrowsAny<Google.Protobuf.InvalidProtocolBufferException>(() => Librespot.Protocol.Keyexchange.ClientHello.Parser.ParseFrom(new byte[] { 0xff, 0xff, 0xff }));
        Assert.Equal(ErrorKind.FailedPrecondition, LibrespotException.From(pbEx).Kind);
        Assert.Equal(ErrorKind.Unavailable, LibrespotException.From(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)).Kind);
        Assert.Equal(ErrorKind.Unknown, LibrespotException.From(new InvalidOperationException()).Kind);
    }

    [Fact]
    public void Date_from_proto_defaults_missing_month_and_day()
    {
        var d = Date.From(new Spotify.Metadata.Date { Year = 2020 });
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), d.Value);
        Assert.Equal(1577836800000, d.AsTimestampMs());
        Assert.Equal(d, Date.FromTimestampMs(1577836800000));
    }

    [Fact]
    public void PacketType_lookup()
    {
        Assert.Equal(PacketType.Ping, PacketTypeExtensions.FromByte(0x04));
        Assert.Null(PacketTypeExtensions.FromByte(0xff));
    }

    [Fact]
    public void Version_constants()
    {
        Assert.Equal("0.8.0", LibrespotVersion.SemVer);
        Assert.Equal("e023adb", LibrespotVersion.ShaShort);
        Assert.Equal(8, LibrespotVersion.BuildId.Length);
        Assert.StartsWith("librespot-", LibrespotVersion.VersionString);
    }
}
