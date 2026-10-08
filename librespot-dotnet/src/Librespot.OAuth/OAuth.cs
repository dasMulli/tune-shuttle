// Port of oauth/src/lib.rs. The `OAuthError` enum lives in OAuthError.cs, the logging hook in OAuthLog.cs.
//
// Upstream builds on the `oauth2` crate (BasicClient + reqwest). There is no equivalent in the BCL, so the parts of
// oauth2 v5 that librespot uses are hand-written here and keep its wire format and error texts:
//   * PKCE (`PkceCodeChallenge::new_random_sha256`): 32 random bytes, base64url without padding (43 chars), S256.
//   * CSRF state (`CsrfToken::new_random`): 16 random bytes, base64url without padding.
//   * Authorize URL (`AuthorizationRequest::url`): response_type, client_id, state, code_challenge,
//     code_challenge_method, redirect_uri, scope - form-urlencoded and appended to AUTH_URL in that order.
//   * Token endpoint (`endpoint_request` / `endpoint_response`): form POST with `Accept: application/json`, client id
//     in the body (no client secret), status / Content-Type / body checks and `RequestTokenError` messages.
//   * Device flow (`DeviceAccessTokenRequest::request`): poll immediately, keep the interval on
//     `authorization_pending`, add 5 s on `slow_down`, double (max 10 s) on transport errors, fail with
//     `expired_token` once `expires_in` (counted from the first poll) has passed.
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

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
    IReadOnlyList<string> Scopes)
{
    // Renders Scopes as a list (like upstream's `{:#?}`) instead of the collection's type name.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AccessToken = ").Append(AccessToken)
            .Append(", RefreshToken = ").Append(RefreshToken)
            .Append(", ExpiresAt = ").Append(ExpiresAt.ToString("O", CultureInfo.InvariantCulture))
            .Append(", TokenType = ").Append(TokenType)
            .Append(", Scopes = [").AppendJoin(", ", Scopes).Append(']');
        return true;
    }
}

/// <summary>
/// Obtains and refreshes a Spotify access token via the authorization-code flow with PKCE
/// (upstream <c>OAuthClient</c>). Build one with <see cref="OAuthClientBuilder"/>.
/// </summary>
public sealed class OAuthClient : IDisposable
{
    private readonly TokenEndpoint _endpoint;

    internal OAuthClient(
        string clientId,
        Uri redirectUri,
        IReadOnlyList<string> scopes,
        bool shouldOpenUrl,
        string message,
        HttpMessageHandler? handler = null)
    {
        ClientId = clientId;
        RedirectUri = redirectUri;
        Scopes = scopes;
        ShouldOpenUrl = shouldOpenUrl;
        Message = message;
        _endpoint = new TokenEndpoint(handler);
    }

    private static readonly ILogger Log = OAuthLog.CreateLogger<OAuthClient>();

    /// <summary>The Spotify client id the tokens are requested for.</summary>
    public string ClientId { get; }

    /// <summary>The redirect URI registered for <see cref="ClientId"/>; its <see cref="Uri.OriginalString"/> is what is sent.</summary>
    public Uri RedirectUri { get; }

    /// <summary>The scopes requested in the authorization URL.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>Whether the authorization URL is also opened in the default web browser.</summary>
    public bool ShouldOpenUrl { get; }

    /// <summary>Body of the HTTP response sent to the browser once the redirect has been received.</summary>
    public string Message { get; }

    /// <summary>Where the redirect URL is read from when it does not name a local http port (test seam; default stdin).</summary>
    internal TextReader? Input { get; set; }

    /// <summary>Where the authorization URL and prompts are printed (test seam; default stdout).</summary>
    internal TextWriter? Output { get; set; }

    /// <summary>
    /// Synchronously obtain a Spotify access token using the authorization code with PKCE OAuth flow
    /// (upstream <c>get_access_token</c>).
    /// </summary>
    /// <exception cref="OAuthException">The code could not be obtained or exchanged.</exception>
    public OAuthToken GetAccessToken() => GetAccessTokenAsync().GetAwaiter().GetResult();

    /// <summary>Synchronously obtain a new valid OAuth token from <paramref name="refreshToken"/> (upstream <c>refresh_token</c>).</summary>
    /// <exception cref="OAuthException">The refresh failed.</exception>
    public OAuthToken RefreshToken(string refreshToken) => RefreshTokenAsync(refreshToken).GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously obtain a Spotify access token using the authorization code with PKCE OAuth flow
    /// (upstream <c>get_access_token_async</c>): shows (and optionally opens) the authorization URL, collects the code
    /// from a local listener on the redirect URI's port (or from stdin), and exchanges it for a token.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the redirect and the token request.</param>
    /// <exception cref="OAuthException">The code could not be obtained or exchanged.</exception>
    public async Task<OAuthToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Upstream's sync variant runs the exchange on a helper thread and reports a dropped channel as
        // `OAuthError::Recv`; here the sync variant blocks on this method, so that error cannot occur.
        var pkceVerifier = SetAuthUrl();

        var redirectUri = RedirectUri.OriginalString;
        var addr = await OAuthHelpers.GetSocketAddressAsync(redirectUri, cancellationToken).ConfigureAwait(false);
        var code = addr is not null
            ? await OAuthHelpers.GetAuthCodeListenerAsync(addr, Message, cancellationToken).ConfigureAwait(false)
            : await OAuthHelpers.GetAuthCodeStdinAsync(Input ?? Console.In, Output ?? Console.Out, cancellationToken)
                .ConfigureAwait(false);
        Log.LogTrace("Exchange {Code} for access token", OAuthHelpers.RedactedCode);

