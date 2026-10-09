using System.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// "What do you want to do?" (CUST-269): the goal page in front of the llm, guardrail, hook-connector and splunk wizards (and the goals an AI
/// discovery dialog gets), built over the fields the installed CLI's help produced. Choosing a goal seeds its presets and narrows the pages to
/// what it needs; Advanced narrows nothing; a goal never puts a flag into a command that the CLI does not have.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class WizardGoalTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();
    private readonly List<WizardViewModel> _viewModels = new();

    public void Dispose()
    {
        UiThread.Run(() =>
        {
            foreach (var vm in _viewModels)
            {
                vm.Dispose();
            }
        });

        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>A Docker look that answers "could not look" and starts nothing: no wizard in this class may ask the real one.</summary>
    private sealed class NeverDocker : IDockerProbe
    {
        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DockerStatus(DockerState.Unknown, "Docker was not looked at.", Array.Empty<string>()));
    }

    private WizardViewModel Open(WizardDefinition definition, string? config = null, IDockerProbe? docker = null)
    {
        var services = TestServices.Create(_temp, config);
        _services.Add(services);
        var vm = UiThread.Run(() => new WizardViewModel(services, definition, docker ?? new NeverDocker()));
        _viewModels.Add(vm);
        return vm;
    }

    private static Task<WizardDefinition> Llm() => CatalogHelp.RealAsync("llm");

    private static Task<WizardDefinition> Guardrail() => CatalogHelp.RealAsync("guardrail");

    private static async Task<WizardDefinition> ClaudeCode()
    {
        var definition = await CatalogHelp.RealAsync("claude-code");
        Assert.True(definition.IsCurated);
        return definition;
    }

    private static string[] Ids(WizardDefinition definition) => definition.Goals.Select(g => g.Id).ToArray();

    private static string[] ShownFlags(WizardViewModel vm) =>
        vm.Steps.Where(s => s.IsVisible).SelectMany(s => s.Fields).Where(f => f.IsVisible && f.Field.Flag is not null).Select(f => f.Field.Flag!).ToArray();

    // ------------------------------------------------------------------ which wizards have goals, and what they are

    [Fact]
    public async Task Each_wizard_with_goals_offers_the_choices_its_menu_has_and_ends_with_advanced()
    {
        Assert.Equal(new[] { "main", "judge", "regional", "instance", "test", "advanced" }, Ids(await Llm()));
        Assert.Equal(
            new[] { "mode", "judge", "cisco", "hitl", "detection", "rule-pack", "disable", "advanced" },
            Ids(await Guardrail()));
        Assert.Equal(new[] { "setup", "mode", "hitl", "rules", "judge", "advanced" }, Ids(await ClaudeCode()));
    }

    [Fact]
    public async Task The_page_that_asks_is_the_first_page_and_holds_one_synthetic_choice_of_all_the_goals()
    {
        var definition = await Llm();

        var first = definition.Steps[0];
        Assert.Equal(WizardGoals.StepId, first.Id);
        var field = Assert.Single(first.Fields);
        Assert.Equal(WizardGoals.FieldId, field.Id);
        Assert.True(field.IsSynthetic);
        Assert.Null(field.Flag);
        Assert.Equal(definition.Goals.Select(g => g.Id).ToArray(), field.Choices.Select(c => c.Value).ToArray());
        Assert.All(definition.Goals, g => Assert.False(string.IsNullOrWhiteSpace(g.Label)));
    }

    [Fact]
    public async Task A_wizard_the_menu_does_not_cover_has_no_goal_page()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "mcp-scanner",
            new Dictionary<string, string> { ["mcp-scanner"] = CatalogHelp.Screen("mcp-scanner", "--analyzers TEXT  Analyzers.", "--verify / --no-verify  Verify.") });

        Assert.Empty(definition.Goals);
        Assert.DoesNotContain(definition.Steps, s => s.Id == WizardGoals.StepId);
    }

    [Fact]
    public async Task Advanced_narrows_nothing_so_it_shows_what_no_goal_shows()
    {
        var definition = await Llm();
        var none = WizardAnswers.Pages(definition, WizardAnswers.Start(definition));
        var advanced = WizardAnswers.Pages(definition, WizardAnswers.Start(definition, (WizardGoals.FieldId, WizardGoals.AdvancedId)));

        Assert.Equal(none, advanced);
        Assert.Contains(WizardPages.LlmKey, advanced);
        Assert.Contains("more-page-1", advanced);
    }

    // ------------------------------------------------------------------ over the fields the CLI has

    [Fact]
    public async Task A_goals_flag_that_the_installed_cli_does_not_have_is_dropped_and_a_goal_with_nothing_left_goes()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "llm",
            new Dictionary<string, string>
            {
                ["llm"] = CatalogHelp.Screen(
                    "llm",
                    "--provider [anthropic|openai]  Provider.",
                    "--model TEXT  Model.",
                    "--non-interactive  Never prompt."),
            });

        // "main" keeps the two flags it can still ask for, and loses the role it would have set.
        var main = definition.Goals.Single(g => g.Id == "main");
        Assert.Equal(new[] { "--provider", "--model" }, main.Flags);
        Assert.Empty(main.Presets);

        // A goal is about something: no --ping, so no "test my connection"; no --role, so no judge; no --instance-name, so no instance.
        Assert.Equal(new[] { "main", "advanced" }, Ids(definition));
        Assert.DoesNotContain("test", Ids(definition));
        Assert.DoesNotContain("judge", Ids(definition));
        Assert.DoesNotContain("instance", Ids(definition));
        Assert.All(definition.Goals.SelectMany(g => g.Flags), flag => Assert.Contains(flag, definition.AllFields.Select(f => f.Flag)));
    }

    [Fact]
    public async Task A_wizard_none_of_whose_goals_can_be_honoured_keeps_its_first_page()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "llm",
            new Dictionary<string, string> { ["llm"] = CatalogHelp.Screen("llm", "--timeout INTEGER  Timeout.", "--non-interactive  Never prompt.") });

        Assert.Empty(definition.Goals);
        Assert.NotEqual(WizardGoals.StepId, definition.Steps[0].Id);
    }

    [Fact]
    public async Task A_preset_the_control_cannot_hold_is_dropped()
    {
        // This build of the CLI has no judge role: --role takes unified or agent only.
        var definition = await CatalogHelp.DefinitionAsync(
            "llm",
            new Dictionary<string, string>
            {
                ["llm"] = CatalogHelp.Screen(
                    "llm",
                    "--role [unified|agent]  Where to write.  [default: unified]",
                    "--provider [anthropic|openai]  Provider.",
                    "--model TEXT  Model.",
                    "--non-interactive  Never prompt."),
            });

        var judge = definition.Goals.Single(g => g.Id == "judge");

        Assert.DoesNotContain("--role", judge.Presets.Keys);
        Assert.Contains("--provider", judge.Presets.Keys);
    }

    [Fact]
    public async Task Hook_connectors_get_the_same_goals_by_shape_whatever_they_are_called()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "hermes",
            new Dictionary<string, string>
            {
                ["hermes"] = CatalogHelp.Screen(
                    "hermes",
                    "--mode [observe|action]  Policy mode.  [default: observe]",
                    "--fail-mode [open|closed]  Fail mode.",
                    "--rule-pack [default|strict|permissive]  Rule pack.",
                    "--human-approval / --no-human-approval  Human approval.",
                    "-y, --yes  Do not prompt."),
            });

        Assert.Equal(new[] { "setup", "mode", "hitl", "rules", "advanced" }, Ids(definition));
    }

    [Fact]
    public void An_ai_discovery_dialog_over_the_enable_flags_gets_the_tuning_goals()
    {
        var help = SetupHelpParser.Parse(CatalogHelp.Real("agent-discovery-enable"), commandDepth: 3);
        var (steps, _) = WizardStepFactory.Build("agent-discovery", help);

        var goals = WizardGoals.For("agent-discovery", steps);

        Assert.Equal(new[] { "cadence", "scope", "sources", "advanced" }, goals.Select(g => g.Id).ToArray());
        Assert.Equal(new[] { "--mode", "--scan-interval-min", "--process-interval-s" }, goals[0].Flags);
        Assert.Contains("--scan-roots", goals[1].Flags);
        Assert.Contains("--include-shell-history", goals[2].Flags);
        Assert.Equal(WizardFieldKind.Integer, steps.SelectMany(s => s.Fields).Single(f => f.Flag == "--scan-interval-min").Kind);
    }

    // ------------------------------------------------------------------ narrowing

    [Fact]
    public async Task A_goal_shows_only_the_fields_it_names_and_what_its_pages_hold()
    {
        var definition = await Llm();
        var values = WizardAnswers.Start(definition, (WizardGoals.FieldId, "main"), ("provider", "anthropic"));

        Assert.Equal(new[] { WizardGoals.StepId, WizardPages.LlmProvider, WizardPages.LlmKey }, WizardAnswers.Pages(definition, values));
        Assert.Equal(new[] { "provider", "model" }, WizardAnswers.Shown(definition, values, WizardPages.LlmProvider));
        Assert.Equal(new[] { "api-key-env", "api-key", "base-url" }, WizardAnswers.Shown(definition, values, WizardPages.LlmKey));
    }

    [Fact]
    public async Task A_goal_that_names_the_provider_groups_shows_the_one_the_provider_picks()
    {
        var definition = await Llm();

        var bedrock = WizardAnswers.Start(definition, (WizardGoals.FieldId, "regional"), ("provider", "bedrock"));
        Assert.Equal(
            new[] { WizardGoals.StepId, WizardPages.LlmProvider, WizardPages.LlmBedrock, WizardPages.LlmTls },
            WizardAnswers.Pages(definition, bedrock));

        var azure = WizardAnswers.Start(definition, (WizardGoals.FieldId, "regional"), ("provider", "azure"));
        Assert.Contains(WizardPages.LlmAzure, WizardAnswers.Pages(definition, azure));
        Assert.DoesNotContain(WizardPages.LlmBedrock, WizardAnswers.Pages(definition, azure));
    }

    [Theory]
    [InlineData("mode", new[] { WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailMode, WizardPages.GuardrailApply })]
    [InlineData("hitl", new[] { WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailApproval, WizardPages.GuardrailApply })]
    // Cisco AI Defense is used only when the scanner mode says remote or both, so the scanner mode is asked first, on the detection page.
    [InlineData("cisco", new[] { WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailDetection, WizardPages.GuardrailCisco, WizardPages.GuardrailApply })]
    [InlineData("detection", new[] { WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailDetection, WizardPages.GuardrailApply })]
    [InlineData("rule-pack", new[] { WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailMode, WizardPages.GuardrailApply })]
    public async Task Each_guardrail_goal_is_the_scope_step_the_page_it_is_about_and_the_apply_page(string goal, string[] expected)
    {
        var definition = await Guardrail();

        Assert.Equal(expected, WizardAnswers.Pages(definition, WizardAnswers.Start(definition, (WizardGoals.FieldId, goal))));
    }

    [Fact]
    public async Task The_rule_pack_goal_shows_the_connectors_rows_and_not_the_ones_for_every_connector()
    {
        var definition = await Guardrail();
        var values = WizardAnswers.Start(definition, (WizardGoals.FieldId, "rule-pack"), ("scope", GuardrailScopes.Connector));

        Assert.Equal(
            new[] { "connector-rule-pack", "connector-rule-pack-dir", "connector-block-message" },
            WizardAnswers.Shown(definition, values, WizardPages.GuardrailMode));
    }

    [Fact]
    public async Task The_judge_goal_walks_through_the_judge_pages_once_the_strategy_asks_for_the_judge()
    {
        var definition = await Guardrail();
        var values = WizardAnswers.Start(
            definition,
            (WizardGoals.FieldId, "judge"),
            ("scope", GuardrailScopes.Global),
            ("detection-strategy", "regex_judge"),
            ("judge-provider", "bedrock"));

        Assert.Equal(
            new[]
            {
                WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailDetection, WizardPages.GuardrailJudge,
                WizardPages.GuardrailJudgeBedrock, WizardPages.GuardrailJudgeTls, WizardPages.GuardrailApply,
            },
            WizardAnswers.Pages(definition, values));

        // The detection page is there only for its strategy: the scanner mode, the port and the per-direction strategies are not this goal.
        Assert.Equal(new[] { "detection-strategy" }, WizardAnswers.Shown(definition, values, WizardPages.GuardrailDetection));
    }

    [Fact]
    public async Task The_no_prompt_switch_is_in_the_command_under_every_goal_though_no_goal_asks_about_it()
    {
        foreach (var definition in new[] { await Llm(), await Guardrail() })
        {
            foreach (var goal in definition.Goals)
            {
                var values = WizardAnswers.Start(definition, (WizardGoals.FieldId, goal.Id), ("scope", GuardrailScopes.Global));
                Assert.Contains("--non-interactive", definition.BuildArgv(values));
                if (!goal.IsAdvanced)
                {
                    Assert.DoesNotContain("non-interactive", definition.Steps.SelectMany(s => WizardAnswers.Shown(definition, values, s.Id)));
                }
            }
        }

        var connector = await ClaudeCode();
        foreach (var goal in connector.Goals)
        {
            Assert.Contains("--yes", connector.BuildArgv(WizardAnswers.Start(connector, (WizardGoals.FieldId, goal.Id))));
        }
    }

    // ------------------------------------------------------------------ choosing a goal

    [Fact]
    public async Task The_wizard_will_not_leave_the_first_page_until_a_goal_is_chosen()
    {
        var vm = Open(await Llm());

        Assert.Equal("What do you want to do?", vm.PageTitle);
        Assert.NotNull(vm.CurrentStep!.Goals);
        UiThread.Run(vm.Next);

        Assert.True(vm.HasValidationSummary);
        Assert.Contains("Choose what you want to do", vm.ValidationSummary, StringComparison.Ordinal);
        Assert.Equal(0, vm.PageIndex);

        UiThread.Run(() => Assert.True(vm.SelectGoal("main")));
        UiThread.Run(vm.Next);

        Assert.False(vm.HasValidationSummary);
        Assert.Equal("Provider and model", vm.PageTitle);
    }

    [Fact]
    public async Task Choosing_a_goal_sets_its_presets_and_the_pages_that_follow_are_its_own()
    {
        var vm = Open(await Llm(), "llm:\n  provider: anthropic\n  model: acme-large-2\n  api_key_env: ANTHROPIC_API_KEY\n  timeout: 30\n");

        UiThread.Run(() => Assert.True(vm.SelectGoal("test")));

        Assert.Equal(ToggleValues.On, Field(vm, "ping").Value);
        Assert.Equal("test", vm.ChosenGoal!.Id);
        Assert.Equal(
            new[] { "--provider", "--model", "--api-key", "--ping" },
            ShownFlags(vm));
        Assert.Equal(new[] { "setup", "llm", "--ping", "--non-interactive" }, vm.Definition.BuildArgv(Values(vm)));
    }

    [Fact]
    public async Task The_judge_goal_blanks_the_main_models_values_so_that_only_what_is_typed_is_the_judges()
    {
        var vm = Open(await Llm(), "llm:\n  provider: anthropic\n  model: acme-large-2\n  api_key_env: ANTHROPIC_API_KEY\n  timeout: 30\n");
        Assert.Equal("anthropic", Field(vm, "provider").Value);

        UiThread.Run(() => Assert.True(vm.SelectGoal("judge")));

        Assert.Equal("judge", Field(vm, "role").Value);
        Assert.All(new[] { "provider", "model", "api-key-env", "timeout", "max-retries" }, id => Assert.Equal(string.Empty, Field(vm, id).Value));

        // Untouched, nothing about the judge's model is sent - and the review does not call the blanking a change.
        Assert.Equal(new[] { "setup", "llm", "--role", "judge", "--non-interactive" }, vm.Definition.BuildArgv(Values(vm)));
        Assert.DoesNotContain(vm.Definition.DescribeChanges(Values(vm)), line => line.Contains("cleared", StringComparison.Ordinal));

        Field(vm, "model").Value = "acme-mini";
        Assert.Equal(
            new[] { "setup", "llm", "--role", "judge", "--model", "acme-mini", "--non-interactive" },
            vm.Definition.BuildArgv(Values(vm)));

        // The judge's model starts blank: it is a model set, not the main model changed.
        Assert.Contains("Model: (not set) → acme-mini", vm.Definition.DescribeChanges(Values(vm), vm.GoalStarts));
        Assert.Contains("Role: unified → judge", vm.Definition.DescribeChanges(Values(vm), vm.GoalStarts));
    }

    [Fact]
    public async Task Choosing_another_goal_puts_back_what_the_last_one_set()
    {
        var vm = Open(await Llm(), "llm:\n  provider: anthropic\n  model: acme-large-2\n");

        UiThread.Run(() => Assert.True(vm.SelectGoal("test")));
        UiThread.Run(() => Assert.True(vm.SelectGoal("judge")));
        Assert.Equal(string.Empty, Field(vm, "model").Value);
        Assert.Equal(ToggleValues.Unset, Field(vm, "ping").Value);

        UiThread.Run(() => Assert.True(vm.SelectGoal("main")));

        // The model is back to what config.yaml held, the role to the default, and the ping is no longer on.
        Assert.Equal("acme-large-2", Field(vm, "model").Value);
        Assert.Equal("anthropic", Field(vm, "provider").Value);
        Assert.Equal("unified", Field(vm, "role").Value);
        Assert.Equal(new[] { "setup", "llm", "--non-interactive" }, vm.Definition.BuildArgv(Values(vm)));
    }

    [Fact]
    public async Task Advanced_puts_back_the_presets_and_shows_every_page()
    {
        var vm = Open(await Llm());
        UiThread.Run(() => Assert.True(vm.SelectGoal("test")));
        UiThread.Run(() => Assert.True(vm.SelectGoal("advanced")));

        Assert.Equal(ToggleValues.Unset, Field(vm, "ping").Value);
        Assert.Contains("--timeout", ShownFlags(vm));
        Assert.Contains("--show", ShownFlags(vm));
        Assert.Contains(vm.Steps, s => s.Step.Id == WizardPages.LlmKey && s.IsVisible);
    }

    [Fact]
    public async Task A_goal_that_is_not_in_the_wizard_cannot_be_chosen()
    {
        var vm = Open(await Llm());

        UiThread.Run(() =>
        {
            Assert.False(vm.SelectGoal("no-such-goal"));
            Assert.False(vm.SelectGoal(string.Empty));
        });
        Assert.Null(vm.ChosenGoal);
        Assert.Equal(string.Empty, Field(vm, WizardGoals.FieldId).Value);
    }

    [Fact]
    public async Task The_number_of_pages_the_wizard_shows_follows_the_goal_chosen()
    {
        var vm = Open(await Guardrail());
        var before = vm.ProgressText;
        Assert.StartsWith("Step 1 of ", before, StringComparison.Ordinal);

        UiThread.Run(() => Assert.True(vm.SelectGoal("hitl")));

        // Goal, scope, approval, apply, review.
        Assert.Equal("Step 1 of 5", vm.ProgressText);

        UiThread.Run(() => Assert.True(vm.SelectGoal("advanced")));
        Assert.NotEqual("Step 1 of 5", vm.ProgressText);
    }

    [Fact]
    public async Task A_guardrail_goal_walks_to_a_review_that_carries_the_goals_command()
    {
        var vm = Open(await Guardrail(), "guardrail:\n  connector: claudecode\n  mode: observe\n  detection_strategy: regex_only\n");

        UiThread.Run(() => Assert.True(vm.SelectGoal("hitl")));
        UiThread.Run(vm.Next);
        Assert.Equal("Scope", vm.PageTitle);
        UiThread.Run(vm.Next);
        Assert.Equal("Human approval", vm.PageTitle);
        UiThread.Run(vm.Next);
        Assert.Equal("Apply", vm.PageTitle);
        UiThread.Run(vm.Next);

        Assert.True(vm.IsReview, vm.ValidationSummary);
        var argv = vm.CommandReview!.Steps[0].Argv;
        Assert.Equal(new[] { "setup", "guardrail", "--connector", "claudecode", "--human-approval", "--non-interactive" }, argv);
        Assert.Contains(vm.ReviewChanges, line => line.StartsWith("Human approval:", StringComparison.Ordinal) && line.EndsWith("→ on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_disable_goal_turns_the_guardrail_off_and_the_review_says_it_is_a_destructive_change()
    {
        var vm = Open(await Guardrail(), "guardrail:\n  connector: claudecode\n  mode: action\n");

        UiThread.Run(() => Assert.True(vm.SelectGoal("disable")));
        Assert.Equal(ToggleValues.On, Field(vm, "disable").Value);
        UiThread.Run(vm.Next);
        UiThread.Run(vm.Next);
        UiThread.Run(vm.Next);

        Assert.True(vm.IsReview, vm.ValidationSummary);
        Assert.Equal(new[] { "guardrail", "disable", "--yes" }, vm.CommandReview!.Steps[0].Argv);
        Assert.Equal(DefenseClaw.Core.Cli.CommandTier.Destructive, vm.CommandReview.Tier);
        Assert.Contains(vm.CommandReview.Warnings, w => w.Title == WizardCautions.GuardrailOffTitle);
        Assert.True(vm.CommandReview.RestartsGateway);
    }

    [Fact]
    public async Task A_connector_goal_that_turns_a_switch_on_sends_it_without_asking_the_page_that_holds_it()
    {
        var vm = Open(await ClaudeCode(), "guardrail:\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n");

        UiThread.Run(() => Assert.True(vm.SelectGoal("hitl")));

        Assert.Equal(ToggleValues.On, Field(vm, "human-approval").Value);
        Assert.Equal(new[] { "--human-approval", "--hilt-min-severity", "--restart" }, ShownFlags(vm));
        Assert.Equal(new[] { "setup", "claude-code", "--human-approval", "--yes" }, vm.Definition.BuildArgv(Values(vm)));
    }

    // ------------------------------------------------------------------ splunk

    private static async Task<WizardDefinition> Splunk() =>
        await CatalogHelp.DefinitionAsync("splunk", new Dictionary<string, string> { ["splunk"] = CatalogHelp.Real("setup-splunk") });

    [Fact]
    public async Task Splunks_goals_are_its_three_pipelines_and_the_guided_page_is_the_advanced_way()
    {
        var definition = await Splunk();

        Assert.Equal(new[] { "o11y", "local-docker", "enterprise", "advanced" }, Ids(definition));
        Assert.Equal(WizardGoals.StepId, definition.Steps[0].Id);
        Assert.Equal(WizardWalkthroughs.GuideStepId, definition.Steps[1].Id);
        Assert.Equal(WizardGuideRequirement.Docker, definition.Goals.Single(g => g.Id == "local-docker").Requires);
    }

    [Fact]
    public async Task The_o11y_goal_turns_that_pipeline_on_and_goes_straight_to_its_page()
    {
        var vm = Open(await Splunk());

        UiThread.Run(() => Assert.True(vm.SelectGoal("o11y")));

        Assert.Equal(ToggleValues.On, Field(vm, "o11y").Value);
        Assert.NotEqual(ToggleValues.On, Field(vm, "enterprise").Value);
        UiThread.Run(vm.Next);

        // Not the guide: the pipeline is chosen. The page of the pipeline is next.
        Assert.Equal("Splunk Observability Cloud", vm.PageTitle);
        Assert.Contains("--o11y", vm.Definition.BuildArgv(Values(vm)));
        Assert.DoesNotContain("--logs", vm.Definition.BuildArgv(Values(vm)));
        Assert.DoesNotContain("--enterprise", vm.Definition.BuildArgv(Values(vm)));
    }

    [Fact]
    public async Task The_enterprise_goal_shows_the_hec_pages_and_not_the_other_pipelines()
    {
        var vm = Open(await Splunk());

        UiThread.Run(() => Assert.True(vm.SelectGoal("enterprise")));

        Assert.Equal(
            new[] { WizardGoals.StepId, WizardPages.SplunkEnterprise, WizardPages.SplunkHec },
            vm.Steps.Where(s => s.IsVisible).Select(s => s.Step.Id).ToArray());
    }

    private sealed class Probe : IDockerProbe
    {
        private readonly DockerStatus _status;

        public Probe(DockerStatus status) => _status = status;

        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(_status);
    }

    [Fact]
    public async Task The_local_goal_is_off_with_the_reason_until_docker_can_run_it()
    {
        var vm = Open(
            await Splunk(),
            docker: new Probe(new DockerStatus(DockerState.NotInstalled, "Docker was not found on this machine's PATH.", Array.Empty<string>())));
        var option = vm.GoalOptions.Single(o => o.Goal.Id == "local-docker");

        UiThread.WaitFor(() => option.UnavailableReason.Contains("not found", StringComparison.Ordinal), "the Docker answer");

        Assert.False(option.IsAvailable);
        Assert.Contains("Local Splunk runs in Docker", option.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("not available", option.AutomationName, StringComparison.Ordinal);

        // A stray write does not choose it, and the wizard says so.
        UiThread.Run(() => option.IsSelected = true);
        Assert.False(option.IsSelected);
        UiThread.Run(() => Assert.False(vm.SelectGoal("local-docker")));
        Assert.Null(vm.ChosenGoal);

        // The goals that do not need Docker are not touched by its absence.
        Assert.All(vm.GoalOptions.Where(o => !o.RequiresDocker), o => Assert.True(o.IsAvailable));
    }

    [Fact]
    public async Task The_local_goal_can_be_chosen_once_docker_answers()
    {
        var vm = Open(await Splunk(), docker: new Probe(new DockerStatus(DockerState.Ready, "Docker is running.", Array.Empty<string>())));
        var option = vm.GoalOptions.Single(o => o.Goal.Id == "local-docker");

        UiThread.WaitFor(() => option.IsAvailable, "the Docker answer");
        UiThread.Run(() => Assert.True(vm.SelectGoal("local-docker")));

        Assert.Equal(ToggleValues.On, Field(vm, "logs").Value);
        Assert.True(option.IsSelected);
    }

    // ------------------------------------------------------------------ the page's choices and the opening preset

    [Fact]
    public async Task A_radio_button_is_a_choice_made_by_answering_the_goal_field()
    {
        var vm = Open(await Llm());
        var options = vm.Steps[0].Goals!.Options;
        var raised = new List<string?>();
        ((INotifyPropertyChanged)options[1]).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        UiThread.Run(() => options[1].IsSelected = true);

        Assert.Equal(options[1].Goal.Id, Field(vm, WizardGoals.FieldId).Value);
        Assert.True(options[1].IsSelected);
        Assert.False(options[0].IsSelected);
        Assert.Contains(nameof(WizardGoalOptionViewModel.IsSelected), raised);
        Assert.True(vm.Steps[0].Goals!.HasSelection);

        // Setting false is the radio group unchecking the old one; it changes no answer.
        UiThread.Run(() => options[1].IsSelected = false);
        Assert.True(options[1].IsSelected);
    }

    [Fact]
    public async Task The_page_says_where_things_stand_today_from_config_yaml()
    {
        var vm = Open(await Llm(), "llm:\n  provider: anthropic\n  model: acme-large-2\nguardrail:\n  judge:\n    llm:\n      model: acme-mini\n");

        var goals = vm.Steps[0].Goals!;
        Assert.True(goals.HasStateSummary);
        Assert.Equal("Main: anthropic/acme-large-2  ·  Judge: acme-mini", goals.StateSummary);

        var empty = Open(await Llm(), "config_version: 5\n");
        Assert.Equal("Main: not set  ·  Judge: not set", empty.Steps[0].Goals!.StateSummary);

        var fleet = Open(await Guardrail(), "guardrail:\n  enabled: true\n  mode: action\n  connectors:\n    codex:\n      mode: observe\n    claudecode:\n      mode: action\n");
        Assert.Equal(
            "Guardrail: on  ·  Mode: action  ·  Strategy: regex_only  ·  Connectors: claudecode (action), codex (observe)",
            fleet.Steps[0].Goals!.StateSummary);
    }

    [Fact]
    public async Task A_preset_that_is_only_a_goal_opens_the_wizard_past_the_question()
    {
        var vm = Open(await Llm());

        var opened = UiThread.Run(() => WizardPreset.ForGoal("judge").ApplyTo(vm));

        Assert.True(opened);
        Assert.Equal("judge", vm.ChosenGoal!.Id);
        Assert.Equal("Provider and model", vm.PageTitle);
    }

    [Fact]
    public async Task A_preset_for_a_goal_the_wizard_lacks_leaves_it_on_its_first_page()
    {
        var vm = Open(await Llm());

        var opened = UiThread.Run(() => WizardPreset.ForGoal("no-such-goal").ApplyTo(vm));

        Assert.False(opened);
        Assert.Equal("What do you want to do?", vm.PageTitle);
        Assert.Equal(0, vm.PageIndex);
    }

    // ------------------------------------------------------------------ helpers

    private static WizardFieldViewModel Field(WizardViewModel vm, string id) =>
        vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == id);

    private static WizardValues Values(WizardViewModel vm)
    {
        var values = new WizardValues();
        foreach (var field in vm.Steps.SelectMany(s => s.Fields))
        {
            values[field.Id] = field.Value;
        }

        return values;
    }
}
