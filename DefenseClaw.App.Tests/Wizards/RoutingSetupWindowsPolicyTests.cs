using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The Setup "Routing" card on Windows (CUST-457): <c>setup routing --enable</c> raises on native Windows (cmd_setup.py@1.0.10 <c>setup_routing</c>), so the
/// card offers <c>--disable</c> and <c>--status</c> only and carries the runtime's sentence.
/// </summary>
public sealed class RoutingSetupWindowsPolicyTests
{
    private static readonly string RoutingHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup routing [OPTIONS]

          Configure semantic model routing.

          In managed mode, the gateway owns a minimal vLLM Semantic Router Docker
          container.

        Options:
          --enable    Enable semantic model routing.
          --disable   Disable semantic model routing.
          --status    Show routing status.
          -y, --yes   Accepted for compatibility; this command is non-interactive.
          --help      Show this message and exit.
        """);

    private static readonly string OtherHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup widget [OPTIONS]

          Configure widgets.

        Options:
          --enable    Enable widgets.
          --disable   Disable widgets.
          --yes       Do not prompt.
          --help      Show this message and exit.
        """);

    private static IReadOnlyList<WizardStep> Steps(string target, string help)
    {
        var parsed = SetupHelpParser.Parse(help);
        var (steps, _) = WizardStepFactory.Build(target, parsed);
        return WizardWindowsPolicy.Filter(target, steps);
    }

    private static string[] Flags(IReadOnlyList<WizardStep> steps) =>
        steps.SelectMany(s => s.Fields).Select(f => f.Flag ?? string.Empty).Where(f => f.Length > 0).ToArray();

    [Fact]
    public void The_routing_card_has_no_enable_field_and_keeps_disable_and_status()
    {
        var flags = Flags(Steps("routing", RoutingHelp));

        Assert.DoesNotContain("--enable", flags);
        Assert.Contains("--disable", flags);
        Assert.Contains("--status", flags);
    }

    [Fact]
    public void The_first_page_carries_the_runtimes_sentence_word_for_word()
    {
        var steps = Steps("routing", RoutingHelp);

        Assert.Contains(
            "Semantic routing is not supported on native Windows: it routes OpenClaw and ZeptoClaw proxy traffic, and DefenseClaw runs no guardrail proxy on Windows.",
            steps[0].Subtitle,
            StringComparison.Ordinal);
        Assert.Equal(RoutingSetupWindowsPolicy.RuntimeSentence, steps[0].Subtitle[^RoutingSetupWindowsPolicy.RuntimeSentence.Length..]);
        Assert.StartsWith("Configure semantic model routing.", steps[0].Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_defaults_the_card_still_builds_a_valid_command_that_only_shows_the_status()
    {
        var steps = Steps("routing", RoutingHelp);
        var definition = new WizardDefinition { Target = "routing", Title = "Routing", Group = WizardGroups.Other, Steps = steps, IsDetailLoaded = true };
        var values = new WizardValues();
        foreach (var field in steps.SelectMany(s => s.Fields))
        {
            values[field.Id] = field.DefaultValue;
        }

        var argv = definition.BuildArgv(values);

        Assert.Equal("setup", argv[0]);
        Assert.Equal("routing", argv[1]);
        Assert.DoesNotContain("--enable", argv);
        Assert.DoesNotContain("--disable", argv);
    }

    // ------------------------------------------------------------------ by release (the refusal is in the source from 1.0.5; 1.0.4 accepts --enable)

    private const string RuntimeHelp = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeCapabilities Runtime(string version) =>
        RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", RuntimeHelp, RuntimeHelp, null, null, null, null, null),
            "test",
            "fp",
            DateTimeOffset.UnixEpoch).Capabilities;

    private static IReadOnlyList<WizardStep> StepsFor(RuntimeCapabilities? capabilities)
    {
        var (steps, _) = WizardStepFactory.Build("routing", SetupHelpParser.Parse(RoutingHelp));
        return WizardWindowsPolicy.Filter("routing", steps, capabilities);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.4")]
    public void On_1_0_0_to_1_0_4_the_card_offers_enable_and_carries_no_note(string version)
    {
        var steps = StepsFor(Runtime(version));

        Assert.Contains("--enable", Flags(steps));
        Assert.Contains("--disable", Flags(steps));
        Assert.DoesNotContain("not supported on native Windows", steps[0].Subtitle, StringComparison.Ordinal);
        Assert.StartsWith("Configure semantic model routing.", steps[0].Subtitle, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.5")]
    [InlineData("1.0.9")]
    public void From_1_0_5_to_1_0_9_the_card_has_no_enable_and_the_runtimes_sentence_word_for_word(string version)
    {
        var steps = StepsFor(Runtime(version));

        Assert.DoesNotContain("--enable", Flags(steps));
        Assert.Contains("--disable", Flags(steps));
        Assert.EndsWith(RoutingSetupWindowsPolicy.RuntimeSentence, steps[0].Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void On_1_0_10_the_sentence_says_this_command_refuses_and_route_only_is_a_separate_path()
    {
        var steps = StepsFor(Runtime("1.0.10"));

        Assert.DoesNotContain("--enable", Flags(steps));
        Assert.EndsWith(RoutingSetupWindowsPolicy.RuntimeSentenceWithRouteOnly, steps[0].Subtitle, StringComparison.Ordinal);
        Assert.DoesNotContain(RoutingSetupWindowsPolicy.RuntimeSentence, steps[0].Subtitle, StringComparison.Ordinal);
        Assert.Contains("Route-only routing of hook connectors is a separate path", steps[0].Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void A_caller_that_does_not_know_the_runtime_and_an_unprobed_runtime_get_the_policy()
    {
        Assert.DoesNotContain("--enable", Flags(StepsFor(null)));
        Assert.DoesNotContain("--enable", Flags(StepsFor(RuntimeCapabilities.Unknown)));
    }

    [Fact]
    public void Another_target_with_the_same_flags_is_unchanged()
    {
        var steps = Steps("widget", OtherHelp);

        Assert.Contains("--enable", Flags(steps));
        Assert.DoesNotContain(RoutingSetupWindowsPolicy.RuntimeSentence, steps[0].Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_note_leaves_other_targets_and_later_pages_alone()
    {
        var steps = Steps("widget", OtherHelp);

        Assert.Same(steps, RoutingSetupWindowsPolicy.AddNote("widget", steps));
        Assert.Equal(steps.Count, RoutingSetupWindowsPolicy.AddNote("routing", steps).Count);
        Assert.False(RoutingSetupWindowsPolicy.Suppresses("widget", "--enable"));
        Assert.False(RoutingSetupWindowsPolicy.Suppresses("routing", "--disable"));
        Assert.True(RoutingSetupWindowsPolicy.Suppresses("routing", "--enable"));
    }

    [Fact]
    public async Task A_probe_that_lands_after_the_card_was_built_reads_it_again_and_the_same_answer_reads_nothing()
    {
        var current = Runtime("1.0.4");
        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true);
        var catalog = new WizardCatalog(
            new SetupHelpProbe(paths, diskCache: null, (_, path, _) =>
            {
                var text = string.Join(' ', path) switch
                {
                    "" => "Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  routing  Configure model routing.\n",
                    "routing" => RoutingHelp,
                    _ => null,
                };
                return Task.FromResult(text is null ? new HelpProbeResult(string.Empty, "defenseclaw exited 2.") : new HelpProbeResult(text, null));
            }),
            () => current);
        _ = await catalog.LoadAsync();
        Assert.Contains("--enable", Flags((await catalog.EnsureDetailAsync("routing")).Steps));

        current = Runtime("1.0.9");
        await catalog.ObserveRuntimeAsync();
        Assert.DoesNotContain("--enable", Flags((await catalog.EnsureDetailAsync("routing")).Steps));

        var built = await catalog.EnsureDetailAsync("routing");
        await catalog.ObserveRuntimeAsync();
        Assert.Same(built, await catalog.EnsureDetailAsync("routing"));

        current = Runtime("1.0.10");
        await catalog.ObserveRuntimeAsync();
        Assert.Contains(RoutingSetupWindowsPolicy.RuntimeSentenceWithRouteOnly, (await catalog.EnsureDetailAsync("routing")).Steps[0].Subtitle, StringComparison.Ordinal);
    }}
