using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "Add" on the connector discovery table (CUST-210): an agent the CLI found installed that DefenseClaw is not set up for opens the shared review
/// of <c>defenseclaw setup &lt;alias&gt; --yes --mode observe</c>. Nothing runs until the review is confirmed; afterwards the table is re-read,
/// so the row turns Active.
/// </summary>
public sealed partial class AiDiscoveryPanelViewModel
{
    /// <summary>True for an installed, not-yet-active connector that can be set up on this machine (not a proxy connector, not one the CLI calls unsupported on Windows).</summary>
    private bool CanAddConnector(string name, bool installed, bool active)
    {
        if (!installed || active)
        {
            return false;
        }

        var catalog = WizardCatalog.Shared(Services);
        var known = ConnectorOnboarding.Offerable(null)
            .Select(o => catalog.Find(o.Alias))
            .Where(static d => d is not null)
            .Cast<WizardDefinition>()
            .ToList();
        var id = ConnectorOnboarding.Normalize(name);
        return ConnectorOnboarding.Offerable(known).Any(o => o.Id == id);
    }

    [RelayCommand]
    private void AddConnector(AgentDiscoveryRow? row)
    {
        if (row is not { CanAdd: true })
        {
            return;
        }

        var argv = ConnectorOnboarding.AddArgv(row.Name);
        var label = ConnectorOnboarding.Label(row.Name);
        Review.Open(
            $"Add {label}?",
            $"Configures DefenseClaw for {label} in observe mode, alongside any connector already configured. Observe records what the agent does and never blocks it.",
            new[]
            {
                new DiscoverStep(argv, $"Add {label} in observe mode.", CommandTier.StateChanging, CliRunner.LongRunningTimeout),
            },
            result => AfterRunAsync(result, argv),
            restartsGateway: CommandReview.RestartsGatewayFor(argv),
            primaryText: $"Add {label}");
    }
}
