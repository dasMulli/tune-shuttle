// Port of core/src/authentication.rs.
using System.Buffers.Binary;
using System.Security.Cryptography;
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
    /// <param name="username">The username the blob belongs to (PBKDF2 salt).</param>
    /// <param name="encryptedBlob">The base64 text of the encrypted blob.</param>
    /// <param name="deviceId">The device id the blob was encrypted for.</param>
    /// <exception cref="AuthenticationException">The blob contains an unknown authentication type.</exception>
    /// <exception cref="LibrespotException">The blob is not valid base64 or is truncated (kind FailedPrecondition).</exception>
    public static Credentials WithBlob(string username, ReadOnlySpan<byte> encryptedBlob, ReadOnlySpan<byte> deviceId)
    {
        var secret = SHA1.HashData(deviceId);

        var key = new byte[24];
        if (key.Length < 20)
        {
            throw AuthenticationException.Key();
        }

        var derived = Rfc2898DeriveBytes.Pbkdf2(secret, Encoding.UTF8.GetBytes(username), 0x100, HashAlgorithmName.SHA1, 20);
        SHA1.HashData(derived, key.AsSpan(0, 20));
        BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(20), 20);

        // decrypt data using ECB mode without padding
        byte[] data;
        try
        {
            data = Convert.FromBase64String(Encoding.ASCII.GetString(encryptedBlob));
        }
        catch (FormatException e)
        {
            throw LibrespotException.From(e);
        }

        using (var aes = Aes.Create())
        {
            aes.Key = key;

            // Upstream decrypts `chunks_exact_mut(block_size)`: a trailing partial block stays as it is.
            var full = data.Length - (data.Length % 16);
            if (full > 0)
            {
                aes.DecryptEcb(data.AsSpan(0, full), data.AsSpan(0, full), PaddingMode.None);
            }
        }

        var l = data.Length;
        for (var i = 0; i < l - 0x10; i++)
        {
            data[l - i - 1] ^= data[l - i - 0x11];
        }

        var reader = new BlobReader(data);
        reader.ReadU8();
        reader.ReadBytes();
        reader.ReadU8();
        var authType = reader.ReadInt();
        if (!Enum.IsDefined(typeof(AuthenticationType), (int)authType))
        {
            throw AuthenticationException.AuthType(authType);
        }

        reader.ReadU8();
        var authData = reader.ReadBytes();

        return new Credentials
        {
            Username = username,
            AuthType = (AuthenticationType)(int)authType,
            AuthData = authData,
        };
    }

    /// <summary>The <c>read_u8</c>/<c>read_int</c>/<c>read_bytes</c> helpers of upstream <c>with_blob</c>.</summary>
    private ref struct BlobReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public byte ReadU8()
        {
            if (_position >= _data.Length)
            {
                // Upstream: io::ErrorKind::UnexpectedEof -> ErrorKind::FailedPrecondition.
                throw LibrespotException.From(new EndOfStreamException("failed to fill whole buffer"));
            }

            return _data[_position++];
        }

        public uint ReadInt()
        {
            uint lo = ReadU8();
            if ((lo & 0x80) == 0)
            {
                return lo;
            }

            uint hi = ReadU8();
            return (lo & 0x7f) | (hi << 7);
        }

        public byte[] ReadBytes()
        {
            var length = ReadInt();
            if (length > (uint)(_data.Length - _position))
            {
                throw LibrespotException.From(new EndOfStreamException("failed to fill whole buffer"));
            }

            var result = _data.Slice(_position, (int)length).ToArray();
            _position += (int)length;
            return result;
        }
    }

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
