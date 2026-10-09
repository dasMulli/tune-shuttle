// Tests for core/src/spclient.rs (upstream has no #[cfg(test)] module), plus the live smoke tests of
// core-spclient and the deferred smoke tests of core-dealer (plan 5.3) and metadata (plan 5.5).
using System.Buffers.Binary;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Librespot.Core;
using Librespot.Core.Dealer;
using Spotify.Clienttoken.Data.V0;
using Spotify.Clienttoken.Http.V0;
using Xunit;
using ProtoTrack = Spotify.Metadata.Track;

namespace Librespot.Tests.Core;

public class SpClientTests
{
    [Theory]
    [InlineData("windows", "x86_64", 34404, 34404)]
    [InlineData("windows", "aarch64", 43620, 452)]
    [InlineData("windows", "arm", 448, 452)]
    [InlineData("windows", "x86", 332, 332)]
    public void Client_data_request_for_windows(string os, string arch, int pe, int imageFile)
    {
        var request = SpClient.BuildClientTokenRequest(os, "client", "device", "10.0.26300.0", "26300", arch);

        Assert.Equal(ClientTokenRequestType.RequestClientDataRequest, request.RequestType);
        Assert.Equal("client", request.ClientData.ClientId);
        Assert.Equal(LibrespotVersion.SpotifySemanticVersion, request.ClientData.ClientVersion);
        Assert.Equal("device", request.ClientData.ConnectivitySdkData.DeviceId);

        var windows = request.ClientData.ConnectivitySdkData.PlatformSpecificData.DesktopWindows;
        Assert.NotNull(windows);
        Assert.Equal(10, windows.OsVersion); // "10.0.26300.0" is not an f32 -> 10
        Assert.Equal(26300, windows.OsBuild);
        Assert.Equal(2, windows.PlatformId);
        Assert.Equal(9, windows.UnknownValue6);
        Assert.Equal(imageFile, windows.ImageFileMachine);
        Assert.Equal(pe, windows.PeMachine);
        Assert.True(windows.UnknownValue10);
    }

    [Fact]
    public void Client_data_request_windows_fallbacks()
    {
        var windows = SpClient.BuildClientTokenRequest("windows", "c", "d", "11", "0", "x86_64")
            .ClientData.ConnectivitySdkData.PlatformSpecificData.DesktopWindows;
        Assert.Equal(11, windows.OsVersion);
        Assert.Equal(0, windows.OsBuild);

        windows = SpClient.BuildClientTokenRequest("windows", "c", "d", "x", "not-a-number", "x86_64")
            .ClientData.ConnectivitySdkData.PlatformSpecificData.DesktopWindows;
        Assert.Equal(10, windows.OsVersion);
        Assert.Equal(21370, windows.OsBuild);
    }

    [Fact]
    public void Client_data_request_for_other_platforms()
    {
        var iosRequest = SpClient.BuildClientTokenRequest("ios", "c", "d", "17.0", "0", "aarch64");
        Assert.Equal(LibrespotVersion.SpotifyMobileVersion, iosRequest.ClientData.ClientVersion);
        var ios = iosRequest.ClientData.ConnectivitySdkData.PlatformSpecificData;
        Assert.Equal(PlatformSpecificData.DataOneofCase.Ios, ios.DataCase);
        Assert.Equal("iPhone14,5", ios.Ios.HwMachine);
        Assert.Equal("17.0", ios.Ios.SystemVersion);

        var androidRequest = SpClient.BuildClientTokenRequest("android", "c", "d", "14", "0", "aarch64");
        Assert.Equal(LibrespotVersion.SpotifyMobileVersion, androidRequest.ClientData.ClientVersion);
        var android = androidRequest.ClientData.ConnectivitySdkData.PlatformSpecificData;
        Assert.Equal("14", android.Android.AndroidVersion);
        Assert.Equal(31, android.Android.ApiVersion);
        Assert.Equal("Pixel", android.Android.DeviceName);
        Assert.Equal("GF5KQ", android.Android.ModelStr);
        Assert.Equal("Google", android.Android.Vendor);

        var macosRequest = SpClient.BuildClientTokenRequest("macos", "c", "d", "14.1", "0", "aarch64");
        Assert.Equal(LibrespotVersion.SpotifySemanticVersion, macosRequest.ClientData.ClientVersion);
        var macos = macosRequest.ClientData.ConnectivitySdkData.PlatformSpecificData;
        Assert.Equal("14.1", macos.DesktopMacos.SystemVersion);
        Assert.Equal("iMac21,1", macos.DesktopMacos.HwModel);
        Assert.Equal("aarch64", macos.DesktopMacos.CompiledCpuType);

        var linuxRequest = SpClient.BuildClientTokenRequest("freebsd", "c", "d", "14", "6.5.0-14-generic", "x86_64");
        Assert.Equal(LibrespotVersion.SpotifySemanticVersion, linuxRequest.ClientData.ClientVersion);
        var linux = linuxRequest.ClientData.ConnectivitySdkData.PlatformSpecificData;
        Assert.Equal("Linux", linux.DesktopLinux.SystemName);
        Assert.Equal("6.5.0-14-generic", linux.DesktopLinux.SystemRelease);
        Assert.Equal("14", linux.DesktopLinux.SystemVersion);
        Assert.Equal("x86_64", linux.DesktopLinux.Hardware);
    }

