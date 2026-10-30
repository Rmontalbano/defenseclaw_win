using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// What the app says observe mode and the open fail mode do, by runtime (CUST-400): through 1.0.4 "never blocks", from 1.0.5 never blocks on policy findings but
/// still blocks a change to DefenseClaw's own files; and the Splunk Enterprise card, which from 1.0.5 probes the endpoint before it saves.
/// </summary>
public sealed class Observe105CopyTests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok(Help));

    // ------------------------------------------------------------------ the batch dialog's mode note

    [Theory]
    [InlineData("1.0.5", "Observe records what an agent does and never blocks on policy findings; DefenseClaw still blocks changes to its own files.")]
    [InlineData("1.0.4", "Observe records what an agent does and never blocks it.")]
    [InlineData("0.8.10", "Observe records what an agent does and never blocks it.")]
    public async Task The_batch_dialogs_observe_note_follows_the_runtime(string version, string expected)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: Runtime(version));
        _ = await services.Runtime.RefreshAsync();
        using var vm = new ConnectorBatchViewModel(
            services,
            _ => Task.FromResult(new HelpProbeResult(Help, null)),
            () => Array.Empty<OfferedConnector>());

        Assert.Equal("observe", vm.Mode);
        Assert.Equal(expected, vm.ModeNote);
    }

    // ------------------------------------------------------------------ the Setup roster

    private static GuardrailConnectorRow Row(string mode, string fail) => new(new KeyValuePair<string, string>[]
    {
        new("Connector", "Codex"),
        new("Key", "codex"),
        new("State", "enabled"),
        new("Mode", mode),
        new("Fail", fail),
    });

    [Fact]
    public void The_guardrail_rows_mismatch_note_names_the_own_file_block_only_from_1_0_5()
    {
        var old = new GuardrailRowViewModel(Row("observe", "closed"));
        var current = new GuardrailRowViewModel(Row("observe", "closed"), selfChangeBlocked: true);

        Assert.True(old.HasMismatch);
        Assert.Equal("observe + fail-closed: this connector never blocks on policy, but does block whenever the gateway errors.", old.MismatchNote);
        Assert.Equal(ObserveModeCopy.ObserveFailClosedNote(true), current.MismatchNote);
        Assert.Contains("changes to DefenseClaw's own files", current.MismatchNote, StringComparison.Ordinal);

        // Same columns, different runtime: the roster merge must replace the row rather than keep the old sentence.
        Assert.False(old.IsSameAs(current));
        Assert.True(current.IsSameAs(new GuardrailRowViewModel(Row("observe", "closed"), selfChangeBlocked: true)));
    }

    [Fact]
    public void The_connector_chips_mismatch_note_follows_the_runtime_too()
    {
        var old = new ConnectorChipViewModel("codex", "observe", "closed", hasMismatch: true, fromConfig: true);
        var current = new ConnectorChipViewModel("codex", "observe", "closed", hasMismatch: true, fromConfig: true, selfChangeBlocked: true);

        Assert.Equal("observe + fail-closed: this connector never blocks on policy, but does block whenever the gateway errors.", old.MismatchNote);
        Assert.Equal(ObserveModeCopy.ObserveFailClosedNote(true), current.MismatchNote);
        Assert.Contains("never blocks on policy findings", current.ToolTipText, StringComparison.Ordinal);
        Assert.False(old.IsSameAs(current));
    }

    // ------------------------------------------------------------------ the durability row's fallback

    [Fact]
    public void The_codex_mode_fallback_says_which_observe_it_picked_by_runtime()
    {
        var (mode, source) = DurabilityAttention.ResolveCodexMode(Array.Empty<string>(), null);
        Assert.Equal("observe", mode);
        Assert.Equal("no current mode could be found, so observe, which never blocks", source);

        (mode, source) = DurabilityAttention.ResolveCodexMode(Array.Empty<string>(), null, observeBlocksSelfChange: true);
        Assert.Equal("observe", mode);
        Assert.Equal("no current mode could be found, so observe, which never blocks on policy findings", source);

        // A mode that was found is described the same on every runtime.
        Assert.Equal(
            DurabilityAttention.ResolveCodexMode(Array.Empty<string>(), "enforce"),
            DurabilityAttention.ResolveCodexMode(Array.Empty<string>(), "enforce", observeBlocksSelfChange: true));
    }

    // ------------------------------------------------------------------ the Splunk Enterprise card

    private const string OldEnterpriseLine = "Sends one best-effort HEC probe unless --skip-test is set.";

    private const string NewEnterpriseLines =
        "Probes the HEC endpoint before saving; a failed probe\n" +
        "                     saves nothing (exit 1). --skip-test saves without it.";

    private static WizardGuideCard EnterpriseCard(string help)
    {
        var parsed = SetupHelpParser.Parse(help);
        var subcommands = parsed.Commands.ToDictionary(c => c.Name, c => new ParsedHelp { Summary = c.Summary });
        var steps = WizardStepFactory.BuildGroup(parsed, subcommands);
        steps = WizardWindowsPolicy.Filter("splunk", steps);
        steps = WizardSyntheticSecrets.Add("splunk", steps);
        steps = SecretRoutes.Annotate("splunk", steps);
        steps = WizardWalkthroughs.Apply("splunk", steps, help);
        return steps.Single(s => s.Id == WizardWalkthroughs.GuideStepId).Guide!.Cards.Single(c => c.FieldId == "enterprise");
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void A_1_0_5_help_makes_the_card_say_a_failed_probe_saves_nothing(string eol)
    {
        Assert.Contains(OldEnterpriseLine, WizardSamples.SplunkHelp, StringComparison.Ordinal);
        var help = LineEndings.With(WizardSamples.SplunkHelp.Replace(OldEnterpriseLine, NewEnterpriseLines, StringComparison.Ordinal), eol);

        var card = EnterpriseCard(help);

        Assert.Contains("Sends one live HEC probe to your endpoint first", card.WillDo, StringComparison.Ordinal);
        Assert.Contains("A failed probe saves nothing and the command exits 1", card.WillDo, StringComparison.Ordinal);
        Assert.Contains("Skip test", card.WillDo, StringComparison.Ordinal);
        Assert.DoesNotContain("best-effort", card.WillDo, StringComparison.Ordinal);
        Assert.Contains("a failed probe saves nothing", card.CliSays, StringComparison.Ordinal);
    }

    [Fact]
    public void An_earlier_help_keeps_the_best_effort_probe_sentence()
    {
        var card = EnterpriseCard(WizardSamples.SplunkHelp);

        Assert.Contains("one best-effort HEC probe", card.WillDo, StringComparison.Ordinal);
        Assert.DoesNotContain("saves nothing", card.WillDo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Probes the HEC endpoint before saving; a failed probe saves nothing (exit 1).", true)]
    [InlineData("Probes the HEC endpoint before saving; a failed probe   saves nothing (exit 1).", true)]
    [InlineData("Sends one best-effort HEC probe unless --skip-test is set.", false)]
    [InlineData("", false)]
    public void ProbesBeforeSaving_reads_the_clis_own_words(string enterpriseHelp, bool expected) =>
        Assert.Equal(expected, WizardWalkthroughs.ProbesBeforeSaving(enterpriseHelp));
}
