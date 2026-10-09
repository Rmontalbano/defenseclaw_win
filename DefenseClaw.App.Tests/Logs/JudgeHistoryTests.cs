using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.JudgeHistory;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// The judge response history window (CUST-260): what the view-model shows from a synthetic forensic store plus a legacy audit table,
/// that every body is masked and bounded before a view can bind to it, how a missing or unreadable database reads, that the Logs
/// toolbar button only opens it, and a rendering of the real view for a human to look at (<c>DC_RENDER_DIR</c>).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class JudgeHistoryTests : IDisposable
{
    private const string Ddl =
        "CREATE TABLE judge_responses (id TEXT, timestamp DATETIME, kind TEXT, direction TEXT, model TEXT, action TEXT, severity TEXT, latency_ms INTEGER, " +
        "parse_error TEXT, raw_response TEXT, request_id TEXT, trace_id TEXT, run_id TEXT, input_hash TEXT, confidence REAL, fail_closed_applied INTEGER, " +
        "inspected_model TEXT, prompt_template_id TEXT, timestamp_unix_nano INTEGER)";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public JudgeHistoryTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        SqlitePools.Release(_temp.Path);
        _services.Dispose();
        _temp.Dispose();
    }

    private static void Exec(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        // nosemgrep: csharp-sqli -- test helper: the SQL is written by the test and runs on its own temp database
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        _ = command.ExecuteNonQuery();
    }

    private static void Insert(
        string path,
        string id,
        string timestamp,
        string raw,
        string action = "allow",
        string severity = "LOW",
        double? confidence = 0.5,
        int failClosed = 0,
        string? parseError = null)
    {
        Exec(
            path,
            "INSERT INTO judge_responses (id, timestamp, kind, direction, model, action, severity, latency_ms, parse_error, raw_response, request_id, trace_id, run_id, input_hash, confidence, fail_closed_applied, inspected_model, prompt_template_id) " +
            "VALUES ($id, $ts, 'injection', 'prompt', 'judge-model-1', $action, $sev, 412, $pe, $raw, 'req-0001', 'trace-0001', 'run-0001', 'sha256:abc123', $conf, $fc, 'agent-model-x', 'injection-v3')",
            ("$id", id), ("$ts", timestamp), ("$action", action), ("$sev", severity), ("$raw", raw), ("$conf", confidence), ("$fc", failClosed), ("$pe", parseError));
    }

    /// <summary>A forensic store and a legacy table holding synthetic rows: two duplicates by id, one legacy-only, one fail-closed, one with a credential in its body.</summary>
    private JudgeHistoryReader Fixture()
    {
        var bodies = _temp.File("judge_bodies.db");
        var legacy = _temp.File("audit.db");
        Exec(bodies, Ddl);
        Exec(legacy, Ddl);
        Insert(bodies, "j-5", "2026-10-02 09:15:30.25 +0000 UTC", "{\"verdict\":\"block\",\"reason\":\"prompt injection\",\"password\":\"synthetic-" + "not-a-secret\"}", action: "block", severity: "HIGH", confidence: 0.91234);
        Insert(bodies, "j-4", "2026-10-02T09:10:00Z", "{\"verdict\":\"allow\"}", confidence: 0.0, failClosed: 1, parseError: "unexpected end of JSON input");
        Insert(bodies, "j-3", "2026-10-02T08:00:00Z", "authorization: Bearer synthetic-" + "token-value\n{\"verdict\":\"allow\"}");
        Insert(legacy, "j-3", "2026-10-02T08:00:00Z", "legacy copy that must not show");
        Insert(legacy, "j-2", "2026-10-01T23:59:59Z", "{\"verdict\":\"warn\"}", action: "alert", severity: "MEDIUM", confidence: null);
        return new JudgeHistoryReader(bodies, legacy);
    }

    private static JudgeHistoryViewModel Model(AppServices services, Func<JudgeHistoryReader> reader) => new(services, reader);

    [Fact]
    public async Task The_window_lists_the_merged_rows_newest_first_with_the_TUI_fields()
    {
        var vm = Model(_services, Fixture);

        await vm.InitializeAsync();

        Assert.Equal(new[] { "j-5", "j-4", "j-3", "j-2" }, vm.Items.Select(i => i.Id).ToArray());
        Assert.Equal("Judge responses - last 4", vm.Title);
        Assert.False(vm.HasBanner);
        Assert.False(vm.IsEmpty);

        var top = vm.Items[0];
        Assert.Same(top, vm.SelectedItem);
        Assert.Equal(
            new[] { "Timestamp", "Kind", "Direction", "Action", "Severity", "Latency (ms)", "Inspected model", "Judge model", "Request ID", "Trace ID", "Run ID", "Input hash", "Confidence", "Prompt template", "Source" },
            top.Fields.Select(f => f.Label).ToArray());
        Assert.Equal("0.912", top.Fields.Single(f => f.Label == "Confidence").Value);
        Assert.Equal("High", top.Tone);
    }

    [Fact]
    public async Task Fail_closed_shows_only_when_set_and_a_zero_confidence_still_shows()
    {
        var vm = Model(_services, Fixture);
        await vm.InitializeAsync();

        var failClosed = vm.Items.Single(i => i.Id == "j-4");
        Assert.Equal("yes", failClosed.Fields.Single(f => f.Label == "Fail-closed").Value);
        Assert.Equal("0.000", failClosed.Fields.Single(f => f.Label == "Confidence").Value);
        Assert.Equal("unexpected end of JSON input", failClosed.Fields.Single(f => f.Label == "Parse error").Value);

        foreach (var other in vm.Items.Where(i => i.Id != "j-4"))
        {
            Assert.DoesNotContain(other.Fields, f => f.Label == "Fail-closed");
        }

        // No stored confidence at all: no line, rather than "0.000".
        Assert.DoesNotContain(vm.Items.Single(i => i.Id == "j-2").Fields, f => f.Label == "Confidence");
    }

    [Fact]
    public async Task A_body_is_masked_before_it_can_be_shown_and_the_forensic_copy_wins()
    {
        var vm = Model(_services, Fixture);
        await vm.InitializeAsync();

        var all = string.Join("\n", vm.Items.Select(i => i.RawText));
        Assert.DoesNotContain("synthetic-" + "not-a-secret", all, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-" + "token-value", all, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy copy", all, StringComparison.Ordinal);
        Assert.Contains("[redacted]", vm.Items.Single(i => i.Id == "j-5").RawText, StringComparison.Ordinal);
        Assert.Contains("prompt injection", vm.Items.Single(i => i.Id == "j-5").RawText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_longer_than_the_display_bound_is_cut_and_says_so()
    {
        var row = new JudgeResponseRow(
            "big", DateTimeOffset.UnixEpoch, "2026-10-02T00:00:00Z", "k", "d", "a", "LOW", "1", string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, null, string.Empty, false, string.Empty, string.Empty, new string('x', JudgeHistoryReader.RawLimit), true, JudgeHistorySource.JudgeBodies);

        var item = new JudgeHistoryItem(row);

        Assert.True(item.RawText.Length < JudgeHistoryReader.RawLimit);
        Assert.Contains("body cut", item.RawText, StringComparison.Ordinal);
        Assert.Equal("Raw (redacted, shortened)", item.RawNote);
    }

    [Fact]
    public async Task Load_more_adds_a_page_and_stops_when_there_is_no_more()
    {
        var bodies = _temp.File("judge_bodies.db");
        Exec(bodies, Ddl);
        for (var i = 0; i < 25; i++)
        {
            Insert(bodies, $"r{i:00}", $"2026-10-02T09:{i:00}:00Z", "{}");
        }

        var vm = Model(_services, () => new JudgeHistoryReader(bodies, null));
        await vm.InitializeAsync();

        Assert.Equal(20, vm.Items.Count);
        Assert.True(vm.HasMore);
        Assert.True(vm.LoadMoreCommand.CanExecute(null));

        await vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(25, vm.Items.Count);
        Assert.False(vm.HasMore);
        Assert.False(vm.LoadMoreCommand.CanExecute(null));

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(20, vm.Items.Count);
    }

    [Fact]
    public async Task Missing_files_show_guidance_and_not_an_error()
    {
        var vm = Model(_services, () => new JudgeHistoryReader(_temp.File("nope.db"), _temp.File("nope-audit.db")));

        await vm.InitializeAsync();

        Assert.Empty(vm.Items);
        Assert.True(vm.ShowGuidance);
        Assert.False(vm.ShowError);
        Assert.Contains("judge_bodies_path", vm.BannerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_database_shows_an_error_and_does_not_throw()
    {
        var bad = _temp.File("judge_bodies.db");
        await File.WriteAllTextAsync(bad, new string('z', 4096));
        var vm = Model(_services, () => new JudgeHistoryReader(bad, null));

        await vm.InitializeAsync();

        Assert.Empty(vm.Items);
        Assert.True(vm.ShowError);
        Assert.False(vm.ShowGuidance);
        Assert.Equal("Judge responses - error", vm.Title);
    }

    [Fact]
    public async Task An_empty_store_is_the_none_persisted_state()
    {
        var bodies = _temp.File("judge_bodies.db");
        Exec(bodies, Ddl);
        var vm = Model(_services, () => new JudgeHistoryReader(bodies, null));

        await vm.InitializeAsync();

        Assert.True(vm.IsEmpty);
        Assert.Contains("retain_judge_bodies", vm.EmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_reader_follows_the_configured_path_and_never_opens_the_install()
    {
        var elsewhere = _temp.File("custom-bodies.db");
        Exec(elsewhere, Ddl);
        Insert(elsewhere, "c-1", "2026-10-02T09:00:00Z", "{}");
        _ = _temp.WriteFile("config.yaml", "observability:\n  local:\n    judge_bodies_path: " + elsewhere.Replace('\\', '/') + "\n");
        _services.ReloadConfig();

        var vm = new JudgeHistoryViewModel(_services);
        await vm.InitializeAsync();

        Assert.Equal(new[] { "c-1" }, vm.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void The_Logs_button_opens_the_window_through_the_opener_and_shows_on_Verdicts_only()
    {
        var logs = new LogsPanelViewModel(_services);
        var opened = 0;
        logs.JudgeHistoryOpener = () => opened++;

        logs.OpenJudgeHistoryCommand.Execute(null);
        Assert.Equal(1, opened);

        logs.ActiveSource = LogsPanelViewModel.VerdictsSource;
        Assert.True(logs.IsVerdictsSource);
        logs.ActiveSource = LogsPanelViewModel.EventsSource;
        Assert.False(logs.IsVerdictsSource);
        logs.ActiveSource = "Gateway";
        Assert.False(logs.IsVerdictsSource);
    }

    [Fact]
    public async Task The_view_renders_the_list_the_fields_and_the_redacted_body()
    {
        var vm = Model(_services, Fixture);
        await vm.InitializeAsync();

        UiThread.Run(() =>
        {
            var view = new JudgeHistoryView { DataContext = vm };
            using var host = new OffscreenHost(view, 1040, 680);

            host.Relayout();
            RenderTo.Png(host, "judge-history");

            var texts = VisibleTexts(host).ToList();
            Assert.Contains(texts, t => t.Contains("prompt injection", StringComparison.Ordinal));
            Assert.Contains("Latency (ms)", texts);
            Assert.DoesNotContain(texts, t => t.Contains("synthetic-" + "not-a-secret", StringComparison.Ordinal));
            Assert.Equal(4, VisualTree.Descendants<ListBoxItem>(view).Count());
        });
    }

    [Fact]
    public async Task The_view_with_an_error_says_so()
    {
        var bad = _temp.File("judge_bodies.db");
        await File.WriteAllTextAsync(bad, new string('z', 4096));
        var vm = Model(_services, () => new JudgeHistoryReader(bad, null));
        await vm.InitializeAsync();

        UiThread.Run(() =>
        {
            var view = new JudgeHistoryView { DataContext = vm };
            using var host = new OffscreenHost(view, 1040, 400);
            host.Relayout();
            RenderTo.Png(host, "judge-history-error");

            Assert.Contains("Judge responses could not be read", VisibleTexts(host));
        });
    }

    private static IEnumerable<string> VisibleTexts(OffscreenHost host) =>
        VisualTree.Descendants<TextBlock>(host.Content).Select(t => t.Text)
            .Concat(VisualTree.Descendants<TextBox>(host.Content).Select(t => t.Text))
            .Where(t => !string.IsNullOrEmpty(t));
}
