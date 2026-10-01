using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>
/// The first-run plan (CUST-210). Every flag asserted here exists in <c>defenseclaw init --help</c> 0.8.10 and in the option list of
/// <c>defenseclaw/commands/cmd_init.py</c> (<c>--non-interactive --yes --json-summary --connector --profile --observe-all
/// --action-connectors --scanner-mode --with-judge/--no-judge --fail-mode --human-approval/--no-human-approval --hilt-min-severity
/// --start-gateway/--no-start-gateway --verify/--no-verify</c>); the follow-up is <c>setup &lt;alias&gt; --yes --mode --no-restart</c>
/// (<c>setup claude-code --help</c>). Nothing here runs a process.
/// </summary>
public sealed class InitPlanBuilderTests
{
    private static readonly string[] InitFlags =
    {
        "--non-interactive", "--yes", "--json-summary", "--connector", "--profile", "--observe-all", "--action-connectors", "--scanner-mode",
        "--with-judge", "--no-judge", "--fail-mode", "--human-approval", "--no-human-approval", "--hilt-min-severity",
        "--start-gateway", "--no-start-gateway", "--verify", "--no-verify",
    };

    private static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items, StringComparer.Ordinal);

    private static string[] Init(InitPlan plan) => plan.Steps[0].Argv.ToArray();

    [Fact]
    public void Nothing_detected_configures_the_fallback_connector_in_observe_and_leaves_the_gateway_to_its_own_step()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions { FallbackConnector = "codex" });

        Assert.Equal(
            new[]
            {
                "init", "--non-interactive", "--yes", "--json-summary", "--connector", "codex", "--profile", "observe",
                "--scanner-mode", "local", "--no-judge", "--fail-mode", "open", "--no-start-gateway", "--verify",
            },
            Init(plan));
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(new[] { "start" }, plan.Steps[1].Argv.ToArray());
        Assert.Equal("defenseclaw-gateway", plan.Steps[1].Executable);
        Assert.Equal(new[] { "codex" }, plan.Connectors.ToArray());
    }

    [Fact]
    public void Without_the_gateway_start_the_plan_is_the_init_step_alone()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions { StartGateway = false, Verify = false });

        var step = Assert.Single(plan.Steps);
        Assert.Equal("--no-verify", step.Argv[^1]);
        Assert.Contains("--no-start-gateway", step.Argv);
    }

    [Fact]
    public void Init_never_starts_the_gateway_itself_whatever_the_form_says()
    {
        foreach (var start in new[] { true, false })
        {
            var plan = InitPlanBuilder.Build(new InitPlanOptions { StartGateway = start });

            Assert.Contains("--no-start-gateway", Init(plan));
            Assert.DoesNotContain("--start-gateway", Init(plan));
        }
    }

    [Fact]
    public void Every_detected_connector_registered_uses_observe_all_and_names_the_ones_that_enforce()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions
        {
            Detected = new[] { "codex", "claudecode" },
            Registered = Set("codex", "claudecode"),
            Action = Set("claudecode"),
            Profile = "action",
            HumanApproval = true,
            HiltSeverity = "medium",
            FailMode = "closed",
            ScannerMode = "both",
            LlmJudge = true,
        });

        Assert.Equal(
            new[]
            {
                "init", "--non-interactive", "--yes", "--json-summary", "--observe-all", "--action-connectors", "claudecode", "--profile", "action",
                "--scanner-mode", "both", "--with-judge", "--fail-mode", "closed", "--human-approval", "--hilt-min-severity", "MEDIUM",
                "--no-start-gateway", "--verify",
            },
            Init(plan));
        Assert.Equal(new[] { "codex", "claudecode" }, plan.Connectors.ToArray());
    }

    [Fact]
    public void A_strict_subset_is_init_for_one_connector_then_additive_setup_per_extra_one_with_no_restart()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions
        {
            Detected = new[] { "codex", "claudecode", "cursor" },
            Registered = Set("codex", "claudecode", "cursor").Where(c => c != "cursor").ToHashSet(StringComparer.Ordinal),
            Action = Set("claudecode"),
            Profile = "action",
        });

        // Leads with the enforcing connector so init carries the global enforcement options.
        Assert.Equal(new[] { "--connector", "claudecode", "--profile", "action" }, Init(plan).SkipWhile(a => a != "--connector").Take(4).ToArray());
        Assert.Equal(new[] { "setup", "codex", "--yes", "--mode", "observe", "--no-restart" }, plan.Steps[1].Argv.ToArray());
        Assert.Equal("start", plan.Steps[2].Argv[0]);
        Assert.DoesNotContain("--observe-all", Init(plan));
        Assert.Equal(new[] { "claudecode", "codex" }, plan.Connectors.ToArray());
    }

    [Fact]
    public void The_follow_up_for_claude_code_uses_the_hyphenated_alias_the_cli_knows()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions
        {
            Detected = new[] { "codex", "claudecode", "hermes" },
            Registered = Set("codex", "claudecode"),
        });

        Assert.Equal(new[] { "setup", "claude-code", "--yes", "--mode", "observe", "--no-restart" }, plan.Steps[1].Argv.ToArray());
    }

    [Fact]
    public void Human_approval_flags_are_sent_only_for_the_action_profile()
    {
        var observe = InitPlanBuilder.Build(new InitPlanOptions { Profile = "observe", HumanApproval = true });
        var action = InitPlanBuilder.Build(new InitPlanOptions { Profile = "action", HumanApproval = false });

        Assert.DoesNotContain("--human-approval", Init(observe));
        Assert.DoesNotContain("--no-human-approval", Init(observe));
        Assert.DoesNotContain("--hilt-min-severity", Init(observe));
        Assert.Contains("--no-human-approval", Init(action));
        Assert.DoesNotContain("--hilt-min-severity", Init(action));
    }

    [Fact]
    public void An_unknown_severity_falls_back_to_high_rather_than_reaching_argv()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions { Profile = "action", HumanApproval = true, HiltSeverity = "&calc" });

        Assert.Equal("HIGH", Init(plan)[Array.IndexOf(Init(plan), "--hilt-min-severity") + 1]);
    }

    [Fact]
    public void A_proxy_connector_never_enters_the_plan()
    {
        var fallback = InitPlanBuilder.Build(new InitPlanOptions { FallbackConnector = "openclaw" });
        var detected = InitPlanBuilder.Build(new InitPlanOptions
        {
            Detected = new[] { "zeptoclaw", "codex" },
            Registered = Set("zeptoclaw", "codex"),
        });

        Assert.Equal("codex", Init(fallback)[Array.IndexOf(Init(fallback), "--connector") + 1]);
        Assert.Equal(new[] { "codex" }, detected.Connectors.ToArray());
        Assert.DoesNotContain(detected.Steps.SelectMany(s => s.Argv), a => a.Contains("zeptoclaw", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_registered_set_registers_everything_detected_rather_than_half_a_setup()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions { Detected = new[] { "codex", "hermes" }, Registered = Set() });

        Assert.Contains("--observe-all", Init(plan));
        Assert.Equal(new[] { "codex", "hermes" }, plan.Connectors.ToArray());
    }

    [Fact]
    public void Secrets_never_reach_argv_and_only_known_init_flags_are_used()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions
        {
            Detected = new[] { "codex", "claudecode" },
            Registered = Set("codex"),
            Profile = "action",
            Action = Set("codex"),
            HumanApproval = true,
            LlmJudge = true,
        });

        foreach (var arg in plan.Steps.SelectMany(s => s.Argv))
        {
            Assert.DoesNotContain("api-key", arg, StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(Init(plan).Where(a => a.StartsWith("--", StringComparison.Ordinal)), flag => Assert.Contains(flag, InitFlags));
    }

    [Fact]
    public void Every_step_is_state_changing_through_the_shared_review_tiers()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions { Detected = new[] { "codex", "hermes" }, Registered = Set("codex") });

        Assert.All(plan.Steps, step => Assert.Equal(CommandTier.StateChanging, DiscoverActionReview.EffectiveTier(step)));
    }

    [Fact]
    public void The_same_options_always_give_the_same_plan()
    {
        var options = new InitPlanOptions { Detected = new[] { "codex", "hermes" }, Registered = Set("codex", "hermes"), Profile = "action", Action = Set("hermes") };

        Assert.Equal(
            InitPlanBuilder.Build(options).Steps.Select(s => string.Join(' ', s.Argv)),
            InitPlanBuilder.Build(options).Steps.Select(s => string.Join(' ', s.Argv)));
    }

    // ---- exit 0 is not success ----

    private static CliInvocation Run(string stdout, int exit = 0)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, "init", "--json-summary");
        foreach (var line in stdout.Split('\n'))
        {
            InvocationFactory.Append(invocation, line.TrimEnd('\r'));
        }

        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private const string ReadyReport =
        "{\n  \"status\": \"ready\",\n  \"setup\": [{\"name\": \"Config\", \"status\": \"pass\", \"detail\": \"\", \"next_command\": \"\"}],\n" +
        "  \"readiness\": [],\n  \"next_commands\": []\n}";

    private const string FailedReport =
        "{\n  \"status\": \"needs_attention\",\n  \"setup\": [{\"name\": \"Config\", \"status\": \"fail\", \"detail\": \"schema v7\", \"next_command\": \"defenseclaw upgrade\"}],\n" +
        "  \"readiness\": [],\n  \"next_commands\": []\n}";

    [Fact]
    public void The_init_step_verifies_its_report_and_the_gateway_start_has_nothing_to_verify()
    {
        var plan = InitPlanBuilder.Build(new InitPlanOptions());

        Assert.NotNull(plan.Steps[0].Verify);
        Assert.True(plan.Steps[0].RetainFullOutput);
        Assert.Null(plan.Steps[1].Verify);
    }

    [Fact]
    public void A_ready_report_passes_the_check()
    {
        var verify = InitPlanBuilder.Build(new InitPlanOptions()).Steps[0].Verify!;

        Assert.Null(verify(Run(ReadyReport)));
    }

    [Fact]
    public void An_exit_zero_run_with_a_failed_report_is_reported_as_a_problem_naming_the_step()
    {
        var verify = InitPlanBuilder.Build(new InitPlanOptions()).Steps[0].Verify!;

        var problem = verify(Run(FailedReport));

        Assert.NotNull(problem);
        Assert.Contains("Config", problem, StringComparison.Ordinal);
        Assert.Contains("schema v7", problem, StringComparison.Ordinal);
        Assert.Contains("gateway was not started", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Setup complete.")]
    public void An_exit_zero_run_with_no_report_is_a_problem_too(string stdout)
    {
        var verify = InitPlanBuilder.Build(new InitPlanOptions()).Steps[0].Verify!;

        Assert.NotNull(verify(Run(stdout)));
    }
}
