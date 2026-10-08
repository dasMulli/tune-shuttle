// Port of core/src/session.rs.
//
// Ported now: the facade shape (component accessors, all SessionData getters/setters, UserData, SessionException).
// TODO(port: core-session): ConnectAsync (AP resolution + connect_inner retry loop), the packet sender task,
//   DispatchTask (packet routing + keep-alive state machine: INITIAL_PING_TIMEOUT 20s, PING_TIMEOUT 80s,
//   PONG_DELAY 60s, PONG_ACK_TIMEOUT 20s), ProductInfo XML parsing, check_catalogue, Shutdown wiring.
using Librespot.Core.Dealer;
using Librespot.Core.Logging;
using Librespot.Core.Mercury;
using Microsoft.Extensions.Logging;

namespace Librespot.Core;

/// <summary>Upstream <c>SessionError</c>.</summary>
public sealed class SessionException : LibrespotException
{
    public enum Reason
    {
        /// <summary>Wraps an <see cref="Connection.ApAuthenticationException"/> (kind Unauthenticated).</summary>
        AuthenticationError,
        /// <summary>Cannot create session: I/O error (kind Unavailable).</summary>
        IoError,
        /// <summary>Session is not connected (kind Unavailable).</summary>
        NotConnected,
        /// <summary>Unknown packet (kind Unimplemented).</summary>
        Packet,
    }

    public Reason Cause { get; }

    private SessionException(Reason cause, ErrorKind kind, string detail, Exception? inner = null)
        : base(kind, detail, inner)
    {
        Cause = cause;
    }

    public static SessionException AuthenticationError(Connection.ApAuthenticationException inner) =>
        new(Reason.AuthenticationError, ErrorKind.Unauthenticated, inner.Detail, inner);

    public static SessionException IoError(Exception inner) =>
        new(Reason.IoError, ErrorKind.Unavailable, $"Cannot create session: {inner.Message}", inner);

    public static SessionException NotConnected() =>
        new(Reason.NotConnected, ErrorKind.Unavailable, "Session is not connected");

    public static SessionException Packet(byte cmd) =>
        new(Reason.Packet, ErrorKind.Unimplemented, $"packet {cmd} unknown");
}

/// <summary>Upstream <c>UserAttributes = HashMap&lt;String, String&gt;</c>.</summary>
public sealed class UserAttributes : Dictionary<string, string>
{
    public UserAttributes()
    {
    }

    public UserAttributes(IDictionary<string, string> source) : base(source)
    {
    }
}

/// <summary>Upstream <c>UserData</c>: what the AP tells us about the logged-in user.</summary>
public sealed record UserData
{
    public string Country { get; init; } = "";
    public string CanonicalUsername { get; init; } = "";
    public UserAttributes Attributes { get; init; } = new();
}

