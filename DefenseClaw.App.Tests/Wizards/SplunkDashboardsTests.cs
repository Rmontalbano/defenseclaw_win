using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-317, the policy and the wizard's pages: <c>setup splunk dashboards</c> is offered behind the Terraform probe instead of being left
/// out, its verbs are the three somebody read the source of, and each builds an exact argv that cannot carry the Splunk token. Synthetic help
/// screens shaped like the 0.8.10 ones, no CLI, no Terraform: nothing here can run <c>setup splunk</c> or anything else.
/// </summary>
public sealed class SplunkDashboardsPolicyTests
{
    private static readonly string[] Group = { "setup", "splunk", "dashboards" };

    // ------------------------------------------------------------------ what needs Terraform

    [Fact]
    public void Only_the_dashboards_card_is_a_target_that_needs_terraform()
    {
        Assert.True(WizardWindowsPolicy.NeedsTerraform("splunk dashboards"));
        Assert.True(WizardWindowsPolicy.NeedsTerraform(SplunkDashboards.Target));

        // The Splunk wizard has other pipelines that need nothing of it.
        Assert.False(WizardWindowsPolicy.NeedsTerraform("splunk"));
        Assert.False(WizardWindowsPolicy.NeedsTerraform("local-observability"));
        Assert.False(WizardWindowsPolicy.NeedsTerraform("claude-code"));
        Assert.False(WizardWindowsPolicy.NeedsTerraform("Splunk Dashboards"));
        Assert.False(WizardWindowsPolicy.NeedsTerraform(string.Empty));

        // And Docker's gate is not Terraform's.
        Assert.False(WizardWindowsPolicy.NeedsDocker(SplunkDashboards.Target));
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("apply")]
    [InlineData("destroy")]
    [InlineData("a-verb-a-newer-cli-adds")]
    public void Every_verb_of_the_group_needs_terraform_and_a_new_one_is_assumed_to(string verb) =>
        Assert.True(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "splunk", "dashboards", verb }));

    [Fact]
    public void The_registrys_options_do_not_change_the_verb_and_the_group_alone_runs_nothing()
    {
        Assert.True(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "splunk", "dashboards", "apply", "--yes" }));
        Assert.True(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "splunk", "dashboards", "destroy", "--yes", "--name-prefix", "x" }));

        // `setup splunk dashboards` and `... --help` print help; they do not drive Terraform.
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(Group));
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "splunk", "dashboards", "--help" }));
    }

    [Fact]
    public void Other_commands_never_need_terraform()
    {
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "splunk" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "splunk", "--o11y" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "local-observability", "up" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "splunk", "dashboards", "apply" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "Splunk", "dashboards", "apply" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "doctor" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(Array.Empty<string>()));
        Assert.False(WizardWindowsPolicy.CommandNeedsTerraform(new[] { "setup", "splunk", "dashboards" }));
    }

    // ------------------------------------------------------------------ nothing hides it

    [Fact]
    public void The_palette_policy_does_not_hide_the_dashboards_by_name_whatever_the_probe_says()
    {
        // The policy has no probe in it: HidesCommand is a fact about names and summaries, and it never matched these. What gates them is the
        // Terraform look, row by row (SplunkDashboardsPaletteTests), not a hide.
        Assert.False(WizardWindowsPolicy.HidesCommand(new[] { "setup", "splunk", "dashboards" }, "Create/update Splunk Observability Cloud dashboards."));
        Assert.False(WizardWindowsPolicy.HidesCommand(new[] { "setup", "splunk", "dashboards" }, "Create or update DefenseClaw Splunk Observability Cloud dashboards."));

        foreach (var (verb, summary) in new[]
        {
            ("plan", "Show Terraform changes for the O11y dashboard bundle."),
            ("apply", "Create or update the O11y dashboards."),
            ("destroy", "Destroy O11y objects managed by the selected Terraform state."),
        })
        {
            Assert.False(WizardWindowsPolicy.HidesCommand(new[] { "setup", "splunk", "dashboards", verb }, summary), verb);
        }

        // What it still hides.
        Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "sandbox", "list" }, string.Empty));
        Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "setup", "openclaw" }, string.Empty));
        Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "tunnel" }, "Needs Docker to run the stack."));
    }

    [Theory]
    [InlineData(PlatformStatus.Certified)]
    [InlineData(PlatformStatus.NotCertified)]
    [InlineData(PlatformStatus.Unknown)]
    [InlineData(PlatformStatus.NotApplicable)]
    public void The_card_is_not_refused_by_name_whatever_the_help_says_about_certification(PlatformStatus status) =>
        Assert.Null(WizardWindowsPolicy.UnavailableReason(SplunkDashboards.Target, status));

    [Fact]
    public void The_splunk_wizard_still_does_not_offer_the_subcommand_because_the_dashboards_have_a_card_of_their_own()
    {
        var splunk = WizardSamples.Splunk();

        // The help lists `dashboards`; the wizard's Command page does not, and there is no page for it.
        Assert.Contains("dashboards", SplunkSamplesCommandNames());
        Assert.DoesNotContain(splunk.AllFields, f => f.Id == "subcommand" && f.Choices.Any(c => c.Value == "dashboards"));
        Assert.DoesNotContain(splunk.Steps, s => s.VisibleWhenValues.Contains("dashboards"));
        Assert.DoesNotContain(splunk.AllFields, f => f.Flag is "--o11y-api-token" or "--terraform-bin");
    }

    private static IEnumerable<string> SplunkSamplesCommandNames() => SetupHelpParser.Parse(WizardSamples.SplunkHelp).Commands.Select(c => c.Name);

    // ------------------------------------------------------------------ the Command page

    [Fact]
    public void The_command_page_offers_plan_apply_and_destroy_in_that_order_and_starts_on_plan()
    {
        var command = WizardSamples.Dashboards().AllFields.Single(f => f.Id == "subcommand");

        Assert.Equal(new[] { "plan", "apply", "destroy" }, command.Choices.Select(c => c.Value).ToArray());
        Assert.All(command.Choices, c => Assert.StartsWith(c.Value, c.Label, StringComparison.Ordinal));
        Assert.Equal("plan", command.DefaultValue);
        Assert.True(command.IsRequired);
        Assert.True(command.IsPositional);
    }

    [Fact]
    public void The_generator_alone_would_start_on_apply_because_click_lists_the_commands_alphabetically()
    {
        // What the policy exists to fix: untouched, the generated page is on the verb that changes Splunk.
        var group = SetupHelpParser.Parse(WizardSamples.DashboardsGroupHelp, commandDepth: 2);
        var verbs = group.Commands.ToDictionary(c => c.Name, c => SetupHelpParser.Parse(WizardSamples.DashboardsVerbHelp[c.Name], commandDepth: 3));

        var raw = WizardStepFactory.BuildGroup(group, verbs).SelectMany(s => s.Fields).Single(f => f.Id == "subcommand");

        Assert.Equal(new[] { "apply", "destroy", "plan" }, raw.Choices.Select(c => c.Value).ToArray());
        Assert.Equal("apply", raw.DefaultValue);
    }

    [Fact]
    public void Shaping_the_command_page_twice_changes_nothing()
    {
        var group = SetupHelpParser.Parse(WizardSamples.DashboardsGroupHelp, commandDepth: 2);
        var verbs = group.Commands.ToDictionary(c => c.Name, c => SetupHelpParser.Parse(WizardSamples.DashboardsVerbHelp[c.Name], commandDepth: 3));
        var once = WizardWindowsPolicy.Filter(SplunkDashboards.Target, WizardStepFactory.BuildGroup(group, verbs));
        var twice = WizardWindowsPolicy.Filter(SplunkDashboards.Target, once);

        Assert.Same(once[0], twice[0]);
        Assert.Equal(once.Select(s => s.Id), twice.Select(s => s.Id));
    }

    [Fact]
    public void A_verb_nobody_has_read_is_not_offered_and_neither_are_its_pages()
    {
        var import = WizardSamples.DashboardsVerbHelp["plan"].Replace("plan", "import", StringComparison.Ordinal);

        var definition = WizardSamples.Dashboards(extraVerbs: new Dictionary<string, string> { ["import"] = import });

        var command = definition.AllFields.Single(f => f.Id == "subcommand");
        Assert.Equal(new[] { "plan", "apply", "destroy" }, command.Choices.Select(c => c.Value).ToArray());
        Assert.DoesNotContain(definition.Steps, s => s.VisibleWhenValues.Contains("import"));
        Assert.DoesNotContain(definition.AllFields, f => f.Id.StartsWith("import:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_policy_keeps_its_hands_off_other_targets_pages()
    {
        var steps = new[]
        {
            new WizardStep
            {
                Id = "command",
                Title = "Command",
                Fields = new[]
                {
                    new WizardField
                    {
                        Id = "subcommand", Label = "Command", Kind = WizardFieldKind.Choice, IsPositional = true,
                        Choices = new[] { new WizardChoice("zebra", "zebra"), new WizardChoice("apple", "apple") },
                    },
                },
            },
            new WizardStep
            {
                Id = "tf",
                Title = "tf",
                VisibleWhenFieldId = "subcommand",
                VisibleWhenValues = new[] { "zebra" },
                Fields = new[] { new WizardField { Id = "zebra:terraform-bin", Label = "x", Kind = WizardFieldKind.Text, Flag = "--terraform-bin" } },
            },
        };

        var filtered = WizardWindowsPolicy.Filter("webhook", steps);

        // Another group's verbs, order and --terraform-bin are its own business.
        Assert.Equal(new[] { "zebra", "apple" }, filtered[0].Fields[0].Choices.Select(c => c.Value).ToArray());
        Assert.Equal("--terraform-bin", Assert.Single(filtered[1].Fields).Flag);
    }

    // ------------------------------------------------------------------ the options

    [Fact]
    public void The_terraform_the_look_found_is_the_one_that_runs_so_the_option_that_names_another_is_not_offered()
    {
        var definition = WizardSamples.Dashboards();

        Assert.DoesNotContain(definition.AllFields, f => f.Flag == "--terraform-bin");

        // Everything else the verbs accept is still there, on each verb's own pages.
        foreach (var verb in new[] { "plan", "apply", "destroy" })
        {
            var flags = definition.AllFields.Where(f => f.Id.StartsWith(verb + ":", StringComparison.Ordinal)).Select(f => f.Flag).ToArray();
            foreach (var flag in new[]
            {
                "--api-url", "--o11y-api-token", "--name-prefix", "--with-detectors", "--enable-detectors", "--detector-notification",
                "--work-dir", "--state", "--plugin-dir", "--skip-init", "--skip-validate", "--timeout",
            })
            {
                Assert.Contains(flag, flags);
            }

            Assert.Equal(verb != "plan", flags.Contains("--yes"));
        }
    }

    // ------------------------------------------------------------------ the exact argv of every verb

    private static WizardValues Answers(WizardDefinition definition, params (string Id, string Value)[] answers)
    {
        var values = WizardSamples.StartingValues(definition);
        foreach (var (id, value) in answers)
        {
            values[id] = value;
        }

        return values;
    }

    [Fact]
    public void Untouched_the_wizard_builds_the_plan_which_changes_nothing_in_splunk()
    {
        var definition = WizardSamples.Dashboards();

        Assert.Equal(new[] { "setup", "splunk", "dashboards", "plan" }, definition.BuildArgv(WizardSamples.StartingValues(definition)));
    }

    [Theory]
    [InlineData("plan", "setup splunk dashboards plan")]
    [InlineData("apply", "setup splunk dashboards apply --yes")]
    [InlineData("destroy", "setup splunk dashboards destroy --yes")]
    public void Every_verb_builds_an_argv_that_names_it_and_the_review_stands_in_for_the_clis_question(string verb, string expected)
    {
        var definition = WizardSamples.Dashboards();

        Assert.Equal(expected.Split(' '), definition.BuildArgv(Answers(definition, ("subcommand", verb))));
    }

    [Fact]
    public void Only_the_options_the_operator_changed_reach_the_argv_in_the_order_of_the_help()
    {
        var definition = WizardSamples.Dashboards();

        Assert.Equal(
            new[]
            {
                "setup", "splunk", "dashboards", "apply",
                "--api-url", "https://api.us1.signalfx.com",
                "--name-prefix", "smoke",
                "--with-detectors", "--enable-detectors",
                "--detector-notification", "Email,secops@example.test",
                "--detector-notification", "Slack,#alerts",
                "--work-dir", @"C:\scratch\work",
                "--state", @"C:\scratch\work\state.tfstate",
                "--plugin-dir", @"C:\scratch\plugins",
                "--skip-init", "--skip-validate",
                "--timeout", "120",
                "--yes",
            },
            definition.BuildArgv(Answers(
                definition,
                ("subcommand", "apply"),
                ("apply:api-url", "https://api.us1.signalfx.com"),
                ("apply:name-prefix", "smoke"),
                ("apply:with-detectors", ToggleValues.On),
                ("apply:enable-detectors", ToggleValues.On),
                ("apply:detector-notification", "Email,secops@example.test\nSlack,#alerts"),
                ("apply:work-dir", @"C:\scratch\work"),
                ("apply:state", @"C:\scratch\work\state.tfstate"),
                ("apply:plugin-dir", @"C:\scratch\plugins"),
                ("apply:skip-init", ToggleValues.On),
                ("apply:skip-validate", ToggleValues.On),
                ("apply:timeout", "120"))));
    }

    [Fact]
    public void A_value_the_cli_would_use_anyway_is_not_sent_and_the_defaults_are_the_clis()
    {
        var definition = WizardSamples.Dashboards();

        // 900 is the CLI's own default; dashboards-only is its default; an empty name prefix is none.
        Assert.Equal(
            new[] { "setup", "splunk", "dashboards", "destroy", "--yes" },
            definition.BuildArgv(Answers(
                definition,
                ("subcommand", "destroy"),
                ("destroy:timeout", "900"),
                ("destroy:with-detectors", ToggleValues.Off),
                ("destroy:name-prefix", "   "))));
    }

    [Fact]
    public void The_options_of_one_verb_never_reach_the_argv_of_another()
    {
        var definition = WizardSamples.Dashboards();

        // Everything about `apply` is set, then the verb is changed: nothing of it may follow.
        var argv = definition.BuildArgv(Answers(
            definition,
            ("apply:name-prefix", "smoke"),
            ("apply:with-detectors", ToggleValues.On),
            ("destroy:state", @"C:\scratch\state.tfstate"),
            ("subcommand", "plan")));

        Assert.Equal(new[] { "setup", "splunk", "dashboards", "plan" }, argv);
    }

    [Fact]
    public void The_terraform_bin_option_has_no_field_so_no_answer_can_put_it_on_the_command_line()
    {
        var definition = WizardSamples.Dashboards();
        var values = Answers(definition, ("subcommand", "apply"), ("apply:terraform-bin", @"C:\elsewhere\terraform.exe"));

        var argv = definition.BuildArgv(values);

        Assert.DoesNotContain("--terraform-bin", argv);
        Assert.DoesNotContain(@"C:\elsewhere\terraform.exe", argv);
    }

    // ------------------------------------------------------------------ the token

    [Fact]
    public void The_token_is_a_secret_field_on_every_verb_and_nothing_put_in_its_answer_reaches_argv()
    {
        var definition = WizardSamples.Dashboards();
        const string Typed = "synthetic-should-never-appear-0317";

        var tokens = definition.AllFields.Where(f => f.Flag == "--o11y-api-token").ToArray();
        Assert.Equal(3, tokens.Length);
        Assert.All(tokens, t => Assert.Equal(WizardFieldKind.Secret, t.Kind));

        foreach (var verb in new[] { "plan", "apply", "destroy" })
        {
            var values = Answers(definition, ("subcommand", verb), (verb + ":o11y-api-token", Typed));
            var argv = definition.BuildArgv(values);

            Assert.All(argv, a => Assert.DoesNotContain("synthetic-should-never-appear", a, StringComparison.Ordinal));
            Assert.DoesNotContain("--o11y-api-token", argv);
        }
    }

    [Fact]
    public void The_token_is_typed_in_the_app_and_delivered_as_sfx_auth_token_and_the_cli_does_not_keep_it()
    {
        var definition = WizardSamples.Dashboards();

        foreach (var verb in new[] { "plan", "apply", "destroy" })
        {
            var field = definition.AllFields.Single(f => f.Id == verb + ":o11y-api-token");
            var route = field.Credential!;

            Assert.Equal("SFX_AUTH_TOKEN", route.InAppVariable(WizardSamples.StartingValues(definition)));
            Assert.Equal("SFX_AUTH_TOKEN", route.EnvName(new WizardValues()));
            Assert.Contains("Splunk Observability Cloud API token", route.Purpose, StringComparison.Ordinal);
            Assert.Contains("not the ingest token", route.Purpose, StringComparison.Ordinal);

            // The card does not say the CLI writes it to .env, because it does not.
            Assert.StartsWith("the CLI does not store it", route.InAppStorage, StringComparison.Ordinal);
            Assert.Contains("token not found", route.IfMissing, StringComparison.Ordinal);
            Assert.Contains("changes nothing", route.IfMissing, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_in_app_route_is_offered_only_while_the_clis_own_help_still_names_the_variable()
    {
        // A CLI that stopped saying so may have stopped reading it; a value supplied that way would be ignored, so the route falls back to the
        // terminal (the card still knows what to store, and under what name).
        var definition = WizardSamples.Dashboards(help => help.Replace("SFX_AUTH_TOKEN", "the token variable", StringComparison.Ordinal));
        var route = definition.AllFields.Single(f => f.Id == "apply:o11y-api-token").Credential!;

        Assert.Null(route.InAppEnvName);
        Assert.Null(route.InAppVariable(WizardSamples.StartingValues(definition)));
        Assert.Equal("SFX_AUTH_TOKEN", route.EnvName(new WizardValues()));
    }

    [Fact]
    public void The_help_the_parser_reads_does_name_the_variable_across_clicks_line_wrap()
    {
        // The real screen wraps "Prefer the / SFX_AUTH_TOKEN environment variable" over two lines under either line ending.
        foreach (var eol in new[] { LineEndings.Lf, LineEndings.Crlf })
        {
            var parsed = SetupHelpParser.Parse(LineEndings.With(WizardSamples.DashboardsVerbHelp["apply"], eol), commandDepth: 3);

            Assert.Contains("SFX_AUTH_TOKEN", parsed.Option("--o11y-api-token")!.Description, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--o11y-api-token", WizardFieldKind.Secret)]
    [InlineData("--detector-notification", WizardFieldKind.Lines)]
    [InlineData("--work-dir", WizardFieldKind.Path)]
    [InlineData("--state", WizardFieldKind.Path)]
    [InlineData("--plugin-dir", WizardFieldKind.Path)]
    [InlineData("--timeout", WizardFieldKind.Integer)]
    [InlineData("--with-detectors", WizardFieldKind.Toggle)]
    [InlineData("--enable-detectors", WizardFieldKind.Switch)]
    [InlineData("--skip-init", WizardFieldKind.Switch)]
    [InlineData("--yes", WizardFieldKind.Switch)]
    [InlineData("--api-url", WizardFieldKind.Text)]
    [InlineData("--name-prefix", WizardFieldKind.Text)]
    public void Each_option_becomes_the_field_it_should(string flag, WizardFieldKind kind)
    {
        var field = WizardSamples.Dashboards().AllFields.First(f => f.Flag == flag && f.Id.StartsWith("apply:", StringComparison.Ordinal));

        Assert.Equal(kind, field.Kind);
    }

    // ------------------------------------------------------------------ the unavailable definition

    [Fact]
    public void A_cli_without_the_group_gets_a_definition_that_says_so_and_has_nothing_to_run()
    {
        var missing = SplunkDashboards.Missing();

        Assert.Equal(SplunkDashboards.Target, missing.Target);
        Assert.NotEmpty(missing.UnavailableReason);
        Assert.Contains("setup splunk dashboards", missing.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("plan, apply, destroy", missing.UnavailableReason, StringComparison.Ordinal);
        Assert.Empty(missing.Steps);
        Assert.True(missing.IsDetailLoaded);
        Assert.Equal(missing.UnavailableReason, missing.With(Array.Empty<WizardStep>(), string.Empty, string.Empty).UnavailableReason);
    }

    [Fact]
    public void The_group_is_present_when_its_help_lists_a_verb_somebody_has_read_and_not_when_it_is_an_error_screen()
    {
        Assert.True(SplunkDashboards.IsPresentIn(SetupHelpParser.Parse(WizardSamples.DashboardsGroupHelp, commandDepth: 2)));

        // What a CLI without the group prints for `setup splunk dashboards --help`: the usage of the group above it, and an error.
        var error = SetupHelpParser.Parse(
            LineEndings.Normalize("""
                Usage: defenseclaw setup splunk [OPTIONS] [COMMAND] [ARGS]...
                Try 'defenseclaw setup splunk --help' for help.

                Error: No such command 'dashboards'.
                """),
            commandDepth: 2);
        Assert.False(SplunkDashboards.IsPresentIn(error));

        // A group of verbs nobody has read is not this one.
        var strangers = SetupHelpParser.Parse(
            LineEndings.Normalize("""
                Usage: defenseclaw setup splunk dashboards [OPTIONS] COMMAND [ARGS]...

                  Something else entirely.

                Options:
                  --help  Show this message and exit.

                Commands:
                  import  Adopt something.
                  prune   Remove something.
                """),
            commandDepth: 2);
        Assert.False(SplunkDashboards.IsPresentIn(strangers));
    }

    [Fact]
    public void The_command_words_of_a_target_are_one_argument_each()
    {
        Assert.Equal(new[] { "splunk", "dashboards" }, WizardDefinition.CommandWords("splunk dashboards"));
        Assert.Equal(new[] { "llm" }, WizardDefinition.CommandWords("llm"));
        Assert.Equal(new[] { "claude-code" }, WizardDefinition.CommandWords("claude-code"));
        Assert.Equal(new[] { "splunk", "dashboards" }, WizardDefinition.CommandWords("  splunk   dashboards "));

        // The degenerate target keeps the argument it always had.
        Assert.Equal(new[] { string.Empty }, WizardDefinition.CommandWords(string.Empty));
    }

    [Fact]
    public void An_ordinary_wizards_argv_is_what_it_always_was()
    {
        var definition = WizardSamples.Llm();

        Assert.Equal(new[] { "setup", "llm" }, definition.BuildArgv(WizardSamples.StartingValues(definition)).Take(2));
    }
}

/// <summary>
/// What a review of a dashboards command says: the sentence for each verb, the bars that are true of it with those flags, that none of the
/// three restarts the gateway, and that a typed value can neither lower a tier nor switch a bar off.
/// </summary>
public sealed class SplunkDashboardsReviewTests
{
    private static string[] Words(string commandLine) => commandLine.Split(' ');

    // ------------------------------------------------------------------ the tiers

    [Theory]
    [InlineData("setup splunk dashboards plan", CommandTier.StateChanging)]
    [InlineData("setup splunk dashboards apply --yes", CommandTier.StateChanging)]
    [InlineData("setup splunk dashboards destroy --yes", CommandTier.Destructive)]
    [InlineData("setup splunk dashboards destroy", CommandTier.Destructive)]
    public void A_review_gives_each_verb_the_tier_its_source_earns_and_never_a_read(string commandLine, CommandTier expected)
    {
        var step = new CommandReviewStep(Words(commandLine), floor: CommandTier.StateChanging);

        Assert.Equal(expected, step.Tier);
        Assert.Equal(expected, CommandReview.ResolveTier(Words(commandLine)));
        Assert.Equal(expected == CommandTier.Destructive, new CommandReview { Title = "x", Steps = new[] { step } }.IsDestructive);
    }

    [Theory]
    [InlineData("plan", "--name-prefix", "--dry-run")]
    [InlineData("apply", "--name-prefix", "--help")]
    [InlineData("destroy", "--name-prefix", "--dry-run")]
    [InlineData("destroy", "--api-url", "--version")]
    [InlineData("destroy", "--detector-notification", "--version-json")]
    public void A_value_that_spells_a_read_only_flag_cannot_make_a_review_look_harmless(string verb, string option, string typed)
    {
        var argv = Words("setup splunk dashboards " + verb).Append(option).Append(typed).ToArray();

        var tier = new CommandReviewStep(argv, floor: CommandTier.StateChanging).Tier;

        Assert.Equal(verb == "destroy" ? CommandTier.Destructive : CommandTier.StateChanging, tier);
    }

    // ------------------------------------------------------------------ the gateway

    [Theory]
    [InlineData("setup splunk dashboards plan")]
    [InlineData("setup splunk dashboards apply --yes")]
    [InlineData("setup splunk dashboards destroy --yes --name-prefix smoke")]
    [InlineData("setup splunk dashboards a-verb-a-newer-cli-adds")]
    public void None_of_the_verbs_restarts_the_gateway_because_none_writes_config_yaml(string commandLine)
    {
        Assert.False(SplunkDashboardsReview.RestartsGateway(Words(commandLine)));

        // The one rule every surface asks agrees; the general rule for a `setup` command would have said yes.
        Assert.False(CommandReview.RestartsGatewayFor(Words(commandLine)));
        Assert.False(WizardReview.RestartsGateway(Words(commandLine)));
    }

    [Fact]
    public void Only_a_dashboards_command_has_an_opinion_and_every_other_setup_command_is_judged_as_before()
    {
        Assert.Null(SplunkDashboardsReview.RestartsGateway(Words("setup splunk --o11y")));
        Assert.Null(SplunkDashboardsReview.RestartsGateway(Words("setup splunk dashboards")));
        Assert.Null(SplunkDashboardsReview.RestartsGateway(Words("setup local-observability up")));
        Assert.Null(SplunkDashboardsReview.RestartsGateway(Words("doctor")));

        Assert.True(CommandReview.RestartsGatewayFor(Words("setup splunk --o11y --non-interactive")));
        Assert.True(CommandReview.RestartsGatewayFor(Words("setup codex --yes")));
        Assert.False(CommandReview.RestartsGatewayFor(Words("setup observability list")));
    }

    // ------------------------------------------------------------------ the sentences

    [Fact]
    public void Each_verb_says_what_it_does_to_splunk()
    {
        var plan = SplunkDashboardsReview.Summary(Words("setup splunk dashboards plan"));
        var apply = SplunkDashboardsReview.Summary(Words("setup splunk dashboards apply --yes"));
        var destroy = SplunkDashboardsReview.Summary(Words("setup splunk dashboards destroy --yes"));

        Assert.Contains("changes nothing there", plan, StringComparison.Ordinal);
        Assert.Contains("working files and state", plan, StringComparison.Ordinal);
        Assert.Contains("Creates or updates", apply, StringComparison.Ordinal);
        Assert.Contains("everything plan does on this machine first", apply, StringComparison.Ordinal);
        Assert.StartsWith("Deletes from Splunk Observability Cloud everything the Terraform state manages", destroy, StringComparison.Ordinal);
        Assert.Contains("adopted", destroy, StringComparison.Ordinal);
        Assert.Equal(3, new[] { plan, apply, destroy }.Distinct().Count());
    }

    [Fact]
    public void A_command_that_is_not_a_dashboards_verb_has_no_sentence_and_no_bars()
    {
        foreach (var commandLine in new[] { "setup splunk dashboards", "setup splunk --o11y", "setup local-observability reset --yes", "doctor", "setup splunk dashboards a-verb-a-newer-cli-adds" })
        {
            Assert.Equal(string.Empty, SplunkDashboardsReview.Summary(Words(commandLine)));
        }

        Assert.Empty(SplunkDashboardsReview.Warnings(Words("setup splunk --o11y"), CredentialPresence.NotSet));
        Assert.Empty(SplunkDashboardsReview.Warnings(Words("setup splunk dashboards"), CredentialPresence.NotSet));
        Assert.Empty(SplunkDashboardsReview.Warnings(Words("setup splunk dashboards a-verb-a-newer-cli-adds --yes"), CredentialPresence.NotSet));
    }

    [Fact]
    public void Each_verb_has_a_one_line_description_for_its_palette_row_and_a_stranger_has_none()
    {
        foreach (var verb in SplunkDashboards.Verbs)
        {
            var line = SplunkDashboardsReview.RowDescription(verb);

            Assert.NotEmpty(line);
            Assert.DoesNotContain('\n', line);
            Assert.Contains("Splunk Observability Cloud", line, StringComparison.Ordinal);
        }

        Assert.Contains("Changes nothing there", SplunkDashboardsReview.RowDescription("plan"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, SplunkDashboardsReview.RowDescription("import"));
    }

    // ------------------------------------------------------------------ the bars

    private static string[] Titles(string commandLine, CredentialPresence? token = null) =>
        SplunkDashboardsReview.Warnings(Words(commandLine), token).Select(w => w.Title).ToArray();

    [Fact]
    public void Plan_carries_no_bar_because_it_changes_nothing_in_splunk()
    {
        Assert.Empty(Titles("setup splunk dashboards plan"));
        Assert.Empty(Titles("setup splunk dashboards plan --with-detectors --name-prefix x"));
    }

    [Fact]
    public void Apply_says_it_goes_on_after_the_plan_and_that_without_detectors_it_can_remove_the_ones_that_exist()
    {
        Assert.Equal(
            new[] { SplunkDashboardsReview.AppliesNowTitle, SplunkDashboardsReview.RemovesDetectorsTitle },
            Titles("setup splunk dashboards apply --yes"));

        var applies = SplunkDashboardsReview.Warnings(Words("setup splunk dashboards apply --yes"))[0];
        Assert.Contains("--yes stands in for the CLI's own", applies.Message, StringComparison.Ordinal);
        Assert.Contains("run plan", applies.Message, StringComparison.Ordinal);

        var removes = SplunkDashboardsReview.Warnings(Words("setup splunk dashboards apply --yes"))[1];
        Assert.Contains("--with-detectors", removes.Message, StringComparison.Ordinal);
        Assert.Contains("removes them from Splunk", removes.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_with_detectors_on_does_not_say_it_removes_them()
    {
        Assert.Equal(
            new[] { SplunkDashboardsReview.AppliesNowTitle },
            Titles("setup splunk dashboards apply --yes --with-detectors"));
        Assert.Equal(
            new[] { SplunkDashboardsReview.AppliesNowTitle },
            Titles("setup splunk dashboards apply --with-detectors --enable-detectors --yes"));
    }

    [Fact]
    public void A_value_that_spells_with_detectors_does_not_turn_detectors_on()
    {
        // `--name-prefix --with-detectors` names a prefix; it creates no detector, so the bar about removing them still stands.
        Assert.Equal(
            new[] { SplunkDashboardsReview.AppliesNowTitle, SplunkDashboardsReview.RemovesDetectorsTitle },
            Titles("setup splunk dashboards apply --yes --name-prefix --with-detectors"));

        // And nothing after a `--` is an option at all.
        Assert.Contains(
            SplunkDashboardsReview.RemovesDetectorsTitle,
            Titles("setup splunk dashboards apply --yes -- --with-detectors"));
    }

    [Fact]
    public void Destroy_says_it_deletes_and_that_the_review_is_the_question_the_cli_would_ask_when_yes_is_on_the_command()
    {
        var bar = Assert.Single(SplunkDashboardsReview.Warnings(Words("setup splunk dashboards destroy --yes")));

        Assert.Equal(SplunkDashboardsReview.DeletesTitle, bar.Title);
        Assert.Contains("deletes every Splunk Observability Cloud object its state records", bar.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", bar.Message, StringComparison.Ordinal);
        Assert.Contains("which is why --yes is on the command", bar.Message, StringComparison.Ordinal);

        var without = Assert.Single(SplunkDashboardsReview.Warnings(Words("setup splunk dashboards destroy")));
        Assert.DoesNotContain("which is why --yes", without.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", without.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setup splunk dashboards plan")]
    [InlineData("setup splunk dashboards apply --yes")]
    [InlineData("setup splunk dashboards destroy --yes")]
    public void A_surface_that_cannot_ask_for_the_token_says_so_when_none_is_set_and_otherwise_stays_quiet(string commandLine)
    {
        var withoutToken = Titles(commandLine, CredentialPresence.NotSet);
        Assert.Equal(SplunkDashboardsReview.NoTokenTitle, withoutToken[^1]);

        var bar = SplunkDashboardsReview.Warnings(Words(commandLine), CredentialPresence.NotSet)[^1];
        Assert.Contains("SFX_AUTH_TOKEN", bar.Message, StringComparison.Ordinal);
        Assert.Contains("will stop at once", bar.Message, StringComparison.Ordinal);
        Assert.Contains("change nothing", bar.Message, StringComparison.Ordinal);
        Assert.Contains("Setup page", bar.Message, StringComparison.Ordinal);

        foreach (var present in new CredentialPresence?[] { null, CredentialPresence.Unknown, CredentialPresence.InDotEnv, CredentialPresence.InEnvironment })
        {
            Assert.DoesNotContain(SplunkDashboardsReview.NoTokenTitle, Titles(commandLine, present));
        }
    }

    [Fact]
    public void The_wizards_own_review_does_not_judge_the_token_because_its_card_asks_for_it()
    {
        Assert.DoesNotContain(SplunkDashboardsReview.NoTokenTitle, Titles("setup splunk dashboards apply --yes"));
    }
}
