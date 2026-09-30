using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The Alerts panel lists the unacknowledged queue from <c>audit.db</c> (the Mac's definition, the same count as the sidebar
/// badge), keeps the gateway's <c>/alerts</c> for a database that predates the queue, takes a severity floor as a deep link, and
/// tells the counts when something is acknowledged. Synthetic rows from the real DDL; the panel runs on a plain STA thread.
/// </summary>
public sealed class AlertsPanelQueueTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly AlertQueueDatabase _database;

    public AlertsPanelQueueTests()
    {
        _services = TestServices.Create(_temp);
        _database = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    /// <summary>A finding with the attributes the gateway writes into <c>structured_json</c>.</summary>
    private void Finding(string id, int minute, string severity, string? rule = null, string? title = null, string? evidence = null, string connector = "claudecode")
    {
        var structured = new Dictionary<string, string>();
        if (rule is not null)
        {
            structured[GatewayAlert.Keys.RuleId] = rule;
        }

        if (title is not null)
        {
            structured[GatewayAlert.Keys.Title] = title;
        }

        if (evidence is not null)
        {
            structured[GatewayAlert.Keys.EvidenceSummary] = evidence;
        }

        using var connection = new SqliteConnection($"Data Source={_services.Paths.AuditDatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, structured_json, bucket, connector, event_name)
            VALUES ($id, $timestamp, 'scan-finding', $target, 'audit_logger', $severity, $structured, 'security.finding', $connector, 'finding.observed')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", AlertQueueDatabase.Format(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$target", "/synthetic/" + id);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$structured", structured.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(structured));
        command.Parameters.AddWithValue("$connector", connector);
        _ = command.ExecuteNonQuery();
    }

    private static GatewayAlert GatewayAlertRow(string id, string severity) => new()
    {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
        Severity = severity,
        Action = "block",
        Structured = new Dictionary<string, JsonElement>(),
    };

    private static GatewaySnapshot Snapshot(params GatewayAlert[] alerts) => new()
    {
        State = AppGatewayState.Running,
        PolledAt = DateTimeOffset.UtcNow,
        AlertsFetchedAt = DateTimeOffset.UtcNow,
        RecentAlerts = alerts,
    };

    private static string[] Ids(AlertsPanelViewModel vm) => vm.Alerts.Select(a => a.Key).ToArray();

    private static SeverityFilter Chip(AlertsPanelViewModel vm, string severity) => vm.SeverityFilters.Single(f => f.Severity == severity);

    // ------------------------------------------------------------------ the queue is the source

    [Fact]
    public void The_queue_is_listed_newest_first_without_the_acknowledged_and_past_the_gateways_25()
    {
        for (var i = 0; i < 40; i++)
        {
            Finding($"f{i:D2}", i, "HIGH");
        }

        _database.Acknowledge("f05");
        _database.Acknowledge("f06");
        _database.Acknowledge("f39");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);

            await vm.InitializeAsync();

            Assert.Equal(37, vm.Alerts.Count);
            Assert.Equal("f38", vm.Alerts[0].Key);
            Assert.Equal("f00", vm.Alerts[^1].Key);
            Assert.DoesNotContain("f05", Ids(vm));
            Assert.DoesNotContain("f39", Ids(vm));
            Assert.Equal("37 of 37 alerts", vm.CountSummary);
            Assert.StartsWith("Unacknowledged findings · 37 · read ", vm.SourceNote, StringComparison.Ordinal);
            Assert.Equal(37, Chip(vm, "HIGH").Count);
        });
    }

    [Fact]
    public void A_row_carries_the_details_of_its_audit_row_not_just_the_queues_columns()
    {
        Finding("a", 1, "CRITICAL", rule: "CMD-ENV-DUMP", title: "Environment variable dump", evidence: "printenv piped to curl");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);

            await vm.InitializeAsync();

            var row = Assert.Single(vm.Alerts);
            Assert.Equal("CRITICAL", row.Severity);
            Assert.Equal("CMD-ENV-DUMP", row.RuleId);
            Assert.Equal("Environment variable dump", row.Headline);
            Assert.Equal("printenv piped to curl", row.Evidence);
            Assert.Equal("scan-finding", row.Action);
            Assert.Equal("/synthetic/a", row.TargetRef);
            Assert.NotEmpty(row.Fields);
        });
    }

    [Fact]
    public void An_empty_queue_says_there_is_nothing_to_acknowledge_not_that_the_gateway_reported_nothing()
    {
        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);

            await vm.InitializeAsync();

            Assert.True(vm.IsEmpty);
            Assert.Equal("Nothing to acknowledge", vm.EmptyTitle);
            Assert.Contains("audit database", vm.EmptyDetail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void While_the_queue_is_the_source_a_gateway_poll_leaves_the_list_alone()
    {
        Finding("a", 1, "HIGH");
        Finding("b", 2, "HIGH");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var before = Ids(vm);

            vm.Apply(Snapshot(GatewayAlertRow("from-the-gateway", "CRITICAL")));

            Assert.Equal(before, Ids(vm));
            Assert.DoesNotContain("from-the-gateway", Ids(vm));
        });
    }

    [Fact]
    public void Refresh_adds_what_arrived_and_keeps_the_rows_it_already_has()
    {
        Finding("a", 1, "HIGH");
        Finding("b", 2, "HIGH");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var kept = vm.Alerts.ToArray();

            Finding("c", 3, "CRITICAL");
            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "c", "b", "a" }, Ids(vm));
            Assert.Same(kept[0], vm.Alerts[1]);
            Assert.Same(kept[1], vm.Alerts[2]);
        });
    }

    [Fact]
    public void A_finding_acknowledged_elsewhere_leaves_the_list_on_the_next_read()
    {
        Finding("a", 1, "HIGH");
        Finding("b", 2, "HIGH");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            _database.Acknowledge("b");
            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "a" }, Ids(vm));
        });
    }

    // ------------------------------------------------------------------ the gateway stays the fallback

    [Fact]
    public void A_database_from_before_the_queue_schema_keeps_the_gateways_list()
    {
        StaThread.Run(async () =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            _ = AlertQueueDatabase.Legacy(services.Paths.AuditDatabasePath);
            var vm = new AlertsPanelViewModel(services);

            await vm.InitializeAsync();
            vm.Apply(Snapshot(GatewayAlertRow("g1", "HIGH"), GatewayAlertRow("g2", "CRITICAL")));

            Assert.Equal(new[] { "g1", "g2" }, Ids(vm));
            Assert.Contains("GET /alerts", vm.SourceNote, StringComparison.Ordinal);
            SqliteConnection.ClearAllPools();
        });
    }

    [Fact]
    public void With_no_audit_database_the_gateways_list_is_what_there_is()
    {
        StaThread.Run(async () =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var vm = new AlertsPanelViewModel(services);

            await vm.InitializeAsync();
            vm.Apply(Snapshot(GatewayAlertRow("g1", "HIGH")));

            Assert.Equal(new[] { "g1" }, Ids(vm));
            Assert.Contains("GET /alerts", vm.SourceNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_queue_that_stops_being_usable_gives_the_list_back_to_the_gateway()
    {
        Finding("a", 1, "HIGH");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            Assert.Equal(new[] { "a" }, Ids(vm));

            // The database goes away (a reinstall): the queue says so, and the gateway's own list is shown again.
            SqliteConnection.ClearAllPools();
            File.Delete(_services.Paths.AuditDatabasePath);
            await vm.InitializeAsync();
            vm.Apply(Snapshot(GatewayAlertRow("g1", "HIGH")));

            Assert.Equal(new[] { "g1" }, Ids(vm));
        });
    }

    // ------------------------------------------------------------------ deep links

    [Fact]
    public void A_severity_floor_turns_on_that_severity_and_above_and_clears_the_text_filter()
    {
        Finding("crit", 4, "CRITICAL");
        Finding("high", 3, "HIGH");
        Finding("med", 2, "MEDIUM");
        Finding("low", 1, "LOW");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            vm.FilterText = "no row says this";
            Assert.Empty(vm.Alerts);

            vm.Accept(new AlertsFilter(AuditSeverity.High));

            Assert.Equal(string.Empty, vm.FilterText);
            Assert.Equal(new[] { "crit", "high" }, Ids(vm));
            Assert.Equal(new[] { true, true, false, false, false }, vm.SeverityFilters.Select(f => f.IsEnabled));

            vm.Accept(new AlertsFilter(AuditSeverity.Critical));
            Assert.Equal(new[] { "crit" }, Ids(vm));

            vm.Accept(new AlertsFilter(AuditSeverity.Low));
            Assert.Equal(new[] { "crit", "high", "med", "low" }, Ids(vm));
            Assert.Equal(new[] { true, true, true, true, false }, vm.SeverityFilters.Select(f => f.IsEnabled));
        });
    }

    [Fact]
    public void A_link_that_lands_before_the_first_read_is_honoured_when_the_rows_arrive()
    {
        Finding("crit", 2, "CRITICAL");
        Finding("med", 1, "MEDIUM");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);

            vm.Accept(new AlertsFilter(AuditSeverity.Critical));
            await vm.InitializeAsync();

            Assert.Equal(new[] { "crit" }, Ids(vm));
        });
    }

    [Fact]
    public void A_payload_with_no_floor_or_of_another_kind_changes_nothing()
    {
        Finding("crit", 2, "CRITICAL");
        Finding("med", 1, "MEDIUM");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            vm.FilterText = "med";

            vm.Accept(new AlertsFilter(Kind: AlertsFilter.KindBlocks));
            vm.Accept(new AuditPreset("errors"));
            vm.Accept("a string");

            Assert.Equal("med", vm.FilterText);
            Assert.All(vm.SeverityFilters, f => Assert.True(f.IsEnabled));
        });
    }

    [Fact]
    public void Setting_the_floor_filters_once_not_once_per_severity_toggle()
    {
        Finding("c1", 1, "CRITICAL");
        Finding("c2", 2, "CRITICAL");
        Finding("m1", 3, "MEDIUM");
        Finding("m2", 4, "MEDIUM");
        Finding("l1", 5, "LOW");
        Finding("l2", 6, "LOW");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var summaries = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AlertsPanelViewModel.CountSummary))
                {
                    summaries.Add(vm.CountSummary);
                }
            };

            vm.Accept(new AlertsFilter(AuditSeverity.Critical));

            // One pass: the list goes from "6 of 6" straight to "2 of 6", never showing "4 of 6" on the way (a pass per toggle would).
            Assert.Equal(new[] { "2 of 6 alerts" }, summaries);
            Assert.Equal(new[] { "c2", "c1" }, Ids(vm));
        });
    }

    // ------------------------------------------------------------------ the badge follows an acknowledge

    private static CliInvocation Invocation(int exitCode, params string[] lines)
    {
        var invocation = InvocationFactory.Create(false, "alerts");
        foreach (var line in lines)
        {
            InvocationFactory.Append(invocation, line);
        }

        InvocationFactory.Finish(invocation, exitCode);
        return invocation;
    }

    [Fact]
    public void Acknowledging_takes_the_rows_out_of_the_list_and_the_count_down_at_once()
    {
        Finding("a", 1, "CRITICAL");
        Finding("b", 2, "CRITICAL");
        Finding("c", 3, "CRITICAL");

        StaThread.Run(async () =>
        {
            await _services.AlertCounts.RefreshAsync();
            Assert.Equal(3, _services.AlertCounts.Current.Total);

            var vm = new AlertsPanelViewModel(_services)
            {
                RunCli = (argv, _) =>
                {
                    if (argv.Contains("--dry-run"))
                    {
                        return Task.FromResult(Invocation(0, "Preview: 3 alert(s) matched; digest=sha256:v1:0123", "  c version=0", "  b version=0", "  a version=0"));
                    }

                    // The real command writes the acknowledgement into audit.db before it returns.
                    foreach (var id in new[] { "a", "b", "c" })
                    {
                        _database.Acknowledge(id);
                    }

                    return Task.FromResult(Invocation(0, "Preview: 3 alert(s) matched; digest=sha256:v1:0123", "  OK Acknowledged 3 alert(s)."));
                },
            };
            await vm.InitializeAsync();
            Assert.Equal(3, vm.Alerts.Count);

            await vm.OpenAcknowledgeCommand.ExecuteAsync(null);
            await vm.ConfirmReviewCommand.ExecuteAsync(null);

            Assert.Empty(vm.Alerts);
            Assert.True(vm.IsEmpty);
            Assert.Equal(0, _services.AlertCounts.Current.Total);
        });
    }

    [Fact]
    public void The_counts_are_refreshed_even_when_a_test_supplies_its_own_reload()
    {
        Finding("a", 1, "CRITICAL");

        StaThread.Run(async () =>
        {
            await _services.AlertCounts.RefreshAsync();
            var reloads = 0;
            var vm = new AlertsPanelViewModel(_services)
            {
                RunCli = (argv, _) =>
                {
                    if (!argv.Contains("--dry-run"))
                    {
                        _database.Acknowledge("a");
                    }

                    return Task.FromResult(Invocation(0, "Preview: 1 alert(s) matched; digest=sha256:v1:0123", "  a version=0", "  OK Acknowledged 1 alert(s)."));
                },
                AfterApply = () =>
                {
                    reloads++;
                    return Task.CompletedTask;
                },
            };
            await vm.InitializeAsync();

            await vm.OpenAcknowledgeCommand.ExecuteAsync(null);
            await vm.ConfirmReviewCommand.ExecuteAsync(null);

            Assert.Equal(1, reloads);
            Assert.Equal(0, _services.AlertCounts.Current.Total);
        });
    }

    // ------------------------------------------------------------------ activation

    [Fact]
    public void An_active_panel_follows_the_counts_and_gives_the_subscription_back_when_it_leaves()
    {
        Finding("a", 1, "HIGH");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            Assert.False(_services.AlertCounts.IsRunning);

            vm.SetActive(true);
            Assert.True(_services.AlertCounts.IsRunning);

            Finding("b", 2, "CRITICAL");
            await _services.AlertCounts.RefreshAsync();
            var deadline = Environment.TickCount64 + 30_000;
            while (vm.Alerts.Count < 2)
            {
                Assert.True(Environment.TickCount64 < deadline, "The panel did not pick up the new finding when the counts changed.");
                await Task.Delay(20);
            }

            Assert.Equal("b", vm.Alerts[0].Key);

            vm.SetActive(false);
            Assert.False(_services.AlertCounts.IsRunning);
        });
    }

    // ------------------------------------------------------------------ a row from what the queue knows

    [Fact]
    public void A_finding_whose_audit_row_is_gone_is_still_a_row_built_from_the_queues_own_columns()
    {
        var item = new AlertQueueItem("gone", AuditSeverity.High, "scan-finding", "/synthetic/gone", "codex", Base);

        var row = AlertItem.FromQueue(item);

        Assert.Equal("gone", row.Key);
        Assert.Equal("HIGH", row.Severity);
        Assert.Equal("High", row.SeverityKey);
        Assert.Equal("scan-finding", row.Headline);
        Assert.Equal("/synthetic/gone", row.TargetRef);
        Assert.Equal("codex", row.Source);
        Assert.False(row.HasEvidence);
        Assert.Empty(row.Fields);

        var anonymous = AlertItem.FromQueue(item with { Id = string.Empty, Action = string.Empty, Target = null, Connector = null });
        Assert.False(string.IsNullOrEmpty(anonymous.Key));
        Assert.Equal("(finding)", anonymous.Headline);
    }
}