        TokenResponse resp;
        try
        {
            resp = await _endpoint.RequestAsync(
                    OAuthHelpers.TokenUrl,
                    [
                        new("grant_type", "authorization_code"),
                        new("code", code),
                        new("code_verifier", pkceVerifier),
                        new("client_id", ClientId),
                        new("redirect_uri", redirectUri),
                    ],
                    OAuthJsonContext.Default.TokenResponse,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestTokenException e)
        {
            throw OAuthException.ExchangeCode(e.Message, e);
        }

        return BuildToken(resp);
    }

    /// <summary>Asynchronously obtain a new valid OAuth token from <paramref name="refreshToken"/> (upstream <c>refresh_token_async</c>).</summary>
    /// <param name="refreshToken">The refresh token of a previously obtained <see cref="OAuthToken"/>.</param>
    /// <param name="cancellationToken">Cancels the token request.</param>
    /// <exception cref="OAuthException">The refresh failed.</exception>
    public async Task<OAuthToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        var resp = await _endpoint.RefreshAsync(ClientId, refreshToken, cancellationToken).ConfigureAwait(false);
        return BuildToken(resp);
    }

    /// <summary>Releases the HTTP client.</summary>
    public void Dispose() => _endpoint.Dispose();

    /// <summary>
    /// Generates and opens/shows the authorization URL (upstream <c>set_auth_url</c>). Returns the PKCE verifier
    /// that must be included in the final request for validation.
    /// </summary>
    private string SetAuthUrl()
    {
        var (pkceChallenge, pkceVerifier) = Pkce.NewRandomSha256();
        // Generate the full authorization URL.
        // Some of these scopes are unavailable for custom client IDs. Which?
        var authUrl = OAuthHelpers.BuildAuthorizeUrl(
            ClientId, RedirectUri.OriginalString, Scopes, OAuthHelpers.NewCsrfToken(), pkceChallenge);

        if (ShouldOpenUrl)
        {
            OAuthHelpers.OpenInBackground(authUrl);
        }

        (Output ?? Console.Out).WriteLine($"Browse to: {authUrl}");

        return pkceVerifier;
    }

    private OAuthToken BuildToken(TokenResponse resp) => OAuthHelpers.BuildToken(resp, Scopes, DateTimeOffset.UtcNow);
}

/// <summary>Builder through which <see cref="OAuthClient"/> instances are created (upstream <c>OAuthClientBuilder</c>).</summary>
public sealed class OAuthClientBuilder
{
    private readonly string _clientId;
    private readonly string _redirectUri;
    private readonly IReadOnlyList<string> _scopes;
    private bool _openInBrowser;
    private string _message = OAuthHelpers.DefaultMessage;

    /// <summary>
    /// Create a new builder with the provided parameters and default config. <paramref name="redirectUri"/> must
    /// match one of the URIs registered for <paramref name="clientId"/>.
    /// </summary>
    public OAuthClientBuilder(string clientId, string redirectUri, IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentNullException.ThrowIfNull(scopes);
        _clientId = clientId;
        _redirectUri = redirectUri;
        _scopes = scopes.ToList();
    }

    /// <summary>
    /// Open the authorization URL with the default web browser. Otherwise it is only printed to standard output.
    /// </summary>
    public OAuthClientBuilder OpenInBrowser()
    {
        _openInBrowser = true;
        return this;
    }

    /// <summary>
    /// Use <paramref name="message"/> as the body of the response to the callback request (useful to serve a custom
    /// HTML page). Defaults to <c>"Go back to your terminal :)"</c>.
    /// </summary>
    public OAuthClientBuilder WithCustomMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _message = message;
        return this;
    }

    /// <summary>End of the building process: validates the redirect URI and builds the client.</summary>
    /// <exception cref="OAuthException"><see cref="OAuthErrorKind.InvalidRedirectUri"/> when the redirect URI does not parse.</exception>
    public OAuthClient Build()
    {
        if (!OAuthHelpers.TryParseUrl(_redirectUri, out var redirect, out var error))
        {
            throw OAuthException.InvalidRedirectUri(_redirectUri, error);
        }

        return new OAuthClient(_clientId, redirect, _scopes, _openInBrowser, _message);
    }
}

/// <summary>Upstream free function <c>get_access_token(client_id, redirect_uri, scopes)</c>.</summary>
public static class OAuthFlow
{
    /// <summary>
    /// Obtain a Spotify access token using the authorization code with PKCE OAuth flow. The
    /// <paramref name="redirectUri"/> must match what is registered to the client id. The URL is only printed, not
    /// opened.
    /// </summary>
    /// <exception cref="OAuthException">The flow failed.</exception>
    [Obsolete("please use builder pattern with `OAuthClientBuilder` instead")]
    public static OAuthToken GetAccessToken(string clientId, string redirectUri, IEnumerable<string> scopes)
    {
        // Upstream duplicates OAuthClient::get_access_token inline (without opening the browser and with the default
        // message); building a client with the same settings is equivalent.
        using var client = new OAuthClientBuilder(clientId, redirectUri, scopes).Build();
        return client.GetAccessToken();
    }
}

/// <summary>
/// A pending device authorization, waiting on the user's approval (upstream <c>DeviceAuthorization</c>).
/// Show <see cref="Url"/> and <see cref="UserCode"/> to the user, then hand this to
/// <see cref="DeviceAuthClient.PollForTokenAsync"/>.
/// </summary>
public sealed class DeviceAuthorization
{
    /// <summary>Creates a device authorization from its parts.</summary>
    public DeviceAuthorization(
        string deviceCode,
        string userCode,
        string verificationUri,
        string? verificationUriComplete,
        TimeSpan interval,
        DateTimeOffset expiresAt)
        : this(deviceCode, userCode, verificationUri, verificationUriComplete, interval, expiresAt,
            OAuthHelpers.Max(expiresAt - DateTimeOffset.UtcNow, TimeSpan.Zero))
    {
    }

