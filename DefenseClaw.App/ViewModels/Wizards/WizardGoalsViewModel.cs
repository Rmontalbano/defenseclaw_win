using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// One choice on the "what do you want to do?" page. It is bound to the wizard's own goal field, so choosing it is just answering that field: the
/// view-model of the wizard sees the answer change and seeds the goal's presets (<see cref="WizardViewModel.SelectGoal"/>).
/// </summary>
public sealed partial class WizardGoalOptionViewModel : ObservableObject
{
    private readonly WizardFieldViewModel _field;

    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>Why the goal cannot be chosen right now (Docker missing, engine down, still checking), or empty.</summary>
    [ObservableProperty]
    private string _unavailableReason = string.Empty;

    public WizardGoalOptionViewModel(WizardGoal goal, WizardFieldViewModel field)
    {
        Goal = goal ?? throw new ArgumentNullException(nameof(goal));
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _field.PropertyChanged += OnFieldChanged;
    }

    public WizardGoal Goal { get; }

    public string Label => Goal.Label;

    public string Summary => Goal.Summary;

    /// <summary>The name a screen reader gives the choice: the goal, and why it is off when it is.</summary>
    public string AutomationName => IsAvailable ? Goal.Label : Goal.Label + " (not available: " + UnavailableReason + ")";

    public bool RequiresDocker => Goal.Requires == WizardGuideRequirement.Docker;

    public bool HasUnavailableReason => UnavailableReason.Length > 0;

    /// <summary>Two-way target of the radio button. An unavailable goal cannot be chosen.</summary>
    public bool IsSelected
    {
        get => string.Equals(_field.Value, Goal.Id, StringComparison.Ordinal);
        set
        {
            if (!value)
            {
                return;
            }

            if (!IsAvailable)
            {
                // The button is disabled, so this is only a stray write; say the answer is what it was.
                OnPropertyChanged(nameof(IsSelected));
                return;
            }

            _field.Value = Goal.Id;
        }
    }

    /// <summary>The read-only look is running: the goal is shown but cannot be chosen yet.</summary>
    internal void BeginChecking()
    {
        IsAvailable = false;
        UnavailableReason = "Checking for Docker…";
    }

    /// <summary>Applies what the read-only Docker look found. A goal that was chosen and can no longer be is cleared, so the pages never follow a choice that cannot run.</summary>
    internal void ApplyDocker(DockerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (status.AllowsLocalSplunk)
        {
            IsAvailable = true;
            UnavailableReason = string.Empty;
            return;
        }

        IsAvailable = false;
        UnavailableReason = status.Summary + " Local Splunk runs in Docker: install or start Docker Desktop, then check again.";
        if (IsSelected)
        {
            _field.Value = string.Empty;
        }
    }

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(AutomationName));

    partial void OnUnavailableReasonChanged(string value)
    {
        OnPropertyChanged(nameof(HasUnavailableReason));
        OnPropertyChanged(nameof(AutomationName));
    }

    private void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(WizardFieldViewModel.Value), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(IsSelected));
        }
    }
}

/// <summary>The choices of the goal page, and the sentence that says where things stand today.</summary>
public sealed class WizardGoalsViewModel
{
    public WizardGoalsViewModel(IReadOnlyList<WizardGoalOptionViewModel> options, string stateSummary)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        StateSummary = stateSummary ?? string.Empty;
    }

    public IReadOnlyList<WizardGoalOptionViewModel> Options { get; }

    /// <summary>"Main: anthropic/claude-sonnet-4-5 · Judge: not set": what the wizard is about to change, from config.yaml. Empty when there is nothing useful to say.</summary>
    public string StateSummary { get; }

    public bool HasStateSummary => StateSummary.Length > 0;

    public string Intro => "Pick what you want to do. The pages that follow show only what it needs; Advanced shows every setting this command has.";

    /// <summary>True when a goal has been chosen.</summary>
    public bool HasSelection => Options.Any(o => o.IsSelected);
}
