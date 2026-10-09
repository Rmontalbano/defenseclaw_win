using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-261: the Logs panel's filter box takes <c>connector:codex</c> - the one token the 0.8.10 TUI's Logs panel knows - through the shared parser. It narrows
/// the Events and Verdicts streams by their connector and a log file's lines by the connector they name; the free words that are left are the substring search
/// the box always was, and every other <c>field:value</c> is just text to look for. Synthetic events from the real DDL; lines are fed as the tailer would.
/// </summary>
public sealed class LogsConnectorTokenTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public LogsConnectorTokenTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ the Events stream

    private string EventsDatabase()
    {
        var path = _temp.File("logs-search.db");
        AuditTestDatabase.Create(path, rows: 0);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            void Add(string id, int minute, string connector, string? details)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal)
                    VALUES ($id, $ts, 'act', '', 'gateway', $details, 'HIGH', 'guardrail.evaluation', 'guardrail.evaluated', $connector, 'sidecar', 'logs')
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$ts", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
                command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
                command.Parameters.AddWithValue("$connector", string.IsNullOrEmpty(connector) ? DBNull.Value : connector);
                _ = command.ExecuteNonQuery();
            }

            Add("e-claude-1", 1, "claudecode", "matched a rule in the prompt");
            Add("e-codex-1", 2, "codex", "matched a rule in the prompt");
            Add("e-claude-2", 3, "claudecode", "something else entirely");
            Add("e-codex-2", 4, "codex", "something else entirely");
            Add("e-platform", 5, string.Empty, "matched a rule in the prompt");
        }

        return path;
    }

    private async Task<LogsPanelViewModel> EventsPanelAsync()
    {
        var panel = new LogsPanelViewModel(_services) { StreamReader = new EventStreamReader(EventsDatabase()) };
        panel.SetActive(true);
        panel.ActiveSource = "Events";
        await panel.LoadStructuredAsync();
        return panel;
    }

    private static string[] Ids(LogsPanelViewModel panel) => panel.DisplayedLines.Select(l => l.Fields.Single(f => f.Name == "id").Value).ToArray();

    [Fact]
    public async Task A_connector_token_narrows_the_events_to_that_connector()
    {
        var panel = await EventsPanelAsync();
        panel.ActionableOnly = false;
        Assert.Equal(5, panel.DisplayedLines.Count);

        panel.FilterText = "connector:claudecode";
        Assert.Equal(new[] { "e-claude-1", "e-claude-2" }, Ids(panel));

        panel.FilterText = "Connector:CODEX";
        Assert.Equal(new[] { "e-codex-1", "e-codex-2" }, Ids(panel));

        // The name whole: a part of it is nobody; and a platform event is in no connector's view.
        panel.FilterText = "connector:code";
        Assert.Empty(panel.DisplayedLines);
        panel.FilterText = "connector:nobody";
        Assert.Empty(panel.DisplayedLines);

        panel.FilterText = string.Empty;
        Assert.Equal(5, panel.DisplayedLines.Count);
    }

    [Fact]
    public async Task The_free_words_beside_a_connector_token_are_the_search_they_always_were()
    {
        var panel = await EventsPanelAsync();
        panel.ActionableOnly = false;

        panel.FilterText = "connector:codex entirely";
        Assert.Equal(new[] { "e-codex-2" }, Ids(panel));

        panel.FilterText = "entirely connector:codex";
        Assert.Equal(new[] { "e-codex-2" }, Ids(panel));

        panel.FilterText = "connector:claudecode \"a rule in the\"";
        Assert.Equal(new[] { "e-claude-1" }, Ids(panel));

        // Without a token it is a plain substring search, as before.
        panel.FilterText = "a rule in the";
        Assert.Equal(new[] { "e-claude-1", "e-codex-1", "e-platform" }, Ids(panel));
    }

    [Fact]
    public async Task Other_fields_are_not_tokens_here_they_are_text_to_look_for()
    {
        var panel = await EventsPanelAsync();
        panel.ActionableOnly = false;

        // The Logs panel knows connector: only. severity:high is the text "severity:high", which no event says.
        panel.FilterText = "severity:high";
        Assert.Empty(panel.DisplayedLines);

        panel.FilterText = "severity:high connector:codex";
        Assert.Empty(panel.DisplayedLines);

        // A token with no value yet narrows nothing.
        panel.FilterText = "connector:";
        Assert.Equal(5, panel.DisplayedLines.Count);
    }

    [Fact]
    public async Task A_connector_token_pauses_the_actionable_view_like_any_search()
    {
        var panel = await EventsPanelAsync();
        Assert.True(panel.IsActionableApplied);

        panel.FilterText = "connector:codex";

        Assert.False(panel.IsActionableApplied);
        Assert.Equal("a search is on", panel.ActionableSuspendedBy);
        Assert.Equal(new[] { "e-codex-1", "e-codex-2" }, Ids(panel));
    }

    [Fact]
    public async Task The_shared_connector_scope_and_a_typed_connector_must_both_hold()
    {
        // Scoped to claudecode (the chip) before the panel looks: a typed codex is another connector's, so nobody's.
        _services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
        Assert.True(_services.ConnectorScope.Set("claudecode"));
        var panel = await EventsPanelAsync();
        panel.ActionableOnly = false;
        Assert.Equal(new[] { "e-claude-1", "e-claude-2" }, Ids(panel));

        panel.FilterText = "connector:codex";
        Assert.Empty(panel.DisplayedLines);
        panel.FilterText = "connector:claudecode";
        Assert.Equal(new[] { "e-claude-1", "e-claude-2" }, Ids(panel));
    }

    // ------------------------------------------------------------------ the log files

    private LogsPanelViewModel FilePanel(params string[] lines)
    {
        var panel = new LogsPanelViewModel(_services);
        panel.SetActive(true);
        panel.AcceptLines("Gateway", lines.Select((text, i) => LogLine.Parse(text, i)).ToArray());
        return panel;
    }

    [Fact]
    public void A_log_line_belongs_to_the_connector_in_its_component_or_the_one_it_says()
    {
        var panel = FilePanel(
            "[hook:codex] preToolUse allow",
            "[hook:claudecode] preToolUse allow",
            "[guardrail] decision connector=codex action=allow",
            "[guardrail] decision connector=claudecode action=block",
            "[guardrail] connector: \"codex\" verdict ok",
            "[api] request served",
            "[otel-ingest] connector=codex-cli batch");
        panel.SelectedPreset = LogPresets.All;

        panel.FilterText = "connector:codex";

        // Both ways of naming it - and not codex-cli, which is another name.
        Assert.Equal(
            new[] { "[hook:codex] preToolUse allow", "[guardrail] decision connector=codex action=allow", "[guardrail] connector: \"codex\" verdict ok" },
            panel.DisplayedLines.Select(l => l.Raw));

        panel.FilterText = "connector:claudecode block";
        Assert.Equal(new[] { "[guardrail] decision connector=claudecode action=block" }, panel.DisplayedLines.Select(l => l.Raw));

        panel.FilterText = "connector:nobody";
        Assert.Empty(panel.DisplayedLines);

        panel.FilterText = string.Empty;
        Assert.Equal(7, panel.DisplayedLines.Count);
    }

    [Fact]
    public void A_line_arriving_while_a_connector_token_is_on_is_judged_the_same_way()
    {
        var panel = FilePanel("[hook:codex] first");
        panel.SelectedPreset = LogPresets.All;
        panel.FilterText = "connector:codex";
        Assert.Single(panel.DisplayedLines);

        panel.AcceptLines("Gateway", new[]
        {
            LogLine.Parse("[hook:codex] second", 10),
            LogLine.Parse("[hook:claudecode] third", 11),
            LogLine.Parse("[guardrail] later connector=codex", 12),
        });

        Assert.Equal(
            new[] { "[hook:codex] first", "[hook:codex] second", "[guardrail] later connector=codex" },
            panel.DisplayedLines.Select(l => l.Raw));
    }

    [Fact]
    public void A_name_with_characters_that_mean_something_to_a_pattern_is_only_a_name()
    {
        var panel = FilePanel("[guardrail] connector=a.b ok", "[guardrail] connector=aXb ok", "[guardrail] connector=(a|b) ok");
        panel.SelectedPreset = LogPresets.All;

        panel.FilterText = "connector:a.b";
        Assert.Equal(new[] { "[guardrail] connector=a.b ok" }, panel.DisplayedLines.Select(l => l.Raw));

        panel.FilterText = "connector:(a|b)";
        Assert.Equal(new[] { "[guardrail] connector=(a|b) ok" }, panel.DisplayedLines.Select(l => l.Raw));
    }
}
