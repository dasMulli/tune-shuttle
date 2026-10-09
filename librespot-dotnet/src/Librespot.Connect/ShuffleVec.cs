// Port of connect/src/shuffle_vec.rs.
using System.Collections;
using Librespot.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Librespot.Connect;

/// <summary>
/// Seedable pseudo random number generator used by <see cref="ShuffleVec{T}"/>.
/// </summary>
/// <remarks>
/// Upstream uses <c>rand::rngs::SmallRng::seed_from_u64</c> (rand 0.9, 64-bit targets): xoshiro256++ whose state
/// is expanded from the seed with SplitMix64, and <c>random_range(0..n)</c> for <c>usize</c>, which samples with
/// Canon's widening-multiply method on 32-bit words while the range fits into <c>u32</c>. This class reimplements
/// exactly that, so a seed produces the same shuffle as the Rust binary (verified against vectors generated with
/// rand 0.9.2). <see cref="System.Random"/> is deliberately not used: its sequence is unspecified across runtimes.
/// Upstream's <c>SmallRng</c> documents that its algorithm may change between rand versions, so cross-binary
/// compatibility of seeds is a convenience, not a contract; seeds are only used in-process and in
/// <c>transfer_shuffle</c> (stored in the context metadata).
/// </remarks>
internal sealed class SeededRng
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <summary>Upstream <c>SmallRng::seed_from_u64</c> (SplitMix64 state expansion).</summary>
    public SeededRng(ulong seed)
    {
        const ulong Phi = 0x9e3779b97f4a7c15;

        Span<ulong> s = stackalloc ulong[4];
        var state = seed;
        for (var i = 0; i < s.Length; i++)
        {
            state = unchecked(state + Phi);
            var z = state;
            z = unchecked((z ^ (z >> 30)) * 0xbf58476d1ce4e5b9);
            z = unchecked((z ^ (z >> 27)) * 0x94d049bb133111eb);
            z ^= z >> 31;
            s[i] = z;
        }

        (_s0, _s1, _s2, _s3) = (s[0], s[1], s[2], s[3]);
    }

    /// <summary>Next 64 random bits (xoshiro256++ <c>next_u64</c>).</summary>
    public ulong NextU64()
    {
        var result = unchecked(ulong.RotateLeft(unchecked(_s0 + _s3), 23) + _s0);

        var t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;

        _s2 ^= t;

        _s3 = ulong.RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>Next 32 random bits: the upper half of <see cref="NextU64"/> (xoshiro256++ <c>next_u32</c>).</summary>
    public uint NextU32() => (uint)(NextU64() >> 32);

    /// <summary>
    /// Upstream <c>rng.random_range(0..upper)</c> for <c>usize</c>: a value in <c>[0, upper)</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="upper"/> is not positive (upstream panics).</exception>
    public int NextRange(int upper)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(upper);

        // UniformInt::<u32>::sample_single_inclusive(0, upper - 1), the biased (non-"unbiased" feature) variant.
        var range = (uint)upper;
        var (result, loOrder) = WideningMultiply(NextU32(), range);

        // if the sample is biased...
        if (loOrder > unchecked(0u - range))
        {
            // ...generate a new sample to reduce bias, incrementing result on overflow
            var (newHiOrder, _) = WideningMultiply(NextU32(), range);
            if (loOrder > uint.MaxValue - newHiOrder)
            {
                result++;
            }
        }

        return (int)result;
    }

    private static (uint Hi, uint Lo) WideningMultiply(uint a, uint b)
    {
        var tmp = (ulong)a * b;
        return ((uint)(tmp >> 32), (uint)tmp);
    }
}

/// <summary>Logger holder for the generic <see cref="ShuffleVec{T}"/>.</summary>
internal static class ShuffleVecLog
{
    internal static readonly ILogger Log = LibrespotLog.CreateLogger("Librespot.Connect.ShuffleVec");
}

/// <summary>
/// A list that can be shuffled with a seed and later restored to its original order (upstream <c>ShuffleVec</c>,
/// which derefs to <c>Vec&lt;T&gt;</c>; here the list operations are exposed directly).
/// </summary>
internal sealed class ShuffleVec<T> : IList<T>, IReadOnlyList<T>, IEquatable<ShuffleVec<T>>
{
    private readonly List<T> _vec;
    private List<int>? _indices;

