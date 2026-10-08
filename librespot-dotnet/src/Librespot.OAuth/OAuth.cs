// Port of oauth/src/lib.rs. Only the public contract is in place; see docs/PORT-PLAN.md package `oauth`.
namespace Librespot.OAuth;

/// <summary>Represents an OAuth token used for accessing Spotify's Web API and sessions.</summary>
/// <param name="AccessToken">Bearer token used for authenticated Spotify API requests.</param>
/// <param name="RefreshToken">Long-lived token used to obtain new access tokens.</param>
/// <param name="ExpiresAt">Instant when the access token becomes invalid.</param>
/// <param name="TokenType">Type of token (usually "Bearer").</param>
/// <param name="Scopes">Permission scopes granted by this token.</param>
public sealed record OAuthToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string TokenType,
    IReadOnlyList<string> Scopes);

/// <summary>
/// Obtains a Spotify access token via the authorization-code flow with PKCE
/// (upstream <c>OAuthClient</c>). Build one with <see cref="OAuthClientBuilder"/>.
/// </summary>
public sealed class OAuthClient
{
    internal OAuthClient(string clientId, Uri redirectUri, IReadOnlyList<string> scopes, bool shouldOpenUrl, string message)
    {
        ClientId = clientId;
        RedirectUri = redirectUri;
        Scopes = scopes;
        ShouldOpenUrl = shouldOpenUrl;
        Message = message;
    }

    public string ClientId { get; }
    public Uri RedirectUri { get; }
    public IReadOnlyList<string> Scopes { get; }
    public bool ShouldOpenUrl { get; }
    public string Message { get; }

    /// <summary>Synchronously obtain a token (upstream <c>get_access_token</c>).</summary>
    public OAuthToken GetAccessToken() => GetAccessTokenAsync().GetAwaiter().GetResult();

    /// <summary>Synchronously refresh a token (upstream <c>refresh_token</c>).</summary>
    public OAuthToken RefreshToken(string refreshToken) => RefreshTokenAsync(refreshToken).GetAwaiter().GetResult();

    /// <summary>Upstream <c>get_access_token_async</c>: opens the browser (or prints the URL), listens for the redirect, exchanges the code.</summary>
    public Task<OAuthToken> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: oauth)");

    /// <summary>Upstream <c>refresh_token_async</c>.</summary>
    public Task<OAuthToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: oauth)");
}

/// <summary>Builder for <see cref="OAuthClient"/> (upstream <c>OAuthClientBuilder</c>).</summary>
public sealed class OAuthClientBuilder
{
    private readonly string _clientId;
    private readonly string _redirectUri;
    private readonly IReadOnlyList<string> _scopes;
    private bool _openInBrowser;
    private string _message = "Go to the following URL to continue authentication:";

    public OAuthClientBuilder(string clientId, string redirectUri, IEnumerable<string> scopes)
    {
        _clientId = clientId;
        _redirectUri = redirectUri;
        _scopes = scopes.ToList();
    }

    /// <summary>Open the authorization URL in the system browser instead of only printing it.</summary>
    public OAuthClientBuilder OpenInBrowser()
    {
        _openInBrowser = true;
        return this;
    }

    /// <summary>Custom message printed before the authorization URL.</summary>
    public OAuthClientBuilder WithCustomMessage(string message)
    {
        _message = message;
        return this;
    }

    /// <summary>Validates the redirect URI and builds the client.</summary>
    public OAuthClient Build()
    {
        if (!Uri.TryCreate(_redirectUri, UriKind.Absolute, out var redirect))
        {
            throw OAuthException.InvalidRedirectUri(_redirectUri, new UriFormatException(_redirectUri));
        }

        return new OAuthClient(_clientId, redirect, _scopes, _openInBrowser, _message);
    }
}

/// <summary>Upstream free function <c>get_access_token(client_id, redirect_uri, scopes)</c>: one-shot convenience.</summary>
public static class OAuthFlow
{
    public static OAuthToken GetAccessToken(string clientId, string redirectUri, IEnumerable<string> scopes) =>
        new OAuthClientBuilder(clientId, redirectUri, scopes).OpenInBrowser().Build().GetAccessToken();
}

/// <summary>Result of the device-authorization request (upstream <c>DeviceAuthorization</c>).</summary>
public sealed class DeviceAuthorization
{
    public DeviceAuthorization(string deviceCode, string userCode, string verificationUri, string? verificationUriComplete, TimeSpan interval, DateTimeOffset expiresAt)
    {
        DeviceCode = deviceCode;
        UserCode = userCode;
        VerificationUri = verificationUri;
        VerificationUriComplete = verificationUriComplete;
        Interval = interval;
        ExpiresAt = expiresAt;
    }

    internal string DeviceCode { get; }
    /// <summary>Short code the user types in at the verification URI.</summary>
    public string UserCode { get; }
    /// <summary>Where the user has to go (usually https://spotify.com/pair).</summary>
    public string VerificationUri { get; }
    /// <summary>Verification URI with the code pre-filled, when Spotify provides one.</summary>
    public string? VerificationUriComplete { get; }
    public TimeSpan Interval { get; }
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>The URL to show or open: <see cref="VerificationUriComplete"/> if present, else <see cref="VerificationUri"/>.</summary>
    public string Url => VerificationUriComplete ?? VerificationUri;
}

/// <summary>Obtains a token via the device-authorization flow (upstream <c>DeviceAuthClient</c>).</summary>
public sealed class DeviceAuthClient
{
    internal DeviceAuthClient(string clientId, IReadOnlyList<string> scopes, bool shouldOpenUrl)
    {
        ClientId = clientId;
        Scopes = scopes;
        ShouldOpenUrl = shouldOpenUrl;
    }

    public string ClientId { get; }
    public IReadOnlyList<string> Scopes { get; }
    public bool ShouldOpenUrl { get; }

    public DeviceAuthorization RequestDeviceCode() => RequestDeviceCodeAsync().GetAwaiter().GetResult();
    public OAuthToken PollForToken(DeviceAuthorization auth) => PollForTokenAsync(auth).GetAwaiter().GetResult();
    public OAuthToken GetAccessToken() => GetAccessTokenAsync().GetAwaiter().GetResult();
    public OAuthToken RefreshToken(string refreshToken) => RefreshTokenAsync(refreshToken).GetAwaiter().GetResult();

    public Task<DeviceAuthorization> RequestDeviceCodeAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: oauth)");

    public Task<OAuthToken> PollForTokenAsync(DeviceAuthorization auth, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: oauth)");

    public Task<OAuthToken> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: oauth)");

    public Task<OAuthToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: oauth)");
}

/// <summary>Builder for <see cref="DeviceAuthClient"/>.</summary>
public sealed class DeviceAuthClientBuilder
{
    private readonly string _clientId;
    private readonly IReadOnlyList<string> _scopes;
    private bool _openInBrowser;

    public DeviceAuthClientBuilder(string clientId, IEnumerable<string> scopes)
    {
        _clientId = clientId;
        _scopes = scopes.ToList();
    }

    public DeviceAuthClientBuilder OpenInBrowser()
    {
        _openInBrowser = true;
        return this;
    }

    public DeviceAuthClient Build() => new(_clientId, _scopes, _openInBrowser);
}
