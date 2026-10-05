using System.Collections.Specialized;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Logs;
using Xunit.Abstractions;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// The Logs panel's buffering and projection under load: how many lines it keeps, what it shows, and
/// how much churn a burst causes. Batches are fed through <c>AcceptLines</c>, the UI-thread half of the
/// tailer callback, so no dispatcher is needed.
/// </summary>
public sealed class LogsPanelViewModelTests : IDisposable
{
    private const int Cap = 5000;

    private readonly ITestOutputHelper _output;
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public LogsPanelViewModelTests(ITestOutputHelper output)
    {
        _output = output;
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static IReadOnlyList<LogLine> Lines(int from, int count, string text = "line") =>
        Enumerable.Range(from, count).Select(n => LogLine.Parse($"[api] {text} {n}", n)).ToArray();

    private LogsPanelViewModel ActivePanel()
    {
        var panel = new LogsPanelViewModel(_services);
        panel.SetActive(true);
        return panel;
    }

    private static List<NotifyCollectionChangedAction> Record(LogsPanelViewModel panel)
    {
        var seen = new List<NotifyCollectionChangedAction>();
        panel.DisplayedLines.CollectionChanged += (_, e) => seen.Add(e.Action);
        return seen;
    }

    [Fact]
    public void A_batch_that_fits_is_appended_line_by_line()
    {
        var panel = ActivePanel();
        var seen = Record(panel);

        panel.AcceptLines("Gateway", Lines(0, 100));

        Assert.Equal(100, panel.DisplayedLines.Count);
        Assert.Equal(100, panel.BufferedCount);
        Assert.Equal(100, seen.Count);
        Assert.All(seen, action => Assert.Equal(NotifyCollectionChangedAction.Add, action));
        Assert.Equal("100 shown · 100 matching · 100 total", panel.StatusLineCount);
    }

    [Fact]
    public void A_burst_on_a_full_panel_keeps_the_newest_5000_and_trims_in_one_notification()
    {
        var panel = ActivePanel();
        for (var from = 0; from < Cap; from += 1000)
        {
            panel.AcceptLines("Gateway", Lines(from, 1000));
        }

        Assert.Equal(Cap, panel.DisplayedLines.Count);
        var seen = Record(panel);

        // LogTailerOptions.MaxLinesPerBatch: the largest batch one read can produce.
        panel.AcceptLines("Gateway", Lines(Cap, 2000));

        _output.WriteLine($"2,000-line burst on a full panel: {seen.Count} notification(s) (was 4,000: 2,000 Add + 2,000 RemoveAt(0)).");

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, seen);
        Assert.Equal(Cap, panel.DisplayedLines.Count);
        Assert.Equal(2000, panel.DisplayedLines[0].Sequence);
        Assert.Equal(6999, panel.DisplayedLines[^1].Sequence);
        Assert.Equal(Cap, panel.BufferedCount);
        Assert.Equal("5,000 shown · 5,000 matching · 5,000 total", panel.StatusLineCount);
    }

