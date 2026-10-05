using System.Text;
using DefenseClaw.Core.Logs;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

public class LogTailerTests
{
    private static void Append(string path, string text) =>
        File.AppendAllText(path, text, new UTF8Encoding(false));

    [Fact]
    public void Reads_nothing_when_the_file_does_not_exist_yet()
    {
        using var temp = new TempDirectory();
        using var tailer = new LogTailer(temp.File("gateway.log"));

        Assert.Empty(tailer.ReadNewLines());
        Assert.Equal(0, tailer.Offset);
    }

    [Fact]
    public void Reads_the_whole_file_on_first_read()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "[sidecar] one\n[api] two\n");
        using var tailer = new LogTailer(path);

        var lines = tailer.ReadNewLines();

        Assert.Equal(2, lines.Count);
        Assert.Equal("[sidecar] one", lines[0].Raw);
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
    }

    [Fact]
    public void Only_new_lines_are_returned_on_subsequent_reads()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "first\n");
        using var tailer = new LogTailer(path);

        Assert.Single(tailer.ReadNewLines());
        Assert.Empty(tailer.ReadNewLines());

        Append(path, "second\nthird\n");
        var lines = tailer.ReadNewLines();

        Assert.Equal(new[] { "second", "third" }, lines.Select(l => l.Raw));
    }

    [Fact]
    public void Partial_lines_are_held_back_until_the_writer_finishes_them()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "complete\n");
        using var tailer = new LogTailer(path);
        tailer.ReadNewLines();

        Append(path, "incomple");
        Assert.Empty(tailer.ReadNewLines());

        Append(path, "te\n");
        Assert.Equal(new[] { "incomplete" }, tailer.ReadNewLines().Select(l => l.Raw));
    }

    [Fact]
    public void Truncation_resets_the_offset_and_raises_the_event()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "line one\nline two\nline three\n");
        using var tailer = new LogTailer(path);

        var truncations = 0;
        tailer.Truncated += (_, _) => truncations++;

        Assert.Equal(3, tailer.ReadNewLines().Count);
        var offsetBefore = tailer.Offset;
        Assert.True(offsetBefore > 0);

        // Log rotation: the file is replaced by a shorter one.
        File.WriteAllText(path, "rotated\n", new UTF8Encoding(false));
        var lines = tailer.ReadNewLines();

        Assert.Equal(1, truncations);
        Assert.Equal(new[] { "rotated" }, lines.Select(l => l.Raw));
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
    }

    [Fact]
    public void Deleting_the_file_resets_the_offset()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "line one\n");
        using var tailer = new LogTailer(path);
        tailer.ReadNewLines();

        var truncations = 0;
        tailer.Truncated += (_, _) => truncations++;

        File.Delete(path);
        Assert.Empty(tailer.ReadNewLines());
        Assert.Equal(0, tailer.Offset);
        Assert.Equal(1, truncations);

        File.WriteAllText(path, "fresh start\n", new UTF8Encoding(false));
        Assert.Equal(new[] { "fresh start" }, tailer.ReadNewLines().Select(l => l.Raw));
    }

    [Fact]
    public void StartAtEnd_skips_existing_content()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "history\n");
        using var tailer = new LogTailer(path, new LogTailerOptions { StartAtEnd = true });

        Assert.Empty(tailer.ReadNewLines());

        Append(path, "live\n");
        Assert.Equal(new[] { "live" }, tailer.ReadNewLines().Select(l => l.Raw));
    }

    [Fact]
    public void Reset_replays_from_the_beginning()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "a\nb\n");
        using var tailer = new LogTailer(path);

        Assert.Equal(2, tailer.ReadNewLines().Count);
        tailer.Reset();
        Assert.Equal(2, tailer.ReadNewLines().Count);
    }

    [Fact]
    public void Batch_size_is_capped()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", string.Concat(Enumerable.Range(0, 50).Select(i => $"line {i}\n")));
        using var tailer = new LogTailer(path, new LogTailerOptions { MaxLinesPerBatch = 10 });

        Assert.Equal(10, tailer.ReadNewLines().Count);
        Assert.Equal(10, tailer.ReadNewLines().Count);
    }

    [Fact]
    public void Multi_byte_characters_survive_the_read()
    {
        // gateway.log opens with a box-drawing banner.
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "╔══════╗\n║ hi   ║\n");
        using var tailer = new LogTailer(path);

        var lines = tailer.ReadNewLines();

        Assert.Equal("╔══════╗", lines[0].Raw);
        Assert.Equal("║ hi   ║", lines[1].Raw);
    }

    [Fact]
    public void Windows_line_endings_are_stripped()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "one\r\ntwo\r\n");
        using var tailer = new LogTailer(path);

        Assert.Equal(new[] { "one", "two" }, tailer.ReadNewLines().Select(l => l.Raw));
    }

    [Fact]
    public async Task TailAsync_yields_appended_batches()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "first\n");
        using var tailer = new LogTailer(path, new LogTailerOptions { PollInterval = TimeSpan.FromMilliseconds(50) });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var batches = new List<IReadOnlyList<LogLine>>();
        await foreach (var batch in tailer.TailAsync(cts.Token))
        {
            batches.Add(batch);
            if (batches.Count == 1)
            {
                Append(path, "second\n");
            }
            else
            {
                break;
            }
        }

        Assert.Equal(2, batches.Count);
        Assert.Equal("first", batches[0][0].Raw);
        Assert.Equal("second", batches[1][0].Raw);
    }

    // ---- rotation, BOM, faults (D1-07 / D3-13) ----

    [Fact]
    public void Rotation_to_a_file_larger_than_the_offset_is_detected_and_read_from_the_top()
    {
        // Length alone cannot see this: the replacement is already longer than the old offset, so it looks like growth
        // and the first bytes of the new file were skipped (the "spliced first line"). Recreating within seconds also
        // keeps the old creation time on NTFS (tunneling), so the content anchors have to catch it.
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "old line 1\nold line 2\n");
        using var tailer = new LogTailer(path);
        var truncations = 0;
        tailer.Truncated += (_, _) => truncations++;

        Assert.Equal(2, tailer.ReadNewLines().Count);
        var offsetBefore = tailer.Offset;

        File.Delete(path);
        File.WriteAllText(path, "NEW FILE first line is much longer than the whole old file so it is past the offset\nNEW second\n", new UTF8Encoding(false));
        Assert.True(new FileInfo(path).Length > offsetBefore);

        var lines = tailer.ReadNewLines();

        Assert.Equal(1, truncations);
        Assert.Equal(
            new[] { "NEW FILE first line is much longer than the whole old file so it is past the offset", "NEW second" },
            lines.Select(l => l.Raw));
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
    }

    [Fact]
    public void A_replacement_with_the_same_banner_is_still_told_apart_by_the_bytes_before_the_offset()
    {
        // gateway.log opens with the same banner every run, so the head of a rotated file matches the old one; the last
        // bytes consumed do not.
        using var temp = new TempDirectory();
        var banner = string.Concat(Enumerable.Repeat("╔══════════════════╗\n", 6));
        var path = temp.Write("gateway.log", banner + "[api] old run\n");
        using var tailer = new LogTailer(path);
        var truncations = 0;
        tailer.Truncated += (_, _) => truncations++;
        Assert.Equal(7, tailer.ReadNewLines().Count);

        File.Delete(path);
        File.WriteAllText(path, banner + "[api] a newer run that wrote a lot more than the old one did\n[api] second\n", new UTF8Encoding(false));

        var lines = tailer.ReadNewLines();

        Assert.Equal(1, truncations);
        Assert.Equal(8, lines.Count);
        Assert.Equal("╔══════════════════╗", lines[0].Raw);
    }

    [Fact]
    public void An_ordinary_append_is_not_mistaken_for_a_rotation()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "[api] one\n");
        using var tailer = new LogTailer(path);
        var truncations = 0;
        tailer.Truncated += (_, _) => truncations++;

        Assert.Single(tailer.ReadNewLines());
        for (var i = 0; i < 20; i++)
        {
            Append(path, $"[api] line {i} with a little padding so the anchors move on\n");
            Assert.Single(tailer.ReadNewLines());
        }

        Assert.Equal(0, truncations);
    }

    [Fact]
    public void A_tail_that_starts_at_the_end_still_notices_a_larger_replacement()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "history one\nhistory two\n");
        using var tailer = new LogTailer(path, new LogTailerOptions { StartAtEnd = true });
        var truncations = 0;
        tailer.Truncated += (_, _) => truncations++;
        Assert.Empty(tailer.ReadNewLines());

        File.Delete(path);
        File.WriteAllText(path, "replacement that is longer than the two history lines put together\nsecond\n", new UTF8Encoding(false));

        Assert.Equal(2, tailer.ReadNewLines().Count);
        Assert.Equal(1, truncations);
    }

    [Fact]
    public void A_utf8_byte_order_mark_is_not_part_of_the_first_line()
    {
        using var temp = new TempDirectory();
        var path = temp.File("gateway.log");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[gateway] café\r\n[x] two\r\n")).ToArray());
        using var tailer = new LogTailer(path);

        var lines = tailer.ReadNewLines();

        Assert.Equal(new[] { "[gateway] café", "[x] two" }, lines.Select(l => l.Raw));
        Assert.Equal("gateway", lines[0].Component);
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
    }

    [Fact]
    public void A_bom_only_matters_at_the_start_of_the_file()
    {
        using var temp = new TempDirectory();
        var path = temp.File("gateway.log");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("first\n")).ToArray());
        using var tailer = new LogTailer(path);
        Assert.Single(tailer.ReadNewLines());

        Append(path, "﻿mid-file mark stays\n");

        Assert.Equal("﻿mid-file mark stays", tailer.ReadNewLines().Single().Raw);
    }

    private static LogTailerOptions Fast(bool startAtEnd = false) => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(40),
        FaultBackoff = TimeSpan.FromMilliseconds(20),
        MaxFaultBackoff = TimeSpan.FromMilliseconds(80),
        StartAtEnd = startAtEnd,
    };

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            Thread.Sleep(15);
        }
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_end_the_tail()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "one\n");
        using var tailer = new LogTailer(path, Fast());
        var received = new List<string>();
        var faults = new List<LogTailFaultedEventArgs>();
        var recovered = 0;
        var first = true;

        tailer.LinesReceived += (_, e) =>
        {
            lock (received)
            {
                received.AddRange(e.Lines.Select(l => l.Raw));
            }

            if (first)
            {
                first = false;
                throw new InvalidOperationException("subscriber bug");
            }
        };
        tailer.TailFaulted += (_, e) =>
        {
            lock (faults)
            {
                faults.Add(e);
            }
        };
        tailer.TailRecovered += (_, _) => Interlocked.Increment(ref recovered);

        tailer.StartWatching();
        WaitFor(() => { lock (faults) { return faults.Count == 1; } }, "the fault");
        Append(path, "two\n");
        WaitFor(() => { lock (received) { return received.Count == 2; } }, "the line after the fault");
        WaitFor(() => Volatile.Read(ref recovered) == 1, "the recovery");

        lock (faults)
        {
            Assert.Single(faults);
            Assert.IsType<InvalidOperationException>(faults[0].Exception);
            Assert.Equal(1, faults[0].ConsecutiveFaults);
            Assert.Equal(TimeSpan.FromMilliseconds(20), faults[0].RetryIn);
        }

        lock (received)
        {
            Assert.Equal(new[] { "one", "two" }, received);
        }
    }

    [Fact]
    public void A_subscriber_that_keeps_throwing_backs_off_and_the_fault_count_climbs()
    {
        // One line per batch, so every iteration delivers something to the throwing subscriber: five faults in a row.
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", string.Concat(Enumerable.Range(0, 5).Select(i => $"line {i}\n")));
        using var tailer = new LogTailer(path, new LogTailerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(40),
            FaultBackoff = TimeSpan.FromMilliseconds(20),
            MaxFaultBackoff = TimeSpan.FromMilliseconds(80),
            MaxLinesPerBatch = 1,
        });
        var faults = new List<LogTailFaultedEventArgs>();
        tailer.LinesReceived += (_, _) => throw new InvalidOperationException("always");
        tailer.TailFaulted += (_, e) =>
        {
            lock (faults)
            {
                faults.Add(e);
            }
        };

        tailer.StartWatching();
        WaitFor(() => { lock (faults) { return faults.Count >= 5; } }, "five faults");

        lock (faults)
        {
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, faults.Take(5).Select(f => f.ConsecutiveFaults));
            Assert.Equal(
                new[] { 20, 40, 80, 80, 80 },
                faults.Take(5).Select(f => (int)f.RetryIn.TotalMilliseconds));
        }
    }

    [Fact]
    public void A_handler_of_TailFaulted_that_throws_does_not_end_the_tail()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "one\n");
        using var tailer = new LogTailer(path, Fast());
        var received = new List<string>();
        var throwOnce = true;

        tailer.LinesReceived += (_, e) =>
        {
            lock (received)
            {
                received.AddRange(e.Lines.Select(l => l.Raw));
            }

            if (throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("subscriber bug");
            }
        };
        tailer.TailFaulted += (_, _) => throw new InvalidOperationException("reporting bug");

        tailer.StartWatching();
        WaitFor(() => !throwOnce, "the first batch");
        Append(path, "two\n");

        WaitFor(() => { lock (received) { return received.Count == 2; } }, "the line after both faults");
    }

    [Fact]
    public void StartWatching_is_idempotent()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", string.Empty);
        using var tailer = new LogTailer(path, Fast());
        var received = new List<string>();
        tailer.LinesReceived += (_, e) =>
        {
            lock (received)
            {
                received.AddRange(e.Lines.Select(l => l.Raw));
            }
        };

        tailer.StartWatching();
        tailer.StartWatching();
        tailer.StartWatching();
        Thread.Sleep(100);
        Append(path, "x1\nx2\nx3\n");

        WaitFor(() => { lock (received) { return received.Count >= 3; } }, "the lines");
        Thread.Sleep(300);

        lock (received)
        {
            Assert.Equal(new[] { "x1", "x2", "x3" }, received);
        }
    }

    [Fact]
    public void Disposing_stops_the_loop_and_a_later_StartWatching_does_nothing()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "one\n");
        var tailer = new LogTailer(path, Fast());
        var received = 0;
        tailer.LinesReceived += (_, e) => Interlocked.Add(ref received, e.Lines.Count);

        tailer.StartWatching();
        WaitFor(() => Volatile.Read(ref received) == 1, "the first line");
        tailer.Dispose();
        Append(path, "two\n");
        tailer.StartWatching();
        Thread.Sleep(300);

        Assert.Equal(1, Volatile.Read(ref received));
    }

    [Theory]
    [InlineData("[sidecar] starting subsystems", null, "sidecar", "starting subsystems")]
    [InlineData("[watchdog] gateway recovered", null, "watchdog", "gateway recovered")]
    [InlineData("2026-07-28T12:13:19Z [api] listening", "2026-07-28T12:13:19Z", "api", "listening")]
    [InlineData("plain message with no structure", null, null, "plain message with no structure")]
    public void Parses_the_shapes_the_gateway_writes(string raw, string? timestamp, string? component, string message)
    {
        var line = LogLine.Parse(raw);

        Assert.Equal(raw, line.Raw);
        Assert.Equal(component, line.Component);
        Assert.Equal(message, line.Message);
        if (timestamp is null)
        {
            Assert.Null(line.Timestamp);
        }
        else
        {
            Assert.Equal(DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture), line.Timestamp);
        }
    }

    [Theory]
    [InlineData("2026-07-28T12:13:19Z ERROR something broke", LogLevel.Error)]
    [InlineData("[WARN] heads up", LogLevel.Warn)]
    [InlineData("INFO: all good", LogLevel.Info)]
    [InlineData("[sidecar] no level here", LogLevel.Unknown)]
    public void Parses_levels_best_effort(string raw, LogLevel expected)
    {
        Assert.Equal(expected, LogLine.Parse(raw).Level);
    }

    // ---- seeding from the tail (CUST-256) ----

    private static string Numbered(int from, int count) =>
        string.Concat(Enumerable.Range(from, count).Select(n => $"[api] line {n:D7} padding padding padding\n"));

    private static string Row(int n) => $"[api] line {n:D7} padding padding padding";

    [Fact]
    public void Seeding_a_large_file_returns_only_the_newest_lines_and_resumes_live_with_no_gap_or_duplicate()
    {
        // Scaled down from the 300 MB case: ~3.9 MB, so the 5,000-line cap bites inside the 512 KB window.
        using var temp = new TempDirectory();
        const int total = 90_000;
        var path = temp.Write("gateway.log", Numbered(0, total));
        using var tailer = new LogTailer(path);

        var lines = tailer.SeedFromTail();

        Assert.Equal(LogTailer.DefaultSeedLines, lines.Count);
        Assert.Equal(Row(total - 5000), lines[0].Raw);
        Assert.Equal(Row(total - 1), lines[^1].Raw);
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
        Assert.Empty(tailer.ReadNewLines());

        Append(path, Numbered(total, 3));
        Assert.Equal(new[] { Row(total), Row(total + 1), Row(total + 2) }, tailer.ReadNewLines().Select(l => l.Raw));
    }

    [Fact]
    public void The_byte_window_bounds_the_seed_and_drops_the_line_it_cuts()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", Numbered(0, 2000));
        using var tailer = new LogTailer(path);

        var lines = tailer.SeedFromTail(maxBytes: 4096, maxLines: 5000);

        Assert.InRange(lines.Count, 1, 4096 / 40);
        Assert.Equal(Row(1999), lines[^1].Raw);
        // Whole lines, contiguous up to the end: none cut, none skipped inside the window.
        var first = int.Parse(lines[0].Raw.AsSpan(11, 7));
        Assert.Equal(Row(first), lines[0].Raw);
        Assert.Equal(2000 - first, lines.Count);
    }

    [Fact]
    public void A_window_that_opens_exactly_on_a_line_boundary_keeps_that_line()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "aaaa\nbbbb\ncccc\n");
        using var tailer = new LogTailer(path);

        // 10 bytes is "bbbb\ncccc\n" exactly.
        Assert.Equal(new[] { "bbbb", "cccc" }, tailer.SeedFromTail(maxBytes: 10).Select(l => l.Raw));
    }

    [Fact]
    public void A_window_that_cuts_inside_a_multibyte_character_never_shows_a_broken_first_line()
    {
        using var temp = new TempDirectory();
        // Box-drawing characters are 3 bytes in UTF-8: try every cut position across the lines.
        var line = "[api] ╔═══╗ ünï 日本語 end";
        var path = temp.Write("gateway.log", line + "\n" + line + "\n" + line + "\n");
        var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;

        for (var window = 1; window <= lineBytes * 2 + 3; window++)
        {
            using var tailer = new LogTailer(path);
            var lines = tailer.SeedFromTail(maxBytes: window);

            Assert.All(lines, l => Assert.Equal(line, l.Raw));
            Assert.Equal(window / lineBytes, lines.Count);
        }
    }

    [Fact]
    public void A_file_smaller_than_the_window_is_read_whole_and_the_bom_is_skipped()
    {
        using var temp = new TempDirectory();
        var path = temp.File("gateway.log");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[api] one\r\n[api] two\n")).ToArray());
        using var tailer = new LogTailer(path);

        var lines = tailer.SeedFromTail();

        Assert.Equal(new[] { "[api] one", "[api] two" }, lines.Select(l => l.Raw));
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
    }

    [Fact]
    public void A_partial_trailing_line_is_held_back_by_the_seed_and_delivered_once_complete()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "one\ntwo\nthr");
        using var tailer = new LogTailer(path);

        Assert.Equal(new[] { "one", "two" }, tailer.SeedFromTail().Select(l => l.Raw));
        Assert.Equal(8, tailer.Offset);

        Append(path, "ee\n");
        Assert.Equal(new[] { "three" }, tailer.ReadNewLines().Select(l => l.Raw));
    }

    [Fact]
    public void Seeding_a_missing_or_empty_file_is_empty_and_the_file_is_picked_up_when_it_appears()
    {
        using var temp = new TempDirectory();
        var path = temp.File("gateway.log");
        using var tailer = new LogTailer(path);

        Assert.Empty(tailer.SeedFromTail());
        Assert.Equal(0, tailer.Offset);

        File.WriteAllText(path, "");
        Assert.Empty(tailer.SeedFromTail());

        Append(path, "hello\n");
        Assert.Equal(new[] { "hello" }, tailer.ReadNewLines().Select(l => l.Raw));
    }

    [Fact]
    public void Seeding_works_while_the_writer_holds_the_file_open()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", "one\ntwo\n");
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var tailer = new LogTailer(path);

        Assert.Equal(new[] { "one", "two" }, tailer.SeedFromTail().Select(l => l.Raw));
    }

    [Fact]
    public void A_seeded_tailer_still_detects_replacement_and_truncation_and_caps_the_replay()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", Numbered(0, 30_000));
        using var tailer = new LogTailer(path);
        var truncations = 0;
        tailer.Truncated += (_, _) => truncations++;
        tailer.SeedFromTail();

        // Replaced by a file still longer than the offset: the anchors, not the length, must catch it.
        File.Delete(path);
        File.WriteAllText(path, Numbered(500_000, 40_000), new UTF8Encoding(false));
        Assert.True(new FileInfo(path).Length > tailer.Offset);

        var lines = tailer.ReadNewLines();

        Assert.Equal(1, truncations);
        // The replay is capped like the seed, not read from byte 0.
        Assert.Equal(LogTailer.DefaultSeedLines, lines.Count);
        Assert.Equal(Row(539_999), lines[^1].Raw);
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);

        File.WriteAllText(path, "short\n", new UTF8Encoding(false));
        Assert.Equal(new[] { "short" }, tailer.ReadNewLines().Select(l => l.Raw));
        Assert.Equal(2, truncations);
    }

    [Fact]
    public void Seed_work_is_bounded_by_the_window_not_the_file_size()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("gateway.log", Numbered(0, 150_000)); // ~6 MB
        using var tailer = new LogTailer(path);

        var lines = tailer.SeedFromTail(maxBytes: 64 * 1024, maxLines: 100);

        Assert.Equal(100, lines.Count);
        Assert.Equal(Row(149_999), lines[^1].Raw);
    }
}
