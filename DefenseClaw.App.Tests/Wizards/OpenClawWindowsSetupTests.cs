using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// OpenClaw on native Windows (DefenseClaw 1.0.10, CUST-456), synthetic throughout. Before 1.0.10 the CLI lists OpenClaw as "unsupported on windows" and the app
/// shows it in "not available"; from 1.0.10 the CLI's help carries no such line, so the card reads as certified and launchable, opens on what has to be in place
/// first, and the Gateway card, the palette's hidden-command rule and the config editor's connector pickers stop saying OpenClaw is unsupported.
/// The help screens are written from <c>cmd_setup.py</c> in Click's layout (1.0.9: the platform status paragraph; 1.0.10: none).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OpenClawWindowsSetupTests
{
    private const string RuntimeHelp = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeCapabilities Runtime(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", RuntimeHelp, RuntimeHelp, null, null, null, null, null),
            "test",
            "fp",
            DateTimeOffset.UnixEpoch).Capabilities;

    private static readonly string TopHelp1010 = LineEndings.Normalize("""
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          claude-code  Configure DefenseClaw hooks for Claude Code.
          codex        Configure DefenseClaw hooks for Codex.
          openclaw     Configure OpenClaw guardrail setup.
          zeptoclaw    ZeptoClaw: unsupported on windows.
        """);

    private static readonly string TopHelp109 = LineEndings.Normalize("""
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          claude-code  Configure DefenseClaw hooks for Claude Code.
          codex        Configure DefenseClaw hooks for Codex.
          openclaw     OpenClaw: unsupported on windows.
          zeptoclaw    ZeptoClaw: unsupported on windows.
        """);

    /// <summary>The options both versions share, as Click lays them out (the alias in <c>_make_guardrail_connector_setup_command</c>).</summary>
    private const string Options = """


        Options:
          -y, --yes                       Skip confirmation prompt.
          --non-interactive, --accept-defaults
                                          Alias for --yes.
          --mode [observe|action]         Guardrail mode.
          --scanner-mode [local|remote|both]
                                          Scanner mode.
          --port INTEGER                  Guardrail proxy port.
          --block-message TEXT            Custom block message.
          --rule-pack [default|strict|permissive]
                                          Guardrail rule-pack profile.
          --judge-model TEXT              LLM judge model.
          --human-approval / --no-human-approval
                                          Enable or disable human approval.
          --restart / --no-restart        Restart gateway after setup.
                                          [default: restart]
          --replace                       Switch to OpenClaw: remove every configured
                                          hook connector (it cannot run next to
                                          them). Asks first unless --yes is given.
          --help                          Show this message and exit.
        """;

    /// <summary>1.0.10 <c>setup openclaw --help</c>: the description, and no "Platform status on windows" paragraph (OpenClaw is supported there).</summary>
    private static readonly string OpenClawHelp1010 = LineEndings.Normalize("""
        Usage: defenseclaw setup openclaw [OPTIONS]

          Configure DefenseClaw guardrail for OpenClaw.

          Configures the proxy-backed connector selection, then runs the same
          backend as `defenseclaw setup guardrail --connector ...`.
        """ + Options);

    /// <summary>1.0.9 <c>setup openclaw --help</c>: the same, plus the platform status paragraph the CLI adds for a connector that is not supported.</summary>
    private static readonly string OpenClawHelp109 = LineEndings.Normalize("""
        Usage: defenseclaw setup openclaw [OPTIONS]

          Configure DefenseClaw guardrail for OpenClaw.

          Configures the proxy-backed connector selection, then runs the same
          backend as `defenseclaw setup guardrail --connector ...`.

          Platform status on windows: unsupported — DefenseClaw on Windows is
          hook-only; OpenClaw integration requires the guardrail proxy.
        """ + Options);

    // ------------------------------------------------------------------ the CLI's own wording: certified on 1.0.10, unsupported on 1.0.9

    [Fact]
    public void On_1_0_10_the_openclaw_help_has_no_platform_line_so_the_card_is_certified_offered_and_kept_by_Hide_uncertified()
    {
        var summary = SetupHelpParser.Parse(TopHelp1010).Commands.Single(c => c.Name == "openclaw").Summary;
        Assert.Equal("Configure OpenClaw guardrail setup.", summary);
        Assert.Equal(PlatformStatus.Unknown, SetupHelpParser.StatusFromSummary(summary));

        var parsed = SetupHelpParser.Parse(OpenClawHelp1010, commandDepth: 1);

        Assert.True(SetupHelpParser.IsConnectorShaped(parsed.Options));
        Assert.Equal(PlatformStatus.Certified, parsed.PlatformStatus);
        Assert.Equal(string.Empty, parsed.PlatformNote);
        Assert.Null(WizardWindowsPolicy.UnavailableReason("openclaw", parsed.PlatformStatus));
        Assert.True(SetupPanelViewModel.PassesCertificationFilter(parsed.PlatformStatus));

        // ZeptoClaw is still unsupported there.
        Assert.Equal(PlatformStatus.Unsupported, SetupHelpParser.StatusFromSummary(
            SetupHelpParser.Parse(TopHelp1010).Commands.Single(c => c.Name == "zeptoclaw").Summary));
    }

    [Fact]
    public void On_1_0_9_the_openclaw_help_says_unsupported_and_the_card_is_unavailable_and_hidden_by_Hide_uncertified()
    {
        var summary = SetupHelpParser.Parse(TopHelp109).Commands.Single(c => c.Name == "openclaw").Summary;
        Assert.Equal("OpenClaw: unsupported on windows.", summary);
        Assert.Equal(PlatformStatus.Unsupported, SetupHelpParser.StatusFromSummary(summary));

        var parsed = SetupHelpParser.Parse(OpenClawHelp109, commandDepth: 1);

        Assert.Equal(PlatformStatus.Unsupported, parsed.PlatformStatus);
        Assert.Contains("hook-only", parsed.PlatformNote, StringComparison.Ordinal);
        Assert.NotNull(WizardWindowsPolicy.UnavailableReason("openclaw", parsed.PlatformStatus));
        Assert.False(SetupPanelViewModel.PassesCertificationFilter(parsed.PlatformStatus));
    }

    // ------------------------------------------------------------------ the Gateway card's reason

    [Fact]
    public void The_Gateway_card_reason_follows_the_runtime_and_the_1_0_9_words_are_unchanged()
    {
        const string before = "OpenClaw is not supported on Windows, so there is no fleet gateway to configure.";

        Assert.Equal(before, WizardWindowsPolicy.UnavailableReason("gateway", PlatformStatus.NotApplicable));
        Assert.Equal(before, WizardWindowsPolicy.UnavailableReason("gateway", PlatformStatus.NotApplicable, null));
        Assert.Equal(before, WizardWindowsPolicy.UnavailableReason("gateway", PlatformStatus.NotApplicable, RuntimeCapabilities.Unknown));
        Assert.Equal(before, WizardWindowsPolicy.UnavailableReason("gateway", PlatformStatus.NotApplicable, Runtime("1.0.9")));

        var now = WizardWindowsPolicy.UnavailableReason("gateway", PlatformStatus.NotApplicable, Runtime("1.0.10"));

        Assert.Equal("On Windows, setup openclaw sets this uplink. Set up OpenClaw instead.", now);
        Assert.DoesNotContain("unsupported", now, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_Setup_hub_card_for_Gateway_takes_the_runtimes_reason_and_stays_unavailable()
    {
        var definition = new WizardDefinition
        {
            Target = "gateway",
            Title = "Gateway",
            Group = WizardGroups.Other,
            Description = "Configure the OpenClaw gateway.",
            PlatformStatus = PlatformStatus.NotApplicable,
            IsDetailLoaded = true,
        };

        var old = new WizardCardViewModel(definition, null, () => Runtime("1.0.9"));
        var current = new WizardCardViewModel(definition, null, () => Runtime("1.0.10"));
        var none = new WizardCardViewModel(definition);

        Assert.Contains("OpenClaw is not supported on Windows", old.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("OpenClaw is not supported on Windows", none.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("Set up OpenClaw instead", current.UnavailableReason, StringComparison.Ordinal);
        Assert.False(current.IsAvailable);
        Assert.False(old.IsAvailable);
    }

    // ------------------------------------------------------------------ the palette's hidden-command rule

    [Fact]
    public void The_openclaw_word_hides_a_command_before_1_0_10_and_not_from_it()
    {
        var setupOpenClaw = new[] { "setup", "openclaw" };

        Assert.True(WizardWindowsPolicy.HidesCommand(setupOpenClaw, string.Empty));
        Assert.True(WizardWindowsPolicy.HidesCommand(setupOpenClaw, string.Empty, RuntimeCapabilities.Unknown));
        Assert.True(WizardWindowsPolicy.HidesCommand(setupOpenClaw, string.Empty, Runtime("1.0.9")));
        Assert.False(WizardWindowsPolicy.HidesCommand(setupOpenClaw, string.Empty, Runtime("1.0.10")));
        Assert.False(WizardWindowsPolicy.HidesCommand(new[] { "doctor" }, "Checks OpenClaw too.", Runtime("1.0.10")));

        // What stays hidden on every runtime: ZeptoClaw, sandboxes, Docker, and the Gateway setup card.
        foreach (var version in new[] { "1.0.9", "1.0.10" })
        {
            var caps = Runtime(version);
            Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "setup", "zeptoclaw" }, string.Empty, caps));
            Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "sandbox", "list" }, string.Empty, caps));
            Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "tunnel" }, "Needs Docker to run the stack.", caps));
            Assert.True(WizardWindowsPolicy.HidesCommand(new[] { "setup", "gateway" }, string.Empty, caps));
        }
    }

    // ------------------------------------------------------------------ the config editor's connector pickers

    [Fact]
    public void The_config_editors_Windows_connector_picker_offers_openclaw_from_1_0_10_and_not_before()
    {
        foreach (var extended in new[] { false, true })
        {
            var before = ConfigFieldCatalog.SupportedConnectors(new ConfigChoiceProfile("windows", extended));
            var now = ConfigFieldCatalog.SupportedConnectors(new ConfigChoiceProfile("windows", extended, OpenClawNativeWindows: true));

            Assert.DoesNotContain("openclaw", before);
            Assert.Contains("openclaw", now);
            Assert.DoesNotContain("zeptoclaw", now);
            Assert.Equal(before.Concat(new[] { "openclaw" }).Order(StringComparer.Ordinal), now.Order(StringComparer.Ordinal));
        }

        // Another platform keeps every connector, flag or not.
        Assert.Equal(
            ConfigFieldCatalog.SupportedConnectors(new ConfigChoiceProfile("linux", true)),
            ConfigFieldCatalog.SupportedConnectors(new ConfigChoiceProfile("linux", true, OpenClawNativeWindows: true)));
    }

    [Fact]
    public void The_flag_comes_from_the_runtime_capability()
    {
        Assert.False(ConfigChoiceProfile.ForHost().OpenClawNativeWindows);
        Assert.True(ConfigChoiceProfile.ForHost(openClawNativeWindows: Runtime("1.0.10").Has(RuntimeCapability.OpenClawNativeWindows)).OpenClawNativeWindows);
        Assert.False(ConfigChoiceProfile.ForHost(openClawNativeWindows: Runtime("1.0.9").Has(RuntimeCapability.OpenClawNativeWindows)).OpenClawNativeWindows);
    }

    // ------------------------------------------------------------------ the OpenClaw card's first page

    private static WizardCatalog CatalogOver(string top, string openClaw, Func<RuntimeCapabilities?>? capabilities)
    {
        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true);
        return new WizardCatalog(
            new SetupHelpProbe(paths, diskCache: null, (_, path, _) =>
            {
                var text = string.Join(' ', path) switch
                {
                    "" => top,
                    "openclaw" => openClaw,
                    _ => null,
                };

                return Task.FromResult(text is null ? new HelpProbeResult(string.Empty, "defenseclaw exited 2.") : new HelpProbeResult(text, null));
            }),
            capabilities);
    }

    [Fact]
    public async Task On_1_0_10_the_openclaw_card_opens_on_what_has_to_be_in_place_in_the_runtimes_words()
    {
        var catalog = CatalogOver(TopHelp1010, OpenClawHelp1010, () => Runtime("1.0.10"));
        _ = await catalog.LoadAsync();

        var definition = await catalog.EnsureDetailAsync("openclaw");

        var first = definition.Steps[0];
        Assert.Equal(OpenClawSetupIntro.StepId, first.Id);
        Assert.Empty(first.Fields);
        var card = Assert.Single(first.Guide!.Cards);
        var needs = string.Join("\n", card.Needs);
        Assert.Contains("2026.8.35 or later", needs, StringComparison.Ordinal);
        Assert.Contains("npm install -g openclaw@latest", needs, StringComparison.Ordinal);
        Assert.Contains("defenseclaw init --connector openclaw", needs, StringComparison.Ordinal);
        Assert.Contains("openclaw gateway run", needs, StringComparison.Ordinal);
        Assert.Contains("No changes made.", needs, StringComparison.Ordinal);
        Assert.Contains("runs alone", needs, StringComparison.Ordinal);
        Assert.Contains(OpenClawSetupIntro.PreflightCommand, needs, StringComparison.Ordinal);
        Assert.Contains("openclaw preflight: ok", needs, StringComparison.Ordinal);
        Assert.Contains("does not run it", needs, StringComparison.Ordinal);
        Assert.Contains("restarts it", card.WillDo, StringComparison.Ordinal);
        Assert.Contains("loads the plugin", card.WillDo, StringComparison.Ordinal);
        Assert.Equal(PlatformStatus.Certified, definition.PlatformStatus);

        // The preflight command is text on the page and never a step of the wizard: nothing in the pages' fields runs it.
        Assert.DoesNotContain(definition.Steps.SelectMany(s => s.Fields), f => (f.Flag ?? string.Empty).Contains("preflight", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_1_0_10_the_generic_openclaw_wizard_keeps_restart_on_so_OpenClaws_gateway_is_restarted()
    {
        var catalog = CatalogOver(TopHelp1010, OpenClawHelp1010, () => Runtime("1.0.10"));
        _ = await catalog.LoadAsync();

        var definition = await catalog.EnsureDetailAsync("openclaw");

        var restart = definition.Steps.SelectMany(s => s.Fields).Single(f => f.Flag == "--restart");
        Assert.Equal(WizardFieldKind.Toggle, restart.Kind);
        Assert.Equal("--no-restart", restart.NegativeFlag);
        Assert.Equal(ToggleValues.On, restart.DefaultValue);
        Assert.Equal(ToggleValues.Off, definition.Steps.SelectMany(s => s.Fields).Single(f => f.Flag == "--replace").DefaultValue);
    }

    [Theory]
    [InlineData("1.0.9")]
    [InlineData("none")]
    [InlineData("unknown")]
    public async Task Without_OpenClaw_on_Windows_the_card_has_no_intro_and_reads_as_it_always_did(string runtime)
    {
        Func<RuntimeCapabilities?>? source = runtime switch
        {
            "none" => null,
            "unknown" => () => RuntimeCapabilities.Unknown,
            _ => () => Runtime(runtime),
        };
        var catalog = CatalogOver(TopHelp109, OpenClawHelp109, source);
        _ = await catalog.LoadAsync();

        var definition = await catalog.EnsureDetailAsync("openclaw");

        Assert.DoesNotContain(definition.Steps, s => s.Id == OpenClawSetupIntro.StepId);
        Assert.All(definition.Steps, s => Assert.Null(s.Guide));
        Assert.Equal(PlatformStatus.Unsupported, definition.PlatformStatus);
    }

    [Fact]
    public async Task A_probe_that_lands_after_the_detail_was_built_brings_the_intro_in_and_the_same_answer_again_reads_nothing()
    {
        // An in-app upgrade rebuilds the details before the runtime is probed again: the detail is built with the old capabilities first.
        var current = Runtime("1.0.9");
        var catalog = CatalogOver(TopHelp1010, OpenClawHelp1010, () => current);
        _ = await catalog.LoadAsync();
        Assert.DoesNotContain((await catalog.EnsureDetailAsync("openclaw")).Steps, s => s.Id == OpenClawSetupIntro.StepId);

        current = Runtime("1.0.10");
        await catalog.ObserveRuntimeAsync();

        Assert.Equal(OpenClawSetupIntro.StepId, (await catalog.EnsureDetailAsync("openclaw")).Steps[0].Id);

        // Told again with the same answer: the detail is the one already built.
        var built = await catalog.EnsureDetailAsync("openclaw");
        await catalog.ObserveRuntimeAsync();
        Assert.Same(built, await catalog.EnsureDetailAsync("openclaw"));

        current = Runtime("1.0.9");
        await catalog.ObserveRuntimeAsync();
        Assert.DoesNotContain((await catalog.EnsureDetailAsync("openclaw")).Steps, s => s.Id == OpenClawSetupIntro.StepId);
    }

    [Fact]
    public void The_intro_is_added_once_and_only_to_the_openclaw_card()
    {
        var steps = new[] { new WizardStep { Id = "options", Title = "Options" } };
        var caps = Runtime("1.0.10");

        var once = OpenClawSetupIntro.Apply("openclaw", steps, caps);
        var twice = OpenClawSetupIntro.Apply("openclaw", once, caps);

        Assert.Equal(new[] { OpenClawSetupIntro.StepId, "options" }, once.Select(s => s.Id));
        Assert.Same(once, twice);
        Assert.Same(steps, OpenClawSetupIntro.Apply("codex", steps, caps));
        Assert.Same(steps, OpenClawSetupIntro.Apply("openclaw", steps, Runtime("1.0.9")));
        Assert.Same(steps, OpenClawSetupIntro.Apply("openclaw", steps, null));
    }

    [Fact]
    public void Setup_openclaw_with_replace_says_it_removes_every_other_connector_and_is_destructive()
    {
        var replace = new[] { "setup", "openclaw", "--mode", "observe", "--replace", "--yes" };
        var plain = new[] { "setup", "openclaw", "--mode", "observe", "--yes" };

        var bar = Assert.Single(WizardCautions.For(replace), w => w.Title == OpenClawReplaceReview.Title);
        Assert.Contains("removes every configured hook connector", bar.Message, StringComparison.Ordinal);
        Assert.Equal(DefenseClaw.Core.Cli.CommandTier.Destructive, WizardCautions.Floor(replace));

        Assert.DoesNotContain(WizardCautions.For(plain), w => w.Title == OpenClawReplaceReview.Title);
        Assert.Null(WizardCautions.Floor(plain));
        Assert.DoesNotContain(WizardCautions.For(new[] { "setup", "codex", "--replace", "--yes" }), w => w.Title == OpenClawReplaceReview.Title);
    }
}