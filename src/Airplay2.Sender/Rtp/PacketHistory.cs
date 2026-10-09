namespace Airplay2.Sender.Rtp;

/// <summary>
/// The retransmit ring of one receiver (SPEC-audio.md 2.4, SPEC.md D8): the last <see cref="Capacity"/> fully built,
/// encrypted packets, indexed by RTP sequence number, so a retransmission is byte-identical to the original (same
/// nonce, same ciphertext). Cleared on TEARDOWN, kept across FLUSH (receivers do not ask for pre-flush packets).
/// </summary>
/// <remarks>
/// <para>
/// Wrap safety: the window is <c>[newest - Capacity + 1, newest]</c> in 16-bit sequence arithmetic, the same rule as
/// "drop requests outside <c>[next_seq - 1000, next_seq)</c>" in the spec. Slots are indexed by
/// <c>sequence &amp; 1023</c> rather than <c>sequence % 1000</c>, because 65 536 is a multiple of 1024 but not of 1000: a
/// modulo-1000 ring maps two sequence numbers inside one window to the same slot around the 16-bit wrap (65 000 and 0
/// both land in slot 0). Each slot remembers the sequence number it holds and a slot only answers for that number.
/// </para>
/// <para>
/// Thread safety: <see cref="Store"/> runs on the pacer thread, <see cref="TryGet"/> on the control port's receive loop,
/// <see cref="Clear"/> on whichever thread runs TEARDOWN; a short lock serialises them (uncontended almost always).
/// </para>
/// </remarks>
public sealed class PacketHistory : IPacketHistory
{
    private const int SlotCount = 1024;
    private const int SlotMask = SlotCount - 1;

    private readonly Lock _gate = new();
    private readonly byte[] _buffer;
    private readonly int[] _lengths = new int[SlotCount];
    private readonly ushort[] _sequences = new ushort[SlotCount];
    private readonly int _slotSize;
    private ushort _newest;
    private bool _any;

    /// <summary>Creates a ring.</summary>
    /// <param name="capacity">Window in packets, 1..1024; default 1000 (8 s, both reference senders).</param>
    /// <param name="maxPacketSize">Largest packet stored; default <see cref="RealtimeStreamConstants.MaxPacketSize"/>.</param>
    public PacketHistory(int capacity = RealtimeStreamConstants.HistoryPackets, int maxPacketSize = RealtimeStreamConstants.MaxPacketSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, SlotCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPacketSize, RealtimeStreamConstants.HeaderSize);
        Capacity = capacity;
        _slotSize = maxPacketSize;
        _buffer = new byte[SlotCount * maxPacketSize];
    }

    /// <summary>The window in packets.</summary>
    public int Capacity { get; }

    /// <summary>The largest packet a slot holds.</summary>
    public int MaxPacketSize => _slotSize;

    /// <summary>The newest stored sequence number, or null when empty.</summary>
    public ushort? Newest
    {
        get
        {
            lock (_gate)
            {
                return _any ? _newest : null;
            }
        }
    }

    /// <inheritdoc />
    public void Store(ushort sequence, ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0 || packet.Length > _slotSize)
        {
            throw new ArgumentException($"Packet of {packet.Length} bytes does not fit a {_slotSize}-byte history slot.", nameof(packet));
        }

        lock (_gate)
        {
            if (!_any)
            {
                _newest = sequence;
                _any = true;
            }
            else
            {
                var ahead = (ushort)(sequence - _newest);
                if (ahead != 0 && ahead < 0x8000)
                {
                    // Forward. Slots skipped by a jump would otherwise keep packets from 1024 sequence numbers ago
                    // that happen to share the slot index; invalidate them.
                    var skipped = Math.Min(ahead - 1, SlotCount);
                    for (var i = 1; i <= skipped; i++)
                    {
                        _lengths[(_newest + i) & SlotMask] = 0;
                    }

                    _newest = sequence;
                }

                // Backward or equal: an overwrite inside the window (never expected, sequence numbers are not reused),
                // newest stays.
            }

            var slot = sequence & SlotMask;
            packet.CopyTo(_buffer.AsSpan(slot * _slotSize, _slotSize));
            _lengths[slot] = packet.Length;
            _sequences[slot] = sequence;
        }
    }

    /// <inheritdoc />
    public bool TryGet(ushort sequence, Span<byte> destination, out int length)
    {
        length = 0;
        lock (_gate)
        {
            if (!_any || (ushort)(_newest - sequence) >= Capacity)
            {
                return false;
            }

            var slot = sequence & SlotMask;
            var stored = _lengths[slot];
            if (stored == 0 || _sequences[slot] != sequence || destination.Length < stored)
            {
                return false;
            }

            _buffer.AsSpan(slot * _slotSize, stored).CopyTo(destination);
            length = stored;
            return true;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_lengths);
            _any = false;
            _newest = 0;
        }
    }
}
