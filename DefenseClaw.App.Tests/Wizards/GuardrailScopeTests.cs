using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The guardrail wizard's Scope step (CUST-269): a change is for one connector or for every active one, and in the first case nothing process-wide
/// can ride along. The allow-list is the TUI's <c>_GUARDRAIL_CONNECTOR_SETUP_FLAGS</c> (0.8.10); the layouts are built from the help screen the
/// installed CLI prints and, to prove the rule on every flag at once, from a made-up one that has them all.
/// </summary>
public class GuardrailScopeTests
{
    /// <summary>Every flag that is global, and the ones that are not, so a single test can try them all.</summary>
    private static readonly string FakeHelp = CatalogHelp.Screen(
        "guardrail",
        "--disable  Disable guardrail.",
        "--connector, --agent [claudecode|codex]  Agent framework connector.",
        "--mode [observe|action]  Guardrail mode",
        "--scanner-mode [local|remote|both]  Scanner mode",
        "--cisco-endpoint TEXT  Cisco AI Defense API endpoint",
        "--cisco-api-key-env TEXT  Env var name holding the Cisco key",
        "--cisco-timeout-ms INTEGER  Cisco timeout (ms)",
        "--port INTEGER  Guardrail proxy port",
        "--block-message TEXT  Custom block message",
        "--detection-strategy [regex_only|regex_judge|judge_first]  Detection strategy",
        "--detection-strategy-prompt [regex_only|regex_judge|judge_first]  Prompt lane strategy",
        "--rule-pack [default|strict|permissive]  Rule pack",
        "--rule-pack-dir TEXT  Rule pack directory",
        "--judge-model TEXT  LLM judge model",
        "--judge-api-base TEXT  Judge base URL",
        "--judge-api-key-env TEXT  Judge key variable",
        "--judge-provider TEXT  Judge provider",
        "--judge-bedrock-region TEXT  Judge Bedrock region",
        "--judge-insecure-skip-verify  Judge TLS off",
        "--llm-role [judge_only|judge_and_agent]  LLM role",
        "--inherit-from [|guardrail|scanners.skill]  Inherit from",
        "--human-approval / --no-human-approval  Human approval",
        "--hilt-min-severity [high|medium|low|critical]  Minimum severity",
        "--workspace TEXT  Workspace",
        "--restart / --no-restart  Restart the gateway",
        "--verify / --no-verify  Verify afterwards",
        "--non-interactive, --accept-defaults, --yes  Use flags instead of prompts");

    private static readonly string[] GlobalOnly =
    {
        "--scanner-mode", "--cisco-endpoint", "--cisco-api-key-env", "--cisco-timeout-ms", "--port", "--detection-strategy",
        "--detection-strategy-prompt", "--judge-model", "--judge-api-base", "--judge-api-key-env", "--judge-provider", "--judge-bedrock-region",
        "--judge-insecure-skip-verify", "--llm-role", "--inherit-from", "--workspace",
    };

    private const string FleetConfig = """
        guardrail:
          connector: claudecode
          mode: observe
          detection_strategy: regex_only
          connectors:
            claudecode:
              mode: action
            codex:
              mode: observe
        """;

    private const string SingleConfig = """
        guardrail:
          connector: claudecode
          mode: action
          port: 4100
          scanner_mode: local
          detection_strategy: regex_only
        """;

    private static Task<WizardDefinition> Fake() => CatalogHelp.DefinitionAsync("guardrail", new Dictionary<string, string> { ["guardrail"] = FakeHelp });

    private static Task<WizardDefinition> Real() => CatalogHelp.RealAsync("guardrail");

    private static WizardDefinition With(WizardDefinition definition, string yaml) =>
        WizardBaseline.Apply(definition, WizardSamples.Config(LineEndings.Normalize(yaml)), configReadable: true);

