using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.Services;

/// <summary>
/// The command palette's rows for the Splunk dashboards: <c>setup splunk dashboards plan</c>, <c>apply --yes</c> and <c>destroy --yes</c>.
/// <para>
/// They are not entries of the TUI command registry (neither runtime's registry lists a dashboards command; they list <c>setup splunk</c>,
/// which is an interactive wizard), so the palette's registry rows do not carry them. Both runtimes have the commands
/// (<see cref="SplunkDashboards"/>), so they are added beside the registry's rows and treated like any of them: reviewed before they run
/// (none is on the list of reads that may run unreviewed), with the tier <see cref="DefenseClaw.Core.Cli.CommandTiers"/> gives them
/// (<c>plan</c> and <c>apply</c> change state, <c>destroy</c> is destructive), and enabled only while the shared Terraform look says Terraform
/// can run them (<see cref="WizardWindowsPolicy.CommandNeedsTerraform"/>, applied by <see cref="ShellCommandRegistry.BuildCliCommands"/>).
/// </para>
/// <para>
/// A row carries the exact argv and nothing an operator typed: <c>--yes</c> is the one option, and it stands in for the CLI's own question
/// between Terraform's plan and its apply or destroy, which a window with no console could not answer (the review is that question). The
/// token cannot ride on it - the palette has no field to type one and <c>--o11y-api-token</c> is not a reviewed option - so the command reads
/// <c>SFX_AUTH_TOKEN</c> from this app's environment or <c>~/.defenseclaw/.env</c>, and the review says when neither has it
/// (<see cref="SplunkDashboardsReview.NoTokenTitle"/>). The Setup page's card is the way to type one in.
/// </para>
/// </summary>
internal static class SplunkDashboardsCommands
{
    /// <summary>The rows, in the order of <see cref="SplunkDashboards.Verbs"/>: the preview first, the deleting one last.</summary>
    public static IReadOnlyList<CuratedCommand> Rows { get; } = SplunkDashboards.Verbs.Select(Build).ToArray();

    private static CuratedCommand Build(string verb)
    {
        var argv = new List<string> { "setup", SplunkDashboards.ParentTarget, "dashboards", verb };

        // plan has no confirmation to answer; apply and destroy ask "Apply / Destroy these Splunk Observability Cloud ...?" unless given --yes.
        if (!string.Equals(verb, SplunkDashboards.Plan, StringComparison.Ordinal))
        {
            argv.Add("--yes");
        }

        return new CuratedCommand(
            argv,
            CuratedCommandCatalog.CategoryOf("setup"),
            SplunkDashboardsReview.RowDescription(verb),
            string.Empty,
            Array.Empty<string>(),
            TuiName: $"setup {SplunkDashboards.ParentTarget} dashboards {verb}");
    }
}
