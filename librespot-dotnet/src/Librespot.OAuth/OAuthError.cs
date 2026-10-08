// Port of oauth/src/lib.rs (the `OAuthError` enum). The rest of lib.rs lives in OAuth.cs.
namespace Librespot.OAuth;

/// <summary>
/// The variants of the upstream <c>OAuthError</c> enum. <see cref="OAuthException.Kind"/> carries
/// the variant; the variant payloads (uri, addr, inner error text) are folded into the message
/// and <see cref="Exception.InnerException"/>.
/// </summary>
public enum OAuthErrorKind
{
    /// <summary>The redirect URI cannot be parsed as a valid URL.</summary>
    AuthCodeBadUri,
    /// <summary>The authorization code parameter is missing in the redirect URI.</summary>
    AuthCodeNotFound,
    /// <summary>Failed to read input from standard input when manually collecting the auth code.</summary>
    AuthCodeStdinRead,
    /// <summary>Could not bind the TCP listener for the OAuth callback.</summary>
    AuthCodeListenerBind,
    /// <summary>Listener terminated before receiving an OAuth callback connection.</summary>
    AuthCodeListenerTerminated,
    /// <summary>Failed to read the incoming HTTP request containing the OAuth callback.</summary>
    AuthCodeListenerRead,
    /// <summary>Received a malformed HTTP request for the OAuth callback.</summary>
    AuthCodeListenerParse,
    /// <summary>Could not send the HTTP response after handling the OAuth callback.</summary>
    AuthCodeListenerWrite,
    /// <summary>Invalid Spotify authorization endpoint URL.</summary>
    InvalidSpotifyUri,
    /// <summary>Redirect URI failed validation.</summary>
    InvalidRedirectUri,
    /// <summary>Channel communication failure.</summary>
    Recv,
    /// <summary>Token exchange failure with Spotify's authorization server.</summary>
    ExchangeCode,
    /// <summary>Device code request failure with Spotify's authorization server.</summary>
    DeviceCode,
    /// <summary>Device-code token exchange failure, including the user declining or letting the code expire.</summary>
    ExchangeDeviceCode,
}

/// <summary>Possible errors encountered during the OAuth authentication flow (upstream <c>OAuthError</c>).</summary>
public sealed class OAuthException : Exception
{
    /// <summary>Which upstream enum variant this error corresponds to.</summary>
    public OAuthErrorKind Kind { get; }

    /// <summary>Creates an error of the given <paramref name="kind"/>; prefer the static factories, which build upstream's message.</summary>
    public OAuthException(OAuthErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary><c>AuthCodeBadUri</c>: the redirect URI cannot be parsed as a valid URL.</summary>
    public static OAuthException AuthCodeBadUri(string uri, Exception e) =>
        new(OAuthErrorKind.AuthCodeBadUri, $"Unable to parse redirect URI {uri} ({e.Message})", e);

    /// <summary><c>AuthCodeNotFound</c>: the authorization code parameter is missing in the redirect URI.</summary>
    public static OAuthException AuthCodeNotFound(string uri) =>
        new(OAuthErrorKind.AuthCodeNotFound, $"Auth code param not found in URI {uri}");

    /// <summary><c>AuthCodeStdinRead</c>: failed to read the redirect URI from standard input.</summary>
    public static OAuthException AuthCodeStdinRead() =>
        new(OAuthErrorKind.AuthCodeStdinRead, "Failed to read redirect URI from stdin");

    /// <summary><c>AuthCodeListenerBind</c>: could not bind the TCP listener for the OAuth callback.</summary>
    public static OAuthException AuthCodeListenerBind(System.Net.EndPoint addr, Exception e) =>
        new(OAuthErrorKind.AuthCodeListenerBind, $"Failed to bind server to {addr} ({e.Message})", e);

    /// <summary><c>AuthCodeListenerTerminated</c>: the listener stopped before accepting a connection.</summary>
    public static OAuthException AuthCodeListenerTerminated() =>
        new(OAuthErrorKind.AuthCodeListenerTerminated, "Listener terminated without accepting a connection");

    /// <summary><c>AuthCodeListenerRead</c>: failed to read the HTTP request containing the OAuth callback.</summary>
    public static OAuthException AuthCodeListenerRead() =>
        new(OAuthErrorKind.AuthCodeListenerRead, "Failed to read redirect URI from HTTP request");

    /// <summary><c>AuthCodeListenerParse</c>: malformed HTTP request line for the OAuth callback.</summary>
    public static OAuthException AuthCodeListenerParse() =>
        new(OAuthErrorKind.AuthCodeListenerParse, "Failed to parse redirect URI from HTTP request");

    /// <summary><c>AuthCodeListenerWrite</c>: could not send the HTTP response to the OAuth callback.</summary>
    public static OAuthException AuthCodeListenerWrite() =>
        new(OAuthErrorKind.AuthCodeListenerWrite, "Failed to write HTTP response");

    /// <summary><c>InvalidSpotifyUri</c>: invalid Spotify authorization endpoint URL.</summary>
    public static OAuthException InvalidSpotifyUri() =>
        new(OAuthErrorKind.InvalidSpotifyUri, "Invalid Spotify OAuth URI");

    /// <summary><c>InvalidRedirectUri</c>: the redirect URI failed validation.</summary>
    public static OAuthException InvalidRedirectUri(string uri, Exception e) =>
        new(OAuthErrorKind.InvalidRedirectUri, $"Invalid Redirect URI {uri} ({e.Message})", e);

    /// <summary><c>Recv</c>: channel communication failure.</summary>
    public static OAuthException Recv() =>
        new(OAuthErrorKind.Recv, "Failed to receive code");

    /// <summary><c>ExchangeCode</c>: token exchange (or refresh) failure with Spotify's authorization server.</summary>
    public static OAuthException ExchangeCode(string e, Exception? inner = null) =>
        new(OAuthErrorKind.ExchangeCode, $"Failed to exchange code for access token ({e})", inner);

    /// <summary><c>DeviceCode</c>: device code request failure with Spotify's authorization server.</summary>
    public static OAuthException DeviceCode(string e, Exception? inner = null) =>
        new(OAuthErrorKind.DeviceCode, $"Failed to obtain device code ({e})", inner);

    /// <summary><c>ExchangeDeviceCode</c>: device-code token exchange failure, including the user declining or letting the code expire.</summary>
    public static OAuthException ExchangeDeviceCode(string e, Exception? inner = null) =>
        new(OAuthErrorKind.ExchangeDeviceCode, $"Failed to exchange device code for access token ({e})", inner);
}