    /// <summary>Every field set to something that is not where it started, so any flag that can be sent is sent.</summary>
    private static WizardValues Everything(WizardDefinition definition, params (string Id, string Value)[] then)
    {
        var values = WizardAnswers.Start(definition);
        foreach (var field in definition.AllFields.Where(f => !f.IsSecret && f.Id != WizardGoals.FieldId))
        {
            values[field.Id] = field.Kind switch
            {
                WizardFieldKind.Choice => field.Choices.First(c => c.Value.Length > 0 && c.Value != field.DefaultValue).Value,
                WizardFieldKind.Toggle => ToggleValues.On,
                WizardFieldKind.Switch => ToggleValues.On,
                WizardFieldKind.Integer => "4242",
                WizardFieldKind.Lines => "one\ntwo",
                _ => "synthetic-" + field.Id,
            };
        }

        foreach (var (id, value) in then)
        {
            values[id] = value;
        }

        // Turning the guardrail off is a different command; this is about configuring it.
        values["disable"] = ToggleValues.Off;
        return values;
    }

    // ------------------------------------------------------------------ the layout

    [Fact]
    public async Task The_real_help_opens_with_the_scope_step_and_puts_the_global_settings_behind_it()
    {
        var definition = await Real();

        Assert.True(definition.IsCurated);
        Assert.Equal(
            new[]
            {
                WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailMode, WizardPages.GuardrailApproval, WizardPages.GuardrailDetection,
                WizardPages.GuardrailJudge, WizardPages.GuardrailJudgeBedrock, WizardPages.GuardrailJudgeVertex, WizardPages.GuardrailJudgeAzure,
                WizardPages.GuardrailJudgeTls, WizardPages.GuardrailCisco, WizardPages.GuardrailApply, "more-page-1",
            },
            definition.Steps.Select(s => s.Id).ToArray());

        var scope = definition.Steps.Single(s => s.Id == WizardPages.GuardrailScope);
        Assert.Equal(new[] { "scope", "connector", "disable" }, scope.Fields.Select(f => f.Id).ToArray());
        Assert.True(scope.Fields[0].IsSynthetic);
        Assert.Equal(new[] { GuardrailScopes.Connector, GuardrailScopes.Global }, scope.Fields[0].Choices.Select(c => c.Value).ToArray());
        Assert.Equal(string.Empty, scope.Fields[0].FlagDisplay);
    }

    [Fact]
    public async Task The_scope_defaults_to_every_connector_with_no_roster_and_to_one_connector_with_a_fleet()
    {
        var plain = await Real();
        Assert.Equal(GuardrailScopes.Global, WizardAnswers.Start(plain)["scope"]);

        var single = With(plain, SingleConfig);
        Assert.Equal(GuardrailScopes.Global, WizardAnswers.Start(single)["scope"]);

        var fleet = With(plain, FleetConfig);
        Assert.Equal(GuardrailScopes.Connector, WizardAnswers.Start(fleet)["scope"]);
    }

