using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Events view's "Actionable only" switch (CUST-262). Alerts stays the findings queue; the 0.8.10 TUI's other half - its Alerts panel over the v8
/// history buckets (<c>guardrail.evaluation</c>, <c>enforcement.action</c>, <c>network.egress</c>, <c>asset.scan</c>, <c>platform.health</c>,
/// <c>diagnostic</c>, <c>security.finding</c>) with the low-signal rows hidden until asked for - is this stream, which reads every one of those buckets
/// (<see cref="DefenseClaw.Core.Audit.EventStreamReader.AlertBuckets"/>) and every other canonical bucket but telemetry.
/// <para>
/// <b>Default on, as the TUI's is.</b> Events open on the rows <see cref="DefenseClaw.Core.Audit.ActionableRule"/> calls signal (HIGH, CRITICAL and
/// ERROR, or a block, denial, rejection, quarantine, failure, error or panic in the row's text); the switch shows everything, and the chip beside it
/// says how many rows the view is leaving out. The rule is one pass over the rows already read, decided when each row was projected
/// (<see cref="LogEntry.IsActionable"/>), so the switch costs no query.
/// </para>
/// <para>
/// <b>Off while the operator is looking for something.</b> The TUI turns the actionable view off for good the moment a search or a severity is chosen;
/// here the choice is kept and merely suspended <em>while</em> such a filter is on (<see cref="ActionableSuspendedBy"/>), so clearing the search brings
/// the actionable view back. A preset other than the two that hide nothing of their own (<c>all</c>, <c>no-noise</c>), a severity floor, an action or event
/// picker and the telemetry switch are all the operator asking for something specific, and each suspends it - the Overview's Hook Calls tile, which opens
/// the <c>hooks</c> preset on Events, would otherwise land on a list with no hook in it.
/// </para>
/// </summary>
public sealed partial class LogsPanelViewModel
{
    /// <summary>The Events view opens on the actionable rows, as the TUI's default view does.</summary>
    public const bool ActionableByDefault = true;

    /// <summary>The operator's choice: show only the actionable events. Kept when a filter suspends it (see <see cref="ActionableSuspendedBy"/>).</summary>
    [ObservableProperty]
    private bool _actionableOnly = ActionableByDefault;

    /// <summary>Events the view leaves out: those that pass every other filter and are low-signal. Zero unless <see cref="IsActionableApplied"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionableHidden))]
    [NotifyPropertyChangedFor(nameof(ActionableHiddenText))]
    private int _actionableHidden;

    /// <summary>
    /// What is suspending the actionable view, as the end of "paused while ..." - or null when nothing is. A search, a severity floor, a preset that is
    /// not <c>all</c> or <c>no-noise</c>, an action or event picker, or telemetry.
    /// </summary>
    public string? ActionableSuspendedBy
    {
        get
        {
            if (FilterText.Trim().Length > 0)
            {
                return "a search is on";
            }

            if (!string.Equals(SelectedSeverity, LogPresets.AnySeverity, StringComparison.Ordinal))
            {
                return "a minimum severity is set";
            }

            if (SelectedPreset is not (LogPresets.All or LogPresets.NoNoise))
            {
                return string.Create(CultureInfo.InvariantCulture, $"the {SelectedPreset} preset is chosen");
            }

            if (!string.Equals(SelectedAction, "all", StringComparison.Ordinal) || !string.Equals(SelectedEvent, "all", StringComparison.Ordinal))
            {
                return "an action or event filter is set";
            }

            return IncludeTelemetry ? "telemetry is included" : null;
        }
    }

    /// <summary>The switch can be changed: no filter is suspending it.</summary>
    public bool CanChangeActionable => ActionableSuspendedBy is null;

    /// <summary>True when the rows on screen are narrowed to the actionable ones: on Events, switched on, and not suspended.</summary>
    public bool IsActionableApplied => IsEventsSource && ActionableOnly && ActionableSuspendedBy is null;

    public bool HasActionableHidden => ActionableHidden > 0;

    /// <summary>"12 low-signal hidden": the chip beside the switch; empty when nothing is hidden.</summary>
    public string ActionableHiddenText => ActionableHidden > 0
        ? string.Create(CultureInfo.CurrentCulture, $"{ActionableHidden:N0} low-signal hidden")
        : string.Empty;

    /// <summary>What the switch says when pointed at: what the view shows, or why it is paused.</summary>
    public string ActionableToolTip => ActionableSuspendedBy is { } why
        ? $"Actionable only is paused while {why}. Clear it to bring the actionable view back."
        : "Show what the DefenseClaw TUI shows by default: HIGH, CRITICAL and ERROR events, and any event that blocked, denied, rejected, quarantined or failed. " +
          "Turn it off to see every event.";

    /// <summary>The state the switch, its tooltip and the chip depend on changed (a filter, the stream, the choice itself).</summary>
    private void RaiseActionableState()
    {
        OnPropertyChanged(nameof(ActionableSuspendedBy));
        OnPropertyChanged(nameof(CanChangeActionable));
        OnPropertyChanged(nameof(IsActionableApplied));
        OnPropertyChanged(nameof(ActionableToolTip));
    }

    partial void OnActionableOnlyChanged(bool value) => AfterSourceOrFilterChanged();
}
