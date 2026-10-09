using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Audit panel's "Actionable only" switch (CUST-262): the 0.8.10 TUI's Audit panel hides its low-signal rows by default, and so does this one.
/// <para>
/// <b>The rule is the TUI's, shared.</b> A row is kept when <see cref="ActionableRule"/> says so: HIGH, CRITICAL or ERROR, or a block, denial, rejection,
/// quarantine, failure, error or panic in its action, target, actor, details or run id. It is applied in <see cref="ActionableAuditPaging"/>, to the rows of
/// the ordinary indexed page, because the words it reads live in <c>details</c> and no cheap SQL says the same thing; a call reads up to twenty pages to
/// fill one, so a window that is mostly noise still opens on a screenful of what matters.
/// </para>
/// <para>
/// <b>On by default, as the TUI's is; off while the operator is looking for something.</b> The TUI turns it off for good once a search or a preset is
/// chosen. Here the choice is kept and merely suspended <em>while</em> something specific is asked for (<see cref="ActionableSuspendedBy"/>): a search, an
/// action filter, one run, a preset other than All, or a minimum severity. Clear it and the actionable view is back. The Overview's Blocks tile opens the
/// <c>blocks</c> preset, so it lands on every block, not on the blocks that also pass the rule. The switch is a view setting beside the filters, not one of
/// them: Reset filters and a switch between Live and Archive leave it as the operator set it.
/// </para>
/// <para>
/// <b>What it costs and what it says.</b> It adds no query of its own: the pages are the ones the panel always read, and the total (which counts every
/// event in the window, low-signal or not) is not asked for while the view is narrowed - the caption says how many actionable events were loaded and how
/// many low-signal ones were left out of the pages read (<see cref="HiddenCount"/>), and the chip beside the switch repeats the second number.
/// </para>
/// </summary>
public sealed partial class AuditPanelViewModel
{
    /// <summary>The panel opens on the actionable events, as the TUI's Audit panel does.</summary>
    public const bool ActionableByDefault = true;

    /// <summary>The operator's choice: list only the actionable events. Kept when a filter suspends it (see <see cref="ActionableSuspendedBy"/>).</summary>
    [ObservableProperty]
    private bool _actionableOnly = ActionableByDefault;

    /// <summary>
    /// Low-signal events read and left out of the list: those of the pages the list was built from (and of the rows a live refresh added). Zero when the
    /// list is not narrowed. Not a count of the window - "Load more" reads further back, and the number grows with it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden))]
    [NotifyPropertyChangedFor(nameof(HiddenText))]
    private int _hiddenCount;

