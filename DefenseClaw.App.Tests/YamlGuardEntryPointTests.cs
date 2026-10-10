using System.Diagnostics;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests;

/// <summary>CUST-341: the app's own YamlDotNet entry points refuse a too-deep text before the parser sees it, the way <see cref="ConfigStore"/> does.</summary>
public sealed class YamlGuardEntryPointTests
{
    private static string Deep(string prefix) => prefix + new string('[', 5000) + new string(']', 5000) + "\n";

    [Fact]
    public void Each_app_yaml_reader_refuses_a_too_deep_text_quickly_through_its_own_could_not_parse_path()
    {
        var clock = Stopwatch.StartNew();

        // Registries panel: an unreadable section reads as "not required".
        Assert.False(RegistriesPanelViewModel.ReadRegistryRequired(Deep("asset_policy:\n  skill:\n    registry_required: "), "skill"));

        // Wizard baseline: an unreadable document is an empty one.
        Assert.Null(WizardBaseline.ConfigYaml.Parse(Deep("llm:\n  provider: ")).Get("llm", "provider"));

        // Restart queue: a text that does not parse reports the change as unknown (changed, no sections named).
        var change = RestartQueueRules.CompareConfig(Deep("a: "), "a: 1\n");
        Assert.True(change.Changed);
        Assert.Empty(change.Sections);

        // Config editor form: the refusal is the warning, with the reason.
        var built = ConfigFormBuilder.Build(Deep("a: "), ConfigStore.Parse("a: 1\n"), _ => { }, _ => { });
        Assert.Empty(built.Sections);
        Assert.Contains("nests flow collections", Assert.Single(built.Warnings), StringComparison.Ordinal);

        Assert.True(clock.Elapsed < DefenseClaw.Tests.TestSupport.TestTimeouts.Ceiling, "refusals must not run the parser");
    }
}
