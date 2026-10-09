using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// The Policies panel on a runtime that has the policy model (CUST-293), as a view-model: what it reads, the six views it shows (Windows has no
/// Sandbox packs view), the scopes with what each inherits, the changes the runtime's model offers and what each puts in front of the operator
/// before anything runs, and when every change is off. The CLI is a script handed to the view-model (<see cref="PolicyModelViewModel.RunCli"/>
/// for reads, <see cref="DiscoverActionReview.RunStep"/> for a confirmed change) over the synthetic fixtures of the pinned runtime, so no process
/// starts and nothing touches an install.
/// </summary>
public sealed class PolicyModelPanelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public PolicyModelPanelTests()
    {
        _services = TestServices.Create(_temp, runtimeProbeRunner: ProbeRunner(Pinned));
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ a panel over the script

    private async Task<(PolicyModelViewModel Vm, ScriptedCli Cli, List<string> Ran)> OpenAsync(string scenario = Fresh, Action<ScriptedCli>? script = null)
    {
        var cli = new ScriptedCli(scenario);
        script?.Invoke(cli);
        var ran = new List<string>();
        var vm = new PolicyModelViewModel(_services, SevenViewPolicyBackend.Instance, new FixtureData()) { RunCli = cli.Run };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Result(0, argv, "ok"));
        };
        await vm.InitializeAsync();
        return (vm, cli, ran);
    }

    private static void Go(PolicyModelViewModel vm, string view) => vm.SelectedNav = vm.Nav.Single(n => n.View == view);

    private static void Pick(PolicyModelViewModel vm, string key) => vm.SelectedRow = vm.Rows.Single(r => r.Key == key);

    private static PolicyActionViewModel Act(PolicyModelViewModel vm, string group, string title) =>
        vm.ActionGroups.Single(g => g.Name == group).Actions.Single(a => a.Title == title);

    private static Task Run(PolicyModelViewModel vm, PolicyActionViewModel action) => vm.RunActionCommand.ExecuteAsync(action);

    private static string[] Cells(PolicyModelViewModel vm, string key) => vm.Rows.Single(r => r.Key == key).Cells.Select(c => c.Shown).ToArray();

    // ------------------------------------------------------------------ the views

    [Fact]
    public async Task The_views_are_the_six_the_runtime_shows_on_windows_with_their_counts_and_nothing_asks_about_sandboxes()
    {
        var (vm, cli, _) = await OpenAsync();

        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.Equal(new[] { "posture", "optin", "chains", "families", "policies", "packs" }, vm.Nav.Select(n => n.View).ToArray());
        Assert.Equal(new[] { "Posture", "Opt-in packs", "Chains", "Rule families", "Policies", "Rule packs" }, vm.Nav.Select(n => n.Title).ToArray());
        Assert.Equal(new[] { "1", "0/5", "26", "7", "3", "1" }, vm.Nav.Select(n => n.Badge).ToArray());
        Assert.DoesNotContain(vm.Nav, n => n.View.Contains("sandbox", StringComparison.OrdinalIgnoreCase) || n.Title.Contains("sandbox", StringComparison.OrdinalIgnoreCase));

        // Four reads, once each, none of them about sandboxes, none of them a change.
        Assert.Equal(
            new[] { "config show --section guardrail --format json", "guardrail list-packs --json", "guardrail protection list --json", "policy list --json" },
            cli.Calls.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(cli.Mutations);
        Assert.Equal("● default policy · default pack · 0 of 5 opt-in packs · 26 chains (4 can block)", vm.CaptionText);
    }

    [Theory]
    [InlineData("posture", "Scope,Mode,Blocks at,Alerts at,Approval,Rule pack,Opt-in", 1)]
    [InlineData("optin", "Pack,Covers,Rules,State", 6)]
    [InlineData("chains", "Effect,Chain,Severity,Domain", 26)]
    [InlineData("families", "Family,Rules,Enabled,What it catches", 7)]
    [InlineData("policies", "Active,Policy,Kind,LLM block,LLM alert,Install block,Firewall,Description", 3)]
    [InlineData("packs", "Scope,Pack,Source,Folder", 1)]
    public async Task Each_view_is_one_table_with_the_columns_and_rows_the_model_gives(string view, string headers, int rows)
    {
        var (vm, _, _) = await OpenAsync();

        Go(vm, view);

        Assert.Equal(view, vm.View);
        Assert.Equal(headers.Split(','), vm.Columns.Select(c => c.Header).ToArray());
        Assert.Equal(rows, vm.Rows.Count);
        Assert.False(vm.HasViewError);
        Assert.False(vm.ShowEmpty);
    }

    [Fact]
    public async Task The_status_line_is_the_runtimes_own_words_and_the_read_only_views_say_so()
    {
        var (vm, _, _) = await OpenAsync();

        Assert.Equal("LLM traffic (default policy): blocks CRITICAL, alerts MEDIUM+ · tool calls: per scope below", vm.Headline);
        Assert.False(vm.HasReadOnlyNote);

        Go(vm, "chains");
        Assert.Equal("26 bounded chains · ✓ 4 can block · ◐ 22 alert only · built in, read-only", vm.Headline);
        Assert.StartsWith("Built in and read-only", vm.ReadOnlyNote, StringComparison.Ordinal);
        Pick(vm, "chain.sensitive_sql_read_then_unbounded_delete_same_table");
        Assert.Empty(vm.ActionGroups);

        Go(vm, "families");
        Assert.StartsWith("Read-only", vm.ReadOnlyNote, StringComparison.Ordinal);
        Assert.Equal("Scope: global  ·  default pack", vm.Headline);
    }

    // ------------------------------------------------------------------ scopes and inheritance

    [Fact]
    public async Task The_scopes_are_the_global_default_and_each_connector_and_what_a_connector_inherits_is_shown()
    {
        var (vm, _, _) = await OpenAsync(Connectors);

        Assert.Equal(new[] { "global", "claudecode", "codex" }, vm.Scopes.ToArray());
        Assert.Equal(new[] { "global", "claudecode", "codex" }, vm.Rows.Select(r => r.Key).ToArray());

        // claudecode follows the global mode and pack and sets only its block level; codex sets its own mode, pack and alert level.
        Assert.Equal(new[] { "global", "action", "HIGH+", "MEDIUM+", "HIGH+", "default", "0 of 5" }, Cells(vm, "global"));
        Assert.Equal(new[] { "claudecode", "action", "MEDIUM+", "MEDIUM+", "HIGH+", "default", "0 of 5" }, Cells(vm, "claudecode"));
        Assert.Equal(new[] { "codex", "observe (own)", "HIGH+", "LOW+", "off", "strict+1 (own)", "1 of 5" }, Cells(vm, "codex"));

        Pick(vm, "claudecode");
        Assert.Equal("Posture · claudecode", vm.InspectorTitle);
        Assert.Contains(vm.InspectorLines, l => l.Text == "Tool calls (action, global mode):");
        Assert.Contains(vm.InspectorLines, l => l.Text == "Block level set for claudecode; alert level from the default pack.");
        Assert.Equal("Use the global level (HIGH+)", vm.ActionGroups.Single(g => g.Name == "Tool-call block level").Actions[^1].Title);

        Pick(vm, "codex");
        Assert.Contains(vm.InspectorLines, l => l.Text == "Tool calls (observe, its own mode):");
        Assert.Contains(vm.InspectorLines, l => l.Text == "Block level set globally; alert level set for codex.");
        Assert.Contains(vm.InspectorLines, l => l.Text.StartsWith("Rule pack: protected-codex = strict + 1 opt-in pack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choosing_a_posture_row_chooses_the_scope_the_opt_in_packs_and_rule_families_are_shown_for()
    {
        var (vm, _, _) = await OpenAsync(Connectors);

        Pick(vm, "codex");
        Assert.Equal("codex", vm.SelectedScope);

        Go(vm, "optin");
        Assert.True(vm.ShowScopeChooser);
        Assert.Equal("Scope: codex  ·  1 of 5 on", vm.Headline);
        Assert.Equal("on", Cells(vm, "kubernetes-production-protection")[3]);
        Assert.Equal("off", Cells(vm, "privacy-high-assurance")[3]);

        vm.SelectedScope = "claudecode";
        Assert.Equal("Scope: claudecode  ·  0 of 5 on", vm.Headline);
        Assert.Equal("off", Cells(vm, "kubernetes-production-protection")[3]);

        Go(vm, "families");
        Assert.Equal("Scope: claudecode  ·  default pack", vm.Headline);
        vm.SelectedScope = "codex";
        Assert.Equal("Scope: codex  ·  protected-codex pack", vm.Headline);

        // The chooser belongs to the views that follow one scope; posture lists every scope as a row.
        Go(vm, "posture");
        Assert.False(vm.ShowScopeChooser);
    }

    [Fact]
    public async Task A_single_connector_install_has_no_scope_chooser()
    {
        var (vm, _, _) = await OpenAsync();

        Go(vm, "optin");

        Assert.Equal(new[] { "global" }, vm.Scopes.ToArray());
        Assert.False(vm.ShowScopeChooser);
    }

    // ------------------------------------------------------------------ tool-call levels are not the policy's LLM thresholds

    [Fact]
    public async Task Tool_call_levels_and_the_llm_thresholds_of_a_policy_are_kept_apart()
    {
        var (vm, _, _) = await OpenAsync();

        Pick(vm, "global");
        var posture = vm.ActionGroups.Select(g => g.Name).ToArray();
        Assert.Contains("Tool-call block level", posture);
        Assert.Contains("Tool-call alert level", posture);
        Assert.DoesNotContain(posture, g => g.StartsWith("LLM", StringComparison.Ordinal));
        Assert.Contains(vm.InspectorLines, l => l.Text.StartsWith("LLM traffic through the guardrail proxy: the default policy", StringComparison.Ordinal));

        Go(vm, "policies");
        Assert.Contains("LLM block", vm.Columns.Select(c => c.Header));
        Assert.DoesNotContain("Blocks at", vm.Columns.Select(c => c.Header));
        Pick(vm, "strict");
        var policy = vm.ActionGroups.Select(g => g.Name).ToArray();
        Assert.Equal(new[] { "Activate", "LLM block level", "LLM alert level" }, policy);
        Assert.DoesNotContain(policy, g => g.StartsWith("Tool-call", StringComparison.Ordinal));
        Assert.All(Act(vm, "LLM block level", "HIGH+").Action.Consequence.Details.Take(1), d => Assert.Contains("tool calls keep each scope's levels", d, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ every change is reviewed

    [Fact]
    public async Task A_change_opens_the_review_of_the_exact_command_and_runs_only_after_it_is_confirmed()
    {
        var (vm, cli, ran) = await OpenAsync(Connectors);
        Pick(vm, "claudecode");

        // Alerting from LOW+ instead of MEDIUM+ watches more, so nothing is weakened.
        await Run(vm, Act(vm, "Tool-call alert level", "LOW+"));

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw guardrail alert-at LOW --connector claudecode", review.CommandText);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.True(review.RestartsGateway);
        Assert.Equal("Set claudecode's alert level to LOW+?", review.Title);
        Assert.False(vm.Review.RequiresAcknowledgement);
        Assert.Empty(ran);
        Assert.Empty(cli.Mutations);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw guardrail alert-at LOW --connector claudecode" }, ran);
        Assert.Equal(2, cli.Calls.Count(c => c == "policy list --json"));
        Assert.Equal("Done", vm.NoticeTitle);
        Assert.True(vm.HasNotice);
    }

    [Fact]
    public async Task The_review_carries_the_models_own_account_of_the_consequences()
    {
        var (vm, _, _) = await OpenAsync(Connectors);
        Pick(vm, "claudecode");

        await Run(vm, Act(vm, "Tool-call alert level", "LOW+"));

        var summary = vm.Review.CommandReview!.Summary;
        Assert.StartsWith("alert level: MEDIUM+ → LOW+", summary, StringComparison.Ordinal);
        Assert.Contains("- Only this connector changes; a running gateway restarts to apply it.", summary, StringComparison.Ordinal);
        Assert.Contains("- These are the levels for tool calls. The policy's levels for LLM traffic through the guardrail proxy are separate (Policies view).", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_that_protects_less_names_what_it_weakens_and_needs_the_acknowledgement()
    {
        var (vm, cli, ran) = await OpenAsync(Connectors);
        Pick(vm, "global");

        var observe = Act(vm, "Mode", "Log only (observe)");
        Assert.True(observe.Weakens);
        Assert.True(observe.IsEnabled);
        await Run(vm, observe);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw guardrail mode observe", review.CommandText);
        Assert.Contains(review.Warnings, w => w.Title == "Reduces protection" && w.Message.StartsWith("This weakens protection:", StringComparison.Ordinal));
        Assert.True(vm.Review.RequiresAcknowledgement);
        Assert.Equal(PolicyModelViewModel.WeakeningAcknowledgement, vm.Review.AcknowledgementText);

        // Not until it is ticked - not by the button, and not by invoking the command directly.
        Assert.False(vm.Review.ConfirmCommand.CanExecute(null));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(ran);

        vm.Review.IsAcknowledged = true;
        Assert.True(vm.Review.ConfirmCommand.CanExecute(null));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw guardrail mode observe" }, ran);
        Assert.Equal(2, cli.Calls.Count(c => c == "guardrail list-packs --json"));
    }

    [Fact]
    public async Task Turning_an_opt_in_pack_off_protects_less_and_on_asks_for_nothing_extra()
    {
        var (vm, _, _) = await OpenAsync(Connectors);
        Pick(vm, "codex");
        Go(vm, "optin");
        Assert.Equal("codex", vm.SelectedScope);

        Pick(vm, "kubernetes-production-protection");
        var off = Act(vm, "Opt-in pack", "Turn off");
        Assert.True(off.Weakens);
        await Run(vm, off);
        Assert.Equal("defenseclaw guardrail protection disable kubernetes-production-protection --connector codex", vm.Review.CommandReview!.CommandText);
        Assert.True(vm.Review.RequiresAcknowledgement);
        vm.Review.HandleEscape();

        Pick(vm, "cloud-production-protection");
        var on = Act(vm, "Opt-in pack", "Turn on");
        Assert.False(on.Weakens);
        await Run(vm, on);
        Assert.Equal("defenseclaw guardrail protection enable cloud-production-protection --connector codex", vm.Review.CommandReview!.CommandText);
        Assert.False(vm.Review.RequiresAcknowledgement);
    }

    [Fact]
    public async Task A_setting_already_in_force_cannot_be_chosen_again()
    {
        var (vm, _, _) = await OpenAsync();
        Pick(vm, "global");

        var current = Act(vm, "Mode", "Log only (observe)");

        Assert.True(current.IsCurrent);
        Assert.False(current.IsEnabled);
        Assert.Equal("This is already in force.", current.ToolTip);
        await Run(vm, current);
        Assert.False(vm.Review.IsOpen);
    }

    // ------------------------------------------------------------------ a rule pack must validate before it is switched to

    [Fact]
    public async Task A_rule_pack_that_validates_is_switched_to_through_the_review_with_the_validation_in_it()
    {
        var (vm, cli, ran) = await OpenAsync();
        Pick(vm, "global");

        await Run(vm, Act(vm, "Rule pack", "strict"));

        Assert.Contains(@"guardrail validate-pack C:\Users\operator\.defenseclaw\policies\guardrail\strict --json", cli.Calls);
        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw guardrail use-pack strict", review.CommandText);
        Assert.Contains("- Validation: valid: 29/31 rules enabled across 8 files · digest 0123456789ab.", review.Summary, StringComparison.Ordinal);
        Assert.True(review.RestartsGateway);
        Assert.Empty(ran);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "defenseclaw guardrail use-pack strict" }, ran);
    }

    [Theory]
    [InlineData(1, "rules.yaml declares an unknown severity")]
    [InlineData(2, "the gateway binary was not found")]
    public async Task A_rule_pack_that_does_not_validate_or_cannot_be_checked_is_never_offered(int exit, string reason)
    {
        var (vm, cli, ran) = await OpenAsync(script: c => c.PackValidationExit = exit);
        Pick(vm, "global");

        await Run(vm, Act(vm, "Rule pack", "strict"));

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Switching is withheld", vm.NoticeTitle);
        Assert.Contains(reason, vm.OutputText, StringComparison.Ordinal);
        Assert.Equal("Bad", vm.OutputTone);
        Assert.DoesNotContain(cli.Mutations, c => c.Contains("use-pack", StringComparison.Ordinal));
        Assert.Empty(ran);
    }

    [Fact]
    public async Task A_validation_that_did_not_complete_withholds_the_switch_too()
    {
        var (vm, cli, _) = await OpenAsync(script: c => c.DidNotComplete.Add(@"guardrail validate-pack C:\Users\operator\.defenseclaw\policies\guardrail\strict --json"));
        Pick(vm, "global");

        await Run(vm, Act(vm, "Rule pack", "strict"));

        Assert.False(vm.Review.IsOpen);
        Assert.Contains("timed out", vm.OutputText, StringComparison.Ordinal);
        Assert.DoesNotContain(cli.Calls, c => c.Contains("use-pack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validating_a_rule_pack_is_a_read_that_shows_the_validators_answer_and_changes_nothing()
    {
        var (vm, cli, _) = await OpenAsync();
        Pick(vm, "global");

        var validate = Act(vm, "Validate a rule pack", "strict");
        Assert.True(validate.IsRead);
        await Run(vm, validate);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Ok", vm.OutputTone);
        Assert.StartsWith("valid: 29/31 rules enabled across 8 files", vm.OutputText, StringComparison.Ordinal);
        Assert.Empty(cli.Mutations);
    }

    // ------------------------------------------------------------------ a policy is validated before it is activated

    [Fact]
    public async Task Activating_a_policy_validates_the_bundle_first_and_a_failure_stops_it()
    {
        var (vm, cli, ran) = await OpenAsync(script: c => c.PolicyValidateExit = 1);
        Go(vm, "policies");
        Pick(vm, "strict");

        await Run(vm, Act(vm, "Activate", "Activate"));

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Activation is withheld", vm.NoticeTitle);
        Assert.Contains("opa", vm.OutputText, StringComparison.Ordinal);
        Assert.Contains("OPA", vm.OutputNote, StringComparison.Ordinal);
        Assert.Contains("policy validate", cli.Calls);
        Assert.Empty(ran);
    }

    [Fact]
    public async Task A_stronger_policy_is_activated_through_the_review_with_no_acknowledgement()
    {
        var (vm, cli, ran) = await OpenAsync();
        Go(vm, "policies");
        Pick(vm, "strict");

        var activate = Act(vm, "Activate", "Activate");
        Assert.False(activate.Weakens);
        await Run(vm, activate);

        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw policy activate strict", review.CommandText);
        Assert.Contains("- Validation: policy validate passed.", review.Summary, StringComparison.Ordinal);
        Assert.False(review.RestartsGateway);
        Assert.False(vm.Review.RequiresAcknowledgement);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw policy activate strict" }, ran);
        Assert.Equal(2, cli.Calls.Count(c => c == "policy list --json"));
    }

    [Fact]
    public async Task A_weaker_policy_needs_the_acknowledgement_before_it_is_activated()
    {
        var (vm, _, ran) = await OpenAsync();
        Go(vm, "policies");
        Pick(vm, "permissive");

        var activate = Act(vm, "Activate", "Activate");
        Assert.True(activate.Weakens);
        await Run(vm, activate);

        Assert.True(vm.Review.RequiresAcknowledgement);
        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Reduces protection" && w.Message.Contains("weakens protection", StringComparison.Ordinal));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(ran);

        vm.Review.IsAcknowledged = true;
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "defenseclaw policy activate permissive" }, ran);
    }

    [Fact]
    public async Task A_policys_llm_thresholds_are_edited_with_policy_edit_guardrail_and_a_draft_says_so()
    {
        var (vm, _, ran) = await OpenAsync();
        Go(vm, "policies");
        Pick(vm, "strict");

        await Run(vm, Act(vm, "LLM block level", "HIGH+"));

        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw policy edit guardrail --block-threshold 3 -p strict", review.CommandText);
        Assert.Contains("- The strict policy isn't active, so nothing changes until you activate it.", review.Summary, StringComparison.Ordinal);
        Assert.False(vm.Review.RequiresAcknowledgement);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "defenseclaw policy edit guardrail --block-threshold 3 -p strict" }, ran);
    }

    [Fact]
    public async Task Lowering_the_active_policys_alert_threshold_protects_less_and_asks_for_the_tick()
    {
        var (vm, _, _) = await OpenAsync();
        Go(vm, "policies");
        Pick(vm, "default");

        var weaker = Act(vm, "LLM alert level", "HIGH+");
        Assert.True(weaker.Weakens);
        await Run(vm, weaker);

        Assert.True(vm.Review.RequiresAcknowledgement);
        Assert.Equal("defenseclaw policy edit guardrail --alert-threshold 3 -p default", vm.Review.CommandReview!.CommandText);
    }

    // ------------------------------------------------------------------ the data may not authorize a change

    [Fact]
    public async Task A_partial_read_shows_what_it_got_and_turns_every_change_off_with_the_reason()
    {
        var (vm, cli, _) = await OpenAsync(script: c => c.Overrides["guardrail list-packs --json"] = (1, string.Empty, "Traceback: fixture failure"));

        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.True(vm.Trust.IsPartial);
        Assert.True(vm.HasReadWarning);
        Assert.Equal("Incomplete read", vm.ReadWarningTitle);
        Assert.Contains("guardrail list-packs --json", vm.ReadWarning, StringComparison.Ordinal);
        Assert.False(vm.CanChange);
        Assert.Contains("incomplete", vm.ChangesBlockedReason, StringComparison.Ordinal);

        // The views that could be read are there; the one that could not says why.
        Go(vm, "policies");
        Assert.Equal(3, vm.Rows.Count);
        Go(vm, "packs");
        Assert.True(vm.HasViewError);
        Assert.Equal("Bad", vm.HeadlineTone);
        Assert.StartsWith("Could not read rule packs:", vm.Headline, StringComparison.Ordinal);
        Assert.Empty(vm.Rows);

        Go(vm, "policies");
        Pick(vm, "strict");
        var activate = Act(vm, "Activate", "Activate");
        Assert.False(activate.IsEnabled);
        Assert.Contains("incomplete", activate.ToolTip, StringComparison.Ordinal);
        Assert.True(vm.HasActionsNote);

        // Asking anyway is refused with the same reason, and runs nothing.
        cli.Calls.Clear();
        await Run(vm, activate);
        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Changes are off", vm.NoticeTitle);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_good_rows_and_turns_every_change_off()
    {
        var (vm, cli, _) = await OpenAsync(Connectors);
        Pick(vm, "codex");
        Assert.True(vm.CanChange);

        foreach (var read in new[] { "policy list --json", "guardrail list-packs --json", "guardrail protection list --json", "config show --section guardrail --format json" })
        {
            cli.Overrides[read] = (2, string.Empty, "Traceback: fixture failure");
        }

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("codex", vm.SelectedRow?.Key);
        Assert.Equal("Showing an older read", vm.ReadWarningTitle);
        Assert.Contains("Showing the last good read", vm.ReadWarning, StringComparison.Ordinal);
        Assert.False(vm.CanChange);
        Assert.Contains("last read failed", vm.ChangesBlockedReason, StringComparison.Ordinal);
        Assert.All(vm.ActionGroups.SelectMany(g => g.Actions).Where(a => !a.IsRead), a => Assert.False(a.IsEnabled));
    }

    [Fact]
    public async Task A_first_read_that_fails_entirely_is_an_error_state_with_nothing_to_change()
    {
        var (vm, _, _) = await OpenAsync(script: c =>
        {
            foreach (var read in new[] { "policy list --json", "guardrail list-packs --json", "guardrail protection list --json", "config show --section guardrail --format json" })
            {
                c.Overrides[read] = (2, string.Empty, "Traceback: fixture failure");
            }
        });

        Assert.Equal(PoliciesState.Error, vm.State);
        Assert.True(vm.ShowError);
        Assert.Equal("Could not read the policies", vm.ErrorTitle);
        Assert.False(vm.CanChange);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task A_read_that_has_gone_old_stops_authorizing_changes_at_the_moment_of_the_request()
    {
        var clock = new ManualClock();
        var (vm, cli, _) = await OpenAsync();
        vm.Trust = new CatalogTrust(time: clock);
        vm.Trust.MarkComplete();
        Pick(vm, "global");
        var mode = Act(vm, "Mode", "Enforce (action)");
        Assert.True(mode.IsEnabled);

        clock.Advance(TimeSpan.FromMinutes(11));
        cli.Calls.Clear();
        await Run(vm, mode);

        Assert.False(vm.Review.IsOpen);
        Assert.Contains("ago", vm.NoticeMessage, StringComparison.Ordinal);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task A_configuration_that_moved_since_the_read_turns_changes_off_even_before_anyone_heard_about_it()
    {
        var (vm, cli, _) = await OpenAsync(Connectors);
        Pick(vm, "global");
        var observe = Act(vm, "Mode", "Log only (observe)");
        Assert.True(observe.IsEnabled);

        File.WriteAllText(_services.Paths.ConfigFilePath, "guardrail:\n  mode: observe\n");
        cli.Calls.Clear();
        await Run(vm, observe);

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Changes are off", vm.NoticeTitle);
        Assert.Contains("config.yaml", vm.NoticeMessage, StringComparison.Ordinal);
        Assert.Empty(cli.Calls);
        Assert.False(observe.IsEnabled);
    }

    [Fact]
    public async Task A_change_to_the_configuration_while_the_panel_is_on_screen_reads_the_settings_again()
    {
        var (vm, cli, _) = await OpenAsync(Connectors);
        vm.SetActive(true);
        cli.Calls.Clear();

        File.WriteAllText(_services.Paths.ConfigFilePath, "guardrail:\n  mode: action\n");
        _services.ReloadConfig();
        for (var i = 0; i < 2000 && (cli.Calls.Count < 4 || vm.IsBusy); i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(4, cli.Calls.Count);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanChange);
        vm.SetActive(false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_first_visit_reads_once_however_many_things_ask_for_the_first_read(bool activatedFirst)
    {
        var cli = new ScriptedCli();
        var vm = new PolicyModelViewModel(_services, SevenViewPolicyBackend.Instance, new FixtureData()) { RunCli = cli.Run };

        if (activatedFirst)
        {
            vm.SetActive(true);
            await vm.InitializeAsync();
        }
        else
        {
            await vm.InitializeAsync();
            vm.SetActive(true);
        }

        Assert.Equal(PoliciesState.Loaded, vm.State);
        Assert.Equal(1, cli.Calls.Count(c => c == "policy list --json"));
        vm.SetActive(false);
    }

    [Fact]
    public async Task A_change_confirmed_while_a_read_is_running_reads_the_settings_once_more_after_it()
    {
        var (vm, cli, ran) = await OpenAsync(Connectors);
        Pick(vm, "claudecode");
        await Run(vm, Act(vm, "Tool-call alert level", "LOW+"));
        Assert.True(vm.Review.IsOpen);

        // A read starts (held at its first command) while the review is still open ...
        var gate = new TaskCompletionSource();
        var held = false;
        vm.RunCli = async (argv, options) =>
        {
            if (!held && string.Join(' ', argv) == "policy list --json")
            {
                held = true;
                await gate.Task;
            }

            return cli.Handle(argv);
        };
        cli.Calls.Clear();
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        // ... and the change is confirmed meanwhile: that read may have begun before the change, so one more follows it.
        var confirmed = vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.False(confirmed.IsCompleted);
        gate.SetResult();
        await confirmed;
        await refresh;

        Assert.Equal(new[] { "defenseclaw guardrail alert-at LOW --connector claudecode" }, ran);
        Assert.Equal(2, cli.Calls.Count(c => c == "policy list --json"));
        Assert.False(vm.IsBusy);
        Assert.Equal("Done", vm.NoticeTitle);
    }

    [Fact]
    public async Task A_configuration_change_while_a_review_is_open_waits_for_the_review_and_then_reads_again()
    {
        var (vm, cli, _) = await OpenAsync(Connectors);
        vm.SetActive(true);
        Pick(vm, "claudecode");
        await Run(vm, Act(vm, "Tool-call alert level", "LOW+"));
        Assert.True(vm.Review.IsOpen);
        cli.Calls.Clear();

        File.WriteAllText(_services.Paths.ConfigFilePath, "guardrail:\n  mode: action\n");
        _services.ReloadConfig();

        // The notification is marshalled to the UI thread when the process has one (other tests of the suite make one), so wait for it.
        for (var i = 0; i < 2000 && vm.CanChange; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(vm.CanChange);
        Assert.Empty(cli.Calls);

        vm.Review.HandleEscape();
        for (var i = 0; i < 2000 && (cli.Calls.Count < 4 || vm.IsBusy); i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(4, cli.Calls.Count);
        Assert.True(vm.CanChange);
        vm.SetActive(false);
    }

    // ------------------------------------------------------------------ what the panel may start

    [Fact]
    public async Task The_read_door_runs_the_catalog_reads_and_the_two_validations_and_refuses_everything_else()
    {
        var (vm, cli, _) = await OpenAsync();

        foreach (var argv in new[]
                 {
                     new[] { "guardrail", "mode", "observe" },
                     new[] { "guardrail", "use-pack", "strict" },
                     new[] { "guardrail", "protection", "enable", "x" },
                     new[] { "policy", "activate", "strict" },
                     new[] { "policy", "edit", "guardrail", "--block-threshold", "2" },
                     new[] { "policy", "delete", "x" },
                     new[] { "policy", "list" },
                     new[] { "policy", "validate", "--rego-dir", "x" },
                     new[] { "guardrail", "validate-pack", "relative", "--json" },
                     new[] { "guardrail", "validate-pack", @"C:\x", "--json", "--extra" },
                     new[] { "sandbox", "pack", "list" },
                     new[] { "doctor" },
                 })
        {
            Assert.Throws<InvalidOperationException>(() => { _ = vm.RunReadAsync(argv); });
        }

        cli.Calls.Clear();
        foreach (var argv in new[]
                 {
                     new[] { "policy", "list", "--json" },
                     new[] { "guardrail", "list-packs", "--json" },
                     new[] { "guardrail", "protection", "list", "--json" },
                     new[] { "config", "show", "--section", "guardrail", "--format", "json" },
                     new[] { "policy", "validate" },
                     new[] { "guardrail", "validate-pack", DefaultPackFolder, "--json" },
                 })
        {
            _ = await vm.RunReadAsync(argv);
        }

        Assert.Equal(6, cli.Calls.Count);
        Assert.Empty(cli.Mutations);
    }

    [Fact]
    public async Task A_pack_folder_the_cli_would_rewrite_is_refused_before_anything_runs()
    {
        // The CLI expands %VARIABLES% in every argument on Windows, so "...\%TEMP%\strict" would arrive as another folder than the one reviewed.
        var packs = Fixture("cli/guardrail-list-packs.json").Replace(@"guardrail\\strict", @"guardrail\\%TEMP%\\strict", StringComparison.Ordinal);
        var (vm, cli, ran) = await OpenAsync(script: c => c.Overrides["guardrail list-packs --json"] = (0, packs, string.Empty));
        Pick(vm, "global");
        cli.Calls.Clear();

        await Run(vm, Act(vm, "Rule pack", "strict"));

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Command refused", vm.NoticeTitle);
        Assert.Contains("%TEMP%", vm.NoticeMessage, StringComparison.Ordinal);
        Assert.Empty(cli.Calls);
        Assert.Empty(ran);
    }

    // ------------------------------------------------------------------ the rows

    [Fact]
    public async Task The_filter_narrows_the_rows_says_when_nothing_matches_and_keeps_the_selection_while_the_row_is_there()
    {
        var (vm, _, _) = await OpenAsync();
        Go(vm, "policies");
        Pick(vm, "strict");

        vm.SearchText = "maximum";
        Assert.Equal(new[] { "strict" }, vm.Rows.Select(r => r.Key).ToArray());
        Assert.Equal("strict", vm.SelectedRow?.Key);
        Assert.False(vm.NoMatches);

        vm.SearchText = "nothing like this";
        Assert.Empty(vm.Rows);
        Assert.True(vm.NoMatches);
        Assert.False(vm.ShowEmpty);
        Assert.Null(vm.SelectedRow);

        vm.SearchText = string.Empty;
        Assert.Equal(3, vm.Rows.Count);
        Assert.False(vm.NoMatches);
    }

    [Fact]
    public async Task A_refresh_keeps_the_view_the_scope_and_the_selected_row()
    {
        var (vm, cli, _) = await OpenAsync(Connectors);
        Go(vm, "optin");
        vm.SelectedScope = "codex";
        Pick(vm, "kubernetes-production-protection");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("optin", vm.View);
        Assert.Equal("codex", vm.SelectedScope);
        Assert.Equal("kubernetes-production-protection", vm.SelectedRow?.Key);
        Assert.Equal(2, cli.Calls.Count(c => c == "policy list --json"));
    }

    [Fact]
    public async Task Escape_closes_the_review_then_the_notice_then_the_output_then_the_selection()
    {
        var (vm, _, _) = await OpenAsync(script: c => c.PackValidationExit = 1);
        Pick(vm, "global");
        await Run(vm, Act(vm, "Rule pack", "strict"));
        Assert.True(vm.HasNotice);
        Assert.True(vm.HasOutput);

        await Run(vm, Act(vm, "Tool-call alert level", "LOW+"));
        Assert.True(vm.Review.IsOpen);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.HasNotice);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.HasNotice);
        Assert.True(vm.HasOutput);

        Assert.True(vm.HandleEscape());
        Assert.False(vm.HasOutput);
        Assert.NotNull(vm.SelectedRow);

        Assert.True(vm.HandleEscape());
        Assert.Null(vm.SelectedRow);
        Assert.False(vm.HandleEscape());
    }
}
