// Port of audio/src/range_set.rs.
using System.Text;

namespace Librespot.Audio;

/// <summary>A half-open byte range <c>[Start, Start + Length)</c> of an audio file (upstream <c>Range</c>).</summary>
/// <param name="Start">First byte of the range.</param>
/// <param name="Length">Number of bytes in the range.</param>
internal readonly record struct Range(long Start, long Length)
{
    /// <summary>The exclusive end of the range (upstream <c>end()</c>).</summary>
    public long End => Start + Length;

    /// <summary>Renders like upstream <c>Display</c>: <c>[start, last]</c>.</summary>
    public override string ToString() => $"[{Start}, {Start + Length - 1}]";
}

/// <summary>
/// A sorted set of disjoint, non-touching byte ranges (upstream <c>RangeSet</c>). Used to track which parts of an
/// audio file have been requested and downloaded. Not thread-safe; callers guard it with a lock.
/// </summary>
internal sealed class RangeSet
{
    private readonly List<Range> _ranges;

    /// <summary>Creates an empty set (upstream <c>RangeSet::new</c>).</summary>
    public RangeSet()
    {
        _ranges = [];
    }

    private RangeSet(List<Range> ranges)
    {
        _ranges = ranges;
    }

    /// <summary>True when the set contains no ranges.</summary>
    public bool IsEmpty => _ranges.Count == 0;

    /// <summary>The total number of bytes covered by the set (upstream <c>len()</c>).</summary>
    public long Len
    {
        get
        {
            long sum = 0;
            foreach (var range in _ranges)
            {
                sum += range.Length;
            }

            return sum;
        }
    }

    /// <summary>The number of disjoint ranges in the set.</summary>
    public int Count => _ranges.Count;

    /// <summary>The range at <paramref name="index"/> (upstream <c>get_range</c>).</summary>
    public Range GetRange(int index) => _ranges[index];

    /// <summary>The ranges in ascending order (upstream <c>iter()</c>).</summary>
    public IReadOnlyList<Range> Ranges => _ranges;

    /// <summary>A copy of this set (upstream <c>clone()</c>).</summary>
    public RangeSet Clone() => new([.. _ranges]);

