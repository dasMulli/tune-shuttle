// Port of core/src/session.rs.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using System.Xml;
using Librespot.Core.Connection;
using Librespot.Core.Dealer;
using Librespot.Core.Logging;
using Librespot.Core.Mercury;
using Librespot.Protocol.Authentication;
using Microsoft.Extensions.Logging;
using ProtoErrorCode = Librespot.Protocol.Keyexchange.ErrorCode;

namespace Librespot.Core;

/// <summary>Upstream <c>SessionError</c>.</summary>
public sealed class SessionException : LibrespotException
{
    /// <summary>The upstream variants.</summary>
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

    /// <summary>Which variant this is.</summary>
    public Reason Cause { get; }

    private SessionException(Reason cause, ErrorKind kind, string detail, Exception? inner = null)
        : base(kind, detail, inner)
    {
        Cause = cause;
    }

    /// <summary>Upstream <c>SessionError::AuthenticationError</c>.</summary>
    public static SessionException AuthenticationError(Connection.ApAuthenticationException inner) =>
        new(Reason.AuthenticationError, ErrorKind.Unauthenticated, inner.Detail, inner);

    /// <summary>Upstream <c>SessionError::IoError</c>.</summary>
    public static SessionException IoError(Exception inner) =>
        new(Reason.IoError, ErrorKind.Unavailable, $"Cannot create session: {inner.Message}", inner);

    /// <summary>Upstream <c>SessionError::NotConnected</c>.</summary>
    public static SessionException NotConnected() =>
        new(Reason.NotConnected, ErrorKind.Unavailable, "Session is not connected");

    /// <summary>Upstream <c>SessionError::Packet(cmd)</c>.</summary>
    public static SessionException Packet(byte cmd) =>
        new(Reason.Packet, ErrorKind.Unimplemented, $"packet {cmd} unknown");
}

/// <summary>Upstream <c>UserAttributes = HashMap&lt;String, String&gt;</c>.</summary>
public sealed class UserAttributes : Dictionary<string, string>
{
    /// <summary>An empty attribute map.</summary>
    public UserAttributes()
    {
    }

    /// <summary>A copy of <paramref name="source"/>.</summary>
    public UserAttributes(IDictionary<string, string> source) : base(source)
    {
    }
}

/// <summary>Upstream <c>UserData</c>: what the AP tells us about the logged-in user.</summary>
public sealed record UserData
{
    /// <summary>Country code from the CountryCode packet.</summary>
    public string Country { get; init; } = "";

    /// <summary>The canonical username from the APWelcome.</summary>
    public string CanonicalUsername { get; init; } = "";

    /// <summary>Product info attributes.</summary>
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
/// Upstream closes the connection when the last <c>Session</c> is dropped; the port does that in
/// <see cref="DisposeAsync"/>, which the owner must call.
/// Note: instances cannot be reused once invalidated. After an unexpectedly closed connection you need
/// to create a new session.
/// </remarks>
public sealed class Session : IAsyncDisposable
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<Session>();

    /// <summary>Upstream <c>INITIAL_PING_TIMEOUT</c>.</summary>
    internal static readonly TimeSpan InitialPingTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Upstream <c>PING_TIMEOUT</c>: 60s expected + 20s buffer.</summary>
    internal static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(80);

    /// <summary>Upstream <c>PONG_DELAY</c>.</summary>
    internal static readonly TimeSpan PongDelay = TimeSpan.FromSeconds(60);

    /// <summary>Upstream <c>PONG_ACK_TIMEOUT</c>.</summary>
    internal static readonly TimeSpan PongAckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The four keep-alive timeouts of the dispatch task. Upstream has only the constants; the port lets tests shorten
    /// them through <see cref="StartTasks"/>.
    /// </summary>
    internal sealed record KeepAliveTimeouts(TimeSpan InitialPing, TimeSpan Ping, TimeSpan PongDelay, TimeSpan PongAck)
    {
        /// <summary>The upstream constants.</summary>
        public static readonly KeepAliveTimeouts Default = new(InitialPingTimeout, PingTimeout, Session.PongDelay, PongAckTimeout);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

