using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Timing;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Tests.Rtsp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Airplay2.Sender.Tests.ReceiverLibrary;

/// <summary>
/// The hooks added while extracting the fake receiver into <c>Airplay2.Receiver</c> (SPEC-receiver.md 4.2, 6.2, wave 0
/// <c>receiver-extract</c>): the controller registry's change callback, the controller store write-through and the
/// monotonic clock adapters. The moved code itself is covered by the fake receiver's tests.
/// </summary>
public class ReceiverExtractionTests
{
    [Fact]
    public void Registry_reports_adds_replacements_and_removals_but_not_refusals()
    {
        var changes = new List<(string Id, byte[]? Key)>();
        var registry = new HapControllerRegistry((id, key) => changes.Add((id, key)));
        var key = new byte[32];
        key[0] = 7;

        Assert.True(registry.TryAddOrUpdate("a", key, maxControllers: 1));
        Assert.False(registry.TryAddOrUpdate("b", key, maxControllers: 1)); // full: no change, no callback
        registry.Set("a", new byte[32]);
        Assert.False(registry.Remove("missing"));
        Assert.True(registry.Remove("a"));

        Assert.Collection(
            changes,
            c => Assert.Equal(("a", (byte)7), (c.Id, c.Key![0])),
            c => Assert.Equal(("a", (byte)0), (c.Id, c.Key![0])),
            c => Assert.Equal(("a", (byte[]?)null), (c.Id, c.Key)));
    }

    [Fact]
    public void Accessory_built_on_a_registry_shares_it_with_every_connection_view()
    {
        var registry = new HapControllerRegistry();
        var accessory = new HapAccessoryPairing(new HapAccessoryOptions(ControllerIdentity.Generate(), "1234"), registry);

        Assert.Same(registry, accessory.KnownControllers);
        Assert.Same(registry, accessory.ForConnection().KnownControllers);
    }

    [Fact]
    public async Task Load_seeds_the_registry_without_writing_back_and_removals_reach_the_store()
    {
        var store = new CountingStore();
        await store.SaveAsync("controller-1", new byte[32], CancellationToken.None);
        await store.SaveAsync("controller-2", new byte[32], CancellationToken.None);
        store.Saves = 0;
        var pairing = new ReceiverPairing(new HapAccessoryOptions(ControllerIdentity.Generate(), "1234"), store);

        await pairing.LoadAsync(CancellationToken.None);

        Assert.Equal(2, pairing.ControllerCount);
        Assert.Equal(0, store.Saves);
        Assert.True(pairing.RemoveController("controller-1"));
        Assert.False(pairing.RemoveController("controller-1"));
        Assert.Equal(1, pairing.ControllerCount);
        Assert.False(store.Contains("controller-1"));
        Assert.True(store.Contains("controller-2"));
    }

    [Fact]
    public async Task In_memory_store_copies_keys_in_and_out()
    {
        var store = new InMemoryControllerStore();
        var key = new byte[32];
        await store.SaveAsync("c", key, CancellationToken.None);
        key[0] = 1;
        var loaded = await store.LoadAsync(CancellationToken.None);
        loaded["c"][0] = 2;

        Assert.Equal(0, (await store.LoadAsync(CancellationToken.None))["c"][0]);
        await store.RemoveAsync("c", CancellationToken.None);
        await store.RemoveAsync("c", CancellationToken.None); // removing an unknown controller is not an error
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Monotonic_clock_adapters_read_their_source()
    {
        var master = new MasterClock();
        var fromMaster = MonotonicClock.FromMasterClock(master);
        var first = fromMaster.NowNanoseconds;
        Assert.True(fromMaster.NowNanoseconds >= first);

        ulong now = 42;
        var fromDelegate = MonotonicClock.FromDelegate(() => now);
        Assert.Equal(42UL, fromDelegate.NowNanoseconds);
        now = 43;
        Assert.Equal(43UL, fromDelegate.NowNanoseconds);

        var stopwatch = MonotonicClock.CreateStopwatch();
        var a = stopwatch.NowNanoseconds;
        Assert.True(stopwatch.NowNanoseconds >= a);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_store_save_is_logged_once_and_the_controller_stays_registered(bool throwSynchronously)
    {
        var log = new CapturingLogger(LogLevel.Warning);
        var pairing = new ReceiverPairing(new HapAccessoryOptions(ControllerIdentity.Generate(), "1234"), new FailingStore(throwSynchronously), logger: log);

        Assert.True(pairing.Controllers.TryAddOrUpdate("controller-1", new byte[32], maxControllers: 16));
        await pairing.FlushWritesAsync(); // must not throw (SPEC-receiver.md 4.2: logged, the pairing still answers)
        await pairing.FlushWritesAsync(); // nothing left; no second log entry

        Assert.Equal(1, pairing.ControllerCount);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("storing a paired controller failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_store_removal_is_logged_when_RemoveControllerAsync_returns()
    {
        var log = new CapturingLogger(LogLevel.Warning);
        var pairing = new ReceiverPairing(new HapAccessoryOptions(ControllerIdentity.Generate(), "1234"), new FailingStore(throwSynchronously: false), logger: log);
        pairing.Controllers.Set("controller-1", new byte[32]);
        await pairing.FlushWritesAsync();
        log.Entries.Clear();

        Assert.True(await pairing.RemoveControllerAsync("controller-1"));
        Assert.False(await pairing.RemoveControllerAsync("controller-1"));

        Assert.Equal(0, pairing.ControllerCount);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_write_failure_is_logged_even_when_nothing_flushes()
    {
        var log = new CapturingLogger(LogLevel.Warning);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pairing = new ReceiverPairing(new HapAccessoryOptions(ControllerIdentity.Generate(), "1234"), new FailingStore(throwSynchronously: false, gate.Task), logger: log);

        pairing.Controllers.Set("controller-1", new byte[32]); // the write is pending and nobody flushes
        Assert.Empty(log.Entries);
        gate.SetResult();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (log.Entries.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Single(log.Entries);
    }

    private sealed class FailingStore(bool throwSynchronously, Task? gate = null) : IControllerStore
    {
        public Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, byte[]>>(new Dictionary<string, byte[]>());

        public Task SaveAsync(string controllerId, byte[] publicKey, CancellationToken cancellationToken) => Fail();

        public Task RemoveAsync(string controllerId, CancellationToken cancellationToken) => Fail();

        private Task Fail()
        {
            if (throwSynchronously)
            {
                throw new InvalidOperationException("store unavailable");
            }

            return gate is null
                ? Task.FromException(new InvalidOperationException("store unavailable"))
                : gate.ContinueWith(_ => throw new InvalidOperationException("store unavailable"), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private sealed class CountingStore : IControllerStore
    {
        private readonly InMemoryControllerStore _inner = new();

        public int Saves { get; set; }

        public bool Contains(string id) => _inner.Contains(id);

        public Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(CancellationToken cancellationToken) => _inner.LoadAsync(cancellationToken);

        public Task SaveAsync(string controllerId, byte[] publicKey, CancellationToken cancellationToken)
        {
            Saves++;
            return _inner.SaveAsync(controllerId, publicKey, cancellationToken);
        }

        public Task RemoveAsync(string controllerId, CancellationToken cancellationToken) => _inner.RemoveAsync(controllerId, cancellationToken);
    }
}
