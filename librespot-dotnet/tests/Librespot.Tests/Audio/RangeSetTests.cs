// Tests for audio/src/range_set.rs (upstream has no #[cfg(test)] module; these follow the plan's verification list).
using Librespot.Audio;
using Xunit;
using Range = Librespot.Audio.Range;

namespace Librespot.Tests.Audio;

public class RangeSetTests
{
    private static RangeSet Set(params (long Start, long Length)[] ranges)
    {
        var set = new RangeSet();
        foreach (var (start, length) in ranges)
        {
            set.AddRange(new Range(start, length));
        }

        return set;
    }

    [Fact]
    public void RangeDisplayAndEnd()
    {
        var range = new Range(10, 5);
        Assert.Equal(15, range.End);
        Assert.Equal("[10, 14]", range.ToString());
        Assert.Equal("([0, 9][20, 29])", Set((0, 10), (20, 10)).ToString());
    }

    [Fact]
    public void AddRangeKeepsOrderAndIgnoresEmpty()
    {
        var set = Set((100, 10), (0, 10), (50, 10), (70, 0));
        Assert.Equal([new Range(0, 10), new Range(50, 10), new Range(100, 10)], set.Ranges);
        Assert.Equal(30, set.Len);
        Assert.False(set.IsEmpty);
        Assert.True(new RangeSet().IsEmpty);
    }

    [Fact]
    public void AddRangeMergesOverlappingAndTouching()
    {
        // touching at the end of the first range and at the start of the second
        var set = Set((0, 10), (20, 10), (10, 10));
        Assert.Equal([new Range(0, 30)], set.Ranges);

        // overlapping several ranges at once
        set = Set((0, 5), (10, 5), (20, 5), (30, 5));
        set.AddRange(new Range(3, 20));
        Assert.Equal([new Range(0, 25), new Range(30, 5)], set.Ranges);
    }

    [Fact]
    public void ContainsAndContainedLength()
    {
        var set = Set((10, 10), (30, 10));
        Assert.False(set.Contains(9));
        Assert.True(set.Contains(10));
        Assert.True(set.Contains(19));
        Assert.False(set.Contains(20));
        Assert.True(set.Contains(35));
        Assert.False(set.Contains(40));

        Assert.Equal(0, set.ContainedLengthFromValue(5));
        Assert.Equal(10, set.ContainedLengthFromValue(10));
        Assert.Equal(3, set.ContainedLengthFromValue(17));
        Assert.Equal(0, set.ContainedLengthFromValue(25));
        Assert.Equal(1, set.ContainedLengthFromValue(39));

        Assert.True(set.ContainsRangeSet(Set((12, 5), (30, 10))));
        Assert.False(set.ContainsRangeSet(Set((15, 10))));
    }

    [Fact]
    public void SubtractPunchesHole()
    {
        var set = Set((0, 100));
        set.SubtractRange(new Range(40, 20));
        Assert.Equal([new Range(0, 40), new Range(60, 40)], set.Ranges);
    }

    [Fact]
    public void SubtractTruncatesAndRemovesAcrossRanges()
    {
        var set = Set((0, 10), (20, 10), (40, 10), (60, 10));
        set.SubtractRange(new Range(5, 40)); // [5, 45)
        Assert.Equal([new Range(0, 5), new Range(45, 5), new Range(60, 10)], set.Ranges);

        // subtracting from the start of a range
        set.SubtractRange(new Range(55, 8));
        Assert.Equal([new Range(0, 5), new Range(45, 5), new Range(63, 7)], set.Ranges);

        // subtracting nothing / something disjoint
        set.SubtractRange(new Range(100, 0));
        set.SubtractRange(new Range(20, 5));
        Assert.Equal(17, set.Len);
    }

    [Fact]
    public void MinusUnionIntersection()
    {
        var a = Set((0, 50), (100, 50));
        var b = Set((25, 100));

        Assert.Equal([new Range(0, 25), new Range(125, 25)], a.Minus(b).Ranges);
        Assert.Equal([new Range(0, 150)], a.Union(b).Ranges);
        Assert.Equal([new Range(25, 25), new Range(100, 25)], a.Intersection(b).Ranges);
        Assert.Equal([new Range(25, 25), new Range(100, 25)], b.Intersection(a).Ranges);

        // the originals are untouched
        Assert.Equal([new Range(0, 50), new Range(100, 50)], a.Ranges);
        Assert.Equal([new Range(25, 100)], b.Ranges);

        Assert.True(a.Intersection(Set((60, 10))).IsEmpty);
    }
}
