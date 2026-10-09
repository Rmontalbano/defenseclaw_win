using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-311: <c>setup local-observability</c> is offered on Windows behind the Docker probe instead of being hidden by name. The evidence
/// for that is in <see cref="WizardWindowsPolicy.LocalObservabilityTarget"/>; these tests hold what follows from it: the policy no longer
/// hides it, the wizard's command page names a real verb, each verb's argv and tier are exact, and the hub's card and the palette's rows
/// follow the probe. Synthetic help screens, fake probes, an isolated runner with no CLI: nothing here can start Docker, DefenseClaw or
/// any process, and nothing runs <c>setup local-observability</c> for real.
/// </summary>
public sealed class LocalObservabilityPolicyTests
{
    private static readonly string[] Verbs = { "up", "down", "status", "logs", "url", "env", "reset" };

    private static WizardValues Answers(WizardDefinition definition, params (string Id, string Value)[] answers)
    {
        var values = WizardSamples.StartingValues(definition);
        foreach (var (id, value) in answers)
        {
            values[id] = value;
        }

        return values;
    }

    // ------------------------------------------------------------------ the static hide is gone

    [Theory]
    [InlineData(PlatformStatus.Certified)]
    [InlineData(PlatformStatus.NotCertified)]
    [InlineData(PlatformStatus.Unknown)]
    [InlineData(PlatformStatus.NotApplicable)]
    public void The_stack_is_not_refused_by_name_whatever_the_help_says_about_certification(PlatformStatus status) =>
        Assert.Null(WizardWindowsPolicy.UnavailableReason("local-observability", status));