    /// <summary>True when <paramref name="value"/> lies in one of the ranges.</summary>
    public bool Contains(long value)
    {
        foreach (var range in _ranges)
        {
            if (value < range.Start)
            {
                return false;
            }
            else if (range.Start <= value && value < range.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The number of contiguous bytes in the set starting at <paramref name="value"/> (0 if not contained).</summary>
    public long ContainedLengthFromValue(long value)
    {
        foreach (var range in _ranges)
        {
            if (value < range.Start)
            {
                return 0;
            }
            else if (range.Start <= value && value < range.End)
            {
                return range.End - value;
            }
        }

        return 0;
    }

    /// <summary>True when every range of <paramref name="other"/> is fully contained in this set.</summary>
    public bool ContainsRangeSet(RangeSet other)
    {
        foreach (var range in other._ranges)
        {
            if (ContainedLengthFromValue(range.Start) < range.Length)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Adds <paramref name="range"/>, merging it with overlapping or touching ranges.</summary>
    public void AddRange(Range range)
    {
        if (range.Length == 0)
        {
            // the interval is empty -> nothing to do.
            return;
        }

        for (var index = 0; index < _ranges.Count; index++)
        {
            // the new range is clear of any ranges we already iterated over.
            if (range.End < _ranges[index].Start)
            {
                // the new range starts after anything we already passed and ends before the next range starts
                // (they don't touch) -> insert it.
                _ranges.Insert(index, range);
                return;
            }
            else if (range.Start <= _ranges[index].End && _ranges[index].Start <= range.End)
            {
                // the new range overlaps (or touches) the first range. They are to be merged.
                // In addition we might have to merge further ranges in as well.
                var newRange = range;

                while (index < _ranges.Count && _ranges[index].Start <= newRange.End)
                {
                    var newEnd = Math.Max(newRange.End, _ranges[index].End);
                    var newStart = Math.Min(newRange.Start, _ranges[index].Start);
                    newRange = new Range(newStart, newEnd - newStart);
                    _ranges.RemoveAt(index);
                }

                _ranges.Insert(index, newRange);
                return;
            }
        }

        // the new range is after everything else -> just add it
        _ranges.Add(range);
    }

    /// <summary>Adds every range of <paramref name="other"/>.</summary>
    public void AddRangeSet(RangeSet other)
    {
        foreach (var range in other._ranges)
        {
            AddRange(range);
        }
    }

    /// <summary>A new set containing the ranges of both sets.</summary>
    public RangeSet Union(RangeSet other)
    {
        var result = Clone();
        result.AddRangeSet(other);
        return result;
    }

    /// <summary>Removes <paramref name="range"/> from the set, splitting or truncating ranges as needed.</summary>
    public void SubtractRange(Range range)
    {
        if (range.Length == 0)
        {
            return;
        }

        for (var index = 0; index < _ranges.Count; index++)
        {
            // the ranges we already passed don't overlap with the range to remove
            if (range.End <= _ranges[index].Start)
            {
                // the remaining ranges are past the one to subtract. -> we're done.
                return;
            }
            else if (range.Start <= _ranges[index].Start && _ranges[index].Start < range.End)
            {
                // the range to subtract started before the current range and reaches into the current range
                // -> we have to remove the beginning of the range or the entire range and do the same for following ranges.
                while (index < _ranges.Count && _ranges[index].End <= range.End)
                {
                    _ranges.RemoveAt(index);
                }

                if (index < _ranges.Count && _ranges[index].Start < range.End)
                {
                    var current = _ranges[index];
                    _ranges[index] = new Range(range.End, current.Length - (range.End - current.Start));
                }

                return;
            }
            else if (range.End < _ranges[index].End)
            {
                // the range to subtract punches a hole into the current range -> we need to create two smaller ranges.
                var current = _ranges[index];
                var firstRange = new Range(current.Start, range.Start - current.Start);

                _ranges[index] = new Range(range.End, current.Length - (range.End - current.Start));
                _ranges.Insert(index, firstRange);

                return;
            }
            else if (range.Start < _ranges[index].End)
            {
                // the range truncates the existing range -> truncate the range. Let the for loop take care of
                // overlaps with other ranges.
                var current = _ranges[index];
                _ranges[index] = current with { Length = range.Start - current.Start };
            }
        }
    }

    /// <summary>Removes every range of <paramref name="other"/>.</summary>
    public void SubtractRangeSet(RangeSet other)
    {
        foreach (var range in other._ranges)
        {
            SubtractRange(range);
        }
    }

    /// <summary>A new set with the ranges of <paramref name="other"/> removed.</summary>
    public RangeSet Minus(RangeSet other)
    {
        var result = Clone();
        result.SubtractRangeSet(other);
        return result;
    }

    /// <summary>A new set containing only the bytes present in both sets.</summary>
    public RangeSet Intersection(RangeSet other)
    {
        var result = new RangeSet();

        var selfIndex = 0;
        var otherIndex = 0;

        while (selfIndex < _ranges.Count && otherIndex < other._ranges.Count)
        {
            if (_ranges[selfIndex].End <= other._ranges[otherIndex].Start)
            {
                // skip the interval
                selfIndex++;
            }
            else if (other._ranges[otherIndex].End <= _ranges[selfIndex].Start)
            {
                // skip the interval
                otherIndex++;
            }
            else
            {
                // the two intervals overlap. Add the union and advance the index of the one that ends first.
                var newStart = Math.Max(_ranges[selfIndex].Start, other._ranges[otherIndex].Start);
                var newEnd = Math.Min(_ranges[selfIndex].End, other._ranges[otherIndex].End);
                result.AddRange(new Range(newStart, newEnd - newStart));
                if (_ranges[selfIndex].End <= other._ranges[otherIndex].End)
                {
                    selfIndex++;
                }
                else
                {
                    otherIndex++;
                }
            }
        }

        return result;
    }

    /// <summary>Renders like upstream <c>Display</c>: <c>([a, b][c, d])</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder("(");
        foreach (var range in _ranges)
        {
            sb.Append(range);
        }

        return sb.Append(')').ToString();
    }
}
