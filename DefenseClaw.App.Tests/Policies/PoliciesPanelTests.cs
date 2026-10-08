using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// The Policies panel (CUST-281): what it reads, what it shows, what it refuses, and what each change puts in front of the operator
/// before anything runs. The CLI is a script handed to the view-model (<see cref="PoliciesPanelViewModel.RunCli"/> for reads,
/// <see cref="DiscoverActionReview.RunStep"/> for a confirmed change), so no process starts; every name and value is synthetic.
/// </summary>
public sealed class PoliciesPanelTests : IDisposable
{
    private const string Opa = "X FAIL: 'opa' binary not found - install OPA to validate Rego bundles.";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public PoliciesPanelTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ a scripted CLI

    private sealed class Cli
    {
        public List<string> Calls { get; } = new();

        public string ListText { get; set; } = PolicyFixtures.ListReport;

        public int ListExit { get; set; }

        public int ValidateExit { get; set; }

        public Dictionary<string, string> Shows { get; } = new()
        {
            ["default"] = PolicyFixtures.Show("default"),
            ["strict"] = PolicyFixtures.Show("strict"),
            ["permissive"] = PolicyFixtures.Show("permissive", install: "none", runtime: "enable", block: 4, alert: 3, trust: "none", firewallDefault: "allow"),
            ["team-baseline"] = PolicyFixtures.Show("team-baseline", install: "none", runtime: "enable"),
        };

        public CliInvocation Handle(IReadOnlyList<string> argv)
        {
            Calls.Add(string.Join(' ', argv));
            if (argv.SequenceEqual(new[] { "policy", "list" }))
            {
                return ListExit == 0 ? Result(0, argv, Out(ListText)) : Result(ListExit, argv, Err("Traceback: fixture failure"));
            }

            if (argv.Count == 3 && argv[1] == "show")
            {
                return Shows.TryGetValue(argv[2], out var text) ? Result(0, argv, Out(text)) : Result(1, argv, Err($"error: policy '{argv[2]}' not found"));
            }

            if (argv.SequenceEqual(new[] { "policy", "validate" }))
            {
                return ValidateExit == 0
                    ? Result(0, argv, Out("  OK data.json: OK"), Out("  OK All validations passed."))
                    : Result(1, argv, Out("  OK data.json: OK"), Out(Opa));
            }

            if (argv.SequenceEqual(new[] { "policy", "test" }))
            {
                return Result(0, argv, Out("PASS: 12/12"), Out("  OK All Rego tests passed."));
            }

            throw new InvalidOperationException("a read-only door ran: " + string.Join(' ', argv));
        }

        public IEnumerable<string> Mutations => Calls.Where(c =>
            !c.StartsWith("policy list", StringComparison.Ordinal) && !c.StartsWith("policy show", StringComparison.Ordinal)
            && c != "policy validate" && c != "policy test");
    }

    private static (CliStream Stream, string Text) Out(string text) => (CliStream.StandardOutput, text);

    private static (CliStream Stream, string Text) Err(string text) => (CliStream.StandardError, text);

