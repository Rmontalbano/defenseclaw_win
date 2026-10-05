using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// The Audit panel's Mac-parity extras at the view-model: the preset strip and its deep link, "Same run", the inspector's related
/// events and run findings, and Export. Synthetic rows only; the database is the real schema in a scratch directory.
/// </summary>
public sealed class AuditPresetsCorrelationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;
    private string _dbPath = string.Empty;

    public void Dispose()
    {
        _services?.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static string Stamp(int secondsAgo) =>
        DateTimeOffset.UtcNow.AddSeconds(-secondsAgo).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";

    /// <summary>A database of <paramref name="filler"/> plain rows (ids evt-000000...) plus the rows <paramref name="extra"/> adds.</summary>
    /// <summary>Adds one event: id, seconds ago, action, severity, target, details, run id.</summary>
    private delegate string AddEvent(string id, int secondsAgo, string action, string severity, string target, string? details, string? run);

    private AuditPanelViewModel PanelOver(int filler, Action<AddEvent>? extra = null)
    {
        _dbPath = Path.Combine(_temp.Path, "audit.db");
        AuditTestDatabase.Create(_dbPath, filler);

        if (extra is not null)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, connector, event_name, run_id)
                VALUES ($id, $ts, $action, $target, 'audit_logger', $details, $severity, 'guardrail.evaluation', 'claudecode', 'evt', $run)
                """;
            extra((id, secondsAgo, action, severity, target, details, run) =>
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$ts", Stamp(secondsAgo));
                insert.Parameters.AddWithValue("$action", action);
                insert.Parameters.AddWithValue("$severity", severity);
                insert.Parameters.AddWithValue("$target", target);
                insert.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
                insert.Parameters.AddWithValue("$run", (object?)run ?? DBNull.Value);
                _ = insert.ExecuteNonQuery();
                return id;
            });
        }

        SqliteConnection.ClearAllPools();
        _services = TestServices.Create(_temp);
        return new AuditPanelViewModel(_services);
    }

    private void Exec(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database; values go in as parameters
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        _ = command.ExecuteNonQuery();
    }

    private static async Task Settled(AuditPanelViewModel panel)
    {
        await panel.LastLoad;
        await panel.LoadGate.WaitAsync();
        _ = panel.LoadGate.Release();
    }

    private AuditPanelViewModel MixedPanel() => PanelOver(
        20,
        add =>
        {
            // id, seconds ago, action, severity, target, details, run
            _ = add("blk-1", 1, "hook_decision", "HIGH", "rm -rf", "decision=BLOCK reason=policy", "run-A");
            _ = add("blk-2", 2, "quarantine-skill", "INFO", "skill-x", null, "run-A");
            _ = add("scan-1", 3, "scan", "LOW", "skill-x", "scanner=skill-scan finding_count=2", "run-A");
            _ = add("cred-1", 4, "key-rotation", "CRITICAL", "api", "token=<redacted len=40 sha=abc12345>", "run-B");
            _ = add("risk-1", 5, "tool_invocation", "CRITICAL", "curl", "x", null);
        });

    // ------------------------------------------------------------------ presets

    [Fact]
    public async Task Each_preset_narrows_the_list_the_way_the_mac_does()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        Assert.Equal(25, panel.Rows.Count);
        Assert.Contains("of 25 matching events", panel.ResultSummary, StringComparison.Ordinal);

        async Task<string[]> Ids(string preset)
        {
            panel.ActivePreset = preset;
            await Settled(panel);
            return panel.Rows.Select(r => r.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray();
        }

        Assert.Equal(new[] { "blk-1", "cred-1", "risk-1" }, await Ids("risk"));
        Assert.Equal(new[] { "blk-1", "blk-2" }, await Ids("blocks"));
        Assert.Equal(new[] { "scan-1" }, await Ids("scans"));
        Assert.Equal(new[] { "cred-1" }, await Ids("credentials"));
        Assert.Equal(25, (await Ids("all")).Length);
    }

    [Fact]
    public async Task A_preset_combines_with_the_filters_and_a_stricter_minimum_severity_wins_over_risk()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();

        panel.ActivePreset = "risk";
        panel.SelectedSeverity = SeverityOption.All.First(s => s.Value == DefenseClaw.Core.Audit.AuditSeverity.Critical);
        await Settled(panel);
        Assert.Equal(new[] { "cred-1", "risk-1" }, panel.Rows.Select(r => r.Id).OrderBy(i => i, StringComparer.Ordinal));

        panel.ActivePreset = "blocks";
        panel.SelectedSeverity = SeverityOption.Any;
        panel.SearchText = "policy";
        await Settled(panel);
        Assert.Equal(new[] { "blk-1" }, panel.Rows.Select(r => r.Id));

        // The search box and the preset are not among the filters behind the expander.
        Assert.Equal(0, panel.ActiveFilterCount);
    }

    [Fact]
    public async Task A_preset_view_does_not_count_the_window_and_says_how_many_it_loaded()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        var countsBefore = _services!.Audit.CountQueryCount;

        panel.ActivePreset = "blocks";
        await Settled(panel);

        Assert.Equal(countsBefore, _services.Audit.CountQueryCount);
        Assert.Equal("2 matching events loaded · Last 24 hours", panel.ResultSummary);
    }

    [Fact]
    public async Task The_overview_blocks_link_selects_the_blocks_preset_and_clears_search_and_run()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        panel.SearchText = "something";
        panel.RunFilter = "run-A";
        await Settled(panel);
        var pagesBefore = _services!.Audit.PageQueryCount;

        panel.Accept(new AuditPreset("Blocks"));
        await Settled(panel);

        Assert.Equal("blocks", panel.ActivePreset);
        Assert.Equal(string.Empty, panel.SearchText);
        Assert.Equal(string.Empty, panel.RunFilter);
        Assert.Equal(1, _services.Audit.PageQueryCount - pagesBefore);
        Assert.Equal(new[] { "blk-1", "blk-2" }, panel.Rows.Select(r => r.Id).OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_unknown_preset_or_another_payload_is_ignored()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        var pagesBefore = _services!.Audit.PageQueryCount;

        panel.Accept(new AuditPreset("nonsense"));
        panel.Accept(new LogsPreset("blocks"));
        panel.Accept("blocks");
        await Settled(panel);

        Assert.Equal("all", panel.ActivePreset);
        Assert.Equal(0, _services.Audit.PageQueryCount - pagesBefore);
    }

    [Fact]
    public async Task Reset_filters_puts_the_preset_and_the_run_filter_back_too()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        panel.ActivePreset = "scans";
        panel.RunFilter = "run-A";
        panel.SelectedBucket = "guardrail.evaluation";
        await Settled(panel);
        Assert.Equal(1, panel.ActiveFilterCount);
        Assert.Equal("Filters (1)", panel.FiltersHeader);

        panel.ResetFiltersCommand.Execute(null);
        await Settled(panel);

        Assert.Equal("all", panel.ActivePreset);
        Assert.Equal(string.Empty, panel.RunFilter);
        Assert.Equal(0, panel.ActiveFilterCount);
        Assert.Equal("Filters", panel.FiltersHeader);
        Assert.Equal(25, panel.Rows.Count);
    }

    // ------------------------------------------------------------------ same run / same target

    [Fact]
    public async Task Same_run_shows_only_that_runs_events_in_every_time_range_and_the_chip_clears_it()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        var row = panel.Rows.First(r => r.Id == "blk-1");
        Assert.True(row.HasRun);

        panel.ShowSameRun(row);
        await Settled(panel);

        Assert.Equal("run-A", panel.RunFilter);
        Assert.True(panel.HasRunFilter);
        Assert.Equal("Run run-A", panel.RunFilterText);
        Assert.Same(TimeRangeOption.AllTime, panel.SelectedRange);
        Assert.Equal(new[] { "blk-1", "blk-2", "scan-1" }, panel.Rows.Select(r => r.Id).OrderBy(i => i, StringComparer.Ordinal));
        Assert.Contains("of 3 matching events", panel.ResultSummary, StringComparison.Ordinal);

        panel.ClearRunFilterCommand.Execute(null);
        await Settled(panel);
        Assert.False(panel.HasRunFilter);
        Assert.Equal(25, panel.Rows.Count);
    }

    [Fact]
    public async Task Same_run_on_a_row_without_a_run_changes_nothing_and_same_target_searches_for_the_target()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        var noRun = panel.Rows.First(r => r.Id == "risk-1");
        Assert.False(noRun.HasRun);
        var pagesBefore = _services!.Audit.PageQueryCount;

        panel.ShowSameRun(noRun);
        await Settled(panel);
        Assert.Equal(0, _services.Audit.PageQueryCount - pagesBefore);

        panel.FilterBySameTargetCommand.Execute(panel.Rows.First(r => r.Id == "scan-1"));
        await Settled(panel);
        Assert.Equal("skill-x", panel.SearchText);
        Assert.Equal(new[] { "blk-2", "scan-1" }, panel.Rows.Select(r => r.Id).OrderBy(i => i, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ inspector correlation

    private const string FindingsJson = """{"findings":[{"title":"Exfil","severity":"HIGH","location":"SKILL.md:3","scanner":"skill-scan"},{"title":"Odd","severity":"LOW"}]}""";

    [Fact]
    public async Task Selecting_a_row_lists_its_runs_other_events_and_the_findings_of_the_run()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        Exec(
            "INSERT INTO scan_results (id, scanner, target, timestamp, raw_json, run_id) VALUES ('s1', 'skill-scan', 'skill-x', $ts, $raw, 'run-A')",
            ("$ts", Stamp(30)),
            ("$raw", FindingsJson));

        panel.SelectedRow = panel.Rows.First(r => r.Id == "blk-1");
        await panel.LastCorrelation;

        Assert.Equal(new[] { "blk-2", "scan-1" }, panel.RelatedEvents.Select(r => r.Id).OrderBy(i => i, StringComparer.Ordinal));
        Assert.DoesNotContain(panel.RelatedEvents, r => r.Id == "blk-1");
        Assert.Equal("Other events of the same run, newest first.", panel.RelatedNote);
        Assert.Equal(new[] { "Exfil", "Odd" }, panel.RunFindings.Select(f => f.Title));
        Assert.Equal("High", panel.RunFindings[0].SeverityKey);
        Assert.Equal("skill-scan · SKILL.md:3", panel.RunFindings[0].Origin);
        Assert.False(panel.IsCorrelating);
    }

    [Fact]
    public async Task Related_events_are_capped_at_eight_and_a_run_without_scans_says_so()
    {
        var panel = PanelOver(0, add =>
        {
            for (var i = 1; i <= 12; i++)
            {
                _ = add("r" + i.ToString("D2", CultureInfo.InvariantCulture), i, "tool_invocation", "INFO", "t", null, "run-big");
            }
        });
        await panel.InitializeAsync();

        panel.SelectedRow = panel.Rows.First(r => r.Id == "r06");
        await panel.LastCorrelation;

        Assert.Equal(8, panel.RelatedEvents.Count);
        Assert.Empty(panel.RunFindings);
        Assert.Equal("No scan findings were recorded for this run.", panel.FindingsNote);
    }

    [Fact]
    public async Task A_row_with_a_target_but_no_run_correlates_by_target_and_one_with_neither_has_nothing_to_show()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();

        panel.SelectedRow = panel.Rows.First(r => r.Id == "risk-1");
        await panel.LastCorrelation;
        Assert.Empty(panel.RelatedEvents);
        Assert.Contains("No other events about this target", panel.RelatedNote, StringComparison.Ordinal);
        Assert.Equal(string.Empty, panel.FindingsNote);

        panel.SelectedRow = panel.Rows.First(r => r.Id == "evt-000003");
        await panel.LastCorrelation;
        Assert.Contains("no run id or target", panel.RelatedNote, StringComparison.Ordinal);

        panel.SelectedRow = null;
        Assert.Empty(panel.RelatedEvents);
        Assert.Equal(string.Empty, panel.RelatedNote);
    }

    [Fact]
    public async Task Changing_the_selection_drops_what_the_old_one_listed()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        panel.SelectedRow = panel.Rows.First(r => r.Id == "blk-1");
        await panel.LastCorrelation;
        Assert.NotEmpty(panel.RelatedEvents);

        panel.SelectedRow = panel.Rows.First(r => r.Id == "cred-1");
        Assert.Empty(panel.RelatedEvents);
        await panel.LastCorrelation;

        Assert.Empty(panel.RelatedEvents);
        Assert.Contains("No other events in this run", panel.RelatedNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_detail_text_is_parsed_into_pairs_with_redactions_read_as_length_and_digest()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();

        var cred = panel.Rows.First(r => r.Id == "cred-1");
        Assert.True(cred.HasDetailPairs);
        Assert.Equal(("Token", "redacted · 40 bytes · sha:abc12345"), (cred.DetailPairs[0].Label, cred.DetailPairs[0].Value));

        // Prose gives only its known metadata keys; plain text with none gives nothing.
        Assert.Equal("Scanner", panel.Rows.First(r => r.Id == "scan-1").DetailPairs[0].Label);
        Assert.False(panel.Rows.First(r => r.Id == "risk-1").HasDetailPairs);
    }

    // ------------------------------------------------------------------ shortcut

    [Fact]
    public void The_shortcut_list_names_ctrl_e_for_the_audit_export_and_no_other_chord_uses_it()
    {
        _ = MixedPanel();
        var model = ShortcutCatalog.Build(new PanelCatalog(_services!));

        var rows = new[] { model.Panels }.Concat(model.Others).SelectMany(s => s.Rows).ToList();
        var export = Assert.Single(rows, r => r.Keys == "Ctrl+E");
        Assert.Contains("Audit", export.Description, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ export

    private string ExportPath(string name) => Path.Combine(_temp.Path, name);

    [Fact]
    public async Task Export_writes_the_current_filtered_set_as_json_with_the_agreed_fields()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        panel.ActivePreset = "blocks";
        await Settled(panel);
        var path = ExportPath("out.json");
        panel.ExportPathPicker = () => path;

        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { "blk-1", "blk-2" }, document.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).OrderBy(i => i, StringComparer.Ordinal));
        var first = document.RootElement.EnumerateArray().First(e => e.GetProperty("id").GetString() == "blk-1");
        Assert.Equal("run-A", first.GetProperty("run_id").GetString());
        Assert.Equal("guardrail.evaluation", first.GetProperty("bucket").GetString());
        Assert.Equal("claudecode", first.GetProperty("connector").GetString());
        Assert.Equal("evt", first.GetProperty("event_name").GetString());
        Assert.Contains("Exported 2 matching events", panel.ExportNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_to_a_csv_name_writes_csv_with_formula_cells_defused()
    {
        var panel = PanelOver(0, add => add("x1", 1, "hook_decision", "HIGH", "=HYPERLINK(\"http://evil\")", "-1+1", "run-Z"));
        await panel.InitializeAsync();
        var path = ExportPath("out.csv");
        panel.ExportPathPicker = () => path;

        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        var text = await File.ReadAllTextAsync(path);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("id,timestamp,action,target,actor,details,severity,run_id,bucket,event_name,connector", lines[0]);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", lines[1], StringComparison.Ordinal);
        Assert.Contains(",'-1+1,", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_is_capped_and_says_how_many_matched()
    {
        var panel = PanelOver(AuditPanelViewModel.ExportCap + 40);
        await panel.InitializeAsync();
        var path = ExportPath("big.json");
        panel.ExportPathPicker = () => path;

        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(AuditPanelViewModel.ExportCap, document.RootElement.GetArrayLength());
        var cap = AuditPanelViewModel.ExportCap.ToString("N0", CultureInfo.CurrentCulture);
        var all = (AuditPanelViewModel.ExportCap + 40).ToString("N0", CultureInfo.CurrentCulture);
        Assert.Contains($"the newest {cap} of {all} matching events", panel.ExportNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_exports_nothing()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        panel.ExportPathPicker = () => null;

        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        Assert.Equal(string.Empty, panel.ExportNote);
    }

    [Fact]
    public async Task An_unwritable_target_is_a_note_not_a_crash()
    {
        var panel = MixedPanel();
        await panel.InitializeAsync();
        panel.ExportPathPicker = () => Path.Combine(_temp.Path, "no-such-folder", "out.json");

        panel.ExportCommand.Execute(null);
        await panel.LastExport;

        Assert.StartsWith("Could not export", panel.ExportNote, StringComparison.Ordinal);
    }
}
