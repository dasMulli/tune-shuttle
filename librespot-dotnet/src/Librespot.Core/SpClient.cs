// Port of core/src/spclient.rs.
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Librespot.Core.Dealer;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using Spotify.Clienttoken.Data.V0;
using Spotify.Clienttoken.Http.V0;
using Spotify.Extendedmetadata;
using AutoplayContextRequest = Spotify.Player.Proto.AutoplayContextRequest;
using Context = Spotify.Player.Proto.Context;
using PutStateRequest = Spotify.Connectstate.PutStateRequest;

namespace Librespot.Core;

/// <summary>Upstream <c>SpClientError</c>; every variant has kind <see cref="ErrorKind.FailedPrecondition"/>.</summary>
public sealed class SpClientException : LibrespotException
{
    /// <summary>The upstream variant.</summary>
    public enum Reason
    {
        /// <summary>"missing attribute {0}": a required user attribute is not set.</summary>
        Attribute,
        /// <summary>"expected data but received none".</summary>
        NoData,
        /// <summary>"expected an entry to exist in {0}".</summary>
        ExpectedEntry,
    }

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    /// <summary>Creates the error; the kind is fixed by the variant like upstream <c>impl From&lt;SpClientError&gt; for Error</c> (prefer the static factories).</summary>
    public SpClientException(Reason cause, string detail, Exception? inner = null) : base(KindFor(cause), detail, inner)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>SpClientError::Attribute(name)</c>.</summary>
    public static SpClientException Attribute(string name) =>
        new(Reason.Attribute, $"missing attribute {name}");

    /// <summary>Upstream <c>SpClientError::NoData</c>.</summary>
    public static SpClientException NoData() =>
        new(Reason.NoData, "expected data but received none");

    /// <summary>Upstream <c>SpClientError::ExpectedEntry(field)</c>.</summary>
    public static SpClientException ExpectedEntry(string field) =>
        new(Reason.ExpectedEntry, $"expected an entry to exist in {field}");

    /// <summary>Upstream <c>impl From&lt;SpClientError&gt; for Error</c>: every variant is <see cref="ErrorKind.FailedPrecondition"/>.</summary>
    private static ErrorKind KindFor(Reason cause) => cause switch
    {
        _ => ErrorKind.FailedPrecondition,
    };
}

/// <summary>Upstream <c>RequestStrategy</c>: how often <see cref="SpClient"/> tries a failing request.</summary>
public abstract record RequestStrategy
{
    private RequestStrategy()
    {
    }

    /// <summary>Upstream <c>RequestStrategy::default()</c>: <c>TryTimes(10)</c>.</summary>
    public static RequestStrategy Default { get; } = new TryTimes(10);

    /// <summary>Give up after <paramref name="MaxTries"/> attempts (upstream <c>TryTimes(usize)</c>).</summary>
    public sealed record TryTimes(int MaxTries) : RequestStrategy;

    /// <summary>Retry until the request succeeds or fails with a non-network error (upstream <c>Infinitely</c>).</summary>
    public sealed record Infinitely : RequestStrategy;
}

/// <summary>Upstream <c>RequestOptions</c>.</summary>
/// <param name="Metrics">Append <c>product=0&amp;country=..</c> to the URL.</param>
/// <param name="Salt">Append a random <c>salt=..</c> to defeat caches.</param>
/// <param name="BaseUrl">Use this base URL instead of the resolved spclient access point.</param>
public sealed record RequestOptions(bool Metrics = true, bool Salt = true, string? BaseUrl = null)
{
    /// <summary>Upstream <c>RequestOptions::default()</c>: metrics and salt on, resolved base URL.</summary>
    public static RequestOptions Default { get; } = new();

    /// <summary>Upstream <c>NO_METRICS_AND_SALT</c>.</summary>
    public static RequestOptions NoMetricsAndSalt { get; } = new(Metrics: false, Salt: false);
}

/// <summary>Upstream <c>TransferRequest</c>: the JSON body of <see cref="SpClient.TransferAsync"/>.</summary>
/// <param name="TransferOptions">How the receiving device should restore the playback.</param>
public sealed record TransferRequest(
    [property: JsonPropertyName("transfer_options")] TransferOptions TransferOptions);

