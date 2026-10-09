// Port of core/src/login5.rs.
using System.Net.Http;
using Google.Protobuf;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using Spotify.Login5.V3;
using Spotify.Login5.V3.ChallengeTypes;
using Spotify.Login5.V3.Credentials;
using ProtoDuration = Google.Protobuf.WellKnownTypes.Duration;

namespace Librespot.Core;

/// <summary>Upstream <c>Login5Error</c>.</summary>
public sealed class Login5Exception : LibrespotException
{
    /// <summary>The upstream variant.</summary>
    public enum Reason
    {
        /// <summary>"Login request was denied: {0:?}" (kind FailedPrecondition).</summary>
        FaultyRequest,
        /// <summary>"Code challenge is not supported" (kind Unimplemented).</summary>
        CodeChallenge,
        /// <summary>"Couldn't successfully authenticate after {0} times" (kind FailedPrecondition).</summary>
        RetriesFailed,
        /// <summary>"Login via login5 is only allowed for android or ios" (kind Unavailable).</summary>
        OnlyForMobile,
        /// <summary>"Tried to acquire token without stored credentials" (kind Unavailable).</summary>
        NoStoredCredentials,
    }

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    /// <summary>The server's error, for <see cref="Reason.FaultyRequest"/>.</summary>
    public LoginError? LoginError { get; init; }

    /// <summary>Creates the error; the kind is fixed by the variant like upstream <c>impl From&lt;Login5Error&gt; for Error</c> (prefer the static factories).</summary>
    public Login5Exception(Reason cause, string detail, Exception? inner = null) : base(KindFor(cause), detail, inner)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>Login5Error::FaultyRequest(err)</c>.</summary>
    public static Login5Exception FaultyRequest(LoginError error) =>
        new(Reason.FaultyRequest, $"Login request was denied: {SpClientUtil.ProtoEnumName(error)}") { LoginError = error };

    /// <summary>Upstream <c>Login5Error::CodeChallenge</c>.</summary>
    public static Login5Exception CodeChallenge() =>
        new(Reason.CodeChallenge, "Code challenge is not supported");

    /// <summary>Upstream <c>Login5Error::NoStoredCredentials</c>.</summary>
    public static Login5Exception NoStoredCredentials() =>
        new(Reason.NoStoredCredentials, "Tried to acquire token without stored credentials");

    /// <summary>Upstream <c>Login5Error::RetriesFailed(tries)</c>.</summary>
    public static Login5Exception RetriesFailed(int tries) =>
        new(Reason.RetriesFailed, $"Couldn't successfully authenticate after {tries} times");

    /// <summary>Upstream <c>Login5Error::OnlyForMobile</c>.</summary>
    public static Login5Exception OnlyForMobile() =>
        new(Reason.OnlyForMobile, "Login via login5 is only allowed for android or ios");

    /// <summary>Upstream <c>impl From&lt;Login5Error&gt; for Error</c>.</summary>
    private static ErrorKind KindFor(Reason cause) => cause switch
    {
        Reason.NoStoredCredentials or Reason.OnlyForMobile => ErrorKind.Unavailable,
        Reason.RetriesFailed or Reason.FaultyRequest => ErrorKind.FailedPrecondition,
        Reason.CodeChallenge => ErrorKind.Unimplemented,
        _ => ErrorKind.Internal,
    };
}

