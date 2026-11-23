using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What the batch dialog says when the runtime keeps the connectors a command does not name (1.0.10, <see cref="RuntimeCapability.SetupKeepsRoster"/>).
/// <para>
/// Before 1.0.10, <c>setup -c X</c>, <c>--detected</c> and <c>--all</c> replaced the configured roster with the selection. From 1.0.10 they add or update the
/// named connectors and keep every other one with its own mode, fail mode and judge, <b>except</b> when the configured roster is empty or holds openclaw or
/// zeptoclaw: that roster still takes the replacing path (<c>_roster_accepts_hook_merge</c>, <c>cmd_setup.py</c> at 1.0.10, lines 13849-13857 and 13943-13946).
/// The CLI's summary calls the ones it did not touch "Left as they were" (line 12390). Taking a connector out is <c>setup remove</c>, which the Setup panel
/// offers as "Remove a connector…"; this dialog never builds that command.
/// </para>
/// </summary>
public sealed partial class ConnectorBatchViewModel
{
    private bool _keepsRoster;

    /// <summary>
    /// The batch command adds or updates the named connectors and keeps the others: the runtime is 1.0.10 or later and the configured roster is not
    /// empty and holds no proxy connector (openclaw, zeptoclaw). False keeps the dialog exactly as it was (it sets the roster and says which it drops).
    /// </summary>
    internal bool KeepsRoster => _keepsRoster;

    private bool ComputeKeepsRoster() =>
        Services.Runtime.Capabilities.Has(RuntimeCapability.SetupKeepsRoster) &&
        _roster.Count > 0 &&
        !_roster.Any(static id => ConnectorOnboarding.IsProxy(id));

    internal const string ModeAppliesOnlyToNamed = "The mode applies only to the connectors this command adds or updates; every other connector keeps its own mode.";

    internal const string KeepsRosterTitle = "Other connectors are kept";

    private const string HowToStop = "To stop one, use Remove a connector… in the Setup panel's connector list.";

    /// <summary>Under the roster line: what unticking does, and where to take a connector out. Empty when the dialog replaces the roster.</summary>
    public string RosterNote => _keepsRoster
        ? "A connector you leave unticked keeps its configuration and mode. " + HowToStop
        : string.Empty;

    public bool HasRosterNote => RosterNote.Length > 0;

    /// <summary>The configured connectors the command does not name (so the CLI leaves them as they were); none when the runtime replaces the roster or <c>--detected</c>/<c>--all</c> may name more.</summary>
    private IReadOnlyList<string> KeptConnectors(IReadOnlyCollection<string> selected, BatchExtra extra) =>
        !_keepsRoster || extra != BatchExtra.None
            ? Array.Empty<string>()
            : _roster.Where(r => !selected.Contains(r, StringComparer.Ordinal)).ToArray();

    private string KeptLine(IReadOnlyList<string> kept) =>
        BatchRow.LeftAsTheyWere + ": " + string.Join(", ", kept.Select(Label));

    private string KeepsRosterText(IReadOnlyList<string> kept) =>
        "This adds or updates the connectors it names. Every other connector keeps its configuration and mode." +
        (kept.Count > 0 ? " Left as they were: " + string.Join(", ", kept.Select(Label)) + "." : string.Empty) +
        " " + ModeAppliesOnlyToNamed + " " + HowToStop;
}
