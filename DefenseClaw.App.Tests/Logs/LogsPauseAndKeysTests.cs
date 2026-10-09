using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-263: the small controls the 0.8.10 TUI's Logs panel has and this one lacked - "+N since pause" in the status line, E and W for the Errors and Warnings+
/// presets, Home and End for the first row and the newest - at the view-model, plus the shortcut map they must not collide with. The same keys through the real
/// view are <see cref="LogsKeysViewTests"/>.
/// </summary>
public sealed class LogsPauseAndKeysTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public LogsPauseAndKeysTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
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

    // ---- "+N since pause" ----

    [Fact]
    public void While_the_list_follows_the_newest_row_the_status_line_has_no_pause_count()
    {
        var panel = ActivePanel();

        panel.AcceptLines("Gateway", Lines(0, 3));

        Assert.True(panel.AutoScroll);
        Assert.Equal("3 shown · 3 matching · 3 total", panel.StatusLineCount);
        Assert.Equal(0, panel.SincePause);
    }

    [Fact]
    public void Pausing_starts_at_zero_and_counts_every_line_the_source_receives_before_the_filters()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines(0, 5));

        panel.AutoScroll = false;
        Assert.Equal("5 shown · 5 matching · 5 total · +0 since pause", panel.StatusLineCount);

        panel.AcceptLines("Gateway", Lines(5, 3));
        Assert.Equal("8 shown · 8 matching · 8 total · +3 since pause", panel.StatusLineCount);
        Assert.Equal(3, panel.SincePause);

        // A heartbeat that no-noise hides is still a line the source received, as it is in the TUI's count.
        panel.AcceptLines("Gateway", new[] { LogLine.Parse("[ws] event tick seq=9", 9) });
        Assert.Equal("8 shown · 8 matching · 9 total · +4 since pause", panel.StatusLineCount);
    }

    [Fact]
    public void Resuming_drops_the_count_and_pausing_again_starts_over()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines(0, 2));
        panel.AutoScroll = false;
        panel.AcceptLines("Gateway", Lines(2, 2));
        Assert.EndsWith("· +2 since pause", panel.StatusLineCount, StringComparison.Ordinal);

        panel.AutoScroll = true;
        Assert.Equal("4 shown · 4 matching · 4 total", panel.StatusLineCount);
        Assert.Equal(0, panel.SincePause);

        panel.AutoScroll = false;
        Assert.EndsWith("· +0 since pause", panel.StatusLineCount, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_buffer_still_counts_what_arrives()
    {
        // The TUI counts the growth of its line list, which stops growing at 5,000; counting arrivals does not.
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines(0, 5000));
        panel.AutoScroll = false;

        panel.AcceptLines("Gateway", Lines(5000, 120));

        Assert.Equal(120, panel.SincePause);
        Assert.EndsWith("· +120 since pause", panel.StatusLineCount, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_stream_counts_from_the_moment_of_the_pause_and_a_switch_keeps_the_baselines()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Watchdog", Lines(0, 2));
        panel.AutoScroll = false;

        panel.AcceptLines("Watchdog", Lines(2, 4));
        Assert.Equal(0, panel.SincePause);

        panel.ActiveSource = "Watchdog";
        Assert.Equal(4, panel.SincePause);
        Assert.EndsWith("· +4 since pause", panel.StatusLineCount, StringComparison.Ordinal);

        panel.ActiveSource = "Gateway";
        Assert.Equal(0, panel.SincePause);
    }

    [Fact]
    public async Task The_seed_and_a_reload_are_not_arrivals()
    {
        File.WriteAllLines(_services.Paths.GatewayLogPath, Enumerable.Range(0, 20).Select(n => $"[api] seeded {n}"));
        var panel = new LogsPanelViewModel(_services);
        await panel.InitializeAsync();
        panel.SetActive(true);

        panel.AutoScroll = false;
        panel.ReloadFromDiskCommand.Execute(null);

        Assert.Equal(0, panel.SincePause);
        Assert.EndsWith("· +0 since pause", panel.StatusLineCount, StringComparison.Ordinal);
    }

    private string Database(Action<LogsInspectorFieldsTests.AddEvent> seed)
    {
        var path = _temp.File("pause-audit.db");
        LogsInspectorFieldsTests.Fill(path, seed);
        return path;
    }

    [Fact]
    public async Task Verdicts_count_the_rows_a_poll_finds_that_the_one_before_did_not_and_the_first_read_is_not_one_of_them()
    {
        var path = Database(add =>
        {
            add("v-1", 1, "guardrail.evaluation", "guardrail.evaluated", "HIGH", "act", "one", null, null, null);
            add("v-2", 2, "enforcement.action", "enforcement.applied", "HIGH", "act", "two", null, null, null);
        });
        var panel = new LogsPanelViewModel(_services) { StreamReader = new EventStreamReader(path) };
        panel.SetActive(true);
        panel.ActiveSource = "Verdicts";
        panel.SelectedPreset = "all";

        // Paused before anything is read: the two rows the first read brings are where the stream starts.
        panel.AutoScroll = false;
        await panel.LoadStructuredAsync();
        Assert.Equal(0, panel.SincePause);
        Assert.EndsWith("2 total · +0 since pause", panel.StatusLineCount, StringComparison.Ordinal);

        LogsInspectorFieldsTests.Append(path, add =>
        {
            add("v-3", 3, "guardrail.evaluation", "guardrail.evaluated", "HIGH", "act", "three", null, null, null);
            add("v-4", 4, "asset.scan", "scan.completed", "HIGH", "act", "four", null, null, null);
        });
        await panel.LoadStructuredAsync();
        Assert.Equal(2, panel.SincePause);
        Assert.EndsWith("4 total · +2 since pause", panel.StatusLineCount, StringComparison.Ordinal);

        // The same rows again are nothing new.
        await panel.LoadStructuredAsync();
        Assert.Equal(2, panel.SincePause);

        panel.AutoScroll = true;
        panel.AutoScroll = false;
        Assert.Equal(0, panel.SincePause);
    }

    // ---- E and W ----

    [Fact]
    public void E_and_W_toggle_their_preset_against_all()
    {
        var panel = ActivePanel();
        Assert.Equal(LogPresets.NoNoise, panel.SelectedPreset);

        panel.ToggleErrorsCommand.Execute(null);
        Assert.Equal(LogPresets.Errors, panel.SelectedPreset);

        // The TUI's e goes from errors back to everything, not to the preset before it.
        panel.ToggleErrorsCommand.Execute(null);
        Assert.Equal(LogPresets.All, panel.SelectedPreset);

        panel.ToggleWarningsCommand.Execute(null);
        Assert.Equal(LogPresets.WarningsPlus, panel.SelectedPreset);

        panel.ToggleErrorsCommand.Execute(null);
        Assert.Equal(LogPresets.Errors, panel.SelectedPreset);

        panel.ToggleWarningsCommand.Execute(null);
        Assert.Equal(LogPresets.WarningsPlus, panel.SelectedPreset);

        panel.ToggleWarningsCommand.Execute(null);
        Assert.Equal(LogPresets.All, panel.SelectedPreset);
    }

    [Fact]
    public void The_list_follows_the_preset_the_key_chose()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", new[]
        {
            LogLine.Parse("[api] plain information", 1),
            LogLine.Parse("[api] a warn about a thing", 2),
            LogLine.Parse("[api] panic: nil pointer", 3),
        });

        panel.ToggleErrorsCommand.Execute(null);
        Assert.Equal(new[] { "panic: nil pointer" }, panel.DisplayedLines.Select(l => l.Message).ToArray());

        panel.ToggleWarningsCommand.Execute(null);
        Assert.Equal(new[] { "a warn about a thing", "panic: nil pointer" }, panel.DisplayedLines.Select(l => l.Message).ToArray());

        panel.ToggleWarningsCommand.Execute(null);
        Assert.Equal(3, panel.DisplayedLines.Count);
    }

    [Fact]
    public void On_events_the_key_pauses_the_actionable_view_as_choosing_the_preset_by_hand_does()
    {
        var panel = ActivePanel();
        panel.ActiveSource = "Events";
        Assert.Null(panel.ActionableSuspendedBy);

        panel.ToggleErrorsCommand.Execute(null);

        Assert.Equal("the errors preset is chosen", panel.ActionableSuspendedBy);
    }

    // ---- Home and End ----

    [Fact]
    public void Home_selects_the_first_row_and_pauses_and_End_the_newest_and_follows_again()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines(0, 10));

        var first = panel.JumpToStart();

        Assert.Same(panel.DisplayedLines[0], first);
        Assert.Same(first, panel.SelectedEntry);
        Assert.False(panel.AutoScroll);
        Assert.Equal("paused", panel.LiveStateText);
        Assert.EndsWith("· +0 since pause", panel.StatusLineCount, StringComparison.Ordinal);

        var last = panel.JumpToEnd();

        Assert.Same(panel.DisplayedLines[^1], last);
        Assert.Same(last, panel.SelectedEntry);
        Assert.True(panel.AutoScroll);
        Assert.Equal("live", panel.LiveStateText);
        Assert.DoesNotContain("since pause", panel.StatusLineCount, StringComparison.Ordinal);
    }

    [Fact]
    public void On_an_empty_list_Home_has_nothing_to_go_to_and_End_still_follows_again()
    {
        var panel = ActivePanel();

        Assert.Null(panel.JumpToStart());
        Assert.True(panel.AutoScroll, "Home does not pause a list it cannot move in");

        panel.AutoScroll = false;
        Assert.Null(panel.JumpToEnd());
        Assert.True(panel.AutoScroll);
        Assert.Null(panel.SelectedEntry);
    }

    // ---- The map they must not collide with ----

    [Fact]
    public void The_keys_are_bare_and_collide_with_no_chord_of_the_shell_or_a_panel()
    {
        var keys = new[] { ShellShortcuts.LogsErrorsText, ShellShortcuts.LogsWarningsText, ShellShortcuts.LogsFirstRowText, ShellShortcuts.LogsNewestRowText };
        Assert.Equal(new[] { "E", "W", "Home", "End" }, keys);

        // No shell chord is a bare key, and no panel number is.
        Assert.Empty(keys.Intersect(ShellShortcuts.ShellChords, StringComparer.OrdinalIgnoreCase));
        var panelChords = Enumerable.Range(0, ShellShortcuts.NumberedPanels).Select(i => ShellShortcuts.PanelChordText(i)!).ToArray();
        Assert.Empty(keys.Intersect(panelChords, StringComparer.OrdinalIgnoreCase));
        Assert.NotEqual(ShellShortcuts.AuditExportText, ShellShortcuts.LogsErrorsText);

        foreach (var key in new[] { Key.E, Key.W, Key.Home, Key.End })
        {
            Assert.Null(ShellShortcuts.ActionFor(key, ModifierKeys.None));
            Assert.Null(ShellShortcuts.PanelIndexFor(key, ModifierKeys.None));
            Assert.False(ShellShortcuts.IsToggleThemeChord(key, ModifierKeys.None));
            Assert.False(ShellShortcuts.IsCycleConnectorChord(key, ModifierKeys.None));
            Assert.False(ShellShortcuts.IsSettingsChord(key, ModifierKeys.None));
        }

        // Ctrl+E is Audit's export and Ctrl+Shift+E the shell's: the panel's E answers neither, and the shell's answer is unchanged.
        Assert.Equal(ShellChordAction.ExportOutput, ShellShortcuts.ActionFor(Key.E, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Windows)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]
    public void Only_the_bare_key_is_the_panels(ModifierKeys modifiers)
    {
        Assert.True(ShellShortcuts.IsLogsErrorsKey(Key.E, ModifierKeys.None));
        Assert.True(ShellShortcuts.IsLogsWarningsKey(Key.W, ModifierKeys.None));
        Assert.True(ShellShortcuts.IsLogsFirstRowKey(Key.Home, ModifierKeys.None));
        Assert.True(ShellShortcuts.IsLogsNewestRowKey(Key.End, ModifierKeys.None));

        Assert.False(ShellShortcuts.IsLogsErrorsKey(Key.E, modifiers));
        Assert.False(ShellShortcuts.IsLogsWarningsKey(Key.W, modifiers));
        Assert.False(ShellShortcuts.IsLogsFirstRowKey(Key.Home, modifiers));
        Assert.False(ShellShortcuts.IsLogsNewestRowKey(Key.End, modifiers));
    }

    [Fact]
    public void The_other_keys_are_not_the_panels()
    {
        Assert.False(ShellShortcuts.IsLogsErrorsKey(Key.W, ModifierKeys.None));
        Assert.False(ShellShortcuts.IsLogsWarningsKey(Key.E, ModifierKeys.None));
        Assert.False(ShellShortcuts.IsLogsFirstRowKey(Key.End, ModifierKeys.None));
        Assert.False(ShellShortcuts.IsLogsNewestRowKey(Key.Home, ModifierKeys.None));
    }

    [Fact]
    public void The_overlay_lists_each_once_beside_the_audit_export_and_no_row_repeats()
    {
        var model = ShortcutCatalog.Build(new PanelCatalog(_services));
        var rows = new[] { model.Panels }.Concat(model.Others).SelectMany(s => s.Rows).ToList();

        foreach (var key in new[] { "E", "W", "Home", "End" })
        {
            var row = Assert.Single(rows, r => r.Keys == key);
            Assert.StartsWith("Logs:", row.Description, StringComparison.Ordinal);
        }

        Assert.Contains(rows, r => r.Keys == ShellShortcuts.AuditExportText && r.Description.StartsWith("Audit", StringComparison.Ordinal));
        Assert.Equal(rows.Count, rows.Select(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
