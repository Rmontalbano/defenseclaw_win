namespace DefenseClaw.App.Services.Wizards;

/// <summary>One line of a <see cref="WizardInfoBlock"/>: a label, what it says, and the tone it is drawn in.</summary>
/// <param name="Label">The left column; empty for a line that continues the one above it (a problem under its pair).</param>
/// <param name="Value">The text.</param>
/// <param name="ToneKey"><c>Neutral</c>, <c>Ok</c> or <c>Warn</c>: the keys the shared tone text style knows.</param>
public sealed record WizardInfoLine(string Label, string Value, string ToneKey = "Neutral");

/// <summary>
/// Read-only facts a wizard shows above its first page (the ACP Guard's current pairs and whether each is healthy, CUST-401). Nothing here is an answer or
/// is sent anywhere; it is what was read when the window opened, with a line on where it came from.
/// </summary>
/// <param name="Title">The card's heading.</param>
/// <param name="Lines">The rows.</param>
/// <param name="Note">The caption under the rows; may be empty.</param>
public sealed record WizardInfoBlock(string Title, IReadOnlyList<WizardInfoLine> Lines, string Note = "");
