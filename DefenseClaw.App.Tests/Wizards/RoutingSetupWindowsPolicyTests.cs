using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

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
}