    // upstream tx_connection: OnceLock<mpsc::UnboundedSender<(u8, Vec<u8>)>>
    private ChannelWriter<(byte Cmd, ReadOnlyMemory<byte> Data)>? _txConnection;

    // Replaces upstream's Drop: cancelled by DisposeAsync to stop the sender and dispatch tasks.
    private readonly CancellationTokenSource _lifetime = new();
    private Transport? _transport;
    private Task? _senderTask;
    private Task? _dispatchTask;
    private int _disposed;

    /// <summary>Creates an unconnected session (upstream <c>Session::new</c>).</summary>
    /// <param name="config">Session configuration.</param>
    /// <param name="cache">Optional credentials/volume/audio cache.</param>
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

    /// <summary>Upstream <c>connect_inner</c>: connect + authenticate to one access point.</summary>
    private async Task<(Credentials Credentials, Transport Transport)> ConnectInnerAsync(
        SocketAddress accessPoint,
        Credentials credentials,
        CancellationToken cancellationToken)
    {
        const byte MaxRetries = 1;
        var transport = await ApConnection.ConnectWithRetryAsync(
            accessPoint.Host,
            accessPoint.Port,
            Config.Proxy,
            MaxRetries,
            cancellationToken).ConfigureAwait(false);

        Credentials reusableCredentials;
        try
        {
            reusableCredentials = await ApConnection.AuthenticateAsync(transport, credentials, Config.DeviceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Might be able to remove this once keymaster is replaced with login5.
        if (credentials.AuthType == AuthenticationType.AuthenticationSpotifyToken)
        {
            Log.LogTrace("Reconnect using stored credentials as token authed sessions cannot use keymaster.");

            // Upstream drops the first transport when it reassigns the variable.
            await transport.DisposeAsync().ConfigureAwait(false);
            transport = await ApConnection.ConnectWithRetryAsync(
                accessPoint.Host,
                accessPoint.Port,
                Config.Proxy,
                MaxRetries,
                cancellationToken).ConfigureAwait(false);
            try
            {
                reusableCredentials = await ApConnection.AuthenticateAsync(transport, reusableCredentials, Config.DeviceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        return (reusableCredentials, transport);
    }

    /// <summary>
    /// Resolves an access point, connects, authenticates and starts the packet sender and dispatch tasks.
    /// Retries other access points on <c>TryAnotherAP</c> / transport errors (at most 6 APs); other login failures
    /// are thrown immediately.
    /// </summary>
    /// <param name="credentials">Password, token or stored-credentials blob.</param>
    /// <param name="storeCredentials">Save the reusable credentials to <see cref="Cache"/> when they changed.</param>
    /// <param name="cancellationToken">Cancels the connection attempt (not the established session).</param>
    /// <exception cref="LibrespotException">All attempts failed, the login was refused, or the session is already connected.</exception>
    public async Task ConnectAsync(Credentials credentials, bool storeCredentials, CancellationToken cancellationToken = default)
    {
        // There currently happen to be 6 APs but anything will do to avoid an infinite loop.
        const byte MaxApTries = 6;
        byte numApTries = 0;
        Credentials reusableCredentials;
        Transport transport;
        while (true)
        {
            var ap = await ApResolver.ResolveAsync("accesspoint", cancellationToken).ConfigureAwait(false);
            Log.LogInformation("Connecting to AP \"{Host}:{Port}\"", ap.Host, ap.Port);
            try
            {
                (reusableCredentials, transport) = await ConnectInnerAsync(ap, credentials, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                numApTries++;
                if (numApTries == MaxApTries)
                {
                    Log.LogError("Tried too many access points");
                    if (e is LibrespotException)
                    {
                        throw;
                    }

                    throw LibrespotException.From(e);
                }

                if (e is ApAuthenticationException { Cause: ApAuthenticationException.Reason.LoginFailed, ErrorCode: ProtoErrorCode.TryAnotherAp })
                {
                    Log.LogWarning("Instructed to try another access point...");
                    continue;
                }

                if (e is ApAuthenticationException { Cause: ApAuthenticationException.Reason.LoginFailed })
                {
                    throw;
                }

                Log.LogWarning("Try another access point...");
            }
        }

        var username = reusableCredentials.Username ?? "UNKNOWN";
        Log.LogInformation("Authenticated as '{Username}' !", username);
        SetUsername(username);
        SetAuthData(reusableCredentials.AuthData);
        if (Cache is { } cache && storeCredentials)
        {
            var credChanged = cache.Credentials() is not { } cached || cached != reusableCredentials;
            if (credChanged)
            {
                cache.SaveCredentials(reusableCredentials);
            }
        }

        StartTasks(transport);
    }

    /// <summary>
    /// The tail of upstream <c>connect</c>: installs the packet queue and spawns the sender and dispatch tasks over an
    /// authenticated transport. Internal so tests can drive a session over a loopback transport.
    /// </summary>
    /// <param name="transport">The authenticated connection; owned by the session from here on.</param>
    /// <param name="timeouts">Keep-alive timeouts; null uses the upstream constants (tests shorten them).</param>
    internal void StartTasks(Transport transport, KeepAliveTimeouts? timeouts = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            // The session was disposed while connecting: drop the transport like the duplicate-connect branch below.
            DisposeInBackground(transport);
            throw SessionException.NotConnected();
        }

        // This channel serves as a buffer for packets and serializes access to the transport, such
        // that SendPacket can return immediately and needs no additional synchronization.
        var connection = System.Threading.Channels.Channel.CreateUnbounded<(byte Cmd, ReadOnlyMemory<byte> Data)>(
            new UnboundedChannelOptions { SingleReader = true });
        if (Interlocked.CompareExchange(ref _txConnection, connection.Writer, null) is not null)
        {
            // Upstream drops the new transport and returns the error.
            DisposeInBackground(transport);
            throw SessionException.NotConnected();
        }

        _transport = transport;
        var lifetime = _lifetime.Token;
        var keepAlive = timeouts ?? KeepAliveTimeouts.Default;
        _senderTask = Task.Run(() => RunSenderAsync(transport, connection, lifetime), CancellationToken.None);
        _dispatchTask = Task.Run(() => new DispatchTask(this, transport, keepAlive).RunAsync(lifetime), CancellationToken.None);
    }

    private static void DisposeInBackground(Transport transport) =>
        _ = transport.DisposeAsync().AsTask().ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Upstream's sender task: <c>UnboundedReceiverStream(rx_connection).forward(sink)</c>.</summary>
    private async Task RunSenderAsync(Transport transport, Channel<(byte Cmd, ReadOnlyMemory<byte> Data)> packets, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (cmd, data) in packets.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await transport.SendAsync(cmd, data, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Session disposed (upstream: the session was dropped).
        }
        catch (Exception e)
        {
            // Upstream's forward() drops rx_connection when the sink fails, so later send_packet calls fail with a
            // SendError; completing the writer makes SendPacket throw the same way (kind Internal).
            packets.Writer.TryComplete();
            Log.LogError("{Error}", e.Message);
            if (!IsInvalid)
            {
                Shutdown();
            }
        }
    }

    // ---- component accessors (upstream OnceLock::get_or_init) ----------------------------------------------------

    /// <summary>Upstream <c>apresolver()</c>.</summary>
    public ApResolver ApResolver => _apResolver.Value;

    /// <summary>Upstream <c>audio_key()</c>.</summary>
    public AudioKeyManager AudioKey => _audioKey.Value;

    /// <summary>Upstream <c>channel()</c>.</summary>
    public ChannelManager Channel => _channel.Value;

    /// <summary>Upstream <c>http_client()</c>.</summary>
    public HttpClient HttpClient { get; }

    /// <summary>Upstream <c>mercury()</c>.</summary>
    public MercuryManager Mercury => _mercury.Value;

    /// <summary>Upstream <c>dealer()</c>.</summary>
    public DealerManager Dealer => _dealer.Value;

    /// <summary>Upstream <c>spclient()</c>.</summary>
    public SpClient SpClient => _spClient.Value;

    /// <summary>Upstream <c>token_provider()</c>.</summary>
    public TokenProvider TokenProvider => _tokenProvider.Value;

    /// <summary>Upstream <c>login5()</c>.</summary>
    public Login5Manager Login5 => _login5.Value;

    /// <summary>Upstream <c>cache()</c>.</summary>
    public Cache? Cache { get; }

    /// <summary>Upstream <c>config()</c>.</summary>
    public SessionConfig Config { get; }

    /// <summary>Server time minus local time, in seconds, as learned from the last Ping.</summary>
    public long TimeDelta => Read(d => d.TimeDelta);

    internal void SetTimeDelta(long delta) => Write(d => d.TimeDelta = delta);

    /// <summary>Queues a packet for the connection (upstream <c>send_packet</c>); returns immediately.</summary>
    /// <exception cref="SessionException">Not connected.</exception>
    /// <exception cref="LibrespotException">The packet queue is closed (kind Internal).</exception>
    public void SendPacket(PacketType cmd, ReadOnlyMemory<byte> data)
    {
        if (Volatile.Read(ref _txConnection) is not { } tx)
        {
            throw SessionException.NotConnected();
        }

        if (!tx.TryWrite(((byte)cmd, data)))
        {
            throw LibrespotException.From(new ChannelClosedException());
        }
    }

    // ---- session data -------------------------------------------------------------------------------------------

    /// <summary>Clones the whole user data (use the specific getters unless you need several fields at once).</summary>
    public UserData UserData => Read(d => new UserData
    {
        Country = d.Country,
        CanonicalUsername = d.CanonicalUsername,
        Attributes = new UserAttributes(d.Attributes),
    });

    /// <summary>Upstream <c>session_id()</c>.</summary>
    public string SessionId => Read(d => d.SessionId);

    /// <summary>Upstream <c>set_session_id</c>.</summary>
    public void SetSessionId(string sessionId) => Write(d => d.SessionId = sessionId);

    /// <summary>Upstream <c>device_id()</c>.</summary>
    public string DeviceId => Config.DeviceId;

    /// <summary>Upstream <c>client_id()</c>.</summary>
    public string ClientId => Read(d => d.ClientId);

    /// <summary>Upstream <c>set_client_id</c>.</summary>
    public void SetClientId(string clientId) => Write(d => d.ClientId = clientId);

    /// <summary>Upstream <c>client_name()</c>.</summary>
    public string ClientName => Read(d => d.ClientName);

    /// <summary>Upstream <c>set_client_name</c>.</summary>
    public void SetClientName(string clientName) => Write(d => d.ClientName = clientName);

    /// <summary>Upstream <c>client_brand_name()</c>.</summary>
    public string ClientBrandName => Read(d => d.ClientBrandName);

    /// <summary>Upstream <c>set_client_brand_name</c>.</summary>
    public void SetClientBrandName(string clientBrandName) => Write(d => d.ClientBrandName = clientBrandName);

    /// <summary>Upstream <c>client_model_name()</c>.</summary>
    public string ClientModelName => Read(d => d.ClientModelName);

    /// <summary>Upstream <c>set_client_model_name</c>.</summary>
    public void SetClientModelName(string clientModelName) => Write(d => d.ClientModelName = clientModelName);

    /// <summary>Upstream <c>connection_id()</c>.</summary>
    public string ConnectionId => Read(d => d.ConnectionId);

    /// <summary>Upstream <c>set_connection_id</c>.</summary>
    public void SetConnectionId(string connectionId) => Write(d => d.ConnectionId = connectionId);

    /// <summary>Upstream <c>username()</c>: the canonical username.</summary>
    public string Username => Read(d => d.CanonicalUsername);

    /// <summary>Upstream <c>set_username</c>.</summary>
    public void SetUsername(string username) => Write(d => d.CanonicalUsername = username);

    /// <summary>Upstream <c>auth_data()</c>: the reusable credentials blob.</summary>
    public byte[] AuthData => Read(d => d.AuthData.ToArray());

    /// <summary>Upstream <c>set_auth_data</c>.</summary>
    public void SetAuthData(ReadOnlySpan<byte> authData)
    {
        var copy = authData.ToArray();
        Write(d => d.AuthData = copy);
    }

    /// <summary>Upstream <c>country()</c>.</summary>
    public string Country => Read(d => d.Country);

    internal void SetCountry(string country) => Write(d => d.Country = country);

    /// <summary>Upstream <c>filter_explicit_content()</c>.</summary>
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

    /// <summary>Upstream <c>set_user_attributes</c>: merges <paramref name="attributes"/> into the current ones.</summary>
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

    /// <summary>Upstream <c>get_user_attribute</c>.</summary>
    public string? GetUserAttribute(string key) => Read(d => d.Attributes.TryGetValue(key, out var v) ? v : null);

    /// <summary>Invalidates the session and shuts down mercury and channel managers.</summary>
    public void Shutdown()
    {
        Log.LogDebug("Shutdown: Invalidating session");
        Write(d => d.Invalid = true);
        Mercury.Shutdown();
        Channel.Shutdown();
    }

    /// <summary>Upstream <c>is_invalid()</c>.</summary>
    public bool IsInvalid => Read(d => d.Invalid);

    /// <summary>
    /// Raised with the account type when the AP reports a non-premium account. Upstream logs an error and exits the
    /// process (<c>exit(1)</c>); the port raises this event instead so the host (the CLI) can decide.
    /// </summary>
    public event Action<string>? UnsupportedAccountType;

    private void CheckCatalogue(UserAttributes attributes)
    {
        if (attributes.TryGetValue("type", out var accountType) && accountType != "premium")
        {
            Log.LogError("librespot does not support \"{AccountType}\" accounts.", accountType);
            Log.LogInformation("Please support Spotify and your artists and sign up for a premium account.");
            UnsupportedAccountType?.Invoke(accountType);
        }
    }

    /// <summary>
    /// Closes the connection and stops the background tasks (upstream: dropping the last <c>Session</c>). Pending
    /// mercury requests and channels are failed as upstream's dropped components would. Packets still queued are
    /// discarded.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Log.LogDebug("drop Session");

        if (!IsInvalid)
        {
            Shutdown();
        }

        Volatile.Read(ref _txConnection)?.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_transport is { } transport)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var task in new[] { _senderTask, _dispatchTask })
        {
            if (task is not null)
            {
                // Both tasks observe their own exceptions; this only waits for them to unwind.
                await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        _lifetime.Dispose();
    }

    /// <summary>
    /// Parses the ProductInfo XML into attributes the way upstream's quick-xml loop does: every text node inside a
    /// start tag is stored under that tag (the last one wins), the key is reset by every end tag, and parse errors
    /// are logged.
    /// </summary>
    /// <remarks>
    /// Deviations forced by <see cref="XmlReader"/>: upstream keys by the raw start-tag content (name plus attributes
    /// as written); the port rebuilds it as <c>name attr="value"</c>. quick-xml 0.38 reports entity references as
    /// separate events, so upstream keeps only the text after the last reference; the port keeps the whole unescaped
    /// text. quick-xml continues after an error, <see cref="XmlReader"/> stops.
    /// </remarks>
    internal static UserAttributes ParseProductInfo(ReadOnlySpan<byte> data)
    {
        var text = StrictUtf8.GetString(data);

        var currentElement = "";
        var userAttributes = new UserAttributes();

        var settings = new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Auto,
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            XmlResolver = null,
        };

        using var reader = XmlReader.Create(new StringReader(text), settings);
        try
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when !reader.IsEmptyElement:
                        currentElement = StartTagContent(reader);
                        break;
                    case XmlNodeType.EndElement:
                        currentElement = "";
                        break;
                    case XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        if (currentElement.Length != 0)
                        {
                            userAttributes[currentElement] = reader.Value;
                        }

                        break;
                }
            }
        }
        catch (XmlException e)
        {
            Log.LogWarning("Error parsing XML at position {Line}:{Column}: {Error}", e.LineNumber, e.LinePosition, e.Message);
        }

        return userAttributes;
    }

