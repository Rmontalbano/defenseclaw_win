using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Policy.Model;

namespace DefenseClaw.App.ViewModels;

/// <summary>One entry of the Policies panel's navigation list: a view, its title and its count. Created once per view and updated in place, so the list keeps its selection when the counts change.</summary>
public sealed partial class PolicyNavItemViewModel : ObservableObject
{
    public PolicyNavItemViewModel(string view, string title)
    {
        View = view;
        Title = title;
    }

    /// <summary>The view's id (<c>posture</c>, <c>optin</c> ...).</summary>
    public string View { get; }

    public string Title { get; }

    /// <summary>A count (<c>26</c>, <c>0/5</c>); empty when there is nothing to count.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge), nameof(AutomationText))]
    private string _badge = string.Empty;

    public bool HasBadge => Badge.Length > 0;

    /// <summary>What a screen reader says: <c>Chains, 26</c>.</summary>
    public string AutomationText => HasBadge ? $"{Title}, {Badge}" : Title;

    public override string ToString() => Title;
}

/// <summary>One line of a row's detail in the inspector. A blank line is spacing.</summary>
public sealed record PolicyInspectorLine(string Text)
{
    public bool IsBlank => Text.Length == 0;

    /// <summary>Lines the runtime indents (the rule list, the severity matrix) keep their columns, in the monospace face.</summary>
    public bool IsIndented => Text.StartsWith("  ", StringComparison.Ordinal);
}

/// <summary>
/// One button of the inspector: a choice the runtime's model offers for the selected row (<see cref="PolicyAction"/>), with whether it can be
/// used right now and the sentence that says so when it cannot. Nothing runs from here: <see cref="RunCommand"/> asks the panel to prepare
/// the action, which ends in a review of the exact command.
/// </summary>
public sealed partial class PolicyActionViewModel : ObservableObject
{
    private readonly PolicyModelViewModel _owner;

    public PolicyActionViewModel(PolicyModelViewModel owner, PolicyAction action)
    {
        _owner = owner;
        Action = action;
    }

    public PolicyAction Action { get; }

    public string Title => Action.Title;

    public string Group => Action.Group;

    /// <summary>True when the choice is already in force (the button shows it as the current one).</summary>
    public bool IsCurrent => Action.IsCurrent;

    /// <summary>True when the choice protects less: its review asks for an acknowledgement, and its button says so.</summary>
    public bool Weakens => Action.Weakens;

    public bool IsRead => Action.IsRead;

    /// <summary>The button can be used now (the data is a complete, recent read, nothing else is running, and the choice is not already in force).</summary>
    public bool IsEnabled => _owner.ActionBlockedReason(Action) is null;

    /// <summary>Why it cannot be used, else what it does.</summary>
    public string ToolTip => _owner.ActionBlockedReason(Action) ?? Describe();

    /// <summary>What a screen reader says: <c>Tool-call block level: HIGH+, reduces protection</c>.</summary>
    public string AutomationName => $"{Action.Group}: {Action.Title}{(Action.IsCurrent ? " (current)" : string.Empty)}{(Action.Weakens ? ", reduces protection" : string.Empty)}";

    public IRelayCommand RunCommand => _owner.RunActionCommand;

    /// <summary>Asks the bindings again whether the button is usable (the panel's trust or busy state changed).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ToolTip));
    }

    private string Describe()
    {
        var summary = Action.Consequence.Summary;
        return Action.Weakens ? $"Reduces protection. {summary}" : summary;
    }

    public override string ToString() => $"{Action.Group}: {Action.Title}";
}

/// <summary>The choices of one group in the inspector (<c>Tool-call block level</c>), in the order the model lists them.</summary>
public sealed record PolicyActionGroupViewModel(string Name, IReadOnlyList<PolicyActionViewModel> Actions)
{
    /// <summary>True for the group of read-only checks (validating a rule pack), drawn after the changes.</summary>
    public bool IsRead => Actions.Count > 0 && Actions[0].IsRead;
}
