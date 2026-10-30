using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// What a wizard says before its review (CUST-401): the read-only card an ACP Guard window opens with, and, for <c>setup codex</c> on a 1.0.5 runtime, the
/// warning that the Codex home is a link (1.0.5 refuses it before changing anything, so the run would stop; <see cref="CodexHomeLink"/>). Nothing here
/// starts a process or changes a file.
/// </summary>
public sealed partial class WizardViewModel
{
    private CodexHomeLinkFinding? _codexHomeLink;
    private bool _codexHomeLinkRead;

    /// <summary>Test seam: the Codex home look, so a test never reads the real <c>.codex</c> of the machine it runs on. Set before the first page is drawn.</summary>
    internal Func<CodexHomeLinkFinding?>? CodexHomeProbe { get; set; }

    /// <summary>The read-only card above the first page (the ACP Guard's pairs and their health); null for every other wizard.</summary>
    public WizardInfoBlock? InfoBlock => Definition.InfoBlock;

    public bool HasInfoBlock => Definition.InfoBlock is not null;

    /// <summary>The sentence under the card's rows; empty when it has none.</summary>
    public string InfoBlockNote => Definition.InfoBlock?.Note ?? string.Empty;

    /// <summary>True when this is <c>setup codex</c> on a runtime whose setup refuses a linked Codex home (<see cref="RuntimeCapability.CodexHomeLinkRefusal"/>).</summary>
    private bool ChecksCodexHome =>
        string.Equals(Definition.Target, "codex", StringComparison.Ordinal) &&
        string.Equals(Definition.DisplayCommand, "setup codex", StringComparison.Ordinal) &&
        _services.Runtime.Check(RuntimeCapability.CodexHomeLinkRefusal).IsAvailable;

    private CodexHomeLinkFinding? CodexHomeFinding
    {
        get
        {
            if (!_codexHomeLinkRead)
            {
                _codexHomeLinkRead = true;
                _codexHomeLink = ChecksCodexHome ? (CodexHomeProbe ?? CodexHomeLink.Find)() : null;
            }

            return _codexHomeLink;
        }
    }

    /// <summary>The warning shown on every page before the review: the Codex home is a link and the setup would be refused. Empty otherwise.</summary>
    public string PreflightWarning => CodexHomeFinding?.Message ?? string.Empty;

    public bool HasPreflightWarning => CodexHomeFinding is not null;

    /// <summary>The same warning as a bar on the review of <c>setup codex</c> (a different wizard's review never carries it).</summary>
    private IEnumerable<CommandReviewWarning> CodexHomeWarnings(IReadOnlyList<string> argv)
    {
        if (argv.Count >= 2 && string.Equals(argv[0], "setup", StringComparison.Ordinal) && string.Equals(argv[1], "codex", StringComparison.Ordinal) &&
            CodexHomeFinding is { } finding)
        {
            yield return new CommandReviewWarning(CodexHomeLinkFinding.Title, finding.Message);
        }
    }
}