    private static string StartTagContent(XmlReader reader)
    {
        if (!reader.HasAttributes)
        {
            return reader.Name;
        }

        var builder = new StringBuilder(reader.Name);
        while (reader.MoveToNextAttribute())
        {
            builder.Append(' ').Append(reader.Name).Append("=\"").Append(reader.Value).Append('"');
        }

        reader.MoveToElement();
        return builder.ToString();
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

    /// <summary>Upstream <c>KeepAliveState</c>.</summary>
    internal enum KeepAliveState
    {
        /// <summary>Expecting a Ping from the server, either after startup or after a PongAck.</summary>
        ExpectingPing,

        /// <summary>We need to send a Pong at the given time.</summary>
        PendingPong,

        /// <summary>We just sent a Pong and wait for it be ACK'd.</summary>
        ExpectingPongAck,
    }

    /// <summary>
    /// Upstream <c>DispatchTask</c>: reads packets from the AP, routes them to the components and runs the keep-alive
    /// state machine. Upstream's resettable <c>Sleep</c> is a <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
    /// recreated whenever the deadline changes; a packet that is ready wins over an elapsed timeout, as upstream polls
    /// the stream first.
    /// </summary>
    private sealed class DispatchTask(Session session, Transport stream, KeepAliveTimeouts timeouts)
    {
        private KeepAliveState _keepAliveState = KeepAliveState.ExpectingPing;
        private long _deadline;
        private bool _deadlineChanged;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            ResetTimeout(timeouts.InitialPing);

            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<ApPacket?>? receive = null;
            CancellationTokenSource? timeoutCts = null;
            Task? timeout = null;
            try
            {
                while (true)
                {
                    receive ??= stream.ReceiveAsync(receiveCts.Token).AsTask();

                    if (timeout is null || _deadlineChanged)
                    {
                        if (timeoutCts is not null)
                        {
                            await timeoutCts.CancelAsync().ConfigureAwait(false);
                            timeoutCts.Dispose();
                        }

                        timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _deadline);
                        timeout = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeoutCts.Token);
                        _deadlineChanged = false;
                    }

                    // Process all messages that are immediately ready (WhenAny prefers the first completed task).
                    var completed = await Task.WhenAny(receive, timeout).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Session disposed (upstream: the session can no longer be upgraded).
                        return;
                    }

                    if (completed == receive)
                    {
                        ApPacket? packet;
                        try
                        {
                            packet = await receive.ConfigureAwait(false);
                        }
                        catch (Exception) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception e)
                        {
                            receive = null;
                            Log.LogError(e, "Connection to server closed.");
                            session.Shutdown();
                            return;
                        }

                        receive = null;

                        if (packet is not { } p)
                        {
                            Log.LogWarning("Connection to server closed.");
                            session.Shutdown();
                            return;
                        }

                        try
                        {
                            Dispatch(p.Cmd, p.Payload);
                        }
                        catch (Exception e)
                        {
                            Log.LogDebug("could not dispatch command: {Error}", e.Message);
                        }

                        continue;
                    }

