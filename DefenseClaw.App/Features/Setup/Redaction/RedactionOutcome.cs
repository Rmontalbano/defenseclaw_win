using DefenseClaw.Core.Redaction;

namespace DefenseClaw.App.ViewModels.Redaction;

/// <summary>What the result card is about.</summary>
public enum RedactionOutcomeKind
{
    /// <summary>A dry run: the difference a change would make. Nothing was written.</summary>
    Preview,

    /// <summary>A change that was applied (and, if the CLI could, checked).</summary>
    Applied,

    /// <summary>A read operation's answer: a list, a profile, a destination.</summary>
    Read,

    /// <summary>A command that did not finish, or printed something this app could not read.</summary>
    Failed,
}

/// <summary>
/// The result card: one run, in words. Immutable once built. A preview's <see cref="Diff"/> is the readable form of the CLI's
/// <c>changes</c>; an apply's <see cref="Notes"/> carry the backup and whether the written plan was the one shown; a failure keeps the
/// CLI's own last lines in <see cref="Raw"/>.
/// </summary>
public sealed class RedactionOutcomeViewModel
{
    public required RedactionOutcomeKind Kind { get; init; }

    /// <summary>"Preview: Apply profile everywhere".</summary>
    public required string Heading { get; init; }

    /// <summary>The badge beside the heading: "Dry run, nothing written", "Applied and verified", "Failed".</summary>
    public required string Badge { get; init; }

    /// <summary>Ok / Warn / Bad / Neutral.</summary>
    public string Tone { get; init; } = "Neutral";

    /// <summary>The exact command that ran, as the operator would read it.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>The first sentence: what it comes to.</summary>
    public string Headline { get; init; } = string.Empty;

    /// <summary>"14 start redacting"; empty when nothing moves.</summary>
    public string Breakdown { get; init; } = string.Empty;

    public IReadOnlyList<RedactionDiffRow> Diff { get; init; } = [];

    /// <summary>Plain sentences under the diff: digests, backup, locked destinations, whether the gateway restarted.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyList<RedactionWarningRow> Warnings { get; init; } = [];

    /// <summary>Label and value pairs of a read (a profile's field modes, a route's selector).</summary>
    public IReadOnlyList<RedactionFact> Facts { get; init; } = [];

    /// <summary>The CLI's own words: what a text-only read printed, or the tail of a failure. Shown under "Show CLI output".</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>The parsed result of a preview or an apply; null for a read or a failure.</summary>
    public RedactionResult? Result { get; init; }

    /// <summary>For a preview: the command it ran. A preview is applied only while the form still builds the same one.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>For a preview: the operation and the inputs it was run for, which "Apply this change" goes on with.</summary>
    public RedactionOperation? Operation { get; init; }

    public RedactionInputs? Inputs { get; init; }

    /// <summary>True when the quick sheet ran it (its own restart choice then applies), false for the advanced editor.</summary>
    public bool FromQuick { get; init; }

    public bool HasDiff => Diff.Count > 0;

    public bool HasBreakdown => Breakdown.Length > 0;

    public bool HasNotes => Notes.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasFacts => Facts.Count > 0;

    public bool HasRaw => Raw.Length > 0;

    public bool IsPreview => Kind == RedactionOutcomeKind.Preview;

    /// <summary>True for a preview of a change that would alter the configuration: the operator can go on to apply it.</summary>
    public bool CanApply => Kind == RedactionOutcomeKind.Preview && Result is { Changed: true };

    /// <summary>Screen readers get the heading, the badge and the headline in one breath.</summary>
    public string AutomationName => $"{Heading}. {Badge}. {Headline}";
}
