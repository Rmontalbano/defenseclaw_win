using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// When the Setup hub offers <c>setup decision-model</c> (CUST-446). DefenseClaw 1.0.6 added the command and this app held its card back
/// (CUST-423) while the feature could still change; 1.0.7 settled it (the <c>--connectors</c> cascade, <see cref="RuntimeCapability.DecisionModelCascade"/>)
/// and 1.0.8 is identical, so the card is offered exactly when the connected runtime has that capability. A runtime that lacks it
/// (1.0.6 and earlier) or one that has not been probed yet gets what it always got: no card, no probe of its help, no search hit. Only the card
/// is concerned: the config editor's read-only "Decision Model" section and the doctor row report what the runtime has, whatever this says.
/// The command palette is not fed by this rule (its registry has no decision-model entry).
/// </summary>
public static class DecisionModelCard
{
    /// <summary>The setup target: <c>defenseclaw setup decision-model</c>.</summary>
    public const string Target = "decision-model";

    /// <summary>The id of the choice that says what to do with <c>--connectors</c>; never a command-line option itself.</summary>
    public const string ConnectorsModeFieldId = "connectors-mode";

    /// <summary>Leave <c>decision_model.hook_connectors</c> as it is: nothing is sent.</summary>
    public const string KeepConnectors = "keep";

    /// <summary>Every connector: <c>--connectors *</c>.</summary>
    public const string AllConnectors = "all";

    /// <summary>The names typed in the box: <c>--connectors a,b</c>.</summary>
    public const string OnlyConnectors = "only";

    /// <summary>Empty the list: <c>--connectors ""</c> (an empty string is how the CLI is told to clear it).</summary>
    public const string ClearConnectors = "clear";

    /// <summary>
    /// The "flag omitted" baseline of the field that clears the list. A text answer is sent only when it differs from its baseline; the answer
    /// for "clear" is the empty string, so its baseline must be something else for the empty string to count as a change to send.
    /// </summary>
    internal const string ClearBaseline = "(the saved list)";

    /// <summary>True when <paramref name="target"/> is the decision-model card and <paramref name="capabilities"/> does not say the runtime has it.</summary>
    public static bool IsHeld(string target, RuntimeCapabilities? capabilities) =>
        string.Equals(target, Target, StringComparison.Ordinal) &&
        !(capabilities?.Has(RuntimeCapability.DecisionModelCascade) ?? false);
}
