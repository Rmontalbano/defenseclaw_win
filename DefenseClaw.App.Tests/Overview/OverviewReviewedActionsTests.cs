using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The buttons the 0.8.10 TUI's Overview offers by text (CUST-274): Enable AI discovery, Scan, notifications on / off and Fill missing keys. Each is
/// drawn only while it applies, opens the shared review with its exact argv and tier (the keys go to CUST-266's console route instead, which cannot
/// run in-app), is off on a read-only installation with that installation's sentence first, and runs through the runner so it lands in Activity.
/// Synthetic installs only: no process starts, no window opens.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewReviewedActionsTests : IDisposable
{
    private const string Gateway = "gateway:\n  api_port: " + "39871" + "\n";

    private const string AiOn = Gateway + "ai_discovery:\n  enabled: true\n  mode: enhanced\n";

    private const string AiOff = Gateway + "ai_discovery:\n  enabled: false\n";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private AppServices Services => _services!;

    private OverviewPanelViewModel Panel(string config = AiOn, GatewaySnapshot? snapshot = null, InstallationContext? installation = null)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config, installation: installation);
        var vm = new OverviewPanelViewModel(_services);
        var applied = snapshot ?? Running();
        Publish(_services, applied);
        vm.Apply(applied);
        return vm;
    }

    /// <summary>Makes <paramref name="snapshot"/> the monitor's current one, as a poll does: what a panel re-reads after a command, and what the shared connector scope follows.</summary>
    private static void Publish(AppServices services, GatewaySnapshot snapshot) =>
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(services.Monitor, new object[] { snapshot });

    private static GatewayHealth Health(string body = "") =>
        JsonSerializer.Deserialize<GatewayHealth>("{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}" + body + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>A running, initialized gateway that does not run the discovery service.</summary>
    private static GatewaySnapshot Running(GatewayHealth? health = null) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        CliPath = @"C:\Tools\defenseclaw.exe",
        Health = health ?? Health(),
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static GatewaySnapshot Stopped() => new()
    {
        State = AppGatewayState.GatewayStopped,
        Install = InstallState.GatewayStopped,
        Detail = "not answering",
        CliPath = @"C:\Tools\defenseclaw.exe",
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string MissingKeys(params string[] names) =>
        "{\"passed\":3,\"failed\":" + names.Length + ",\"warned\":0,\"skipped\":0,\"captured_at\":\"" +
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\",\"checks\":[" +
        string.Join(",", names.Select(n => "{\"status\":\"fail\",\"label\":\"credential " + n + "\",\"detail\":\"d\"}")) + "]}";

    private async Task LoadDoctorAsync(OverviewPanelViewModel vm, string json)
    {
        _ = _temp.WriteFile("doctor_cache.json", json);
        await vm.ReloadDoctorCacheAsync(CancellationToken.None);
    }

    private static List<(string Executable, string[] Argv, CliRunOptions? Options)> Capture(OverviewPanelViewModel vm)
    {
        var ran = new List<(string, string[], CliRunOptions?)>();
        vm.Review.RunStep = (executable, argv, options) =>
        {
            ran.Add((executable, argv.ToArray(), options));
            var done = InvocationFactory.Create(retainFullOutput: false, argv.ToArray());
            InvocationFactory.Finish(done, 0);
            return Task.FromResult(done);
        };
        return ran;
    }

    // ------------------------------------------------------------------ AI discovery: one of the two, by whether it is on

    [Fact]
    public void While_ai_discovery_is_on_scan_is_offered_and_enable_is_not()
    {
        var vm = Panel(AiOn);

        Assert.True(vm.ShowScanAiDiscovery);
        Assert.False(vm.ShowEnableAiDiscovery);
        Assert.True(vm.ScanAiDiscoveryCommand.CanExecute(null));
        Assert.StartsWith("Ask the running gateway for one AI discovery scan now: defenseclaw agent discovery scan.", vm.ScanAiDiscoveryTip, StringComparison.Ordinal);
        Assert.EndsWith("Asks for confirmation first.", vm.ScanAiDiscoveryTip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Gateway + "ai_discovery:\n  enabled: false\n")]
    [InlineData(Gateway)]
    public void While_it_is_off_or_config_yaml_does_not_say_enable_is_offered_and_scan_is_not(string config)
    {
        var vm = Panel(config);

        Assert.True(vm.ShowEnableAiDiscovery);
        Assert.False(vm.ShowScanAiDiscovery);
        Assert.True(vm.EnableAiDiscoveryCommand.CanExecute(null));
        Assert.Contains("defenseclaw agent discovery enable --yes", vm.EnableAiDiscoveryTip, StringComparison.Ordinal);
        Assert.Contains("restarts the gateway", vm.EnableAiDiscoveryTip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gateway_that_runs_the_service_counts_as_on_even_if_the_file_says_off()
    {
        var vm = Panel(AiOff, Running(Health(",\"ai_discovery\":{\"state\":\"running\"}")));

        Assert.True(vm.ShowScanAiDiscovery);
        Assert.False(vm.ShowEnableAiDiscovery);
    }

    [Fact]
    public void The_button_follows_config_yaml_when_it_is_edited_or_the_command_changes_it()
    {
        var vm = Panel(AiOff);
        Assert.True(vm.ShowEnableAiDiscovery);

        _ = _temp.WriteFile("config.yaml", AiOn);
        Services.ReloadConfig();
        vm.Apply(Running());

        Assert.True(vm.ShowScanAiDiscovery);
        Assert.False(vm.ShowEnableAiDiscovery);
    }

    [Fact]
    public void A_scan_with_the_gateway_down_is_off_with_the_reason_and_enable_still_works()
    {
        var vm = Panel(AiOn, Stopped());

        Assert.True(vm.ShowScanAiDiscovery);
        Assert.False(vm.ScanAiDiscoveryCommand.CanExecute(null));
        Assert.Equal("The gateway is not running, so there is nothing to ask for a scan. Start it first.", vm.ScanAiDiscoveryTip);

        var off = Panel(AiOff, Stopped());
        Assert.True(off.EnableAiDiscoveryCommand.CanExecute(null));
    }

    [Fact]
    public void Enable_opens_the_review_with_the_exact_command_its_tier_and_the_restart_and_runs_nothing()
    {
        var vm = Panel(AiOff);

        vm.EnableAiDiscoveryCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.IsRunning);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Turn on AI discovery?", review.Title);
        Assert.Equal("Turn on", review.ConfirmLabel);
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "agent", "discovery", "enable", "--yes" }, step.Argv);
        Assert.Equal("defenseclaw agent discovery enable --yes", step.CommandText);
        Assert.Equal(CommandReview.DefaultExecutable, step.Executable);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Equal("Changes state", review.TierLabel);
        Assert.True(review.RestartsGateway);
        Assert.Contains(review.Warnings, w => w.Title == CommandReviewWarning.GatewayRestart().Title);
        Assert.Contains("ai_discovery.enabled to true", review.Summary, StringComparison.Ordinal);
        Assert.Contains("asks it for a first scan", review.Summary, StringComparison.Ordinal);
        Assert.Empty(Services.Cli.Activity);
    }

    [Fact]
    public async Task Confirming_enable_runs_it_through_the_runner_with_the_five_minute_allowance_and_reads_the_page_again()
    {
        var vm = Panel(AiOff);
        var ran = Capture(vm);
        vm.EnableAiDiscoveryCommand.Execute(null);

        // The command turns the service on in config.yaml; the page takes the new file when the review finishes.
        _ = _temp.WriteFile("config.yaml", AiOn);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        var call = Assert.Single(ran);
        Assert.Equal(CommandReview.DefaultExecutable, call.Executable);
        Assert.Equal(new[] { "agent", "discovery", "enable", "--yes" }, call.Argv);
        Assert.Equal(TimeSpan.FromMinutes(5), call.Options?.Timeout);
        Assert.True(vm.Review.IsFinished);
        Assert.Equal("Ok", vm.Review.ResultKey);
        Assert.True(vm.ShowScanAiDiscovery);
        Assert.False(vm.ShowEnableAiDiscovery);
    }

    [Fact]
    public void Scan_opens_the_review_with_the_exact_command_and_no_restart()
    {
        var vm = Panel(AiOn);

        vm.ScanAiDiscoveryCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal("Run an AI discovery scan?", review.Title);
        Assert.Equal("Run scan", review.ConfirmLabel);
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "agent", "discovery", "scan" }, step.Argv);
        Assert.Equal("defenseclaw agent discovery scan", step.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.False(review.RestartsGateway);
        Assert.Contains("ai.discovery event", review.Summary, StringComparison.Ordinal);
        Assert.Empty(Services.Cli.Activity);
    }

    [Fact]
    public async Task Confirming_scan_runs_it_with_a_three_minute_allowance()
    {
        var vm = Panel(AiOn);
        var ran = Capture(vm);
        vm.ScanAiDiscoveryCommand.Execute(null);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        var call = Assert.Single(ran);
        Assert.Equal(new[] { "agent", "discovery", "scan" }, call.Argv);
        Assert.Equal(TimeSpan.FromMinutes(3), call.Options?.Timeout);
        Assert.Equal("Ok", vm.Review.ResultKey);
    }

    [Fact]
    public void Cancelling_either_review_runs_nothing()
    {
        var vm = Panel(AiOn);
        var ran = Capture(vm);

        vm.ScanAiDiscoveryCommand.Execute(null);
        vm.Review.DismissCommand.Execute(null);
        vm.ToggleNotificationsCommand.Execute(null);
        vm.Review.DismissCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(ran);
        Assert.Empty(Services.Cli.Activity);
    }

    // ------------------------------------------------------------------ notifications

    [Fact]
    public void Notifications_that_config_yaml_does_not_mention_are_on_on_windows_so_the_button_turns_them_off()
    {
        var vm = Panel(AiOn);

        Assert.Equal("Turn notifications off", vm.NotificationsLabel);
        Assert.Contains("are on", vm.NotificationsTip, StringComparison.Ordinal);
        Assert.Contains("defenseclaw setup notifications off", vm.NotificationsTip, StringComparison.Ordinal);

        vm.ToggleNotificationsCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal("Turn desktop notifications off?", review.Title);
        Assert.Equal("Turn off", review.ConfirmLabel);
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "setup", "notifications", "off" }, step.Argv);
        Assert.Equal("defenseclaw setup notifications off", step.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.True(review.RestartsGateway);
        Assert.Contains("notifications.enabled to false", review.Summary, StringComparison.Ordinal);
        Assert.Contains("separate setting", review.Summary, StringComparison.Ordinal);
        Assert.Empty(Services.Cli.Activity);
    }

    [Fact]
    public async Task Notifications_that_config_yaml_turned_off_are_turned_on_and_the_run_is_the_exact_command()
    {
        var vm = Panel(AiOn + "notifications:\n  enabled: false\n");
        var ran = Capture(vm);

        Assert.Equal("Turn notifications on", vm.NotificationsLabel);
        Assert.Contains("are off", vm.NotificationsTip, StringComparison.Ordinal);
        vm.ToggleNotificationsCommand.Execute(null);
        var review = vm.Review.CommandReview!;
        Assert.Equal("Turn desktop notifications on?", review.Title);
        Assert.Equal("Turn on", review.ConfirmLabel);
        Assert.Contains("notifications.enabled to true", review.Summary, StringComparison.Ordinal);

        _ = _temp.WriteFile("config.yaml", AiOn);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        var call = Assert.Single(ran);
        Assert.Equal(new[] { "setup", "notifications", "on" }, call.Argv);
        Assert.Null(call.Options);
        Assert.Equal("Turn notifications off", vm.NotificationsLabel);
    }

    [Fact]
    public void The_notifications_switch_works_with_the_gateway_down_as_with_it_up()
    {
        Assert.True(Panel(AiOn).ToggleNotificationsCommand.CanExecute(null));
        Assert.True(Panel(AiOn, Stopped()).ToggleNotificationsCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ fill missing keys: the console route

    [Fact]
    public async Task Fill_missing_keys_is_drawn_only_while_the_doctor_cache_names_a_required_key_that_is_not_set()
    {
        var vm = Panel();
        Assert.False(vm.ShowFillMissingKeys);

        await LoadDoctorAsync(vm, MissingKeys("ANTHROPIC_API_KEY", "OPENAI_API_KEY"));
        Assert.True(vm.ShowFillMissingKeys);
        Assert.True(vm.FillKeysInConsoleCommand.CanExecute(null));
        Assert.StartsWith("Changes state.", vm.FillMissingKeysTip, StringComparison.Ordinal);
        Assert.Contains("defenseclaw keys fill-missing --yes", vm.FillMissingKeysTip, StringComparison.Ordinal);
        Assert.Contains("never through this app", vm.FillMissingKeysTip, StringComparison.Ordinal);

        await LoadDoctorAsync(vm, MissingKeys());
        Assert.False(vm.ShowFillMissingKeys);
    }

    [Fact]
    public async Task Fill_missing_keys_opens_a_console_with_the_exact_command_records_the_hand_off_and_opens_no_review()
    {
        var vm = Panel();
        await LoadDoctorAsync(vm, MissingKeys("ANTHROPIC_API_KEY"));
        ProcessStartInfo? started = null;
        vm.Terminal = new CredentialTerminal(Services.Paths)
        {
            ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
            Launch = info =>
            {
                started = info;
                return null;
            },
        };

        await vm.FillKeysInConsoleCommand.ExecuteAsync(null);

        Assert.NotNull(started);
        Assert.Equal("/d /s /c \"\"C:\\Tools\\defenseclaw.exe\" keys fill-missing --yes & echo. & pause\"", started!.Arguments);
        Assert.False(vm.Review.IsOpen);

        // The hand-off is the one Activity entry: the argv and a note, no exit code (the app does not observe the console), and no value anywhere.
        var entry = Assert.Single(Services.Cli.Activity);
        Assert.Equal(new[] { "keys", "fill-missing", "--yes" }, entry.Argv);
        Assert.Null(entry.ExitCode);
        Assert.Null(entry.FailureReason);

        Assert.True(vm.HasDiagnosticMessage);
        Assert.Equal("Fill missing keys", vm.DiagnosticTitle);
        Assert.Equal("Neutral", vm.DiagnosticKey);
        Assert.Contains("keys fill-missing --yes", vm.DiagnosticMessage, StringComparison.Ordinal);
        Assert.Contains("run doctor again", vm.DiagnosticMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_console_that_will_not_start_or_a_cli_that_is_missing_says_why_and_records_nothing()
    {
        var vm = Panel();
        await LoadDoctorAsync(vm, MissingKeys("ANTHROPIC_API_KEY"));

        vm.Terminal = new CredentialTerminal(Services.Paths)
        {
            ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
            Launch = _ => "The console could not be started: access denied",
        };
        await vm.FillKeysInConsoleCommand.ExecuteAsync(null);
        Assert.Equal("Bad", vm.DiagnosticKey);
        Assert.Contains("access denied", vm.DiagnosticMessage, StringComparison.Ordinal);

        vm.Terminal = new CredentialTerminal(Services.Paths)
        {
            ResolveExecutable = () => Task.FromResult<string?>(null),
            Launch = _ => throw new InvalidOperationException("nothing should be launched without a CLI"),
        };
        await vm.FillKeysInConsoleCommand.ExecuteAsync(null);
        Assert.Equal("Bad", vm.DiagnosticKey);
        Assert.Contains("defenseclaw is not on PATH", vm.DiagnosticMessage, StringComparison.Ordinal);

        Assert.Empty(Services.Cli.Activity);
    }

    [Fact]
    public void No_secret_is_ever_an_argument_of_the_console_command_or_of_anything_the_buttons_run()
    {
        // The argv of every button is a fixed list of words: nothing a person types, and nothing read from a file, can be on it.
        var all = new[]
        {
            OverviewPanelViewModel.EnableAiDiscoveryArgv,
            OverviewPanelViewModel.ScanAiDiscoveryArgv,
            OverviewPanelViewModel.NotificationsOnArgv,
            OverviewPanelViewModel.NotificationsOffArgv,
            OverviewPanelViewModel.FillMissingKeysArgv,
        };

        Assert.All(all, argv => Assert.All(argv, token => Assert.Matches("^[a-z-]+$", token)));
        Assert.All(all, argv => Assert.All(argv, token => Assert.True(CredentialTerminal.IsSafeToken(token), token)));
    }

    // ------------------------------------------------------------------ why a button is off: DefenseClaw is not there to run it

    [Theory]
    [InlineData(null, "Still checking the gateway; try again in a moment.")]
    [InlineData(InstallState.NotInstalled, "DefenseClaw is not installed on this machine.")]
    [InlineData(InstallState.InstalledNotInitialized, "DefenseClaw is not initialized yet. Run 'defenseclaw init' first.")]
    public void Until_there_is_an_install_to_run_them_on_every_button_is_off_and_says_why(InstallState? install, string reason)
    {
        var vm = Panel(AiOn, new GatewaySnapshot { Install = install, State = AppGatewayState.Unknown, PolledAt = DateTimeOffset.UtcNow });

        Assert.False(vm.EnableAiDiscoveryCommand.CanExecute(null));
        Assert.False(vm.ScanAiDiscoveryCommand.CanExecute(null));
        Assert.False(vm.ToggleNotificationsCommand.CanExecute(null));
        Assert.False(vm.FillKeysInConsoleCommand.CanExecute(null));
        Assert.Equal(reason, vm.EnableAiDiscoveryTip);
        Assert.Equal(reason, vm.ScanAiDiscoveryTip);
        Assert.Equal(reason, vm.NotificationsTip);
        Assert.Equal(reason, vm.FillMissingKeysTip);
    }

    // ------------------------------------------------------------------ the installation guard

    public static IEnumerable<object[]> ReadOnlyInstallations() =>
        new[]
        {
            new object[] { "managed layout" },
            new object[] { "managed by config.yaml" },
            new object[] { "invalid selection" },
        };

    private InstallationContext ReadOnly(string which) => which switch
    {
        "managed layout" => TestInstallations.Managed(_temp.Path),
        "managed by config.yaml" => TestInstallations.ManagedAt(_temp.Path),
        _ => TestInstallations.Invalid(),
    };

    [Theory]
    [MemberData(nameof(ReadOnlyInstallations))]
    public async Task On_a_read_only_installation_every_button_is_off_with_the_installations_sentence_before_any_other_reason(string which)
    {
        var context = ReadOnly(which);
        var reason = context.BlockedReason!;

        // The gateway is down too: the installation's sentence still comes first, since starting it would not cure that.
        var vm = Panel(AiOn, Stopped(), context);
        await LoadDoctorAsync(vm, MissingKeys("ANTHROPIC_API_KEY"));

        Assert.False(vm.CanChangeInstallation);
        Assert.False(vm.EnableAiDiscoveryCommand.CanExecute(null));
        Assert.False(vm.ScanAiDiscoveryCommand.CanExecute(null));
        Assert.False(vm.ToggleNotificationsCommand.CanExecute(null));
        Assert.False(vm.FillKeysInConsoleCommand.CanExecute(null));
        Assert.Equal(reason, vm.EnableAiDiscoveryTip);
        Assert.Equal(reason, vm.ScanAiDiscoveryTip);
        Assert.Equal(reason, vm.NotificationsTip);
        Assert.Equal(reason, vm.FillMissingKeysTip);
    }

    [Theory]
    [MemberData(nameof(ReadOnlyInstallations))]
    public async Task A_button_pressed_by_a_route_that_skips_the_command_gets_a_blocked_review_that_cannot_be_confirmed(string which)
    {
        var context = ReadOnly(which);
        var vm = Panel(AiOn, installation: context);
        var ran = new List<string>();
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            throw new InvalidOperationException("a blocked review ran a step");
        };

        foreach (var open in new Action[] { () => vm.ScanAiDiscoveryCommand.Execute(null), () => vm.ToggleNotificationsCommand.Execute(null) })
        {
            open();
            var review = vm.Review.CommandReview!;
            Assert.True(review.IsBlocked);
            Assert.Equal(context.BlockedReason, review.BlockedReason);
            Assert.False(vm.Review.ConfirmCommand.CanExecute(null));
            await vm.Review.ConfirmCommand.ExecuteAsync(null);
            vm.Review.DismissCommand.Execute(null);
        }

        Assert.Empty(ran);
        Assert.Empty(Services.Cli.Activity);
    }

    [Theory]
    [MemberData(nameof(ReadOnlyInstallations))]
    public async Task The_console_route_is_refused_by_the_guard_too_and_no_window_opens(string which)
    {
        var context = ReadOnly(which);
        var vm = Panel(AiOn, installation: context);
        await LoadDoctorAsync(vm, MissingKeys("ANTHROPIC_API_KEY"));
        var launched = 0;
        vm.Terminal = new CredentialTerminal(Services.Paths)
        {
            ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
            Launch = _ =>
            {
                launched++;
                return null;
            },
        };

        await vm.FillKeysInConsoleCommand.ExecuteAsync(null);

        Assert.Equal(0, launched);
        Assert.Equal("Warn", vm.DiagnosticKey);
        Assert.Equal(context.BlockedReason, vm.DiagnosticMessage);
        Assert.Empty(Services.Cli.Activity);
    }

    [Fact]
    public async Task A_review_opened_while_the_installation_was_writable_is_refused_at_confirm_and_the_refusal_is_in_activity()
    {
        var vm = Panel(AiOff);
        var ran = Capture(vm);
        vm.EnableAiDiscoveryCommand.Execute(null);
        Assert.False(vm.Review.CommandReview!.IsBlocked);

        // config.yaml is edited to managed while the question is on screen: the live answer is asked again at Confirm.
        Services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(ran);
        Assert.Equal("Bad", vm.Review.ResultKey);
        var recorded = Assert.Single(Services.Cli.Activity);
        Assert.Equal(new[] { "agent", "discovery", "enable", "--yes" }, recorded.Argv);
        Assert.StartsWith(CliRunner.RefusedPrefix, recorded.FailureReason, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, recorded.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_runners_own_gate_refuses_every_button_on_a_read_only_installation_and_lets_the_policy_list_read_through()
    {
        foreach (var argv in new[]
                 {
                     OverviewPanelViewModel.EnableAiDiscoveryArgv,
                     OverviewPanelViewModel.ScanAiDiscoveryArgv,
                     OverviewPanelViewModel.NotificationsOnArgv,
                     OverviewPanelViewModel.NotificationsOffArgv,
                     OverviewPanelViewModel.FillMissingKeysArgv,
                 })
        {
            Assert.False(InstallationGate.IsReadOnly(CommandReview.DefaultExecutable, argv), string.Join(' ', argv));
        }

        Assert.True(InstallationGate.IsReadOnly(CommandReview.DefaultExecutable, new[] { "policy", "list" }));
    }

    [Fact]
    public void An_installation_that_turns_read_only_while_the_page_is_open_turns_the_buttons_off_and_back_on()
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, AiOff);
        var snapshot = Running();
        Publish(_services, snapshot);
        var vm = new OverviewPanelViewModel(_services);
        vm.Apply(snapshot);

        UiThread.Run(() =>
        {
            vm.SetActive(true);
            try
            {
                Assert.True(vm.EnableAiDiscoveryCommand.CanExecute(null));
                var raised = 0;
                vm.EnableAiDiscoveryCommand.CanExecuteChanged += (_, _) => raised++;

                Services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

                Assert.True(raised > 0, "the Enable button was not asked again");
                Assert.False(vm.EnableAiDiscoveryCommand.CanExecute(null));
                Assert.False(vm.ToggleNotificationsCommand.CanExecute(null));
                Assert.Equal(TestInstallations.ManagedReason, vm.EnableAiDiscoveryTip);
                Assert.Equal(TestInstallations.ManagedReason, vm.NotificationsTip);

                Services.Installation.Replace(TestInstallations.UserDefault());

                Assert.True(vm.EnableAiDiscoveryCommand.CanExecute(null));
                Assert.Contains("defenseclaw agent discovery enable --yes", vm.EnableAiDiscoveryTip, StringComparison.Ordinal);
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    // ------------------------------------------------------------------ the Diagnostics item

    [Fact]
    public async Task List_policies_is_a_listed_read_that_runs_straight_away_into_activity_with_no_review()
    {
        var vm = Panel();
        var listPolicies = OverviewPanelViewModel.DiagnosticCommands.Single(c => c.Title == "List policies");

        Assert.Equal(new[] { "policy", "list" }, listPolicies.Argv);
        Assert.True(CommandReview.MayRunUnreviewed(listPolicies.Executable, listPolicies.Argv));

        await vm.RunDiagnosticCommand.ExecuteAsync(listPolicies);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("List policies", vm.DiagnosticTitle);
        Assert.Contains("'defenseclaw' was not found", vm.DiagnosticMessage, StringComparison.Ordinal);
    }
}
