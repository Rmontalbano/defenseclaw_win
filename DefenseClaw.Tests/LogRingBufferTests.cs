using System.Diagnostics;
using DefenseClaw.Core.Logs;
using Xunit.Abstractions;

namespace DefenseClaw.Tests;

/// <summary>
/// The log panel's bounded buffer. The property that matters is the cost of staying bounded:
/// a <c>List&lt;T&gt;</c> trimmed with <c>RemoveRange(0, n)</c> shifts every surviving element for every
/// line a full buffer accepts (O(lines x capacity)); the ring evicts in place (O(lines)).
/// </summary>
public class LogRingBufferTests
{
    private readonly ITestOutputHelper _output;

    public LogRingBufferTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static LogLine Line(long n) => LogLine.Parse($"[test] line {n}", n);

    private static long[] Sequences(LogRingBuffer buffer) => buffer.Select(l => l.Sequence).ToArray();

    [Fact]
    public void Below_capacity_it_is_a_plain_ordered_list()
    {
        var buffer = new LogRingBuffer(10);

        for (var i = 0; i < 4; i++)
        {
            Assert.False(buffer.Add(Line(i)));
        }

        Assert.Equal(4, buffer.Count);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, Sequences(buffer));
        Assert.Equal(0, buffer[0].Sequence);
        Assert.Equal(3, buffer[3].Sequence);
        Assert.Equal(0, buffer.TotalEvicted);
    }

    [Fact]
    public void Once_full_each_add_evicts_the_oldest_and_order_is_kept()
    {
        var buffer = new LogRingBuffer(5);

        for (var i = 0; i < 12; i++)
        {
            var evicted = buffer.Add(Line(i));
            Assert.Equal(i >= 5, evicted);
        }

        Assert.Equal(5, buffer.Count);
        Assert.Equal(new long[] { 7, 8, 9, 10, 11 }, Sequences(buffer));
        Assert.Equal(7, buffer[0].Sequence);
        Assert.Equal(11, buffer[4].Sequence);
        Assert.Equal(7, buffer.TotalEvicted);
    }

    [Fact]
    public void A_capacity_of_one_keeps_only_the_newest_line()
    {
        var buffer = new LogRingBuffer(1);

        buffer.Add(Line(1));
        buffer.Add(Line(2));
        buffer.Add(Line(3));

        Assert.Equal(new long[] { 3 }, Sequences(buffer));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void Growth_toward_the_capacity_never_reorders_or_drops_anything(int capacity)
    {
        var buffer = new LogRingBuffer(capacity);

        // Fill exactly, so every doubling (64, 128, ... capped at capacity) has happened.
        for (var i = 0; i < capacity; i++)
        {
            buffer.Add(Line(i));
        }

        Assert.Equal(capacity, buffer.Count);
        Assert.Equal(Enumerable.Range(0, capacity).Select(i => (long)i), Sequences(buffer));

        // And a full lap plus a bit more, so the wrap crosses the array end at least once.
        for (var i = capacity; i < capacity + capacity + 7; i++)
        {
            buffer.Add(Line(i));
        }

        Assert.Equal(capacity, buffer.Count);
        Assert.Equal(
            Enumerable.Range(capacity + 7, capacity).Select(i => (long)i),
            Sequences(buffer));
    }

    [Fact]
    public void Indexing_outside_the_held_lines_throws()
    {
        var buffer = new LogRingBuffer(4);
        buffer.Add(Line(0));
        buffer.Add(Line(1));

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[2]);
    }

    [Fact]
    public void Clear_empties_it_and_it_can_be_refilled_from_the_top()
    {
        var buffer = new LogRingBuffer(4);
        for (var i = 0; i < 9; i++)
        {
            buffer.Add(Line(i));
        }

        buffer.Clear();

        Assert.Empty(buffer);

        buffer.Add(Line(100));
        buffer.Add(Line(101));

        Assert.Equal(new long[] { 100, 101 }, Sequences(buffer));
    }

    [Fact]
    public void A_capacity_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogRingBuffer(0));
    }

    // ------------------------------------------------------------------ cost

    [Fact]
    public void Once_full_adding_lines_copies_no_elements_at_all()
    {
        // The deterministic form of "trim is O(1)": the only copying the buffer ever does is
        // growing its backing array on the way to the capacity, and that stops at the first fill.
        // A RemoveRange(0, 1) trim would have moved ~capacity elements for every line after it.
        const int capacity = 5000;
        var buffer = new LogRingBuffer(capacity);

        for (var i = 0; i < capacity; i++)
        {
            buffer.Add(Line(i));
        }

        var copiedAtFirstFill = buffer.ElementsCopied;
        Assert.InRange(copiedAtFirstFill, 0, 2L * capacity);

        for (var i = capacity; i < capacity + 200_000; i++)
        {
            buffer.Add(Line(i));
        }

        Assert.Equal(copiedAtFirstFill, buffer.ElementsCopied);
        Assert.Equal(200_000, buffer.TotalEvicted);
    }

    [Fact]
    public void Trimming_cost_is_linear_in_lines_not_lines_times_capacity()
    {
        // Replaying a 100,000-line file through the panel's 5,000-line window (the seed path
        // after a long-running gateway) - and the same again, to get a timing worth reading.
        const int capacity = 5000;
        const int lines = 200_000;
        var input = Enumerable.Range(0, lines).Select(i => Line(i)).ToArray();

        var ring = new LogRingBuffer(capacity);
        var ringWatch = Stopwatch.StartNew();
        foreach (var line in input)
        {
            ring.Add(line);
        }

        ringWatch.Stop();

        // The old shape: append, then trim the excess off the front of a List.
        var list = new List<LogLine>();
        var moves = 0L;
        var listWatch = Stopwatch.StartNew();
        foreach (var line in input)
        {
            list.Add(line);
            var excess = list.Count - capacity;
            if (excess > 0)
            {
                moves += list.Count - excess;
                list.RemoveRange(0, excess);
            }
        }

        listWatch.Stop();

        _output.WriteLine(
            $"{lines:N0} lines through a {capacity:N0}-line window: ring {ringWatch.Elapsed.TotalMilliseconds:N1} ms " +
            $"(0 elements moved after the first fill), List+RemoveRange {listWatch.Elapsed.TotalMilliseconds:N1} ms " +
            $"({moves:N0} elements moved).");

        Assert.Equal(capacity, ring.Count);
        Assert.Equal(list.Select(l => l.Sequence), Sequences(ring));

        // The list form moves ~capacity elements per line: lines x capacity in all.
        Assert.True(moves > (long)(lines - capacity) * (capacity - 1));

        // A generous absolute bound on the ring (it runs in single-digit milliseconds); a timing
        // ratio against the list would be flaky on a loaded CI machine, so it is only reported above.
        Assert.True(ringWatch.Elapsed < TimeSpan.FromSeconds(2), $"ring took {ringWatch.Elapsed}");
    }
}
