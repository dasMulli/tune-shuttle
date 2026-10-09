// Tests for core/src/cache.rs (port of its #[cfg(test)] module plus file-system round trips).
using Librespot.Core;
using Librespot.Protocol.Authentication;
using Xunit;

namespace Librespot.Tests.Core;

public sealed class CacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "librespot-cache-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static DateTime OrderedTime(ulong v) => DateTime.UnixEpoch + TimeSpan.FromSeconds(v);

    [Fact]
    public void test_size_limiter()
    {
        var limiter = new SizeLimiter(1000);

        limiter.Add("a", 500, OrderedTime(2));
        limiter.Add("b", 500, OrderedTime(1));

        // b (500) -> a (500)  => sum: 1000 <= 1000
        Assert.False(limiter.ExceedsLimit());
        Assert.Null(limiter.Pop());

        limiter.Add("c", 1000, OrderedTime(3));

        // b (500) -> a (500) -> c (1000)  => sum: 2000 > 1000
        Assert.True(limiter.ExceedsLimit());
        Assert.Equal("b", limiter.Pop());
        // a (500) -> c (1000)  => sum: 1500 > 1000
        Assert.Equal("a", limiter.Pop());
        // c (1000)   => sum: 1000 <= 1000
        Assert.Null(limiter.Pop());

        limiter.Add("d", 5, OrderedTime(2));
        // d (5) -> c (1000) => sum: 1005 > 1000
        Assert.Equal("d", limiter.Pop());
        // c (1000)   => sum: 1000 <= 1000
        Assert.Null(limiter.Pop());

        // Test updating

        limiter.Add("e", 500, OrderedTime(3));
        //  c (1000) -> e (500)  => sum: 1500 > 1000
        Assert.True(limiter.Update("c", OrderedTime(4)));
        // e (500) -> c (1000)  => sum: 1500 > 1000
        Assert.Equal("e", limiter.Pop());
        // c (1000)  => sum: 1000 <= 1000

        // Test removing
        limiter.Add("f", 500, OrderedTime(2));
        Assert.True(limiter.Remove("c"));
        Assert.False(limiter.ExceedsLimit());
    }

    [Fact]
    public void SizeLimiterReAddReplacesSizeAndTimestamp()
    {
        var limiter = new SizeLimiter(100);
        limiter.Add("a", 80, OrderedTime(1));
        limiter.Add("b", 10, OrderedTime(2));
        limiter.Add("a", 20, OrderedTime(3)); // a is now newer and smaller: 30 in use
        Assert.False(limiter.ExceedsLimit());

        limiter.Add("c", 80, OrderedTime(4)); // 110 > 100: b is the oldest
        Assert.Equal("b", limiter.Pop());
        Assert.Null(limiter.Pop());
        Assert.False(limiter.Update("b", OrderedTime(5)));
        Assert.False(limiter.Remove("b"));
    }

    [Fact]
    public void CredentialsRoundTrip()
    {
        var cache = new Cache(Path.Combine(_root, "creds"), null, null, null);
        Assert.Null(cache.Credentials());

        var credentials = new Credentials
        {
            Username = "user",
            AuthType = AuthenticationType.AuthenticationStoredSpotifyCredentials,
            AuthData = [1, 2, 3, 4],
        };
        cache.SaveCredentials(credentials);

        Assert.True(File.Exists(Path.Combine(_root, "creds", "credentials.json")));
        Assert.Equal(credentials, cache.Credentials());
    }

    [Fact]
    public void CorruptCredentialsAreIgnored()
    {
        var dir = Path.Combine(_root, "creds");
        var cache = new Cache(dir, null, null, null);
        File.WriteAllText(Path.Combine(dir, "credentials.json"), "{ not json");
        Assert.Null(cache.Credentials());

        // Well-formed JSON with invalid field values (serde errors upstream) is ignored too.
        File.WriteAllText(Path.Combine(dir, "credentials.json"), """{"username":"u","auth_type":1,"auth_data":"not base64!"}""");
        Assert.Null(cache.Credentials());
        File.WriteAllText(Path.Combine(dir, "credentials.json"), """{"username":"u","auth_type":"x","auth_data":""}""");
        Assert.Null(cache.Credentials());
        File.WriteAllText(Path.Combine(dir, "credentials.json"), """{"username":"u","auth_type":99999999999,"auth_data":""}""");
        Assert.Null(cache.Credentials());
        File.WriteAllText(Path.Combine(dir, "credentials.json"), "null");
        Assert.Null(cache.Credentials());
    }

    [Fact]
    public void VolumeRoundTrip()
    {
        var dir = Path.Combine(_root, "vol");
        var cache = new Cache(null, dir, null, null);
        Assert.Null(cache.Volume());

        cache.SaveVolume(12345);
        Assert.Equal("12345", File.ReadAllText(Path.Combine(dir, "volume")));
        Assert.Equal((ushort)12345, cache.Volume());

        File.WriteAllText(Path.Combine(dir, "volume"), "70000");
        Assert.Null(cache.Volume());
        File.WriteAllText(Path.Combine(dir, "volume"), " 1");
        Assert.Null(cache.Volume());
    }

    [Fact]
    public void DisabledLocationsAreNoOps()
    {
        var cache = new Cache(null, null, null, null);
        Assert.Null(cache.Credentials());
        cache.SaveCredentials(Credentials.WithPassword("a", "b"));
        Assert.Null(cache.Volume());
        cache.SaveVolume(1);

        var fileId = FileId.FromRaw(new byte[20]);
        Assert.Null(cache.FilePath(fileId));
        Assert.Null(cache.File(fileId));
        Assert.Throws<CacheException>(() => cache.SaveFile(fileId, new MemoryStream([1])));
        Assert.Throws<CacheException>(() => cache.RemoveFile(fileId));
    }

    [Fact]
    public void AudioFileRoundTrip()
    {
        var audio = Path.Combine(_root, "audio");
        var cache = new Cache(null, null, audio, 1_000_000);
        var fileId = FileId.FromRaw(Enumerable.Range(0xa0, 20).Select(i => (byte)i).ToArray());

        var expectedPath = Path.Combine(audio, "a0", "a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3");
        Assert.Equal(expectedPath, cache.FilePath(fileId));
        Assert.Null(cache.File(fileId));

        var saved = cache.SaveFile(fileId, new MemoryStream([1, 2, 3, 4, 5]));
        Assert.Equal(expectedPath, saved);

        using (var stream = cache.File(fileId))
        {
            Assert.NotNull(stream);
            var buffer = new byte[10];
            Assert.Equal(5, stream!.Read(buffer));
            Assert.Equal([1, 2, 3, 4, 5], buffer[..5]);
        }

        cache.RemoveFile(fileId);
        Assert.False(File.Exists(expectedPath));
        var error = Assert.ThrowsAny<LibrespotException>(() => cache.RemoveFile(fileId));
        Assert.Equal(ErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public void SaveFilePrunesLeastRecentlyUsed()
    {
        var audio = Path.Combine(_root, "audio");
        var cache = new Cache(null, null, audio, 10);
        var first = FileId.FromRaw(Enumerable.Repeat((byte)1, 20).ToArray());
        var second = FileId.FromRaw(Enumerable.Repeat((byte)2, 20).ToArray());

        cache.SaveFile(first, new MemoryStream(new byte[6]));
        Thread.Sleep(20); // distinct timestamps
        cache.SaveFile(second, new MemoryStream(new byte[6]));

        Assert.False(File.Exists(cache.FilePath(first)));
        Assert.True(File.Exists(cache.FilePath(second)));
    }

    [Fact]
    public void ConstructorPrunesExistingFilesByAccessTime()
    {
        var audio = Path.Combine(_root, "audio");
        Directory.CreateDirectory(Path.Combine(audio, "aa"));
        var older = Path.Combine(audio, "aa", "older");
        var newer = Path.Combine(audio, "aa", "newer");
        File.WriteAllBytes(older, new byte[8]);
        File.WriteAllBytes(newer, new byte[8]);
        File.SetLastAccessTimeUtc(older, OrderedTime(1000));
        File.SetLastAccessTimeUtc(newer, OrderedTime(2000));

        _ = new Cache(null, null, audio, 10);

        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
    }
}