    internal DeviceAuthorization(
        string deviceCode,
        string userCode,
        string verificationUri,
        string? verificationUriComplete,
        TimeSpan interval,
        DateTimeOffset expiresAt,
        TimeSpan expiresIn)
    {
        DeviceCode = deviceCode;
        UserCode = userCode;
        VerificationUri = verificationUri;
        VerificationUriComplete = verificationUriComplete;
        Interval = interval;
        ExpiresAt = expiresAt;
        ExpiresIn = expiresIn;
    }

    internal string DeviceCode { get; }

    /// <summary>
    /// The lifetime Spotify gave the code (<c>expires_in</c>). Like oauth2, polling gives up once this much time has
    /// passed since polling started.
    /// </summary>
    internal TimeSpan ExpiresIn { get; }

    /// <summary>The code the user types in at <see cref="VerificationUri"/>.</summary>
    public string UserCode { get; }

    /// <summary>The page the user visits to enter <see cref="UserCode"/> (usually https://spotify.com/pair).</summary>
    public string VerificationUri { get; }

    /// <summary><see cref="VerificationUri"/> with the code already filled in, when Spotify provides it.</summary>
    public string? VerificationUriComplete { get; }

    /// <summary>The poll interval Spotify asked for (5 s when it does not say).</summary>
    public TimeSpan Interval { get; }

    /// <summary>When the device code expires, computed from <c>expires_in</c> when the code was issued.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>The URL to send the user to, preferring the prefilled variant.</summary>
    public string Url => VerificationUriComplete ?? VerificationUri;
}

/// <summary>
/// Obtains and refreshes access tokens by pairing a code (upstream <c>DeviceAuthClient</c>). Unlike
/// <see cref="OAuthClient"/> this needs no redirect URI, nothing listening on a port, and no browser on the machine
/// running it. Spotify only enables this flow for some client ids.
/// </summary>
public sealed class DeviceAuthClient : IDisposable
{
    /// <summary>Upper bound of the backoff after transport errors while polling (oauth2's <c>MAX_BACKOFF_INTERVAL</c>).</summary>
    private static readonly TimeSpan MaxBackoffInterval = TimeSpan.FromSeconds(10);

    /// <summary>Added to the poll interval on every <c>slow_down</c> (RFC 8628 section 3.5).</summary>
    private static readonly TimeSpan SlowDownIncrement = TimeSpan.FromSeconds(5);

    private readonly TokenEndpoint _endpoint;

    internal DeviceAuthClient(string clientId, IReadOnlyList<string> scopes, bool shouldOpenUrl, HttpMessageHandler? handler = null)
    {
        ClientId = clientId;
        Scopes = scopes;
        ShouldOpenUrl = shouldOpenUrl;
        _endpoint = new TokenEndpoint(handler);
    }

    private static readonly ILogger Log = OAuthLog.CreateLogger<DeviceAuthClient>();

    /// <summary>The Spotify client id the tokens are requested for.</summary>
    public string ClientId { get; }

    /// <summary>The scopes requested with the device code.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>Whether the verification URL is also opened in the default web browser.</summary>
    public bool ShouldOpenUrl { get; }

    /// <summary>Sleep between polls (upstream passes <c>tokio::time::sleep</c> / <c>std::thread::sleep</c>; test seam).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } = OAuthHelpers.DelayAsync;

    /// <summary>Clock used for the poll deadline (oauth2's <c>time_fn</c>; test seam).</summary>
    internal Func<DateTimeOffset> Now { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Where the verification URL and user code are printed (test seam; default stdout).</summary>
    internal TextWriter? Output { get; set; }

    /// <summary>Synchronously request a device code for the user to approve (upstream <c>request_device_code</c>).</summary>
    /// <exception cref="OAuthException"><see cref="OAuthErrorKind.DeviceCode"/> on failure.</exception>
    public DeviceAuthorization RequestDeviceCode() => RequestDeviceCodeAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Synchronously poll Spotify until the user resolves <paramref name="auth"/> (upstream <c>poll_for_token</c>).
    /// Blocks until the user approves or declines, or the authorization expires.
    /// </summary>
    /// <exception cref="OAuthException"><see cref="OAuthErrorKind.ExchangeDeviceCode"/> on failure.</exception>
    public OAuthToken PollForToken(DeviceAuthorization auth) => PollForTokenAsync(auth).GetAwaiter().GetResult();

    /// <summary>
    /// Synchronously obtain a Spotify access token using the device authorization flow (upstream
    /// <c>get_access_token</c>): requests a code, shows it to the user, then blocks until they approve it. Drive
    /// <see cref="RequestDeviceCode"/> and <see cref="PollForToken"/> directly to present the code some other way.
    /// </summary>
    /// <exception cref="OAuthException">The flow failed.</exception>
    public OAuthToken GetAccessToken() => GetAccessTokenAsync().GetAwaiter().GetResult();

    /// <summary>Synchronously obtain a new valid OAuth token from <paramref name="refreshToken"/> (upstream <c>refresh_token</c>).</summary>
    /// <exception cref="OAuthException"><see cref="OAuthErrorKind.ExchangeCode"/> on failure.</exception>
    public OAuthToken RefreshToken(string refreshToken) => RefreshTokenAsync(refreshToken).GetAwaiter().GetResult();

    /// <summary>Asynchronously request a device code for the user to approve (upstream <c>request_device_code_async</c>).</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="OAuthException"><see cref="OAuthErrorKind.DeviceCode"/> on failure.</exception>
    public async Task<DeviceAuthorization> RequestDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> form = [];
        if (Scopes.Count > 0)
        {
            form.Add(new("scope", string.Join(' ', Scopes)));
        }

        form.Add(new("client_id", ClientId));

        DeviceAuthorizationResponse inner;
        string verificationUri;
        try
        {
            inner = await _endpoint.RequestAsync(
                    OAuthHelpers.DeviceAuthorizationUrl,
                    form,
                    OAuthJsonContext.Default.DeviceAuthorizationResponse,
                    cancellationToken)
                .ConfigureAwait(false);

            // `verification_url` is accepted as an alias, and the URI must parse (oauth2's EndUserVerificationUrl).
            verificationUri = inner.VerificationUri ?? inner.VerificationUrl
                ?? throw RequestTokenException.Parse(new JsonException("missing field `verification_uri`"));
            if (!OAuthHelpers.TryParseUrl(verificationUri, out _, out var error))
            {
                throw RequestTokenException.Parse(error);
            }
        }
        catch (RequestTokenException e)
        {
            throw OAuthException.DeviceCode(e.Message, e);
        }

        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("Obtained device authorization: {Response}", inner.ToDebugString());
        }