/// <summary>HTTP client for the spclient API, the client-token endpoint and the CDN (upstream <c>SpClient</c>).</summary>
public sealed class SpClient : SessionComponent
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<SpClient>();

    /// <summary>Header carrying the client token (upstream <c>CLIENT_TOKEN</c>).</summary>
    public const string ClientTokenHeader = "client-token";

    /// <summary>Header carrying the dealer connection id (upstream <c>CONNECTION_ID</c>).</summary>
    private const string ConnectionIdHeader = "x-spotify-connection-id";

    private const string ClientTokenUrl = "https://clienttoken.spotify.com/v1/clienttoken";

    private SocketAddress? _accesspoint;
    private RequestStrategy _strategy = RequestStrategy.Default;
    private Token? _clientToken;

    internal SpClient(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>set_strategy</c>.</summary>
    public void SetStrategy(RequestStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        lock (Lock)
        {
            _strategy = strategy;
        }
    }

    /// <summary>Upstream <c>flush_accesspoint</c>: forgets the memoized access point so the next request resolves a new one.</summary>
    public Task FlushAccesspointAsync()
    {
        lock (Lock)
        {
            _accesspoint = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>Upstream <c>get_accesspoint</c>: the memoized spclient access point, resolved on first use.</summary>
    public async Task<SocketAddress> GetAccesspointAsync(CancellationToken cancellationToken = default)
    {
        // Memoize the current access point.
        SocketAddress? ap;
        lock (Lock)
        {
            ap = _accesspoint;
        }

        if (ap is { } tuple)
        {
            return tuple;
        }

        tuple = await Session.ApResolver.ResolveAsync("spclient", cancellationToken).ConfigureAwait(false);
        lock (Lock)
        {
            _accesspoint = tuple;
        }

        Log.LogInformation("Resolved \"{Host}:{Port}\" as spclient access point", tuple.Host, tuple.Port);
        return tuple;
    }

    /// <summary>Upstream <c>base_url</c>: <c>https://{host}:{port}</c> of the spclient access point.</summary>
    public async Task<string> BaseUrlAsync(CancellationToken cancellationToken = default)
    {
        var ap = await GetAccesspointAsync(cancellationToken).ConfigureAwait(false);
        return $"https://{ap.Host}:{ap.Port}";
    }

    private async Task<byte[]> ClientTokenRequestAsync(IMessage message, CancellationToken cancellationToken)
    {
        var body = message.ToByteArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, ClientTokenUrl)
        {
            Content = new ByteArrayContent(body),
        };
        InsertHeader(request, "Accept", "application/x-protobuf");

        return await Session.HttpClient.RequestBodyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Upstream <c>client_token</c>: the cached client token, or a new one from the client-token endpoint, solving
    /// the hash cash challenge the endpoint may present first (at most three tries).
    /// </summary>
    /// <exception cref="LibrespotException">The endpoint failed, sent no or unknown response types, or no challenge could be solved.</exception>
    public async Task<string> ClientTokenAsync(CancellationToken cancellationToken = default)
    {
        Token? cached;
        lock (Lock)
        {
            if (_clientToken is { IsExpired: true })
            {
                _clientToken = null;
            }

            cached = _clientToken;
        }

        if (cached is not null)
        {
            return cached.AccessToken;
        }

        Log.LogDebug("Client token unavailable or expired, requesting new token.");

        // Current state of affairs: keymaster ID works on all tested platforms, but may be phased out,
        // so it seems a good idea to mimick the real clients. `self.session().client_id()` returns the
        // ID of the client that last connected, but requesting a client token with this ID only works
        // on macOS and Windows. On Android and iOS we can send a platform-specific client ID and are
        // then presented with a hash cash challenge. On Linux, we have to pass the old keymaster ID.
        // We delegate most of this logic to `SessionConfig`.
        var os = Platform.Os;
        var clientId = os switch
        {
            "macos" or "windows" => Session.ClientId,
            _ => SessionConfig.DefaultForOs(os).ClientId,
        };

        var request = BuildClientTokenRequest(os, clientId, Session.DeviceId, Platform.OsVersion(), KernelVersion(), Arch());

        var response = await ClientTokenRequestAsync(request, cancellationToken).ConfigureAwait(false);
        var count = 0;
        const int MaxTries = 3;

        ClientTokenResponse tokenResponse;
        while (true)
        {
            count++;

            ClientTokenResponse message;
            try
            {
                message = ClientTokenResponse.Parser.ParseFrom(response);
            }
            catch (InvalidProtocolBufferException e)
            {
                throw LibrespotException.From(e);
            }

            var responseType = message.ResponseType;
            if (responseType == ClientTokenResponseType.ResponseGrantedTokenResponse)
            {
                // depending on the platform, you're either given a token immediately
                // or are presented a hash cash challenge to solve first
                Log.LogDebug("Received a granted token");
                tokenResponse = message;
                break;
            }

            if (responseType == ClientTokenResponseType.ResponseChallengesResponse)
            {
                Log.LogDebug("Received a hash cash challenge, solving...");

                var challenges = message.Challenges ?? new ChallengesResponse();
                var state = challenges.State;
                if (challenges.Challenges.Count == 0)
                {
                    throw LibrespotException.FailedPrecondition("No challenges found");
                }

                var hashCashChallenge = challenges.Challenges[0].EvaluateHashcashParameters ?? new HashCashParameters();

                byte[] prefix;
                try
                {
                    prefix = Convert.FromHexString(hashCashChallenge.Prefix);
                }
                catch (FormatException e)
                {
                    throw LibrespotException.FailedPrecondition($"Unable to decode hash cash challenge: {e.Message}", e);
                }

                var length = hashCashChallenge.Length;

                var suffix = new byte[0x10];
                var solved = false;
                try
                {
                    HashCash.Solve([], prefix, length, suffix);
                    solved = true;
                }
                catch (LibrespotException e)
                {
                    Log.LogTrace("Unable to solve hash cash challenge {Count}/{MaxTries}: {Error}", count, MaxTries, e.Message);
                }

                if (solved)
                {
                    var answerMessage = BuildChallengeAnswer(state, suffix);

                    Log.LogTrace("Answering hash cash challenge");
                    try
                    {
                        response = await ClientTokenRequestAsync(answerMessage, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (LibrespotException e)
                    {
                        Log.LogTrace("Answer not accepted {Count}/{MaxTries}: {Error}", count, MaxTries, e.Message);
                    }
                }

                if (count < MaxTries)
                {
                    response = await ClientTokenRequestAsync(request, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw LibrespotException.FailedPrecondition($"Unable to solve any of {MaxTries} hash cash challenges");
                }

                continue;
            }

            if (Enum.IsDefined(responseType))
            {
                throw LibrespotException.Unimplemented($"Unknown client token response type: {SpClientUtil.ProtoEnumName(responseType)}");
            }

            throw LibrespotException.FailedPrecondition("No client token response type");
        }

        var grantedToken = tokenResponse.GrantedToken ?? new GrantedTokenResponse();
        var accessToken = grantedToken.Token;

        lock (Lock)
        {
            _clientToken = new Token(
                accessToken,
                TimeSpan.FromSeconds(grantedToken.RefreshAfterSeconds >= 0 ? grantedToken.RefreshAfterSeconds : 7200),
                "client-token",
                grantedToken.Domains.Select(d => d.Domain).ToList(),
                DateTimeOffset.UtcNow);
        }

        Log.LogTrace("Got client token: {GrantedToken}", grantedToken);

        return accessToken;
    }

    /// <summary>
    /// The <c>REQUEST_CLIENT_DATA_REQUEST</c> message of upstream <c>client_token</c>, with the platform-specific data
    /// the real clients send for <paramref name="os"/> (Rust <c>std::env::consts::OS</c> vocabulary).
    /// </summary>
    internal static ClientTokenRequest BuildClientTokenRequest(string os, string clientId, string deviceId, string osVersion, string kernelVersion, string arch)
    {
        var platformData = new PlatformSpecificData();

        switch (os)
        {
            case "windows":
            {
                var windowsOsVersion = double.TryParse(osVersion, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                    ? SaturatingToInt32(v)
                    : 10;
                var windowsKernelVersion = int.TryParse(kernelVersion, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var k)
                    ? k
                    : 21370;

                var (pe, imageFile) = arch switch
                {
                    "arm" => (448, 452),
                    "aarch64" => (43620, 452),
                    "x86_64" => (34404, 34404),
                    _ => (332, 332), // x86
                };

                platformData.DesktopWindows = new NativeDesktopWindowsData
                {
                    OsVersion = windowsOsVersion,
                    OsBuild = windowsKernelVersion,
                    PlatformId = 2,
                    UnknownValue6 = 9,
                    ImageFileMachine = imageFile,
                    PeMachine = pe,
                    UnknownValue10 = true,
                };
                break;
            }
            case "ios":
                platformData.Ios = new NativeIOSData
                {
                    UserInterfaceIdiom = 0,
                    TargetIphoneSimulator = false,
                    HwMachine = "iPhone14,5",
                    SystemVersion = osVersion,
                };
                break;
            case "android":
                platformData.Android = new NativeAndroidData
                {
                    AndroidVersion = osVersion,
                    ApiVersion = 31,
                    DeviceName = "Pixel",
                    ModelStr = "GF5KQ",
                    Vendor = "Google",
                };
                break;
            case "macos":
                platformData.DesktopMacos = new NativeDesktopMacOSData
                {
                    SystemVersion = osVersion,
                    HwModel = "iMac21,1",
                    CompiledCpuType = arch,
                };
                break;
            default:
                platformData.DesktopLinux = new NativeDesktopLinuxData
                {
                    SystemName = "Linux",
                    SystemRelease = kernelVersion,
                    SystemVersion = osVersion,
                    Hardware = arch,
                };
                break;
        }

        return new ClientTokenRequest
        {
            RequestType = ClientTokenRequestType.RequestClientDataRequest,
            ClientData = new ClientDataRequest
            {
                // upstream spotify_semantic_version(), keyed on the same OS as the platform data
                ClientVersion = os is "android" or "ios" ? LibrespotVersion.SpotifyMobileVersion : LibrespotVersion.SpotifySemanticVersion,
                ClientId = clientId,
                ConnectivitySdkData = new ConnectivitySdkData
                {
                    DeviceId = deviceId,
                    PlatformSpecificData = platformData,
                },
            },
        };
    }

    /// <summary>The <c>REQUEST_CHALLENGE_ANSWERS_REQUEST</c> answering a hash cash challenge with <paramref name="suffix"/>.</summary>
    internal static ClientTokenRequest BuildChallengeAnswer(string state, ReadOnlySpan<byte> suffix)
    {
        var answer = new ChallengeAnswer
        {
            // the suffix must be in uppercase
            HashCash = new HashCashAnswer { Suffix = Convert.ToHexString(suffix) },
            ChallengeType = ChallengeType.ChallengeHashCash,
        };

        var answers = new ChallengeAnswersRequest { State = state };
        answers.Answers.Add(answer);

        return new ClientTokenRequest
        {
            RequestType = ClientTokenRequestType.RequestChallengeAnswersRequest,
            ChallengeAnswers = answers,
        };
    }

    /// <summary>Upstream <c>request_with_protobuf</c>: <see cref="RequestWithProtobufAndOptionsAsync"/> with the default options.</summary>
    public Task<byte[]> RequestWithProtobufAsync(string method, string endpoint, IReadOnlyDictionary<string, string>? headers, IMessage message, CancellationToken cancellationToken = default) =>
        RequestWithProtobufAndOptionsAsync(method, endpoint, headers, message, RequestOptions.Default, cancellationToken);

    /// <summary>Upstream <c>request_with_protobuf_and_options</c>: serializes <paramref name="message"/> and sends it as <c>application/x-protobuf</c>.</summary>
    public Task<byte[]> RequestWithProtobufAndOptionsAsync(string method, string endpoint, IReadOnlyDictionary<string, string>? headers, IMessage message, RequestOptions options, CancellationToken cancellationToken = default)
    {
        var body = message.ToByteArray();

        var allHeaders = CopyHeaders(headers);
        allHeaders["Content-Type"] = "application/x-protobuf";

        return RequestWithOptionsAsync(method, endpoint, allHeaders, body, options, cancellationToken);
    }

    /// <summary>Upstream <c>request_as_json</c>: <see cref="RequestAsync"/> with <c>Accept: application/json</c>.</summary>
    public Task<byte[]> RequestAsJsonAsync(string method, string endpoint, IReadOnlyDictionary<string, string>? headers, string? body, CancellationToken cancellationToken = default)
    {
        var allHeaders = CopyHeaders(headers);
        allHeaders["Accept"] = "application/json";

        return RequestAsync(method, endpoint, allHeaders, body is null ? null : Encoding.UTF8.GetBytes(body), cancellationToken);
    }

    /// <summary>Upstream <c>request</c>: <see cref="RequestWithOptionsAsync"/> with the default options.</summary>
    public Task<byte[]> RequestAsync(string method, string endpoint, IReadOnlyDictionary<string, string>? headers, ReadOnlyMemory<byte>? body, CancellationToken cancellationToken = default) =>
        RequestWithOptionsAsync(method, endpoint, headers, body, RequestOptions.Default, cancellationToken);

    /// <summary>
    /// Upstream <c>request_with_options</c>: sends <paramref name="method"/> to <c>base_url + endpoint</c> with the
    /// login5 bearer token and (if obtainable) the client token, retrying according to <see cref="SetStrategy"/>.
    /// Network errors (Unavailable, DeadlineExceeded) are retried and the access point is re-resolved every third
    /// try; any other error ends the retries.
    /// </summary>
    /// <returns>The response body.</returns>
    /// <exception cref="LibrespotException">The last error once the strategy gives up.</exception>
    public async Task<byte[]> RequestWithOptionsAsync(string method, string endpoint, IReadOnlyDictionary<string, string>? headers, ReadOnlyMemory<byte>? body, RequestOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var tries = 0;
        LibrespotException lastError;

        var bodyBytes = body?.ToArray() ?? [];
        var httpMethod = ParseMethod(method);

        while (true)
        {
            tries++;

            // Reconnection logic: retrieve the endpoint every iteration, so we can try
            // another access point when we are experiencing network issues (see below).
            var url = new StringBuilder(options.BaseUrl ?? await BaseUrlAsync(cancellationToken).ConfigureAwait(false));
            url.Append(endpoint);

            // Add metrics. There is also an optional `partner` key with a value like
            // `vodafone-uk` but we've yet to discover how we can find that value.
            // For the sake of documentation you could also do "product=free" but
            // we only support premium anyway.
            if (options.Metrics && !url.ToString().Contains("product=0", StringComparison.Ordinal))
            {
                url.Append(UrlUtil.GetNextQuerySeparator(url.ToString()))
                    .Append("product=0&country=")
                    .Append(Session.Country);
            }

            // Defeat caches. Spotify-generated URLs already contain this.
            if (options.Salt && !url.ToString().Contains("salt=", StringComparison.Ordinal))
            {
                url.Append(UrlUtil.GetNextQuerySeparator(url.ToString()))
                    .Append("salt=")
                    .Append(Random.Shared.NextInt64(0, 1L << 32).ToString(CultureInfo.InvariantCulture));
            }

            using var request = new HttpRequestMessage(httpMethod, ParseUri(url.ToString()))
            {
                // Upstream always sends the body with an explicit Content-Length (0 for no body).
                Content = new ByteArrayContent(bodyBytes),
            };

            // Reconnection logic: keep getting (cached) tokens because they might have expired.
            var token = await Session.Login5.AuthTokenAsync(cancellationToken).ConfigureAwait(false);

            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                {
                    InsertHeader(request, name, value);
                }
            }

            InsertHeader(request, "Authorization", $"{token.TokenType} {token.AccessToken}");

            string? clientToken = null;
            try
            {
                clientToken = await ClientTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (LibrespotException e)
            {
                // currently these endpoints seem to work fine without it
                Log.LogWarning("Unable to get client token: {Error} Trying to continue without...", e.Message);
            }

            if (clientToken is not null)
            {
                InsertHeader(request, ClientTokenHeader, clientToken);
            }

            try
            {
                return await Session.HttpClient.RequestBodyAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (LibrespotException e)
            {
                lastError = e;
            }

            // Break before the reconnection logic below, so that the current access point
            // is retained when max_tries == 1. Leave it up to the caller when to flush.
            RequestStrategy strategy;
            lock (Lock)
            {
                strategy = _strategy;
            }

            if (strategy is RequestStrategy.TryTimes { MaxTries: var maxTries } && tries >= maxTries)
            {
                break;
            }

            // Reconnection logic: drop the current access point if we are experiencing issues.
            // This will cause the next call to base_url() to resolve a new one.
            if (lastError.Kind is ErrorKind.Unavailable or ErrorKind.DeadlineExceeded)
            {
                // Keep trying the current access point three times before dropping it.
                if (tries % 3 == 0)
                {
                    await FlushAccesspointAsync().ConfigureAwait(false);
                }
            }
            else
            {
                // if we can't build the request now, then we won't ever
                break;
            }

            Log.LogDebug("Error was: {Error}", lastError.Message);
        }

        ExceptionDispatchInfo.Capture(lastError).Throw();
        throw lastError; // unreachable
    }

    /// <summary>Upstream <c>put_connect_state_request</c>: publishes this device's connect state.</summary>
    public Task<byte[]> PutConnectStateRequestAsync(PutStateRequest state, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/connect-state/v1/devices/{Session.DeviceId}";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ConnectionIdHeader] = Session.ConnectionId,
        };

        return RequestWithProtobufAsync("PUT", endpoint, headers, state, cancellationToken);
    }

    /// <summary>Upstream <c>delete_connect_state_request</c>: removes this device from the connect device list.</summary>
    public Task<byte[]> DeleteConnectStateRequestAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = $"/connect-state/v1/devices/{Session.DeviceId}";
        return RequestAsync("DELETE", endpoint, null, null, cancellationToken);
    }

    /// <summary>Upstream <c>put_connect_state_inactive(notify)</c>: marks this device inactive.</summary>
    public Task<byte[]> PutConnectStateInactiveAsync(bool notify, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/connect-state/v1/devices/{Session.DeviceId}/inactive?notify={RustBool(notify)}";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ConnectionIdHeader] = Session.ConnectionId,
        };

        return RequestAsync("PUT", endpoint, headers, null, cancellationToken);
    }

    /// <summary>Upstream <c>get_extended_metadata</c>: a batched extended-metadata request.</summary>
    public async Task<BatchedExtensionResponse> GetExtendedMetadataAsync(BatchedEntityRequest request, CancellationToken cancellationToken = default)
    {
        var res = await RequestWithProtobufAsync("POST", "/extended-metadata/v0/extended-metadata", null, request, cancellationToken).ConfigureAwait(false);
        try
        {
            return BatchedExtensionResponse.Parser.ParseFrom(res);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw LibrespotException.From(e);
        }
    }

    /// <summary>Upstream <c>get_metadata(kind, id)</c>: the raw extension data of one entity.</summary>
    /// <exception cref="SpClientException">The response has no entry (<see cref="SpClientException.Reason.ExpectedEntry"/>).</exception>
    public async Task<byte[]> GetMetadataAsync(ExtensionKind kind, SpotifyUri id, CancellationToken cancellationToken = default)
    {
        var entityRequest = new EntityRequest { EntityUri = id.ToUri() };
        entityRequest.Query.Add(new ExtensionQuery { ExtensionKind = kind });

        var req = new BatchedEntityRequest();
        req.EntityRequest.Add(entityRequest);

        var res = await GetExtendedMetadataAsync(req, cancellationToken).ConfigureAwait(false);

        // upstream pops the last entries
        if (res.ExtendedMetadata.Count == 0)
        {
            throw SpClientException.ExpectedEntry("extended_metadata");
        }

        var extendedMetadata = res.ExtendedMetadata[^1];

        if (extendedMetadata.ExtensionData.Count == 0)
        {
            throw SpClientException.ExpectedEntry("extension_data");
        }

        var data = extendedMetadata.ExtensionData[^1];

        return data.ExtensionData is { } any
            ? any.Value.ToByteArray()
            : throw SpClientException.ExpectedEntry("data");
    }

    /// <summary>Upstream <c>get_track_metadata</c>: a serialized <c>Spotify.Metadata.Track</c>.</summary>
    public Task<byte[]> GetTrackMetadataAsync(SpotifyUri trackUri, CancellationToken cancellationToken = default) =>
        GetMetadataAsync(ExtensionKind.TrackV4, trackUri, cancellationToken);

    /// <summary>Upstream <c>get_episode_metadata</c>: a serialized <c>Spotify.Metadata.Episode</c>.</summary>
    public Task<byte[]> GetEpisodeMetadataAsync(SpotifyUri episodeUri, CancellationToken cancellationToken = default) =>
        GetMetadataAsync(ExtensionKind.EpisodeV4, episodeUri, cancellationToken);

    /// <summary>Upstream <c>get_album_metadata</c>: a serialized <c>Spotify.Metadata.Album</c>.</summary>
    public Task<byte[]> GetAlbumMetadataAsync(SpotifyUri albumUri, CancellationToken cancellationToken = default) =>
        GetMetadataAsync(ExtensionKind.AlbumV4, albumUri, cancellationToken);

    /// <summary>Upstream <c>get_artist_metadata</c>: a serialized <c>Spotify.Metadata.Artist</c>.</summary>
    public Task<byte[]> GetArtistMetadataAsync(SpotifyUri artistUri, CancellationToken cancellationToken = default) =>
        GetMetadataAsync(ExtensionKind.ArtistV4, artistUri, cancellationToken);

    /// <summary>Upstream <c>get_show_metadata</c>: a serialized <c>Spotify.Metadata.Show</c>.</summary>
    public Task<byte[]> GetShowMetadataAsync(SpotifyUri showUri, CancellationToken cancellationToken = default) =>
        GetMetadataAsync(ExtensionKind.ShowV4, showUri, cancellationToken);

    /// <summary>Upstream <c>get_lyrics</c>: the color-lyrics JSON of a track.</summary>
    public Task<byte[]> GetLyricsAsync(SpotifyId trackId, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/color-lyrics/v2/track/{trackId.ToBase62()}";
        return RequestAsJsonAsync("GET", endpoint, null, null, cancellationToken);
    }

    /// <summary>Upstream <c>get_lyrics_for_image</c>: the color-lyrics JSON of a track for a cover image.</summary>
    public Task<byte[]> GetLyricsForImageAsync(SpotifyId trackId, FileId imageId, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/color-lyrics/v2/track/{trackId.ToBase62()}/image/spotify:image:{imageId}";
        return RequestAsJsonAsync("GET", endpoint, null, null, cancellationToken);
    }

    /// <summary>Upstream <c>get_playlist</c>: a serialized <c>SelectedListContent</c>.</summary>
    public Task<byte[]> GetPlaylistAsync(SpotifyId playlistId, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/playlist/v2/playlist/{playlistId.ToBase62()}";
        return RequestAsync("GET", endpoint, null, null, cancellationToken);
    }

    /// <summary>Upstream <c>get_user_profile</c>: the profile JSON of a user.</summary>
    public Task<byte[]> GetUserProfileAsync(string username, int? playlistLimit, int? artistLimit, CancellationToken cancellationToken = default)
    {
        var endpoint = new StringBuilder($"/user-profile-view/v3/profile/{username}");

        if (playlistLimit is not null || artistLimit is not null)
        {
            endpoint.Append('?');

            if (playlistLimit is { } playlists)
            {
                endpoint.Append(CultureInfo.InvariantCulture, $"playlist_limit={playlists}");
                if (artistLimit is not null)
                {
                    endpoint.Append('&');
                }
            }

            if (artistLimit is { } artists)
            {
                endpoint.Append(CultureInfo.InvariantCulture, $"artist_limit={artists}");
            }
        }

        return RequestAsJsonAsync("GET", endpoint.ToString(), null, null, cancellationToken);
    }

    /// <summary>Upstream <c>get_user_followers</c>.</summary>
    public Task<byte[]> GetUserFollowersAsync(string username, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/user-profile-view/v3/profile/{username}/followers";
        return RequestAsJsonAsync("GET", endpoint, null, null, cancellationToken);
    }

    /// <summary>Upstream <c>get_user_following</c>.</summary>
    public Task<byte[]> GetUserFollowingAsync(string username, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/user-profile-view/v3/profile/{username}/following";
        return RequestAsJsonAsync("GET", endpoint, null, null, cancellationToken);
    }

    /// <summary>Upstream <c>get_radio_for_track</c>: the "inspired by" mix JSON for a seed track.</summary>
    public Task<byte[]> GetRadioForTrackAsync(SpotifyUri trackUri, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/inspiredby-mix/v2/seed_to_playlist/{trackUri.ToUri()}?response-format=json";
        return RequestAsJsonAsync("GET", endpoint, null, null, cancellationToken);
    }

    /// <summary>
    /// Upstream <c>get_apollo_station</c>. Known working scopes: <c>stations</c>, <c>tracks</c>; Spotify's default
    /// <paramref name="count"/> is 50.
    /// </summary>
    /// <remarks>
    /// For other scopes see https://gist.github.com/roderickvd/62df5b74d2179a12de6817a37bb474f9. Seen-in-the-wild but
    /// unimplemented query parameters: <c>image_style=gradient_overlay</c>, <c>excludeClusters=true</c>,
    /// <c>language=en</c>, <c>count_tracks=0</c>, <c>market=from_token</c>.
    /// </remarks>
    public Task<byte[]> GetApolloStationAsync(string scope, string contextUri, int? count, IReadOnlyList<SpotifyId> previousTracks, bool autoplay, CancellationToken cancellationToken = default)
    {
        var endpoint = new StringBuilder($"/radio-apollo/v3/{scope}/{contextUri}?autoplay={RustBool(autoplay)}");

        // Spotify has a default of 50
        if (count is { } c)
        {
            endpoint.Append(CultureInfo.InvariantCulture, $"&count={c}");
        }

        var previousTrackStr = string.Join(",", previousTracks.Select(id => id.ToBase62()));
        // better than checking `previous_tracks.len() > 0` because the `filter_map` could still return 0 items
        if (previousTrackStr.Length > 0)
        {
            endpoint.Append("&prev_tracks=").Append(previousTrackStr);
        }

        return RequestAsJsonAsync("GET", endpoint.ToString(), null, null, cancellationToken);
    }

    /// <summary>Upstream <c>get_next_page</c>: follows a context page URL (leading <c>hm:/</c> stripped).</summary>
    public Task<byte[]> GetNextPageAsync(string nextPageUri, CancellationToken cancellationToken = default)
    {
        // Rust `trim_start_matches` removes every leading repetition of the pattern.
        var endpoint = nextPageUri;
        while (endpoint.StartsWith("hm:/", StringComparison.Ordinal))
        {
            endpoint = endpoint["hm:/".Length..];
        }

        return RequestAsJsonAsync("GET", endpoint, null, null, cancellationToken);
    }

    // Upstream note: seen-in-the-wild but unimplemented endpoints
    // - /presence-view/v1/buddylist

    /// <summary>Upstream <c>get_audio_storage</c>: a serialized <c>StorageResolveResponse</c> for an audio file.</summary>
    public Task<byte[]> GetAudioStorageAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/storage-resolve/files/audio/interactive/{fileId.ToBase16()}";
        return RequestAsync("GET", endpoint, null, null, cancellationToken);
    }

    /// <summary>
    /// Upstream <c>stream_from_cdn(cdn_url, offset, length)</c>: a GET of one URL from <see cref="CdnUrl.TryGetUrls"/>
    /// with <c>Range: bytes={offset}-{offset+length-1}</c>, sent with <c>HttpCompletionOption.ResponseHeadersRead</c>
    /// through <see cref="HttpClient.RequestStreamAsync"/>. The caller (audio-fetch) needs the whole response:
    /// the status (206 Partial Content expected, 429 handled with <see cref="HttpClient.GetRetryAfter"/>), the
    /// <c>Content-Range</c> header (file size) and the body stream (<c>Content.ReadAsStreamAsync</c>).
    /// Non-2xx statuses are returned, not thrown. The caller disposes the response.
    /// </summary>
    /// <exception cref="LibrespotException">The URL or range is invalid (InvalidArgument) or the connection failed.</exception>
    public Task<HttpResponseMessage> StreamFromCdnAsync(string cdnUrl, long offset, long length, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ParseUri(cdnUrl));
        InsertHeader(request, "Range", $"bytes={offset}-{offset + length - 1}");

        // The request has no content to dispose; the returned response references it (RequestMessage).
        return Session.HttpClient.RequestStreamAsync(request, cancellationToken);
    }

    /// <summary>Upstream <c>request_url</c>: a plain GET of an absolute URL (no tokens, no retries).</summary>
    public async Task<byte[]> RequestUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ParseUri(url));
        return await Session.HttpClient.RequestBodyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upstream <c>get_audio_preview</c>: audio preview in 96 kbps MP3, unencrypted.</summary>
    /// <exception cref="SpClientException">The <c>audio-preview-url-template</c> user attribute is missing.</exception>
    public Task<byte[]> GetAudioPreviewAsync(FileId previewId, CancellationToken cancellationToken = default)
    {
        const string attribute = "audio-preview-url-template";
        var template = Session.GetUserAttribute(attribute) ?? throw SpClientException.Attribute(attribute);

        var url = template.Replace("{id}", previewId.ToBase16(), StringComparison.Ordinal);
        var separator = url.Contains('?') ? "&" : "?";
        url = $"{url}{separator}cid={Session.ClientId}";

        return RequestUrlAsync(url, cancellationToken);
    }

    /// <summary>Upstream <c>get_head_file</c>: the first 128 kB of a track, unencrypted.</summary>
    /// <exception cref="SpClientException">The <c>head-files-url</c> user attribute is missing.</exception>
    public Task<byte[]> GetHeadFileAsync(FileId fileId, CancellationToken cancellationToken = default)
    {
        const string attribute = "head-files-url";
        var template = Session.GetUserAttribute(attribute) ?? throw SpClientException.Attribute(attribute);

        var url = template.Replace("{file_id}", fileId.ToBase16(), StringComparison.Ordinal);

        return RequestUrlAsync(url, cancellationToken);
    }

    /// <summary>Upstream <c>get_image</c>: an image by file id.</summary>
    /// <exception cref="SpClientException">The <c>image-url</c> user attribute is missing.</exception>
    public Task<byte[]> GetImageAsync(FileId imageId, CancellationToken cancellationToken = default)
    {
        const string attribute = "image-url";
        var template = Session.GetUserAttribute(attribute) ?? throw SpClientException.Attribute(attribute);
        var url = template.Replace("{file_id}", imageId.ToBase16(), StringComparison.Ordinal);

        return RequestUrlAsync(url, cancellationToken);
    }

    /// <summary>
    /// Upstream <c>get_context(uri)</c>: request the context for an uri.
    /// </summary>
    /// <remarks>
    /// All <see cref="SpotifyId"/> uris are supported in addition to the following special uris:
    /// liked songs (all: <c>spotify:user:&lt;user_id&gt;:collection</c>, of artist:
    /// <c>spotify:user:&lt;user_id&gt;:collection:artist:&lt;artist_id&gt;</c>) and search
    /// (<c>spotify:search:&lt;search+query&gt;</c>, whitespaces are replaced with <c>+</c>).
    /// Query params found in the wild: <c>include_video=true</c>.
    /// Known results of uri types: <c>track</c> returns a single page with a single track (when requesting a single
    /// track with a query in the request, the returned track uri <b>will</b> contain the query); <c>artist</c>
    /// returns 2 pages with tracks (10 most popular tracks and latest/popular album), remaining pages are artist
    /// albums sorted by popularity (only provided as page_url); <c>search</c> is massively influenced by the provided
    /// query, the query result shown by the search expects no query at all, uri looks like
    /// <c>spotify:search:never+gonna</c>.
    /// </remarks>
    /// <exception cref="SpClientException">Empty response (<see cref="SpClientException.Reason.NoData"/>).</exception>
    /// <exception cref="LibrespotException">The request failed or the JSON is not a valid <c>Context</c> (FailedPrecondition).</exception>
    public async Task<Context> GetContextAsync(string uri, CancellationToken cancellationToken = default)
    {
        var endpoint = $"/context-resolve/v1/{uri}";

        var res = await RequestWithOptionsAsync("GET", endpoint, null, null, RequestOptions.NoMetricsAndSalt, cancellationToken).ConfigureAwait(false);
        return ParseContext(res);
    }

    /// <summary>Upstream <c>get_autoplay_context</c>: resolves the autoplay context for a request.</summary>
    /// <exception cref="SpClientException">Empty response (<see cref="SpClientException.Reason.NoData"/>).</exception>
    /// <exception cref="LibrespotException">The request failed or the JSON is not a valid <c>Context</c> (FailedPrecondition).</exception>
    public async Task<Context> GetAutoplayContextAsync(AutoplayContextRequest request, CancellationToken cancellationToken = default)
    {
        var res = await RequestWithProtobufAndOptionsAsync(
            "POST",
            "/context-resolve/v1/autoplay",
            null,
            request,
            RequestOptions.NoMetricsAndSalt,
            cancellationToken).ConfigureAwait(false);

        return ParseContext(res);
    }

    /// <summary>Upstream <c>get_rootlist(from, length)</c>: the user's playlist root list (default length 120).</summary>
    public Task<byte[]> GetRootlistAsync(int from, int? length, CancellationToken cancellationToken = default)
    {
        var len = length ?? 120;
        var user = Session.Username;
        var endpoint = $"/playlist/v2/user/{user}/rootlist?decorate=revision,attributes,length,owner,capabilities,status_code&from={from}&length={len}";

        return RequestAsync("GET", endpoint, null, null, cancellationToken);
    }

    /// <summary>
    /// Upstream <c>transfer(from_device_id, to_device_id, transfer_request)</c>: triggers the transfer of the playback
    /// from one device to another.
    /// </summary>
    /// <remarks>
    /// Using the same device id for <paramref name="fromDeviceId"/> and <paramref name="toDeviceId"/> initiates the
    /// transfer from the currently active device.
    /// </remarks>
    public Task<byte[]> TransferAsync(string fromDeviceId, string toDeviceId, TransferRequest? transferRequest, CancellationToken cancellationToken = default)
    {
        byte[]? body = transferRequest is null ? null : JsonSerializer.SerializeToUtf8Bytes(transferRequest);

        var endpoint = $"/connect-state/v1/connect/transfer/from/{fromDeviceId}/to/{toDeviceId}";
        return RequestWithOptionsAsync("POST", endpoint, null, body, RequestOptions.NoMetricsAndSalt, cancellationToken);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>HeaderMap::insert</c> on a request: replaces any existing value. Content headers go to the request content
    /// (created empty if needed). An invalid name or value is an <see cref="ErrorKind.InvalidArgument"/> error, like
    /// upstream's <c>InvalidHeaderValue</c>.
    /// </summary>
    internal static void InsertHeader(HttpRequestMessage request, string name, string value)
    {
        try
        {
            if (IsContentHeader(name))
            {
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.Remove(name);
                request.Content.Headers.Add(name, value);
            }
            else
            {
                request.Headers.Remove(name);
                request.Headers.Add(name, value);
            }
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            throw LibrespotException.InvalidArgument($"invalid HTTP header {name}: {e.Message}", e);
        }
    }

    private static bool IsContentHeader(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Allow", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> CopyHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                copy[name] = value;
            }
        }

        return copy;
    }

    private static HttpMethod ParseMethod(string method)
    {
        try
        {
            return HttpMethod.Parse(method);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw LibrespotException.InvalidArgument($"invalid HTTP method {method}", e);
        }
    }

    private static Uri ParseUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : throw LibrespotException.InvalidArgument($"invalid uri: {url}");

    private static Context ParseContext(byte[] res)
    {
        string ctxJson;
        try
        {
            ctxJson = SpClientUtil.StrictUtf8.GetString(res);
        }
        catch (DecoderFallbackException e)
        {
            throw LibrespotException.From(e);
        }

        if (ctxJson.Length == 0)
        {
            throw SpClientException.NoData();
        }

        try
        {
            return JsonParser.Default.Parse<Context>(ctxJson);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.LogTrace("failed parsing context: {ContextJson}", ctxJson);
            throw LibrespotException.FailedPrecondition(e);
        }
    }

    /// <summary>Rust's <c>Display</c> for <c>bool</c>.</summary>
    private static string RustBool(bool value) => value ? "true" : "false";

    private static int SaturatingToInt32(double value) =>
        double.IsNaN(value) ? 0 : value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;

    /// <summary>
    /// <c>sysinfo::System::kernel_version()</c> (falling back to "0"): the build number on Windows, the kernel release
    /// on Linux, the OS version elsewhere.
    /// </summary>
    private static string KernelVersion()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        }

        if (OperatingSystem.IsLinux())
        {
            // OSDescription is "Linux <release> <version>", like `uname -srv`.
            var parts = RuntimeInformation.OSDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return parts[1];
            }
        }

        var version = Environment.OSVersion.Version;
        return version.Major == 0 ? "0" : version.ToString();
    }

    /// <summary><c>std::env::consts::ARCH</c> of the running process.</summary>
    private static string Arch() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x86_64",
        Architecture.Arm => "arm",
        Architecture.Arm64 => "aarch64",
        Architecture.LoongArch64 => "loongarch64",
        Architecture.RiscV64 => "riscv64",
        Architecture.S390x => "s390x",
        Architecture.Ppc64le => "powerpc64",
        var other => other.ToString().ToLowerInvariant(),
    };
}

/// <summary>Helpers shared by the spclient, login5 and token ports.</summary>
internal static class SpClientUtil
{
    /// <summary>A UTF-8 decoder that throws on invalid bytes, like Rust's <c>String::from_utf8</c>.</summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The proto name of an enum value (what rust-protobuf's <c>Debug</c> prints, e.g. <c>INVALID_CREDENTIALS</c>).</summary>
    internal static string ProtoEnumName<T>(T value) where T : struct, Enum =>
        typeof(T).GetField(value.ToString())?.GetCustomAttribute<OriginalNameAttribute>()?.Name ?? value.ToString();
}
