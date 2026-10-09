using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The flyout after the Mac's popover (CUST-204): what each section says, where each row goes, that nothing is read unless the flyout is on
/// screen and monitoring is running, and that it never changes anything by itself. The data is synthetic (<see cref="FlyoutScene"/>); the gateway
/// "Running" snapshot is built by hand, because a poll cannot reach that state without a real gateway on the port.
/// </summary>
[Collection(UiCollection.Name)]
public class TrayFlyoutViewModelTests
{
    private static TrayFlyoutViewModel Create(FlyoutScene scene, Action? openDashboard = null, Action? exit = null) =>
        new(scene.Services, openDashboard ?? (() => { }), exit ?? (() => { }), metricsReader: null, timeProvider: new ManualClock());

    private static void WaitForNumbers(TrayFlyoutViewModel viewModel) =>
        UiThread.WaitFor(
            () => viewModel.Metrics[0].Value != "—" && viewModel.Metrics[2].Value != "—",
            "the flyout to read the audit window and the alert counts");

    private static string N0(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // ------------------------------------------------------------------ header and connectors

    [Fact]
    public void A_running_gateway_reads_like_the_macs_header()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);

            viewModel.Apply(FlyoutScene.RunningSnapshot());

            Assert.Equal("Running", viewModel.StateLabel);
            Assert.Equal("Ok", viewModel.StateTone);
            Assert.Equal("Gateway up · 4d up · 2 connectors", viewModel.HeaderCaption);
            Assert.StartsWith("v0.8.10 · 127.0.0.1:18970 · polled ", viewModel.FactsLine, StringComparison.Ordinal);
            Assert.False(viewModel.IsPaused);
            Assert.Equal("Pause", viewModel.PauseText);
        });
    }

    [Fact]
    public void One_connector_is_singular_and_a_young_gateway_counts_minutes()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);

            viewModel.Apply(FlyoutScene.RunningSnapshot(uptimeMs: 43 * 60_000) with { ActiveConnectors = new[] { "claudecode" } });

            Assert.Equal("Gateway up · 43m up · 1 connector", viewModel.HeaderCaption);
        });
    }

    [Fact]
    public void A_gateway_that_is_not_answering_reads_offline_and_its_connectors_have_no_counters()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);

            viewModel.Apply(new GatewaySnapshot
            {
                State = AppGatewayState.GatewayStopped,
                Detail = "Nothing is listening on port 18970.",
                ActiveConnectors = new[] { "claudecode" },
                ApiPort = 18970,
                PolledAt = FlyoutScene.Now,
            });

            Assert.Equal("Gateway stopped", viewModel.StateLabel);
            Assert.Equal("Bad", viewModel.StateTone);
            Assert.Equal("Gateway offline", viewModel.HeaderCaption);

            // Say what is missing: a zero here would read as "nothing happened".
            var row = Assert.Single(viewModel.Connectors);
            Assert.Equal("no live counters", row.Counts);
            Assert.Equal("Neutral", row.Tone);
        });
    }

    [Fact]
    public void Before_the_first_poll_nothing_is_claimed()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);

            Assert.Equal("Checking…", viewModel.StateLabel);
            Assert.Empty(viewModel.Connectors);
            Assert.False(viewModel.ShowNoConnectors);
            Assert.Equal("never", viewModel.LastPolled);
        });
    }

    [Fact]
    public void A_gateway_that_names_no_connector_says_so()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);

            viewModel.Apply(FlyoutScene.RunningSnapshot() with { ActiveConnectors = Array.Empty<string>() });

            Assert.True(viewModel.ShowNoConnectors);
            Assert.False(viewModel.HasConnectors);
            Assert.Equal("Gateway up · 4d up · 0 connectors", viewModel.HeaderCaption);
        });
    }

    [Fact]
    public void Each_connector_row_shows_its_name_its_mode_and_what_the_gateway_counted()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);

            viewModel.Apply(FlyoutScene.RunningSnapshot());

            Assert.Equal(new[] { "claudecode", "copilot" }, viewModel.Connectors.Select(c => c.Name));

            var first = viewModel.Connectors[0];
            Assert.Equal("observe", first.Mode);
            Assert.True(first.HasMode);
            Assert.Equal("Ok", first.Tone);
            Assert.Equal($"{N0(1934)} calls · {N0(0)} blocks", first.Counts);

            // Tool blocks and subprocess blocks are one number, as the Mac adds them.
            var second = viewModel.Connectors[1];
            Assert.Equal("enforce", second.Mode);
            Assert.Equal($"{N0(52)} calls · {N0(4)} blocks", second.Counts);
            Assert.Contains("copilot, enforce", second.AutomationName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_connector_the_gateway_does_not_report_is_not_running_and_a_mode_config_does_not_state_is_left_out()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);

            viewModel.Apply(FlyoutScene.RunningSnapshot() with { ActiveConnectors = new[] { "claudecode", "hermes" } });

            var hermes = viewModel.Connectors[1];
            Assert.Equal("hermes", hermes.Name);
            Assert.Equal("not running", hermes.Counts);
            Assert.Equal(string.Empty, hermes.Mode);
            Assert.False(hermes.HasMode);
            Assert.Equal("hermes, not running", hermes.AutomationName);
        });
    }

    [Fact]
    public void A_poll_that_changes_only_the_numbers_keeps_the_connector_rows_it_has()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);
            viewModel.Apply(FlyoutScene.RunningSnapshot());
            var rows = viewModel.Connectors.ToArray();

            viewModel.Apply(FlyoutScene.RunningSnapshot() with { PolledAt = FlyoutScene.Now });

            Assert.True(rows.SequenceEqual(viewModel.Connectors));
        });
    }

    // ------------------------------------------------------------------ the numbers

    [Fact]
    public void Showing_the_flyout_reads_the_audit_window_and_the_findings()
    {
        using var scene = new FlyoutScene();
        scene.Populate(hookCalls: 46, blocks: 4, other: 150, findings: 12);
        var viewModel = UiThread.Run(() => Create(scene));

        try
        {
            UiThread.Run(() => viewModel.SetVisible(true));
            WaitForNumbers(viewModel);

            UiThread.Run(() =>
            {
                var hooks = viewModel.Metrics[0];
                Assert.Equal("Hook Calls", hooks.Title);
                Assert.Equal("46", hooks.Value);
                Assert.Equal("latest 500 audit events", hooks.Detail);
                Assert.Equal("Accent", hooks.BarTone);
                Assert.Equal("Primary", hooks.ValueTone);
                Assert.True(hooks.Fill.Value > 0);
                Assert.Equal("Hook Calls 46, latest 500 audit events. Opens Logs.", hooks.AutomationName);

                var blocks = viewModel.Metrics[1];
                Assert.Equal("Blocks", blocks.Title);
                Assert.Equal("4", blocks.Value);
                Assert.Equal("latest 500 decisions · 9% block rate", blocks.Detail);
                Assert.Equal("Bad", blocks.BarTone);
                Assert.Equal("Bad", blocks.ValueTone);

                var findings = viewModel.Metrics[2];
                Assert.Equal("Findings", findings.Title);
                Assert.Equal("12", findings.Value);
                Assert.Equal("unacknowledged", findings.Detail);
                Assert.Equal("Warn", findings.BarTone);
                Assert.Equal("Warn", findings.ValueTone);
                Assert.Equal("Findings 12, unacknowledged. Opens Alerts.", findings.AutomationName);

                Assert.Equal("Updated just now", viewModel.UpdatedText);
            });
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    [Fact]
    public void The_recent_list_is_the_newest_five_unacknowledged_findings_with_their_kind_severity_and_age()
    {
        using var scene = new FlyoutScene();
        scene.Populate(hookCalls: 2, blocks: 0, other: 2, findings: 12);
        scene.Audit.Acknowledge("finding-000");
        var viewModel = UiThread.Run(() => Create(scene));

        try
        {
            UiThread.Run(() => viewModel.SetVisible(true));
            WaitForNumbers(viewModel);

            UiThread.Run(() =>
            {
                Assert.Equal("11", viewModel.Metrics[2].Value);
                Assert.Equal(TrayFlyoutViewModel.RecentFindingLimit, viewModel.RecentFindings.Count);
                Assert.True(viewModel.HasFindings);

                // finding-000 was the newest and is acknowledged, so it is gone; the list starts at 001 (MEDIUM, the pattern's second severity).
                Assert.Equal(new[] { "finding-001", "finding-002", "finding-003", "finding-004", "finding-005" }, viewModel.RecentFindings.Select(f => f.Id));

                var first = viewModel.RecentFindings[0];
                Assert.Equal("scan-finding", first.Kind);
                Assert.Equal("Medium", first.Severity);
                Assert.Equal("Medium", first.Tone);
                Assert.Equal("3w ago", first.When);
                Assert.Equal("Medium scan-finding, 3w ago. Opens Alerts.", first.AutomationName);
                Assert.Contains("/synthetic/finding-001", first.ToolTipText, StringComparison.Ordinal);
            });
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    [Fact]
    public void A_full_window_of_findings_reads_with_a_plus_like_the_badge()
    {
        using var scene = new FlyoutScene();
        scene.AddFindingsInBulk(520, FlyoutScene.Now.AddDays(-2));
        var viewModel = UiThread.Run(() => Create(scene));

        try
        {
            UiThread.Run(() => viewModel.SetVisible(true));
            WaitForNumbers(viewModel);

            UiThread.Run(() => Assert.Equal(N0(500) + "+", viewModel.Metrics[2].Value));
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    [Fact]
    public void Nothing_waiting_is_a_quiet_green_row_and_a_line_that_says_so()
    {
        using var scene = new FlyoutScene();
        var viewModel = UiThread.Run(() => Create(scene));

        try
        {
            UiThread.Run(() => viewModel.SetVisible(true));
            WaitForNumbers(viewModel);

            UiThread.Run(() =>
            {
                Assert.Equal("0", viewModel.Metrics[2].Value);
                Assert.Equal("Ok", viewModel.Metrics[2].BarTone);
                Assert.Equal("Neutral", viewModel.Metrics[2].ValueTone);
                Assert.Equal(0, viewModel.Metrics[2].Fill.Value);
                Assert.False(viewModel.HasFindings);
                Assert.Equal("No unacknowledged findings", viewModel.FindingsEmptyText);
                Assert.Equal("0", viewModel.Metrics[0].Value);
                Assert.Equal("recent block decisions", viewModel.Metrics[1].Detail.Split(" · ")[^1]);
            });
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    [Fact]
    public void A_database_that_cannot_be_read_is_unavailable_not_zero()
    {
        using var scene = new FlyoutScene();
        SqlitePools.Release(scene.AuditPath);
        File.WriteAllText(scene.AuditPath, "this is not a database");
        var viewModel = UiThread.Run(() => Create(scene));

        try
        {
            UiThread.Run(() => viewModel.SetVisible(true));
            UiThread.WaitFor(() => viewModel.Metrics[0].Detail == "audit database unavailable", "the failed audit read to show");

            UiThread.Run(() =>
            {
                Assert.Equal("—", viewModel.Metrics[0].Value);
                Assert.Equal("—", viewModel.Metrics[1].Value);
                Assert.Equal("audit database unavailable", viewModel.Metrics[1].Detail);
            });

            UiThread.WaitFor(() => viewModel.Metrics[2].Detail == "unavailable", "the alert counts to report unavailable");
            UiThread.Run(() =>
            {
                Assert.Equal("—", viewModel.Metrics[2].Value);
                Assert.Equal("Unacknowledged findings are unavailable", viewModel.FindingsEmptyText);
            });
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    // ------------------------------------------------------------------ only while it is on screen and monitoring runs

    [Fact]
    public void A_hidden_flyout_reads_nothing_and_holds_no_subscription_that_reads()
    {
        using var scene = new FlyoutScene();
        scene.Populate();

        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);
            viewModel.Apply(FlyoutScene.RunningSnapshot());
            viewModel.Apply(FlyoutScene.RunningSnapshot() with { PolledAt = FlyoutScene.Now });

            Assert.False(viewModel.IsLive);
            Assert.Equal(0, viewModel.MetricsReader.ReadCount);
            Assert.Equal(0, scene.Services.AlertQueue.ReadCount);
            Assert.False(scene.Services.AlertCounts.IsRunning);
        });
    }

    [Fact]
    public void Showing_starts_the_reads_and_hiding_stops_them()
    {
        using var scene = new FlyoutScene();
        scene.Populate();
        var viewModel = UiThread.Run(() => Create(scene));

        try
        {
            UiThread.Run(() => viewModel.SetVisible(true));
            Assert.True(UiThread.Run(() => viewModel.IsLive));
            Assert.True(scene.Services.AlertCounts.IsRunning);
            WaitForNumbers(viewModel);

            var reads = viewModel.MetricsReader.ReadCount;
            Assert.Equal(1, reads);

            UiThread.Run(() => viewModel.SetVisible(false));
            Assert.False(UiThread.Run(() => viewModel.IsLive));

            // The last subscriber left, so the counts service is detached from the monitor and idle too.
            Assert.False(scene.Services.AlertCounts.IsRunning);
            Assert.Equal(reads, viewModel.MetricsReader.ReadCount);
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    [Fact]
    public void Pausing_stops_the_reads_keeps_the_last_numbers_and_says_how_old_they_are()
    {
        using var scene = new FlyoutScene();
        scene.Populate();
        var clock = new ManualClock();
        var viewModel = UiThread.Run(() => new TrayFlyoutViewModel(scene.Services, () => { }, () => { }, metricsReader: null, timeProvider: clock));

        try
        {
            UiThread.Run(() => viewModel.SetVisible(true));
            WaitForNumbers(viewModel);
            var reads = viewModel.MetricsReader.ReadCount;
            var queueReads = scene.Services.AlertQueue.ReadCount;

            clock.Advance(TimeSpan.FromMinutes(3));
            UiThread.Run(() => viewModel.TogglePauseCommand.Execute(null));

            UiThread.Run(() =>
            {
                Assert.True(scene.Services.Monitor.IsPaused);
                Assert.True(viewModel.IsPaused);
                Assert.False(viewModel.IsLive);
                Assert.Equal("Paused", viewModel.StateLabel);
                Assert.Equal("Neutral", viewModel.StateTone);
                Assert.Equal("Monitoring paused", viewModel.HeaderCaption);
                Assert.Equal("Resume", viewModel.PauseText);
                Assert.Equal("Resume monitoring", viewModel.PauseAutomationName);
                Assert.Equal("Monitoring paused · counts as of 3m ago", viewModel.UpdatedText);

                // The last numbers stay on screen.
                Assert.Equal("46", viewModel.Metrics[0].Value);
                Assert.Equal("12", viewModel.Metrics[2].Value);
            });
            Assert.False(scene.Services.AlertCounts.IsRunning);

            // Hidden and shown again while paused: still nothing is read.
            UiThread.Run(() =>
            {
                viewModel.SetVisible(false);
                viewModel.SetVisible(true);
                Assert.False(viewModel.IsLive);
            });
            Assert.Equal(reads, viewModel.MetricsReader.ReadCount);
            Assert.Equal(queueReads, scene.Services.AlertQueue.ReadCount);
            Assert.False(scene.Services.AlertCounts.IsRunning);
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    [Fact]
    public void An_app_that_starts_paused_shows_no_numbers_it_never_read_and_says_why()
    {
        using var scene = new FlyoutScene();
        scene.Populate();
        _ = scene.Services.Monitor.SetPaused(true);

        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);
            viewModel.SetVisible(true);

            Assert.True(viewModel.IsPaused);
            Assert.False(viewModel.IsLive);
            Assert.Equal("Monitoring paused", viewModel.HeaderCaption);
            Assert.Equal("Monitoring paused · no counts read", viewModel.UpdatedText);
            Assert.Equal("Monitoring is paused; findings have not been read", viewModel.FindingsEmptyText);
            Assert.All(viewModel.Metrics, m => Assert.Equal("—", m.Value));
            Assert.Equal(0, viewModel.MetricsReader.ReadCount);
            Assert.Equal(0, scene.Services.AlertQueue.ReadCount);
        });
    }

    [Fact]
    public void Resuming_starts_the_reads_again_and_the_monitor_is_told_to_poll()
    {
        using var scene = new FlyoutScene();
        scene.Populate();
        var viewModel = UiThread.Run(() => Create(scene));

        try
        {
            UiThread.Run(() =>
            {
                viewModel.SetVisible(true);
                viewModel.TogglePauseCommand.Execute(null);
            });
            Assert.False(UiThread.Run(() => viewModel.IsLive));
            var reads = viewModel.MetricsReader.ReadCount;

            UiThread.Run(() => viewModel.TogglePauseCommand.Execute(null));

            UiThread.Run(() =>
            {
                Assert.False(scene.Services.Monitor.IsPaused);
                Assert.False(viewModel.IsPaused);
                Assert.True(viewModel.IsLive);
                Assert.Equal("Pause", viewModel.PauseText);
                Assert.NotEqual("Paused", viewModel.StateLabel);
            });
            UiThread.WaitFor(() => viewModel.MetricsReader.ReadCount > reads, "the read a resume starts");
            WaitForNumbers(viewModel);
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    [Fact]
    public void Pausing_is_the_apps_own_and_persists()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);

            viewModel.TogglePauseCommand.Execute(null);

            Assert.True(scene.Services.Settings.Current.Monitoring.Paused);
            Assert.True(AppSettingsStore.OpenFresh(scene.Services.Settings.FilePath).Current.Monitoring.Paused);
        });
    }

    // ------------------------------------------------------------------ where each row goes

    [Fact]
    public void Each_metric_row_opens_the_dashboard_on_the_panel_that_explains_its_number()
    {
        using var scene = new FlyoutScene();
        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);
            var closed = 0;
            viewModel.CloseRequested += (_, _) => closed++;

            viewModel.Metrics[0].OpenCommand.Execute(null);
            Assert.Equal(new NavigationRequest("logs", new LogsPreset("hooks")), scene.Services.Navigation.Pending);

            viewModel.Metrics[1].OpenCommand.Execute(null);
            Assert.Equal(new NavigationRequest("audit", new AuditPreset("blocks")), scene.Services.Navigation.Pending);

            viewModel.Metrics[2].OpenCommand.Execute(null);
            Assert.Equal(new NavigationRequest("alerts", new AlertsFilter(null, AlertsFilter.KindAll)), scene.Services.Navigation.Pending);

            // The flyout gets out of the way for each.
            Assert.Equal(3, closed);
        });
    }

    [Fact]
    public void A_finding_and_the_review_link_open_alerts_and_the_gear_opens_settings()
    {
        using var scene = new FlyoutScene();
        scene.Populate(hookCalls: 1, blocks: 0, other: 1, findings: 3);
        var viewModel = UiThread.Run(() => Create(scene));

        try
        {
            UiThread.Run(() => viewModel.SetVisible(true));
            WaitForNumbers(viewModel);

            UiThread.Run(() =>
            {
                var closed = 0;
                viewModel.CloseRequested += (_, _) => closed++;

                viewModel.RecentFindings[0].OpenCommand.Execute(null);
                Assert.Equal(new NavigationRequest("alerts", new AlertsFilter(null, AlertsFilter.KindAll)), scene.Services.Navigation.Pending);

                // "Review acknowledge…" opens Alerts, where the reviewed acknowledge flow is; it asks for no filter and changes nothing.
                viewModel.ReviewAcknowledgeCommand.Execute(null);
                Assert.Equal(new NavigationRequest("alerts", new AlertsFilter()), scene.Services.Navigation.Pending);

                // The gear is the dashboard on its Settings page: a navigation request (the app shows the window for it), no payload.
                viewModel.OpenSettingsCommand.Execute(null);
                Assert.Equal(new NavigationRequest("settings", null), scene.Services.Navigation.Pending);

                Assert.Equal(3, closed);
            });
        }
        finally
        {
            UiThread.Run(viewModel.Dispose);
        }
    }

    [Fact]
    public void Open_dashboard_and_exit_run_the_actions_they_were_given()
    {
        using var scene = new FlyoutScene();
        var opened = 0;
        var exited = 0;

        UiThread.Run(() =>
        {
            using var viewModel = Create(scene, () => opened++, () => exited++);

            viewModel.OpenDashboardCommand.Execute(null);
            viewModel.ExitCommand.Execute(null);

            Assert.Equal(1, opened);
            Assert.Equal(1, exited);
        });
    }

    [Fact]
    public async Task Reviewing_acknowledgements_acknowledges_nothing()
    {
        using var scene = new FlyoutScene();
        scene.Populate(hookCalls: 1, blocks: 0, other: 1, findings: 4);

        UiThread.Run(() =>
        {
            using var viewModel = Create(scene);
            viewModel.ReviewAcknowledgeCommand.Execute(null);
        });

        Assert.Equal(0, AcknowledgedRows(scene));
        Assert.Equal(4, (await scene.Services.AlertQueue.ReadAsync()).Counts.Total);
    }

    private static long AcknowledgedRows(FlyoutScene scene)
    {
        using var connection = new SqliteConnection($"Data Source={scene.AuditPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alert_acknowledgement_projection";
        return (long)command.ExecuteScalar()!;
    }
}