        var expiresIn = OAuthHelpers.Seconds(inner.ExpiresIn);
        return new DeviceAuthorization(
            inner.DeviceCode,
            inner.UserCode,
            verificationUri,
            inner.VerificationUriComplete,
            OAuthHelpers.Seconds(inner.Interval ?? OAuthHelpers.DefaultDeviceCodeIntervalSeconds),
            OAuthHelpers.SaturatingAdd(Now(), expiresIn),
            expiresIn);
    }

    /// <summary>
    /// Asynchronously poll Spotify until the user resolves <paramref name="auth"/> (upstream
    /// <c>poll_for_token_async</c>). Spotify's requested poll interval and any <c>slow_down</c> it sends back are
    /// both respected.
    /// </summary>
    /// <param name="auth">The pending authorization returned by <see cref="RequestDeviceCodeAsync"/>.</param>
    /// <param name="cancellationToken">Cancels polling.</param>
    /// <exception cref="OAuthException"><see cref="OAuthErrorKind.ExchangeDeviceCode"/> when the user declines, the code expires or a request fails.</exception>
    public async Task<OAuthToken> PollForTokenAsync(DeviceAuthorization auth, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auth);

        // oauth2 `compute_timeout`: the deadline is `expires_in` from the start of polling.
        var timeoutAt = OAuthHelpers.SaturatingAdd(Now(), auth.ExpiresIn);
        var interval = auth.Interval;

        TokenResponse resp;
        while (true)
        {
            if (Now() > timeoutAt)
            {
                var expired = RequestTokenException.ServerResponseError(
                    new ErrorResponse { Error = "expired_token", ErrorDescription = "This device code has expired." });
                throw OAuthException.ExchangeDeviceCode(expired.Message, expired);
            }

            try
            {
                resp = await _endpoint.RequestAsync(
                        OAuthHelpers.TokenUrl,
                        [
                            new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                            new("device_code", auth.DeviceCode),
                            new("client_id", ClientId),
                        ],
                        OAuthJsonContext.Default.TokenResponse,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            catch (RequestTokenException e) when (e.IsRequestError)
            {
                // RFC 8628 requires a backoff in cases of connection timeout, but those cannot be told apart from
                // other HTTP client errors here. Cap the backoff so that the client doesn't effectively back off
                // forever when there are network issues unrelated to server load.
                interval = OAuthHelpers.Min(OAuthHelpers.DoubleOrSame(interval), MaxBackoffInterval);
            }
            catch (RequestTokenException e) when (e.ServerResponse?.Error == "authorization_pending")
            {
                // Retry with the same poll interval.
            }
            catch (RequestTokenException e) when (e.ServerResponse?.Error == "slow_down")
            {
                interval = OAuthHelpers.SaturatingAdd(interval, SlowDownIncrement);
            }
            catch (RequestTokenException e)
            {
                throw OAuthException.ExchangeDeviceCode(e.Message, e);
            }

            await Sleep(interval, cancellationToken).ConfigureAwait(false);
        }

        return OAuthHelpers.BuildToken(resp, Scopes, Now());
    }

    /// <summary>
    /// Asynchronously obtain a Spotify access token using the device authorization flow (upstream
    /// <c>get_access_token_async</c>). See <see cref="GetAccessToken"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the requests and polling.</param>
    /// <exception cref="OAuthException">The flow failed.</exception>
    public async Task<OAuthToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var auth = await RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        Announce(auth);
        return await PollForTokenAsync(auth, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asynchronously obtain a new valid OAuth token from <paramref name="refreshToken"/> (upstream <c>refresh_token_async</c>).</summary>
    /// <param name="refreshToken">The refresh token of a previously obtained <see cref="OAuthToken"/>.</param>
    /// <param name="cancellationToken">Cancels the token request.</param>
    /// <exception cref="OAuthException"><see cref="OAuthErrorKind.ExchangeCode"/> on failure.</exception>
    public async Task<OAuthToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        var resp = await _endpoint.RefreshAsync(ClientId, refreshToken, cancellationToken).ConfigureAwait(false);
        return OAuthHelpers.BuildToken(resp, Scopes, Now());
    }

    /// <summary>Releases the HTTP client.</summary>
    public void Dispose() => _endpoint.Dispose();

    /// <summary>
    /// Shows the user where to approve the authorization, opening it for them if the client was built with
    /// <see cref="DeviceAuthClientBuilder.OpenInBrowser"/> (upstream <c>announce</c>).
    /// </summary>
    private void Announce(DeviceAuthorization auth)
    {
        var url = auth.Url;
        if (ShouldOpenUrl)
        {
            OAuthHelpers.OpenInBackground(url);
        }

        var output = Output ?? Console.Out;
        output.WriteLine($"Browse to: {url}");
        output.WriteLine($"If prompted, enter code: {auth.UserCode}");
    }
}

