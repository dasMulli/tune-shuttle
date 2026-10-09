// Port of core/src/mercury/mod.rs (MercuryManager; types.rs -> Types.cs, sender.rs -> Sender.cs).
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;
using ProtoHeader = Librespot.Protocol.Mercury.Header;
using ProtoSubscription = Librespot.Protocol.Pubsub.Subscription;

namespace Librespot.Core.Mercury;

/// <summary>
/// Hermes/Mercury request-response and pub-sub over the AP connection (upstream <c>MercuryManager</c>).
/// </summary>
public sealed class MercuryManager : SessionComponent
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<MercuryManager>();

    // upstream MercuryManagerInner
    private SeqGenerator<ulong> _sequence = new(0);
    private readonly Dictionary<string, MercuryPending> _pending = new(); // keyed by the hex of the sequence bytes
    private readonly List<(string Prefix, ChannelWriter<MercuryResponse> Sender)> _subscriptions = [];
    private bool _invalid;

    internal MercuryManager(Session session) : base(session)
    {
    }

    /// <summary>Upstream <c>MercuryPending</c>: a request whose (possibly multi-packet) response is being reassembled.</summary>
    private sealed class MercuryPending
    {
        public List<byte[]> Parts { get; } = [];

        public byte[]? Partial { get; set; }

        public TaskCompletionSource<MercuryResponse>? Callback { get; init; }
    }

    private byte[] NextSeq()
    {
        ulong seq;
        lock (Lock)
        {
            seq = _sequence.Get();
        }

        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, seq);
        return bytes;
    }

    /// <summary>
    /// Upstream <c>request(req)</c> before its future is awaited: registers the pending request and queues the
    /// packet. A failure to queue the packet is thrown synchronously; the returned task completes with the response.
    /// </summary>
    internal Task<MercuryResponse> Request(MercuryRequest request)
    {
        var callback = new TaskCompletionSource<MercuryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new MercuryPending { Callback = callback };

        var seq = NextSeq();
        lock (Lock)
        {
            if (!_invalid)
            {
                _pending[Key(seq)] = pending;
            }
            else
            {
                // Upstream drops the oneshot sender here, so the future resolves to a RecvError.
                FailDropped(callback);
            }
        }

        var cmd = request.Method.Command();
        var data = request.Encode(seq);

        Session.SendPacket(cmd, data);
        return callback.Task;
    }

    /// <summary>Upstream <c>request(req)</c>, awaited: sends a request and waits for its response.</summary>
    /// <exception cref="MercuryException">The response has an error status code.</exception>
    /// <exception cref="LibrespotException">The session is not connected or was shut down.</exception>
    public async Task<MercuryResponse> RequestAsync(MercuryRequest request, CancellationToken cancellationToken = default)
    {
        var response = Request(request);
        return await response.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upstream <c>get(uri)</c>: a GET request.</summary>
    public Task<MercuryResponse> GetAsync(string uri, CancellationToken cancellationToken = default) =>
        RequestAsync(new MercuryRequest(MercuryMethod.Get, uri, null, []), cancellationToken);

    /// <summary>Upstream <c>send(uri, data)</c>: a SEND request with one payload part.</summary>
    public Task<MercuryResponse> SendAsync(string uri, byte[] data, CancellationToken cancellationToken = default) =>
        RequestAsync(new MercuryRequest(MercuryMethod.Send, uri, null, [data]), cancellationToken);

    /// <summary>Upstream <c>send(uri, data)</c> before its future is awaited (used by <see cref="MercurySender"/>).</summary>
    internal Task<MercuryResponse> Send(string uri, byte[] data) =>
        Request(new MercuryRequest(MercuryMethod.Send, uri, null, [data]));

    /// <summary>Upstream <c>sender(uri)</c>.</summary>
    public MercurySender Sender(string uri) => new(this, uri);

    /// <summary>
    /// Upstream <c>subscribe(uri)</c>: sends a SUB request, then returns a stream of the events pushed for the uri
    /// (or, with the old subscription protocol, for each uri listed in the response). The stream ends on
    /// <see cref="Shutdown"/>.
    /// </summary>
    public async Task<ChannelReader<MercuryResponse>> SubscribeAsync(string uri, CancellationToken cancellationToken = default)
    {
        var request = Request(new MercuryRequest(MercuryMethod.Sub, uri, null, []));
        var response = await request.WaitAsync(cancellationToken).ConfigureAwait(false);

        var channel = System.Threading.Channels.Channel.CreateUnbounded<MercuryResponse>();
        var registered = false;

        lock (Lock)
        {
            if (!_invalid)
            {
                Log.LogDebug("subscribed uri={Uri} count={Count}", uri, response.Payload.Count);
                if (response.Payload.Count > 0)
                {
                    // Old subscription protocol, watch the provided list of URIs
                    foreach (var sub in response.Payload)
                    {
                        try
                        {
                            var subUri = ProtoSubscription.Parser.ParseFrom(sub).Uri;

                            Log.LogDebug("subscribed sub_uri={SubUri}", subUri);

                            _subscriptions.Add((subUri, channel.Writer));
                            registered = true;
                        }
                        catch (InvalidProtocolBufferException e)
                        {
                            Log.LogError("could not subscribe to {Uri}: {Error}", uri, e.Message);
                        }
                    }
                }
                else
                {
                    // New subscription protocol, watch the requested URI
                    _subscriptions.Add((uri, channel.Writer));
                    registered = true;
                }
            }
        }

        if (!registered)
        {
            // Upstream drops every sender here, so the receiver ends immediately.
            channel.Writer.TryComplete();
        }

        return channel.Reader;
    }

    /// <summary>
    /// Upstream <c>listen_for(uri)</c>: a stream of the events pushed for every uri starting with
    /// <paramref name="uri"/>, without sending SUB. The stream ends on <see cref="Shutdown"/>.
    /// </summary>
    public ChannelReader<MercuryResponse> ListenFor(string uri)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<MercuryResponse>();

        lock (Lock)
        {
            if (!_invalid)
            {
                Log.LogDebug("listening to uri={Uri}", uri);
                _subscriptions.Add((uri, channel.Writer));
                return channel.Reader;
            }
        }

        // Upstream drops the sender, so the receiver ends immediately.
        channel.Writer.TryComplete();
        return channel.Reader;
    }

    /// <summary>Upstream <c>dispatch(cmd, data)</c>: handles a MercuryReq/Sub/Unsub/Event packet.</summary>
    /// <exception cref="MercuryException">Unknown sequence, error status, or the response could not be delivered.</exception>
    internal void Dispatch(PacketType cmd, ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        var seqLen = BinaryPrimitives.ReadUInt16BigEndian(span);
        span = span[2..];
        var seq = span[..seqLen].ToArray();
        span = span[seqLen..];

        var flags = span[0];
        span = span[1..];
        var count = BinaryPrimitives.ReadUInt16BigEndian(span);
        span = span[2..];

        var key = Key(seq);
        MercuryPending? pending;
        lock (Lock)
        {
            _pending.Remove(key, out pending);
        }

        if (pending is null)
        {
            if (cmd == PacketType.MercuryEvent)
            {
                pending = new MercuryPending();
            }
            else
            {
                Log.LogWarning("Ignore seq {Seq} cmd {Cmd:x}", FormatBytes(seq), (byte)cmd);
                throw MercuryException.Command(cmd);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var part = ParsePart(ref span);
            if (pending.Partial is { } partial)
            {
                pending.Partial = null;
                part = [.. partial, .. part];
            }

            if (i == count - 1 && flags == 2)
            {
                pending.Partial = part;
            }
            else
            {
                pending.Parts.Add(part);
            }
        }

        if (flags == 0x1)
        {
            CompleteRequest(cmd, pending);
        }
        else
        {
            lock (Lock)
            {
                _pending[key] = pending;
            }
        }
    }

    private static byte[] ParsePart(ref ReadOnlySpan<byte> data)
    {
        var size = BinaryPrimitives.ReadUInt16BigEndian(data);
        data = data[2..];
        var part = data[..size].ToArray();
        data = data[size..];
        return part;
    }

    private void CompleteRequest(PacketType cmd, MercuryPending pending)
    {
        try
        {
            CompleteRequestInner(cmd, pending);
        }
        finally
        {
            // Every path that does not hand the response to the callback drops it upstream, which makes the waiting
            // future fail with a RecvError. TrySet* is a no-op when the callback was already completed.
            if (pending.Callback is { } callback)
            {
                FailDropped(callback);
            }
        }
    }

    private void CompleteRequestInner(PacketType cmd, MercuryPending pending)
    {
        var headerData = pending.Parts[0];
        pending.Parts.RemoveAt(0);

        ProtoHeader header;
        try
        {
            header = ProtoHeader.Parser.ParseFrom(headerData);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw LibrespotException.From(e);
        }

        var response = new MercuryResponse(header.Uri, header.StatusCode, pending.Parts);

        var statusCode = response.StatusCode;
        if (statusCode >= 500)
        {
            Log.LogError("error {StatusCode} for uri {Uri}", statusCode, response.Uri);
            throw MercuryException.ResponseError(response);
        }

        if (statusCode >= 400)
        {
            Log.LogError("error {StatusCode} for uri {Uri}", statusCode, response.Uri);
            if (pending.Callback is { } cb && !cb.TrySetException(MercuryException.ResponseError(response)))
            {
                throw MercuryException.Channel();
            }

            throw MercuryException.ResponseError(response);
        }

        if (cmd == PacketType.MercuryEvent)
        {
            // Upstream: "This is just a workaround to make utf-8 encoded usernames work. A better solution would be
            // to use an uri struct and urlencode it directly before sending while saving the subscription under its
            // unencoded form."
            var encodedUri = EncodeUri(response.Uri);

            var found = false;

            lock (Lock)
            {
                _subscriptions.RemoveAll(sub =>
                {
                    if (encodedUri.StartsWith(sub.Prefix, StringComparison.Ordinal))
                    {
                        found = true;

                        // if send fails, remove from list of subs (upstream notes it should send an unsub message)
                        return !sub.Sender.TryWrite(response);
                    }

                    // URI doesn't match
                    return false;
                });
            }

            if (found)
            {
                return;
            }

            if (Session.Dealer.Handles(response.Uri))
            {
                Log.LogTrace("mercury response <{Uri}> is handled by dealer", response.Uri);
                return;
            }

            Log.LogDebug("unknown subscription uri={Uri}", response.Uri);
            Log.LogTrace("response pushed over Mercury: {Response}", response);
            throw MercuryException.ResponseError(response);
        }

        if (pending.Callback is { } callback)
        {
            if (!callback.TrySetResult(response))
            {
                throw MercuryException.Channel();
            }

            return;
        }

        Log.LogError("can't handle Mercury response: {Response}", response);
        throw MercuryException.ResponseError(response);
    }

    /// <summary>Fails all pending requests and closes subscriptions (called from <see cref="Session.Shutdown"/>).</summary>
    public void Shutdown()
    {
        lock (Lock)
        {
            _invalid = true;

            // destroy the sending halves of the channels to signal everyone who is waiting for something.
            foreach (var pending in _pending.Values)
            {
                if (pending.Callback is { } callback)
                {
                    FailDropped(callback);
                }
            }

            _pending.Clear();

            foreach (var (_, sender) in _subscriptions)
            {
                sender.TryComplete();
            }

            _subscriptions.Clear();
        }
    }

    /// <summary>
    /// Upstream's subscription-matching workaround: keeps the first '/'-separated component and
    /// <c>application/x-www-form-urlencoded</c>-encodes every following one.
    /// </summary>
    internal static string EncodeUri(string uri)
    {
        var components = uri.Split('/');
        var builder = new StringBuilder(components[0]);
        for (var i = 1; i < components.Length; i++)
        {
            builder.Append('/');
            FormUrlEncode(components[i], builder);
        }

        return builder.ToString();
    }

    /// <summary>Upstream <c>form_urlencoded::byte_serialize</c>.</summary>
    private static void FormUrlEncode(string component, StringBuilder output)
    {
        foreach (var b in Encoding.UTF8.GetBytes(component))
        {
            if (b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
            {
                output.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                output.Append('+');
            }
            else
            {
                output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Completes a callback the way a dropped upstream <c>oneshot::Sender</c> does (a RecvError, kind Internal).</summary>
    private static void FailDropped(TaskCompletionSource<MercuryResponse> callback) =>
        callback.TrySetException(LibrespotException.From(new ChannelClosedException()));

    private static string Key(ReadOnlySpan<byte> seq) => Convert.ToHexString(seq);

    private static string FormatBytes(byte[] bytes) => "[" + string.Join(", ", bytes) + "]";
}
