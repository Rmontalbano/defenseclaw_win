using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.SetupResources;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.SetupResources;

/// <summary>
/// CUST-270: every verb of the Setup list editors goes through the shared review with the exact argv - Remove destructive, Test reviewed because it
/// contacts a live endpoint - and nothing runs without it; Enter on a row never tests; a managed installation reads the list and changes nothing; a
/// built-in is protected; a review confirmed after the installation or the configuration moved is refused and recorded. The CLI is the fake.
/// </summary>
public sealed class SetupResourceChangeTests : IDisposable
{
    private readonly SetupEditorHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthkey", "synthtoken", "synthfrag", "synthpath-secret" };

    private const string ConfigBin = @"C:\Users\synthetic\.synthetic-tools\bin";

    private static void Invoke(SetupResourceViewModel vm, SetupVerb verb)
    {
        switch (verb)
        {
            case SetupVerb.Enable:
                vm.EnableCommand.Execute(null);
                break;
            case SetupVerb.Disable:
                vm.DisableCommand.Execute(null);
                break;
            case SetupVerb.Test:
                vm.TestCommand.Execute(null);
                break;
            case SetupVerb.Remove:
                vm.RemoveCommand.Execute(null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, null);
        }
    }

    private static string[] Words(string line) => line.Split(' ');

    public static TheoryData<SetupResource, string, SetupVerb, string, CommandTier> Changes => new()
    {
        { SetupResource.Observability, "example-hec", SetupVerb.Enable, "setup observability enable -- example-hec", CommandTier.StateChanging },
        { SetupResource.Observability, "example-otlp", SetupVerb.Disable, "setup observability disable -- example-otlp", CommandTier.StateChanging },
        { SetupResource.Observability, "example-otlp", SetupVerb.Test, "setup observability test -- example-otlp", CommandTier.StateChanging },
        { SetupResource.Observability, "example-jsonl", SetupVerb.Remove, "setup observability remove --yes -- example-jsonl", CommandTier.Destructive },
        { SetupResource.Webhooks, "example-pagerduty", SetupVerb.Enable, "setup webhook enable -- example-pagerduty", CommandTier.StateChanging },
        { SetupResource.Webhooks, "example-slack", SetupVerb.Disable, "setup webhook disable -- example-slack", CommandTier.StateChanging },
        { SetupResource.Webhooks, "example-slack", SetupVerb.Test, "setup webhook test -- example-slack", CommandTier.StateChanging },
        { SetupResource.Webhooks, "example-hmac", SetupVerb.Remove, "setup webhook remove --yes -- example-hmac", CommandTier.Destructive },
        { SetupResource.TrustedPaths, ConfigBin, SetupVerb.Remove, @"setup trusted-paths remove -- " + ConfigBin, CommandTier.Destructive },
    };