/// <summary>Builder through which <see cref="DeviceAuthClient"/> instances are created (upstream <c>DeviceAuthClientBuilder</c>).</summary>
public sealed class DeviceAuthClientBuilder
{
    private readonly string _clientId;
    private readonly IReadOnlyList<string> _scopes;
    private bool _openInBrowser;

    /// <summary>
    /// Create a new builder with the provided parameters and default config. Spotify fails the whole request with
    /// <c>invalid_scope</c> if it does not recognise any one of <paramref name="scopes"/>, so unsupported scopes cannot
    /// be discovered by asking for extras and seeing what comes back.
    /// </summary>
    public DeviceAuthClientBuilder(string clientId, IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(scopes);
        _clientId = clientId;
        _scopes = scopes.ToList();
    }

    /// <summary>
    /// Also open the verification URL with the default web browser; otherwise it is only printed. Off by default,
    /// since the point of this flow is that the device running librespot need not have a browser at all.
    /// </summary>
    public DeviceAuthClientBuilder OpenInBrowser()
    {
        _openInBrowser = true;
        return this;
    }

    /// <summary>End of the building process. The endpoint URLs are constants, so this cannot fail.</summary>
    public DeviceAuthClient Build() => new(_clientId, _scopes, _openInBrowser);
}

/// <summary>The free functions and constants of <c>oauth/src/lib.rs</c>, plus the oauth2 pieces they rely on.</summary>
internal static class OAuthHelpers
{
    /// <summary>Spotify's authorization endpoint, for the authorization code flow (upstream <c>AUTH_URL</c>).</summary>
    internal const string AuthUrl = "https://accounts.spotify.com/authorize";

    /// <summary>Spotify's token endpoint, shared by every grant type (upstream <c>TOKEN_URL</c>).</summary>
    internal const string TokenUrl = "https://accounts.spotify.com/api/token";

    /// <summary>Spotify's device authorization endpoint (upstream <c>DEVICE_AUTHORIZATION_URL</c>).</summary>
    internal const string DeviceAuthorizationUrl = "https://accounts.spotify.com/oauth2/device/authorize";

    /// <summary>Default body of the callback response (upstream <c>OAuthClientBuilder::new</c>).</summary>
    internal const string DefaultMessage = "Go back to your terminal :)";

    /// <summary>oauth2's <c>default_devicecode_interval</c>, in seconds.</summary>
    internal const ulong DefaultDeviceCodeIntervalSeconds = 5;

    /// <summary>How the oauth2 crate prints an <c>AuthorizationCode</c> with <c>{:?}</c>.</summary>
    internal const string RedactedCode = "AuthorizationCode([redacted])";

    private const string NoBaseError = "relative URL without a base";

    // Upstream logs everything under the crate target `librespot_oauth`.
    private static readonly ILogger Log = OAuthLog.CreateLogger("Librespot.OAuth");

    /// <summary>
    /// Parses an absolute URL the way <c>url::Url::parse</c> does for our purposes: a scheme is required.
    /// (<see cref="System.Uri"/> would also accept bare file-system paths as implicit <c>file:</c> URIs.)
    /// </summary>
    internal static bool TryParseUrl(string s, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out Exception? error)
    {
        try
        {
            uri = new Uri(s, UriKind.Absolute);
        }
        catch (UriFormatException e)
        {
            uri = null;
            error = e;
            return false;
        }

        if ((uri.IsFile || uri.IsUnc) && !s.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            uri = null;
            error = new UriFormatException(NoBaseError);
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Return the <c>code</c> query-string parameter from the redirect URI (upstream <c>get_code</c>).</summary>
    /// <exception cref="OAuthException"><see cref="OAuthErrorKind.AuthCodeBadUri"/> or <see cref="OAuthErrorKind.AuthCodeNotFound"/>.</exception>
    internal static string GetCode(string redirectUrl)
    {
        if (!TryParseUrl(redirectUrl, out var url, out var error))
        {
            throw OAuthException.AuthCodeBadUri(redirectUrl, error);
        }

        foreach (var (key, value) in QueryPairs(url.Query))
        {
            if (key == "code")
            {
                return value;
            }
        }

        throw OAuthException.AuthCodeNotFound(redirectUrl);
    }

    /// <summary>Prompt for the redirect URI on stdin and return the auth code (upstream <c>get_authcode_stdin</c>).</summary>
    internal static async Task<string> GetAuthCodeStdinAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        output.WriteLine("Provide redirect URL");

        // Console input has no cancellable read; read on the pool and stop waiting when cancelled.
        var read = Task.Run(input.ReadLine, CancellationToken.None);
        string? line;
        try
        {
            line = await read.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = read.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception)
        {
            throw OAuthException.AuthCodeStdinRead();
        }

        // `read_line` leaves the buffer empty at end of input.
        return GetCode((line ?? string.Empty).Trim());
    }

    /// <summary>
    /// Bind an HTTP server at <paramref name="socketAddress"/>, accept the OAuth callback and return the auth code
    /// (upstream <c>get_authcode_listener</c>).
    /// </summary>
    internal static async Task<string> GetAuthCodeListenerAsync(IPEndPoint socketAddress, string message, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(socketAddress);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            listener.Dispose();
            throw OAuthException.AuthCodeListenerBind(socketAddress, e);
        }

        using (listener)
        {
            Log.LogInformation("OAuth server listening on {SocketAddress}", socketAddress);
            return await AcceptAuthCodeAsync(listener, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Accept one connection on a started <paramref name="listener"/>, parse the request line, answer with
    /// <paramref name="message"/> and return the code. Split from <see cref="GetAuthCodeListenerAsync"/> so tests can
    /// listen on an ephemeral port.
    /// </summary>
    internal static async Task<string> AcceptAuthCodeAsync(TcpListener listener, string message, CancellationToken cancellationToken)
    {
        // The server terminates itself after collecting the first code. Like `incoming().flatten()`, failed accepts
        // are skipped.
        TcpClient? client = null;
        while (client is null)
        {
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Skipped, as `flatten()` skips `Err` items.
            }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
            {
                throw OAuthException.AuthCodeListenerTerminated();
            }
        }

        using (client)
        {
            var stream = client.GetStream();

            string requestLine;
            try
            {
                // Same 8 KiB buffer as Rust's BufReader, strict UTF-8 like `read_line`.
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 8192, leaveOpen: true);
                requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw OAuthException.AuthCodeListenerRead();
            }

            var parts = requestLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw OAuthException.AuthCodeListenerParse();
            }

            // As upstream, a missing/bad code is only reported after the response has been sent.
            string? code = null;
            OAuthException? codeError = null;
            try
            {
                code = GetCode("http://localhost" + parts[1]);
            }
            catch (OAuthException e)
            {
                codeError = e;
            }

            // One write of the whole response, as upstream's `write_all`.
            var body = Encoding.UTF8.GetBytes(message);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\ncontent-length: {body.Length}\r\n\r\n");
            var response = new byte[head.Length + body.Length];
            head.CopyTo(response, 0);
            body.CopyTo(response, head.Length);
            try
            {
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw OAuthException.AuthCodeListenerWrite();
            }

            return codeError is not null ? throw codeError : code!;
        }
    }

