using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-379: the sequence sentence on every multi-command review, the 1.0.4 per-user fail-mode rule in the hook connector wizard and in first run, the explained
/// private-network opt-in of <c>setup observability add</c>, and the curated scanner pages. Synthetic help screens only; nothing runs.
/// </summary>
public sealed class Cust379Tests
{
    private const string AddHelp = """
        Usage: defenseclaw setup observability add [OPTIONS] <preset>

          Configure a telemetry destination from a <preset>.

        Options:
          --name TEXT                   Destination name.
          --endpoint TEXT               Endpoint: OTLP host:port or URL.
          --protocol [grpc|http]        OTLP transport.
          --allow-private-networks      Allow an endpoint on this computer or a
                                        private network (for a collector you run
                                        yourself).
          --plaintext                   Send OTLP without TLS (for a collector you
                                        run yourself that has no TLS).
          --non-interactive             Skip prompts; use flags only.
          --help                        Show this message and exit.
        """;

    // ---------------------------------------------------------------- sequence sentence

    [Fact]
    public void A_review_of_several_commands_opens_its_summary_with_the_sequence_sentence_once()
    {
        var steps = new[]
        {
            new CommandReviewStep(new[] { "keys", "set", "EXAMPLE_KEY" }),
            new CommandReviewStep(new[] { "setup", "llm" }),
        };

        var plain = new CommandReview { Title = "Commands", Steps = steps };
        Assert.Equal(CommandReview.SequenceSentence, plain.DisplaySummary);

        var withSummary = new CommandReview { Title = "Commands", Summary = "The first stores the key.", Steps = steps };
        Assert.StartsWith(CommandReview.SequenceSentence + " The first stores", withSummary.DisplaySummary, StringComparison.Ordinal);

        var already = new CommandReview { Title = "Commands", Summary = CommandReview.SequenceSentence + " More.", Steps = steps };
        Assert.Equal(already.Summary, already.DisplaySummary);

        var single = new CommandReview { Title = "Command", Steps = steps.Take(1).ToArray() };
        Assert.Equal(string.Empty, single.DisplaySummary);
    }

    // ---------------------------------------------------------------- fail mode on per-user 1.0.4

    [Fact]
    public void A_hook_connector_wizard_drops_the_fail_mode_question_where_the_saved_value_is_ignored_and_says_what_decides()
    {
        var definition = WizardSamples.ClaudeCode();
        Assert.Contains(definition.AllFields, f => f.Flag == "--fail-mode");

        var changed = WizardFailModeRule.Apply(definition, savedValueIgnored: true);

        Assert.DoesNotContain(changed.AllFields, f => f.Flag == "--fail-mode");
        Assert.Contains(changed.AllFields, f => f.Flag == "--mode");
        var page = changed.Steps.Single(s => s.Subtitle.Contains(HookFailModeRule.StrictPackCommand, StringComparison.Ordinal));
        Assert.Contains("policy tier", page.Subtitle, StringComparison.Ordinal);

        // The command can no longer carry the flag.
        var values = WizardSamples.StartingValues(changed);
        Assert.DoesNotContain("--fail-mode", changed.BuildArgv(values));
    }

    [Fact]
    public void Older_runtimes_and_managed_installs_keep_the_fail_mode_question_as_it_was()
    {
        var definition = WizardSamples.ClaudeCode();

        Assert.Same(definition, WizardFailModeRule.Apply(definition, savedValueIgnored: false));
    }

