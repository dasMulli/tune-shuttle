// Port of core/src/error.rs.
//
// Upstream has a single `Error { kind: ErrorKind, error: Box<dyn Error> }` plus a long list of
// `From<X> for Error` impls that classify third-party errors into an ErrorKind. In C# this becomes:
//
//   * LibrespotException     - the base type; carries `Kind` (ErrorKind) and wraps the cause as InnerException.
//   * <Domain>Exception      - one subclass per upstream domain error enum (SessionError, AuthenticationError,
//                              SpotifyIdError, MercuryError, ...). Each fixes `Kind` the way the upstream
//                              `impl From<DomainError> for Error` does, so `catch (LibrespotException e) when (e.Kind == ...)`
//                              and `catch (SpotifyIdException)` both work.
//   * LibrespotException.From(Exception) - the equivalent of the `From<io::Error>`, `From<hyper::Error>`, ... impls:
//                              classifies .NET framework exceptions into an ErrorKind.
//
// Porters: never throw a bare `Exception`; throw a LibrespotException (or subclass), and wrap foreign exceptions
// with `LibrespotException.From(e)` at the boundary where upstream used `?` on a non-librespot error.
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Librespot.OAuth;

namespace Librespot.Core;

/// <summary>Error classification, numerically identical to upstream <c>ErrorKind</c> (which follows gRPC status codes).</summary>
public enum ErrorKind
{
    /// <summary>The operation was cancelled by the caller.</summary>
    Cancelled = 1,
    /// <summary>Unknown error.</summary>
    Unknown = 2,
    /// <summary>Client specified an invalid argument.</summary>
    InvalidArgument = 3,
    /// <summary>Deadline expired before operation could complete.</summary>
    DeadlineExceeded = 4,
    /// <summary>Requested entity was not found.</summary>
    NotFound = 5,
    /// <summary>Attempt to create entity that already exists.</summary>
    AlreadyExists = 6,
    /// <summary>Permission denied.</summary>
    PermissionDenied = 7,
    /// <summary>No valid authentication credentials.</summary>
    Unauthenticated = 16,
    /// <summary>Resource has been exhausted.</summary>
    ResourceExhausted = 8,
    /// <summary>Invalid state.</summary>
    FailedPrecondition = 9,
    /// <summary>Operation aborted.</summary>
    Aborted = 10,
    /// <summary>Operation attempted past the valid range.</summary>
    OutOfRange = 11,
    /// <summary>Not implemented.</summary>
    Unimplemented = 12,
    /// <summary>Internal error.</summary>
    Internal = 13,
    /// <summary>Service unavailable.</summary>
    Unavailable = 14,
    /// <summary>Unrecoverable data loss or corruption.</summary>
    DataLoss = 15,
    /// <summary>Operation must not be used.</summary>
    DoNotUse = -1,
}

public static class ErrorKindExtensions
{
    /// <summary>The upstream <c>#[error("...")]</c> text for the kind.</summary>
    public static string Describe(this ErrorKind kind) => kind switch
    {
        ErrorKind.Cancelled => "The operation was cancelled by the caller",
        ErrorKind.Unknown => "Unknown error",
        ErrorKind.InvalidArgument => "Client specified an invalid argument",
        ErrorKind.DeadlineExceeded => "Deadline expired before operation could complete",
        ErrorKind.NotFound => "Requested entity was not found",
        ErrorKind.AlreadyExists => "Attempt to create entity that already exists",
        ErrorKind.PermissionDenied => "Permission denied",
        ErrorKind.Unauthenticated => "No valid authentication credentials",
        ErrorKind.ResourceExhausted => "Resource has been exhausted",
        ErrorKind.FailedPrecondition => "Invalid state",
        ErrorKind.Aborted => "Operation aborted",
        ErrorKind.OutOfRange => "Operation attempted past the valid range",
        ErrorKind.Unimplemented => "Not implemented",
        ErrorKind.Internal => "Internal error",
        ErrorKind.Unavailable => "Service unavailable",
        ErrorKind.DataLoss => "Unrecoverable data loss or corruption",
        ErrorKind.DoNotUse => "Operation must not be used",
        _ => "Unknown error",
    };
}

/// <summary>
/// The librespot error type (upstream <c>librespot_core::Error</c>). <see cref="Kind"/> classifies the error;
/// <see cref="Detail"/> is the wrapped error text and <see cref="Exception.InnerException"/> the wrapped cause, if any.
/// </summary>
public class LibrespotException : Exception
{
    /// <summary>Classification of the error.</summary>
    public ErrorKind Kind { get; }

    /// <summary>The message of the wrapped error (upstream <c>error.to_string()</c>).</summary>
    public string Detail { get; }