    /// <summary>
    /// This is primarily necessary to ensure that shuffle does not behave out of place.
    /// <para>
    /// For that reason we swap the first track with the currently playing track. By that we ensure
    /// that the shuffle state is consistent between resets of the state because the first track is
    /// always the track with which we started playing when switching to shuffle.
    /// </para>
    /// </summary>
    private int? _originalFirstPosition;

    /// <summary>Creates an empty, unshuffled list (upstream <c>Default</c>).</summary>
    public ShuffleVec()
    {
        _vec = [];
    }

    /// <summary>Upstream <c>From&lt;Vec&lt;T&gt;&gt;</c>: wraps the items in their original order.</summary>
    public ShuffleVec(IEnumerable<T> items)
    {
        _vec = [.. items];
    }

    private ShuffleVec(ShuffleVec<T> other)
    {
        _vec = [.. other._vec];
        _indices = other._indices is null ? null : [.. other._indices];
        _originalFirstPosition = other._originalFirstPosition;
    }

    /// <summary>Upstream <c>Clone</c>: copies the items and the shuffle state (the items themselves are not cloned).</summary>
    public ShuffleVec<T> Clone() => new(this);

    /// <inheritdoc />
    public int Count => _vec.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public T this[int index]
    {
        get => _vec[index];
        set => _vec[index] = value;
    }

    /// <summary>The index of the first item matching <paramref name="predicate"/>, or -1 (upstream <c>iter().position</c>).</summary>
    public int FindIndex(Predicate<T> predicate) => _vec.FindIndex(predicate);

    /// <summary>
    /// Shuffles the list with a seeded RNG. The item for which <paramref name="isFirst"/> returns true is swapped to
    /// the front. An already shuffled list is unshuffled first.
    /// </summary>
    public void ShuffleWithSeed(ulong seed, Func<T, bool> isFirst) => ShuffleWithRng(new SeededRng(seed), isFirst);

    /// <summary>Upstream <c>shuffle_with_rng</c>.</summary>
    public void ShuffleWithRng(SeededRng rng, Func<T, bool> isFirst)
    {
        if (_vec.Count <= 1)
        {
            ShuffleVecLog.Log.LogInformation("skipped shuffling for less or equal one item");
            return;
        }

        if (_indices is not null)
        {
            Unshuffle();
        }

        var indices = new List<int>(_vec.Count - 1);
        for (var i = _vec.Count - 1; i >= 1; i--)
        {
            indices.Add(rng.NextRange(i + 1));
        }

        var n = 0;
        for (var i = _vec.Count - 1; i >= 1; i--)
        {
            Swap(i, indices[n++]);
        }

        _indices = indices;

        var firstPosition = _vec.FindIndex(item => isFirst(item));
        _originalFirstPosition = firstPosition >= 0 ? firstPosition : null;
        if (_originalFirstPosition is { } firstPos)
        {
            Swap(0, firstPos);
        }
    }

    /// <summary>Restores the order from before the last shuffle; a no-op when not shuffled.</summary>
    public void Unshuffle()
    {
        var indices = _indices;
        if (indices is null)
        {
            return;
        }

        _indices = null;

        if (_originalFirstPosition is { } firstPos)
        {
            Swap(0, firstPos);
            _originalFirstPosition = null;
        }

        for (var i = 1; i < _vec.Count; i++)
        {
            var at = _vec.Count - i - 1;
            if (at < 0 || at >= indices.Count)
            {
                return;
            }

            Swap(indices[at], i);
        }
    }

    private void Swap(int a, int b) => (_vec[a], _vec[b]) = (_vec[b], _vec[a]);

    /// <inheritdoc />
    public void Add(T item) => _vec.Add(item);

    /// <inheritdoc />
    public void Clear() => _vec.Clear();

    /// <inheritdoc />
    public bool Contains(T item) => _vec.Contains(item);

    /// <inheritdoc />
    public void CopyTo(T[] array, int arrayIndex) => _vec.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public int IndexOf(T item) => _vec.IndexOf(item);

    /// <inheritdoc />
    public void Insert(int index, T item) => _vec.Insert(index, item);

    /// <inheritdoc />
    public bool Remove(T item) => _vec.Remove(item);

    /// <inheritdoc />
    public void RemoveAt(int index) => _vec.RemoveAt(index);

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _vec.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Upstream <c>PartialEq</c>: compares only the items, not the shuffle state.</summary>
    public bool Equals(ShuffleVec<T>? other) =>
        other is not null && (ReferenceEquals(this, other) || _vec.SequenceEqual(other._vec));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ShuffleVec<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _vec)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}
