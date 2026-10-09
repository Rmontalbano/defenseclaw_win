using System.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.PendingRestart;

/// <summary>
/// The three places the restart queue is shown (CUST-267) - the Setup hub's banner, the readiness checklist's "Restart Pending" row and the Overview's
/// attention row - and the two buttons they share: <b>Restart now</b>, the reviewed <c>defenseclaw-gateway restart</c>, off with the installation's
/// sentence first; and <b>Clear</b>. The queue is the real <c>Services.RestartQueue</c> of a synthetic install whose gateway port nothing listens on, so
/// no test reaches a real gateway; nothing starts a process (the review's runner is a seam).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RestartQueueSurfacesTests : IDisposable
{
    private const string Reason = "config.yaml saved in the config editor (guardrail)";

    private readonly OverviewScene _scene = OverviewScene.Create(seedAudit: false, seedAgents: false);

    public void Dispose() => _scene.Dispose();

    private AppServices Services => _scene.Services;

    /// <summary>What a poll reports after a restart: a start a moment from now, which is after anything queued so far.</summary>
    private static GatewaySnapshot Restarted() =>
        OverviewScene.Snapshot(health: new GatewayHealth { StartedAt = DateTimeOffset.UtcNow.AddSeconds(1), UptimeMs = 1_000 }) with
        {
            PolledAt = DateTimeOffset.UtcNow.AddSeconds(2),
        };

    /// <summary>The Setup hub, active, with the gateway known to be running (so Restart now is not waiting on the first poll).</summary>
    private SetupPanelViewModel ActiveSetup()
    {
        _scene.Publish(OverviewScene.Snapshot());
        return UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(Services);
            vm.ShowCards(Array.Empty<DefenseClaw.App.Services.Wizards.WizardDefinition>());
            vm.SetActive(true);
            return vm;
        });
    }

    /// <summary>Lets what activation started (the credential read, the guardrail read) finish, so nothing outlives the composition.</summary>
    private static void Settled(SetupPanelViewModel vm)
    {
        UiThread.Run(() => vm.SetActive(false));
        UiThread.WaitFor(() => !vm.Credentials.IsLoading && !vm.IsGuardrailBusy, "the Setup hub's reads finished");
    }

    private ReadinessRowViewModel RestartRow(SetupPanelViewModel vm) => vm.Readiness.Rows.Single(r => r.Title == "Restart Pending");

    // ------------------------------------------------------------------ the Setup hub's banner and its readiness row

    [Fact]
    public void A_queued_restart_draws_the_banner_with_the_reasons_and_the_time_and_the_readiness_row_says_the_same()
    {
        var vm = ActiveSetup();
        try
        {
            UiThread.Run(() =>
            {
                Assert.False(vm.HasRestartPending);
                Assert.Equal(ReadinessStatus.Pass, RestartRow(vm).Status);

                var changed = new List<string?>();
                ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

                Assert.True(Services.RestartQueue.Queue(Reason));

                // The queue's event reaches the page while it is on screen: the banner and the checklist are drawn again.
                Assert.Contains(nameof(SetupPanelViewModel.HasRestartPending), changed);
                Assert.Contains(nameof(SetupPanelViewModel.RestartPendingDetail), changed);
                Assert.True(vm.HasRestartPending);
                Assert.Equal("Gateway restart pending", vm.RestartPendingTitle);
                Assert.StartsWith(Reason + ". Queued ", vm.RestartPendingDetail, StringComparison.Ordinal);
                Assert.EndsWith(RestartQueueText.Consequence, vm.RestartPendingDetail, StringComparison.Ordinal);

                // The TUI's row: warn, the reasons as its detail, and a Fix that is the reviewed restart.
                var row = RestartRow(vm);
                Assert.Equal(ReadinessStatus.Warn, row.Status);
                Assert.Equal(Reason, row.Detail);
                Assert.True(row.HasFix);
                Assert.StartsWith("Review, then run: defenseclaw-gateway restart", row.FixToolTip, StringComparison.Ordinal);
            });
        }
        finally
        {
            Settled(vm);
        }
    }

    [Fact]
    public void The_banner_persists_across_panel_switches_and_a_line_queued_while_the_panel_was_away_is_there_when_it_returns()
    {
        var vm = ActiveSetup();
        try
        {
            UiThread.Run(() =>
            {
                _ = Services.RestartQueue.Queue(Reason);
                Assert.True(vm.HasRestartPending);

                // The operator goes to another panel: this one is deactivated, and a save somewhere else adds a line.
                vm.SetActive(false);
                _ = Services.RestartQueue.Queue("guardrail hilt ran with --no-restart");

                // ... and comes back: nothing was copied, so nothing is missing.
                vm.SetActive(true);
                Assert.True(vm.HasRestartPending);
                Assert.Contains("guardrail hilt ran with --no-restart", vm.RestartPendingDetail, StringComparison.Ordinal);
                Assert.Equal(Reason + "; guardrail hilt ran with --no-restart", RestartRow(vm).Detail);
                Assert.True(vm.ClearRestartCommand.CanExecute(null));
            });
        }
        finally
        {
            Settled(vm);
        }
    }

    [Fact]
    public void A_restart_by_any_route_takes_the_banner_and_the_row_down_when_the_monitor_polls()
    {
        var vm = ActiveSetup();
        try
        {
            UiThread.Run(() =>
            {
                _ = Services.RestartQueue.Queue(Reason);
                Assert.True(vm.HasRestartPending);

                // Nobody pressed anything here: a terminal, the tray or another app restarted the gateway, and the next poll reports a new start.
                _scene.Publish(Restarted());

                Assert.False(Services.RestartQueue.IsPending);
                Assert.False(vm.HasRestartPending);
                Assert.Equal(string.Empty, vm.RestartPendingDetail);
                Assert.Equal(ReadinessStatus.Pass, RestartRow(vm).Status);
                Assert.Equal("No queued restart.", RestartRow(vm).Detail);
                Assert.False(vm.RestartNowCommand.CanExecute(null));
                Assert.False(vm.ClearRestartCommand.CanExecute(null));
            });
        }
        finally
        {
            Settled(vm);
        }
    }

    [Fact]
    public void Clear_forgets_the_queue_and_runs_nothing()
    {
        var vm = ActiveSetup();
        try
        {
            UiThread.Run(() =>
            {
                Assert.False(vm.ClearRestartCommand.CanExecute(null));
                _ = Services.RestartQueue.Queue(Reason);
                Assert.True(vm.ClearRestartCommand.CanExecute(null));

                vm.ClearRestartCommand.Execute(null);

                Assert.False(Services.RestartQueue.IsPending);
                Assert.False(vm.HasRestartPending);
                Assert.Equal(ReadinessStatus.Pass, RestartRow(vm).Status);
                Assert.Empty(Services.Cli.Activity);
                Assert.False(vm.Review.IsOpen);
            });
        }
        finally
        {
            Settled(vm);
        }
    }

    [Fact]
    public void While_the_page_is_not_on_screen_it_listens_to_nothing()
    {
        var vm = ActiveSetup();
        Settled(vm);

        UiThread.Run(() =>
        {
            var changed = new List<string?>();
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            _ = Services.RestartQueue.Queue(Reason);

            Assert.DoesNotContain(nameof(SetupPanelViewModel.HasRestartPending), changed);

            // It reads the queue, so what it says is right the moment it is asked.
            Assert.True(vm.HasRestartPending);
        });
    }

    // ------------------------------------------------------------------ Restart now (the Setup hub)

    [Fact]
    public void Restart_now_opens_the_review_of_defenseclaw_gateway_restart_and_runs_nothing_until_it_is_confirmed()
    {
        var vm = ActiveSetup();
        try
        {
            UiThread.Run(() =>
            {
                Assert.False(vm.RestartNowCommand.CanExecute(null)); // nothing is queued

                _ = Services.RestartQueue.Queue(Reason);
                Assert.Null(vm.RestartNowBlockedReason);
                Assert.True(vm.RestartNowCommand.CanExecute(null));

                vm.RestartNowCommand.Execute(null);

                Assert.True(vm.Review.IsOpen);
                Assert.False(vm.Review.IsRunning);
                var review = vm.Review.CommandReview!;
                Assert.Equal("Restart the gateway now?", review.Title);
                var step = Assert.Single(review.Steps);
                Assert.Equal("defenseclaw-gateway", step.Executable);
                Assert.Equal(new[] { "restart" }, step.Argv); // never a bare defenseclaw-gateway, which would start a second daemon
                Assert.Equal(CommandTier.StateChanging, step.Tier);
                Assert.True(review.RestartsGateway);
                Assert.Contains(review.Warnings, w => w.Title == "Gateway restart");
                Assert.Contains(Reason, review.Summary, StringComparison.Ordinal);
                Assert.Empty(Services.Cli.Activity);
                Assert.True(Services.RestartQueue.IsPending);
            });
        }
        finally
        {
            Settled(vm);
        }
    }

    [Fact]
    public async Task Confirming_the_review_runs_the_exact_restart_and_the_restart_it_records_applies_the_queue()
    {
        var vm = ActiveSetup();
        var ran = new List<string>();
        try
        {
            UiThread.Run(() =>
            {
                vm.Review.RunStep = (executable, argv, options) =>
                {
                    ran.Add(executable + " " + string.Join(' ', argv));

                    // The runner's record of it, as the runner would hand it to the queue (the seam stands in for the child process).
                    var invocation = InvocationFactory.CreateFor(executable, argv.ToArray());
                    InvocationFactory.Finish(invocation, 0);
                    _ = Services.RestartQueue.Note(invocation);
                    return Task.FromResult(invocation);
                };
                _ = Services.RestartQueue.Queue(Reason);
                vm.RestartNowCommand.Execute(null);
            });

            await UiThread.Run(() => vm.Review.ConfirmCommand.ExecuteAsync(null));

            Assert.Equal(new[] { "defenseclaw-gateway restart" }, ran);
            UiThread.Run(() =>
            {
                Assert.True(vm.Review.IsFinished);
                Assert.Equal("Ok", vm.Review.ResultKey);
                Assert.False(Services.RestartQueue.IsPending);
                Assert.False(vm.HasRestartPending);
            });
        }
        finally
        {
            Settled(vm);
        }
    }

    [Fact]
    public void Restart_now_is_off_with_the_installations_sentence_first_and_opens_nothing_when_reached_anyway()
    {
        var vm = ActiveSetup();
        try
        {
            UiThread.Run(() =>
            {
                _ = Services.RestartQueue.Queue(Reason);

                Services.Installation.Replace(TestInstallations.ManagedAt(_scene.Temp.Path));
                var reason = Services.Installation.BlockedReason!;

                Assert.False(vm.RestartNowCommand.CanExecute(null));
                Assert.Equal(reason, vm.RestartNowBlockedReason);
                Assert.Equal(reason, vm.RestartNowTip);

                // The keyboard route, or a button drawn before the flip: the review is not even offered.
                vm.RestartNowCommand.Execute(null);
                Assert.False(vm.Review.IsOpen);
                Assert.Empty(Services.Cli.Activity);

                // Clear is the app's own list, not a change to DefenseClaw: it stays.
                Assert.True(vm.ClearRestartCommand.CanExecute(null));
                vm.ClearRestartCommand.Execute(null);
                Assert.False(Services.RestartQueue.IsPending);

                // Fixed: the button is back for the next line.
                Services.Installation.Replace(TestInstallations.UserDefault());
                _ = Services.RestartQueue.Queue(Reason);
                Assert.True(vm.RestartNowCommand.CanExecute(null));
            });
        }
        finally
        {
            Settled(vm);
        }
    }

    [Fact]
    public void The_installations_reason_comes_before_the_gateways_own()
    {
        // The monitor has not polled: "still checking" is the writable installation's reason; a read-only one says its own.
        UiThread.Run(() =>
        {
            Assert.Equal("Still checking the gateway; try again in a moment.", RestartQueueReview.BlockedReason(Services));

            Services.Installation.Replace(TestInstallations.ManagedAt(_scene.Temp.Path));
            Assert.Equal(Services.Installation.BlockedReason, RestartQueueReview.BlockedReason(Services));
        });
    }

    [Fact]
    public async Task A_review_confirmed_after_the_installation_turned_read_only_runs_nothing_and_records_one_refusal()
    {
        var vm = ActiveSetup();
        try
        {
            UiThread.Run(() =>
            {
                vm.Review.RunStep = (executable, argv, options) => throw new InvalidOperationException($"ran {executable} {string.Join(' ', argv)}");
                _ = Services.RestartQueue.Queue(Reason);
                vm.RestartNowCommand.Execute(null);
                Assert.True(vm.Review.IsOpen);

                // The review stood open while config.yaml was edited to managed.
                Services.Installation.Replace(TestInstallations.ManagedAt(_scene.Temp.Path));
            });

            await UiThread.Run(() => vm.Review.ConfirmCommand.ExecuteAsync(null));

            UiThread.Run(() =>
            {
                var recorded = Assert.Single(Services.Cli.Activity);
                Assert.Equal(new[] { "restart" }, recorded.Argv);
                Assert.Null(recorded.ExitCode);
                Assert.StartsWith(CliRunner.RefusedPrefix + " — ", recorded.FailureReason, StringComparison.Ordinal);
                Assert.StartsWith("Not run.", vm.Review.ResultText, StringComparison.Ordinal);
                Assert.True(Services.RestartQueue.IsPending);
            });
        }
        finally
        {
            Settled(vm);
        }
    }

    // ------------------------------------------------------------------ the Overview's attention row

    private OverviewPanelViewModel Overview()
    {
        _scene.Publish(OverviewScene.Snapshot());
        return UiThread.Run(() =>
        {
            var vm = new OverviewPanelViewModel(Services);
            vm.Apply(OverviewScene.Snapshot());
            return vm;
        });
    }

    [Fact]
    public void The_overview_lists_the_queued_restart_as_an_attention_row_with_its_two_buttons()
    {
        var vm = Overview();
        UiThread.Run(() =>
        {
            Assert.DoesNotContain(vm.Attention, r => r.OffersRestart);

            _ = Services.RestartQueue.Queue(Reason);
            vm.Apply(OverviewScene.Snapshot());

            var row = Assert.Single(vm.Attention, r => r.OffersRestart);
            Assert.Equal("Gateway restart pending", row.Title);
            Assert.Equal("Medium", row.SeverityKey);
            Assert.StartsWith(Reason + ". Queued ", row.Detail, StringComparison.Ordinal);
            Assert.EndsWith(RestartQueueText.Consequence, row.Detail, StringComparison.Ordinal);
            Assert.False(row.HasCommand);
            Assert.EndsWith("Restart now and Clear are available.", row.ToString(), StringComparison.Ordinal);

            // It is not the "all clear" row, and it is not buried under the other findings.
            Assert.DoesNotContain(vm.Attention, r => r.Title == "Nothing needs attention");
            Assert.Contains(row, vm.VisibleAttention);
        });
    }

    [Fact]
    public void While_the_overview_is_on_screen_the_row_follows_the_queue_without_waiting_for_a_poll()
    {
        var vm = Overview();
        UiThread.Run(() => vm.SetActive(true));
        try
        {
            UiThread.Run(() =>
            {
                _ = Services.RestartQueue.Queue(Reason);
                Assert.Contains(vm.Attention, r => r.OffersRestart);

                vm.ClearPendingRestartCommand.Execute(null);

                Assert.False(Services.RestartQueue.IsPending);
                Assert.DoesNotContain(vm.Attention, r => r.OffersRestart);
            });
        }
        finally
        {
            UiThread.Run(() => vm.SetActive(false));
        }
    }

    [Fact]
    public void A_restart_the_poll_sees_drops_the_row()
    {
        var vm = Overview();
        UiThread.Run(() => vm.SetActive(true));
        try
        {
            UiThread.Run(() =>
            {
                _ = Services.RestartQueue.Queue(Reason);
                Assert.Contains(vm.Attention, r => r.OffersRestart);

                _scene.Publish(Restarted());

                Assert.False(Services.RestartQueue.IsPending);
                Assert.DoesNotContain(vm.Attention, r => r.OffersRestart);
            });
        }
        finally
        {
            UiThread.Run(() => vm.SetActive(false));
        }
    }

    [Fact]
    public void The_overviews_restart_now_is_the_same_reviewed_restart_and_follows_the_installation()
    {
        var vm = Overview();
        UiThread.Run(() =>
        {
            _ = Services.RestartQueue.Queue(Reason);
            vm.Apply(OverviewScene.Snapshot());

            Assert.True(vm.RestartPendingNowCommand.CanExecute(null));
            vm.RestartPendingNowCommand.Execute(null);

            Assert.True(vm.Review.IsOpen);
            var step = Assert.Single(vm.Review.CommandReview!.Steps);
            Assert.Equal("defenseclaw-gateway", step.Executable);
            Assert.Equal(new[] { "restart" }, step.Argv);
            Assert.True(vm.Review.CommandReview.RestartsGateway);
            Assert.Empty(Services.Cli.Activity);
            vm.Review.DismissCommand.Execute(null);

            Services.Installation.Replace(TestInstallations.ManagedAt(_scene.Temp.Path));
            vm.SetActive(true);
            try
            {
                Assert.False(vm.RestartPendingNowCommand.CanExecute(null));
                Assert.Equal(Services.Installation.BlockedReason, vm.RestartPendingNowBlockedReason);
                Assert.Equal(Services.Installation.BlockedReason, vm.RestartPendingNowTip);

                vm.RestartPendingNowCommand.Execute(null);
                Assert.False(vm.Review.IsOpen);
                Assert.True(vm.ClearPendingRestartCommand.CanExecute(null));
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    [Fact]
    public void A_stopped_gateway_still_gets_the_row_and_the_button_because_a_restart_starts_it()
    {
        var vm = Overview();
        UiThread.Run(() =>
        {
            _ = Services.RestartQueue.Queue(Reason);

            // A stopped gateway is initialized and installed: restart starts it, so the row is there and so is the button.
            vm.Apply(OverviewScene.Snapshot(running: false));

            Assert.Contains(vm.Attention, r => r.OffersRestart);
            Assert.Contains(vm.Attention, r => r.Title == "The gateway is not answering");
            Assert.True(vm.RestartPendingNowCommand.CanExecute(null));
        });
    }

    // ------------------------------------------------------------------ one queue, every panel

    [Fact]
    public void A_line_queued_from_any_panel_is_on_every_panel_and_clearing_on_one_clears_them_all()
    {
        var setup = ActiveSetup();
        var overview = Overview();
        try
        {
            UiThread.Run(() =>
            {
                overview.SetActive(true);

                // The config editor, a wizard or a guardrail run: whoever it is, it queues on the app's queue.
                _ = Services.RestartQueue.Queue(Reason);

                Assert.True(setup.HasRestartPending);
                Assert.Contains(overview.Attention, r => r.OffersRestart);
                Assert.Equal(ReadinessStatus.Warn, RestartRow(setup).Status);

                setup.ClearRestartCommand.Execute(null);

                Assert.False(setup.HasRestartPending);
                Assert.DoesNotContain(overview.Attention, r => r.OffersRestart);
                Assert.Equal(ReadinessStatus.Pass, RestartRow(setup).Status);
            });
        }
        finally
        {
            UiThread.Run(() => overview.SetActive(false));
            Settled(setup);
        }
    }
}
