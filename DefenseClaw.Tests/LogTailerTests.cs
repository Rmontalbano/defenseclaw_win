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
}