                    // Handle the keep-alive sequence, returning an error when we haven't received a
                    // Ping/PongAck for too long.
                    //
                    // The expected keepalive sequence is
                    // - Server: Ping
                    // - wait 60s
                    // - Client: Pong
                    // - Server: PongAck
                    // - wait 60s
                    // - repeat
                    //
                    // This means that we silently lost connection to Spotify servers if
                    // - we don't receive Ping immediately after connecting,
                    // - we don't receive a Ping 60s after the last PongAck or
                    // - we don't receive a PongAck immediately after our Pong.
                    //
                    // Currently, we add a safety margin of 20s to these expected deadlines.
                    switch (_keepAliveState)
                    {
                        case KeepAliveState.ExpectingPing:
                        case KeepAliveState.ExpectingPongAck:
                            if (!session.IsInvalid)
                            {
                                session.Shutdown();
                            }

                            // Upstream ends the task with an io::Error(TimedOut) that nobody observes (and notes that
                            // it could optionally reconnect); the port logs it at debug level.
                            Log.LogDebug("session lost connection to server ({State})", _keepAliveState);
                            return;

                        case KeepAliveState.PendingPong:
                            Log.LogTrace("Sending Pong");
                            // Upstream notes this should ideally flush the sink before starting the timeout.
                            try
                            {
                                session.SendPacket(PacketType.Pong, new byte[] { 0, 0, 0, 0 });
                            }
                            catch (LibrespotException)
                            {
                                // let _ = session.send_packet(..)
                            }

                            _keepAliveState = KeepAliveState.ExpectingPongAck;
                            ResetTimeout(timeouts.PongAck);
                            DebugState();
                            break;
                    }
                }
            }
            finally
            {
                await receiveCts.CancelAsync().ConfigureAwait(false);
                if (timeoutCts is not null)
                {
                    await timeoutCts.CancelAsync().ConfigureAwait(false);
                    timeoutCts.Dispose();
                }

                // A receive still in flight is abandoned (upstream drops the stream); observe its outcome.
                receive?.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                Log.LogDebug("drop Dispatch");
            }
        }

        private void ResetTimeout(TimeSpan delay)
        {
            _deadline = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
            _deadlineChanged = true;
        }

        /// <summary>Upstream <c>KeepAliveState::debug</c>.</summary>
        private void DebugState()
        {
            if (Log.IsEnabled(LogLevel.Trace))
            {
                var delay = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _deadline).TotalSeconds;
                Log.LogTrace("keep-alive state: {State}, timeout in {Delay:F1}", _keepAliveState, delay < 0 ? double.PositiveInfinity : delay);
            }
        }

        /// <summary>Upstream <c>DispatchTask::dispatch</c>.</summary>
        private void Dispatch(byte cmd, ReadOnlyMemory<byte> data)
        {
            if (PacketTypeExtensions.FromByte(cmd) is not { } packetType)
            {
                Log.LogTrace("Ignoring unknown packet {Cmd:x}", cmd);
                throw SessionException.Packet(cmd);
            }

            switch (packetType)
            {
                case PacketType.Ping:
                {
                    Log.LogTrace("Received Ping");
                    if (_keepAliveState != KeepAliveState.ExpectingPing)
                    {
                        Log.LogWarning("Received unexpected Ping from server");
                    }

                    _keepAliveState = KeepAliveState.PendingPong;
                    ResetTimeout(timeouts.PongDelay);
                    DebugState();

                    long serverTimestamp = BinaryPrimitives.ReadUInt32BigEndian(data.Span);
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    session.SetTimeDelta(serverTimestamp - timestamp);

                    // Upstream's debug_info() logs the Arc strong/weak counts; there is no equivalent in .NET.
                    break;
                }

                case PacketType.PongAck:
                    Log.LogTrace("Received PongAck");
                    if (_keepAliveState != KeepAliveState.ExpectingPongAck)
                    {
                        Log.LogWarning("Received unexpected PongAck from server");
                    }

                    _keepAliveState = KeepAliveState.ExpectingPing;
                    ResetTimeout(timeouts.Ping);
                    DebugState();
                    break;

                case PacketType.CountryCode:
                {
                    string country;
                    try
                    {
                        country = StrictUtf8.GetString(data.Span);
                    }
                    catch (DecoderFallbackException e)
                    {
                        throw LibrespotException.From(e);
                    }

                    Log.LogInformation("Country: \"{Country}\"", country);
                    session.SetCountry(country);
                    break;
                }

                case PacketType.StreamChunkRes or PacketType.ChannelError:
                    session.Channel.Dispatch(packetType, data);
                    break;

                case PacketType.AesKey or PacketType.AesKeyError:
                    session.AudioKey.Dispatch(packetType, data);
                    break;

                case PacketType.MercuryReq or PacketType.MercurySub or PacketType.MercuryUnsub or PacketType.MercuryEvent:
                    session.Mercury.Dispatch(packetType, data);
                    break;

                case PacketType.ProductInfo:
                {
                    UserAttributes userAttributes;
                    try
                    {
                        userAttributes = ParseProductInfo(data.Span);
                    }
                    catch (DecoderFallbackException e)
                    {
                        throw LibrespotException.From(e);
                    }

                    if (Log.IsEnabled(LogLevel.Trace))
                    {
                        Log.LogTrace(
                            "Received product info: {Attributes}",
                            string.Join(", ", userAttributes.Select(kv => $"\"{kv.Key}\": \"{kv.Value}\"")));
                    }

                    // ReplaceUserAttributes runs check_catalogue first, like upstream.
                    session.ReplaceUserAttributes(userAttributes);
                    break;
                }

                case PacketType.SecretBlock or PacketType.LegacyWelcome or PacketType.UnknownDataAllZeros or PacketType.LicenseVersion:
                    break;

                default:
                    Log.LogTrace("Ignoring {Cmd} packet with data {Data}", packetType, Convert.ToHexString(data.Span));
                    throw SessionException.Packet(cmd);
            }
        }
    }
}