    [Fact]
    public async Task Each_scope_shows_its_own_pages()
    {
        var definition = await Real();

        var global = WizardAnswers.Pages(definition, WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global)));
        var connector = WizardAnswers.Pages(definition, WizardAnswers.Start(definition, ("scope", GuardrailScopes.Connector)));

        Assert.Equal(
            new[]
            {
                WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailMode, WizardPages.GuardrailApproval, WizardPages.GuardrailDetection,
                WizardPages.GuardrailCisco, WizardPages.GuardrailApply, "more-page-1",
            },
            global);
        Assert.Equal(
            new[] { WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailMode, WizardPages.GuardrailApproval, WizardPages.GuardrailApply },
            connector);
    }

    [Fact]
    public async Task A_flag_that_can_belong_to_one_connector_is_offered_for_each_scope_and_only_the_global_one_starts_from_config_yaml()
    {
        var definition = With(await Real(), SingleConfig);
        var global = WizardAnswers.ById(definition, "mode");
        var connector = WizardAnswers.ById(definition, "connector-mode");

        Assert.Equal("--mode", global.Flag);
        Assert.Equal("--mode", connector.Flag);
        Assert.Equal("action", global.DefaultValue);
        Assert.Equal(string.Empty, connector.DefaultValue);
        Assert.Equal(string.Empty, connector.BaselineValue);
        Assert.True(connector.IgnoresConfig);
        Assert.Contains("selected connector only", connector.Help, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the allow-list

    [Fact]
    public async Task A_command_scoped_to_one_connector_carries_only_flags_on_the_allow_list()
    {
        var definition = await Fake();
        var values = Everything(definition, ("scope", GuardrailScopes.Connector), ("connector", "codex"));

        var argv = definition.BuildArgv(values);

        var flags = argv.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        Assert.All(flags, flag => Assert.Contains(flag, GuardrailScope.ConnectorFlags.Concat(new[] { "--no-restart", "--no-verify", "--no-human-approval" })));
        Assert.Equal(new[] { "setup", "guardrail" }, argv.Take(2));
        Assert.Contains("--connector", flags);
        Assert.Contains("--mode", flags);
        Assert.Contains("--rule-pack", flags);
        Assert.Contains("--non-interactive", flags);
    }

    [Theory]
    [InlineData("--scanner-mode")]
    [InlineData("--port")]
    [InlineData("--cisco-endpoint")]
    [InlineData("--cisco-api-key-env")]
    [InlineData("--cisco-timeout-ms")]
    [InlineData("--judge-model")]
    [InlineData("--judge-provider")]
    [InlineData("--judge-api-base")]
    [InlineData("--judge-api-key-env")]
    [InlineData("--judge-bedrock-region")]
    [InlineData("--judge-insecure-skip-verify")]
    [InlineData("--detection-strategy")]
    [InlineData("--detection-strategy-prompt")]
    [InlineData("--llm-role")]
    [InlineData("--inherit-from")]
    [InlineData("--workspace")]
    public async Task A_command_scoped_to_one_connector_never_carries_a_global_only_flag(string flag)
    {
        var definition = await Fake();
        var values = Everything(definition, ("scope", GuardrailScopes.Connector), ("connector", "codex"), ("detection-strategy", "judge_first"));

        var scoped = definition.BuildArgv(values);
        Assert.DoesNotContain(flag, scoped);

        // The same answers in the global scope do send it: it is the scope that decides, not the flag being unsupported.
        var global = definition.BuildArgv(
            Everything(definition, ("scope", GuardrailScopes.Global), ("detection-strategy", "judge_first"), ("judge-provider", "bedrock")));
        Assert.Contains(flag, global);
    }

    [Fact]
    public async Task The_acceptance_flags_are_absent_from_a_connector_command_whatever_the_answers_hold()
    {
        var definition = await Fake();
        var argv = definition.BuildArgv(Everything(definition, ("scope", GuardrailScopes.Connector), ("connector", "claudecode"), ("detection-strategy", "judge_first")));

        Assert.DoesNotContain(argv, a => a == "--scanner-mode" || a == "--port");
        Assert.DoesNotContain(argv, a => a.StartsWith("--cisco-", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.StartsWith("--judge-", StringComparison.Ordinal));

        // And none of their values.
        Assert.DoesNotContain(argv, a => a.Contains("synthetic-scanner-mode", StringComparison.Ordinal) || a == "4242");
    }

    [Fact]
    public async Task The_allow_list_holds_even_for_rows_the_pages_would_have_hidden()
    {
        // "Stale or injected form rows": a definition whose global-only fields are not gated at all (as if a layout forgot) and whose answers all
        // say yes. The command is still built over the allow-listed flags alone.
        var gated = await Fake();
        var ungated = gated.With(
            gated.Steps.Select(step => new WizardStep
            {
                Id = step.Id,
                Title = step.Title,
                Fields = step.Fields.Select(f => f.WithGate(null, Array.Empty<string>())).ToArray(),
            }).ToArray(),
            string.Empty,
            string.Empty);
        var values = Everything(ungated, ("scope", GuardrailScopes.Connector), ("connector", "codex"));

        // Every global flag is active under these answers - that is the premise - ...
        var active = WizardAnswers.ActiveFlags(ungated, values);
        Assert.All(GlobalOnly, flag => Assert.Contains(flag, active));

        // ... and none of them is sent.
        var argv = ungated.BuildArgv(values);
        Assert.All(GlobalOnly, flag => Assert.DoesNotContain(flag, argv));
        Assert.Contains("--connector", argv);
    }

    [Fact]
    public async Task The_connector_is_always_sent_in_the_connector_scope_even_when_it_is_the_one_config_yaml_names()
    {
        var definition = With(await Real(), SingleConfig);
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Connector));
        Assert.Equal("claudecode", values["connector"]);

        // Left at the configured connector, it is still named: omitting it would write the mode for every connector.
        Assert.Equal(new[] { "setup", "guardrail", "--connector", "claudecode", "--non-interactive" }, definition.BuildArgv(values));

        values["connector-mode"] = "observe";
        Assert.Equal(
            new[] { "setup", "guardrail", "--connector", "claudecode", "--mode", "observe", "--non-interactive" },
            definition.BuildArgv(values));
    }

    [Fact]
    public async Task One_connectors_overrides_start_unset_and_send_only_what_the_operator_picks()
    {
        var definition = With(await Real(), FleetConfig);
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Connector), ("connector", "codex"));

        // Nothing chosen: the connector and the no-prompt switch, nothing about its mode, pack, message or approval.
        Assert.Equal(new[] { "setup", "guardrail", "--connector", "codex", "--non-interactive" }, definition.BuildArgv(values));

        values["connector-mode"] = "action";
        values["connector-rule-pack"] = "strict";
        values["connector-block-message"] = "Not allowed here.";
        values["connector-human-approval"] = ToggleValues.On;
        values["connector-hilt-min-severity"] = "critical";

        Assert.Equal(
            new[]
            {
                "setup", "guardrail", "--connector", "codex", "--mode", "action", "--rule-pack", "strict", "--block-message", "Not allowed here.",
                "--human-approval", "--hilt-min-severity", "critical", "--non-interactive",
            },
            definition.BuildArgv(values));
    }

    [Fact]
    public async Task The_global_scope_starts_from_config_yaml_and_sends_only_what_changes()
    {
        var definition = With(await Real(), SingleConfig);
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global));

        Assert.Equal("4100", values["port"]);
        Assert.Equal("local", values["scanner-mode"]);
        Assert.Equal(new[] { "setup", "guardrail", "--connector", "claudecode", "--non-interactive" }, definition.BuildArgv(values));

        values["scanner-mode"] = "both";
        values["port"] = "4200";
        Assert.Equal(
            new[] { "setup", "guardrail", "--connector", "claudecode", "--scanner-mode", "both", "--port", "4200", "--non-interactive" },
            definition.BuildArgv(values));
    }

    // ------------------------------------------------------------------ the connector must be active

    [Fact]
    public async Task With_a_fleet_the_connector_must_be_one_of_the_active_ones()
    {
        var definition = With(await Real(), FleetConfig);
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Connector));

        // Nothing chosen yet.
        Assert.Contains("Choose the connector", definition.CrossValidator!(values), StringComparison.Ordinal);

        values["connector"] = "hermes";
        Assert.Equal("Connector 'hermes' is not active. Active connectors: claudecode, codex.", definition.CrossValidator!(values));

        values["connector"] = "codex";
        Assert.Null(definition.CrossValidator!(values));

        // However the name is spelled.
        values["connector"] = "Claude-Code";
        Assert.Null(definition.CrossValidator!(values));
    }

    [Fact]
    public async Task With_a_fleet_the_connector_list_is_the_active_members_and_starts_blank()
    {
        var definition = With(await Real(), FleetConfig);
        var connector = WizardAnswers.ById(definition, "connector");

        Assert.Equal(new[] { string.Empty, "claudecode", "codex" }, connector.Choices.Select(c => c.Value).ToArray());
        Assert.Equal(string.Empty, connector.DefaultValue);
        Assert.Contains("claudecode (action), codex (observe)", definition.Steps.Single(s => s.Id == WizardPages.GuardrailScope).Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_fleet_a_global_change_does_not_name_a_connector_because_it_reaches_them_all()
    {
        var definition = With(await Real(), FleetConfig);

        var global = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global), ("connector", "codex"));
        Assert.DoesNotContain("connector", WizardAnswers.Shown(definition, global, WizardPages.GuardrailScope));
        Assert.DoesNotContain("--connector", definition.BuildArgv(global));
        Assert.Null(definition.CrossValidator!(global));

        var connector = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Connector));
        Assert.Contains("connector", WizardAnswers.Shown(definition, connector, WizardPages.GuardrailScope));
    }

    [Fact]
    public async Task With_one_connector_or_none_any_connector_can_be_chosen_in_either_scope()
    {
        var single = With(await Real(), SingleConfig);
        var values = WizardAnswers.Start(single, ("scope", GuardrailScopes.Connector), ("connector", "codex"));
        Assert.Null(single.CrossValidator!(values));
        Assert.Contains("connector", WizardAnswers.Shown(single, WizardAnswers.Start(single, ("scope", GuardrailScopes.Global)), WizardPages.GuardrailScope));

        // A connector scope still needs a connector named.
        values["connector"] = string.Empty;
        Assert.NotNull(single.CrossValidator!(values));

        var none = await Real();
        Assert.NotNull(none.CrossValidator!(WizardAnswers.Start(none, ("scope", GuardrailScopes.Connector))));
        Assert.Null(none.CrossValidator!(WizardAnswers.Start(none, ("scope", GuardrailScopes.Global))));
    }

    [Fact]
    public async Task A_rule_pack_and_a_rule_pack_directory_are_mutually_exclusive()
    {
        var definition = With(await Real(), FleetConfig);

        var connector = WizardAnswers.Start(
            definition,
            ("scope", GuardrailScopes.Connector),
            ("connector", "codex"),
            ("connector-rule-pack", "strict"),
            ("connector-rule-pack-dir", @"C:\packs\corp"));
        Assert.Contains("not both", definition.CrossValidator!(connector), StringComparison.Ordinal);

        connector["connector-rule-pack"] = string.Empty;
        Assert.Null(definition.CrossValidator!(connector));

        var global = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global), ("rule-pack", "strict"), ("rule-pack-dir", @"C:\packs\corp"));
        Assert.Contains("not both", definition.CrossValidator!(global), StringComparison.Ordinal);
    }

    [Fact]
    public void A_layout_with_no_scope_step_is_left_as_it_is_when_the_roster_is_applied()
    {
        var other = new WizardDefinition { Target = "guardrail", Title = "G", Group = WizardGroups.GuardrailAndPolicy };

        Assert.Same(other, GuardrailScope.Install(other, GuardrailScopeContext.Empty));
    }

    // ------------------------------------------------------------------ the judge is gated on the strategy

    [Theory]
    [InlineData("regex_only", false)]
    [InlineData("regex_judge", true)]
    [InlineData("judge_first", true)]
    [InlineData("", false)]
    public async Task The_judge_pages_appear_only_for_a_strategy_that_uses_the_judge(string strategy, bool shown)
    {
        var definition = await Real();
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global), ("detection-strategy", strategy), ("judge-model", "judge-x"));

        Assert.Equal(shown, WizardAnswers.Pages(definition, values).Contains(WizardPages.GuardrailJudge));
        Assert.Equal(shown, definition.BuildArgv(values).Contains("--judge-model"));
    }

    [Fact]
    public async Task The_judge_never_shows_in_the_connector_scope_whatever_the_strategy()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(
            definition,
            ("scope", GuardrailScopes.Connector),
            ("connector", "codex"),
            ("detection-strategy", "judge_first"),
            ("judge-model", "judge-x"),
            ("judge-provider", "bedrock"));

        Assert.DoesNotContain(WizardAnswers.Pages(definition, values), id => id.StartsWith("judge", StringComparison.Ordinal));
        Assert.DoesNotContain("--judge-model", definition.BuildArgv(values));
    }

    [Fact]
    public async Task The_judges_cloud_pages_follow_the_judges_provider_in_the_same_way_as_the_llm_wizards()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global), ("detection-strategy", "regex_judge"), ("judge-provider", "bedrock"));

        Assert.Equal(
            new[] { WizardPages.GuardrailJudgeBedrock, WizardPages.GuardrailJudgeTls },
            WizardAnswers.Pages(definition, values).Where(id => id.StartsWith("judge-", StringComparison.Ordinal)).ToArray());

        values["judge-provider"] = "vertex_ai";
        Assert.Equal(
            new[] { WizardPages.GuardrailJudgeVertex, WizardPages.GuardrailJudgeTls },
            WizardAnswers.Pages(definition, values).Where(id => id.StartsWith("judge-", StringComparison.Ordinal)).ToArray());

        values["detection-strategy"] = "regex_only";
        Assert.DoesNotContain(WizardAnswers.Pages(definition, values), id => id.StartsWith("judge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_judges_model_field_is_a_model_picker_for_the_judges_provider()
    {
        var definition = await Real();
        var model = WizardAnswers.Field(definition, "--judge-model");

        Assert.True(model.HasPicker);
        Assert.Equal("judge-provider", model.PickerProviderFieldId);
        Assert.Equal("judge-instance-name", model.PickerInstanceFieldId);
        Assert.Equal(WizardFieldKind.Choice, WizardAnswers.Field(definition, "--judge-provider").Kind);
    }

    [Fact]
    public async Task Without_a_detection_strategy_flag_the_judge_is_not_gated_on_one()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "guardrail",
            new Dictionary<string, string>
            {
                ["guardrail"] = CatalogHelp.Screen(
                    "guardrail",
                    "--connector [claudecode|codex]  Connector.",
                    "--mode [observe|action]  Mode.",
                    "--judge-model TEXT  Judge model.",
                    "--non-interactive  Never prompt."),
            });

        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global), ("judge-model", "judge-x"));

        Assert.Contains(WizardPages.GuardrailJudge, WizardAnswers.Pages(definition, values));
        Assert.Contains("--judge-model", definition.BuildArgv(values));
    }

    // ------------------------------------------------------------------ the disable branch

    [Fact]
    public async Task Turning_the_guardrail_off_for_one_connector_is_guardrail_disable_for_that_connector()
    {
        var definition = With(await Real(), FleetConfig);
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Connector), ("connector", "codex"), ("disable", ToggleValues.On));

        Assert.Equal(new[] { "guardrail", "disable", "--yes", "--connector", "codex" }, definition.BuildArgv(values));

        values["restart"] = ToggleValues.Off;
        Assert.Equal(new[] { "guardrail", "disable", "--yes", "--connector", "codex", "--no-restart" }, definition.BuildArgv(values));
    }

    [Fact]
    public async Task Turning_the_guardrail_off_globally_names_no_connector()
    {
        var definition = With(await Real(), SingleConfig);
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global), ("disable", ToggleValues.On), ("connector", "claudecode"));

        Assert.Equal(new[] { "guardrail", "disable", "--yes" }, definition.BuildArgv(values));
    }

    [Fact]
    public async Task Turning_the_guardrail_off_hides_everything_but_the_scope_and_the_restart_and_sends_nothing_else()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(
            definition,
            ("scope", GuardrailScopes.Global),
            ("disable", ToggleValues.On),
            ("port", "4999"),
            ("scanner-mode", "remote"),
            ("detection-strategy", "judge_first"),
            ("judge-model", "judge-x"));

        Assert.Equal(
            new[] { WizardGoals.StepId, WizardPages.GuardrailScope, WizardPages.GuardrailApply },
            WizardAnswers.Pages(definition, values));
        Assert.Equal(new[] { "restart" }, WizardAnswers.Shown(definition, values, WizardPages.GuardrailApply));
        Assert.Equal(new[] { "guardrail", "disable", "--yes" }, definition.BuildArgv(values));
    }

    [Fact]
    public async Task Turning_the_guardrail_off_in_the_connector_scope_needs_a_connector_so_it_cannot_become_the_global_switch()
    {
        var fleet = With(await Real(), FleetConfig);
        var values = WizardAnswers.Start(fleet, ("scope", GuardrailScopes.Connector), ("disable", ToggleValues.On));

        Assert.Contains("Choose the connector", fleet.CrossValidator!(values), StringComparison.Ordinal);

        values["connector"] = "hermes";
        Assert.Contains("is not active", fleet.CrossValidator!(values), StringComparison.Ordinal);

        values["connector"] = "codex";
        Assert.Null(fleet.CrossValidator!(values));
    }

    [Fact]
    public async Task The_disable_branch_is_the_review_of_a_destructive_command_that_restarts_the_gateway()
    {
        var definition = await Real();
        var argv = definition.BuildArgv(WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global), ("disable", ToggleValues.On)));

        Assert.Equal(CommandTier.Destructive, WizardCautions.Floor(argv));
        Assert.True(WizardReview.RestartsGateway(argv));

        var warning = Assert.Single(WizardCautions.For(argv));
        Assert.Equal(WizardCautions.GuardrailOffTitle, warning.Title);
        Assert.Contains("global switch", warning.Message, StringComparison.Ordinal);

        var scoped = WizardCautions.For(new[] { "guardrail", "disable", "--yes", "--connector", "codex" });
        Assert.Contains("Only codex is affected", Assert.Single(scoped).Message, StringComparison.Ordinal);

        Assert.Null(WizardCautions.Floor(new[] { "setup", "guardrail", "--mode", "action" }));
        Assert.Empty(WizardCautions.For(new[] { "setup", "guardrail", "--mode", "action" }));
    }

    [Fact]
    public async Task A_cli_without_the_disable_flag_has_no_disable_branch_and_gates_nothing_on_it()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "guardrail",
            new Dictionary<string, string>
            {
                ["guardrail"] = CatalogHelp.Screen(
                    "guardrail",
                    "--connector [claudecode|codex]  Connector.",
                    "--mode [observe|action]  Mode.",
                    "--port INTEGER  Port.",
                    "--non-interactive  Never prompt."),
            });

        Assert.DoesNotContain(definition.AllFields, f => f.Flag == "--disable");
        var values = WizardAnswers.Start(definition, ("scope", GuardrailScopes.Global), ("port", "4300"));
        Assert.Contains(WizardPages.GuardrailDetection, WizardAnswers.Pages(definition, values));
        Assert.Contains("--port", definition.BuildArgv(values));
    }

    // ------------------------------------------------------------------ what the fixture help is

    [Fact]
    public async Task The_real_help_has_the_flags_the_scope_rule_names()
    {
        var definition = await Real();
        var flags = definition.AllFields.Select(f => f.Flag).ToHashSet(StringComparer.Ordinal);

        foreach (var allowed in GuardrailScope.ConnectorFlags)
        {
            Assert.Contains(allowed, flags);
        }

        // The connector list is what the CLI offers on this platform.
        Assert.Equal(new[] { string.Empty, "claudecode", "codex" }, WizardAnswers.ById(definition, "connector").Choices.Select(c => c.Value).ToArray());
    }
}
