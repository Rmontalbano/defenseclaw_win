using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The hook connectors' fail-mode question on a 1.0.4 per-user host (CUST-379).
/// <para>
/// There the runtime reports a gateway-outage mode from the connector's policy tier (<c>closed</c> for the strict rule pack, <c>open</c> otherwise) and does not apply a
/// saved per-user <c>hook_fail_mode</c> (<see cref="HookFailModeRule"/>): <c>init --fail-mode open</c> is refused outright, and <c>setup &lt;connector&gt; --fail-mode</c>
/// only saves a value nothing reads. So the question is not asked; the page says what decides the behaviour and names the command that changes it
/// (<see cref="HookFailModeRule.StrictPackCommand"/>). On an older runtime, or on a managed install (the administrator's value is explicit), the definition is returned as it is.
/// </para>
/// </summary>
public static class WizardFailModeRule
{
    /// <summary>The sentence a page that lost its fail-mode field says instead.</summary>
    public const string Note =
        "Hook failure mode is not asked on this runtime: during a gateway outage a hook follows the policy tier of its rule pack (strict blocks, default and permissive " +
        "let the call run with a warning), and a saved fail mode is not applied. To block calls during an outage, run " + HookFailModeRule.StrictPackCommand + ".";

    private const string FailModeFlag = "--fail-mode";

    /// <summary>
    /// <paramref name="definition"/> without its <c>--fail-mode</c> field when <paramref name="savedValueIgnored"/> (see
    /// <see cref="HookFailModeRule.SavedValueIgnored"/>); the page that held it carries <see cref="Note"/>. Unchanged for a definition without that field.
    /// </summary>
    public static WizardDefinition Apply(WizardDefinition definition, bool savedValueIgnored)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!savedValueIgnored || !definition.AllFields.Any(IsFailMode))
        {
            return definition;
        }

        var steps = definition.Steps
            .Select(step => step.Fields.Any(IsFailMode)
                ? new WizardStep
                {
                    Id = step.Id,
                    Title = step.Title,
                    Subtitle = (step.Subtitle.Length > 0 ? step.Subtitle + " " : string.Empty) + Note,
                    Fields = step.Fields.Where(f => !IsFailMode(f)).ToArray(),
                    VisibleWhenFieldId = step.VisibleWhenFieldId,
                    VisibleWhenValues = step.VisibleWhenValues,
                    Guide = step.Guide,
                }
                : step)
            .ToArray();

        return definition.With(steps, definition.BaselineNote, definition.BaselineWarning);
    }

    private static bool IsFailMode(WizardField field) => string.Equals(field.Flag, FailModeFlag, StringComparison.Ordinal);
}