    /// <summary>
    /// If <paramref name="redirectUri"/> is HTTP and names a (non-default) port, return the corresponding socket
    /// address (upstream <c>get_socket_address</c>; host names resolve to the last address, as <c>addrs.pop()</c>).
    /// </summary>
    internal static async Task<IPEndPoint?> GetSocketAddressAsync(string redirectUri, CancellationToken cancellationToken)
    {
        // url::Url::port() is None both when no port is given and when it is the scheme's default.
        if (!TryParseUrl(redirectUri, out var url, out _) || url.Scheme != Uri.UriSchemeHttp || url.IsDefaultPort)
        {
            return null;
        }

        var host = url.DnsSafeHost;
        if (IPAddress.TryParse(host, out var ip))
        {
            return new IPEndPoint(ip, url.Port);
        }

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addrs.Length == 0 ? null : new IPEndPoint(addrs[^1], url.Port);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Convert a token endpoint response into an <see cref="OAuthToken"/> (upstream <c>build_token</c>).
    /// <paramref name="defaultScopes"/> stands in for the granted scopes when Spotify omits them.
    /// </summary>
    internal static OAuthToken BuildToken(TokenResponse resp, IReadOnlyList<string> defaultScopes, DateTimeOffset now)
    {
        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("Obtained new access token: {Response}", resp.ToDebugString());
        }

        // oauth2 splits `scope` on single spaces without dropping empty entries.
        IReadOnlyList<string> tokenScopes = resp.Scope is { } s ? s.Split(' ') : defaultScopes.ToList();
        return new OAuthToken(
            resp.AccessToken,
            resp.RefreshToken ?? string.Empty, // Spotify always provides a refresh token.
            SaturatingAdd(now, resp.ExpiresIn is { } secs ? Seconds(secs) : TimeSpan.FromSeconds(3600)),
            TokenTypeDebug(resp.TokenType),
            tokenScopes);
    }

    /// <summary>
    /// <c>format!("{:?}", resp.token_type())</c>: oauth2 lower-cases the value and maps it onto
    /// <c>BasicTokenType::{Bearer, Mac, Extension(String)}</c>.
    /// </summary>
    internal static string TokenTypeDebug(string tokenType)
    {
        var lower = tokenType.ToLowerInvariant();
        return lower switch
        {
            "bearer" => "Bearer",
            "mac" => "Mac",
            _ => $"Extension(\"{lower.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\")",
        };
    }

    /// <summary>The authorize URL of oauth2's <c>AuthorizationRequest::url</c> with the parameters librespot sets.</summary>
    internal static string BuildAuthorizeUrl(
        string clientId, string redirectUri, IReadOnlyList<string> scopes, string state, string pkceChallenge)
    {
        List<KeyValuePair<string, string>> pairs =
        [
            new("response_type", "code"),
            new("client_id", clientId),
            new("state", state),
            new("code_challenge", pkceChallenge),
            new("code_challenge_method", Pkce.MethodS256),
            new("redirect_uri", redirectUri),
        ];
        if (scopes.Count > 0)
        {
            pairs.Add(new("scope", string.Join(' ', scopes)));
        }

        return AuthUrl + "?" + FormUrlEncode(pairs);
    }

