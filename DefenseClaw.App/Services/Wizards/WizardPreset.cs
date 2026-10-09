using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Where a wizard for a group target (<c>setup observability</c>, <c>setup webhook</c>, <c>setup trusted-paths</c>) opens: on the page of one
/// subcommand, with its first argument filled in. The Setup editors' Add opens the wizard this way, so it starts on <c>add</c> rather than on
/// the page that asks which subcommand to run, and the Trusted Paths editor can hand it the directory a failed connector setup named.
/// <para>
/// Only answers go in: the wizard still ends on its review page, which shows the exact command, and nothing runs until it is executed.
/// A wizard whose command page has no such subcommand (a CLI that dropped it) opens as it always did.
/// </para>
/// </summary>
/// <param name="Subcommand">The subcommand to start on (<c>add</c>).</param>
/// <param name="FirstArgument">The value of that subcommand's first positional (the directory of <c>trusted-paths add</c>); null leaves it as the wizard has it.</param>
public sealed record WizardPreset(string Subcommand, string? FirstArgument = null)
{
    /// <summary>
    /// Sets the answers and moves past the command page. Returns false, and leaves the wizard alone, when the wizard has no command page or
    /// that page does not offer <see cref="Subcommand"/>.
    /// </summary>
    internal bool ApplyTo(WizardViewModel wizard)
    {
        ArgumentNullException.ThrowIfNull(wizard);

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