    public LibrespotException(ErrorKind kind, string detail, Exception? innerException = null)
        : base(detail, innerException)
    {
        Kind = kind;
        Detail = detail;
    }

    public LibrespotException(ErrorKind kind, Exception innerException)
        : this(kind, innerException.Message, innerException)
    {
    }

    /// <summary>Formats exactly like upstream <c>Display</c>: <c>"{kind} { {error} }"</c>.</summary>
    public override string Message => $"{Kind.Describe()} {{ {Detail} }}";

    // ---- constructors mirroring upstream `Error::aborted(..)` etc. -------------------------------------------------

    public static LibrespotException Create(ErrorKind kind, string detail, Exception? inner = null) => new(kind, detail, inner);
    public static LibrespotException Create(ErrorKind kind, Exception inner) => new(kind, inner);

    public static LibrespotException Aborted(string detail, Exception? inner = null) => new(ErrorKind.Aborted, detail, inner);
    public static LibrespotException Aborted(Exception inner) => new(ErrorKind.Aborted, inner);
    public static LibrespotException AlreadyExists(string detail, Exception? inner = null) => new(ErrorKind.AlreadyExists, detail, inner);
    public static LibrespotException AlreadyExists(Exception inner) => new(ErrorKind.AlreadyExists, inner);
    public static LibrespotException Cancelled(string detail, Exception? inner = null) => new(ErrorKind.Cancelled, detail, inner);
    public static LibrespotException Cancelled(Exception inner) => new(ErrorKind.Cancelled, inner);
    public static LibrespotException DataLoss(string detail, Exception? inner = null) => new(ErrorKind.DataLoss, detail, inner);
    public static LibrespotException DataLoss(Exception inner) => new(ErrorKind.DataLoss, inner);
    public static LibrespotException DeadlineExceeded(string detail, Exception? inner = null) => new(ErrorKind.DeadlineExceeded, detail, inner);
    public static LibrespotException DeadlineExceeded(Exception inner) => new(ErrorKind.DeadlineExceeded, inner);
    public static LibrespotException DoNotUse(string detail, Exception? inner = null) => new(ErrorKind.DoNotUse, detail, inner);
    public static LibrespotException DoNotUse(Exception inner) => new(ErrorKind.DoNotUse, inner);
    public static LibrespotException FailedPrecondition(string detail, Exception? inner = null) => new(ErrorKind.FailedPrecondition, detail, inner);
    public static LibrespotException FailedPrecondition(Exception inner) => new(ErrorKind.FailedPrecondition, inner);
    public static LibrespotException Internal(string detail, Exception? inner = null) => new(ErrorKind.Internal, detail, inner);
    public static LibrespotException Internal(Exception inner) => new(ErrorKind.Internal, inner);
    public static LibrespotException InvalidArgument(string detail, Exception? inner = null) => new(ErrorKind.InvalidArgument, detail, inner);
    public static LibrespotException InvalidArgument(Exception inner) => new(ErrorKind.InvalidArgument, inner);
    public static LibrespotException NotFound(string detail, Exception? inner = null) => new(ErrorKind.NotFound, detail, inner);
    public static LibrespotException NotFound(Exception inner) => new(ErrorKind.NotFound, inner);
    public static LibrespotException OutOfRange(string detail, Exception? inner = null) => new(ErrorKind.OutOfRange, detail, inner);
    public static LibrespotException OutOfRange(Exception inner) => new(ErrorKind.OutOfRange, inner);
    public static LibrespotException PermissionDenied(string detail, Exception? inner = null) => new(ErrorKind.PermissionDenied, detail, inner);
    public static LibrespotException PermissionDenied(Exception inner) => new(ErrorKind.PermissionDenied, inner);
    public static LibrespotException ResourceExhausted(string detail, Exception? inner = null) => new(ErrorKind.ResourceExhausted, detail, inner);
    public static LibrespotException ResourceExhausted(Exception inner) => new(ErrorKind.ResourceExhausted, inner);
    public static LibrespotException Unauthenticated(string detail, Exception? inner = null) => new(ErrorKind.Unauthenticated, detail, inner);
    public static LibrespotException Unauthenticated(Exception inner) => new(ErrorKind.Unauthenticated, inner);
    public static LibrespotException Unavailable(string detail, Exception? inner = null) => new(ErrorKind.Unavailable, detail, inner);
    public static LibrespotException Unavailable(Exception inner) => new(ErrorKind.Unavailable, inner);
    public static LibrespotException Unimplemented(string detail, Exception? inner = null) => new(ErrorKind.Unimplemented, detail, inner);
    public static LibrespotException Unimplemented(Exception inner) => new(ErrorKind.Unimplemented, inner);
    public static LibrespotException Unknown(string detail, Exception? inner = null) => new(ErrorKind.Unknown, detail, inner);
    public static LibrespotException Unknown(Exception inner) => new(ErrorKind.Unknown, inner);

