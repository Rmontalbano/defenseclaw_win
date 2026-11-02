using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// DefenseClaw 1.0.6 moves OpenCode on Windows from supported to <c>preview</c> (<c>platform_support.py</c>), and prints it as
/// <c>Platform status on windows: preview — ...</c> in the connector's help and <c>OpenCode: preview on windows.</c> in <c>setup --help</c>
/// (<c>cmd_setup.py</c> 14260 and 14272). The status is read from the help, so it is not a name list: before CUST-425 the word fell through to
/// "unknown" and the CLI's note was dropped. Synthetic screens; the em dash and the wrapped paragraph are the real layout.
/// </summary>
public sealed class PreviewStatusTests
{
    private const string EmDash = "—";

    internal const string PreviewNote =
        "OpenCode V2 is available as a native Windows preview using the reviewed @opencode/cli contract.";

    /// <summary>Shaped like 1.0.6's <c>setup opencode --help</c>: the status paragraph wrapped across two lines, after the description.</summary>
    internal static readonly string OpenCodeHelp = LineEndings.Normalize($"""
        Usage: defenseclaw setup opencode [OPTIONS]

          Configure DefenseClaw for OpenCode via its agent lifecycle hooks.

          Configures this connector in the hook connector set so CLI/TUI scanners
          read that agent's documented local surfaces. No proxy is involved in
          either mode.

          Platform status on windows: preview {EmDash} OpenCode V2 is available as a
          native Windows preview using the reviewed @opencode/cli contract.

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

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void A_preview_connectors_help_reads_as_preview_with_the_CLIs_own_note(string eol)
    {
        var parsed = SetupHelpParser.Parse(LineEndings.With(OpenCodeHelp, eol));

        Assert.Equal(PlatformStatus.Preview, parsed.PlatformStatus);
        Assert.Equal(PreviewNote, parsed.PlatformNote);
        Assert.DoesNotContain('\r', parsed.PlatformNote);
        Assert.True(SetupHelpParser.IsConnectorShaped(parsed.Options));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_status_word_alone_is_enough(string eol)
    {
        const string line = "Platform status on windows: preview";
        Assert.Equal(PlatformStatus.Preview, SetupHelpParser.ExtractPlatformStatus(line).Status);
        Assert.Equal(PlatformStatus.Preview, SetupHelpParser.ExtractPlatformStatus(line + eol + eol + "Options:" + eol).Status);
    }

    [Fact]
    public void A_word_the_app_does_not_know_still_reads_as_unknown_not_as_preview()
    {
        Assert.Equal(PlatformStatus.Unknown, SetupHelpParser.ExtractPlatformStatus("Platform status on windows: previewish").Status);
        Assert.Equal(PlatformStatus.Unknown, SetupHelpParser.ExtractPlatformStatus("Platform status on windows: something_new").Status);
    }

    [Theory]
    [InlineData("OpenCode: preview on windows.", PlatformStatus.Preview)]
    [InlineData("OpenCode: Preview on Windows.", PlatformStatus.Preview)]
    [InlineData("Cursor: not_certified on windows.", PlatformStatus.NotCertified)]
    [InlineData("OpenClaw: unsupported on windows.", PlatformStatus.Unsupported)]
    [InlineData("Preview what the next command would change.", PlatformStatus.Unknown)]
    [InlineData("Configure DefenseClaw for Amp.", PlatformStatus.Unknown)]
    public void The_top_level_summary_hint_carries_preview_before_the_per_target_help_lands(string summary, PlatformStatus expected)
    {
        Assert.Equal(expected, SetupHelpParser.StatusFromSummary(summary));
    }

    [Fact]
    public void The_new_value_is_last_so_no_stored_number_moves()
    {
        Assert.Equal(0, (int)PlatformStatus.Unknown);
        Assert.Equal(4, (int)PlatformStatus.NotApplicable);
        Assert.Equal(5, (int)PlatformStatus.Preview);
        Assert.Equal(PlatformStatus.Preview, Enum.GetValues<PlatformStatus>().Max());
    }

    [Fact]
    public void Preview_has_its_own_badge_in_the_caution_tone()
    {
        Assert.Equal("Preview on Windows", PlatformStatusText.Badge(PlatformStatus.Preview));
        Assert.Equal("Warn", PlatformStatusText.Key(PlatformStatus.Preview));
        Assert.Equal(PlatformStatusText.Key(PlatformStatus.NotCertified), PlatformStatusText.Key(PlatformStatus.Preview));
    }

    [Fact]
    public void Every_status_has_a_badge_other_than_the_unknown_one_and_the_unknown_is_only_for_unknown()
    {
        foreach (var status in Enum.GetValues<PlatformStatus>().Where(static s => s != PlatformStatus.Unknown))
        {
            Assert.NotEqual(PlatformStatusText.Badge(PlatformStatus.Unknown), PlatformStatusText.Badge(status));
        }
    }

    [Fact]
    public void The_wizard_warning_keeps_the_CLIs_note_instead_of_saying_the_help_could_not_be_read()
    {
        var warning = PlatformStatusText.Warning(PlatformStatus.Preview, PreviewNote);

        Assert.Contains(PreviewNote, warning, StringComparison.Ordinal);
        Assert.Contains("preview on Windows", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", warning, StringComparison.Ordinal);
        Assert.NotEqual(PlatformStatusText.Warning(PlatformStatus.NotCertified, PreviewNote), warning);
        Assert.NotEmpty(PlatformStatusText.Warning(PlatformStatus.Preview, string.Empty));
    }

    [Fact]
    public void A_parsed_preview_screen_reaches_the_wizards_header_and_warning()
    {
        var help = SetupHelpParser.Parse(OpenCodeHelp);
        var definition = new WizardDefinition
        {
            Target = "opencode",
            Title = "OpenCode",
            Group = WizardGroups.Connectors,
            PlatformStatus = help.PlatformStatus,
            PlatformNote = help.PlatformNote,
        };

        Assert.Equal("Preview on Windows", PlatformStatusText.Badge(definition.PlatformStatus));
        Assert.Contains(help.PlatformNote, PlatformStatusText.Warning(definition.PlatformStatus, definition.PlatformNote), StringComparison.Ordinal);
    }

    // ---- first run, the per-row Add and the batch dialog ----

    private static WizardDefinition Card(string target, PlatformStatus status, string title) => new()
    {
        Target = target,
        Title = title,
        Group = WizardGroups.Connectors,
        PlatformStatus = status,
    };

    [Fact]
    public void A_preview_connector_is_offered_with_a_caution_not_blocked()
    {
        var cards = new[]
        {
            Card("codex", PlatformStatus.Certified, "Codex"),
            Card("opencode", PlatformStatus.Preview, "OpenCode"),
            Card("cursor", PlatformStatus.NotCertified, "Cursor"),
            Card("openclaw", PlatformStatus.Unsupported, "OpenClaw"),
        };

        var offered = ConnectorOnboarding.Offerable(cards);
        var opencode = offered.Single(o => o.Id == "opencode");

        Assert.Equal(PlatformStatus.Preview, opencode.Platform);
        Assert.True(opencode.HasCaution);
        Assert.True(opencode.IsPreview);
        Assert.Contains("preview on Windows", opencode.Caution, StringComparison.Ordinal);
        Assert.Contains("OpenCode", opencode.Caution, StringComparison.Ordinal);

        // The caution words preview as preview and the other as the other.
        Assert.DoesNotContain("not certified", opencode.Caution, StringComparison.Ordinal);
        Assert.Contains("not certified on Windows", offered.Single(o => o.Id == "cursor").Caution, StringComparison.Ordinal);
        Assert.False(offered.Single(o => o.Id == "codex").HasCaution);

        // Available: the Windows policy does not refuse it (only an unsupported one is refused), and the unsupported one is still left out.
        Assert.Null(WizardWindowsPolicy.UnavailableReason("opencode", PlatformStatus.Preview));
        Assert.DoesNotContain(offered, o => o.Id == "openclaw");
    }
}