    [Fact]
    public void Only_a_cli_that_itself_calls_it_unsupported_still_refuses_it()
    {
        // The policy takes the CLI's word: nothing of its own is left, but an "unsupported on windows" line in the help still wins.
        var reason = WizardWindowsPolicy.UnavailableReason("local-observability", PlatformStatus.Unsupported);

        Assert.Contains("unsupported on Windows", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Hyper-V", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_palette_does_not_hide_the_stack_but_still_hides_what_windows_does_not_run()
    {
        Assert.False(WizardWindowsPolicy.HidesCommand(new[] { "setup", "local-observability" }, "Run the bundled Prom/Loki/Tempo/Grafana stack on loopback."));

        // Every verb, with the summary the installed 0.8.10 CLI gives it.
        var summaries = SetupHelpParser.Parse(WizardSamples.LocalObservabilityHelp).Commands;
        Assert.Equal(Verbs.Order(StringComparer.Ordinal), summaries.Select(c => c.Name).Order(StringComparer.Ordinal));
        foreach (var verb in summaries)
        {
            Assert.False(WizardWindowsPolicy.HidesCommand(new[] { "setup", "local-observability", verb.Name }, verb.Summary), verb.Name);
        }

        // What it still hides: sandboxes, OpenClaw, ZeptoClaw, anything whose summary needs Docker, and the --with-local-stack flow.
        Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "sandbox", "list" }, string.Empty));
        Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "setup", "openclaw" }, string.Empty));
        Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "setup", "zeptoclaw" }, string.Empty));
        Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "tunnel" }, "Needs Docker to run the stack."));
        Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "setup", "local-stack" }, string.Empty));
        Assert.False(WizardWindowsPolicy.HidesCommand(new[] { "doctor" }, "Check the install."));
    }

    // ------------------------------------------------------------------ what needs Docker

    [Fact]
    public void Only_the_observability_stack_is_a_target_that_needs_docker()
    {
        Assert.True(WizardWindowsPolicy.NeedsDocker("local-observability"));

        // Local Splunk is one pipeline of a wizard that has others: its gate is the wizard's own page, not the card.
        Assert.False(WizardWindowsPolicy.NeedsDocker("splunk"));
        Assert.False(WizardWindowsPolicy.NeedsDocker("claude-code"));
        Assert.False(WizardWindowsPolicy.NeedsDocker("observability"));
        Assert.False(WizardWindowsPolicy.NeedsDocker("Local-Observability"));
    }

    [Theory]
    [InlineData("up", true)]
    [InlineData("down", true)]
    [InlineData("status", true)]
    [InlineData("logs", true)]
    [InlineData("reset", true)]
    [InlineData("url", false)] // prints constants; never reaches Docker
    [InlineData("env", false)] // prints constants; never reaches Docker
    [InlineData("a-verb-a-newer-cli-adds", true)]
    public void Only_the_verbs_that_run_compose_need_docker(string verb, bool expected) =>
        Assert.Equal(expected, WizardWindowsPolicy.CommandNeedsDocker(new[] { "setup", "local-observability", verb }));

    [Fact]
    public void The_group_run_bare_is_its_up_and_needs_docker_and_the_registrys_argv_with_its_options_is_judged_by_its_verb()
    {
        // `setup local-observability` with no verb is not a listing of commands: the CLI's group callback invokes `up`.
        Assert.True(WizardWindowsPolicy.CommandNeedsDocker(new[] { "setup", "local-observability" }));

        // The TUI registry's `reset` carries its --yes; the verb decides, not what follows it.
        Assert.True(WizardWindowsPolicy.CommandNeedsDocker(new[] { "setup", "local-observability", "reset", "--yes" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsDocker(new[] { "setup", "local-observability", "url", "--json" }));
    }

    [Fact]
    public void Other_groups_and_other_commands_never_need_docker()
    {
        Assert.False(WizardWindowsPolicy.CommandNeedsDocker(new[] { "setup", "splunk", "logs" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsDocker(new[] { "setup", "claude-code" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsDocker(new[] { "local-observability", "up" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsDocker(new[] { "doctor" }));
        Assert.False(WizardWindowsPolicy.CommandNeedsDocker(Array.Empty<string>()));
    }

    // ------------------------------------------------------------------ the wizard's command page

    [Fact]
    public void The_command_page_offers_the_seven_real_verbs_status_first_and_reset_last_and_starts_on_status()
    {
        var command = WizardSamples.LocalObservability().AllFields.Single(f => f.Id == "subcommand");

        Assert.Equal(new[] { "status", "up", "down", "logs", "url", "env", "reset" }, command.Choices.Select(c => c.Value).ToArray());
        Assert.All(command.Choices, c => Assert.StartsWith(c.Value, c.Label, StringComparison.Ordinal));
        Assert.Equal("status", command.DefaultValue);
        Assert.True(command.IsRequired);
        Assert.True(command.IsPositional);
    }

    [Fact]
    public void The_bare_alias_for_up_is_not_offered_because_the_review_would_not_name_what_it_does()
    {
        var parsed = SetupHelpParser.Parse(WizardSamples.LocalObservabilityHelp);
        var subcommands = parsed.Commands.ToDictionary(c => c.Name, c => SetupHelpParser.Parse(WizardSamples.LocalObservabilitySubcommandHelp[c.Name], commandDepth: 2));
        var generated = WizardStepFactory.BuildGroup(parsed, subcommands);

        // What the generator makes of "setup local-observability [COMMAND]": a first choice that runs nothing by name.
        var raw = generated.SelectMany(s => s.Fields).Single(f => f.Id == "subcommand");
        Assert.Contains(raw.Choices, c => c.Value.Length == 0);
        Assert.Equal(string.Empty, raw.DefaultValue);
        Assert.False(raw.IsRequired);

        // And what the policy leaves.
        var kept = WizardWindowsPolicy.Filter("local-observability", generated).SelectMany(s => s.Fields).Single(f => f.Id == "subcommand");
        Assert.DoesNotContain(kept.Choices, c => c.Value.Length == 0);
    }

    [Fact]
    public void Shaping_the_command_page_twice_changes_nothing_and_other_groups_keep_their_bare_choice()
    {
        var parsed = SetupHelpParser.Parse(WizardSamples.LocalObservabilityHelp);
        var subcommands = parsed.Commands.ToDictionary(c => c.Name, c => SetupHelpParser.Parse(WizardSamples.LocalObservabilitySubcommandHelp[c.Name], commandDepth: 2));
        var once = WizardWindowsPolicy.Filter("local-observability", WizardStepFactory.BuildGroup(parsed, subcommands));
        var twice = WizardWindowsPolicy.Filter("local-observability", once);

        Assert.Same(once[0], twice[0]);

        // Galileo is a group with the same optional-command shape: its empty choice is its guided setup, and stays.
        var galileo = WizardSamples.Galileo().AllFields.Single(f => f.Id == "subcommand");
        Assert.Contains(galileo.Choices, c => c.Value.Length == 0);
        Assert.Equal(string.Empty, galileo.DefaultValue);
    }

    [Fact]
    public void A_verb_a_newer_cli_adds_keeps_its_place_after_the_known_ones_and_a_missing_status_starts_on_the_first()
    {
        var field = new WizardField
        {
            Id = "subcommand",
            Label = "Command",
            Kind = WizardFieldKind.Choice,
            IsPositional = true,
            Choices = new[] { new WizardChoice(string.Empty, "(bare)"), new WizardChoice("zebra", "zebra"), new WizardChoice("reset", "reset"), new WizardChoice("up", "up"), new WizardChoice("apple", "apple") },
        };
        var step = new WizardStep { Id = "command", Title = "Command", Fields = new[] { field } };

        var shaped = Assert.Single(WizardWindowsPolicy.Filter("local-observability", new[] { step })).Fields[0];

        Assert.Equal(new[] { "up", "reset", "zebra", "apple" }, shaped.Choices.Select(c => c.Value).ToArray());
        Assert.Equal("up", shaped.DefaultValue);
    }

    // ------------------------------------------------------------------ the exact argv of every verb

    [Fact]
    public void Untouched_the_wizard_builds_the_read_only_status()
    {
        var definition = WizardSamples.LocalObservability();

        Assert.Equal(new[] { "setup", "local-observability", "status" }, definition.BuildArgv(WizardSamples.StartingValues(definition)));
    }

    [Theory]
    [InlineData("up", "setup local-observability up")]
    [InlineData("down", "setup local-observability down")]
    [InlineData("status", "setup local-observability status")]
    [InlineData("logs", "setup local-observability logs")]
    [InlineData("url", "setup local-observability url")]
    [InlineData("env", "setup local-observability env")]
    [InlineData("reset", "setup local-observability reset --yes")]
    public void Every_verb_builds_an_argv_that_names_it_and_a_reset_answers_the_cli_s_question_in_the_review(string verb, string expected)
    {
        var definition = WizardSamples.LocalObservability();

        Assert.Equal(expected.Split(' '), definition.BuildArgv(Answers(definition, ("subcommand", verb))));
    }

    [Fact]
    public void Only_the_options_the_operator_changed_reach_the_argv_in_the_order_of_the_help()
    {
        var definition = WizardSamples.LocalObservability();

        Assert.Equal(
            new[] { "setup", "local-observability", "up", "--timeout", "60", "--no-wait", "--signals", "traces", "--service-name", "edge", "--no-refresh-config" },
            definition.BuildArgv(Answers(
                definition,
                ("subcommand", "up"),
                ("up:timeout", "60"),
                ("up:no-wait", ToggleValues.On),
                ("up:signals", "traces"),
                ("up:service-name", "edge"),
                ("up:refresh-config", ToggleValues.Off))));

        Assert.Equal(
            new[] { "setup", "local-observability", "down", "--disable-config" },
            definition.BuildArgv(Answers(definition, ("subcommand", "down"), ("down:disable-config", ToggleValues.On))));

        Assert.Equal(
            new[] { "setup", "local-observability", "logs", "--service", "loki", "--follow" },
            definition.BuildArgv(Answers(definition, ("subcommand", "logs"), ("logs:service", "loki"), ("logs:follow", ToggleValues.On))));

        Assert.Equal(
            new[] { "setup", "local-observability", "url", "--json" },
            definition.BuildArgv(Answers(definition, ("subcommand", "url"), ("url:json", ToggleValues.On))));

        Assert.Equal(
            new[] { "setup", "local-observability", "env", "--json" },
            definition.BuildArgv(Answers(definition, ("subcommand", "env"), ("env:json", ToggleValues.On))));
    }

    [Fact]
    public void The_options_of_one_verb_never_reach_the_argv_of_another()
    {
        var definition = WizardSamples.LocalObservability();

        // Everything about `up` is set, then the verb is changed: nothing of it may follow.
        var argv = definition.BuildArgv(Answers(
            definition,
            ("up:timeout", "5"),
            ("up:no-config", ToggleValues.On),
            ("logs:service", "loki"),
            ("subcommand", "status")));

        Assert.Equal(new[] { "setup", "local-observability", "status" }, argv);
    }
}

/// <summary>
/// The tier and the gateway-restart claim of each verb, held against what the installed source does. A review that said "changes state"
/// for <c>url</c>, or "restarts the gateway" for a <c>down</c> that leaves config.yaml alone (or stayed silent about it for an <c>up</c>
/// that rewrites it), would be wrong in the way that teaches an operator to stop reading it.
/// </summary>
public sealed class LocalObservabilityTierTests
{
    [Theory]
    [InlineData("setup local-observability status", CommandTier.ReadOnly)]
    [InlineData("setup local-observability logs", CommandTier.ReadOnly)]
    [InlineData("setup local-observability logs --service loki", CommandTier.ReadOnly)]
    [InlineData("setup local-observability logs --follow", CommandTier.ReadOnly)]
    [InlineData("setup local-observability url", CommandTier.ReadOnly)]
    [InlineData("setup local-observability url --json", CommandTier.ReadOnly)]
    [InlineData("setup local-observability env", CommandTier.ReadOnly)]
    [InlineData("setup local-observability env --json", CommandTier.ReadOnly)]
    [InlineData("setup local-observability up", CommandTier.StateChanging)]
    [InlineData("setup local-observability up --no-wait", CommandTier.StateChanging)]
    [InlineData("setup local-observability down", CommandTier.StateChanging)]
    [InlineData("setup local-observability down --disable-config", CommandTier.StateChanging)]
    [InlineData("setup local-observability reset", CommandTier.Destructive)]
    [InlineData("setup local-observability reset --yes", CommandTier.Destructive)]
    public void Each_verb_has_the_tier_its_source_earns(string commandLine, CommandTier expected)
    {
        var argv = commandLine.Split(' ');

        Assert.Equal(expected, CommandReview.ResolveTier(argv));
        Assert.Equal(expected, new CommandReviewStep(argv).Tier);
    }

    [Theory]
    [InlineData("setup local-observability up --service-name status")]
    [InlineData("setup local-observability up --endpoint url")]
    [InlineData("setup local-observability up --signals logs")]
    [InlineData("setup local-observability down --disable-config status")]
    public void Text_typed_into_an_option_cannot_make_a_change_look_like_a_read(string commandLine) =>
        Assert.Equal(CommandTier.StateChanging, CommandReview.ResolveTier(commandLine.Split(' ')));

    [Fact]
    public void A_review_may_raise_a_read_to_a_change_but_never_lower_a_change()
    {
        var status = new[] { "setup", "local-observability", "status" };

        Assert.Equal(CommandTier.ReadOnly, new CommandReviewStep(status).Tier);
        Assert.Equal(CommandTier.StateChanging, new CommandReviewStep(status, floor: CommandTier.StateChanging).Tier);
        Assert.Equal(CommandTier.StateChanging, new CommandReviewStep(new[] { "setup", "local-observability", "up" }, floor: CommandTier.ReadOnly).Tier);
    }

    // The verbs restart nothing themselves; the `setup` group's result callback restarts a running gateway after a subcommand that changed
    // config.yaml (cmd_setup.py, _auto_restart_sidecar_after_setup): up (unless --no-config / --no-wait) and down --disable-config.
    [Theory]
    [InlineData("setup local-observability up", true)]
    [InlineData("setup local-observability up --timeout 60 --signals traces", true)]
    [InlineData("setup local-observability up --no-refresh-bundle", true)]
    [InlineData("setup local-observability up --no-config", false)]
    [InlineData("setup local-observability up --no-wait", false)]
    [InlineData("setup local-observability up --no-wait --no-config", false)]
    [InlineData("setup local-observability up --timeout 60 --no-config", false)]
    [InlineData("setup local-observability down", false)]
    [InlineData("setup local-observability down --disable-config", true)]
    [InlineData("setup local-observability reset", false)]
    [InlineData("setup local-observability reset --yes", false)]
    [InlineData("setup local-observability status", false)]
    [InlineData("setup local-observability logs --follow", false)]
    [InlineData("setup local-observability url --json", false)]
    [InlineData("setup local-observability env", false)]
    public void Only_the_verbs_that_rewrite_config_yaml_restart_the_gateway(string commandLine, bool restarts)
    {
        Assert.Equal(restarts, CommandReview.RestartsGatewayFor(commandLine.Split(' ')));
        Assert.Equal(restarts, WizardReview.RestartsGateway(commandLine.Split(' ')));
    }

    [Theory]
    [InlineData("setup local-observability up --service-name --no-config")]
    [InlineData("setup local-observability up --endpoint --no-wait")]
    [InlineData("setup local-observability up --signals --no-config --no-refresh-bundle")]
    public void A_flag_that_is_the_value_of_the_option_before_it_does_not_count_and_the_gateway_restart_is_still_said(string commandLine)
    {
        // Click reads the second word as the option's value ("--no-config" becomes the service name) and the command still writes config.yaml.
        Assert.True(CommandReview.RestartsGatewayFor(commandLine.Split(' ')));
    }

    [Fact]
    public void A_verb_a_newer_cli_adds_is_judged_by_the_general_rule_for_setup_commands()
    {
        Assert.True(CommandReview.RestartsGatewayFor(new[] { "setup", "local-observability", "a-verb-a-newer-cli-adds" }));
        Assert.False(CommandReview.RestartsGatewayFor(new[] { "setup", "local-observability", "a-verb-a-newer-cli-adds", "--no-restart" }));
        Assert.True(CommandReview.RestartsGatewayFor(new[] { "setup", "local-observability" }));
    }

    [Theory]
    [InlineData("setup claude-code", true)]
    [InlineData("setup observability add x", true)]
    [InlineData("setup splunk --logs", true)]
    [InlineData("setup observability list", false)]
    public void Every_other_setup_command_is_judged_as_before(string commandLine, bool restarts) =>
        Assert.Equal(restarts, CommandReview.RestartsGatewayFor(commandLine.Split(' ')));

    [Fact]
    public void The_four_reads_are_the_only_setup_commands_on_the_list_that_may_run_unreviewed()
    {
        var reads = new[] { "env", "logs", "status", "url" };
        var listed = CommandTiers.UnreviewedReadPaths.Where(p => p.StartsWith("setup ", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(reads.Select(v => "setup local-observability " + v).ToArray(), listed);
        Assert.All(reads, v => Assert.True(CommandReview.MayRunUnreviewed(new[] { "setup", "local-observability", v }), v));
        Assert.All(new[] { "up", "down", "reset" }, v => Assert.False(CommandReview.MayRunUnreviewed(new[] { "setup", "local-observability", v }), v));

        // A flag, however harmless, takes it off the list: the list is for the bare noun path only.
        Assert.False(CommandReview.MayRunUnreviewed(new[] { "setup", "local-observability", "url", "--json" }));
    }
}

/// <summary>
/// What the review says beyond the command and the tier: only what each verb really does (data deleted, files overwritten, a destination
/// left enabled, the gateway restarted when config.yaml is rewritten), the Docker look's cautions for the verbs that reach Docker, and the
/// one flag the review stands in for.
/// </summary>
public sealed class LocalStackReviewTests
{
    private static readonly string[] Verbs = { "up", "down", "status", "logs", "url", "env", "reset" };

    private static string[] Argv(string commandLine) => commandLine.Split(' ');

    private static string[] Titles(string commandLine, DockerStatus? docker = null) =>
        LocalStackReview.Warnings(Argv(commandLine), docker).Select(w => w.Title).ToArray();

    [Fact]
    public void A_command_that_is_not_the_stacks_carries_nothing_of_it()
    {
        Assert.Empty(LocalStackReview.Warnings(Argv("setup claude-code"), null));
        Assert.Empty(LocalStackReview.Warnings(Argv("setup splunk --logs"), null));
        Assert.Empty(LocalStackReview.Warnings(Array.Empty<string>(), null));
        Assert.False(LocalStackReview.IsStackCommand(Argv("setup"), out _));
        Assert.False(LocalStackReview.IsStackCommand(Argv("local-observability up"), out _));
        Assert.True(LocalStackReview.IsStackCommand(Argv("setup local-observability up"), out var verb));
        Assert.Equal("up", verb);
    }

    [Fact]
    public void The_group_run_bare_is_judged_as_its_up_because_that_is_what_the_cli_runs()
    {
        // `setup local-observability` with no verb is not a listing: the group callback invokes `up` (cmd_setup_local_observability.py).
        Assert.True(LocalStackReview.IsStackCommand(Argv("setup local-observability"), out var verb));
        Assert.Equal("up", verb);

        Assert.True(LocalStackReview.RestartsGateway(Argv("setup local-observability")));
        Assert.Equal(LocalStackReview.Summary(Argv("setup local-observability up")), LocalStackReview.Summary(Argv("setup local-observability")));
        Assert.Equal(Titles("setup local-observability up"), Titles("setup local-observability"));
        Assert.Equal(new[] { LocalStackReview.RefreshesFilesTitle, LocalStackReview.DockerMayRefuseTitle },
            Titles("setup local-observability", new DockerStatus(DockerState.Ready, "Docker is running.", new[] { "Docker Desktop is using the WSL 2 backend." })));
    }

    [Fact]
    public void Up_says_it_refreshes_the_stacks_files_first_and_what_that_overwrites_and_leaves_the_gateway_restart_to_the_surface()
    {
        // The restart itself is the surface's standard bar (from RestartsGateway); this one is about the stack's own files.
        var bar = Assert.Single(LocalStackReview.Warnings(Argv("setup local-observability up"), null));

        Assert.Equal(LocalStackReview.RefreshesFilesTitle, bar.Title);
        Assert.Contains("~/.defenseclaw/observability-stack/", bar.Message, StringComparison.Ordinal);
        Assert.Contains("overwrites any edits", bar.Message, StringComparison.Ordinal);
        Assert.Contains("--no-refresh-config keeps them", bar.Message, StringComparison.Ordinal);
        Assert.Contains("already running is stopped, refreshed and started again", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Up_says_only_what_the_flags_leave_true_about_its_files()
    {
        // The refresh happens whether or not config.yaml is written, so --no-config and --no-wait change nothing here.
        Assert.Equal(new[] { LocalStackReview.RefreshesFilesTitle }, Titles("setup local-observability up --no-config"));
        Assert.Equal(new[] { LocalStackReview.RefreshesFilesTitle }, Titles("setup local-observability up --no-wait"));

        // --no-refresh-config keeps the operator's edits, and a running stack is still stopped, refreshed and started again.
        var kept = Assert.Single(LocalStackReview.Warnings(Argv("setup local-observability up --no-refresh-config"), null));
        Assert.Equal(LocalStackReview.RefreshesFilesTitle, kept.Title);
        Assert.DoesNotContain("overwrites any edits", kept.Message, StringComparison.Ordinal);
        Assert.Contains("are kept", kept.Message, StringComparison.Ordinal);
        Assert.Contains("already running is stopped, refreshed and started again", kept.Message, StringComparison.Ordinal);

        // --no-refresh-bundle turns the whole refresh off; the same words as an option's value do not.
        Assert.Empty(Titles("setup local-observability up --no-refresh-bundle"));
        Assert.Empty(Titles("setup local-observability up --no-config --no-refresh-bundle"));
        Assert.Equal(new[] { LocalStackReview.RefreshesFilesTitle }, Titles("setup local-observability up --service-name --no-refresh-bundle"));
    }

    [Fact]
    public void Down_says_the_destination_stays_enabled_unless_asked_to_disable_it()
    {
        var stays = Assert.Single(LocalStackReview.Warnings(Argv("setup local-observability down"), null));
        Assert.Equal(LocalStackReview.ExportingContinuesTitle, stays.Title);
        Assert.Contains("stays enabled in config.yaml", stays.Message, StringComparison.Ordinal);
        Assert.Contains("--disable-config does that", stays.Message, StringComparison.Ordinal);

        Assert.Empty(Titles("setup local-observability down --disable-config"));

        // A "--disable-config" that is only the value of the option before it is not the flag.
        Assert.Equal(new[] { LocalStackReview.ExportingContinuesTitle }, Titles("setup local-observability down --x --disable-config"));
    }

    [Fact]
    public void Reset_says_what_it_deletes_and_that_the_destination_stays_enabled()
    {
        Assert.Equal(
            new[] { LocalStackReview.DeletesDataTitle, LocalStackReview.ExportingContinuesTitle },
            Titles("setup local-observability reset --yes"));

        var reset = LocalStackReview.Warnings(Argv("setup local-observability reset --yes"), null)[0];
        Assert.Contains("Prometheus, Loki, Tempo and Grafana", reset.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", reset.Message, StringComparison.Ordinal);
        Assert.Contains("--yes", reset.Message, StringComparison.Ordinal);

        // Without --yes the CLI asks its own question, and the review does not claim to have asked it.
        var bare = LocalStackReview.Warnings(Argv("setup local-observability reset"), null)[0];
        Assert.DoesNotContain("--yes", bare.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_restart_the_surfaces_add_comes_from_the_one_rule_and_only_a_stack_command_has_an_opinion()
    {
        Assert.True(LocalStackReview.RestartsGateway(Argv("setup local-observability up")));
        Assert.False(LocalStackReview.RestartsGateway(Argv("setup local-observability up --no-config")));
        Assert.True(LocalStackReview.RestartsGateway(Argv("setup local-observability down --disable-config")));
        Assert.False(LocalStackReview.RestartsGateway(Argv("setup local-observability reset --yes")));

        Assert.Null(LocalStackReview.RestartsGateway(Argv("setup claude-code")));
        Assert.Null(LocalStackReview.RestartsGateway(Argv("setup local-observability a-verb-a-newer-cli-adds")));
        Assert.Null(LocalStackReview.RestartsGateway(Array.Empty<string>()));
    }

    [Fact]
    public void The_reads_carry_no_warning_of_their_own()
    {
        foreach (var verb in new[] { "status", "logs", "url", "env" })
        {
            Assert.Empty(Titles("setup local-observability " + verb));
        }
    }

    [Fact]
    public void The_docker_looks_cautions_are_shown_for_the_verbs_that_reach_docker_and_not_for_the_ones_that_do_not()
    {
        var caution = new DockerStatus(DockerState.Ready, "Docker is running.", new[] { "Docker Desktop is using the WSL 2 backend.", "This looks like a per-user Docker Desktop install." });

        Assert.Contains(LocalStackReview.DockerMayRefuseTitle, Titles("setup local-observability status", caution));
        Assert.Contains(LocalStackReview.DockerMayRefuseTitle, Titles("setup local-observability up", caution));
        Assert.Contains(LocalStackReview.DockerMayRefuseTitle, Titles("setup local-observability reset --yes", caution));
        Assert.DoesNotContain(LocalStackReview.DockerMayRefuseTitle, Titles("setup local-observability url", caution));
        Assert.DoesNotContain(LocalStackReview.DockerMayRefuseTitle, Titles("setup local-observability env --json", caution));

        var bar = LocalStackReview.Warnings(Argv("setup local-observability status"), caution).Single(w => w.Title == LocalStackReview.DockerMayRefuseTitle);
        Assert.Contains("WSL 2 backend", bar.Message, StringComparison.Ordinal);
        Assert.Contains("per-user", bar.Message, StringComparison.Ordinal);
        Assert.Contains("refuse", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_look_and_a_clean_look_add_no_docker_bar()
    {
        Assert.DoesNotContain(LocalStackReview.DockerMayRefuseTitle, Titles("setup local-observability up", null));
        Assert.DoesNotContain(LocalStackReview.DockerMayRefuseTitle, Titles("setup local-observability up", new DockerStatus(DockerState.Ready, "Docker is running.", Array.Empty<string>())));
    }

    [Fact]
    public void Every_verb_has_a_sentence_for_what_it_does_and_the_four_reads_say_they_only_read()
    {
        foreach (var verb in Verbs)
        {
            Assert.NotEmpty(LocalStackReview.Summary(Argv("setup local-observability " + verb)));
        }

        foreach (var verb in new[] { "status", "logs", "url", "env" })
        {
            Assert.StartsWith("Only reads:", LocalStackReview.Summary(Argv("setup local-observability " + verb)), StringComparison.Ordinal);
        }

        foreach (var verb in new[] { "up", "down", "reset" })
        {
            Assert.DoesNotContain("Only reads", LocalStackReview.Summary(Argv("setup local-observability " + verb)), StringComparison.Ordinal);
        }

        Assert.Contains("deletes their data volumes", LocalStackReview.Summary(Argv("setup local-observability reset")), StringComparison.Ordinal);
        Assert.Contains("data volumes are kept", LocalStackReview.Summary(Argv("setup local-observability down")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_that_is_not_the_stacks_or_a_verb_nobody_here_knows_has_no_sentence_of_its_own()
    {
        Assert.Equal(string.Empty, LocalStackReview.Summary(Argv("setup claude-code")));
        Assert.Equal(string.Empty, LocalStackReview.Summary(Argv("setup local-observability a-verb-a-newer-cli-adds")));
        Assert.Equal(string.Empty, LocalStackReview.Summary(Array.Empty<string>()));
    }
}

/// <summary>
/// The wizard's last page for each verb: the exact command, its tier, whether it is destructive, what it warns about. Driven through the
/// real <see cref="WizardViewModel"/> on the shared UI thread; the Docker look is a fixed fake and the runner has no CLI.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LocalObservabilityReviewTests
{
    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        public Scene(DockerStatus? docker = null)
        {
            _services = AppServices.CreateIsolated(
                TestServices.IsolatedPaths(_temp.Path),
                claudeSettingsPath: _temp.File("claude-settings.json"),
                dockerProbe: new FixedDockerProbe(docker ?? new DockerStatus(DockerState.Ready, "Docker is running.", Array.Empty<string>())));
            _services.LocalStack.RefreshAsync().GetAwaiter().GetResult();
            ViewModel = UiThread.Run(() => new WizardViewModel(_services, WizardSamples.LocalObservability()));
        }

        public WizardViewModel ViewModel { get; }

        public WizardFieldViewModel Field(string id) => ViewModel.Steps.SelectMany(s => s.Fields).Single(f => f.Id == id);

        /// <summary>Picks the verb, sets the answers and walks to the review page.</summary>
        public CommandReview Review(string verb, params (string Id, string Value)[] answers) => UiThread.Run(() =>
        {
            Field("subcommand").Value = verb;
            foreach (var (id, value) in answers)
            {
                Field(id).Value = value;
            }

            for (var i = 0; i < 12 && !ViewModel.IsReview; i++)
            {
                ViewModel.Next();
            }

            Assert.True(ViewModel.IsReview, ViewModel.ValidationSummary);
            return ViewModel.CommandReview!;
        });

        public void Dispose()
        {
            UiThread.Run(() => ViewModel.Dispose());
            _services.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void The_wizard_opens_on_the_command_page_with_status_chosen()
    {
        using var scene = new Scene();

        UiThread.Run(() =>
        {
            var vm = scene.ViewModel;
            Assert.Equal("Command", vm.CurrentStep!.Title);
            Assert.Equal("status", scene.Field("subcommand").Value);
            Assert.False(vm.IsReview);
        });
    }

    [Theory]
    [InlineData("status", CommandTier.ReadOnly, "setup local-observability status", false)]
    [InlineData("logs", CommandTier.ReadOnly, "setup local-observability logs", false)]
    [InlineData("url", CommandTier.ReadOnly, "setup local-observability url", false)]
    [InlineData("env", CommandTier.ReadOnly, "setup local-observability env", false)]
    [InlineData("up", CommandTier.StateChanging, "setup local-observability up", true)] // writes config.yaml, so setup's callback restarts the gateway
    [InlineData("down", CommandTier.StateChanging, "setup local-observability down", false)]
    [InlineData("reset", CommandTier.Destructive, "setup local-observability reset --yes", false)]
    public void Each_verb_is_reviewed_with_its_exact_command_and_the_tier_its_source_earns(string verb, CommandTier tier, string command, bool restarts)
    {
        using var scene = new Scene();

        var review = scene.Review(verb);

        Assert.Equal(command.Split(' '), Assert.Single(review.Steps).Argv);
        Assert.Equal("defenseclaw " + command, review.CommandText);
        Assert.Equal(tier, review.Tier);
        Assert.Equal(restarts, review.RestartsGateway);
        Assert.Equal(restarts, review.Warnings.Any(w => w.Title == "Gateway restart"));

        UiThread.Run(() =>
        {
            // The line above "what you changed" says what the verb does, not "nothing was changed, running this re-applies it".
            Assert.Equal(LocalStackReview.Summary(command.Split(' ')), scene.ViewModel.ChangeSummary);
            Assert.DoesNotContain("re-applies", scene.ViewModel.ChangeSummary, StringComparison.Ordinal);
            Assert.Equal(tier == CommandTier.Destructive, scene.ViewModel.IsDestructive);
            Assert.Equal(tier == CommandTier.Destructive, scene.ViewModel.ShowDangerExecute);
            Assert.Equal(tier != CommandTier.Destructive, scene.ViewModel.ShowExecute);
            Assert.True(scene.ViewModel.CanExecute);
            Assert.False(scene.ViewModel.CanPreview);
            Assert.False(scene.ViewModel.HasPromptWarning, scene.ViewModel.PromptWarning);
        });
    }

    [Fact]
    public void A_read_is_labelled_read_only_and_a_reset_is_labelled_destructive_with_the_danger_button()
    {
        using var scene = new Scene();

        var status = scene.Review("status");
        Assert.Equal("Read-only", status.TierLabel);
        Assert.Equal("Run command", status.ConfirmLabel);

        var reset = scene.Review("reset");
        Assert.Equal("Destructive", reset.TierLabel);
        Assert.Equal("Run destructive command", reset.ConfirmLabel);
        Assert.True(reset.IsDestructive);
    }

    [Fact]
    public void Switching_the_verb_back_to_a_read_lowers_the_tier_with_it()
    {
        using var scene = new Scene();

        Assert.Equal(CommandTier.Destructive, scene.Review("reset").Tier);

        UiThread.Run(() =>
        {
            scene.ViewModel.Back();
            scene.Field("subcommand").Value = "url";
        });

        UiThread.Run(() =>
        {
            for (var i = 0; i < 12 && !scene.ViewModel.IsReview; i++)
            {
                scene.ViewModel.Next();
            }

            Assert.Equal(CommandTier.ReadOnly, scene.ViewModel.CommandReview!.Tier);
            Assert.False(scene.ViewModel.IsDestructive);
        });
    }

    [Fact]
    public void Up_carries_the_standard_gateway_restart_bar_and_the_refresh_bar_and_reset_says_what_it_deletes()
    {
        using var scene = new Scene();

        var up = scene.Review("up");
        Assert.Equal(new[] { "Gateway restart", LocalStackReview.RefreshesFilesTitle }, up.Warnings.Select(w => w.Title).ToArray());

        // The standard sentence every command that restarts the gateway must carry, with no toggle pointed at that this wizard does not have.
        var restart = up.Warnings[0];
        Assert.StartsWith(CommandReview.RestartSentence, restart.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-restart", restart.Message, StringComparison.Ordinal);

        var reset = scene.Review("reset");
        Assert.Equal(new[] { LocalStackReview.DeletesDataTitle, LocalStackReview.ExportingContinuesTitle }, reset.Warnings.Select(w => w.Title).ToArray());

        Assert.Equal(new[] { LocalStackReview.ExportingContinuesTitle }, scene.Review("down").Warnings.Select(w => w.Title).ToArray());
        Assert.Empty(scene.Review("status").Warnings);
    }

    [Fact]
    public void Down_that_disables_the_destination_restarts_the_gateway_and_no_longer_says_it_goes_on_exporting()
    {
        using var scene = new Scene();

        var review = scene.Review("down", ("down:disable-config", ToggleValues.On));

        Assert.Equal(new[] { "setup", "local-observability", "down", "--disable-config" }, Assert.Single(review.Steps).Argv);
        Assert.True(review.RestartsGateway);
        Assert.Equal(new[] { "Gateway restart" }, review.Warnings.Select(w => w.Title).ToArray());
        Assert.Equal(CommandTier.StateChanging, review.Tier);
    }

    [Fact]
    public void Up_that_leaves_config_yaml_alone_does_not_restart_the_gateway()
    {
        using var scene = new Scene();

        var review = scene.Review("up", ("up:no-config", ToggleValues.On));

        Assert.False(review.RestartsGateway);
        Assert.DoesNotContain(review.Warnings, w => w.Title == "Gateway restart");
        Assert.Contains(review.Warnings, w => w.Title == LocalStackReview.RefreshesFilesTitle);
    }

    [Fact]
    public void The_docker_looks_cautions_reach_the_review_of_a_verb_that_reaches_docker_only()
    {
        using var scene = new Scene(new DockerStatus(DockerState.Ready, "Docker is running.", new[] { "This looks like a per-user Docker Desktop install; the CLI's certified path requires a machine-wide install." }));

        var up = scene.Review("up");
        var bar = up.Warnings.Single(w => w.Title == LocalStackReview.DockerMayRefuseTitle);
        Assert.Contains("per-user Docker Desktop", bar.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(scene.Review("url").Warnings, w => w.Title == LocalStackReview.DockerMayRefuseTitle);
        Assert.Equal(CommandTier.ReadOnly, scene.ViewModel.CommandReview!.Tier);
    }

    [Fact]
    public void Turning_the_reset_confirmation_switch_off_says_the_command_will_prompt_and_still_names_the_verb()
    {
        using var scene = new Scene();

        var review = scene.Review("reset", ("reset:yes", ToggleValues.Off));

        Assert.Equal(new[] { "setup", "local-observability", "reset" }, Assert.Single(review.Steps).Argv);
        UiThread.Run(() => Assert.True(scene.ViewModel.HasPromptWarning));
        Assert.Equal(CommandTier.Destructive, review.Tier);
    }
}

/// <summary>
/// The line above what the operator changed, on the wizard's review page: unchanged for a setup wizard, true for a command that only reads, and
/// the verb's own sentence for the stack's lifecycle commands (which are not edits of a setting).
/// </summary>
public sealed class ReviewChangeSummaryTests
{
    private static readonly string[] Llm = { "setup", "llm" };

    [Fact]
    public void A_setup_wizard_says_what_it_always_said()
    {
        Assert.Equal(
            "Nothing was changed from the current configuration. Running this re-applies it as it stands.",
            WizardViewModel.SummaryOfChanges(Llm, hasChanges: false, CommandTier.StateChanging));
        Assert.Equal(
            "Changed from the current configuration:",
            WizardViewModel.SummaryOfChanges(Llm, hasChanges: true, CommandTier.StateChanging));
        Assert.Equal(
            "Changed from the current configuration:",
            WizardViewModel.SummaryOfChanges(new[] { "setup", "guardrail", "--mode", "action" }, hasChanges: true, CommandTier.Destructive));
    }

    [Fact]
    public void A_command_that_only_reads_does_not_claim_to_re_apply_or_change_a_configuration()
    {
        Assert.Equal("This command only reads. Running it changes nothing.", WizardViewModel.SummaryOfChanges(Llm, hasChanges: false, CommandTier.ReadOnly));
        Assert.Equal(
            "What you chose (this command only reads; it changes nothing):",
            WizardViewModel.SummaryOfChanges(Llm, hasChanges: true, CommandTier.ReadOnly));
    }

    [Fact]
    public void Stopping_a_command_that_only_reads_does_not_warn_of_half_applied_setup_and_every_other_stop_says_what_it_always_did()
    {
        Assert.Equal(
            "This ends the command and everything it started. It only reads, so nothing is left half-done.",
            WizardViewModel.StopMessage(closeAfterStop: false, onlyReads: true));
        Assert.Equal(
            "Closing this window stops the command and everything it started. It only reads, so nothing is left half-done.",
            WizardViewModel.StopMessage(closeAfterStop: true, onlyReads: true));

        Assert.Equal(
            "This ends the command and everything it started. It may leave setup half-applied — read the output and the Activity panel afterwards before running it again.",
            WizardViewModel.StopMessage(closeAfterStop: false, onlyReads: false));
        Assert.Equal(
            "Closing this window stops the command and everything it started. It may leave setup half-applied — read the output and the Activity panel afterwards before running it again.",
            WizardViewModel.StopMessage(closeAfterStop: true, onlyReads: false));
    }

    [Fact]
    public void The_result_line_after_a_confirmed_stop_says_the_same_and_is_unchanged_for_everything_else()
    {
        const string Cancelled = "cancelled — process tree killed";

        Assert.Equal(Cancelled + ". The command only reads, so nothing was left half-done.", WizardViewModel.CancelledMessage(Cancelled, preview: false, onlyReads: true));
        Assert.Equal(Cancelled + ". A preview writes nothing, so there is nothing to clean up.", WizardViewModel.CancelledMessage(Cancelled, preview: true, onlyReads: true));
        Assert.Equal(Cancelled + ". A preview writes nothing, so there is nothing to clean up.", WizardViewModel.CancelledMessage(Cancelled, preview: true, onlyReads: false));
        Assert.Equal(
            Cancelled + ". The command may have left setup half-applied — read the output above and the Activity panel before running it again.",
            WizardViewModel.CancelledMessage(Cancelled, preview: false, onlyReads: false));
    }

    [Fact]
    public void The_stacks_verbs_say_what_they_do_and_add_what_you_chose_only_when_something_was()
    {
        var reset = new[] { "setup", "local-observability", "reset", "--yes" };

        Assert.Equal("Stops the stack's containers and deletes their data volumes.", WizardViewModel.SummaryOfChanges(reset, hasChanges: false, CommandTier.Destructive));
        Assert.Equal(
            "Starts the stack's containers (Prometheus, Loki, Tempo, Grafana and the OpenTelemetry collector) on this machine. What you chose:",
            WizardViewModel.SummaryOfChanges(new[] { "setup", "local-observability", "up", "--timeout", "60" }, hasChanges: true, CommandTier.StateChanging));
        Assert.StartsWith("Only reads:", WizardViewModel.SummaryOfChanges(new[] { "setup", "local-observability", "status" }, hasChanges: false, CommandTier.ReadOnly), StringComparison.Ordinal);
    }
}

/// <summary>
/// The Setup hub's card for the stack: unavailable while Docker has not answered or says no (with the probe's reason, in the "not
/// available" group), launchable when Compose v2 is there and the engine answers, and looked at again on Refresh - never on a timer.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LocalObservabilityCardTests
{
    private static WizardDefinition Def(string target, string group, PlatformStatus status = PlatformStatus.NotApplicable, string description = "") => new()
    {
        Target = target,
        Title = target,
        Group = group,
        PlatformStatus = status,
        Description = description,
        IsDetailLoaded = true,
    };

    private static readonly DockerStatus Ready = new(DockerState.Ready, "Docker is running.", Array.Empty<string>());

    private static readonly DockerStatus NoDocker =
        new(DockerState.NotInstalled, "Docker was not found on this machine's PATH.", Array.Empty<string>());

    // ------------------------------------------------------------------ the card

    [Fact]
    public void A_card_that_needs_docker_follows_the_gate_and_moves_between_its_group_and_not_available()
    {
        var card = new WizardCardViewModel(Def("local-observability", WizardGroups.Observability, description: "Drive the bundled local observability stack."));
        var reason = "Docker was not found on this machine's PATH. The local observability stack runs in Docker Compose on this machine; once Docker is ready, refresh to check again.";

        Assert.True(card.ApplyDocker(new GateDecision(false, reason)));

        Assert.False(card.IsAvailable);
        Assert.Equal(reason, card.UnavailableReason);
        Assert.Equal(reason, card.TileBlurb);
        Assert.Equal(WizardGroups.Unavailable, card.Group);
        Assert.Contains("not available on this machine", card.AutomationName, StringComparison.Ordinal);
        Assert.Contains(reason, card.TileToolTip, StringComparison.Ordinal);
        Assert.False(card.ShowTileBadge);

        // The same answer again is nothing to regroup for.
        Assert.False(card.ApplyDocker(new GateDecision(false, reason)));

        Assert.True(card.ApplyDocker(GateDecision.Open));
        Assert.True(card.IsAvailable);
        Assert.Equal(string.Empty, card.UnavailableReason);
        Assert.Equal("Drive the bundled local observability stack.", card.TileBlurb);
        Assert.Equal(WizardGroups.Observability, card.Group);
        Assert.Equal("Configure local-observability", card.LaunchAutomationName);
    }

    [Fact]
    public void A_definition_that_lands_later_neither_forgets_the_gate_nor_overrides_a_reason_the_policy_gives()
    {
        var card = new WizardCardViewModel(Def("local-observability", WizardGroups.Observability));
        _ = card.ApplyDocker(new GateDecision(false, "Docker is not running."));

        // Phase two of the catalog replaces the definition wholesale.
        card.Apply(Def("local-observability", WizardGroups.Observability, description: "richer"));
        Assert.False(card.IsAvailable);
        Assert.Equal("Docker is not running.", card.UnavailableReason);

        // A CLI that itself calls it unsupported is not overruled by Docker being fine.
        _ = card.ApplyDocker(GateDecision.Open);
        card.Apply(Def("local-observability", WizardGroups.Observability, PlatformStatus.Unsupported));
        Assert.False(card.IsAvailable);
        Assert.Contains("unsupported on Windows", card.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_other_card_ignores_the_gate()
    {
        var connector = new WizardCardViewModel(Def("claude-code", WizardGroups.Connectors, PlatformStatus.Certified));
        var splunk = new WizardCardViewModel(Def("splunk", WizardGroups.Observability));

        Assert.False(connector.ApplyDocker(new GateDecision(false, "Docker is not running.")));
        Assert.False(splunk.ApplyDocker(new GateDecision(false, "Docker is not running.")));

        Assert.True(connector.IsAvailable);
        Assert.True(splunk.IsAvailable);
        Assert.Equal(WizardGroups.Connectors, connector.Group);
    }

    // ------------------------------------------------------------------ the hub

    private static readonly WizardDefinition[] Cards =
    {
        Def("claude-code", WizardGroups.Connectors, PlatformStatus.Certified, "Configure DefenseClaw hooks for Claude Code."),
        Def("local-observability", WizardGroups.Observability, description: "Drive the bundled local observability stack."),
        Def("webhook", WizardGroups.Observability, description: "Add a webhook."),
    };

    private sealed class Hub : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        public Hub()
        {
            Probe = new ManualDockerProbe();
            _services = AppServices.CreateIsolated(
                TestServices.IsolatedPaths(_temp.Path),
                claudeSettingsPath: _temp.File("claude-settings.json"),
                dockerProbe: Probe);
            ViewModel = UiThread.Run(() =>
            {
                var vm = new SetupPanelViewModel(_services);
                vm.ShowCards(Cards);
                return vm;
            });
        }

        public ManualDockerProbe Probe { get; }

        public SetupPanelViewModel ViewModel { get; }

        public AppServices Services => _services;

        public WizardCardViewModel Stack => ViewModel.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "local-observability");

        public string GroupOfStack => ViewModel.Groups.Single(g => g.Cards.Contains(Stack)).Name;

        public void Dispose()
        {
            UiThread.Run(() => ViewModel.SetActive(false));
            _services.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void Before_docker_has_answered_the_card_is_not_available_and_says_it_is_checking()
    {
        using var hub = new Hub();

        UiThread.Run(() =>
        {
            Assert.False(hub.Stack.IsAvailable);
            Assert.Equal(LocalStackAvailability.CheckingReason, hub.Stack.UnavailableReason);
            Assert.Equal(WizardGroups.Unavailable, hub.GroupOfStack);

            // Everything else on the page is where it was.
            Assert.True(hub.ViewModel.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "claude-code").IsAvailable);
        });
        Assert.Equal(0, hub.Probe.Calls);
    }

    [Fact]
    public async Task Coming_on_screen_looks_at_docker_once_and_a_yes_moves_the_card_into_observability()
    {
        using var hub = new Hub();

        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(Ready);

        UiThread.WaitFor(() => hub.Stack.IsAvailable, "the probe's yes");
        UiThread.Run(() =>
        {
            Assert.Equal(WizardGroups.Observability, hub.GroupOfStack);
            Assert.Equal(string.Empty, hub.Stack.UnavailableReason);
        });
        Assert.Equal(1, hub.Probe.Calls);
    }

    [Theory]
    [InlineData(DockerState.NotInstalled, "Docker was not found on this machine's PATH.")]
    [InlineData(DockerState.EngineDown, "Docker is installed but its engine is not running. Start Docker Desktop and wait for it to say it is running.")]
    [InlineData(DockerState.ComposeMissing, "Docker Compose v2 is not available (docker compose version failed). Install or enable the Compose plugin; Docker Desktop includes it.")]
    public async Task A_no_leaves_the_card_in_not_available_with_the_probes_own_reason(DockerState state, string summary)
    {
        using var hub = new Hub();

        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(new DockerStatus(state, summary, Array.Empty<string>()));

        UiThread.WaitFor(() => hub.Stack.UnavailableReason.StartsWith(summary, StringComparison.Ordinal), "the probe's no");
        UiThread.Run(() =>
        {
            Assert.False(hub.Stack.IsAvailable);
            Assert.Equal(WizardGroups.Unavailable, hub.GroupOfStack);
            Assert.Equal(hub.Stack.UnavailableReason, hub.Stack.TileBlurb);
        });
    }

    [Fact]
    public async Task Refresh_looks_again_and_the_card_follows_docker_in_both_directions()
    {
        using var hub = new Hub();
        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(NoDocker);
        UiThread.WaitFor(() => !hub.Stack.IsAvailable && !hub.Stack.UnavailableReason.Contains("Checking", StringComparison.Ordinal), "the first no");

        // "I started Docker Desktop" - the page's Refresh, not a timer.
        UiThread.Run(() => hub.ViewModel.RefreshCommand.Execute(null));
        await hub.Probe.WaitForCallsAsync(2);
        hub.Probe.Answer(Ready);
        UiThread.WaitFor(() => hub.Stack.IsAvailable, "the yes after Refresh");
        UiThread.Run(() => Assert.Equal(WizardGroups.Observability, hub.GroupOfStack));

        // ... and Docker stops again.
        UiThread.WaitFor(() => !hub.ViewModel.RefreshCommand.IsRunning, "the first Refresh to end");
        UiThread.Run(() => hub.ViewModel.RefreshCommand.Execute(null));
        await hub.Probe.WaitForCallsAsync(3);
        hub.Probe.Answer(NoDocker);
        UiThread.WaitFor(() => !hub.Stack.IsAvailable, "the no after the second Refresh");
        UiThread.Run(() => Assert.Equal(WizardGroups.Unavailable, hub.GroupOfStack));
    }

    [Fact]
    public async Task A_hub_that_is_off_screen_listens_to_nothing_and_catches_up_when_it_comes_back()
    {
        using var hub = new Hub();
        UiThread.Run(() => hub.ViewModel.SetActive(true));
        await hub.Probe.WaitForCallsAsync(1);
        UiThread.Run(() => hub.ViewModel.SetActive(false));

        // The answer lands while the hub is away: nothing is rebuilt for nobody.
        hub.Probe.Answer(Ready);
        await hub.Services.LocalStack.EnsureFreshAsync();
        Assert.True(hub.Services.LocalStack.Decision.IsAvailable);
        UiThread.Run(() => Assert.False(hub.Stack.IsAvailable));

        // Back on screen, the one catch-up pass puts the card where Docker says it belongs - with no new look, the answer being fresh.
        UiThread.Run(() => hub.ViewModel.SetActive(true));
        UiThread.Run(() =>
        {
            Assert.True(hub.Stack.IsAvailable);
            Assert.Equal(WizardGroups.Observability, hub.GroupOfStack);
        });
        Assert.Equal(1, hub.Probe.Calls);
    }

    [Fact]
    public async Task Cards_built_after_the_answer_started_from_it_instead_of_flashing_checking()
    {
        using var hub = new Hub();
        var look = hub.Services.LocalStack.RefreshAsync();
        await hub.Probe.WaitForCallsAsync(1);
        hub.Probe.Answer(Ready);
        await look;

        // What the catalog's re-read does: every card is built again.
        UiThread.Run(() =>
        {
            hub.ViewModel.ShowCards(Cards);

            Assert.True(hub.Stack.IsAvailable);
            Assert.Equal(WizardGroups.Observability, hub.GroupOfStack);
        });
    }
}