    /// <summary>
    /// What is suspending the actionable view, as the end of "paused while ..." - or null when nothing is: a text search, an action filter, one run's
    /// events, a preset other than All, or a minimum severity (the places the operator asks for something specific; the TUI's search and preset turn
    /// its filter off, and the others are the same request in this panel's other words).
    /// </summary>
    public string? ActionableSuspendedBy
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                return "a search is on";
            }

            if (!string.IsNullOrWhiteSpace(ActionFilter))
            {
                return "an action filter is set";
            }

            if (!string.IsNullOrWhiteSpace(RunFilter))
            {
                return "one run's events are shown";
            }

            if (!string.Equals(ActivePreset, PresetAll, StringComparison.Ordinal))
            {
                return string.Create(CultureInfo.InvariantCulture, $"the {ActivePreset} view is chosen");
            }

            return SelectedSeverity == SeverityOption.Any ? null : "a minimum severity is set";
        }
    }

    /// <summary>The switch can be changed: nothing is suspending it.</summary>
    public bool CanChangeActionable => ActionableSuspendedBy is null;

    /// <summary>True when the list is narrowed to the actionable events: the switch is on and nothing suspends it.</summary>
    public bool IsActionableApplied => ActionableOnly && ActionableSuspendedBy is null;

    public bool HasHidden => HiddenCount > 0;

    /// <summary>"63 low-signal hidden": the chip beside the switch; empty when nothing is left out.</summary>
    public string HiddenText => HiddenCount > 0
        ? string.Create(CultureInfo.CurrentCulture, $"{HiddenCount:N0} low-signal hidden")
        : string.Empty;

    /// <summary>What the switch says when pointed at: what the list shows, or why it is paused.</summary>
    public string ActionableToolTip => ActionableSuspendedBy is { } why
        ? $"Actionable only is paused while {why}. Clear it to bring the actionable view back."
        : "Show what the DefenseClaw TUI shows by default: HIGH, CRITICAL and ERROR events, and any event that blocked, denied, rejected, quarantined or failed. " +
          "Turn it off to see every event in the window.";

    /// <summary>A filter, or the choice itself, changed: the switch, its tooltip and the state of the list may all have moved.</summary>
    private void RaiseActionableState()
    {
        OnPropertyChanged(nameof(ActionableSuspendedBy));
        OnPropertyChanged(nameof(CanChangeActionable));
        OnPropertyChanged(nameof(IsActionableApplied));
        OnPropertyChanged(nameof(ActionableToolTip));
    }

    /// <summary>True once the panel has started a load. Until then there is no list to redo: the first load reads the switch as it is.</summary>
    private bool _loadRequested;

    partial void OnActionableOnlyChanged(bool value)
    {
        if (_loadRequested)
        {
            Reload();
        }
        else
        {
            RaiseActionableState();
        }
    }

    /// <summary>
    /// The caption over the list (the toolbar's): how many events, of what kind, from which window. An ordinary list says how many of how many match (the
    /// SQL total); a narrowed one cannot - the total counts every event, low-signal or not - so it says how many actionable events it holds and how many
    /// it left out. A "+" says the window goes on past what was read.
    /// </summary>
    private string Summarize(int? total, bool platformOnly, bool actionable, string range)
    {
        var count = Rows.Count.ToString("N0", CultureInfo.CurrentCulture);
        var more = HasMore || IsRowCapReached;

        if (actionable)
        {
            var what = platformOnly ? "actionable platform row(s)" : Rows.Count == 1 ? "actionable event" : "actionable events";
            var left = HiddenCount > 0 ? string.Create(CultureInfo.CurrentCulture, $" · {HiddenCount:N0} low-signal hidden") : string.Empty;
            return $"{count}{(more ? "+" : string.Empty)} {what}{(more ? " loaded" : string.Empty)}{left} · {range}";
        }

        return total is { } counted
            ? $"{count} of {counted.ToString("N0", CultureInfo.CurrentCulture)} matching events · {range}"
            : platformOnly
                ? $"{count} platform row(s) loaded · {range}"
                : $"{count}{(more ? "+" : string.Empty)} matching events loaded · {range}";
    }

    /// <summary>What the empty list says. A narrowed list that is empty says what it is hiding and where the switch is, never just "no matching events".</summary>
    private void SetEmptyText(bool actionable)
    {
        EmptyTitle = "No matching events";
        EmptyDetail = SelectedConnector.PlatformOnly
            ? "No platform-scoped rows in this window. Platform rows are the ones with no connector attribution."
            : "Widen the time range, lower the minimum severity, or clear the action filter.";

        if (!actionable)
        {
            return;
        }

        EmptyTitle = "No actionable events";
        var hidden = HiddenCount.ToString("N0", CultureInfo.CurrentCulture);
        EmptyDetail = HasMore
            ? $"None among the newest {hidden} events. Load more to look further back, or turn off \"Actionable only\" to see them all."
            : HiddenCount > 0
                ? $"{hidden} low-signal {(HiddenCount == 1 ? "event is" : "events are")} hidden. Turn off \"Actionable only\" to see {(HiddenCount == 1 ? "it" : "them")}."
                : "Widen the time range, or clear a filter.";
    }
}
