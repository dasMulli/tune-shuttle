// Port of core/src/token.rs.
// TODO(port: core-spclient): keymaster token request via mercury (hm://keymaster/token/authenticated?scope=...&client_id=...&device_id=...),
// token cache with expiry (EXPIRY_THRESHOLD 10 s).
namespace Librespot.Core;

/// <summary>Upstream <c>TokenError::Empty</c> ("no tokens available"), kind Unavailable.</summary>
public sealed class TokenException() : LibrespotException(ErrorKind.Unavailable, "no tokens available");

/// <summary>An access token obtained from keymaster / login5.</summary>
public sealed record Token(string AccessToken, TimeSpan ExpiresIn, string TokenType, IReadOnlyList<string> Scopes, DateTimeOffset Timestamp)
{
    private static readonly TimeSpan ExpiryThreshold = TimeSpan.FromSeconds(10);

    /// <summary>Parses the keymaster JSON response ({accessToken, expiresIn, tokenType, scope}).</summary>
    public static Token FromJson(string body) => throw new NotImplementedException("TODO(port: core-spclient)");

    public bool IsExpired => Timestamp + ExpiresIn - ExpiryThreshold < DateTimeOffset.UtcNow;

    public bool InScope(string scope) => Scopes.Contains(scope);

    public bool InScopes(IEnumerable<string> scopes) => scopes.All(InScope);
}

/// <summary>Caches and refreshes tokens per scope set.</summary>
public sealed class TokenProvider : SessionComponent
{
    internal TokenProvider(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>get_token(scopes)</c> with scopes comma-separated.</summary>
    public Task<Token> GetTokenAsync(string scopes, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-spclient)");
}
