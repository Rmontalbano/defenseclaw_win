using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// Removing the Claude Code connector leaves the Cowork plugin in the Claude account unprotected (runtime 1.0.9, CUST-451): the CLI says so only after
/// <c>setup remove claudecode</c> ran (cmd_setup.py@1.0.9:14786-14792), and the app says it in the review first. Also the uninstall / reset sentence by
/// release (cmd_uninstall.py@1.0.9:509-512). Covers every review path that can hold a <c>setup remove</c>: the Overview and Discover reviews
/// (<see cref="DiscoverActionReview"/>), the Setup wizard's review page, and the Activity rerun. Synthetic data folders only; nothing is started.
/// </summary>
public sealed class CoworkConnectorRemovalReviewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    /// <summary>Services for a runtime of <paramref name="version"/>; the plugin state file exists in its data folder when <paramref name="pluginState"/>.</summary>
    private async Task<AppServices> ServicesAsync(string version, bool pluginState = true)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, "guardrail:\n  enabled: true\n", Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        if (pluginState)
        {
            var folder = CoworkProtectionFacts.StateFolder(_services.Paths.DataDirectory);
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(CoworkProtectionFacts.PluginStatePath(_services.Paths.DataDirectory), "{\"version\":\"1.0.20261101120000\"}");
        }

        return _services;
    }

    private static IReadOnlyList<CommandReviewWarning> Bars(AppServices services, params string[] argv) =>
        CoworkUninstallReview.Warnings(argv, services.Runtime.Capabilities, services.Paths.DataDirectory);

    // ------------------------------------------------------------------ the argv rule

    [Theory]
    [InlineData("claudecode")]
    [InlineData("claude-code")]
    [InlineData("claude_code")]
    [InlineData("claude")]
    [InlineData("ClaudeCode")]
    [InlineData("CLAUDE-CODE")]
    [InlineData(" claude ")]
    public async Task Setup_remove_of_claude_code_under_any_name_carries_the_bar(string name)
    {
        var services = await ServicesAsync("1.0.9");

        var bar = Assert.Single(Bars(services, "setup", "remove", name, "--yes"));

        Assert.Equal("Cowork stops being protected", bar.Title);
        Assert.Equal("Cowork is no longer protected after this; remove the DefenseClaw plugin in Claude (Customize > Plugins).", bar.Message);
    }

    [Theory]
    [InlineData("setup", "remove", "--yes", "claude-code")]
    [InlineData("setup", "remove", "--no-restart", "--force", "claudecode")]
    [InlineData("SETUP", "REMOVE", "claude", "--non-interactive")]
    [InlineData("setup", "remove", "claudecode")]
    public async Task Options_before_or_after_the_name_do_not_matter(params string[] argv)
    {
        var services = await ServicesAsync("1.0.9");

        _ = Assert.Single(Bars(services, argv));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("hermes")]
    [InlineData("claudecode-extra")]
    public async Task Removing_another_connector_carries_no_bar(string name)
    {
        var services = await ServicesAsync("1.0.9");

        Assert.Empty(Bars(services, "setup", "remove", name, "--yes"));
    }

    [Fact]
    public async Task Without_a_connector_or_with_the_name_only_in_an_option_value_there_is_no_bar()
    {
        var services = await ServicesAsync("1.0.9");

        Assert.Empty(Bars(services, "setup", "remove"));
        Assert.Empty(Bars(services, "setup", "remove", "--yes"));

        // The first argument that is not an option is the connector: codex here, whatever follows.
        Assert.Empty(Bars(services, "setup", "remove", "codex", "claudecode"));

        // Other verbs and other nouns are not this command.
        Assert.Empty(Bars(services, "setup", "claudecode"));
        Assert.Empty(Bars(services, "setup", "observability", "remove", "claudecode"));
        Assert.Empty(Bars(services, "remove", "claudecode"));
    }

    [Fact]
    public async Task No_bar_without_the_plugin_state_file()
    {
        var services = await ServicesAsync("1.0.9", pluginState: false);

        Assert.Empty(Bars(services, "setup", "remove", "claudecode", "--yes"));
    }

    [Theory]
    [InlineData("1.0.7")]
    [InlineData("1.0.0")]
    public async Task A_runtime_without_cowork_protection_gets_no_bar_even_with_the_state_file(string version)
    {
        var services = await ServicesAsync(version);

        Assert.Empty(Bars(services, "setup", "remove", "claudecode", "--yes"));
    }

    [Fact]
    public async Task From_1_0_8_the_claude_code_removal_bar_applies_because_cowork_has_run_the_claude_code_hook_since_then()
    {
        var services = await ServicesAsync("1.0.8");

        _ = Assert.Single(Bars(services, "setup", "remove", "claudecode", "--yes"));
    }

    [Fact]
    public async Task On_1_0_8_the_bar_says_only_what_1_0_8_shows_and_not_the_1_0_9_line_or_the_blocked_claim()
    {
        var services = await ServicesAsync("1.0.8");

        var bar = Assert.Single(Bars(services, "setup", "remove", "claudecode", "--yes"));

        Assert.Equal("Cowork stops being protected", bar.Title);
        Assert.Equal(CoworkUninstallReview.ConnectorMessage108, bar.Message);
        Assert.StartsWith("If the removal succeeds, Cowork is no longer protected", bar.Message, StringComparison.Ordinal);
        Assert.Contains("reports it as not protected", bar.Message, StringComparison.Ordinal);
        Assert.Contains("Customize > Plugins, then the ... menu > Remove", bar.Message, StringComparison.Ordinal);
        Assert.NotEqual(CoworkUninstallReview.ConnectorMessage, bar.Message);
        Assert.DoesNotContain("blocked", bar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runs after", bar.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task On_1_0_9_the_bar_is_the_clis_own_line_and_the_message_follows_the_capability()
    {
        var current = await ServicesAsync("1.0.9", pluginState: false);
        Assert.Equal(CoworkUninstallReview.ConnectorMessage, CoworkUninstallReview.ConnectorMessageFor(current.Runtime.Capabilities));

        var old = await ServicesAsync("1.0.8", pluginState: false);
        Assert.Equal(CoworkUninstallReview.ConnectorMessage108, CoworkUninstallReview.ConnectorMessageFor(old.Runtime.Capabilities));
    }

    // ------------------------------------------------------------------ the uninstall / reset sentence by release

    [Fact]
    public async Task Uninstall_says_the_1_0_8_sentence_on_a_1_0_8_runtime_and_the_1_0_9_sentence_on_1_0_9()
    {
        var old = await ServicesAsync("1.0.8");
        var oldBar = Assert.Single(Bars(old, "uninstall", "--yes"));
        Assert.Equal(CoworkProtectionFacts.RemovePluginStep, oldBar.Message);
        Assert.EndsWith("Until then Cowork tool calls are blocked.", oldBar.Message, StringComparison.Ordinal);

        var current = await ServicesAsync("1.0.9");
        foreach (var verb in new[] { "uninstall", "reset" })
        {
            var bar = Assert.Single(Bars(current, verb, "--yes"));
            Assert.Equal("Remove the Cowork plugin in Claude Desktop", bar.Title);
            Assert.Equal(CoworkProtectionFacts.RemovePluginStepRunsAfterRemoval, bar.Message);
        }
    }

    [Fact]
    public void The_two_sentences_are_the_clis_word_for_word()
    {
        Assert.Equal(
            "DefenseClaw cannot remove its Cowork plugin from your Claude account: remove it in Claude Desktop (Customize > Plugins, then the ... menu > Remove). " +
            "Until then Cowork tool calls are blocked.",
            CoworkProtectionFacts.RemovePluginStep);
        Assert.Equal(
            "DefenseClaw cannot remove its Cowork plugin from your Claude account: remove it in Claude Desktop (Customize > Plugins, then the ... menu > Remove). " +
            "A plugin built with 1.0.9 lets Cowork calls run after the data folder is removed; older plugins may block.",
            CoworkProtectionFacts.RemovePluginStepRunsAfterRemoval);
        Assert.Equal(CoworkProtectionFacts.RemovePluginStep, CoworkProtectionFacts.RemovePluginStepFor(false));
        Assert.Equal(CoworkProtectionFacts.RemovePluginStepRunsAfterRemoval, CoworkProtectionFacts.RemovePluginStepFor(true));
    }

    [Theory]
    [InlineData("1.0.8", false)]
    [InlineData("1.0.9", true)]
    public async Task The_sentence_for_capabilities_follows_the_release(string version, bool runsAfterRemoval)
    {
        var services = await ServicesAsync(version, pluginState: false);

        Assert.Equal(CoworkProtectionFacts.RemovePluginStepFor(runsAfterRemoval), CoworkProtectionFacts.RemovePluginStepFor(services.Runtime.Capabilities));
    }

    // ------------------------------------------------------------------ the owner-machine shape

    /// <summary>1.0.9, one connector (codex), Claude Desktop installed, Cowork never set up: no plugin state, so no Cowork bar on any removal.</summary>
    [Fact]
    public async Task On_a_codex_only_machine_with_cowork_never_set_up_no_review_carries_a_cowork_bar()
    {
        var services = await ServicesAsync("1.0.9", pluginState: false);

        Assert.Empty(Bars(services, "setup", "remove", "codex", "--yes"));
        Assert.Empty(Bars(services, "setup", "remove", "claudecode", "--yes"));
        Assert.Empty(Bars(services, "uninstall"));
    }

    // ------------------------------------------------------------------ every review path

    [Fact]
    public async Task The_discover_review_the_overview_remove_connector_uses_carries_the_bar_for_claude_code_only()
    {
        var services = await ServicesAsync("1.0.9");
        var review = new DiscoverActionReview(services);

        review.Open("Remove the Claude Code connector?", "Takes it out.", new[] { new DiscoverStep(new[] { "setup", "remove", "claude-code", "--yes" }, "Remove it.") });
        Assert.Contains(review.CommandReview!.Warnings, w => w.Title == CoworkUninstallReview.ConnectorTitle);
        review.DismissCommand.Execute(null);

        review.Open("Remove the Codex connector?", "Takes it out.", new[] { new DiscoverStep(new[] { "setup", "remove", "codex", "--yes" }, "Remove it.") });
        Assert.DoesNotContain(review.CommandReview!.Warnings, w => w.Title == CoworkUninstallReview.ConnectorTitle);
    }

    [Fact]
    public async Task The_discover_review_does_not_apply_the_bar_to_a_step_on_the_gateway_tool()
    {
        var services = await ServicesAsync("1.0.9");
        var review = new DiscoverActionReview(services);

        review.Open("Example", "Example.", new[] { new DiscoverStep(new[] { "setup", "remove", "claudecode" }, "Example.", Executable: "defenseclaw-gateway") });

        Assert.DoesNotContain(review.CommandReview!.Warnings, w => w.Title == CoworkUninstallReview.ConnectorTitle);
    }

    [Fact]
    public async Task The_wizard_review_page_carries_the_bar_for_a_setup_remove_of_claude_code()
    {
        var services = await ServicesAsync("1.0.9");

        foreach (var (connector, expected) in new[] { ("claudecode", true), ("codex", false) })
        {
            StaThread.Run(() =>
            {
                var definition = new WizardDefinition
                {
                    Target = "remove",
                    Title = "Remove a connector",
                    Group = WizardGroups.Connectors,
                    Steps = new[]
                    {
                        new WizardStep
                        {
                            Id = "main",
                            Title = "Connector",
                            Fields = new[] { new WizardField { Id = "connector", Label = "Connector", Kind = WizardFieldKind.Text } },
                        },
                    },
                    FinalArgvBuilder = (_, _) => new[] { "setup", "remove", connector, "--yes" },
                };
                using var vm = new WizardViewModel(services, definition);
                while (!vm.IsReview)
                {
                    vm.Next();
                }

                Assert.Equal(expected, vm.CommandReview!.Warnings.Any(w => w.Title == CoworkUninstallReview.ConnectorTitle));
            });
        }
    }

    [Fact]
    public async Task The_activity_rerun_review_carries_the_bar_for_a_setup_remove_of_claude_code()
    {
        var services = await ServicesAsync("1.0.9");
        var rerun = new CommandRerun(services);

        var claude = InvocationFactory.CreateFor("defenseclaw", new[] { "setup", "remove", "claudecode", "--yes" });
        InvocationFactory.Finish(claude, 0);
        var codex = InvocationFactory.CreateFor("defenseclaw", new[] { "setup", "remove", "codex", "--yes" });
        InvocationFactory.Finish(codex, 0);

        Assert.Contains(rerun.ReviewFor(claude, CommandTier.StateChanging).Warnings, w => w.Title == CoworkUninstallReview.ConnectorTitle);
        Assert.DoesNotContain(rerun.ReviewFor(codex, CommandTier.StateChanging).Warnings, w => w.Title == CoworkUninstallReview.ConnectorTitle);
    }
}
