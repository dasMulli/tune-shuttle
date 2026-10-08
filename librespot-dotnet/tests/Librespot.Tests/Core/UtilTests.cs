using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Librespot.Core;
using Xunit;

namespace Librespot.Tests.Core;

public class UtilTests
{
    // Vectors computed independently with a Python implementation of upstream `solve_hash_cash`.
    [Theory]
    [InlineData("6c6962726573706f742d68617368636173682d637478", "00112233445566778899aabbccddeeff", 10, "6cce5c2f8b9ec1ef0000000000000010")]
    [InlineData("0a0b0c0d", "707265666978", 14, "230c3ad7733fca0200000000000047ff")]
    [InlineData("", "", 0, "95601890afd807090000000000000000")]
    public void Solve_hash_cash_matches_reference(string ctxHex, string prefixHex, int length, string expectedSuffix)
    {
        var ctx = Convert.FromHexString(ctxHex);
        var prefix = Convert.FromHexString(prefixHex);
        var suffix = new byte[16];

        var elapsed = HashCash.Solve(ctx, prefix, length, suffix);

        Assert.Equal(expectedSuffix, Convert.ToHexStringLower(suffix));
        Assert.True(elapsed >= TimeSpan.Zero);

        // The solution really has `length` trailing zero bits.
        var md = SHA1.HashData([.. prefix, .. suffix]);
        Assert.True(BitOperations.TrailingZeroCount(BinaryPrimitives.ReadInt64BigEndian(md.AsSpan(12, 8))) >= length);
    }

    [Fact]
    public void Solve_hash_cash_times_out()
    {
        // 64 zero bits cannot be found within 5 seconds.
        var error = Assert.Throws<LibrespotException>(() => HashCash.Solve([1], [2], 64, new byte[16]));
        Assert.Equal(ErrorKind.DeadlineExceeded, error.Kind);
        Assert.Equal("5 seconds expired", error.Detail);
    }

    [Fact]
    public void Solve_hash_cash_rejects_wrong_destination_size_before_solving()
    {
        // Would time out after 5 seconds if the size check came after the search.
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<ArgumentOutOfRangeException>(() => HashCash.Solve([1], [2], 64, new byte[15]));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("https://example.com/path", "?")]
    [InlineData("https://example.com/path?a=b", "&")]
    [InlineData("https://example.com/path?", "&")]
    public void Get_next_query_separator(string url, string expected)
    {
        Assert.Equal(expected, UrlUtil.GetNextQuerySeparator(url));
    }

    [Fact]
    public void Seq_generator_wraps()
    {
        var seq = new SeqGenerator<byte>(254);
        Assert.Equal(254, seq.Get());
        Assert.Equal(255, seq.Get());
        Assert.Equal(0, seq.Get());
    }

    [Fact]
    public async Task Cancel_on_drop_aborts_task()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = TaskHandle<int>.Spawn(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });

        await started.Task;
        using (new CancelOnDrop<int>(handle))
        {
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.Task);
    }

    [Fact]
    public async Task Timeout_on_drop_lets_task_finish_within_timeout()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = TaskHandle<int>.Spawn(async ct =>
        {
            await release.Task.WaitAsync(ct);
            return 42;
        });

        new TimeoutOnDrop<int>(handle, TimeSpan.FromSeconds(10)).Dispose();
        release.SetResult();

        Assert.Equal(42, await handle.Task);
    }

    [Fact]
    public async Task Timeout_on_drop_aborts_after_timeout()
    {
        var handle = TaskHandle<int>.Spawn(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });

        new TimeoutOnDrop<int>(handle, TimeSpan.FromMilliseconds(50)).Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Timeout_on_drop_is_awaitable_and_take_detaches()
    {
        var guard = new TimeoutOnDrop<int>(TaskHandle<int>.Spawn(_ => Task.FromResult(7)), TimeSpan.FromSeconds(1));
        Assert.Equal(7, await guard);
        guard.Dispose(); // no-op after completion

        var handle = TaskHandle<int>.Spawn(async ct =>
        {
            await Task.Delay(50, CancellationToken.None);
            return 8;
        });
        var taken = new TimeoutOnDrop<int>(handle, TimeSpan.Zero);
        Assert.Same(handle, taken.Take());
        taken.Dispose(); // nothing to abort anymore
        Assert.Equal(8, await handle.Task);
    }
}
