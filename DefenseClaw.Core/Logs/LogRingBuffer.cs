using System.Collections;

namespace DefenseClaw.Core.Logs;

/// <summary>
/// The newest <see cref="Capacity"/> lines of a log, oldest first. Once full, every
/// <see cref="Add"/> overwrites the oldest slot in place, so keeping the buffer bounded costs O(1)
/// per line and never moves an element.
/// <para>
/// <b>Why not a <see cref="List{T}"/> trimmed with <c>RemoveRange(0, n)</c>.</b> That is an array
/// shift of everything that survives, and a full buffer needs one for <i>every</i> line it accepts:
/// replaying a 100,000-line log through a 5,000-line window shifted ~5,000 references 95,000 times
/// (about half a billion moves) on the UI thread, and a 2,000-line burst on a full buffer did
/// 2,000 shifts inside one dispatcher callback (performance/durability eval #24).
/// </para>
/// <para>
/// Storage grows lazily, doubling up to <see cref="Capacity"/>, so a quiet log does not pay for
/// the whole window up front; the copying growth does is bounded by twice the capacity for the
/// life of the buffer (<see cref="ElementsCopied"/>). Not thread-safe: the log panel touches a
/// buffer from one thread at a time (a background seed, then the UI thread).
/// </para>
/// </summary>
public sealed class LogRingBuffer : IReadOnlyList<LogLine>
{
    private const int InitialSlots = 64;

    private LogLine?[] _slots;
    private int _head;
    private int _count;

    public LogRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Capacity = capacity;
        _slots = new LogLine?[Math.Min(capacity, InitialSlots)];
    }

    /// <summary>The most lines this buffer holds; adding beyond it evicts the oldest.</summary>
    public int Capacity { get; }

    public int Count => _count;

    /// <summary>Lines dropped off the front because the buffer was full, since construction.</summary>
    public long TotalEvicted { get; private set; }

    /// <summary>
    /// Elements moved by growing the backing array, since construction. A diagnostic: it stops
    /// increasing once the buffer first fills, no matter how many more lines pass through, which is
    /// what "trim is O(1)" means in a test.
    /// </summary>
    public long ElementsCopied { get; private set; }

    /// <summary>The line at <paramref name="index"/>, 0 being the oldest one still held.</summary>
    public LogLine this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _slots[(_head + index) % _slots.Length]!;
        }
    }

    /// <summary>Appends <paramref name="line"/>; returns true when that pushed the oldest line out.</summary>
    public bool Add(LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (_count == _slots.Length && _slots.Length < Capacity)
        {
            Grow();
        }

        if (_count < _slots.Length)
        {
            _slots[(_head + _count) % _slots.Length] = line;
            _count++;
            return false;
        }

        _slots[_head] = line;
        _head = (_head + 1) % _slots.Length;
        TotalEvicted++;
        return true;
    }

    /// <summary>Drops every line. The eviction and copy counters keep their totals.</summary>
    public void Clear()
    {
        // Null the slots so the dropped lines can be collected; the array itself is kept.
        Array.Clear(_slots);
        _head = 0;
        _count = 0;
    }

    public IEnumerator<LogLine> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return _slots[(_head + i) % _slots.Length]!;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Grow()
    {
        var grown = new LogLine?[(int)Math.Min((long)_slots.Length * 2, Capacity)];

        // Unroll the ring into order, so the head is back at slot 0.
        var tail = Math.Min(_count, _slots.Length - _head);
        Array.Copy(_slots, _head, grown, 0, tail);
        Array.Copy(_slots, 0, grown, tail, _count - tail);

        ElementsCopied += _count;
        _slots = grown;
        _head = 0;
    }
}