/// <summary>
/// A shared reference to a Spotify session. After instantiating, you need to login via
/// <see cref="ConnectAsync"/>. You can either implement the whole playback logic yourself by using this
/// class's interface directly or hand it to a <c>Player</c>.
/// </summary>
/// <remarks>
/// Upstream <c>Session</c> is <c>Arc&lt;SessionInternal&gt;</c>; cloning it is cheap and components hold a
/// <c>SessionWeak</c>. In C# the class reference is the handle; pass it around freely.
/// Note: instances cannot be reused once invalidated. After an unexpectedly closed connection you need
/// to create a new session.
/// </remarks>
public sealed class Session
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Session>();

    private sealed class SessionData
    {
        public string SessionId = "";
        public string ClientId = "";
        public string ClientName = "";
        public string ClientBrandName = "";
        public string ClientModelName = "";
        public string ConnectionId = "";
        public byte[] AuthData = [];
        public long TimeDelta;
        public bool Invalid;
        public string Country = "";
        public string CanonicalUsername = "";
        public UserAttributes Attributes = new();
    }

    private readonly SessionData _data = new();
    private readonly ReaderWriterLockSlim _dataLock = new();

    private readonly Lazy<ApResolver> _apResolver;
    private readonly Lazy<AudioKeyManager> _audioKey;
    private readonly Lazy<ChannelManager> _channel;
    private readonly Lazy<MercuryManager> _mercury;
    private readonly Lazy<DealerManager> _dealer;
    private readonly Lazy<SpClient> _spClient;
    private readonly Lazy<TokenProvider> _tokenProvider;
    private readonly Lazy<Login5Manager> _login5;

    public Session(SessionConfig config, Cache? cache)
    {
        Config = config;
        Cache = cache;
        HttpClient = new HttpClient(config.Proxy);

        Log.LogDebug("new Session");

        _data.ClientId = config.ClientId;
        // can be any guid, doesn't need to be simple
        _data.SessionId = Guid.NewGuid().ToString("N");

        _apResolver = new(() => new ApResolver(this));
        _audioKey = new(() => new AudioKeyManager(this));
        _channel = new(() => new ChannelManager(this));
        _mercury = new(() => new MercuryManager(this));
        _dealer = new(() => new DealerManager(this));
        _spClient = new(() => new SpClient(this));
        _tokenProvider = new(() => new TokenProvider(this));
        _login5 = new(() => new Login5Manager(this));
    }

    /// <summary>
    /// Resolves an access point, connects, authenticates and starts the packet sender and dispatch tasks.
    /// Retries other access points on <c>TryAnotherAP</c> / transport errors (at most 6 APs).
    /// </summary>
    /// <param name="credentials">Password, token or stored-credentials blob.</param>
    /// <param name="storeCredentials">Save the reusable credentials to <see cref="Cache"/> when they changed.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    public Task ConnectAsync(Credentials credentials, bool storeCredentials, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(port: core-session)");

    // ---- component accessors (upstream OnceLock::get_or_init) ----------------------------------------------------

    public ApResolver ApResolver => _apResolver.Value;
    public AudioKeyManager AudioKey => _audioKey.Value;
    public ChannelManager Channel => _channel.Value;
    public HttpClient HttpClient { get; }
    public MercuryManager Mercury => _mercury.Value;
    public DealerManager Dealer => _dealer.Value;
    public SpClient SpClient => _spClient.Value;
    public TokenProvider TokenProvider => _tokenProvider.Value;
    public Login5Manager Login5 => _login5.Value;
    public Cache? Cache { get; }
    public SessionConfig Config { get; }

    /// <summary>Server time minus local time, in seconds, as learned from the last Ping.</summary>
    public long TimeDelta => Read(d => d.TimeDelta);

    internal void SetTimeDelta(long delta) => Write(d => d.TimeDelta = delta);

    /// <summary>Queues a packet for the connection (upstream <c>send_packet</c>); returns immediately.</summary>
    /// <exception cref="SessionException">Not connected.</exception>
    public void SendPacket(PacketType cmd, ReadOnlyMemory<byte> data) =>
        throw new NotImplementedException("TODO(port: core-session)");

    // ---- session data -------------------------------------------------------------------------------------------

    /// <summary>Clones the whole user data (use the specific getters unless you need several fields at once).</summary>
    public UserData UserData => Read(d => new UserData
    {
        Country = d.Country,
        CanonicalUsername = d.CanonicalUsername,
        Attributes = new UserAttributes(d.Attributes),
    });

    public string SessionId => Read(d => d.SessionId);
    public void SetSessionId(string sessionId) => Write(d => d.SessionId = sessionId);

    public string DeviceId => Config.DeviceId;

    public string ClientId => Read(d => d.ClientId);
    public void SetClientId(string clientId) => Write(d => d.ClientId = clientId);

    public string ClientName => Read(d => d.ClientName);
    public void SetClientName(string clientName) => Write(d => d.ClientName = clientName);

    public string ClientBrandName => Read(d => d.ClientBrandName);
    public void SetClientBrandName(string clientBrandName) => Write(d => d.ClientBrandName = clientBrandName);

    public string ClientModelName => Read(d => d.ClientModelName);
    public void SetClientModelName(string clientModelName) => Write(d => d.ClientModelName = clientModelName);

    public string ConnectionId => Read(d => d.ConnectionId);
    public void SetConnectionId(string connectionId) => Write(d => d.ConnectionId = connectionId);

    public string Username => Read(d => d.CanonicalUsername);
    public void SetUsername(string username) => Write(d => d.CanonicalUsername = username);

    public byte[] AuthData => Read(d => d.AuthData.ToArray());
    public void SetAuthData(ReadOnlySpan<byte> authData)
    {
        var copy = authData.ToArray();
        Write(d => d.AuthData = copy);
    }

    public string Country => Read(d => d.Country);
    internal void SetCountry(string country) => Write(d => d.Country = country);

    public bool FilterExplicitContent => GetUserAttribute("filter-explicit-content") == "1";

    /// <summary>The config override if set, else the account's "autoplay" attribute.</summary>
    public bool Autoplay => Config.Autoplay ?? (GetUserAttribute("autoplay") == "1");

    /// <summary>Sets one attribute and returns the previous value, if any.</summary>
    public string? SetUserAttribute(string key, string value)
    {
        CheckCatalogue(new UserAttributes { [key] = value });
        return Write(d =>
        {
            d.Attributes.TryGetValue(key, out var old);
            d.Attributes[key] = value;
            return old;
        });
    }

    public void SetUserAttributes(UserAttributes attributes)
    {
        CheckCatalogue(attributes);
        Write(d =>
        {
            foreach (var (k, v) in attributes)
            {
                d.Attributes[k] = v;
            }
        });
    }

    internal void ReplaceUserAttributes(UserAttributes attributes)
    {
        CheckCatalogue(attributes);
        Write(d => d.Attributes = attributes);
    }

    public string? GetUserAttribute(string key) => Read(d => d.Attributes.TryGetValue(key, out var v) ? v : null);

    /// <summary>Invalidates the session and shuts down mercury and channel managers.</summary>
    public void Shutdown()
    {
        Log.LogDebug("Shutdown: Invalidating session");
        Write(d => d.Invalid = true);
        Mercury.Shutdown();
        Channel.Shutdown();
    }

    public bool IsInvalid => Read(d => d.Invalid);

    /// <summary>
    /// Upstream exits the process for non-premium accounts. The port raises an event instead so hosts can decide.
    /// TODO(port: core-session): wire into ProductInfo handling.
    /// </summary>
    public event Action<string>? UnsupportedAccountType;

    private void CheckCatalogue(UserAttributes attributes)
    {
        if (attributes.TryGetValue("type", out var accountType) && accountType != "premium")
        {
            Log.LogError("librespot does not support {AccountType} accounts.", accountType);
            Log.LogInformation("Please support Spotify and your artists and sign up for a premium account.");
            UnsupportedAccountType?.Invoke(accountType);
        }
    }

    private T Read<T>(Func<SessionData, T> f)
    {
        _dataLock.EnterReadLock();
        try
        {
            return f(_data);
        }
        finally
        {
            _dataLock.ExitReadLock();
        }
    }

    private void Write(Action<SessionData> f)
    {
        _dataLock.EnterWriteLock();
        try
        {
            f(_data);
        }
        finally
        {
            _dataLock.ExitWriteLock();
        }
    }

    private T Write<T>(Func<SessionData, T> f)
    {
        _dataLock.EnterWriteLock();
        try
        {
            return f(_data);
        }
        finally
        {
            _dataLock.ExitWriteLock();
        }
    }
}