    [Fact]
    public void A_single_batch_bigger_than_the_cap_leaves_only_its_newest_5000()
    {
        var panel = ActivePanel();
        var seen = Record(panel);

        panel.AcceptLines("Gateway", Lines(0, 7000));

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, seen);
        Assert.Equal(Cap, panel.DisplayedLines.Count);
        Assert.Equal(2000, panel.DisplayedLines[0].Sequence);
        Assert.Equal(6999, panel.DisplayedLines[^1].Sequence);
        Assert.Equal(Cap, panel.BufferedCount);
    }

    [Fact]
    public void Only_lines_that_pass_the_filter_count_against_the_display_cap()
    {
        var panel = ActivePanel();
        panel.FilterText = "keep";

        // 8,000 lines, every other one matching: the buffer wraps (5,000 of the 8,000 held) but the list
        // holds only what passes - 4,000 matches, under its own cap, so nothing is trimmed and each
        // arrives as an ordinary Add.
        var batch = Enumerable.Range(0, 8000)
            .Select(n => LogLine.Parse(n % 2 == 0 ? $"[api] keep {n}" : $"[api] drop {n}", n))
            .ToArray();

        var seen = Record(panel);
        panel.AcceptLines("Gateway", batch);

        Assert.Equal(Cap, panel.BufferedCount);
        Assert.Equal(4000, panel.DisplayedLines.Count);
        Assert.All(panel.DisplayedLines, entry => Assert.Contains("keep", entry.Raw));
        Assert.Equal(4000, seen.Count);
        Assert.All(seen, action => Assert.Equal(NotifyCollectionChangedAction.Add, action));
    }

    [Fact]
    public void A_panel_nobody_is_looking_at_keeps_the_newest_5000_without_projecting_any()
    {
        var panel = new LogsPanelViewModel(_services);
        var seen = Record(panel);

        panel.AcceptLines("Gateway", Lines(0, 6500));

        Assert.Equal(Cap, panel.BufferedCount);
        Assert.Empty(panel.DisplayedLines);
        Assert.Empty(seen);
    }

    [Fact]
    public void The_other_source_is_buffered_but_not_shown()
    {
        var panel = ActivePanel();

        panel.AcceptLines("Watchdog", Lines(0, 50));

        Assert.Empty(panel.DisplayedLines);
        Assert.Equal(0, panel.BufferedCount);
    }

    [Fact]
    public async Task Seeding_from_a_long_log_keeps_the_newest_5000_and_shows_them_in_order()
    {
        // A log that has run for a while: 12,000 lines on disk. Replaying it through the 5,000-line
        // window used to shift the whole backing array for every line past the 5,000th.
        var logPath = _services.Paths.GatewayLogPath;
        File.WriteAllLines(logPath, Enumerable.Range(0, 12_000).Select(n => $"[api] seeded {n}"));

        var panel = new LogsPanelViewModel(_services);
        await panel.InitializeAsync();

        Assert.Equal(Cap, panel.BufferedCount);
        Assert.Equal(Cap, panel.DisplayedLines.Count);
        Assert.Equal("[api] seeded 7000", panel.DisplayedLines[0].Raw);
        Assert.Equal("[api] seeded 11999", panel.DisplayedLines[^1].Raw);
        Assert.Equal("5,000 shown · 5,000 matching · 5,000 total", panel.StatusLineCount);
    }

    [Fact]
    public async Task Seeding_and_reloading_a_log_bigger_than_the_window_show_only_its_newest_lines()
    {
        // ~5 MB synthetic log (the real case is hundreds of MB): the seed reads the last 512 KB, not the file.
        var logPath = _services.Paths.GatewayLogPath;
        File.WriteAllLines(logPath, Enumerable.Range(0, 120_000).Select(n => $"[api] seeded {n:D7} padding padding padding"));

        var panel = new LogsPanelViewModel(_services);
        await panel.InitializeAsync();

        Assert.Equal(Cap, panel.BufferedCount);
        Assert.Equal("[api] seeded 0115000 padding padding padding", panel.DisplayedLines[0].Raw);

        // Reload on the big file: emptied and re-seeded from the tail, one bounded read, newest last.
        File.AppendAllText(logPath, "[api] seeded 0120000 padding padding padding\n");
        panel.SetActive(true);
        panel.ReloadFromDiskCommand.Execute(null);

        Assert.Equal(Cap, panel.BufferedCount);
        Assert.Equal(Cap, panel.DisplayedLines.Count);
        Assert.Equal("[api] seeded 0115001 padding padding padding", panel.DisplayedLines[0].Raw);
        Assert.Equal("[api] seeded 0120000 padding padding padding", panel.DisplayedLines[^1].Raw);
    }

    [Fact]
    public async Task Clear_empties_the_buffer_and_the_list_and_new_lines_start_over()
    {
        var logPath = _services.Paths.GatewayLogPath;
        File.WriteAllLines(logPath, Enumerable.Range(0, 300).Select(n => $"[api] seeded {n}"));

        var panel = new LogsPanelViewModel(_services);
        await panel.InitializeAsync();
        panel.SetActive(true);
        Assert.Equal(300, panel.BufferedCount);

        panel.ClearCommand.Execute(null);

        Assert.Equal(0, panel.BufferedCount);
        Assert.Empty(panel.DisplayedLines);

        panel.AcceptLines("Gateway", Lines(1000, 3));

        Assert.Equal(3, panel.BufferedCount);
        Assert.Equal(3, panel.DisplayedLines.Count);
    }

    // ---- a faulted tail says so (D1-07 / D3-13) ----

    private static LogTailFaultedEventArgs Fault(string message = "subscriber bug", int consecutive = 1, int retrySeconds = 2) =>
        new(new InvalidOperationException(message), consecutive, TimeSpan.FromSeconds(retrySeconds));

    [Fact]
    public void A_tail_fault_shows_a_paused_state_naming_the_error_and_the_retry()
    {
        var panel = ActivePanel();
        Assert.False(panel.ShowTailFault);

        panel.AcceptTailFault("Gateway", Fault("boom"));

        Assert.True(panel.ShowTailFault);
        Assert.Contains("gateway.log", panel.TailFaultText, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: boom", panel.TailFaultText, StringComparison.Ordinal);
        Assert.Contains("retries in 2 s", panel.TailFaultText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recovered_tail_clears_the_paused_state()
    {
        var panel = ActivePanel();
        panel.AcceptTailFault("Gateway", Fault());

        panel.AcceptTailRecovered("Gateway");

        Assert.False(panel.ShowTailFault);
        Assert.Equal(string.Empty, panel.TailFaultText);
    }

    [Fact]
    public void A_fault_on_the_other_source_shows_only_when_that_source_is_switched_to()
    {
        var panel = ActivePanel();

        panel.AcceptTailFault("Watchdog", Fault("watchdog trouble"));
        Assert.False(panel.ShowTailFault);

        panel.SelectSourceCommand.Execute("Watchdog");
        Assert.True(panel.ShowTailFault);
        Assert.Contains("watchdog.log", panel.TailFaultText, StringComparison.Ordinal);

        panel.SelectSourceCommand.Execute("Gateway");
        Assert.False(panel.ShowTailFault);
    }

    [Fact]
    public void A_fault_message_is_kept_on_one_line()
    {
        var panel = ActivePanel();

        panel.AcceptTailFault("Gateway", Fault("first\r\nsecond"));

        Assert.DoesNotContain('\n', panel.TailFaultText);
        Assert.Contains("first second", panel.TailFaultText, StringComparison.Ordinal);
    }
}