    [Fact]
    public void Client_token_hash_cash_answer()
    {
        // Challenge as the endpoint sends it: uppercase hex prefix, empty context. Expected suffix computed with an
        // independent Python port of `solve_hash_cash`.
        const string prefixHex = "1A2B3C4D5E6F";
        const int length = 12;
        var prefix = Convert.FromHexString(prefixHex);
        var suffix = new byte[16];
        HashCash.Solve([], prefix, length, suffix);

        var answer = SpClient.BuildChallengeAnswer("state-123", suffix);

        Assert.Equal(ClientTokenRequestType.RequestChallengeAnswersRequest, answer.RequestType);
        Assert.Equal("state-123", answer.ChallengeAnswers.State);
        var single = Assert.Single(answer.ChallengeAnswers.Answers);
        Assert.Equal(ChallengeType.ChallengeHashCash, single.ChallengeType);
        // the suffix must be in uppercase
        Assert.Equal("95601890AFD811B10000000000000AA8", single.HashCash.Suffix);

        var md = SHA1.HashData([.. prefix, .. Convert.FromHexString(single.HashCash.Suffix)]);
        Assert.True(BitOperations.TrailingZeroCount(BinaryPrimitives.ReadInt64BigEndian(md.AsSpan(12, 8))) >= length);

        // wire layout: request_type (field 1, varint 2), challenge_answers (field 3)
        var bytes = answer.ToByteArray();
        Assert.Equal(new byte[] { 0x08, 0x02, 0x1a }, bytes[..3]);
    }

    [Fact]
    public void Transfer_request_serializes_like_serde()
    {
        var request = new TransferRequest(new TransferOptions { RestorePaused = "pause", RestoreTrack = "select" });
        var json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(request));
        Assert.Equal("""{"transfer_options":{"restore_paused":"pause","restore_track":"select"}}""", json);
    }

    [Fact]
    public void Request_strategy_and_options_defaults()
    {
        Assert.Equal(new RequestStrategy.TryTimes(10), RequestStrategy.Default);
        Assert.Equal(new RequestOptions(true, true, null), RequestOptions.Default);
        Assert.Equal(new RequestOptions(false, false, null), RequestOptions.NoMetricsAndSalt);
    }

    [Fact]
    public void Error_texts_match_upstream()
    {
        Assert.Equal("missing attribute image-url", SpClientException.Attribute("image-url").Detail);
        Assert.Equal("expected data but received none", SpClientException.NoData().Detail);
        Assert.Equal("expected an entry to exist in extension_data", SpClientException.ExpectedEntry("extension_data").Detail);
        Assert.Equal(ErrorKind.FailedPrecondition, SpClientException.NoData().Kind);
    }

    [Fact]
    public async Task Attribute_based_urls_need_the_user_attribute()
    {
        var session = new Session(SessionConfig.Default(), null);
        var e = await Assert.ThrowsAsync<SpClientException>(() => session.SpClient.GetImageAsync(default));
        Assert.Equal(SpClientException.Reason.Attribute, e.Cause);
        Assert.Equal("missing attribute image-url", e.Detail);
    }

    [Fact]
    public async Task Stream_from_cdn_returns_non_success_responses_with_the_range_header()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var head = new StringBuilder();
            var buffer = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(buffer) == 0)
                {
                    break;
                }

                head.Append((char)buffer[0]);
            }

            var response = "HTTP/1.1 429 Too Many Requests\r\nRetry-After: 3\r\nContent-Length: 2\r\nConnection: close\r\n\r\nno"u8.ToArray();
            await stream.WriteAsync(response);
            return head.ToString();
        });

        var session = new Session(SessionConfig.Default(), null);
        using var response = await session.SpClient.StreamFromCdnAsync($"http://127.0.0.1:{port}/audio/abc", 10, 100)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), Librespot.Core.HttpClient.GetRetryAfter(response.Headers));
        Assert.Equal("no", await response.Content.ReadAsStringAsync());

        var requestHead = await server.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("GET /audio/abc HTTP/1.1\r\n", requestHead);
        Assert.Contains("Range: bytes=10-109\r\n", requestHead);
    }

    [Fact]
    public async Task Stream_from_cdn_rejects_invalid_urls()
    {
        var session = new Session(SessionConfig.Default(), null);
        var e = await Assert.ThrowsAnyAsync<LibrespotException>(() => session.SpClient.StreamFromCdnAsync("not a url", 0, 1));
        Assert.Equal(ErrorKind.InvalidArgument, e.Kind);
    }

    [Fact]
    public void Insert_header_replaces_and_routes_content_headers()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        SpClient.InsertHeader(request, "Accept", "application/json");
        SpClient.InsertHeader(request, "Accept", "application/x-protobuf");
        SpClient.InsertHeader(request, "Content-Type", "application/x-protobuf");

        Assert.Equal(["application/x-protobuf"], request.Headers.GetValues("Accept"));
        Assert.Equal("application/x-protobuf", request.Content!.Headers.ContentType!.MediaType);

        var e = Assert.ThrowsAny<LibrespotException>(() => SpClient.InsertHeader(request, "client-token", "bad\r\nvalue"));
        Assert.Equal(ErrorKind.InvalidArgument, e.Kind);
    }
}

