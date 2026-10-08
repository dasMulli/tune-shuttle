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

    public OAuthException(OAuthErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public static OAuthException AuthCodeBadUri(string uri, Exception e) =>
        new(OAuthErrorKind.AuthCodeBadUri, $"Unable to parse redirect URI {uri} ({e.Message})", e);

    public static OAuthException AuthCodeNotFound(string uri) =>
        new(OAuthErrorKind.AuthCodeNotFound, $"Auth code param not found in URI {uri}");

    public static OAuthException AuthCodeStdinRead() =>
        new(OAuthErrorKind.AuthCodeStdinRead, "Failed to read redirect URI from stdin");

    public static OAuthException AuthCodeListenerBind(System.Net.EndPoint addr, Exception e) =>
        new(OAuthErrorKind.AuthCodeListenerBind, $"Failed to bind server to {addr} ({e.Message})", e);

    public static OAuthException AuthCodeListenerTerminated() =>
        new(OAuthErrorKind.AuthCodeListenerTerminated, "Listener terminated without accepting a connection");

    public static OAuthException AuthCodeListenerRead() =>
        new(OAuthErrorKind.AuthCodeListenerRead, "Failed to read redirect URI from HTTP request");

    public static OAuthException AuthCodeListenerParse() =>
        new(OAuthErrorKind.AuthCodeListenerParse, "Failed to parse redirect URI from HTTP request");

    public static OAuthException AuthCodeListenerWrite() =>
        new(OAuthErrorKind.AuthCodeListenerWrite, "Failed to write HTTP response");

    public static OAuthException InvalidSpotifyUri() =>
        new(OAuthErrorKind.InvalidSpotifyUri, "Invalid Spotify OAuth URI");

    public static OAuthException InvalidRedirectUri(string uri, Exception e) =>
        new(OAuthErrorKind.InvalidRedirectUri, $"Invalid Redirect URI {uri} ({e.Message})", e);

    public static OAuthException Recv() =>
        new(OAuthErrorKind.Recv, "Failed to receive code");

    public static OAuthException ExchangeCode(string e, Exception? inner = null) =>
        new(OAuthErrorKind.ExchangeCode, $"Failed to exchange code for access token ({e})", inner);

    public static OAuthException DeviceCode(string e, Exception? inner = null) =>
        new(OAuthErrorKind.DeviceCode, $"Failed to obtain device code ({e})", inner);

    public static OAuthException ExchangeDeviceCode(string e, Exception? inner = null) =>
        new(OAuthErrorKind.ExchangeDeviceCode, $"Failed to exchange device code for access token ({e})", inner);
}
