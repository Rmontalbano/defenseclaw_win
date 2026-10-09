using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>Answering a wizard definition without a window: the answers the window starts with, changed, and what they make of the wizard.</summary>
internal static class WizardAnswers
{
    /// <summary>The answers a wizard starts with (every field at its default), then <paramref name="changes"/>, by field id.</summary>
    public static WizardValues Start(WizardDefinition definition, params (string Id, string Value)[] changes)
    {
        var values = WizardSamples.StartingValues(definition);
        foreach (var (id, value) in changes)
        {
            values[id] = value;
        }

        return values;
    }

    /// <summary>The ids of the pages the answers show, in order.</summary>
    public static string[] Pages(WizardDefinition definition, WizardValues values)
    {
        var evaluation = definition.Evaluate(values);
        return definition.Steps.Where(step => evaluation.IsShown(step)).Select(step => step.Id).ToArray();
    }

    /// <summary>The flags of the fields that count under the answers (the command is built from these).</summary>
    public static string[] ActiveFlags(WizardDefinition definition, WizardValues values) =>
        definition.VisibleFields(values).Where(f => f.Flag is not null).Select(f => f.Flag!).ToArray();

    /// <summary>The ids of the fields a page shows under the answers.</summary>
    public static string[] Shown(WizardDefinition definition, WizardValues values, string stepId)
    {
        var evaluation = definition.Evaluate(values);
        return definition.Steps.Single(s => s.Id == stepId).Fields.Where(f => evaluation.IsShown(f)).Select(f => f.Id).ToArray();
    }

    /// <summary>The field with <paramref name="flag"/> (the first, when a flag is offered more than once).</summary>
    public static WizardField Field(WizardDefinition definition, string flag) =>
        definition.AllFields.First(f => f.Flag == flag);

    /// <summary>The field with this id.</summary>
    public static WizardField ById(WizardDefinition definition, string id) =>
        definition.AllFields.Single(f => f.Id == id);
}
