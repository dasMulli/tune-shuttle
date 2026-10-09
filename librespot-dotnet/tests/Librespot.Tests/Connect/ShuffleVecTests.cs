using Librespot.Connect;
using Xunit;

namespace Librespot.Tests.Connect;

public class ShuffleVecTests
{
    private static (ShuffleVec<int> Vec, ulong Seed) Base(int count)
    {
        var seed = (ulong)Random.Shared.NextInt64(0, 10_000_000_000_000);
        return (new ShuffleVec<int>(Enumerable.Range(0, count)), seed);
    }

    [Fact]
    public void test_shuffle_without_first()
    {
        var (baseVec, seed) = Base(100);

        var shuffledVec = baseVec.Clone();
        shuffledVec.ShuffleWithSeed(seed, _ => false);

        var differentShuffledVec = baseVec.Clone();
        differentShuffledVec.ShuffleWithSeed(seed, _ => false);

        // shuffling with the same seed has the same result
        Assert.Equal(shuffledVec, differentShuffledVec);

        var unshuffledVec = shuffledVec.Clone();
        unshuffledVec.Unshuffle();

        // unshuffle restores the original state
        Assert.Equal(baseVec, unshuffledVec);
    }

    [Fact]
    public void test_shuffle_with_first()
    {
        const int MaxRange = 200;

        var (baseVec, seed) = Base(MaxRange);

        var shuffledWithoutFirst = baseVec.Clone();
        shuffledWithoutFirst.ShuffleWithSeed(seed, _ => false);

        // Deviation: upstream picks any random item; if that item already lands at position 0 the upstream test
        // fails spuriously (no positions switch). Pick one that does not.
        int randFirst;
        do
        {
            randFirst = Random.Shared.Next(0, MaxRange);
        }
        while (shuffledWithoutFirst[0] == randFirst);

        var shuffledWithFirst = baseVec.Clone();
        shuffledWithFirst.ShuffleWithSeed(seed, i => i == randFirst);

        // after shuffling the first is expected to be the given item
        Assert.Equal(randFirst, shuffledWithFirst.FirstOrDefault());

        var switchedPositions = new List<int>(2);
        for (var i = 0; i < shuffledWithoutFirst.Count; i++)
        {
            if (shuffledWithoutFirst[i] != shuffledWithFirst[i])
            {
                switchedPositions.Add(i);
            }
        }

        // only the switched positions should be different
        Assert.Equal(2, switchedPositions.Count);

        // the switched values should be equal
        Assert.Equal(shuffledWithFirst[switchedPositions[0]], shuffledWithoutFirst[switchedPositions[1]]);
        Assert.Equal(shuffledWithFirst[switchedPositions[1]], shuffledWithoutFirst[switchedPositions[0]]);
    }

    [Fact]
    public void Unshuffle_restores_order_after_shuffle_with_first()
    {
        var vec = new ShuffleVec<int>(Enumerable.Range(0, 50));
        vec.ShuffleWithSeed(987_654_321_000, i => i == 42);
        Assert.Equal(42, vec[0]);

        vec.Unshuffle();
        Assert.Equal(Enumerable.Range(0, 50), vec);
    }

    [Fact]
    public void Reshuffle_unshuffles_first()
    {
        var once = new ShuffleVec<int>(Enumerable.Range(0, 30));
        once.ShuffleWithSeed(5, _ => false);

        var twice = new ShuffleVec<int>(Enumerable.Range(0, 30));
        twice.ShuffleWithSeed(77, _ => false);
        twice.ShuffleWithSeed(5, _ => false);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Shuffle_skips_single_item()
    {
        var vec = new ShuffleVec<int>([7]);
        vec.ShuffleWithSeed(1, _ => true);
        vec.Unshuffle();
        Assert.Equal([7], vec);
    }

    // Vectors generated with rand 0.9.2: `SmallRng::seed_from_u64(seed)` on x86_64 (xoshiro256++).
    [Fact]
    public void SeededRng_matches_rust_small_rng_next_u64()
    {
        var rng = new SeededRng(42);
        ulong[] expected = [15021278609987233951, 5881210131331364753, 18149643915985481100, 12933668939759105464];
        foreach (var value in expected)
        {
            Assert.Equal(value, rng.NextU64());
        }
    }

    // rand 0.9.2: `(0..10).map(|i| rng.random_range(0..(i * 7 + 3) as usize))` with seed 123456789012.
    [Fact]
    public void SeededRng_matches_rust_random_range()
    {
        var rng = new SeededRng(123456789012);
        int[] expected = [0, 0, 12, 18, 30, 24, 2, 5, 16, 31];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], rng.NextRange(i * 7 + 3));
        }
    }

    // rand 0.9.2: upstream's shuffle_with_seed over 0..20 with seed 555_555_555_555, without and with is_first == 7.
    [Fact]
    public void Shuffle_matches_rust_vectors()
    {
        var without = new ShuffleVec<int>(Enumerable.Range(0, 20));
        without.ShuffleWithSeed(555_555_555_555, _ => false);
        Assert.Equal([19, 12, 6, 4, 5, 17, 14, 1, 2, 9, 11, 0, 16, 7, 8, 15, 13, 18, 3, 10], without);

        var withFirst = new ShuffleVec<int>(Enumerable.Range(0, 20));
        withFirst.ShuffleWithSeed(555_555_555_555, i => i == 7);
        Assert.Equal([7, 12, 6, 4, 5, 17, 14, 1, 2, 9, 11, 0, 16, 19, 8, 15, 13, 18, 3, 10], withFirst);
    }
}
