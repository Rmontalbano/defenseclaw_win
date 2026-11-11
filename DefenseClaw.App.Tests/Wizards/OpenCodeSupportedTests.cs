using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// DefenseClaw 1.0.8 declares OpenCode <c>supported</c> on native Windows (<c>platform_support.py</c>), where 1.0.6 and 1.0.7 call it <c>preview</c>.
/// The CLI then prints the short help "Configure DefenseClaw for OpenCode." in <c>setup --help</c> (it only writes "{label}: {status} on windows."
/// for a connector that is not supported) and leaves the "Platform status on windows:" paragraph out of <c>setup opencode --help</c>. The app reads
/// all of this from the help, never from a name list, so the connector is certified on 1.0.8 and still preview on the older wording. Synthetic screens
/// shaped like Click's output.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OpenCodeSupportedTests
{
    private const string TopLevel108 = """
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          claude-code  Configure DefenseClaw for Claude Code.
          codex        Configure DefenseClaw for Codex.
          cursor       Cursor: not_certified on windows.
          opencode     Configure DefenseClaw for OpenCode.
        """;

    private const string TopLevel106 = """
        Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

          Configure DefenseClaw.

        Options:
          --help  Show this message and exit.

        Commands:
          claude-code  Configure DefenseClaw for Claude Code.
          codex        Configure DefenseClaw for Codex.
          cursor       Cursor: not_certified on windows.
          opencode     OpenCode: preview on windows.
        """;

    /// <summary>Shaped like 1.0.8's <c>setup opencode --help</c>: the description, and no "Platform status on windows" paragraph.</summary>
    private static readonly string OpenCodeHelp108 = LineEndings.Normalize("""
        Usage: defenseclaw setup opencode [OPTIONS]

          Configure DefenseClaw for OpenCode via its agent lifecycle hooks.

          Configures this connector in the hook connector set so CLI/TUI scanners
          read that agent's documented local surfaces. Default mode is observe on
          first setup; a re-run keeps the current mode. Action may enable
          agent-native blocking/approval verdicts with --mode action on supported
          events. No proxy is involved in either mode.

        Options:
          --mode [observe|action]   Hook policy mode.  [default: observe]
          --fail-mode [open|closed]
                                    What the hook does when the gateway is
                                    unreachable.
          --replace / --no-replace  Replace hooks already installed by other tools.
                                    [default: no-replace]
          --restart / --no-restart  Restart the gateway after applying changes.
                                    [default: restart]
          -y, --yes                 Skip the confirmation prompt.
          --help                    Show this message and exit.
        """);

    private static string Summary(string topLevel, string name) =>
        SetupHelpParser.Parse(LineEndings.Normalize(topLevel)).Commands.Single(c => c.Name == name).Summary;

    [Fact]
    public void On_1_0_8_the_top_level_summary_does_not_say_preview_and_the_per_target_help_reads_as_certified()
    {
        var summary = Summary(TopLevel108, "opencode");
        Assert.Equal("Configure DefenseClaw for OpenCode.", summary);
        Assert.Equal(PlatformStatus.Unknown, SetupHelpParser.StatusFromSummary(summary));

        var parsed = SetupHelpParser.Parse(OpenCodeHelp108);

        Assert.True(SetupHelpParser.IsConnectorShaped(parsed.Options));
        Assert.Equal(PlatformStatus.Certified, parsed.PlatformStatus);
        Assert.Equal(string.Empty, parsed.PlatformNote);
        Assert.NotEqual(PlatformStatus.Preview, parsed.PlatformStatus);
        Assert.Equal(PlatformStatus.Certified, SetupHelpParser.ExtractPlatformStatus(OpenCodeHelp108).Status);
    }

    [Fact]
    public void On_1_0_6_and_1_0_7_the_same_connector_is_still_preview_in_both_screens()
    {
        var summary = Summary(TopLevel106, "opencode");
        Assert.Equal("OpenCode: preview on windows.", summary);
        Assert.Equal(PlatformStatus.Preview, SetupHelpParser.StatusFromSummary(summary));

        var parsed = SetupHelpParser.Parse(PreviewStatusTests.OpenCodeHelp);
        Assert.Equal(PlatformStatus.Preview, parsed.PlatformStatus);
        Assert.Equal(PreviewStatusTests.PreviewNote, parsed.PlatformNote);
    }

    [Fact]
    public void A_1_0_8_certified_OpenCode_is_not_hidden_by_Hide_uncertified_connectors_and_a_preview_one_still_is()
    {
        var certified = SetupHelpParser.Parse(OpenCodeHelp108);
        var preview = SetupHelpParser.Parse(PreviewStatusTests.OpenCodeHelp);

        WizardDefinition Def(string target, string title, PlatformStatus status, string note = "") => new()
        {
            Target = target,
            Title = title,
            Group = WizardGroups.Connectors,
            PlatformStatus = status,
            PlatformNote = note,
            Description = "Configure DefenseClaw for " + title + ".",
            IsDetailLoaded = true,
        };

        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);

            // 1.0.8: codex and OpenCode are both certified, cursor is not.
            vm.ShowCards(new[]
            {
                Def("codex", "Codex", PlatformStatus.Certified),
                Def("cursor", "Cursor", PlatformStatus.NotCertified),
                Def("opencode", "OpenCode", certified.PlatformStatus, certified.PlatformNote),
            });

            var card = vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "opencode");
            Assert.Equal(PlatformStatus.Certified, card.PlatformStatus);
            Assert.NotEqual("Preview on Windows", card.Badge);
            Assert.Contains("2 certified connector(s)", vm.StatusNote, StringComparison.Ordinal);

            vm.CertifiedOnly = true;
            Assert.Contains(vm.Groups.SelectMany(g => g.Cards), c => c.Target == "opencode");
            Assert.Contains(vm.ConnectorChoices, c => c.Target == "opencode");
            Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Cards), c => c.Target == "cursor");

            // 1.0.6 / 1.0.7: preview, hidden by the same filter.
            vm.CertifiedOnly = false;
            vm.ShowCards(new[]
            {
                Def("codex", "Codex", PlatformStatus.Certified),
                Def("opencode", "OpenCode", preview.PlatformStatus, preview.PlatformNote),
            });
            vm.CertifiedOnly = true;
            Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Cards), c => c.Target == "opencode");
        });
    }
}