    // ---- `impl From<X> for Error` -------------------------------------------------------------------------------

    /// <summary>
    /// Classifies an arbitrary exception the way upstream's <c>From</c> impls classify third-party errors.
    /// A <see cref="LibrespotException"/> is returned unchanged.
    /// </summary>
    public static LibrespotException From(Exception e) => e switch
    {
        LibrespotException le => le,
        OAuthException oe => FromOAuth(oe),
        OperationCanceledException => new(ErrorKind.Cancelled, e),
        TimeoutException => new(ErrorKind.DeadlineExceeded, e),
        // std::io::Error mapping
        FileNotFoundException or DirectoryNotFoundException => new(ErrorKind.NotFound, e),
        UnauthorizedAccessException => new(ErrorKind.PermissionDenied, e),
        EndOfStreamException or InvalidDataException => new(ErrorKind.FailedPrecondition, e),
        SocketException se => FromSocket(se),
        IOException { InnerException: SocketException se } => FromSocket(se),
        InvalidProtocolBufferException => new(ErrorKind.FailedPrecondition, e), // protobuf::Error (derives from IOException)
        IOException => new(ErrorKind.Unknown, e),
        // hyper / hyper_util
        HttpRequestException he => FromHttp(he),
        // http crate: invalid header/method/uri
        UriFormatException => new(ErrorKind.InvalidArgument, e),
        FormatException => new(ErrorKind.FailedPrecondition, e), // ParseIntError, time::Parse, url::ParseError, base64, ...
        OverflowException => new(ErrorKind.FailedPrecondition, e), // TryFromIntError
        JsonException => new(ErrorKind.FailedPrecondition, e), // serde_json::Error
        System.Xml.XmlException => new(ErrorKind.FailedPrecondition, e), // quick_xml::Error
        System.Text.DecoderFallbackException => new(ErrorKind.FailedPrecondition, e), // Utf8Error / FromUtf8Error
        ChannelClosedException => new(ErrorKind.Internal, e), // mpsc SendError / oneshot RecvError
        SemaphoreFullException => new(ErrorKind.ResourceExhausted, e), // AcquireError / TryAcquireError
        ArgumentException => new(ErrorKind.InvalidArgument, e),
        NotImplementedException or NotSupportedException => new(ErrorKind.Unimplemented, e),
        _ => new(ErrorKind.Unknown, e),
    };

    private static LibrespotException FromOAuth(OAuthException err) => err.Kind switch
    {
        OAuthErrorKind.AuthCodeBadUri or OAuthErrorKind.AuthCodeNotFound or OAuthErrorKind.AuthCodeListenerRead
            or OAuthErrorKind.AuthCodeListenerParse => Unavailable(err),
        OAuthErrorKind.AuthCodeStdinRead or OAuthErrorKind.AuthCodeListenerBind or OAuthErrorKind.AuthCodeListenerTerminated
            or OAuthErrorKind.AuthCodeListenerWrite or OAuthErrorKind.Recv or OAuthErrorKind.ExchangeCode => Internal(err),
        _ => FailedPrecondition(err),
    };

    private static LibrespotException FromSocket(SocketException err) => err.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse => new(ErrorKind.AlreadyExists, err),
        SocketError.AddressNotAvailable or SocketError.ConnectionRefused or SocketError.NotConnected
            or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostNotFound => new(ErrorKind.Unavailable, err),
        SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown => new(ErrorKind.Aborted, err),
        SocketError.Interrupted or SocketError.WouldBlock or SocketError.OperationAborted => new(ErrorKind.Cancelled, err),
        SocketError.TimedOut => new(ErrorKind.DeadlineExceeded, err),
        SocketError.InvalidArgument => new(ErrorKind.InvalidArgument, err),
        SocketError.AccessDenied => new(ErrorKind.PermissionDenied, err),
        _ => new(ErrorKind.Unknown, err),
    };

    private static LibrespotException FromHttp(HttpRequestException err) => err.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError => new(ErrorKind.Unavailable, err),
        HttpRequestError.InvalidResponse
            or HttpRequestError.HttpProtocolError => new(ErrorKind.Internal, err),
        HttpRequestError.UserAuthenticationError => new(ErrorKind.Unauthenticated, err),
        HttpRequestError.VersionNegotiationError => new(ErrorKind.FailedPrecondition, err),
        HttpRequestError.ExtendedConnectNotSupported => new(ErrorKind.Unimplemented, err),
        _ when err.InnerException is IOException { InnerException: SocketException se } => FromSocket(se),
        _ => new(ErrorKind.Unknown, err),
    };
}