    /// <summary><c>CsrfToken::new_random</c>: 16 random bytes, base64url without padding.</summary>
    internal static string NewCsrfToken()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        return Base64Url.EncodeToString(random);
    }

    /// <summary>
    /// <c>application/x-www-form-urlencoded</c> serialization as done by <c>url::form_urlencoded::Serializer</c>:
    /// ASCII alphanumerics and <c>*-._</c> stay, space becomes <c>+</c>, every other UTF-8 byte becomes <c>%XX</c>.
    /// </summary>
    internal static string FormUrlEncode(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (sb.Length > 0)
            {
                sb.Append('&');
            }

            AppendFormEncoded(sb, key);
            sb.Append('=');
            AppendFormEncoded(sb, value);
        }

        return sb.ToString();
    }

    /// <summary><c>url::Url::query_pairs</c>: split on <c>&amp;</c>, skip empty pieces, form-decode key and value.</summary>
    internal static IEnumerable<(string Key, string Value)> QueryPairs(string query)
    {
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        foreach (var piece in query.Split('&'))
        {
            if (piece.Length == 0)
            {
                continue;
            }

            var eq = piece.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? piece : piece[..eq];
            var value = eq < 0 ? string.Empty : piece[(eq + 1)..];
            yield return (FormDecode(key), FormDecode(value));
        }
    }

    /// <summary><c>open::that_in_background</c>: launch the default browser; failures are ignored as upstream does.</summary>
    internal static void OpenInBackground(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Upstream discards the result of the background open.
        }
    }

    /// <summary>Default poll sleep: <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, clamped to its maximum.</summary>
    internal static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(Min(delay, TimeSpan.FromMilliseconds(uint.MaxValue - 1)), cancellationToken);

    /// <summary><c>Duration::from_secs</c>, saturating at <see cref="TimeSpan.MaxValue"/>.</summary>
    internal static TimeSpan Seconds(ulong seconds) =>
        seconds >= (ulong)TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);

    internal static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    internal static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary><c>interval.checked_mul(2).unwrap_or(interval)</c>.</summary>
    internal static TimeSpan DoubleOrSame(TimeSpan interval) =>
        interval.Ticks > long.MaxValue / 2 ? interval : TimeSpan.FromTicks(interval.Ticks * 2);

    internal static TimeSpan SaturatingAdd(TimeSpan a, TimeSpan b) =>
        a.Ticks > TimeSpan.MaxValue.Ticks - b.Ticks ? TimeSpan.MaxValue : a + b;

    internal static DateTimeOffset SaturatingAdd(DateTimeOffset at, TimeSpan span) =>
        span >= DateTimeOffset.MaxValue - at ? DateTimeOffset.MaxValue : at + span;

    private static void AppendFormEncoded(StringBuilder sb, string s)
    {
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
            {
                sb.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
    }

    private static string FormDecode(string s)
    {
        var raw = Encoding.UTF8.GetBytes(s.Replace('+', ' '));
        var bytes = new byte[raw.Length];
        var n = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'%' && i + 2 < raw.Length
                && Uri.IsHexDigit((char)raw[i + 1]) && Uri.IsHexDigit((char)raw[i + 2]))
            {
                bytes[n++] = (byte)((Uri.FromHex((char)raw[i + 1]) << 4) | Uri.FromHex((char)raw[i + 2]));
                i += 2;
            }
            else
            {
                bytes[n++] = raw[i];
            }
        }

        // Invalid UTF-8 is replaced, as `decode_utf8_lossy` does.
        return Encoding.UTF8.GetString(bytes, 0, n);
    }
}

/// <summary>oauth2's PKCE code challenge (<c>PkceCodeChallenge</c>, S256 only).</summary>
internal static class Pkce
{
    /// <summary>The <c>code_challenge_method</c> value.</summary>
    internal const string MethodS256 = "S256";

    /// <summary><c>PkceCodeChallenge::new_random_sha256</c>: 32 random bytes, base64url without padding (43 chars).</summary>
    internal static (string Challenge, string Verifier) NewRandomSha256()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var verifier = Base64Url.EncodeToString(random);
        return (ChallengeS256(verifier), verifier);
    }

    /// <summary>BASE64URL-ENCODE(SHA256(ASCII(code_verifier))) (RFC 7636 section 4.2).</summary>
    internal static string ChallengeS256(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

/// <summary>
/// The token / device-authorization endpoint client: oauth2's <c>endpoint_request</c> + <c>endpoint_response</c>
/// over one <see cref="System.Net.Http.HttpClient"/> (system proxy, no timeout, like <c>reqwest::Client::new()</c>).
/// </summary>
internal sealed class TokenEndpoint : IDisposable
{
    private const string ContentTypeJson = "application/json";
    private const string ContentTypeFormEncoded = "application/x-www-form-urlencoded";

    private readonly System.Net.Http.HttpClient _http;

    internal TokenEndpoint(HttpMessageHandler? handler)
    {
        handler ??= new SocketsHttpHandler { MaxAutomaticRedirections = 10 };
        _http = new System.Net.Http.HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The refresh-token grant shared by both clients; failures become <see cref="OAuthErrorKind.ExchangeCode"/>.</summary>
    internal async Task<TokenResponse> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            return await RequestAsync(
                    OAuthHelpers.TokenUrl,
                    [
                        new("grant_type", "refresh_token"),
                        new("refresh_token", refreshToken),
                        new("client_id", clientId),
                    ],
                    OAuthJsonContext.Default.TokenResponse,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestTokenException e)
        {
            throw OAuthException.ExchangeCode(e.Message, e);
        }
    }

    /// <summary>POSTs <paramref name="form"/> to <paramref name="url"/> and parses a JSON <typeparamref name="T"/>.</summary>
    /// <exception cref="RequestTokenException">Transport, status, content-type or parse failure.</exception>
    internal async Task<T> RequestAsync<T>(
        string url,
        IReadOnlyList<KeyValuePair<string, string>> form,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        HttpStatusCode status;
        string? contentType;
        byte[] body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(OAuthHelpers.FormUrlEncode(form)));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFormEncoded);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ContentTypeJson));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            contentType = response.Content.Headers.TryGetValues("Content-Type", out var values) ? values.FirstOrDefault() : null;
            body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw RequestTokenException.Request(e);
        }

        // check_response_status
        if (status != HttpStatusCode.OK)
        {
            if (body.Length == 0)
            {
                throw RequestTokenException.Other("server returned empty error response");
            }

            ErrorResponse error;
            try
            {
                error = JsonSerializer.Deserialize(body, OAuthJsonContext.Default.ErrorResponse)
                    ?? throw new JsonException("invalid type: null, expected struct StandardErrorResponse");
            }
            catch (JsonException e)
            {
                throw RequestTokenException.Parse(e);
            }

            throw RequestTokenException.ServerResponseError(error);
        }

        // check_response_body
        if (contentType is not null && !contentType.ToLowerInvariant().StartsWith(ContentTypeJson, StringComparison.Ordinal))
        {
            throw RequestTokenException.Other(
                $"unexpected response Content-Type: \"{contentType}\", should be `{ContentTypeJson}`");
        }

        if (body.Length == 0)
        {
            throw RequestTokenException.Other("server returned empty response body");
        }

        try
        {
            return JsonSerializer.Deserialize(body, typeInfo) ?? throw new JsonException("invalid type: null");
        }
        catch (JsonException e)
        {
            throw RequestTokenException.Parse(e);
        }
    }
}