    private static CliInvocation Result(int exit, IReadOnlyList<string> argv, params (CliStream Stream, string Text)[] lines)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        foreach (var (stream, text) in lines)
        {
            InvocationFactory.Append(invocation, text, stream);
        }

        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private async Task<(PoliciesPanelViewModel Vm, Cli Cli, List<string> Ran)> OpenAsync(Action<Cli>? script = null)
    {
        var cli = new Cli();
        script?.Invoke(cli);
        var ran = new List<string>();
        var vm = new PoliciesPanelViewModel(_services)
        {
            RunCli = (argv, _) => Task.FromResult(cli.Handle(argv)),
        };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Result(0, argv, Out("ok")));
        };
        await vm.InitializeAsync();
        return (vm, cli, ran);
    }

    private static PolicyRow Row(PoliciesPanelViewModel vm, string name) => vm.Rows.Single(r => r.Name == name);

    private static async Task SelectAsync(PoliciesPanelViewModel vm, string name)
    {
        vm.SelectedRow = Row(vm, name);
        for (var i = 0; i < 200 && (vm.IsDetailLoading || vm.Detail is null); i++)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(vm.Detail);
    }

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task The_list_fills_the_table_marks_the_active_policy_and_reads_the_selected_one_with_show()
    {
        var (vm, cli, _) = await OpenAsync();

        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.Equal(new[] { "default", "bare-builtin", "strict", "team-baseline" }, vm.Rows.Select(r => r.Name).ToArray());
        Assert.Equal("default", vm.ActiveName);
        Assert.Equal("4 policies - active: default", vm.CaptionText);
        Assert.Equal(new[] { "Built-in", "Built-in", "Built-in", "Custom" }, vm.Rows.Select(r => r.Kind).ToArray());
        Assert.Equal("Active", Row(vm, "default").ActiveText);
        Assert.Equal(string.Empty, Row(vm, "strict").ActiveText);

        await SelectAsync(vm, "strict");

        Assert.Equal("strict", vm.Detail!.Name);
        Assert.Contains(vm.DetailSections, s => s.Title == "Severity actions" && s.Lines.Any(l => l.Label == "CRITICAL"));
        Assert.Contains(vm.DetailSections, s => s.Title == "Guardrail");
        Assert.Contains("Severity Actions:", vm.DetailRawText, StringComparison.Ordinal);
        Assert.Empty(cli.Mutations);

        vm.SearchText = "custom";
        Assert.Equal(new[] { "team-baseline" }, vm.Rows.Select(r => r.Name).ToArray());
        Assert.Equal("1 of 4 policies - active: default", vm.CaptionText);
    }

    [Fact]
    public async Task The_read_door_runs_list_show_validate_and_test_and_refuses_everything_else()
    {
        var (vm, cli, _) = await OpenAsync();

        foreach (var argv in new[]
                 {
                     new[] { "policy", "activate", "strict" },
                     new[] { "policy", "delete", "x" },
                     new[] { "policy", "create", "x" },
                     new[] { "policy", "edit", "actions", "--severity", "high" },
                     new[] { "policy", "show", "--help" },
                     new[] { "policy", "validate", "--rego-dir", "x" },
                     new[] { "doctor" },
                 })
        {
            Assert.Throws<InvalidOperationException>(() => { _ = vm.RunReadAsync(argv); });
        }

        _ = await vm.RunReadAsync(new[] { "policy", "test" });
        Assert.Empty(cli.Mutations);
    }

    [Fact]
    public async Task A_failed_first_read_is_an_error_state_and_nothing_can_change()
    {
        var (vm, _, _) = await OpenAsync(c => c.ListExit = 2);

        Assert.Equal(PoliciesState.Error, vm.State);
        Assert.True(vm.ShowError);
        Assert.False(vm.CanCreate);
        Assert.NotNull(vm.ChangesBlockedReason);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_rows_but_turns_every_change_off_with_the_reason()
    {
        var (vm, cli, _) = await OpenAsync();
        await SelectAsync(vm, "strict");
        Assert.True(vm.CanActivate);
        Assert.True(vm.CanEdit);

        cli.ListExit = 2;
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.Equal(4, vm.Rows.Count);
        Assert.True(vm.HasRefreshWarning);
        Assert.False(vm.CanChange);
        Assert.False(vm.CanActivate);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanCreate);
        Assert.Contains("last read failed", vm.ActivateTip, StringComparison.Ordinal);

        // Asking anyway is refused with the same reason, and runs nothing.
        cli.Calls.Clear();
        await vm.ActivateCommand.ExecuteAsync(null);
        vm.CreateCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.False(vm.Form.IsOpen);
        Assert.True(vm.HasNotice);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task A_read_that_has_gone_old_stops_authorizing_changes_at_the_moment_of_the_request()
    {
        var clock = new ManualClock();
        var (vm, cli, _) = await OpenAsync();
        vm.Trust = new CatalogTrust(time: clock);
        vm.Trust.MarkComplete();
        await SelectAsync(vm, "strict");
        Assert.True(vm.CanActivate);

        clock.Advance(TimeSpan.FromMinutes(11));
        cli.Calls.Clear();
        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Contains("ago", vm.NoticeMessage, StringComparison.Ordinal);
        Assert.Empty(cli.Calls);
    }

    // ------------------------------------------------------------------ validate and test

    [Fact]
    public async Task Validate_and_test_run_directly_and_show_copyable_output()
    {
        var (vm, cli, _) = await OpenAsync();

        await vm.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(PolicyValidation.Passed, vm.Validation);
        Assert.Contains("All validations passed", vm.OutputText, StringComparison.Ordinal);
        Assert.Equal("Ok", vm.OutputTone);

        await vm.RunTestsCommand.ExecuteAsync(null);
        Assert.Contains("PASS: 12/12", vm.OutputText, StringComparison.Ordinal);
        Assert.Equal("defenseclaw policy test", vm.OutputTitle);
        Assert.Empty(cli.Mutations);
        Assert.False(vm.Review.IsOpen);
    }

    // ------------------------------------------------------------------ activate

    [Fact]
    public async Task A_failed_validate_blocks_activate_and_no_review_is_offered()
    {
        var (vm, cli, _) = await OpenAsync(c => c.ValidateExit = 1);
        await SelectAsync(vm, "strict");

        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal(PolicyValidation.Failed, vm.Validation);
        Assert.Contains("withheld", vm.NoticeTitle, StringComparison.Ordinal);
        Assert.Contains("opa", vm.OutputText, StringComparison.Ordinal);
        Assert.Contains("OPA", vm.OutputNote, StringComparison.Ordinal);
        Assert.DoesNotContain(cli.Calls, c => c.Contains("activate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activating_a_weaker_policy_shows_the_consequence_and_needs_the_acknowledgement()
    {
        var (vm, cli, ran) = await OpenAsync();
        await SelectAsync(vm, "team-baseline");

        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw policy activate team-baseline", review.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Contains(review.Warnings, w => w.Title == "Reduces protection" && w.Message.Contains("CRITICAL", StringComparison.Ordinal));
        Assert.True(vm.Review.RequiresAcknowledgement);
        Assert.Equal(PoliciesPanelViewModel.WeakeningAcknowledgement, vm.Review.AcknowledgementText);

        // Not until it is ticked - not by the button, and not by invoking the command directly.
        Assert.False(vm.Review.ConfirmCommand.CanExecute(null));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(ran);

        vm.Review.IsAcknowledged = true;
        Assert.True(vm.Review.ConfirmCommand.CanExecute(null));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw policy activate team-baseline" }, ran);
        Assert.Equal(2, cli.Calls.Count(c => c == "policy list"));
        Assert.Equal("Done", vm.NoticeTitle);
    }

    [Fact]
    public async Task Activating_a_stronger_policy_lists_what_changes_and_asks_for_no_acknowledgement()
    {
        var (vm, _, ran) = await OpenAsync(c => c.Shows["default"] = PolicyFixtures.Show("default", install: "none", runtime: "enable", block: 4));
        await SelectAsync(vm, "strict");

        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.True(vm.Review.IsOpen);
        Assert.False(vm.Review.RequiresAcknowledgement);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Other changes");
        Assert.DoesNotContain(vm.Review.CommandReview.Warnings, w => w.Title == "Reduces protection");

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "defenseclaw policy activate strict" }, ran);
    }

    [Fact]
    public async Task When_the_policy_in_force_cannot_be_read_the_switch_is_treated_as_weakening()
    {
        var (vm, cli, _) = await OpenAsync();
        _ = cli.Shows.Remove("default");
        await SelectAsync(vm, "strict");

        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.True(vm.Review.IsOpen);
        Assert.True(vm.Review.RequiresAcknowledgement);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Cannot compare");
    }

    [Fact]
    public async Task The_active_policy_cannot_be_activated_again()
    {
        var (vm, _, _) = await OpenAsync();
        await SelectAsync(vm, "default");

        Assert.False(vm.CanActivate);
        Assert.Contains("already active", vm.ActivateTip, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ delete

    [Fact]
    public async Task Only_custom_policies_can_be_deleted_and_the_review_is_destructive()
    {
        var (vm, _, ran) = await OpenAsync();

        await SelectAsync(vm, "strict");
        Assert.False(vm.CanDelete);
        Assert.Contains("Built-in", vm.DeleteTip, StringComparison.Ordinal);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.False(vm.Review.IsOpen);

        await SelectAsync(vm, "team-baseline");
        Assert.True(vm.CanDelete);
        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.True(vm.Review.IsOpen);
        Assert.Equal(CommandTier.Destructive, vm.Review.CommandReview!.Tier);
        Assert.Equal("defenseclaw policy delete team-baseline", vm.Review.CommandReview.CommandText);
        Assert.False(vm.Review.RequiresAcknowledgement);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "defenseclaw policy delete team-baseline" }, ran);
    }

    [Fact]
    public async Task Deleting_the_active_custom_policy_uses_force_and_asks_when_the_fallback_is_weaker()
    {
        var active = "  * team-baseline\r\n      Active custom\r\n    default [built-in]\r\n";
        var (vm, _, _) = await OpenAsync(c =>
        {
            c.ListText = "Available policies:\r\n\r\n" + active;
            c.Shows["default"] = PolicyFixtures.Show("default", install: "none", runtime: "enable");
            c.Shows["team-baseline"] = PolicyFixtures.Show("team-baseline");
        });
        await SelectAsync(vm, "team-baseline");

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal("defenseclaw policy delete team-baseline --force", vm.Review.CommandReview!.CommandText);
        Assert.True(vm.Review.RequiresAcknowledgement);
    }

    // ------------------------------------------------------------------ create and edit

    [Fact]
    public async Task Create_checks_the_name_then_reviews_the_exact_command()
    {
        var (vm, _, ran) = await OpenAsync();

        vm.CreateCommand.Execute(null);
        Assert.True(vm.Form.IsCreate);

        vm.Form.Name = "strict";
        await vm.SubmitFormCommand.ExecuteAsync(null);
        Assert.True(vm.Form.IsOpen);
        Assert.NotEmpty(vm.Form.Error);
        Assert.False(vm.Review.IsOpen);

        vm.Form.Name = "team-two";
        vm.Form.Preset = "strict";
        vm.Form.CriticalAction = "block";
        await vm.SubmitFormCommand.ExecuteAsync(null);

        Assert.False(vm.Form.IsOpen);
        Assert.Equal("defenseclaw policy create team-two --from-preset strict --critical-action block", vm.Review.CommandReview!.CommandText);

        // Cancelling the review brings the form back with what was typed.
        vm.Review.HandleEscape();
        Assert.True(vm.Form.IsCreate);
        Assert.Equal("team-two", vm.Form.Name);

        await vm.SubmitFormCommand.ExecuteAsync(null);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(ran);
    }

    [Fact]
    public async Task Editing_the_active_policy_downward_is_live_and_needs_the_acknowledgement()
    {
        var (vm, _, ran) = await OpenAsync();
        await SelectAsync(vm, "default");

        vm.EditCommand.Execute(null);
        Assert.True(vm.Form.IsEdit);
        Assert.Contains("live policy data", vm.Form.TargetNote, StringComparison.Ordinal);
        vm.Form.Section = PolicyFormViewModel.SectionActions;
        vm.Form.Severity = "critical";
        vm.Form.Install = "none";
        await vm.SubmitFormCommand.ExecuteAsync(null);

        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw policy edit actions --severity critical --install none --policy-name default", review.CommandText);
        Assert.True(vm.Review.RequiresAcknowledgement);
        Assert.Contains("built-in", review.Summary, StringComparison.Ordinal);

        vm.Review.IsAcknowledged = true;
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(ran);
    }

    [Fact]
    public async Task Editing_a_policy_that_is_not_active_is_a_draft_and_asks_for_nothing()
    {
        var (vm, _, _) = await OpenAsync();
        await SelectAsync(vm, "team-baseline");

        vm.EditCommand.Execute(null);
        vm.Form.Section = PolicyFormViewModel.SectionFirewall;
        vm.Form.AddPort = "8080";
        await vm.SubmitFormCommand.ExecuteAsync(null);

        Assert.Equal("defenseclaw policy edit firewall --add-port 8080 --policy-name team-baseline", vm.Review.CommandReview!.CommandText);
        Assert.False(vm.Review.RequiresAcknowledgement);
        Assert.Contains("draft", vm.Review.CommandReview.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_with_nothing_to_change_stays_in_the_form_with_a_sentence()
    {
        var (vm, _, _) = await OpenAsync();
        await SelectAsync(vm, "team-baseline");
        vm.EditCommand.Execute(null);

        await vm.SubmitFormCommand.ExecuteAsync(null);

        Assert.True(vm.Form.IsOpen);
        Assert.False(vm.Review.IsOpen);
        Assert.Contains("at least one", vm.Form.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_the_cli_could_not_be_trusted_with_is_listed_but_never_acted_on()
    {
        var (vm, cli, _) = await OpenAsync(c => c.ListText = "Available policies:\r\n\r\n    -rf\r\n    default [built-in] [active]\r\n");

        vm.SelectedRow = Row(vm, "-rf");
        await Task.Delay(50);

        Assert.False(vm.CanActivate);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanDelete);
        Assert.DoesNotContain(cli.Calls, c => c.Contains("-rf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Esc_closes_the_review_then_the_form_then_the_output_then_the_detail()
    {
        var (vm, _, _) = await OpenAsync();
        await SelectAsync(vm, "strict");
        await vm.ValidateCommand.ExecuteAsync(null);
        vm.CreateCommand.Execute(null);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.Form.IsOpen);
        Assert.True(vm.HandleEscape());
        Assert.False(vm.HasOutput);
        Assert.True(vm.HandleEscape());
        Assert.Null(vm.SelectedRow);
        Assert.False(vm.HandleEscape());
    }

    // ------------------------------------------------------------------ registration

    [Fact]
    public void The_panel_is_in_the_catalog_with_a_chord_and_a_palette_entry()
    {
        var catalog = new PanelCatalog(_services);

        var descriptor = catalog.ById("policies");
        Assert.NotNull(descriptor);
        Assert.Equal("Configure", descriptor.Group);
        Assert.Equal(typeof(DefenseClaw.App.Views.Panels.PoliciesPanel), descriptor.ViewType);
        Assert.Equal("Ctrl+Shift+4", ShellShortcuts.PanelChordText(catalog.SidebarOrder.ToList().FindIndex(p => p.Id == "policies")));
        Assert.Contains(ShellCommandRegistry.BuildPanelCommands(catalog, _ => { }), c => c.Title == "Go to Policies");
    }
}
