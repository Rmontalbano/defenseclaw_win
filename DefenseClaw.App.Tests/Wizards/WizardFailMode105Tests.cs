using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The hook connector wizard on a per-user 1.0.5 (CUST-397): <c>setup &lt;connector&gt; --fail-mode</c> saves a value that wins over the policy tier (cmd_setup.py
/// <c>--fail-mode</c> help at tag 1.0.5), so the question is back (1.0.4 dropped it), and the argv carries the flag only when the operator picks a value that
/// differs from what config.yaml already saves (<see cref="WizardBaseline"/> makes a saved value the baseline; nothing saved starts on "unchanged").
/// </summary>
public sealed class WizardFailMode105Tests
{
    private const string Help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";

    private static readonly string SavedClosed = LineEndings.Normalize("""
        guardrail:
          connector: claudecode
          connectors:
            claudecode:
              mode: observe
              hook_fail_mode: closed
        """);

    private static readonly string NothingSaved = LineEndings.Normalize("""
        guardrail:
          connector: claudecode
          connectors:
            claudecode:
              mode: observe
        """);

    private static bool Ignored(string version) =>
        HookFailModeRule.SavedValueIgnored(
            RuntimeProbe.Evaluate(
                new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", Help, Help, null, null, null, null, null),
                "test", "fp", DateTimeOffset.UnixEpoch).Capabilities,
            managed: false);

    private static WizardDefinition Wizard(string version, string config) =>
        WizardFailModeRule.Apply(WizardBaseline.Apply(WizardSamples.ClaudeCode(), WizardSamples.Config(config), configReadable: true), Ignored(version));

    [Fact]
    public void On_1_0_5_the_question_is_back_and_an_untouched_page_sends_no_fail_mode_whether_or_not_one_is_saved()
    {
        foreach (var config in new[] { SavedClosed, NothingSaved })
        {
            var definition = Wizard("1.0.5", config);

            Assert.Contains(definition.AllFields, f => f.Flag == "--fail-mode");
            Assert.DoesNotContain("--fail-mode", definition.BuildArgv(WizardSamples.StartingValues(definition)));
        }

        Assert.Equal(string.Empty, WizardSamples.Find(Wizard("1.0.5", NothingSaved), "--fail-mode").DefaultValue);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("open")]
    public void On_1_0_5_a_value_the_operator_picks_is_sent_where_it_changes_what_is_saved(string picked)
    {
        var definition = Wizard("1.0.5", NothingSaved);
        var values = WizardSamples.StartingValues(definition);
        values["fail-mode"] = picked;

        var argv = definition.BuildArgv(values).ToList();

        Assert.Equal(picked, argv[argv.IndexOf("--fail-mode") + 1]);
    }

    [Fact]
    public void On_1_0_4_the_question_stays_dropped()
    {
        var definition = Wizard("1.0.4", SavedClosed);

        Assert.DoesNotContain(definition.AllFields, f => f.Flag == "--fail-mode");
        Assert.DoesNotContain("--fail-mode", definition.BuildArgv(WizardSamples.StartingValues(definition)));
    }
}
