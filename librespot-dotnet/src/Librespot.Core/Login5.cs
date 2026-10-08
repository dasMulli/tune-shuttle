// Port of core/src/login5.rs.
// TODO(port: core-spclient): login5 v3 request/response with hashcash challenge solving, stored-credential
// login, token caching (Token), ClientInfo from Platform/Version constants.
using Librespot.Protocol.Authentication;

namespace Librespot.Core;

/// <summary>Upstream <c>Login5Error</c>.</summary>
public sealed class Login5Exception : LibrespotException
{
    public enum Reason
    {
        FaultyRequest,
        CodeChallenge,
        RetriesFailed,
        OnlyForMobile,
        NoStoredCredentials,
    }

    public Reason Cause { get; }

    public Login5Exception(Reason cause, ErrorKind kind, string detail, Exception? inner = null) : base(kind, detail, inner)
    {
        Cause = cause;
    }
}

/// <summary>login5 API client: exchanges stored credentials / access tokens for login5 tokens.</summary>
public sealed class Login5Manager : SessionComponent
{
    internal Login5Manager(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>login(credentials)</c>: returns (username, stored credentials blob).</summary>
    public Task<Credentials> LoginAsync(Credentials credentials, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-spclient)");

    /// <summary>Upstream <c>auth_token()</c>: a cached or freshly obtained login5 token.</summary>
    public Task<Token> AuthTokenAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-spclient)");
}
