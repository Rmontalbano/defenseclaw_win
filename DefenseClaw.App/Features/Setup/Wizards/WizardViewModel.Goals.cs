using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// The "what do you want to do?" page of a wizard that has goals (<see cref="WizardGoals"/>): choosing a goal seeds its presets into the fields
/// and the pages narrow to what it needs. Choosing another one first puts back what the last one seeded, so the answers never carry over from a
/// goal the operator left.
/// </summary>
public sealed partial class WizardViewModel
{
    /// <summary>The fields the chosen goal set (by id) and the value it gave each, so the next choice can put them back to where the wizard opened.</summary>
    private readonly Dictionary<string, string> _seededByGoal = new(StringComparer.Ordinal);

    private WizardFieldViewModel? _goalField;
    private bool _applyingGoal;

    /// <summary>Every choice of the goal page, in order; empty for a wizard without one.</summary>
    internal IReadOnlyList<WizardGoalOptionViewModel> GoalOptions =>
        Steps.Where(s => s.Goals is not null).SelectMany(s => s.Goals!.Options).ToArray();

    /// <summary>
    /// The fields the chosen goal started blank - a judge goal starts the model blank, which is not where config.yaml has it - so the review
    /// compares an edit with the start the operator saw and not with the main model's value
    /// (<see cref="WizardDefinition.DescribeChanges(WizardValues, IReadOnlyDictionary{string, string}?)"/>). A value the goal turned on is a
    /// change from where the wizard opened, and the review says so.
    /// </summary>
    internal IReadOnlyDictionary<string, string> GoalStarts =>
        _seededByGoal.Where(pair => pair.Value.Length == 0).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>The goal the answers have chosen (Advanced counts), or null before one is chosen.</summary>
    public WizardGoal? ChosenGoal => Definition.Goals.FirstOrDefault(g => string.Equals(g.Id, _values[WizardGoals.FieldId], StringComparison.Ordinal));

    private WizardGoalsViewModel? BuildGoals(WizardStep step, IReadOnlyList<WizardFieldViewModel> fields)
    {
        if (Definition.Goals.Count == 0 || !string.Equals(step.Id, WizardGoals.StepId, StringComparison.Ordinal))
        {
            return null;
        }

        var field = fields.FirstOrDefault(f => string.Equals(f.Id, WizardGoals.FieldId, StringComparison.Ordinal));
        if (field is null)
        {
            return null;
        }

        _goalField = field;
        var options = Definition.Goals.Select(goal => new WizardGoalOptionViewModel(goal, field)).ToArray();
        return new WizardGoalsViewModel(
            options,
            WizardGoals.StateSummary(Definition, _services.Config, _services.ConfigLoadError is null));
    }

    /// <summary>
    /// Chooses a goal as if the operator had picked it on the first page (opening a wizard on a goal: see <see cref="WizardPreset"/>). False, and
    /// nothing changes, when the wizard has no goal of that id or the goal cannot be chosen right now.
    /// </summary>
    public bool SelectGoal(string goalId)
    {
        ArgumentNullException.ThrowIfNull(goalId);

        if (_goalField is null || GoalOptions.FirstOrDefault(o => string.Equals(o.Goal.Id, goalId, StringComparison.Ordinal)) is not { IsAvailable: true })
        {
            return false;
        }

        _goalField.Value = goalId;
        return true;
    }

    /// <summary>
    /// The goal field changed: put back what the last goal seeded, then seed the new one. Fields are set without each one reviewing the command;
    /// the caller does that once.
    /// </summary>
    private void ApplyGoal()
    {
        _applyingGoal = true;
        try
        {
            foreach (var id in _seededByGoal.Keys)
            {
                if (_fields.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal)) is { } field)
                {
                    field.Value = field.Field.DefaultValue;
                }
            }

            _seededByGoal.Clear();

            if (ChosenGoal is not { IsAdvanced: false } goal)
            {
                return;
            }

            foreach (var (definitionField, value) in WizardGoals.PresetsFor(goal, Definition.AllFields))
            {
                if (_fields.FirstOrDefault(f => ReferenceEquals(f.Field, definitionField)) is not { } field)
                {
                    continue;
                }

                field.Value = value;
                _seededByGoal[field.Id] = value;
            }
        }
        finally
        {
            _applyingGoal = false;
        }
    }

    /// <summary>
    /// "Step 2 of 6" again: a gate that opened or closed on this page (the provider's cloud pages, the goal's narrowing) changed how many pages
    /// there are. The page itself, and the ones before it, do not move.
    /// </summary>
    private void UpdateProgress()
    {
        if (IsReview || CurrentStep is null)
        {
            return;
        }

        var pages = VisibleSteps;
        var at = pages.ToList().IndexOf(CurrentStep);
        ProgressText = $"Step {(at < 0 ? PageIndex : at) + 1} of {pages.Count + 1}";
        OnPropertyChanged(nameof(WindowAutomationName));
    }
}
