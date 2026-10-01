using System.Collections.Specialized;
using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// The Logs panel's streams, presets, filters and collapse: the file streams through <c>AcceptLines</c> (the UI-thread half of the tailer
/// callback), the database streams against a synthetic audit.db read by the real reader. No dispatcher, no window.
/// </summary>
public sealed class LogsStreamsTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public LogsStreamsTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static IReadOnlyList<LogLine> Lines(params string[] texts) =>
        texts.Select((t, i) => LogLine.Parse(t, i)).ToArray();

    private LogsPanelViewModel ActivePanel()
    {
        var panel = new LogsPanelViewModel(_services);
        panel.SetActive(true);
        return panel;
    }

    private string CreateDatabase(Action<Action<string, int, string, string, string, string?, string?, string?>> seed)
    {
        var path = _temp.File("stream-audit.db");
        AuditTestDatabase.Create(path, rows: 0);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            seed((id, minute, bucket, eventName, severity, details, payload, connector) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
                    VALUES ($id, $timestamp, 'act', '', 'gateway', $details, $severity, $bucket, $eventName, $connector, 'sidecar', 'logs', $payload)
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$timestamp", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
                command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
                command.Parameters.AddWithValue("$severity", severity);
                command.Parameters.AddWithValue("$bucket", bucket);
                command.Parameters.AddWithValue("$eventName", eventName);
                command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
                command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
                _ = command.ExecuteNonQuery();
            });
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private string SeededDatabase() => CreateDatabase(add =>
    {
        add("v1", 1, "guardrail.evaluation", "guardrail.evaluated", "INFO", "connector=claudecode result=ok action=allow hook", "{\"defenseclaw.guardrail.decision\":\"allow\"}", "claudecode");
        add("v2", 2, "enforcement.action", "enforcement.applied", "HIGH", "blocked a thing token=SECRET-VALUE-1", null, "codex");
        add("t1", 3, "telemetry.ingest", "span.received", "INFO", "span", null, "claudecode");
        add("s1", 4, "asset.scan", "scan.completed", "MEDIUM", "scan done", "{\"defenseclaw.scan.id\":\"abc\"}", "claudecode");
    });

    // ---- Rows: masking, severity, tone ----

    [Fact]
    public void A_file_line_is_masked_in_its_raw_text_and_its_message()
    {
        var entry = new LogEntry(LogLine.Parse("[api] calling upstream token=LEAK-1 Authorization: Bearer LEAK-2", 1));

        Assert.DoesNotContain("LEAK", entry.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAK", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAK", entry.DisplayMessage, StringComparison.Ordinal);
        Assert.Contains("[redacted]", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[api] all is well", AuditSeverity.Info, "")]
    [InlineData("[api] request failed: error 500", AuditSeverity.High, "High")]
    [InlineData("[api] upstream returned HTTP 503", AuditSeverity.High, "High")]
    [InlineData("[api] warn: slow reply", AuditSeverity.Medium, "Medium")]
    [InlineData("[api] FATAL: out of memory", AuditSeverity.Critical, "Critical")]
    public void A_file_lines_severity_comes_from_its_words_and_sets_its_tone_bar(string raw, AuditSeverity expected, string tone)
    {
        var entry = new LogEntry(LogLine.Parse(raw, 1));

        Assert.Equal(expected, entry.Severity);
        Assert.Equal(tone, entry.Tone);
    }

    [Fact]
    public void A_label_is_the_type_and_the_action_in_brackets()
    {
        Assert.Equal("[api]", new LogEntry(LogLine.Parse("[api] hello", 1)).Label);
        var attributed = new LogEntry(LogLine.Parse("[sidecar:claudecode] hi", 1));
        Assert.Equal("[sidecar]", attributed.Label);
        Assert.Equal("claudecode", attributed.Connector);
        Assert.Equal("[api:completed]", new LogEntry(LogLine.Parse("[api] scan completed", 1)).Label);
        Assert.Equal("[api:allow]", new LogEntry(LogLine.Parse("[api] x action=allow", 1)).Label);
        Assert.Equal("[watchdog]", new LogEntry(LogLine.Parse("no brackets here", 1), "watchdog").Label);
    }

    [Fact]
    public void The_display_message_drops_what_differs_on_every_repeat()
    {
        var a = new LogEntry(LogLine.Parse("[api] done call_id=aaa111 len=12 body_bytes=77", 1));
        var b = new LogEntry(LogLine.Parse("[api] done call_id=bbb222 len=99 body_bytes=1", 2));

        Assert.Equal("done", a.DisplayMessage);
        Assert.True(a.Repeats(b));
    }

    // ---- Presets and pickers ----

    [Fact]
    public void Every_preset_in_the_menu_has_a_rule_and_all_passes_everything()
    {
        Assert.Equal(new[] { "all", "no-noise", "important", "errors", "warnings+", "scan", "drift", "guardrail", "hooks" }, LogPresets.Names);

        var anything = new LogEntry(LogLine.Parse("[api] anything", 1));
        Assert.True(LogPresets.Matches("all", anything));
    }

    [Theory]
    [InlineData("no-noise", "[ws] event tick seq=44", false)]
    [InlineData("no-noise", "[ws] payload_len=20 ok", false)]
    [InlineData("no-noise", "[api] a real line", true)]
    [InlineData("important", "[api] connection refused: error", true)]
    [InlineData("important", "[api] chatter", false)]
    [InlineData("errors", "[api] error: bad", true)]
    [InlineData("errors", "[api] warn: slow", false)]
    [InlineData("warnings+", "[api] warn: slow", true)]
    [InlineData("warnings+", "[api] all fine", false)]
    [InlineData("scan", "[api] starting a scan", true)]
    [InlineData("scan", "[api] nothing", false)]
    [InlineData("drift", "[api] drift detected", true)]
    [InlineData("guardrail", "[api] judge said no", true)]
    [InlineData("guardrail", "[api] verdict reached", true)]
    [InlineData("hooks", "[api] hook fired", true)]
    [InlineData("hooks", "[api] nothing", false)]
    public void A_preset_keeps_what_its_rule_says(string preset, string raw, bool kept) =>
        Assert.Equal(kept, LogPresets.Matches(preset, new LogEntry(LogLine.Parse(raw, 1))));

    [Fact]
    public void The_event_picker_keeps_scan_apart_from_scan_finding()
    {
        var panel = ActivePanel();
        var scan = new LogEntry(LogLine.Parse("[scan] x", 1));
        var finding = new LogEntry(LogLine.Parse("[scan_finding] x", 2));

        Assert.True(LogPresets.MatchesEvent("scan", scan));
        Assert.False(LogPresets.MatchesEvent("scan", finding));
        Assert.True(LogPresets.MatchesEvent("scan_finding", finding));
        Assert.True(LogPresets.MatchesEvent("all", finding));
        Assert.NotNull(panel);
    }

    [Fact]
    public void The_panel_starts_on_no_noise_and_the_pickers_narrow_the_list()
    {
        var panel = ActivePanel();
        Assert.Equal("no-noise", panel.SelectedPreset);

        panel.AcceptLines("Gateway", Lines(
            "[ws] event tick seq=1",
            "[api] one error happened: boom",
            "[api] a warn about a thing",
            "[api] block action=block recorded",
            "[api] plain information"));

        Assert.Equal(4, panel.DisplayedLines.Count);
        Assert.Equal("4 shown · 4 matching · 5 total", panel.StatusLineCount);

        panel.SelectedPreset = "errors";
        Assert.Equal(new[] { "one error happened: boom" }, panel.DisplayedLines.Select(l => l.Message).ToArray());

        panel.SelectedPreset = "all";
        panel.SelectedSeverity = "≥ Medium";
        Assert.Equal(2, panel.DisplayedLines.Count);

        panel.SelectedSeverity = LogPresets.AnySeverity;
        panel.SelectedAction = "block";
        Assert.Equal(new[] { "block action=block recorded" }, panel.DisplayedLines.Select(l => l.Message).ToArray());
        Assert.Equal("1 shown · 1 matching · 5 total", panel.StatusLineCount);

        panel.SelectedAction = "all";
        panel.SelectedEvent = "ws";
        _ = Assert.Single(panel.DisplayedLines);
    }

    // ---- Collapse ----

    [Fact]
    public void Adjacent_identical_lines_become_one_row_that_says_how_many()
    {
        var panel = ActivePanel();

        panel.AcceptLines("Gateway", Lines("[net] retrying in 15s", "[net] retrying in 15s", "[net] retrying in 15s", "[net] connected"));

        Assert.Equal(2, panel.DisplayedLines.Count);
        Assert.Equal(3, panel.DisplayedLines[0].Count);
        Assert.Equal("Repeated 3 times", panel.DisplayedLines[0].RepeatText);
        Assert.True(panel.DisplayedLines[0].IsRepeated);
        Assert.False(panel.DisplayedLines[1].IsRepeated);
        Assert.Equal("2 shown · 4 matching · 4 total", panel.StatusLineCount);
    }

    [Fact]
    public void A_repeat_arriving_in_a_later_batch_folds_into_the_row_above_without_adding_one()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines("[net] retrying"));
        var seen = new List<NotifyCollectionChangedAction>();
        panel.DisplayedLines.CollectionChanged += (_, e) => seen.Add(e.Action);

        panel.AcceptLines("Gateway", Lines("[net] retrying", "[net] retrying"));

        Assert.Single(panel.DisplayedLines);
        Assert.Equal(3, panel.DisplayedLines[0].Count);
        Assert.Empty(seen);
    }

    [Fact]
    public void Projecting_again_does_not_count_a_repeat_twice()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines("[net] retrying", "[net] retrying", "[net] other"));

        panel.SelectedPreset = "all";
        panel.SelectedPreset = "no-noise";
        panel.RefreshCommand.Execute(null);

        Assert.Equal(2, panel.DisplayedLines.Count);
        Assert.Equal(2, panel.DisplayedLines[0].Count);
    }

    [Fact]
    public void A_different_severity_or_action_is_not_a_repeat()
    {
        var panel = ActivePanel();

        panel.AcceptLines("Gateway", Lines("[net] failed", "[net] failed error", "[net] failed"));

        Assert.Equal(3, panel.DisplayedLines.Count);
    }

    [Fact]
    public void Collapse_changes_only_the_list_never_the_buffer_that_counts_totals()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines("[net] same", "[net] same", "[net] same"));

        Assert.Equal(3, panel.BufferedCount);
        Assert.Single(panel.DisplayedLines);
    }

    // ---- Navigation ----

    [Fact]
    public void The_hooks_request_opens_events_on_the_hooks_preset_from_a_clean_slate()
    {
        var panel = ActivePanel();
        panel.FilterText = "leftover";
        panel.SelectedSeverity = "≥ High";
        panel.SelectedAction = "block";
        panel.SelectedEvent = "scan";
        panel.AutoScroll = false;

        panel.Accept(new LogsPreset("hooks"));

        Assert.Equal("Events", panel.ActiveSource);
        Assert.Equal("hooks", panel.SelectedPreset);
        Assert.Equal(string.Empty, panel.FilterText);
        Assert.Equal(LogPresets.AnySeverity, panel.SelectedSeverity);
        Assert.Equal("all", panel.SelectedAction);
        Assert.Equal("all", panel.SelectedEvent);
        Assert.True(panel.AutoScroll);
        Assert.False(panel.IncludeTelemetry);
    }

    [Fact]
    public void Another_named_preset_opens_the_gateway_log_and_an_unknown_one_changes_nothing()
    {
        var panel = ActivePanel();
        panel.ActiveSource = "Verdicts";

        panel.Accept(new LogsPreset("Guardrail"));
        Assert.Equal("Gateway", panel.ActiveSource);
        Assert.Equal("guardrail", panel.SelectedPreset);

        panel.Accept(new LogsPreset("no-such-preset"));
        Assert.Equal("guardrail", panel.SelectedPreset);

        panel.Accept("a string");
        panel.Accept(new AuditPreset("errors"));
        Assert.Equal("guardrail", panel.SelectedPreset);
        Assert.Equal("Gateway", panel.ActiveSource);
    }

    [Fact]
    public void A_request_is_one_reprojection_not_one_per_property()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines("[api] hook one", "[api] other"));
        var resets = 0;
        panel.DisplayedLines.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

        panel.Accept(new LogsPreset("scan"));

        Assert.Equal(1, resets);
    }

    // ---- The database streams ----

    [Fact]
    public async Task Verdicts_are_read_from_the_database_oldest_first_and_masked()
    {
        var panel = ActivePanel();
        panel.StreamReader = new EventStreamReader(SeededDatabase());
        panel.ActiveSource = "Verdicts";
        panel.SelectedPreset = "all";

        await panel.LoadStructuredAsync();

        Assert.Equal(new[] { "v1", "v2", "s1" }, panel.DisplayedLines.Select(l => l.Fields.Single(f => f.Name == "id").Value).ToArray());
        Assert.Equal("3 shown · 3 matching · 3 total", panel.StatusLineCount);
        Assert.All(panel.DisplayedLines, l => Assert.DoesNotContain("SECRET-VALUE-1", l.Raw + l.Message, StringComparison.Ordinal));

        var blocked = panel.DisplayedLines[1];
        Assert.Equal("High", blocked.Tone);
        Assert.Equal("[verdict:act]", blocked.Label);
        Assert.Equal(AuditSeverity.High, blocked.Severity);
        Assert.True(blocked.IsStructured);
        Assert.False(panel.ShowStreamNotice);
    }

    [Fact]
    public async Task Events_hide_telemetry_until_it_is_switched_on_and_the_read_is_redone()
    {
        var panel = ActivePanel();
        var reader = new EventStreamReader(SeededDatabase());
        panel.StreamReader = reader;
        panel.ActiveSource = "Events";
        panel.SelectedPreset = "all";

        await panel.LoadStructuredAsync();
        Assert.Equal(3, panel.DisplayedLines.Count);

        panel.IncludeTelemetry = true;
        await panel.LoadStructuredAsync();
        Assert.Equal(4, panel.DisplayedLines.Count);
        Assert.Contains(panel.DisplayedLines, l => l.Fields.Any(f => f is { Name: "bucket", Value: "telemetry.ingest" }));
    }

    [Fact]
    public async Task The_hooks_preset_finds_the_hook_calls_among_the_events()
    {
        var panel = ActivePanel();
        panel.StreamReader = new EventStreamReader(SeededDatabase());

        panel.Accept(new LogsPreset("hooks"));
        await panel.LoadStructuredAsync();

        var row = Assert.Single(panel.DisplayedLines);
        Assert.Equal("allow", row.Action);
    }

    [Fact]
    public async Task A_poll_that_finds_the_same_rows_changes_nothing_so_the_selection_survives()
    {
        var panel = ActivePanel();
        panel.StreamReader = new EventStreamReader(SeededDatabase());
        panel.ActiveSource = "Verdicts";
        panel.SelectedPreset = "all";
        await panel.LoadStructuredAsync();
        panel.SelectedEntry = panel.DisplayedLines[1];
        var seen = new List<NotifyCollectionChangedAction>();
        panel.DisplayedLines.CollectionChanged += (_, e) => seen.Add(e.Action);

        await panel.LoadStructuredAsync();

        Assert.Empty(seen);
        Assert.NotNull(panel.SelectedEntry);
    }

    [Fact]
    public async Task A_panel_that_is_not_on_screen_reads_nothing()
    {
        var reader = new EventStreamReader(SeededDatabase());
        var panel = new LogsPanelViewModel(_services) { StreamReader = reader };
        panel.ActiveSource = "Verdicts";

        await panel.LoadStructuredAsync();
        panel.SetActive(true);
        panel.SetActive(false);
        await panel.LoadStructuredAsync();

        Assert.Equal(0, reader.ReadCount);
        Assert.Empty(panel.DisplayedLines);
    }

    [Fact]
    public async Task Switching_to_a_file_stream_reads_nothing_more()
    {
        var reader = new EventStreamReader(SeededDatabase());
        var panel = ActivePanel();
        panel.StreamReader = reader;
        panel.ActiveSource = "Gateway";

        await panel.LoadStructuredAsync();

        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task A_missing_database_is_an_empty_state_not_an_error()
    {
        var panel = ActivePanel();
        panel.StreamReader = new EventStreamReader(_temp.File("never-created.db"));
        panel.ActiveSource = "Verdicts";

        await panel.LoadStructuredAsync();

        Assert.True(panel.IsEmpty);
        Assert.False(panel.ShowStreamNotice);
        Assert.Equal("No log lines", panel.EmptyTitle);
        Assert.Contains("does not exist yet", panel.EmptyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_database_from_before_the_canonical_schema_says_the_stream_is_unavailable()
    {
        var path = _temp.File("legacy.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE audit_events (id TEXT, timestamp TEXT, action TEXT, severity TEXT)";
            _ = command.ExecuteNonQuery();
        }

        var panel = ActivePanel();
        panel.StreamReader = new EventStreamReader(path);
        panel.ActiveSource = "Events";

        await panel.LoadStructuredAsync();

        Assert.True(panel.ShowStreamNotice);
        Assert.Contains("predates", panel.StreamNoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_is_for_the_log_files_only()
    {
        var panel = ActivePanel();
        Assert.True(panel.ClearCommand.CanExecute(null));

        panel.ActiveSource = "Verdicts";
        Assert.False(panel.ClearCommand.CanExecute(null));

        panel.ActiveSource = "Watchdog";
        Assert.True(panel.ClearCommand.CanExecute(null));
    }

    [Fact]
    public void Auto_scroll_is_on_by_default_and_the_text_follows_it()
    {
        var panel = ActivePanel();

        Assert.True(panel.AutoScroll);
        Assert.Equal("live", panel.LiveStateText);

        panel.AutoScroll = false;
        Assert.Equal("paused", panel.LiveStateText);
    }

    [Fact]
    public void An_empty_filtered_list_says_what_to_change()
    {
        var panel = ActivePanel();
        panel.AcceptLines("Gateway", Lines("[api] plain"));

        panel.SelectedPreset = "errors";

        Assert.True(panel.IsEmpty);
        Assert.Equal("No log lines", panel.EmptyTitle);
        Assert.Contains("Nothing matches", panel.EmptyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Copying_a_selection_never_hands_out_an_unmasked_credential()
    {
        var entry = new LogEntry(LogLine.Parse("[api] password=LEAK-PW", 1));

        Assert.DoesNotContain("LEAK-PW", entry.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAK-PW", entry.ToString(), StringComparison.Ordinal);
    }
}
