// Port of core/src/token.rs.
// Ported from librespot-java. Relicensed under MIT with permission.
//
// Known scopes:
//   ugc-image-upload, playlist-read-collaborative, playlist-modify-private,
//   playlist-modify-public, playlist-read-private, user-read-playback-position,
//   user-read-recently-played, user-top-read, user-modify-playback-state,
//   user-read-currently-playing, user-read-playback-state, user-read-private, user-read-email,
//   user-library-modify, user-library-read, user-follow-modify, user-follow-read, streaming,
//   app-remote-control
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Core;

/// <summary>Upstream <c>TokenError</c>; every variant has kind <see cref="ErrorKind.Unavailable"/>.</summary>
public sealed class TokenException : LibrespotException
{
    /// <summary>The upstream variant.</summary>
    public enum Reason
    {
        /// <summary>"no tokens available".</summary>
        Empty,
    }

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    /// <summary>Creates the error; the kind is fixed like upstream <c>impl From&lt;TokenError&gt; for Error</c> (prefer the static factories).</summary>
    public TokenException(Reason cause, string detail, Exception? inner = null) : base(ErrorKind.Unavailable, detail, inner)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>TokenError::Empty</c>.</summary>
    public static TokenException Empty() => new(Reason.Empty, "no tokens available");
}

/// <summary>An access token obtained from keymaster, login5 or the client-token endpoint (upstream <c>Token</c>).</summary>
/// <param name="AccessToken">The bearer token itself.</param>
/// <param name="ExpiresIn">Lifetime counted from <paramref name="Timestamp"/>.</param>
/// <param name="TokenType">Usually <c>Bearer</c> (login5, keymaster) or <c>client-token</c>.</param>
/// <param name="Scopes">The scopes (keymaster) or domains (client token) the token is valid for.</param>
/// <param name="Timestamp">When the token was received.</param>
public sealed record Token(string AccessToken, TimeSpan ExpiresIn, string TokenType, IReadOnlyList<string> Scopes, DateTimeOffset Timestamp)
{
    /// <summary>A token is treated as expired this long before it actually expires (upstream <c>EXPIRY_THRESHOLD</c>).</summary>
    public static readonly TimeSpan ExpiryThreshold = TimeSpan.FromSeconds(10);

    /// <summary>Upstream <c>TokenData</c>: the keymaster JSON body (camelCase keys, all required).</summary>
    private sealed record TokenData
    {
        [JsonPropertyName("accessToken")]
        [JsonRequired]
        public string AccessToken { get; init; } = "";

        [JsonPropertyName("expiresIn")]
        [JsonRequired]
        public ulong ExpiresIn { get; init; }

        [JsonPropertyName("tokenType")]
        [JsonRequired]
        public string TokenType { get; init; } = "";

        [JsonPropertyName("scope")]
        [JsonRequired]
        public List<string> Scope { get; init; } = [];
    }

    /// <summary>Upstream <c>Token::from_json</c>: parses the keymaster JSON response (<c>{accessToken, expiresIn, tokenType, scope}</c>).</summary>
    /// <exception cref="LibrespotException">Malformed JSON or a missing field (kind <see cref="ErrorKind.FailedPrecondition"/>).</exception>
    public static Token FromJson(string body)
    {
        TokenData data;
        try
        {
            data = JsonSerializer.Deserialize<TokenData>(body)
                ?? throw new JsonException("invalid type: null, expected struct TokenData");
        }
        catch (JsonException e)
        {
            throw LibrespotException.From(e);
        }

        TimeSpan expiresIn;
        try
        {
            expiresIn = TimeSpan.FromSeconds(checked((long)data.ExpiresIn));
        }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException)
        {
            throw LibrespotException.FailedPrecondition(e);
        }

        return new Token(data.AccessToken, expiresIn, data.TokenType, data.Scope, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Upstream <c>is_expired</c>: <c>timestamp + expires_in.saturating_sub(EXPIRY_THRESHOLD) &lt; now</c>.
    /// </summary>
    public bool IsExpired
    {
        get
        {
            var lifetime = ExpiresIn > ExpiryThreshold ? ExpiresIn - ExpiryThreshold : TimeSpan.Zero;
            var expiry = DateTimeOffset.MaxValue - Timestamp < lifetime ? DateTimeOffset.MaxValue : Timestamp + lifetime;
            return expiry < DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Upstream <c>in_scope</c>: whether <paramref name="scope"/> is one of <see cref="Scopes"/>.</summary>
    public bool InScope(string scope) => Scopes.Contains(scope);

    /// <summary>Upstream <c>in_scopes</c>: whether every one of <paramref name="scopes"/> is in <see cref="Scopes"/>.</summary>
    public bool InScopes(IEnumerable<string> scopes) => scopes.All(InScope);
}

/// <summary>Caches and refreshes keymaster tokens per scope set (upstream <c>TokenProvider</c>).</summary>
public sealed class TokenProvider : SessionComponent
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<TokenProvider>();

    private readonly List<Token> _tokens = [];

    internal TokenProvider(Session session) : base(session)
    {
    }

    /// <summary>
    /// Upstream <c>get_token(scopes)</c> with the session's client id; <paramref name="scopes"/> must be comma-separated.
    /// </summary>
    /// <remarks>
    /// Not all combinations of scopes and client ID are allowed. Depending on the client ID currently used, the
    /// function may return an error for specific scopes. In this case <see cref="GetTokenWithClientIdAsync"/> can be
    /// used, where an appropriate client ID can be provided.
    /// </remarks>
    public Task<Token> GetTokenAsync(string scopes, CancellationToken cancellationToken = default) =>
        GetTokenWithClientIdAsync(scopes, Session.ClientId, cancellationToken);

    /// <summary>
    /// Upstream <c>get_token_with_client_id(scopes, client_id)</c>: a cached token covering all
    /// <paramref name="scopes"/> (comma-separated) or a fresh one from keymaster via mercury.
    /// </summary>
    /// <exception cref="LibrespotException">Empty client id (InvalidArgument), mercury failure, empty response
    /// (<see cref="TokenException"/>) or malformed JSON.</exception>
    public async Task<Token> GetTokenWithClientIdAsync(string scopes, string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            throw LibrespotException.InvalidArgument("Client ID cannot be empty");
        }

        var wanted = scopes.Split(',');
        lock (Lock)
        {
            // Upstream finds the index and removes/clones it under separate locks; one critical section is equivalent.
            var index = _tokens.FindIndex(t => t.InScopes(wanted));
            if (index >= 0)
            {
                var cachedToken = _tokens[index];
                if (cachedToken.IsExpired)
                {
                    _tokens.RemoveAt(index);
                }
                else
                {
                    return cachedToken;
                }
            }
        }

        Log.LogTrace("Requested token in scopes {Scopes} unavailable or expired, requesting new token.", scopes);

        var queryUri = $"hm://keymaster/token/authenticated?scope={scopes}&client_id={clientId}&device_id={Session.DeviceId}";
        var response = await Session.Mercury.GetAsync(queryUri, cancellationToken).ConfigureAwait(false);
        if (response.Payload.Count == 0)
        {
            throw TokenException.Empty();
        }

        string json;
        try
        {
            json = SpClientUtil.StrictUtf8.GetString(response.Payload[0]);
        }
        catch (DecoderFallbackException e)
        {
            throw LibrespotException.From(e);
        }

        var token = Token.FromJson(json);
        Log.LogTrace("Got token: {Token}", token);
        lock (Lock)
        {
            _tokens.Add(token);
        }

        return token;
    }
}
