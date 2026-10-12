using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Runtime monitoring row of the Discovered AI agents card (CUST-348): one line of state - <c>off</c>, or <c>on · A running · B running · C off</c> -
/// from config.yaml's <c>ai_discovery.runtime</c> and the gateway's <c>/health</c> <c>ai_runtime</c> block, and a button to the Setup hub's Runtime monitoring card. Drawn only
/// while the runtime has the planes (DefenseClaw 1.0); on 0.8.10 the row does not exist. Re-derived with the quick actions on every poll; no I/O, and nothing is
/// run from here.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    [ObservableProperty]
    private bool _showRuntimeMonitoring;

    /// <summary><c>Runtime monitoring: on · A running · B running · C off</c>.</summary>
    [ObservableProperty]
    private string _runtimeMonitoringText = string.Empty;

    private void ApplyRuntimeMonitoring()
    {
        var gate = Services.Runtime.Check(RuntimeCapability.AiRuntime);
        ShowRuntimeMonitoring = gate.IsAvailable;
        if (!gate.IsAvailable)
        {
            RuntimeMonitoringText = string.Empty;
            return;
        }

        var health = _snapshot.Health;
        var state = RuntimeMonitoringState.Build(
            RuntimeMonitoringConfig.From(Services.Config),
            RuntimeHealthView.From(health),
            health is not null,
            GatewayElevation.Unknown,
            commandLineAudit: null);
        RuntimeMonitoringText = "Runtime monitoring: " + state.Summary;
    }

    /// <summary>Opens the Setup hub on the Runtime monitoring card. Changes nothing.</summary>
    [RelayCommand]
    private void OpenRuntimeMonitoring() => RequestNavigation("setup", new RuntimeMonitoringLink());
}
