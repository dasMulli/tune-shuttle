// Port of core/src/authentication.rs.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Librespot.Protocol.Authentication;

namespace Librespot.Core;

/// <summary>Upstream <c>authentication::AuthenticationError</c>; always <see cref="ErrorKind.InvalidArgument"/>.</summary>
public sealed class AuthenticationException : LibrespotException
{
    public enum Reason
    {
        /// <summary>Unknown authentication type in a credential blob.</summary>
        AuthType,
        /// <summary>Invalid key.</summary>
        Key,
    }

    public Reason Cause { get; }

    private AuthenticationException(Reason cause, string detail)
        : base(ErrorKind.InvalidArgument, detail)
    {
        Cause = cause;
    }

    public static AuthenticationException AuthType(uint value) => new(Reason.AuthType, $"unknown authentication type {value}");

    public static AuthenticationException Key() => new(Reason.Key, "invalid key");
}

/// <summary>
/// The credentials used to log into the Spotify API. Serialises to the same JSON as upstream
/// (<c>{"username": ..., "auth_type": &lt;int&gt;, "auth_data": "&lt;base64&gt;"}</c>, accepting the legacy
/// <c>encoded_auth_blob</c> key), so credential caches are interchangeable with the Rust binary.
/// </summary>
[JsonConverter(typeof(CredentialsJsonConverter))]
public sealed record Credentials
{
    public string? Username { get; init; }

    public AuthenticationType AuthType { get; init; } = AuthenticationType.AuthenticationUserPass;

    public byte[] AuthData { get; init; } = [];

    /// <summary>Initialise these credentials from a username and a password.</summary>
    public static Credentials WithPassword(string username, string password) => new()
    {
        Username = username,
        AuthType = AuthenticationType.AuthenticationUserPass,
        AuthData = Encoding.UTF8.GetBytes(password),
    };

    /// <summary>Initialise these credentials from an OAuth access token.</summary>
    public static Credentials WithAccessToken(string token) => new()
    {
        Username = null,
        AuthType = AuthenticationType.AuthenticationSpotifyToken,
        AuthData = Encoding.UTF8.GetBytes(token),
    };

    /// <summary>
    /// Decrypts a credential blob as produced by Spotify Connect discovery (upstream <c>with_blob</c>):
    /// PBKDF2-HMAC-SHA1(SHA1(device_id), username, 256 iterations) -> SHA1 -> AES-192-ECB decrypt + unchain.
    /// </summary>
    public static Credentials WithBlob(string username, ReadOnlySpan<byte> encryptedBlob, ReadOnlySpan<byte> deviceId) =>
        throw new NotImplementedException("TODO(port: core-connection)");

    public bool Equals(Credentials? other) =>
        other is not null && Username == other.Username && AuthType == other.AuthType && AuthData.AsSpan().SequenceEqual(other.AuthData);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Username);
        h.Add(AuthType);
        h.AddBytes(AuthData);
        return h.ToHashCode();
    }
}

/// <summary>JSON shape identical to upstream serde output.</summary>
public sealed class CredentialsJsonConverter : JsonConverter<Credentials>
{
    public override Credentials Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("expected object");
        }

        string? username = null;
        var authType = AuthenticationType.AuthenticationUserPass;
        byte[] authData = [];

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "username":
                    username = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "auth_type":
                    var v = reader.GetInt32();
                    if (!Enum.IsDefined(typeof(AuthenticationType), v))
                    {
                        throw new JsonException("Invalid enum value");
                    }

                    authType = (AuthenticationType)v;
                    break;
                case "auth_data":
                case "encoded_auth_blob":
                    authData = Convert.FromBase64String(reader.GetString() ?? "");
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new Credentials { Username = username, AuthType = authType, AuthData = authData };
    }

    public override void Write(Utf8JsonWriter writer, Credentials value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Username is null)
        {
            writer.WriteNull("username");
        }
        else
        {
            writer.WriteString("username", value.Username);
        }

        writer.WriteNumber("auth_type", (int)value.AuthType);
        writer.WriteString("auth_data", Convert.ToBase64String(value.AuthData));
        writer.WriteEndObject();
    }
}