/// <summary>
/// oauth2's <c>StandardErrorResponse</c>, also the JSON body of an error response (<c>error</c>,
/// <c>error_description</c>, <c>error_uri</c>); <see cref="ToString"/> is its <c>Display</c>.
/// </summary>
internal sealed record ErrorResponse
{
    [JsonPropertyName("error"), JsonRequired]
    public string Error { get; init; } = string.Empty;

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }

    [JsonPropertyName("error_uri")]
    public string? ErrorUri { get; init; }

    public override string ToString()
    {
        var formatted = Error;
        if (ErrorDescription is not null)
        {
            formatted += ": " + ErrorDescription;
        }

        if (ErrorUri is not null)
        {
            formatted += " (see " + ErrorUri + ")";
        }

        return formatted;
    }
}

/// <summary>oauth2's <c>RequestTokenError</c>; <see cref="Exception.Message"/> is its <c>Display</c>.</summary>
internal sealed class RequestTokenException : Exception
{
    private RequestTokenException(string message, ErrorResponse? serverResponse, bool isRequestError, Exception? inner)
        : base(message, inner)
    {
        ServerResponse = serverResponse;
        IsRequestError = isRequestError;
    }

    /// <summary>The parsed error body for <c>RequestTokenError::ServerResponse</c>.</summary>
    public ErrorResponse? ServerResponse { get; }

    /// <summary>True for <c>RequestTokenError::Request</c> (the HTTP exchange itself failed).</summary>
    public bool IsRequestError { get; }

    public static RequestTokenException ServerResponseError(ErrorResponse response) =>
        new($"Server returned error response: {response}", response, false, null);

    public static RequestTokenException Request(Exception inner) => new("Request failed", null, true, inner);

    public static RequestTokenException Parse(Exception inner) => new("Failed to parse server response", null, false, inner);

    public static RequestTokenException Other(string message) => new($"Other error: {message}", null, false, null);
}

/// <summary>oauth2's <c>StandardTokenResponse&lt;EmptyExtraTokenFields, BasicTokenType&gt;</c>.</summary>
internal sealed record TokenResponse
{
    [JsonPropertyName("access_token"), JsonRequired]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("token_type"), JsonRequired]
    public string TokenType { get; init; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public ulong? ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>The <c>{:?}</c> rendering, with secrets redacted the way oauth2 redacts them.</summary>
    internal string ToDebugString()
    {
        var expires = ExpiresIn is { } e ? $"Some({e})" : "None";
        var refresh = RefreshToken is null ? "None" : "Some(RefreshToken([redacted]))";
        var scopes = Scope is { } s ? $"Some([{string.Join(", ", s.Split(' ').Select(x => $"Scope(\"{x}\")"))}])" : "None";
        return $"StandardTokenResponse {{ access_token: AccessToken([redacted]), token_type: {OAuthHelpers.TokenTypeDebug(TokenType)}, " +
            $"expires_in: {expires}, refresh_token: {refresh}, scopes: {scopes}, extra_fields: EmptyExtraTokenFields }}";
    }
}

/// <summary>oauth2's <c>StandardDeviceAuthorizationResponse</c>.</summary>
internal sealed record DeviceAuthorizationResponse
{
    [JsonPropertyName("device_code"), JsonRequired]
    public string DeviceCode { get; init; } = string.Empty;

    [JsonPropertyName("user_code"), JsonRequired]
    public string UserCode { get; init; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string? VerificationUri { get; init; }

    /// <summary>Accepted alias of <c>verification_uri</c>.</summary>
    [JsonPropertyName("verification_url")]
    public string? VerificationUrl { get; init; }

    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; init; }

    [JsonPropertyName("expires_in"), JsonRequired]
    public ulong ExpiresIn { get; init; }

    [JsonPropertyName("interval")]
    public ulong? Interval { get; init; }

    /// <summary>The <c>{:?}</c> rendering, with secrets redacted the way oauth2 redacts them.</summary>
    internal string ToDebugString()
    {
        var complete = VerificationUriComplete is null ? "None" : "Some(VerificationUriComplete([redacted]))";
        return $"StandardDeviceAuthorizationResponse {{ device_code: DeviceCode([redacted]), user_code: UserCode([redacted]), " +
            $"verification_uri: EndUserVerificationUrl(\"{VerificationUri ?? VerificationUrl}\"), verification_uri_complete: {complete}, " +
            $"expires_in: {ExpiresIn}, interval: {Interval ?? OAuthHelpers.DefaultDeviceCodeIntervalSeconds}, " +
            "extra_fields: EmptyExtraDeviceAuthorizationFields }";
    }
}

/// <summary>Source-generated JSON metadata for the endpoint DTOs.</summary>
[JsonSourceGenerationOptions(RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(DeviceAuthorizationResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class OAuthJsonContext : JsonSerializerContext;
