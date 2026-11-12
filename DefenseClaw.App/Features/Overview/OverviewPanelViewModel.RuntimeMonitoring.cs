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

    /// <summary>
    /// What the gateway says about being degraded (<c>ai_runtime.details.degraded_reasons</c>, 1.0.3 on), in its words; empty unless it reports degraded. Information, not
    /// a fault: a gateway that runs as the user sees only its own process's sockets, which is how this app runs it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeMonitoringDegraded))]
    private string _runtimeMonitoringDegradedText = string.Empty;

    /// <summary>True when <see cref="RuntimeMonitoringDegradedText"/> has something to say.</summary>
    public bool HasRuntimeMonitoringDegraded => RuntimeMonitoringDegradedText.Length > 0;

    private const string OwnSocketsReason = "egress attribution is limited to this process's own sockets";

    /// <summary>The reassurance is true only when every reason listed is the own-sockets one; a second, real reason is not "how a gateway that runs as you works".</summary>
    private static string DegradedTextFor(RuntimeHealthView view)
    {
        if (!view.Reported || !view.Degraded)
        {
            return string.Empty;
        }

        if (view.Reasons.Count == 0)
        {
            return "The gateway reports runtime monitoring degraded and gives no reason.";
        }

        var text = "The gateway reports runtime monitoring degraded: " + string.Join("; ", view.Reasons) + ".";
        return view.Reasons.All(static r => r.Contains(OwnSocketsReason, StringComparison.Ordinal))
            ? text + " That is how a gateway that runs as you normally works; this app keeps the gateway unelevated, so nothing needs to change."
            : text;
    }

    private void ApplyRuntimeMonitoring()
    {
        var gate = Services.Runtime.Check(RuntimeCapability.AiRuntime);
        ShowRuntimeMonitoring = gate.IsAvailable;
        if (!gate.IsAvailable)
        {
            RuntimeMonitoringText = string.Empty;
            RuntimeMonitoringDegradedText = string.Empty;
            return;
        }

        var health = _snapshot.Health;
        var view = RuntimeHealthView.From(health);
        var state = RuntimeMonitoringState.Build(
            RuntimeMonitoringConfig.From(Services.Config),
            view,
            health is not null,
            GatewayElevation.Unknown,
            commandLineAudit: null);
        RuntimeMonitoringText = "Runtime monitoring: " + state.Summary;

        // The line is new with 1.0.7 (RuntimeMonitoringDegradedReasons): a 1.0.3 to 1.0.6 gateway also sends degraded_reasons, but the Overview showed nothing for it there.
        RuntimeMonitoringDegradedText = Services.Runtime.Capabilities.Has(RuntimeCapability.RuntimeMonitoringDegradedReasons)
            ? DegradedTextFor(view)
            : string.Empty;
    }

    /// <summary>Opens the Setup hub on the Runtime monitoring card. Changes nothing.</summary>
    [RelayCommand]
    private void OpenRuntimeMonitoring() => RequestNavigation("setup", new RuntimeMonitoringLink());
}