    [Fact]
    public void First_run_on_a_per_user_1_0_4_host_leaves_fail_mode_off_the_command_and_offers_the_strict_pack_as_its_own_step()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions { OutageFollowsPolicy = true, StrictOutage = true, FailMode = "open" });

        Assert.DoesNotContain("--fail-mode", plan.Steps[0].Argv);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal(new[] { "guardrail", "use-pack", "strict" }, plan.Steps[1].Argv.ToArray());
        Assert.Equal(new[] { "start" }, plan.Steps[2].Argv.ToArray());
        Assert.Equal("defenseclaw-gateway", plan.Steps[2].Executable);
    }

    [Fact]
    public void Without_the_strict_choice_a_per_user_first_run_is_init_and_the_gateway_start_only()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions { OutageFollowsPolicy = true, FailMode = "open" });

        Assert.DoesNotContain("--fail-mode", plan.Steps[0].Argv);
        Assert.Equal(2, plan.Steps.Count);
        Assert.DoesNotContain(plan.Steps, s => s.Argv.Contains("use-pack"));
    }

    [Fact]
    public void An_older_runtime_still_gets_its_fail_mode_on_the_command()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions { FailMode = "closed", StrictOutage = true });

        var argv = plan.Steps[0].Argv.ToList();
        Assert.Equal("closed", argv[argv.IndexOf("--fail-mode") + 1]);
        Assert.DoesNotContain(plan.Steps, s => s.Argv.Contains("use-pack"));
    }

    // ---------------------------------------------------------------- observability add

    [Fact]
    public void Observability_add_offers_the_private_network_opt_in_as_an_explained_switch_that_starts_off()
    {
        var group = SetupHelpParser.Parse(WizardSamples.ObservabilityHelp);
        var add = SetupHelpParser.Parse(LineEndings.Normalize(AddHelp));
        var steps = WizardStepFactory.BuildGroup(group, new Dictionary<string, ParsedHelp> { ["add"] = add }, "observability");

        var fields = steps.SelectMany(s => s.Fields).ToArray();
        var allow = Assert.Single(fields, f => f.Flag == "--allow-private-networks");
        Assert.Equal("Allow a private or loopback endpoint", allow.Label);
        Assert.Contains("collector you run yourself", allow.Help, StringComparison.Ordinal);
        Assert.Equal(ToggleValues.Off, allow.DefaultValue);

        var plaintext = Assert.Single(fields, f => f.Flag == "--plaintext");
        Assert.Equal("Send without TLS", plaintext.Label);
    }

    [Fact]
    public void Another_group_keeps_the_words_the_cli_gave_its_flags()
    {
        var group = SetupHelpParser.Parse(WizardSamples.ObservabilityHelp);
        var add = SetupHelpParser.Parse(LineEndings.Normalize(AddHelp));
        var steps = WizardStepFactory.BuildGroup(group, new Dictionary<string, ParsedHelp> { ["add"] = add }, "webhook");

        var allow = steps.SelectMany(s => s.Fields).Single(f => f.Flag == "--allow-private-networks");
        Assert.NotEqual("Allow a private or loopback endpoint", allow.Label);
    }

    [Fact]
    public void The_review_warns_when_a_destination_is_allowed_on_a_private_network_and_says_when_it_is_also_plaintext()
    {
        var plain = WizardCautions.For(new[] { "setup", "observability", "add", "otlp", "--endpoint", "10.0.0.5:4317", "--allow-private-networks", "--non-interactive" });
        var bar = Assert.Single(plain, w => w.Title == WizardCautions.PrivateNetworkTitle);
        Assert.DoesNotContain("without TLS", bar.Message, StringComparison.Ordinal);

        var both = WizardCautions.For(new[] { "setup", "observability", "add", "otlp", "--allow-private-networks", "--plaintext" });
        Assert.Contains("without TLS", Assert.Single(both, w => w.Title == WizardCautions.PrivateNetworkTitle).Message, StringComparison.Ordinal);

        Assert.Empty(WizardCautions.For(new[] { "setup", "observability", "add", "otlp", "--endpoint", "collector.example.com:4317" }));

        // The value of another option that happens to read like the flag is not the flag.
        Assert.Empty(WizardCautions.For(new[] { "setup", "observability", "add", "otlp", "--name", "--allow-private-networks" }));
    }

    // ---------------------------------------------------------------- scanners

    [Fact]
    public void The_scanner_pages_name_the_1_0_x_policy_gates_and_provider_endpoints_only_when_the_cli_has_them()
    {
        const string skill = """
            Usage: defenseclaw setup skill-scanner [OPTIONS]

              Configure skill-scanner.

            Options:
              --use-llm / --no-use-llm        Enable (or disable) the LLM analyzer
              --use-osv / --no-use-osv        Enable (or disable) OSV dependency checks
              --llm-provider TEXT             Judge LLM provider
              --llm-base-url TEXT             Base URL for openai-compatible and vllm judges
              --policy [strict|balanced|permissive|low-noise|quiet]
                                              Scan policy preset (recommended: quiet)
              --fail-on-severity [CRITICAL|HIGH|MEDIUM|LOW|INFO]
                                              Findings at or above this severity block
              --review-queue-min [CRITICAL|HIGH|MEDIUM|LOW|INFO]
                                              Findings from this severity go to review
              --lenient / --no-lenient        Tolerate malformed skills (or fail them)
              --verify / --no-verify          Run connectivity checks after setup
              --non-interactive               Use flags instead of prompts
              --help                          Show this message and exit.
            """;

        var (steps, curated) = WizardStepFactory.Build("skill-scanner", SetupHelpParser.Parse(LineEndings.Normalize(skill)));

        Assert.True(curated);
        var policy = steps.Single(s => s.Id == "policy");
        Assert.Equal(
            new[] { "--policy", "--fail-on-severity", "--review-queue-min", "--lenient", "--verify", "--non-interactive" },
            policy.Fields.Select(f => f.Flag).ToArray());
        Assert.Contains(steps.Single(s => s.Id == "analyzers").Fields, f => f.Flag == "--use-osv");
        Assert.Contains(steps.Single(s => s.Id == "llm").Fields, f => f.Flag == "--llm-base-url");
        Assert.Contains(policy.Fields.First().Choices, c => c.Value == "quiet");

        // A 0.8.x screen without them: the pages simply do not name them.
        const string old = """
            Usage: defenseclaw setup skill-scanner [OPTIONS]

              Configure skill-scanner.

            Options:
              --use-llm                       Enable the LLM analyzer
              --policy [strict|balanced|permissive|none]
                                              Scan policy preset
              --help                          Show this message and exit.
            """;
        var (oldSteps, _) = WizardStepFactory.Build("skill-scanner", SetupHelpParser.Parse(LineEndings.Normalize(old)));
        Assert.DoesNotContain(oldSteps.SelectMany(s => s.Fields), f => f.Flag == "--fail-on-severity");
    }

    [Fact]
    public void The_mcp_scanner_has_a_page_for_the_cisco_ai_defense_api_when_the_cli_offers_it()
    {
        const string mcp = """
            Usage: defenseclaw setup mcp-scanner [OPTIONS]

              Configure mcp-scanner.

            Options:
              --analyzers TEXT                Comma-separated analyzer list
              --llm-provider TEXT             Judge LLM provider
              --api-endpoint TEXT             Cisco AI Defense API URL for the api analyzer
              --api-key-env TEXT              Env var holding the Cisco AI Defense API key
              --api-timeout-ms INTEGER RANGE  Cisco AI Defense request timeout in milliseconds
              --verify / --no-verify          Run connectivity checks after setup
              --non-interactive               Use flags instead of prompts
              --help                          Show this message and exit.
            """;

        var (steps, _) = WizardStepFactory.Build("mcp-scanner", SetupHelpParser.Parse(LineEndings.Normalize(mcp)));

        var api = steps.Single(s => s.Id == "api");
        Assert.Equal(new[] { "--api-endpoint", "--api-key-env", "--api-timeout-ms" }, api.Fields.Select(f => f.Flag).ToArray());
        Assert.Equal(WizardFieldKind.EnvVarName, api.Fields.Single(f => f.Flag == "--api-key-env").Kind);
    }
}
