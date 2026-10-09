namespace Airplay2.Sender.Playback;

/// <summary>
/// The PCM ring between the producer (librespot's player thread) and the pacer (SPEC-audio.md 8.2): interleaved 16-bit
/// stereo frames with absolute 64-bit write/read counters, so a seek mark taken on one thread can later drop exactly the
/// frames written before it (SPEC-audio.md 8.4, <c>Seeked</c>). Not thread-safe; the group player guards it with its lock.
/// </summary>
internal sealed class PcmRing
{
    private readonly short[] _buffer;
    private long _written;
    private long _read;

    /// <summary>Creates a ring of <paramref name="capacityFrames"/> frames.</summary>
    public PcmRing(int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        CapacityFrames = capacityFrames;
        _buffer = new short[capacityFrames * 2];
    }

    /// <summary>Capacity in frames.</summary>
    public int CapacityFrames { get; }

    /// <summary>Frames queued.</summary>
    public int Count => (int)(_written - _read);

    /// <summary>Frames that fit.</summary>
    public int Free => CapacityFrames - Count;

    /// <summary>Frames ever written (the absolute index of the next frame written).</summary>
    public long WrittenFrames => _written;

    /// <summary>Copies as many whole frames of <paramref name="interleaved"/> as fit; returns the frames copied.</summary>
    public int Write(ReadOnlySpan<short> interleaved)
    {
        var frames = Math.Min(interleaved.Length / 2, Free);
        var start = (int)(_written % CapacityFrames);
        var first = Math.Min(frames, CapacityFrames - start);
        interleaved[..(first * 2)].CopyTo(_buffer.AsSpan(start * 2));
        interleaved.Slice(first * 2, (frames - first) * 2).CopyTo(_buffer);
        _written += frames;
        return frames;
    }

    /// <summary>Moves up to <paramref name="destination"/>.Length / 2 frames out; returns the frames read.</summary>
    public int Read(Span<short> destination)
    {
        var frames = Math.Min(destination.Length / 2, Count);
        var start = (int)(_read % CapacityFrames);
        var first = Math.Min(frames, CapacityFrames - start);
        _buffer.AsSpan(start * 2, first * 2).CopyTo(destination);
        _buffer.AsSpan(0, (frames - first) * 2).CopyTo(destination[(first * 2)..]);
        _read += frames;
        return frames;
    }

    /// <summary>Drops every queued frame whose absolute index is below <paramref name="mark"/>; returns the frames dropped.</summary>
    public int DropUntil(long mark)
    {
        var target = Math.Clamp(mark, _read, _written);
        var dropped = (int)(target - _read);
        _read = target;
        return dropped;
    }

    /// <summary>Drops everything queued; returns the frames dropped.</summary>
    public int Clear() => DropUntil(_written);
}
