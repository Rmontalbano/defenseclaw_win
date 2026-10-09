using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-262: the Events view - the Logs panel's Events stream, narrowed by default to what the 0.8.10 TUI's Alerts panel shows of the v8 history (the shared
/// <see cref="ActionableRule"/>), with the switch, the count of what it leaves out, the filters that pause it, the way in from Alerts, and the empty state that
/// says what is hidden. A synthetic audit.db from the real DDL read by the real reader; no window.
/// </summary>
public sealed class EventsViewTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public EventsViewTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private delegate void AddEvent(string id, int minute, string bucket, string eventName, string severity, string? details, string? payload);

    private string Database(Action<AddEvent> seed, string name = "events-audit.db")
    {
        var path = _temp.File(name);
        AuditTestDatabase.Create(path, rows: 0);
        Add(path, seed);
        return path;
    }

    private static void Add(string path, Action<AddEvent> seed)
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            seed((id, minute, bucket, eventName, severity, details, payload) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, event_name, connector, source, signal, payload_json)
                    VALUES ($id, $timestamp, 'act', '', 'gateway', $details, $severity, $bucket, $eventName, 'claudecode', 'sidecar', 'logs', $payload)
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$timestamp", Base.AddMinutes(minute).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z");
                command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
                command.Parameters.AddWithValue("$severity", severity);
                command.Parameters.AddWithValue("$bucket", bucket);
                command.Parameters.AddWithValue("$eventName", eventName);
                command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
                _ = command.ExecuteNonQuery();
            });
        }

        SqlitePools.Release(path);
    }

    /// <summary>Four events the TUI would show (a HIGH, a block, an egress block, an ERROR), three it would hide, and one of telemetry.</summary>
    private string Mixed() => Database(add =>
    {
        add("e-high", 1, "guardrail.evaluation", "guardrail.evaluated", "HIGH", "matched a rule", null);
        add("e-quiet-1", 2, "guardrail.evaluation", "guardrail.evaluated", "INFO", null, """{"defenseclaw.guardrail.decision":"allow"}""");
        add("e-block", 3, "enforcement.action", "enforcement.applied", "INFO", null, """{"defenseclaw.enforcement.effective_action":"block"}""");
        add("e-quiet-2", 4, "asset.scan", "scan.completed", "MEDIUM", "scan done", null);
        add("e-egress", 5, "network.egress", "egress.decided", "INFO", null, """{"defenseclaw.network.decision":"block","defenseclaw.network.target_ref":"api.example.test","defenseclaw.network.reason":"not on the allow list"}""");
        add("e-quiet-3", 6, "platform.health", "sink.checked", "INFO", "sink ok", null);
        add("e-error", 7, "platform.health", "sink.checked", "ERROR", "sink unreachable", null);
        add("e-telemetry", 8, "telemetry.ingest", "span.received", "INFO", "span", null);
    });

    private LogsPanelViewModel EventsPanel(string path)
    {
        var panel = new LogsPanelViewModel(_services) { StreamReader = new EventStreamReader(path) };
        panel.SetActive(true);
        panel.ActiveSource = "Events";
        return panel;
    }

    private static string[] Ids(LogsPanelViewModel panel) =>
        panel.DisplayedLines.Select(l => l.Fields.Single(f => f.Name == "id").Value).ToArray();

    // ---- The default ----

    [Fact]
    public async Task Events_open_on_the_actionable_view_and_count_what_they_leave_out()
    {
        var panel = EventsPanel(Mixed());

        await panel.LoadStructuredAsync();

        Assert.True(LogsPanelViewModel.ActionableByDefault);
        Assert.True(panel.ActionableOnly);
        Assert.True(panel.IsActionableApplied);
        Assert.True(panel.CanChangeActionable);
        Assert.Null(panel.ActionableSuspendedBy);

        // Oldest first, like a log: the four the TUI shows.
        Assert.Equal(new[] { "e-high", "e-block", "e-egress", "e-error" }, Ids(panel));
        Assert.Equal(3, panel.ActionableHidden);
        Assert.True(panel.HasActionableHidden);
        Assert.Equal("3 low-signal hidden", panel.ActionableHiddenText);
        Assert.False(panel.IsEmpty);
    }

    [Fact]
    public async Task Turning_it_off_shows_every_event_and_back_on_narrows_again_without_another_read()
    {
        var path = Mixed();
        var reader = new EventStreamReader(path);
        var panel = new LogsPanelViewModel(_services) { StreamReader = reader };
        panel.SetActive(true);
        panel.ActiveSource = "Events";
        await panel.LoadStructuredAsync();
        var reads = reader.ReadCount;

        panel.ActionableOnly = false;

        Assert.Equal(new[] { "e-high", "e-quiet-1", "e-block", "e-quiet-2", "e-egress", "e-quiet-3", "e-error" }, Ids(panel));
        Assert.Equal(0, panel.ActionableHidden);
        Assert.False(panel.HasActionableHidden);
        Assert.False(panel.IsActionableApplied);

        panel.ActionableOnly = true;

        Assert.Equal(new[] { "e-high", "e-block", "e-egress", "e-error" }, Ids(panel));
        Assert.Equal(3, panel.ActionableHidden);

        // The rule is applied to the rows already read: the switch costs no query.
        Assert.Equal(reads, reader.ReadCount);
    }

    [Fact]
    public async Task The_decision_target_and_kind_of_an_egress_event_are_on_its_row()
    {
        var panel = EventsPanel(Mixed());
        await panel.LoadStructuredAsync();

        var egress = panel.DisplayedLines.Single(l => l.Fields.Any(f => f is { Name: "id", Value: "e-egress" }));

        Assert.Equal("block", egress.Action);
        Assert.Equal("egress", egress.EventType);
        Assert.Equal("[egress:block]", egress.Label);
        Assert.Contains(egress.Fields, f => f is { Name: "target", Value: "api.example.test" });
        Assert.Contains("not on the allow list", egress.Message, StringComparison.Ordinal);
        Assert.True(egress.IsActionable);
    }

    // ---- Paused while something specific is asked for ----

    [Theory]
    [InlineData("search", "a search is on", "e-quiet-3")]
    [InlineData("severity", "a minimum severity is set", "e-quiet-2")]
    [InlineData("preset", "the scan preset is chosen", "e-quiet-2")]
    [InlineData("action", "an action or event filter is set", "e-quiet-1")]
    [InlineData("event", "an action or event filter is set", "e-quiet-2")]
    [InlineData("telemetry", "telemetry is included", "e-telemetry")]
    public async Task A_filter_that_asks_for_something_specific_pauses_it_while_it_is_on_and_clearing_it_brings_it_back(string filter, string why, string quietRow)
    {
        var panel = EventsPanel(Mixed());
        await panel.LoadStructuredAsync();
        Assert.True(panel.IsActionableApplied);
        Assert.DoesNotContain(quietRow, Ids(panel));

        switch (filter)
        {
            case "search":
                panel.FilterText = "platform";
                break;
            case "severity":
                panel.SelectedSeverity = "≥ Low";
                break;
            case "preset":
                panel.SelectedPreset = "scan";
                break;
            case "action":
                panel.SelectedAction = "allow";
                break;
            case "event":
                panel.SelectedEvent = "scan";
                break;
            default:
                panel.IncludeTelemetry = true;
                await panel.LoadStructuredAsync();
                break;
        }

        // The choice is kept; the view is off while the filter is on, so the quiet event shows - and nothing is counted as hidden.
        Assert.True(panel.ActionableOnly);
        Assert.False(panel.IsActionableApplied);
        Assert.False(panel.CanChangeActionable);
        Assert.Equal(why, panel.ActionableSuspendedBy);
        Assert.Contains("paused while " + why, panel.ActionableToolTip, StringComparison.Ordinal);
        Assert.Contains(quietRow, Ids(panel));
        Assert.Equal(0, panel.ActionableHidden);
        Assert.False(panel.HasActionableHidden);

        switch (filter)
        {
            case "search":
                panel.FilterText = string.Empty;
                break;
            case "severity":
                panel.SelectedSeverity = LogPresets.AnySeverity;
                break;
            case "preset":
                panel.SelectedPreset = "no-noise";
                break;
            case "action":
                panel.SelectedAction = "all";
                break;
            case "event":
                panel.SelectedEvent = "all";
                break;
            default:
                panel.IncludeTelemetry = false;
                await panel.LoadStructuredAsync();
                break;
        }

        Assert.True(panel.IsActionableApplied);
        Assert.Null(panel.ActionableSuspendedBy);
        Assert.DoesNotContain(quietRow, Ids(panel));
        Assert.Equal(3, panel.ActionableHidden);
    }

    [Theory]
    [InlineData("no-noise")]
    [InlineData("all")]
    public async Task The_default_preset_and_all_pause_nothing(string preset)
    {
        var panel = EventsPanel(Mixed());
        await panel.LoadStructuredAsync();

        panel.SelectedPreset = preset;

        Assert.True(panel.IsActionableApplied);
        Assert.Equal(new[] { "e-high", "e-block", "e-egress", "e-error" }, Ids(panel));
    }

    [Fact]
    public async Task The_hooks_link_from_Overview_lands_on_the_hook_calls_not_on_an_empty_list()
    {
        var path = Database(add =>
        {
            add("hook-1", 1, "guardrail.evaluation", "guardrail.evaluated", "INFO", "connector=claudecode result=ok action=allow hook", """{"defenseclaw.guardrail.decision":"allow"}""");
            add("hook-2", 2, "guardrail.evaluation", "guardrail.evaluated", "INFO", "connector=claudecode result=ok action=allow hook", """{"defenseclaw.guardrail.decision":"allow"}""");
            add("other", 3, "asset.scan", "scan.completed", "INFO", "scan done", null);
        });
        var panel = new LogsPanelViewModel(_services) { StreamReader = new EventStreamReader(path) };
        panel.SetActive(true);

        panel.Accept(new LogsPreset("hooks"));
        await panel.LoadStructuredAsync();

        Assert.Equal("Events", panel.ActiveSource);
        Assert.True(panel.ActionableOnly);
        Assert.False(panel.IsActionableApplied);
        Assert.Contains("hooks preset", panel.ActionableSuspendedBy, StringComparison.Ordinal);
        Assert.NotEmpty(panel.DisplayedLines);
        Assert.All(panel.DisplayedLines, l => Assert.Equal("allow", l.Action));
    }

    // ---- Verdicts and the log files are not narrowed ----

    [Fact]
    public async Task Verdicts_and_the_log_files_are_not_narrowed()
    {
        var panel = new LogsPanelViewModel(_services) { StreamReader = new EventStreamReader(Mixed()) };
        panel.SetActive(true);
        panel.ActiveSource = "Verdicts";
        panel.SelectedPreset = "all";

        await panel.LoadStructuredAsync();

        Assert.False(panel.IsEventsSource);
        Assert.False(panel.IsActionableApplied);
        Assert.Contains("e-quiet-1", Ids(panel));
        Assert.Contains("e-quiet-2", Ids(panel));
        Assert.Equal(0, panel.ActionableHidden);

        panel.ActiveSource = "Gateway";
        Assert.False(panel.IsActionableApplied);
    }

    // ---- The way in from Alerts ----

    [Fact]
    public async Task The_alerts_link_opens_events_from_a_clean_slate_on_the_actionable_view()
    {
        var panel = EventsPanel(Mixed());
        panel.ActiveSource = "Watchdog";
        panel.FilterText = "leftover";
        panel.SelectedSeverity = "≥ High";
        panel.SelectedAction = "block";
        panel.SelectedEvent = "scan";
        panel.SelectedPreset = "errors";
        panel.IncludeTelemetry = true;
        panel.AutoScroll = false;
        panel.ActionableOnly = false;

        panel.Accept(new LogsEvents());
        await panel.LoadStructuredAsync();

        Assert.Equal("Events", panel.ActiveSource);
        Assert.Equal("no-noise", panel.SelectedPreset);
        Assert.Equal(string.Empty, panel.FilterText);
        Assert.Equal(LogPresets.AnySeverity, panel.SelectedSeverity);
        Assert.Equal("all", panel.SelectedAction);
        Assert.Equal("all", panel.SelectedEvent);
        Assert.False(panel.IncludeTelemetry);
        Assert.True(panel.AutoScroll);
        Assert.True(panel.ActionableOnly);
        Assert.True(panel.IsActionableApplied);
        Assert.Equal(new[] { "e-high", "e-block", "e-egress", "e-error" }, Ids(panel));
    }

    [Fact]
    public async Task The_alerts_link_can_ask_for_every_event()
    {
        var panel = EventsPanel(Mixed());

        panel.Accept(new LogsEvents(ActionableOnly: false));
        await panel.LoadStructuredAsync();

        Assert.Equal("Events", panel.ActiveSource);
        Assert.False(panel.ActionableOnly);
        Assert.Equal(7, panel.DisplayedLines.Count);
    }

    [Fact]
    public void The_alerts_button_asks_the_shell_for_the_events_view_and_changes_nothing_in_alerts()
    {
        StaThread.Run(() =>
        {
            var alerts = new AlertsPanelViewModel(_services);
            var filter = alerts.FilterText;
            var inbox = new List<NavigationRequest>();
            _services.Navigation.Requested += (_, e) => inbox.Add(e.Request);

            Assert.True(alerts.OpenEventsCommand.CanExecute(null));
            alerts.OpenEventsCommand.Execute(null);

            var request = Assert.Single(inbox);
            Assert.Equal("logs", request.PanelId);
            Assert.Equal(new LogsEvents(), request.Payload);
            Assert.Same(request, _services.Navigation.Pending);
            Assert.Equal(filter, alerts.FilterText);
        });
    }

    [Fact]
    public async Task A_payload_of_another_kind_leaves_the_view_as_it_was()
    {
        var panel = EventsPanel(Mixed());
        await panel.LoadStructuredAsync();

        panel.Accept("a string");
        panel.Accept(new AuditPreset("blocks"));

        Assert.Equal("Events", panel.ActiveSource);
        Assert.True(panel.IsActionableApplied);
        Assert.Equal(4, panel.DisplayedLines.Count);
    }

    // ---- What an empty view says ----

    [Fact]
    public async Task A_view_with_nothing_actionable_says_what_it_hides_and_where_the_switch_is()
    {
        var path = Database(add =>
        {
            add("q-1", 1, "guardrail.evaluation", "guardrail.evaluated", "INFO", null, """{"defenseclaw.guardrail.decision":"allow"}""");
            add("q-2", 2, "asset.scan", "scan.completed", "LOW", "scan done", null);
        });
        var panel = EventsPanel(path);

        await panel.LoadStructuredAsync();

        Assert.True(panel.IsEmpty);
        Assert.Equal("No actionable events", panel.EmptyTitle);
        Assert.Equal("2 low-signal events are hidden. Turn off \"Actionable only\" to see them.", panel.EmptyDetail);
        Assert.Equal(2, panel.ActionableHidden);

        panel.ActionableOnly = false;
        Assert.False(panel.IsEmpty);
        Assert.Equal(2, panel.DisplayedLines.Count);
    }

    [Fact]
    public async Task A_database_with_no_events_at_all_is_not_blamed_on_the_switch()
    {
        var panel = EventsPanel(Database(_ => { }));

        await panel.LoadStructuredAsync();

        Assert.True(panel.IsEmpty);
        Assert.Equal("No log lines", panel.EmptyTitle);
        Assert.Contains("No data in audit.db", panel.EmptyDetail, StringComparison.Ordinal);
    }

    // ---- The poll ----

    [Fact]
    public async Task A_poll_that_finds_new_events_lists_the_actionable_ones_and_counts_the_rest()
    {
        var path = Mixed();
        var panel = EventsPanel(path);
        await panel.LoadStructuredAsync();
        Assert.Equal(3, panel.ActionableHidden);

        Add(path, add =>
        {
            add("e-new-quiet", 20, "guardrail.evaluation", "guardrail.evaluated", "INFO", null, """{"defenseclaw.guardrail.decision":"allow"}""");
            add("e-new-block", 21, "enforcement.action", "enforcement.applied", "INFO", "install-blocked", null);
        });
        await panel.LoadStructuredAsync();

        Assert.Equal(new[] { "e-high", "e-block", "e-egress", "e-error", "e-new-block" }, Ids(panel));
        Assert.Equal(4, panel.ActionableHidden);
        Assert.Equal("4 low-signal hidden", panel.ActionableHiddenText);
    }

    // ---- The dimensions the stream is held to ----

    [Fact]
    public async Task Every_bucket_the_TUIs_alerts_panel_reads_is_on_the_events_view_when_it_matters()
    {
        // One event per alert bucket, each with something the rule keeps; telemetry stays out.
        var path = Database(add =>
        {
            var minute = 0;
            foreach (var bucket in EventStreamReader.AlertBuckets)
            {
                add("id-" + bucket, ++minute, bucket, bucket + ".recorded", "HIGH", "recorded", null);
            }

            add("id-telemetry", ++minute, "telemetry.ingest", "span.received", "HIGH", "span", null);
        });
        var panel = EventsPanel(path);

        await panel.LoadStructuredAsync();

        Assert.Equal(EventStreamReader.AlertBuckets.Select(b => "id-" + b), Ids(panel));
        Assert.DoesNotContain("id-telemetry", Ids(panel));
    }
}
