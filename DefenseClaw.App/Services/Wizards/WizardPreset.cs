using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Where a wizard opens: on the page of one subcommand (a group wizard: <c>setup observability</c>, <c>setup webhook</c>, <c>setup trusted-paths</c>)
/// with its first argument filled in, or on the page after the one that asks "what do you want to do?" (a wizard with goals: <see cref="WizardGoals"/>)
/// with that goal chosen. The Setup editors' Add opens the wizard on <c>add</c> rather than on the page that asks which subcommand to run, the
/// Trusted Paths editor can hand it the directory a failed connector setup named, and a screen that already knows what the operator is after - a judge
/// that is not configured - can open the LLM wizard on that goal.
/// <para>
/// Only answers go in: the wizard still ends on its review page, which shows the exact command, and nothing runs until it is executed.
/// A wizard whose first page has no such subcommand or goal (a CLI that dropped it) opens as it always did.
/// </para>
/// </summary>
/// <param name="Subcommand">The subcommand to start on (<c>add</c>); empty for a preset that is only a goal.</param>
/// <param name="FirstArgument">The value of that subcommand's first positional (the directory of <c>trusted-paths add</c>); null leaves it as the wizard has it.</param>
/// <param name="Goal">The id of the goal to choose (<c>judge</c>), or null.</param>
public sealed record WizardPreset(string Subcommand, string? FirstArgument = null, string? Goal = null)
{
    /// <summary>A preset that opens a wizard with goals on the page after the question, with <paramref name="goal"/> chosen.</summary>
    public static WizardPreset ForGoal(string goal)
    {
        ArgumentException.ThrowIfNullOrEmpty(goal);
        return new WizardPreset(string.Empty, null, goal);
    }

    /// <summary>
    /// Sets the answers and moves past the first page. Returns false, and leaves the wizard alone, when the wizard has no command page that offers
    /// <see cref="Subcommand"/>, or (for a goal-only preset) no goal of that id that can be chosen.
    /// </summary>
    internal bool ApplyTo(WizardViewModel wizard)
    {
        ArgumentNullException.ThrowIfNull(wizard);

        if (Subcommand.Length == 0)
        {
            if (Goal is not { Length: > 0 } || !wizard.SelectGoal(Goal))
            {
                return false;
            }

            wizard.Next();
            return true;
        }

        var fields = wizard.Steps.SelectMany(static s => s.Fields).ToArray();
        if (fields.FirstOrDefault(static f => string.Equals(f.Id, "subcommand", StringComparison.Ordinal)) is not { } command ||
            !command.Choices.Any(c => string.Equals(c.Value, Subcommand, StringComparison.Ordinal)))
        {
            return false;
        }

        command.Value = Subcommand;

        if (FirstArgument is { Length: > 0 } value &&
            fields.FirstOrDefault(f =>
                f.Field.IsPositional &&
                f.Field.PositionalOrder == 1 &&
                string.Equals(f.Field.VisibleWhenFieldId, "subcommand", StringComparison.Ordinal) &&
                f.Field.VisibleWhenValues.Contains(Subcommand, StringComparer.Ordinal)) is { } argument)
        {
            argument.Value = value;
        }

        wizard.Next();
        return true;
    }
}