/// <summary>
/// A test that talks to Spotify. Runs only with <c>LIBRESPOT_LIVE_TESTS=1</c> and an OAuth access token in
/// <c>LIBRESPOT_ACCESS_TOKEN</c> (scope <c>streaming</c>).
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LIBRESPOT_LIVE_TESTS") != "1"
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIBRESPOT_ACCESS_TOKEN")))
        {
            Skip = "network (set LIBRESPOT_LIVE_TESTS=1 and LIBRESPOT_ACCESS_TOKEN)";
        }
    }
}

/// <summary>Live smoke tests: core-spclient (plan 5.9) and the deferred ones of core-dealer (5.3) and metadata (5.5).</summary>
public class SpClientLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static async Task<Session> ConnectAsync()
    {
        var session = new Session(SessionConfig.Default(), null);
        var credentials = Credentials.WithAccessToken(Environment.GetEnvironmentVariable("LIBRESPOT_ACCESS_TOKEN")!);
        await session.ConnectAsync(credentials, storeCredentials: false).WaitAsync(Timeout);
        return session;
    }

    [LiveFact]
    public async Task Get_track_metadata_returns_a_parsable_track()
    {
        var session = await ConnectAsync();
        try
        {
            var uri = SpotifyUri.FromUri("spotify:track:4GNcXTGWmnZ3ySrqvol3o4");
            var bytes = await session.SpClient.GetTrackMetadataAsync(uri).WaitAsync(Timeout);
            var track = ProtoTrack.Parser.ParseFrom(bytes);
            Assert.False(string.IsNullOrEmpty(track.Name));
        }
        finally
        {
            session.Shutdown();
        }
    }

    [LiveFact]
    public async Task Client_token_and_login5_token_are_granted()
    {
        var session = await ConnectAsync();
        try
        {
            Assert.False(string.IsNullOrEmpty(await session.SpClient.ClientTokenAsync().WaitAsync(Timeout)));
            var token = await session.Login5.AuthTokenAsync().WaitAsync(Timeout);
            Assert.False(string.IsNullOrEmpty(token.AccessToken));
            // cached
            Assert.Same(token, await session.Login5.AuthTokenAsync().WaitAsync(Timeout));
        }
        finally
        {
            session.Shutdown();
        }
    }

    [LiveFact]
    public async Task Cdn_url_resolves_for_a_track_file()
    {
        var session = await ConnectAsync();
        try
        {
            var bytes = await session.SpClient.GetTrackMetadataAsync(SpotifyUri.FromUri("spotify:track:4GNcXTGWmnZ3ySrqvol3o4")).WaitAsync(Timeout);
            var track = ProtoTrack.Parser.ParseFrom(bytes);
            var file = track.File.Concat(track.Alternative.SelectMany(t => t.File)).First();
            var cdnUrl = await new CdnUrl(FileId.From(file)).ResolveAudioAsync(session).WaitAsync(Timeout);
            var url = cdnUrl.TryGetUrls()[0];

            using var response = await session.SpClient.StreamFromCdnAsync(url, 0, 1024).WaitAsync(Timeout);
            Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
        }
        finally
        {
            session.Shutdown();
        }
    }

    /// <summary>Deferred smoke test of core-dealer (plan 5.3).</summary>
    [LiveFact]
    public async Task Dealer_yields_the_connection_id_message()
    {
        var session = await ConnectAsync();
        try
        {
            var messages = session.Dealer.AddListenFor("hm://pusher/v1/connections/");
            await session.Dealer.StartAsync().WaitAsync(Timeout);

            await using var enumerator = messages.GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout));
            Assert.StartsWith("hm://pusher/v1/connections/", enumerator.Current.Uri);
            Assert.True(enumerator.Current.Headers.ContainsKey("Spotify-Connection-Id"));
            await session.Dealer.CloseAsync();
        }
        finally
        {
            session.Shutdown();
        }
    }

    /// <summary>Deferred smoke test of metadata (plan 5.5).</summary>
    [LiveFact]
    public async Task Metadata_track_get_returns_the_name()
    {
        var session = await ConnectAsync();
        try
        {
            var track = await Librespot.Metadata.Track.GetAsync(session, SpotifyUri.FromUri("spotify:track:4GNcXTGWmnZ3ySrqvol3o4")).WaitAsync(Timeout);
            Assert.False(string.IsNullOrEmpty(track.Name));
        }
        finally
        {
            session.Shutdown();
        }
    }
}
