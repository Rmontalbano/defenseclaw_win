namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// One answer to "what do you want to do?" - the TUI's goal-first entry point in front of a setup wizard (<c>WizardGoal</c> in its
/// <c>tui/panels/setup.py</c>). Choosing one seeds some answers (<see cref="Presets"/>) and narrows the wizard to the fields
/// (<see cref="Flags"/>) and pages (<see cref="Pages"/>) that matter for it, so "change my judge model" is two pages and not the whole form.
/// <para>
/// It is data over the fields the wizard already has. Every flag a goal names is looked up in the live steps (<see cref="WizardGoals.Apply"/>):
/// one the installed CLI does not have is dropped, a preset whose value the control cannot hold is dropped, and a goal left with nothing to show is
/// dropped - a goal cannot put a flag into a command that the CLI does not have.
/// </para>
/// </summary>
public sealed class WizardGoal
{
    private HashSet<string>? _flags;
    private HashSet<string>? _pages;
    private HashSet<string>? _presetKeys;

    public required string Id { get; init; }

    /// <summary>The words on the choice: "Change my main model".</summary>
    public required string Label { get; init; }

    /// <summary>One sentence under the label.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Answers the goal sets, keyed by flag (<c>--role</c>) or, for a choice that is not a flag, by field id (<c>scope</c>). A value is a field's own
    /// value (<see cref="ToggleValues"/> for a switch, a choice's value, text); an empty one blanks the field ("leave unchanged").
    /// </summary>
    public IReadOnlyDictionary<string, string> Presets { get; init; } = new Dictionary<string, string>();

    /// <summary>Flags whose fields the goal shows.</summary>
    public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();

    /// <summary>Pages (by step id) the goal shows whole; a page still appears only while its own gate holds.</summary>
    public IReadOnlyList<string> Pages { get; init; } = Array.Empty<string>();

    /// <summary>
    /// What the goal is about: flags and pages of which the installed CLI must have at least one for the goal to be offered at all. "Test my
    /// connection" is about <c>--ping</c>; a CLI without it has nothing to test with, and a menu entry that is only a provider and a model is not
    /// that. Empty: the goal stays while any of its flags or pages does.
    /// </summary>
    public IReadOnlyList<string> Needs { get; init; } = Array.Empty<string>();

    /// <summary>What the goal needs from this machine; a goal that needs Docker is offered off, with the reason, until a read-only look says it can.</summary>
    public WizardGuideRequirement Requires { get; init; }

    /// <summary>The "show me everything" escape hatch: it narrows nothing.</summary>
    public bool IsAdvanced => string.Equals(Id, WizardGoals.AdvancedId, StringComparison.Ordinal);

    /// <summary>True when a page shows <paramref name="field"/> under this goal.</summary>
    internal bool Shows(WizardStep step, WizardField field)
    {
        if (IsAdvanced || field.IsSynthetic || field.IsPositional || field.IsRequired)
        {
            return true;
        }

        // "Do not prompt me" is on whatever the goal and is not a decision: it counts (Activates) and is not asked.
        if (WizardFieldBuilder.IsNonInteractiveFlag(field.Flag))
        {
            return false;
        }

        _pages ??= new HashSet<string>(Pages, StringComparer.Ordinal);
        if (_pages.Contains(step.Id))
        {
            return true;
        }

        _flags ??= new HashSet<string>(Flags, StringComparer.Ordinal);
        return field.Flag is { Length: > 0 } flag && _flags.Contains(flag);
    }

    /// <summary>
    /// True when a field no page shows still counts under this goal: the switch a preset turned on, and the no-prompt switch. Without it the
    /// preset would be an answer that never reached the command.
    /// </summary>
    internal bool Activates(WizardField field)
    {
        if (WizardFieldBuilder.IsNonInteractiveFlag(field.Flag))
        {
            return true;
        }

        _presetKeys ??= new HashSet<string>(Presets.Keys, StringComparer.Ordinal);
        return (field.Flag is { Length: > 0 } flag && _presetKeys.Contains(flag)) || _presetKeys.Contains(field.Id);
    }
}
