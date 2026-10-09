using System.Diagnostics;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The Fix buttons: a review fix opens the shared review with the exact argv and tier and runs nothing until confirmed (these tests never
/// confirm); a terminal fix goes to the console launcher; a wizard fix goes to the injected wizard opener. Nothing starts a process.
/// </summary>
public sealed class ReadinessViewModelTests
{
    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _temp = new();
        public List<ProcessStartInfo> Launched { get; } = new();
        public List<string> Wizards { get; } = new();

        public Harness(string? configYaml = null)
        {
            Services = TestServices.Create(_temp, configYaml);
            var terminal = new CredentialTerminal(Services.Paths)
            {
                ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
                Launch = info =>
                {
                    Launched.Add(info);
                    return null;
                },
            };
            Review = new DiscoverActionReview(Services);
            Credentials = new CredentialsViewModel(Services, terminal);
            Readiness = new ReadinessViewModel(Services, Credentials, Review)
            {
                OpenWizard = target =>
                {
                    Wizards.Add(target);
                    return Task.CompletedTask;
                },
            };
        }

        public AppServices Services { get; }

        public DiscoverActionReview Review { get; }

        public CredentialsViewModel Credentials { get; }

        public ReadinessViewModel Readiness { get; }

        public ReadinessRowViewModel Row(string title) => Readiness.Rows.Single(r => r.Title == title);

        public void Dispose()
        {
            Services.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void Rows_are_built_from_the_loaded_config_and_the_credential_read()
    {
        using var h = new Harness("llm:\n  provider: anthropic\n  model: example-model\nguardrail:\n  enabled: true\n");
        h.Credentials.ApplyRows(new[] { new CredentialRow("EXAMPLE_KEY", "EXAMPLE_KEY", "F", "required", "unset", false, string.Empty) });

        h.Readiness.Rebuild();

        Assert.Equal("anthropic/example-model", h.Row("LLM Config").Detail);
        Assert.Equal(ReadinessStatus.Fail, h.Row("Required Credentials").Status);
        Assert.Equal("1 required credential(s) missing", h.Row("Required Credentials").Detail);
        Assert.Equal(ReadinessStatus.Warn, h.Row("Scanner Availability").Status);
        Assert.True(h.Readiness.AttentionCount >= 2);
        Assert.Equal(h.Readiness.Rows.Count(r => r.Status != ReadinessStatus.Pass), h.Readiness.AttentionCount);
    }

    [Fact]
    public void A_review_fix_opens_the_shared_review_with_the_exact_two_step_argv_and_runs_nothing()
    {
        using var h = new Harness();
        h.Readiness.Rebuild();
        var row = h.Row("Scanner Availability");
        Assert.True(row.HasFix);

        row.FixCommand.Execute(null);

        Assert.True(h.Review.IsOpen);
        Assert.False(h.Review.IsRunning);
        var review = h.Review.CommandReview!;
        Assert.Equal(
            new[] { "doctor --fix --dry-run", "doctor --fix --yes" },
            review.Steps.Select(s => string.Join(' ', s.Argv)));
        Assert.All(review.Steps, s => Assert.Equal("defenseclaw", s.Executable));
        Assert.All(review.Steps, s => Assert.Equal(CommandTier.StateChanging, s.Tier)); // a review never shows read-only
        Assert.Empty(h.Services.Cli.Activity);
    }

    [Fact]
    public void The_gateway_fix_is_a_reviewed_gateway_command_with_the_restart_warning()
    {
        using var h = new Harness();
        _ = h.Services.RestartQueue.Queue("A guardrail change was saved without restarting the gateway.");
        h.Readiness.Rebuild();

        var row = h.Row("Restart Pending");
        Assert.Equal(ReadinessStatus.Warn, row.Status);
        row.FixCommand.Execute(null);

        var review = h.Review.CommandReview!;
        var step = Assert.Single(review.Steps);
        Assert.Equal("defenseclaw-gateway", step.Executable);
        Assert.Equal(new[] { "restart" }, step.Argv);
        Assert.Equal(CommandTier.StateChanging, step.Tier);
        Assert.True(review.RestartsGateway);
        Assert.Contains(review.Warnings, w => w.Title == "Gateway restart");
        Assert.Empty(h.Services.Cli.Activity);
    }

    [Fact]
    public void The_restart_pending_row_is_the_app_wide_queue_and_follows_it_both_ways()
    {
        using var h = new Harness();
        h.Readiness.Rebuild();
        Assert.Equal(ReadinessStatus.Pass, h.Row("Restart Pending").Status);
        Assert.Equal("No queued restart.", h.Row("Restart Pending").Detail);

        _ = h.Services.RestartQueue.Queue("pending");
        _ = h.Services.RestartQueue.Queue("config.yaml saved in the config editor (llm)");
        h.Readiness.Rebuild();

        // The TUI's row: the queue's reasons, joined, as its detail.
        Assert.Equal(ReadinessStatus.Warn, h.Row("Restart Pending").Status);
        Assert.Equal("pending; config.yaml saved in the config editor (llm)", h.Row("Restart Pending").Detail);

        Assert.True(h.Services.RestartQueue.Clear());
        h.Readiness.Rebuild();

        Assert.Equal(ReadinessStatus.Pass, h.Row("Restart Pending").Status);
        Assert.Equal("No queued restart.", h.Row("Restart Pending").Detail);
    }

    [Fact]
    public async Task The_required_credentials_fix_opens_a_console_and_does_not_open_a_review()
    {
        using var h = new Harness();
        h.Credentials.ApplyRows(new[] { new CredentialRow("EXAMPLE_KEY", "EXAMPLE_KEY", "F", "required", "unset", false, string.Empty) });
        h.Readiness.Rebuild();

        h.Row("Required Credentials").FixCommand.Execute(null);
        await Task.Yield();

        Assert.False(h.Review.IsOpen);
        var info = Assert.Single(h.Launched);
        Assert.Contains("keys fill-missing --yes", info.Arguments, StringComparison.Ordinal);
        Assert.Contains("fill-missing", Assert.Single(h.Services.Cli.Activity).Argv);
    }

    [Fact]
    public void A_wizard_fix_opens_the_matching_setup_wizard_and_nothing_else()
    {
        using var h = new Harness();
        h.Readiness.Rebuild();

        h.Row("LLM Config").FixCommand.Execute(null);
        h.Row("Guardrail").FixCommand.Execute(null);

        Assert.Equal(new[] { "llm", "guardrail" }, h.Wizards);
        Assert.False(h.Review.IsOpen);
        Assert.Empty(h.Launched);
    }

    [Fact]
    public void A_row_without_a_fix_has_a_disabled_button()
    {
        using var h = new Harness();
        h.Readiness.Rebuild();

        var row = h.Row("Observability v8");

        Assert.False(row.HasFix);
        Assert.False(row.FixCommand.CanExecute(null));
    }

    [Fact]
    public void The_tooltip_and_the_accessible_name_say_what_the_fix_does()
    {
        using var h = new Harness();
        h.Readiness.Rebuild();

        var scanner = h.Row("Scanner Availability");
        Assert.Equal("Fix Scanner Availability", scanner.FixAutomationName);
        Assert.Contains("defenseclaw doctor --fix --dry-run", scanner.FixToolTip, StringComparison.Ordinal);
        Assert.StartsWith("Review, then run", scanner.FixToolTip, StringComparison.Ordinal);
        Assert.Contains("setup wizard", h.Row("LLM Config").FixToolTip, StringComparison.Ordinal);
    }
}