/// <summary>login5 API client: exchanges stored credentials (or, on mobile, a password) for an access token (upstream <c>Login5Manager</c>).</summary>
public sealed class Login5Manager : SessionComponent
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Login5Manager>();

    /// <summary>Upstream <c>MAX_LOGIN_TRIES</c>.</summary>
    public const int MaxLoginTries = 3;

    /// <summary>How long to wait after a <c>TIMEOUT</c> / <c>TOO_MANY_ATTEMPTS</c> answer (upstream <c>LOGIN_TIMEOUT</c>).</summary>
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(3);

    private const string LoginUrl = "https://login5.spotify.com/v3/login";

    private Token? _authToken;

    internal Login5Manager(Session session) : base(session)
    {
    }

    private async Task<byte[]> RequestAsync(LoginRequest message, CancellationToken cancellationToken)
    {
        var clientToken = await Session.SpClient.ClientTokenAsync(cancellationToken).ConfigureAwait(false);
        var body = message.ToByteArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, LoginUrl)
        {
            Content = new ByteArrayContent(body),
        };
        SpClient.InsertHeader(request, "Accept", "application/x-protobuf");
        SpClient.InsertHeader(request, SpClient.ClientTokenHeader, clientToken);

        return await Session.HttpClient.RequestBodyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Upstream <c>login5_request(login)</c>. <paramref name="loginRequest"/> carries the login method; this fills in
    /// the client info, sends it and answers challenges until the server grants a token or the tries are used up.
    /// </summary>
    private async Task<LoginOk> Login5RequestAsync(LoginRequest loginRequest, CancellationToken cancellationToken)
    {
        var clientId = Platform.Os switch
        {
            "macos" or "windows" => Session.ClientId,
            // StoredCredential is used to get an access_token from Session credentials.
            // Using the session client_id allows user to use Keymaster on Android/IOS
            // if their Credentials::with_access_token was obtained there, assuming
            // they have overriden the SessionConfig::client_id with the Keymaster's.
            _ when loginRequest.LoginMethodCase == LoginRequest.LoginMethodOneofCase.StoredCredential => Session.ClientId,
            _ => SessionConfig.DefaultForOs(Platform.Os).ClientId,
        };

        loginRequest.ClientInfo = new ClientInfo
        {
            ClientId = clientId,
            DeviceId = Session.DeviceId,
        };

        var response = await RequestAsync(loginRequest, cancellationToken).ConfigureAwait(false);
        var count = 0;

        while (true)
        {
            count++;

            LoginResponse message;
            try
            {
                message = LoginResponse.Parser.ParseFrom(response);
            }
            catch (InvalidProtocolBufferException e)
            {
                throw LibrespotException.From(e);
            }

            if (message.ResponseCase == LoginResponse.ResponseOneofCase.Ok)
            {
                return message.Ok;
            }

            if (message.HasError)
            {
                switch (message.Error)
                {
                    case LoginError.Timeout:
                    case LoginError.TooManyAttempts:
                        await Task.Delay(LoginTimeout, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw Login5Exception.FaultyRequest(message.Error);
                }
            }

            if (message.ResponseCase == LoginResponse.ResponseOneofCase.Challenges)
            {
                // handles the challenges, and updates the login context with the response
                HandleChallenges(loginRequest, message);
            }

            if (count < MaxLoginTries)
            {
                response = await RequestAsync(loginRequest, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw Login5Exception.RetriesFailed(MaxLoginTries);
            }
        }
    }

    /// <summary>
    /// Upstream <c>login(id, password)</c>: login for android and ios. Returns the access token and the stored
    /// credential blob.
    /// </summary>
    /// <remarks>
    /// This request doesn't require a connected session as it is the entrypoint for android or ios. It only works
    /// when the client id is android or ios (adjust it in <see cref="SessionConfig.Default"/>) and the user agent is
    /// android or ios (see <see cref="HttpClient"/>). By setting <see cref="Platform.Os"/> it can be tested on desktop.
    /// </remarks>
    /// <exception cref="Login5Exception">Not on android/ios, the request was denied or the retries were exhausted.</exception>
    public async Task<(Token Token, byte[] StoredCredential)> LoginAsync(string id, string password, CancellationToken cancellationToken = default)
    {
        if (Platform.Os is not ("android" or "ios"))
        {
            // by manipulating the user-agent and client-id it can be also used/tested on desktop
            throw Login5Exception.OnlyForMobile();
        }

        var method = new LoginRequest
        {
            Password = new Password
            {
                Id = id,
                Password_ = password,
            },
        };

        var tokenResponse = await Login5RequestAsync(method, cancellationToken).ConfigureAwait(false);
        var authToken = TokenFromLogin(tokenResponse.AccessToken, tokenResponse.AccessTokenExpiresIn);

        return (authToken, tokenResponse.StoredCredential.ToByteArray());
    }

    /// <summary>
    /// Upstream <c>auth_token()</c>: the cached login5 access token, or a fresh one obtained with the session's
    /// stored credentials (<see cref="Session.AuthData"/>).
    /// </summary>
    /// <remarks>
    /// This request will only work when the stored credentials match the client id. Meaning that stored credentials
    /// generated with the keymaster client id will not work, for example, with the android client id.
    /// </remarks>
    /// <exception cref="Login5Exception">No stored credentials, the request was denied or the retries were exhausted.</exception>
    public async Task<Token> AuthTokenAsync(CancellationToken cancellationToken = default)
    {
        var authData = Session.AuthData;
        if (authData.Length == 0)
        {
            throw Login5Exception.NoStoredCredentials();
        }

        Token? cached;
        lock (Lock)
        {
            if (_authToken is { IsExpired: true })
            {
                _authToken = null;
            }

            cached = _authToken;
        }

        if (cached is not null)
        {
            return cached;
        }

        var method = new LoginRequest
        {
            StoredCredential = new StoredCredential
            {
                Username = Session.Username,
                Data = ByteString.CopyFrom(authData),
            },
        };

        var tokenResponse = await Login5RequestAsync(method, cancellationToken).ConfigureAwait(false);
        var authToken = TokenFromLogin(tokenResponse.AccessToken, tokenResponse.AccessTokenExpiresIn);

        lock (Lock)
        {
            _authToken = authToken;
        }

        Log.LogTrace("Got auth token: {AuthToken}", authToken);

        return authToken;
    }

    /// <summary>
    /// Upstream <c>handle_challenges</c>: solves every hashcash challenge of <paramref name="message"/>, appends the
    /// solutions to <paramref name="loginRequest"/> and copies the login context over.
    /// </summary>
    /// <exception cref="Login5Exception">A code (SMS) challenge was received.</exception>
    /// <exception cref="LibrespotException">A hashcash challenge could not be solved in time (DeadlineExceeded).</exception>
    internal static void HandleChallenges(LoginRequest loginRequest, LoginResponse message)
    {
        var challenges = message.Challenges ?? new Challenges();
        Log.LogDebug("Received {Count} challenges, solving...", challenges.Challenges_.Count);

        foreach (var challenge in challenges.Challenges_)
        {
            if (challenge.ChallengeCase == Challenge.ChallengeOneofCase.Code)
            {
                throw Login5Exception.CodeChallenge();
            }
            else if (challenge.ChallengeCase != Challenge.ChallengeOneofCase.Hashcash)
            {
                Log.LogDebug("Challenge was empty, skipping...");
                continue;
            }

            var hashCashChallenge = challenge.Hashcash;

            var suffix = new byte[0x10];
            var duration = HashCash.Solve(
                message.LoginContext.Span,
                hashCashChallenge.Prefix.Span,
                hashCashChallenge.Length,
                suffix);

            var seconds = duration.Ticks / TimeSpan.TicksPerSecond;
            var nanos = (int)(duration.Ticks % TimeSpan.TicksPerSecond * TimeSpan.NanosecondsPerTick);
            Log.LogDebug("Solving hashcash took {Seconds}s {Nanos}ns", seconds, nanos);

            var solution = new ChallengeSolution
            {
                Hashcash = new HashcashSolution
                {
                    Suffix = ByteString.CopyFrom(suffix),
                    Duration = new ProtoDuration
                    {
                        Seconds = seconds,
                        Nanos = nanos,
                    },
                },
            };

            loginRequest.ChallengeSolutions ??= new ChallengeSolutions();
            loginRequest.ChallengeSolutions.Solutions.Add(solution);
        }

        loginRequest.LoginContext = message.LoginContext;
    }

    /// <summary>Upstream <c>token_from_login</c>: a bearer token; a negative lifetime falls back to one hour.</summary>
    internal static Token TokenFromLogin(string token, int expiresIn) =>
        new(token, TimeSpan.FromSeconds(expiresIn >= 0 ? expiresIn : 3600), "Bearer", [], DateTimeOffset.UtcNow);
}
