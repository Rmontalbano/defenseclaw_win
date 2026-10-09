namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What the current answers make of a wizard: which fields count (they are the command, and the review) and which of those a page shows.
/// <para>
/// The two differ only under a goal (<see cref="WizardGoals"/>). A goal that turns a switch on - "Send to Splunk Observability Cloud" turns
/// <c>--o11y</c> on - needs the switch to count, so the command carries it, without showing the page whose only job is to ask for it. Without a
/// goal, a field counts exactly when its page and its own gate say so, and a page is shown exactly when it has such a field.
/// </para>
/// </summary>
public sealed class WizardVisibility
{
    private readonly HashSet<object> _shown = new(ReferenceEqualityComparer.Instance);

    internal WizardVisibility(IReadOnlyList<WizardField> active)
    {
        ActiveFields = active;
    }

    /// <summary>Fields that count, in definition order.</summary>
    public IReadOnlyList<WizardField> ActiveFields { get; }

    /// <summary>True when a page shows <paramref name="field"/> (which implies it counts).</summary>
    public bool IsShown(WizardField field) => _shown.Contains(field);

    /// <summary>True when the wizard has a page for <paramref name="step"/> under the current answers.</summary>
    public bool IsShown(WizardStep step) => _shown.Contains(step);

    internal void Show(object item) => _shown.Add(item);
}

public sealed partial class WizardDefinition
{
    /// <summary>A copy that checks its answers across fields with <paramref name="validator"/> (null: none); everything else carries over.</summary>
    public WizardDefinition WithCrossValidator(Func<WizardValues, string?>? validator) => With(
        Steps,
        BaselineNote,
        BaselineWarning,
        validator,
        FinalArgvBuilder,
        Goals);

    /// <summary>
    /// What is wrong with the answers taken together, or null: the definition's own check (<see cref="CrossValidator"/>) and then the rules every
    /// definition gets because of the fields it has (a CA bundle and skipping verification, <see cref="WizardTlsRule"/>). Run on Next from the last
    /// page and before the review is trusted.
    /// </summary>
    public string? CrossCheck(WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return CrossValidator?.Invoke(values) is { Length: > 0 } problem ? problem : WizardTlsRule.Check(this, values);
    }

    /// <summary>A copy that turns its answers into a command with <paramref name="builder"/>; everything else carries over.</summary>
    public WizardDefinition WithFinalArgvBuilder(Func<WizardDefinition, WizardValues, IReadOnlyList<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return With(Steps, BaselineNote, BaselineWarning, CrossValidator, builder, Goals);
    }

    /// <summary>A copy that opens on a "what do you want to do?" page with <paramref name="goals"/> (and, as its first page, the one that asks); everything else carries over.</summary>
    public WizardDefinition WithGoals(IReadOnlyList<WizardStep> steps, IReadOnlyList<WizardGoal> goals)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(goals);
        return With(steps, BaselineNote, BaselineWarning, CrossValidator, FinalArgvBuilder, goals);
    }

    /// <summary>
    /// A copy whose pages hold only the fields <paramref name="keep"/> accepts. A field with no flag (a positional, a choice that steers the
    /// wizard) is not a flag and is kept. Used to hold a command to a list of flags whatever the pages show.
    /// </summary>
    public WizardDefinition OnlyFields(Func<WizardField, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);

        var steps = Steps
            .Select(step => new WizardStep
            {
                Id = step.Id,
                Title = step.Title,
                Subtitle = step.Subtitle,
                Fields = step.Fields.Where(keep).ToArray(),
                VisibleWhenFieldId = step.VisibleWhenFieldId,
                VisibleWhenValues = step.VisibleWhenValues,
                Guide = step.Guide,
            })
            .ToArray();
        return With(steps, BaselineNote, BaselineWarning, CrossValidator, FinalArgvBuilder, Goals);
    }

    private WizardDefinition With(
        IReadOnlyList<WizardStep> steps,
        string baselineNote,
        string baselineWarning,
        Func<WizardValues, string?>? validator,
        Func<WizardDefinition, WizardValues, IReadOnlyList<string>> builder,
        IReadOnlyList<WizardGoal> goals) => new()
    {
        Target = Target,
        Title = Title,
        Group = Group,
        Description = Description,
        Steps = steps,
        PlatformStatus = PlatformStatus,
        PlatformNote = PlatformNote,
        IsDetailLoaded = IsDetailLoaded,
        DetailError = DetailError,
        UnavailableReason = UnavailableReason,
        IsCurated = IsCurated,
        HelpText = HelpText,
        FinalArgvBuilder = builder,
        BaselineNote = baselineNote,
        BaselineWarning = baselineWarning,
        CrossValidator = validator,
        Goals = goals,
    };

    /// <summary>The goal the answers have chosen, or null when none is (or the goal is Advanced, which narrows nothing).</summary>
    public WizardGoal? SelectedGoal(WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (Goals.Count == 0)
        {
            return null;
        }

        var id = values[WizardGoals.FieldId];
        var goal = Goals.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));
        return goal is null || goal.IsAdvanced ? null : goal;
    }

    /// <summary>
    /// Works out, from the answers, which fields count and which pages and fields are shown (see <see cref="WizardVisibility"/>): a page and a
    /// field are each gated (<see cref="WizardGate"/>), a chosen goal narrows both, and a page with nothing to show is not a page. One
    /// evaluation, used by the command, the review and the pages alike, so they cannot disagree about what is in play.
    /// </summary>
    public WizardVisibility Evaluate(WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var goal = SelectedGoal(values);
        var active = new List<WizardField>();
        var shown = new List<object>();

        foreach (var step in Steps)
        {
            if (!WizardGate.IsVisible(step.VisibleWhenFieldId, step.VisibleWhenValues, values))
            {
                continue;
            }

            var showsAField = false;
            foreach (var field in step.Fields)
            {
                if (!WizardGate.IsVisible(field.VisibleWhenFieldId, field.VisibleWhenValues, values))
                {
                    continue;
                }

                var shows = goal is null || goal.Shows(step, field);
                if (shows || goal!.Activates(field))
                {
                    active.Add(field);
                }

                if (shows)
                {
                    shown.Add(field);
                    showsAField = true;
                }
            }

            // A page that asks nothing is not a page. One with no fields at all is a page of words (a guide), shown as long as its gate is.
            if (showsAField || step.Fields.Count == 0)
            {
                shown.Add(step);
            }
        }

        var visibility = new WizardVisibility(active);
        foreach (var item in shown)
        {
            visibility.Show(item);
        }

        return visibility;
    }
}
