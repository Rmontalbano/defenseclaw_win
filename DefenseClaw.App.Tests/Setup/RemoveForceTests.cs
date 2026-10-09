using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// <c>setup remove CONNECTOR --force</c> (CUST-271): the connector removal wizard is generated from the CLI's own help, which lists <c>--force</c>
/// ("Allow removing the LAST remaining connector"), so the option is the generated wizard's, off until chosen, and reaches the command line only
/// then. Held against the live 0.8.10 capture (<c>Fixtures/runtime-0.8.10/setup-remove.txt</c>), so a CLI that drops or renames it is noticed.
/// </summary>
public sealed class RemoveForceTests
{
    private static WizardDefinition Remove()
    {
        var help = SetupHelpParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup-remove.txt")));
        var (steps, curated) = WizardStepFactory.Build("remove", help);
        return new WizardDefinition
        {
            Target = "remove",
            Title = "Remove a connector",
            Group = WizardGroups.Connectors,
            Steps = steps,
            IsCurated = curated,
            IsDetailLoaded = true,
        };
    }

    [Fact]
    public void The_live_help_lists_force_and_describes_it_as_the_last_connector_option()
    {
        var help = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup-remove.txt"));

        Assert.Contains("--force", help, StringComparison.Ordinal);
        Assert.Contains("Allow removing the LAST remaining connector", help, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_wizard_offers_force_off_and_only_chosen_it_reaches_the_command_line()
    {
        var definition = Remove();
        var force = WizardSamples.Find(definition, "--force");
        var values = WizardSamples.StartingValues(definition);
        var connector = definition.AllFields.Single(f => f.Flag is null);

        values[connector.Id] = "codex";
        var plain = definition.BuildArgv(values);
        Assert.DoesNotContain("--force", plain);
        Assert.Equal("setup", plain[0]);
        Assert.Equal("remove", plain[1]);
        Assert.Contains("codex", plain);

        values[force.Id] = ToggleValues.On;
        var forced = definition.BuildArgv(values);
        Assert.Single(forced, a => a == "--force");
        Assert.Contains("codex", forced);
    }
}