    // ---- every change is reviewed with the exact argv ----

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_change_opens_the_review_with_the_exact_argv_and_runs_only_when_confirmed(
        SetupResource resource, string key, SetupVerb verb, string expected, CommandTier tier)
    {
        var (vm, cli) = await _harness.OpenAsync(resource);
        SetupEditorHarness.Select(vm, key);
        var expectedArgv = expected.StartsWith("setup trusted-paths remove", StringComparison.Ordinal)
            ? new[] { "setup", "trusted-paths", "remove", "--", key }
            : Words(expected);

        Invoke(vm, verb);

        // The review is up, shows the exact command and its tier, and nothing has run.
        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        var step = Assert.Single(review.Steps);
        Assert.Equal(expectedArgv, step.Argv);
        Assert.Equal(tier, step.Tier);
        Assert.Equal(tier, review.Tier);
        Assert.Equal(tier == CommandTier.Destructive, review.IsDestructive);
        Assert.Equal("defenseclaw", step.Executable);
        Assert.Empty(cli.Applied);
        Assert.Single(cli.Ran);

        // Confirming runs that command and no other.
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(expectedArgv, Assert.Single(cli.Applied));
        Assert.True(vm.Review.IsFinished);
        Assert.Equal("Ok", vm.Review.ResultKey);

        // The follow-up: the list is read again after a change, and not after a test (it changes nothing).
        Assert.Equal(verb == SetupVerb.Test ? 1 : 2, cli.Ran.Count);
        Assert.Equal(verb == SetupVerb.Test ? "Test passed" : "Done", vm.NoticeTitle);
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task Cancelling_the_review_runs_nothing(SetupResource resource, string key, SetupVerb verb, string expected, CommandTier tier)
    {
        _ = expected;
        _ = tier;
        var (vm, cli) = await _harness.OpenAsync(resource);
        SetupEditorHarness.Select(vm, key);

        Invoke(vm, verb);
        Assert.True(vm.Review.IsOpen);
        vm.Review.DismissCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Applied);
        Assert.Single(cli.Ran);
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task Every_change_carries_the_filter_that_cuts_addresses_in_what_it_prints(SetupResource resource, string key, SetupVerb verb, string expected, CommandTier tier)
    {
        _ = expected;
        _ = tier;
        var (vm, cli) = await _harness.OpenAsync(resource);
        SetupEditorHarness.Select(vm, key);
        Invoke(vm, verb);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        var options = Assert.Single(cli.AppliedOptions);
        var filter = Assert.IsType<Func<string, string>>(options?.OutputLineFilter);
        Assert.Equal("→ https://hooks.example.test", filter("→ https://synthuser:synthpass@hooks.example.test/services/synthpath-secret"));
    }

    [Fact]
    public async Task The_gateway_restart_is_stated_for_what_restarts_it_and_not_for_a_test()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability);
        SetupEditorHarness.Select(vm, "example-otlp");

        vm.DisableCommand.Execute(null);
        Assert.True(vm.Review.CommandReview!.RestartsGateway);
        Assert.Contains(vm.Review.CommandReview.Warnings, w => w.Title == "Gateway restart");
        vm.Review.DismissCommand.Execute(null);

