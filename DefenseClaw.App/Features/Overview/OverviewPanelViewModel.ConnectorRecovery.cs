using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "Finish the interrupted change" (DefenseClaw 1.0.9 and later): the review of <c>defenseclaw-gateway connector recover-config --data-dir &lt;data dir&gt;</c>,
/// offered on the stuck-pending-write row in place of the move-aside, which 1.0.9's installer says not to do (it says not to remove or replay the receipts).
/// A DefenseClaw command, run through the shared review like any other; the gateway may keep running.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private void FinishInterruptedChange()
    {
        var argv = ConnectorRecovery.Argv(Services.Paths.DataDirectory);

        Review.Open(
            ConnectorRecovery.ActionLabel + "?",
            ConnectorRecovery.RunsWhat + " " + ConnectorRecovery.SuccessText + " " + ConnectorRecovery.FailureText + " " + ConnectorRecovery.GatewaySentence,
            new[]
            {
                new DiscoverStep(
                    argv,
                    "Finish the interrupted agent config change in the data folder.",
                    CommandTier.StateChanging,
                    Executable: GatewayControl.Executable),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            primaryText: ConnectorRecovery.ActionLabel);
    }
}
