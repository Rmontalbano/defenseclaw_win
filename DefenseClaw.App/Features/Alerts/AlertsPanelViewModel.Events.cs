using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The way from Alerts to the rest of the history (CUST-262). Alerts is the Mac's definition - unacknowledged findings, and nothing else - and stays
/// that. The 0.8.10 TUI's Alerts panel also lists the v8 history buckets (guardrail evaluations, enforcement, egress decisions, scans, platform health,
/// diagnostics) with the low-signal rows hidden; here that is the Logs panel's <b>Events</b> view, and this is the button that opens it.
/// </summary>
public sealed partial class AlertsPanelViewModel
{
    /// <summary>
    /// "Events (all activity)": shows the Logs panel on its Events stream, from a clean slate and on the actionable view (a <see cref="LogsEvents"/>
    /// request). Changes nothing and reads nothing here: the findings queue, its filters and its selection are as they were when the operator comes back.
    /// </summary>
    [RelayCommand]
    private void OpenEvents() => RequestNavigation("logs", new LogsEvents());
}