        vm.TestCommand.Execute(null);
        Assert.False(vm.Review.CommandReview!.RestartsGateway);
        Assert.DoesNotContain(vm.Review.CommandReview.Warnings, w => w.Title == "Gateway restart");
        Assert.Contains(vm.Review.CommandReview.Warnings, w => w.Title == "Contacts a live endpoint");
    }

    [Fact]
    public async Task A_trusted_folder_restarts_the_gateway_only_when_config_yaml_holds_it()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.TrustedPaths);

        SetupEditorHarness.Select(vm, ConfigBin);
        vm.RemoveCommand.Execute(null);
        Assert.True(vm.Review.CommandReview!.RestartsGateway);
        vm.Review.DismissCommand.Execute(null);

        SetupEditorHarness.Select(vm, @"D:\agents\bin");
        vm.RemoveCommand.Execute(null);
        Assert.False(vm.Review.CommandReview!.RestartsGateway);
    }

    [Fact]
    public async Task Enabling_a_destination_that_sends_content_unredacted_says_so_in_the_review()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability);
        SetupEditorHarness.Select(vm, "example-hec"); // redaction: unredacted (none), disabled

        vm.EnableCommand.Execute(null);

        var warning = Assert.Single(vm.Review.CommandReview!.Warnings, w => w.Title == "Content is not redacted");
        Assert.Contains("unredacted (none)", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_webhook_test_says_it_sends_a_real_message_and_to_where_and_never_shows_the_address_whole()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Webhooks);
        SetupEditorHarness.Select(vm, "example-slack");

        vm.TestCommand.Execute(null);

        var review = vm.Review.CommandReview!;
        var send = Assert.Single(review.Warnings, w => w.Title == "Sends a real message");
        Assert.Contains("hooks.example.test", send.Message, StringComparison.Ordinal);
        Assert.Contains(review.Warnings, w => w.Title == "The address is not shown whole");
        Assert.Equal("Send test event", review.ConfirmLabel);
    }

    [Fact]
    public async Task What_a_webhook_test_prints_is_cut_to_the_host_in_the_reviews_result_and_in_what_the_run_stored()
    {
        const string printed = "  Testing webhook example-slack [slack] → https://synthuser:synthpass@hooks.example.test/services/synthpath-secret?token=synthtoken";
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks, c => c.ApplyOutput = printed + "\n    Result:         ok (HTTP 200)");
        SetupEditorHarness.Select(vm, "example-slack");
        vm.TestCommand.Execute(null);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("https://hooks.example.test", vm.Review.ResultOutput, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, vm.Review.ResultOutput ?? string.Empty, StringComparison.Ordinal));
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, vm.NoticeMessage, StringComparison.Ordinal));
        Assert.Equal("setup webhook test -- example-slack".Split(' '), Assert.Single(cli.Applied));
    }

    [Fact]
    public async Task A_failed_test_says_so_with_the_reason_and_leaves_the_list_alone()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Observability, c =>
        {
            c.ApplyExitCode = 1;
            c.ApplyError = "Error: destination test failed (connection_failed): could not reach https://synthuser:synthpass@collector.example.test:4318/v1/synthpath-secret";
        });
        SetupEditorHarness.Select(vm, "example-otlp");
        vm.TestCommand.Execute(null);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Bad", vm.Review.ResultKey);
        Assert.Equal("Test failed", vm.NoticeTitle);
        Assert.Contains("connection_failed", vm.NoticeMessage, StringComparison.Ordinal);
        Assert.Contains("https://collector.example.test:4318", vm.NoticeMessage, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, vm.NoticeMessage, StringComparison.Ordinal));
        Assert.Single(cli.Ran); // a test changes nothing, so the list is not read again
    }

    [Fact]
    public async Task A_failed_change_reads_the_list_again_anyway_and_says_the_command_did_not_finish()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks, c => c.ApplyExitCode = 1);
        SetupEditorHarness.Select(vm, "example-slack");
        vm.DisableCommand.Execute(null);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("The command did not finish", vm.NoticeTitle);
        Assert.Equal(2, cli.Ran.Count);
    }

    // ---- Enter never tests ----

    [Theory]
    [InlineData(SetupResource.Observability, "example-otlp")]
    [InlineData(SetupResource.Webhooks, "example-slack")]
    [InlineData(SetupResource.TrustedPaths, ConfigBin)]
    public async Task Enter_and_a_double_click_on_a_row_start_no_test_and_no_change(SetupResource resource, string key)
    {
        var (vm, cli) = await _harness.OpenAsync(resource);
        SetupEditorHarness.Select(vm, key);

        await vm.ActivateRowCommand.ExecuteAsync(null);
        await vm.ActivateRowCommand.ExecuteAsync(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Applied);
        Assert.All(cli.Ran, argv =>
        {
            Assert.True(SetupResourceArgv.IsRead(argv), string.Join(' ', argv));
            Assert.DoesNotContain("test", argv);
        });
    }

    [Fact]
    public async Task Enter_on_a_destination_says_that_test_is_the_way_to_check_it_and_sends_nothing()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Observability);
        SetupEditorHarness.Select(vm, "example-otlp");

        await vm.ActivateRowCommand.ExecuteAsync(null);

        Assert.Equal("Nothing was sent", vm.NoticeTitle);
        Assert.Contains("Press Test", vm.NoticeMessage, StringComparison.Ordinal);
        Assert.Single(cli.Ran);
    }

    [Fact]
    public async Task Enter_on_a_webhook_shows_it_the_way_the_tui_does_by_reading_it()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks);
        SetupEditorHarness.Select(vm, "example-pagerduty");

        await vm.ActivateRowCommand.ExecuteAsync(null);

        Assert.Equal(Words("setup webhook show --json -- example-pagerduty"), cli.Ran.Last());
        Assert.Equal("defenseclaw setup webhook show --json -- example-pagerduty", vm.DetailCommand);
        Assert.Contains(vm.DetailFacts, f => f.Label == "Secret variable" && f.Value.StartsWith("EXAMPLE_PD_ROUTING_KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.DetailFacts, f => f.Value.Contains("***", StringComparison.Ordinal) && f.Label == "Endpoint");
        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Applied);
    }

    [Fact]
    public async Task Show_answers_for_the_wrong_webhook_is_not_shown_as_the_selected_one()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Webhooks, c => c.WebhookShow = FakeSetupCli.Fixture("webhook-show.synthetic.json")); // example-pagerduty
        SetupEditorHarness.Select(vm, "example-slack");

        await vm.ShowCommand.ExecuteAsync(null);

        Assert.True(vm.HasDetailError);
        Assert.Contains("different webhook", vm.DetailError, StringComparison.Ordinal);
        Assert.Contains(vm.DetailFacts, f => f.Label == "Name" && f.Value == "example-slack"); // still the row's own facts
    }

    // ---- what a row allows ----

    [Fact]
    public async Task What_the_compiler_adds_is_protected_and_the_buttons_say_why()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability);
        SetupEditorHarness.Select(vm, "local-sqlite");

        Assert.False(vm.CanEnable);
        Assert.False(vm.CanDisable);
        Assert.False(vm.CanRemove);
        Assert.False(vm.CanTest);
        Assert.Contains("mandatory", vm.DisableTip, StringComparison.Ordinal);
        Assert.Contains("mandatory", vm.RemoveTip, StringComparison.Ordinal);
        Assert.Contains("stores or serves events on this PC", vm.TestTip, StringComparison.Ordinal);

        // Pressed anyway, nothing opens.
        vm.RemoveCommand.Execute(null);
        vm.DisableCommand.Execute(null);
        vm.TestCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Contains("Cannot test", vm.NoticeTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_destination_already_in_the_state_asked_for_and_one_this_pc_cannot_run_say_so()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability);

        SetupEditorHarness.Select(vm, "example-otlp");
        Assert.False(vm.CanEnable);
        Assert.Equal("“example-otlp” is already enabled.", vm.EnableTip);
        Assert.True(vm.CanDisable);
        Assert.True(vm.CanTest);
        Assert.True(vm.CanRemove);

        SetupEditorHarness.Select(vm, "example-hec");
        Assert.True(vm.CanEnable);
        Assert.False(vm.CanDisable);
        Assert.Equal("“example-hec” is already disabled.", vm.DisableTip);

        SetupEditorHarness.Select(vm, "example-local-stack");
        Assert.False(vm.CanEnable);
        Assert.Contains("cannot run", vm.EnableTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nothing_selected_a_verb_that_needs_a_row_says_to_select_one_and_add_does_not_need_one()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Webhooks);

        Assert.True(vm.CanAdd);
        Assert.False(vm.CanEnable);
        Assert.False(vm.CanTest);
        Assert.False(vm.CanShow);
        Assert.False(vm.CanRemove);
        Assert.Equal("Select a webhook first.", vm.RemoveTip);
        Assert.Equal("Select a webhook first.", vm.ShowTip);
    }

    [Fact]
    public async Task Built_in_trusted_folders_are_protected_and_so_is_an_entry_only_the_environment_has()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.TrustedPaths);

        SetupEditorHarness.Select(vm, @"C:\Windows\System32");
        Assert.False(vm.CanRemove);
        Assert.Contains("Built-in defaults are protected", vm.RemoveTip, StringComparison.Ordinal);

        SetupEditorHarness.Select(vm, @"C:\opt\env-only");
        Assert.False(vm.CanRemove);
        Assert.Contains("DEFENSECLAW_TRUSTED_BIN_PREFIXES", vm.RemoveTip, StringComparison.Ordinal);

        SetupEditorHarness.Select(vm, ConfigBin);
        Assert.True(vm.CanRemove);

        SetupEditorHarness.Select(vm, @"C:\Windows\System32");
        vm.RemoveCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public async Task A_name_that_cannot_go_on_a_command_line_is_listed_and_not_acted_on()
    {
        const string list = """
            [ {"name": "has space", "type": "slack", "enabled": true, "url": "https://hooks.example.test/***"},
              {"name": "wild*card", "type": "slack", "enabled": false, "url": "https://hooks.example.test/***"} ]
            """;
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks, c => c.Webhooks = list);

        Assert.Equal(2, vm.Rows.Count);
        foreach (var row in vm.Rows)
        {
            vm.SelectedRow = row;
            Assert.False(vm.CanEnable || vm.CanDisable || vm.CanTest || vm.CanRemove || vm.CanShow);
            Assert.Contains("cannot be passed to the CLI safely", vm.RemoveTip, StringComparison.Ordinal);
            vm.RemoveCommand.Execute(null);
            Assert.False(vm.Review.IsOpen);
        }

        Assert.Empty(cli.Applied);
    }

    [Fact]
    public async Task A_trusted_folder_the_cli_would_rewrite_is_refused_before_a_review_is_offered()
    {
        const string list = """[ {"path": "%SystemRoot%\\tools", "resolved": "%SystemRoot%\\tools", "source": "config", "status": "missing", "removable": true} ]""";
        var (vm, cli) = await _harness.OpenAsync(SetupResource.TrustedPaths, c => c.TrustedPaths = list);
        vm.SelectedRow = vm.Rows.Single();

        vm.RemoveCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Cannot remove this trusted folder", vm.NoticeTitle);
        Assert.Contains("would arrive as", vm.NoticeMessage, StringComparison.Ordinal);
        Assert.Empty(cli.Applied);
    }

    // ---- a managed installation reads and changes nothing ----

    [Theory]
    [InlineData(SetupResource.Observability, "example-otlp")]
    [InlineData(SetupResource.Webhooks, "example-slack")]
    [InlineData(SetupResource.TrustedPaths, ConfigBin)]
    public async Task A_managed_installation_still_reads_the_list_and_every_control_that_changes_something_is_off_with_its_sentence(SetupResource resource, string key)
    {
        var services = _harness.Services(TestInstallations.ManagedAt(_harness.DataDirectory));
        var (vm, cli) = await _harness.OpenAsync(resource, services: services);

        // The list is a read: it runs, and its rows are on screen.
        Assert.Equal(SetupListState.Loaded, vm.State);
        Assert.Equal(SetupResourceArgv.List(resource), Assert.Single(cli.Ran));
        Assert.True(vm.HasInstallationBlock);
        Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);

        // With nothing selected and with a row selected, the installation's sentence is the one every change says.
        foreach (var selected in new[] { false, true })
        {
            vm.SelectedRow = selected ? vm.Rows.Single(r => r.Key == key) : null;
            Assert.False(vm.CanAdd);
            Assert.False(vm.CanEnable);
            Assert.False(vm.CanDisable);
            Assert.False(vm.CanTest);
            Assert.False(vm.CanRemove);
            Assert.All(new[] { vm.AddTip, vm.EnableTip, vm.DisableTip, vm.TestTip, vm.RemoveTip }, tip => Assert.Equal(TestInstallations.ManagedReason, tip));
        }

        // Pressed anyway: no review, no command, no wizard.
        var wizard = 0;
        vm.OpenWizard = _ => { wizard++; return Task.CompletedTask; };
        vm.SelectedRow = vm.Rows.Single(r => r.Key == key);
        await vm.AddCommand.ExecuteAsync(null);
        vm.EnableCommand.Execute(null);
        vm.DisableCommand.Execute(null);
        vm.TestCommand.Execute(null);
        vm.RemoveCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Applied);
        Assert.Equal(0, wizard);
        Assert.Equal(TestInstallations.ManagedReason, vm.NoticeMessage);
        Assert.Single(cli.Ran);
    }

    [Fact]
    public async Task A_managed_installation_still_shows_a_webhook_because_show_only_reads()
    {
        var services = _harness.Services(TestInstallations.ManagedAt(_harness.DataDirectory));
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks, services: services);
        SetupEditorHarness.Select(vm, "example-pagerduty");

        Assert.True(vm.CanShow);
        await vm.ShowCommand.ExecuteAsync(null);

        Assert.Equal(Words("setup webhook show --json -- example-pagerduty"), cli.Ran.Last());
        Assert.False(vm.HasDetailError, vm.DetailError);
        Assert.Empty(cli.Applied);
    }

    [Fact]
    public async Task An_invalid_installation_is_read_only_too()
    {
        var services = _harness.Services(TestInstallations.InvalidAt(_harness.DataDirectory));
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Observability, services: services);
        SetupEditorHarness.Select(vm, "example-otlp");

        Assert.Equal(SetupListState.Loaded, vm.State);
        Assert.False(vm.CanDisable);
        Assert.NotNull(vm.InstallationBlockedReason);
        Assert.Equal(vm.InstallationBlockedReason, vm.DisableTip);
        vm.DisableCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Empty(cli.Applied);
    }

    [Fact]
    public async Task The_installation_comes_before_a_stale_list_and_before_a_row_s_own_reason()
    {
        var services = _harness.Services(TestInstallations.ManagedAt(_harness.DataDirectory));
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Observability, services: services);
        SetupEditorHarness.Select(vm, "local-sqlite"); // protected, too

        cli.ReadExitCode = 1;
        await vm.ReadAsync(); // the list is now failed as well

        Assert.Equal(TestInstallations.ManagedReason, vm.RemoveTip);
        Assert.Equal(TestInstallations.ManagedReason, vm.DisableTip);
    }

    [Fact]
    public async Task The_editor_follows_the_installation_when_it_turns_read_only_and_back()
    {
        var services = _harness.Services();
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability, services: services);
        SetupEditorHarness.Select(vm, "example-otlp");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.True(vm.CanDisable);
        Assert.False(vm.HasInstallationBlock);

        services.Installation.Replace(TestInstallations.ManagedAt(_harness.DataDirectory));

        Assert.Contains(nameof(SetupResourceViewModel.CanDisable), raised);
        Assert.Contains(nameof(SetupResourceViewModel.InstallationBlockedReason), raised);
        Assert.False(vm.CanDisable);
        Assert.True(vm.HasInstallationBlock);

        services.Installation.Replace(TestInstallations.UserDefault());

        Assert.True(vm.CanDisable);
        Assert.False(vm.HasInstallationBlock);
    }

    [Fact]
    public async Task A_review_confirmed_after_the_installation_turned_read_only_runs_nothing_and_is_recorded_as_refused()
    {
        var services = _harness.Services();
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks, services: services);
        SetupEditorHarness.Select(vm, "example-slack");
        vm.RemoveCommand.Execute(null);
        Assert.False(vm.Review.CommandReview!.IsBlocked);

        services.Installation.Replace(TestInstallations.ManagedAt(_harness.DataDirectory));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(cli.Applied);
        Assert.True(vm.Review.IsFinished);
        Assert.StartsWith("Not run.", vm.Review.ResultText, StringComparison.Ordinal);
        var refused = Assert.Single(services.Cli.Activity);
        Assert.StartsWith(CliRunner.RefusedPrefix, refused.FailureReason, StringComparison.Ordinal);
        Assert.Equal(Words("setup webhook remove --yes -- example-slack"), refused.Argv);
        Assert.Null(refused.ExitCode);
    }

    // ---- a list the configuration has moved on from does not authorize a change ----

    [Fact]
    public async Task A_change_asked_for_after_config_yaml_changed_is_refused_with_the_trusts_sentence()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Observability);
        SetupEditorHarness.Select(vm, "example-otlp");
        Assert.True(vm.CanDisable);

        File.WriteAllText(Path.Combine(_harness.DataDirectory, "config.yaml"), "config_version: 8\n");
        vm.DisableCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.NoticeMessage);
        Assert.False(vm.CanDisable);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), vm.DisableTip);
        Assert.Empty(cli.Applied);

        // A fresh read restores the change.
        await vm.ReadAsync();
        Assert.True(vm.CanDisable);
    }

    [Fact]
    public async Task A_review_confirmed_after_config_yaml_changed_runs_nothing_and_is_recorded_as_refused()
    {
        var services = _harness.Services();
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Observability, services: services);
        SetupEditorHarness.Select(vm, "example-otlp");
        vm.DisableCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);

        File.WriteAllText(Path.Combine(_harness.DataDirectory, "config.yaml"), "config_version: 8\n");
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(cli.Applied);
        Assert.StartsWith("Not run.", vm.Review.ResultText, StringComparison.Ordinal);
        Assert.Contains(CatalogTrust.ConfigChangedReason(), vm.Review.ResultText, StringComparison.Ordinal);
        var refused = Assert.Single(services.Cli.Activity);
        Assert.StartsWith(CliRunner.RefusedPrefix, refused.FailureReason, StringComparison.Ordinal);
        Assert.Equal(Words("setup observability disable -- example-otlp"), refused.Argv);
    }

    [Fact]
    public async Task Changes_are_off_before_the_first_read_has_finished_and_after_a_list_that_could_not_be_read()
    {
        var (vm, _) = _harness.Editor(SetupResource.Observability); // not read yet
        Assert.Equal(SetupListState.Loading, vm.State);
        Assert.False(vm.CanRemove);
        Assert.Equal("Changes are off until the destinations have been read.", vm.RemoveTip);
        Assert.True(vm.CanAdd); // Add needs only the installation: the wizard reviews its own command

        var (failed, _) = await _harness.OpenAsync(SetupResource.Observability, c => c.ReadExitCode = 1);
        Assert.Equal(SetupListState.Failed, failed.State);
        Assert.False(failed.CanRemove);
        Assert.Contains("Changes are off", failed.RemoveTip, StringComparison.Ordinal);
        Assert.True(failed.CanAdd);
    }

    // ---- Add ----

    [Fact]
    public async Task Add_opens_the_wizard_on_add_and_reads_the_list_again_when_it_closes()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Observability);
        WizardPreset? opened = null;
        vm.OpenWizard = preset =>
        {
            opened = preset;
            return Task.CompletedTask;
        };

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(new WizardPreset("add"), opened);
        Assert.Equal(2, cli.Ran.Count);
        Assert.Empty(cli.Applied);
        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public async Task Add_with_no_wizard_to_open_says_so_and_runs_nothing()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks);

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal("The wizard cannot open here", vm.NoticeTitle);
        Assert.Single(cli.Ran);
    }

    [Fact]
    public async Task A_wizard_that_throws_is_a_notice_and_not_a_crash()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Webhooks);
        vm.OpenWizard = _ => throw new InvalidOperationException("the window could not be created");

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal("The wizard could not be opened", vm.NoticeTitle);
        Assert.Contains("the window could not be created", vm.NoticeMessage, StringComparison.Ordinal);
    }

    // ---- Esc and the end of the window's life ----

    [Fact]
    public async Task Esc_closes_the_review_then_the_notice_then_the_selection()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability);
        SetupEditorHarness.Select(vm, "example-otlp");
        vm.DisableCommand.Execute(null);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.Review.IsOpen);
        Assert.NotNull(vm.SelectedRow);

        await vm.ActivateRowCommand.ExecuteAsync(null); // posts the "nothing was sent" notice
        Assert.True(vm.HasNotice);
        Assert.True(vm.HandleEscape());
        Assert.False(vm.HasNotice);
        Assert.NotNull(vm.SelectedRow);

        Assert.True(vm.HandleEscape());
        Assert.Null(vm.SelectedRow);
        Assert.False(vm.HandleEscape());
    }

    [Fact]
    public async Task A_disposed_editor_lets_go_of_the_installation_and_the_configuration()
    {
        var services = _harness.Services();
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability, services: services);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Dispose();
        vm.Dispose(); // idempotent
        services.Installation.Replace(TestInstallations.ManagedAt(_harness.DataDirectory));

        Assert.Empty(raised);
        await vm.ReadAsync(); // reads nothing more
        Assert.Empty(raised);
    }
}
