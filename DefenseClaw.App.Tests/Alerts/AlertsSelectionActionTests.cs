using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// Acknowledge selection / Dismiss selection / Dismiss filtered name exact ids (the TUI's <c>_alert_id_command_args</c>), never the
/// whole severity class the old flow took: one HIGH alert of forty selected means one <c>--id</c> on the command line. The CLI is the
/// injectable <see cref="AlertsPanelViewModel.RunCli"/>; nothing here starts a process.
/// </summary>
public sealed class AlertsSelectionActionTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly AlertQueueDatabase _database;

    public AlertsSelectionActionTests()
    {
        _services = TestServices.Create(_temp);
        _database = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private void Finding(string id, int minute, string severity)
    {
        using var connection = new SqliteConnection($"Data Source={_services.Paths.AuditDatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, bucket, connector, event_name)
            VALUES ($id, $timestamp, 'scan-finding', $target, 'audit_logger', $severity, 'security.finding', 'claudecode', 'finding.observed')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", AlertQueueDatabase.Format(Base.AddMinutes(minute)));
        command.Parameters.AddWithValue("$target", "/synthetic/" + id);
        command.Parameters.AddWithValue("$severity", severity);
        _ = command.ExecuteNonQuery();
    }

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

    private static string[] IdsIn(string[] argv) =>
        argv.Select((a, i) => (a, i)).Where(x => x.a == "--id").Select(x => argv[x.i + 1]).ToArray();

    /// <summary>What the CLI prints for an <c>--id</c> command: the matched block, then (for the real run) the result line.</summary>
    private static CliInvocation Answer(string[] argv, string verbed, Action<string>? onApply = null)
    {
        var ids = IdsIn(argv);
        var lines = new List<string> { $"Preview: {ids.Length} alert(s) matched; digest=sha256:v1:0123" };
        lines.AddRange(ids.Take(20).Select(id => $"  {id} version=0"));
        if (argv.Contains("--dry-run"))
        {
            return Invocation(0, lines.ToArray());
        }

        foreach (var id in ids)
        {
            onApply?.Invoke(id);
        }

        lines.Add($"  OK {verbed} {ids.Length} alert(s).");
        return Invocation(0, lines.ToArray());
    }

    private static AlertItem Row(string id, string severity = "HIGH") =>
        AlertItem.FromQueue(new AlertQueueItem(id, severity == "HIGH" ? AuditSeverity.High : AuditSeverity.Low, "scan-finding", "/synthetic/" + id, "claudecode", Base));

    // ------------------------------------------------------------------ the argv

    [Fact]
    public void The_id_command_is_sorted_unique_and_confirms_only_when_there_is_more_than_one()
    {
        Assert.Equal(new[] { "alerts", "dismiss", "--id", "a" }, AlertsPanelViewModel.AlertIdCommandArgs("dismiss", new[] { "a", "a" }));
        Assert.Equal(
            new[] { "alerts", "acknowledge", "--id", "a", "--id", "b", "--yes" },
            AlertsPanelViewModel.AlertIdCommandArgs("acknowledge", new[] { "b", "a", "b" }));
        Assert.Equal(
            new[] { "alerts", "acknowledge", "--id", "a", "--id", "b" },
            AlertsPanelViewModel.AlertIdCommandArgs("acknowledge", new[] { "b", "a" }, withYes: false));
    }

    [Fact]
    public void Selecting_one_of_forty_high_alerts_acknowledges_exactly_that_one()
    {
        for (var i = 1; i <= 40; i++)
        {
            Finding($"high-{i:D2}", i, "HIGH");
        }

        StaThread.Run(async () =>
        {
            var calls = new List<string[]>();
            var vm = new AlertsPanelViewModel(_services)
            {
                RunCli = (argv, _) =>
                {
                    calls.Add(argv.ToArray());
                    return Task.FromResult(Answer(argv.ToArray(), "Acknowledged", id => _database.Acknowledge(id)));
                },
            };
            await vm.InitializeAsync();
            Assert.Equal(40, vm.Alerts.Count);

            var picked = vm.Alerts.Single(a => a.Key == "high-17");
            vm.NoteSelection(new[] { picked });
            Assert.Equal("1 selected", vm.SelectionText);

            await vm.OpenAcknowledgeSelectionCommand.ExecuteAsync(null);

            // The preview is the same command with --dry-run: one --id, no selector, nothing to confirm.
            Assert.Equal(new[] { "alerts", "acknowledge", "--id", "high-17", "--dry-run" }, Assert.Single(calls));
            Assert.Equal("defenseclaw alerts acknowledge --id high-17", vm.ConfirmCommandText);
            Assert.False(vm.IsSeverityReview);

            await vm.ConfirmReviewCommand.ExecuteAsync(null);

            Assert.Equal(2, calls.Count);
            Assert.Equal(new[] { "alerts", "acknowledge", "--id", "high-17" }, calls[1]);
            Assert.DoesNotContain(calls, c => c.Contains("--severity") || c.Contains("--before"));

            // Only that row left the list.
            Assert.Equal(39, vm.Alerts.Count);
            Assert.DoesNotContain(vm.Alerts, a => a.Key == "high-17");
            Assert.False(vm.IsReviewOpen);
        });
    }

    [Fact]
    public void Several_selected_alerts_are_named_sorted_and_the_apply_carries_yes_the_preview_does_not()
    {
        foreach (var id in new[] { "c", "a", "b", "d" })
        {
            Finding(id, 1, "HIGH");
        }

        StaThread.Run(async () =>
        {
            var calls = new List<string[]>();
            var vm = new AlertsPanelViewModel(_services)
            {
                RunCli = (argv, _) =>
                {
                    calls.Add(argv.ToArray());
                    return Task.FromResult(Answer(argv.ToArray(), "Dismissed", id => _database.Acknowledge(id)));
                },
            };
            await vm.InitializeAsync();

            vm.NoteSelection(vm.Alerts.Where(a => a.Key is "c" or "a" or "b").ToList());
            await vm.OpenDismissSelectionCommand.ExecuteAsync(null);
            Assert.True(vm.IsDestructive);
            await vm.ConfirmReviewCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "alerts", "dismiss", "--id", "a", "--id", "b", "--id", "c", "--dry-run" }, calls[0]);
            Assert.Equal(new[] { "alerts", "dismiss", "--id", "a", "--id", "b", "--id", "c", "--yes" }, calls[1]);
            Assert.Equal(new[] { "d" }, vm.Alerts.Select(a => a.Key).ToArray());
        });
    }

    // ------------------------------------------------------------------ dismiss filtered

    [Fact]
    public void Dismiss_filtered_names_exactly_the_rows_on_screen()
    {
        Finding("crit-1", 1, "CRITICAL");
        Finding("high-1", 2, "HIGH");
        Finding("high-2", 3, "HIGH");
        Finding("low-1", 4, "LOW");

        StaThread.Run(async () =>
        {
            var calls = new List<string[]>();
            var vm = new AlertsPanelViewModel(_services)
            {
                RunCli = (argv, _) =>
                {
                    calls.Add(argv.ToArray());
                    return Task.FromResult(Answer(argv.ToArray(), "Dismissed", id => _database.Acknowledge(id)));
                },
            };
            await vm.InitializeAsync();
            vm.CollapseRepeats = false;

            // Only High is showing.
            vm.SelectSeverityCommand.Execute(vm.SeverityFilters.Single(f => f.Severity == "HIGH"));
            Assert.Equal(2, vm.Alerts.Count);
            Assert.Equal("Dismiss filtered (2)", vm.FilteredText);

            await vm.OpenDismissFilteredCommand.ExecuteAsync(null);

            Assert.Equal(
                vm.Alerts.Select(a => a.Key).Order(StringComparer.Ordinal).ToArray(),
                IdsIn(calls[0]));
            Assert.Equal("Dismiss 2 filtered alerts", vm.ReviewHeading);

            await vm.ConfirmReviewCommand.ExecuteAsync(null);

            // The filtered rows went; the other severities are untouched.
            Assert.Empty(vm.Alerts);
            vm.SelectSeverityCommand.Execute(vm.SeverityFilters.Single(f => f.Severity == "HIGH"));
            Assert.Equal(new[] { "crit-1", "low-1" }, vm.Alerts.Select(a => a.Key).Order(StringComparer.Ordinal).ToArray());
        });
    }

    // ------------------------------------------------------------------ rows the CLI cannot name

    [Fact]
    public void Gateway_only_rows_are_left_out_and_the_review_says_so()
    {
        StaThread.Run(async () =>
        {
            var calls = new List<string[]>();
            var vm = new AlertsPanelViewModel(_services)
            {
                RunCli = (argv, _) =>
                {
                    calls.Add(argv.ToArray());
                    return Task.FromResult(Answer(argv.ToArray(), "Acknowledged"));
                },
                AfterApply = () => Task.CompletedTask,
            };
            var gateway = AlertItem.FromGateway(new GatewayAlert { Id = "gw:only", Severity = "HIGH", Timestamp = Base });
            Assert.False(gateway.HasAuditId);

            vm.NoteSelection(new[] { gateway, Row("real-1") });
            Assert.Equal("1 selected (1 gateway-only, no audit id)", vm.SelectionText);

            await vm.OpenAcknowledgeSelectionCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "alerts", "acknowledge", "--id", "real-1", "--dry-run" }, Assert.Single(calls));
            Assert.Contains("1 selected gateway-only row(s) have no audit id", vm.ReviewIntro, StringComparison.Ordinal);
            vm.CancelReviewCommand.Execute(null);

            // Nothing but gateway-only rows: no review, no command, a plain sentence.
            calls.Clear();
            vm.NoteSelection(new[] { gateway });
            await vm.OpenDismissSelectionCommand.ExecuteAsync(null);
            Assert.Empty(calls);
            Assert.False(vm.IsReviewOpen);
            Assert.True(vm.ShowActionError);
            Assert.Contains("gateway-only", vm.ActionBannerText, StringComparison.Ordinal);
        });
    }

    // ------------------------------------------------------------------ chunking

    [Fact]
    public void A_big_selection_becomes_several_reviewed_runs_of_at_most_two_hundred_ids()
    {
        StaThread.Run(async () =>
        {
            var calls = new List<string[]>();
            var vm = new AlertsPanelViewModel(_services)
            {
                RunCli = (argv, _) =>
                {
                    calls.Add(argv.ToArray());
                    return Task.FromResult(Answer(argv.ToArray(), "Dismissed"));
                },
                AfterApply = () => Task.CompletedTask,
            };
            vm.NoteSelection(Enumerable.Range(1, 450).Select(i => Row($"id-{i:D3}")).ToList());
            Assert.Equal("450 selected", vm.SelectionText);

            await vm.OpenDismissSelectionCommand.ExecuteAsync(null);

            Assert.True(vm.HasChunkText);
            Assert.Contains("Run 1 of 3", vm.ChunkText, StringComparison.Ordinal);
            Assert.Equal(200, IdsIn(calls[0]).Length);
            Assert.DoesNotContain("--yes", calls[0]);

            await vm.ConfirmReviewCommand.ExecuteAsync(null);

            // The first run is applied, the dialog stays open on the second run's own preview.
            Assert.True(vm.IsReviewOpen);
            Assert.Contains("Run 2 of 3", vm.ChunkText, StringComparison.Ordinal);
            Assert.Equal(200, IdsIn(calls[1]).Length);
            Assert.Contains("--yes", calls[1]);
            Assert.Equal(200, IdsIn(calls[2]).Length);
            Assert.Contains("--dry-run", calls[2]);
            Assert.DoesNotContain("id-001", IdsIn(calls[2]));

            await vm.ConfirmReviewCommand.ExecuteAsync(null);
            Assert.True(vm.IsReviewOpen);
            Assert.Equal(50, IdsIn(calls[4]).Length);

            await vm.ConfirmReviewCommand.ExecuteAsync(null);
            Assert.False(vm.IsReviewOpen);
            Assert.Equal(6, calls.Count);
            Assert.Equal(450, calls.Where(c => !c.Contains("--dry-run")).Sum(c => IdsIn(c).Length));
            Assert.Contains("450 alert(s) in 3 runs", vm.ActionBannerText, StringComparison.Ordinal);
            Assert.All(calls.Where(c => !c.Contains("--dry-run")), c => Assert.True(IdsIn(c).Length <= AlertsPanelViewModel.IdChunkSize));
        });
    }

    // ------------------------------------------------------------------ the preview gate is unchanged

    [Fact]
    public void A_failed_preview_leaves_the_confirm_disabled_for_an_id_review_too()
    {
        StaThread.Run(async () =>
        {
            var calls = new List<string[]>();
            var vm = new AlertsPanelViewModel(_services)
            {
                RunCli = (argv, _) =>
                {
                    calls.Add(argv.ToArray());
                    return Task.FromResult(Invocation(1, "Error: gateway unavailable"));
                },
            };
            vm.NoteSelection(new[] { Row("x-1") });

            await vm.OpenAcknowledgeSelectionCommand.ExecuteAsync(null);

            Assert.False(vm.CanConfirmReview);
            Assert.True(vm.HasReviewError);
            await vm.ConfirmReviewCommand.ExecuteAsync(null);
            Assert.Single(calls);
        });
    }
}
