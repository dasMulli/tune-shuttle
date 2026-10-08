// Port of core/src/spclient.rs.
// TODO(port: core-spclient): client-token negotiation (hashcash challenge), request strategy/retries,
// all endpoint helpers. Signatures below are the contract that metadata/connect/audio code against.
using System.Net.Http;
using Google.Protobuf;
using Spotify.Connectstate;
using Spotify.Extendedmetadata;
using Spotify.Player.Proto;

namespace Librespot.Core;

/// <summary>Upstream <c>SpClientError</c>.</summary>
public sealed class SpClientException : LibrespotException
{
    public enum Reason
    {
        /// <summary>Unable to get the access point: kind Unavailable.</summary>
        Accesspoint,
        /// <summary>Unable to get the client token.</summary>
        ClientToken,
        /// <summary>Unable to solve hashcash challenge.</summary>
        HashCash,
        /// <summary>Unable to parse transfer response.</summary>
        TransferParse,
        /// <summary>Request failed after retries.</summary>
        Request,
    }

    public Reason Cause { get; }

    public SpClientException(Reason cause, ErrorKind kind, string detail, Exception? inner = null) : base(kind, detail, inner)
    {
        Cause = cause;
    }
}

/// <summary>Upstream <c>RequestStrategy</c>.</summary>
public enum RequestStrategy
{
    /// <summary>Try once, no retries.</summary>
    TryTimes,
    /// <summary>Retry with backoff until success.</summary>
    Infinitely,
}

/// <summary>Upstream <c>RequestOptions</c>.</summary>
public sealed record RequestOptions(
    string? Method = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    ReadOnlyMemory<byte>? Body = null,
    string? BaseUrl = null,
    bool RequiresAuth = true);

/// <summary>Upstream <c>TransferRequest</c>: the raw transfer-state bytes plus the originating device id.</summary>
public sealed record TransferRequest(byte[] State, string? FromDeviceId);

/// <summary>HTTP client for the spclient (gRPC-web/protobuf over HTTPS) API.</summary>
public sealed class SpClient : SessionComponent
{
    internal SpClient(Session session) : base(session)
    {
    }

    public void SetStrategy(RequestStrategy strategy) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task FlushAccesspointAsync() => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<SocketAddress> GetAccesspointAsync(CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<string> BaseUrlAsync(CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<string> ClientTokenAsync(CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");

    public Task<byte[]> RequestWithProtobufAsync(string method, string endpoint, IReadOnlyDictionary<string, string>? headers, IMessage message, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> RequestAsJsonAsync(string method, string endpoint, IReadOnlyDictionary<string, string>? headers, string? body, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> RequestAsync(string method, string endpoint, IReadOnlyDictionary<string, string>? headers, ReadOnlyMemory<byte>? body, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> RequestWithOptionsAsync(string method, string endpoint, RequestOptions options, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");

    public Task<byte[]> PutConnectStateRequestAsync(PutStateRequest state, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> DeleteConnectStateRequestAsync(CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> PutConnectStateInactiveAsync(bool notify, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");

    public Task<byte[]> GetExtendedMetadataAsync(BatchedEntityRequest request, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetMetadataAsync(ExtensionKind kind, SpotifyUri id, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetTrackMetadataAsync(SpotifyUri trackUri, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetEpisodeMetadataAsync(SpotifyUri episodeUri, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetAlbumMetadataAsync(SpotifyUri albumUri, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetArtistMetadataAsync(SpotifyUri artistUri, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetShowMetadataAsync(SpotifyUri showUri, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetLyricsAsync(SpotifyId trackId, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetLyricsForImageAsync(SpotifyId trackId, FileId imageId, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetPlaylistAsync(SpotifyId playlistId, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetUserProfileAsync(string username, int? playlistLimit, int? artistLimit, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetUserFollowersAsync(string username, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetUserFollowingAsync(string username, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetRadioForTrackAsync(SpotifyUri trackUri, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetApolloStationAsync(string contextUri, IReadOnlyList<string> prevTracks, int? count, bool autoplay, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetNextPageAsync(string nextPageUri, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetAudioStorageAsync(FileId fileId, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");

    /// <summary>
    /// Upstream <c>stream_from_cdn(cdn_url, offset, length)</c>: a GET of one URL from <see cref="CdnUrl.TryGetUrls"/>
    /// with <c>Range: bytes={offset}-{offset+length-1}</c>, sent with <c>HttpCompletionOption.ResponseHeadersRead</c>
    /// through <see cref="HttpClient.RequestStreamAsync"/>. The caller (audio-fetch) needs the whole response:
    /// the status (206 Partial Content expected, 429 handled with <see cref="HttpClient.GetRetryAfter"/>), the
    /// <c>Content-Range</c> header (file size) and the body stream (<c>Content.ReadAsStreamAsync</c>).
    /// Non-2xx statuses are returned, not thrown.
    /// </summary>
    public Task<HttpResponseMessage> StreamFromCdnAsync(string cdnUrl, long offset, long length, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");

    public Task<byte[]> RequestUrlAsync(string url, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetAudioPreviewAsync(FileId previewId, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetHeadFileAsync(FileId fileId, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetImageAsync(FileId imageId, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");

    public Task<Context> GetContextAsync(string uri, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<Context> GetAutoplayContextAsync(AutoplayContextRequest request, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> GetRootlistAsync(int from, int? length, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
    public Task<byte[]> TransferAsync(string toDeviceId, TransferRequest request, CancellationToken ct = default) => throw new NotImplementedException("TODO(port: core-spclient)");
}
