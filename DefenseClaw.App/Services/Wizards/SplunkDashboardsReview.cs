using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What a review of a <c>setup splunk dashboards</c> command says beyond its tier and its argv, for both surfaces that review one (the
/// wizard's last page and the palette's confirmation), so they cannot tell different stories. Every sentence comes from what the
/// installed CLI's source does (<c>commands/cmd_setup_splunk_o11y_dashboards.py</c>; see <see cref="SplunkDashboards"/>).
/// <para>
/// The tiers are not decided here: <see cref="CommandTiers"/> already makes <c>plan</c> and <c>apply</c> changes (<c>setup</c> is the first
/// verb of the path and a state-changing one) and <c>destroy</c> destructive (its destructive-verb list), and a review may only raise a
/// tier. Nothing typed into an option can lower it: a value that spells a read-only flag counts only when it stands alone.
/// </para>
/// </summary>
public static class SplunkDashboardsReview
{
    /// <summary>The heading of the bar that says <c>apply</c> does not wait for the plan to be read.</summary>
    public const string AppliesNowTitle = "Applies straight after the plan";

    /// <summary>The heading of the bar that says an <c>apply</c> without detectors removes the ones an earlier run created.</summary>
    public const string RemovesDetectorsTitle = "Can remove detectors";

    /// <summary>The heading of the bar that says <c>destroy</c> deletes what is in Splunk.</summary>
    public const string DeletesTitle = "Deletes dashboards in Splunk";

    /// <summary>The heading of the bar that says the command will stop for want of a token.</summary>
    public const string NoTokenTitle = "No Splunk token";

    /// <summary>
    /// True for a command under <c>setup splunk dashboards</c> naming a verb (see <see cref="SplunkDashboards.IsCommand"/>): the three this
    /// app has read, and any a newer CLI adds, which this app treats the same way for everything but the sentences below.
    /// </summary>
    public static bool IsDashboardsCommand(IReadOnlyList<string> argv, out string verb) => SplunkDashboards.IsCommand(argv, out verb);

    /// <summary>
    /// Whether running <paramref name="argv"/> restarts the live gateway, for a dashboards command; null when this has no opinion (it is
    /// not one), and the general rule for <c>setup</c> commands applies. None of the verbs does: the <c>setup</c> group's result callback
    /// (<c>_auto_restart_sidecar_after_setup</c>, <c>cmd_setup.py</c>) restarts a running gateway only after a subcommand that changed
    /// config.yaml's modification time, and these three write Terraform's files under <c>~/.defenseclaw/splunk-o11y-dashboards</c> and read
    /// config.yaml (for the ingest endpoint the API URL is derived from), nothing more. The general rule would have said "restarts the
    /// gateway" for all of them, because it errs toward yes for a <c>setup</c> command that is not a read.
    /// </summary>
    public static bool? RestartsGateway(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return IsDashboardsCommand(argv, out _) ? false : null;
    }

    /// <summary>
    /// What one verb does, in a sentence or two, for the line a wizard's review shows where it says what changed from the current
    /// configuration: these verbs are not edits of a setting. Empty for a command that is not a dashboards command or a verb this app has
    /// not read (the review then says what it says for any wizard).
    /// </summary>
    public static string Summary(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (!IsDashboardsCommand(argv, out var verb))
        {
            return string.Empty;
        }

        return verb switch
        {
            SplunkDashboards.Plan =>
                "Shows what Terraform would change in Splunk Observability Cloud, and changes nothing there. On this machine it writes " +
                "Terraform's working files and state (under ~/.defenseclaw/splunk-o11y-dashboards unless you point it elsewhere) and " +
                "fetches Terraform's Splunk provider the first time.",
            SplunkDashboards.Apply =>
                "Creates or updates the DefenseClaw dashboards in Splunk Observability Cloud through Terraform, adopting ones with the " +
                "same names that already exist. It does everything plan does on this machine first.",
            SplunkDashboards.Destroy =>
                "Deletes from Splunk Observability Cloud everything the Terraform state manages: the DefenseClaw dashboard group, its " +
                "dashboards and charts and, if it created them, its detectors. That includes same-named dashboards an earlier plan or " +
                "apply adopted, even ones that were there before DefenseClaw.",
            _ => string.Empty,
        };
    }

    /// <summary>The one-line description of a verb for the palette's row; empty for a verb this app has not read.</summary>
    public static string RowDescription(string verb) => verb switch
    {
        SplunkDashboards.Plan => "Preview what Terraform would change in Splunk Observability Cloud. Changes nothing there.",
        SplunkDashboards.Apply => "Create or update the DefenseClaw dashboards in Splunk Observability Cloud through Terraform.",
        SplunkDashboards.Destroy => "Delete the DefenseClaw dashboards Terraform manages in Splunk Observability Cloud.",
        _ => string.Empty,
    };

    /// <summary>
    /// The bars a review of <paramref name="argv"/> carries <i>besides</i> the gateway restart (which these never have): nothing for a
    /// command that is not a dashboards command, otherwise only what that verb with those flags really does.
    /// </summary>
    /// <param name="argv">The command as it will run.</param>
    /// <param name="token">
    /// Whether <c>SFX_AUTH_TOKEN</c> is already set for the command (this app's environment or <c>~/.defenseclaw/.env</c>), for a surface
    /// that cannot ask for one — the palette. Null when the surface does (the wizard has its own card for the token) or it is not known.
    /// </param>
    public static IReadOnlyList<CommandReviewWarning> Warnings(IReadOnlyList<string> argv, CredentialPresence? token = null)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (!IsDashboardsCommand(argv, out var verb))
        {
            return Array.Empty<CommandReviewWarning>();
        }

        var warnings = new List<CommandReviewWarning>();
        switch (verb)
        {
            case SplunkDashboards.Apply:
                warnings.Add(new CommandReviewWarning(
                    AppliesNowTitle,
                    "Terraform prints its plan as the command runs but does not wait for you to read it: --yes stands in for the CLI's own " +
                    "\"Apply these Splunk Observability Cloud changes?\" question, and this review is that question. To read the plan " +
                    "first, run plan, which shows the same changes."));
                if (!Has(argv, "--with-detectors"))
                {
                    warnings.Add(new CommandReviewWarning(
                        RemovesDetectorsTitle,
                        "Without --with-detectors Terraform plans for no detectors, so if an earlier run created them from this state, " +
                        "applying removes them from Splunk. Add --with-detectors to keep them; run plan to see what would go."));
                }

                break;

            case SplunkDashboards.Destroy:
                warnings.Add(new CommandReviewWarning(
                    DeletesTitle,
                    "Terraform deletes every Splunk Observability Cloud object its state records. This cannot be undone." +
                    (Has(argv, "--yes")
                        ? " This review stands in for the CLI's own \"Destroy these Splunk Observability Cloud objects?\" question, which is why --yes is on the command."
                        : string.Empty)));
                break;
        }

        if (token == CredentialPresence.NotSet && SplunkDashboards.IsReviewedVerb(verb))
        {
            warnings.Add(new CommandReviewWarning(
                NoTokenTitle,
                $"The command reads the Splunk Observability Cloud API token from {SplunkDashboards.TokenVariable}, which is not set in this " +
                "app's environment or in ~/.defenseclaw/.env, so it will stop at once with \"Splunk O11y token not found\" and change " +
                "nothing. The palette cannot ask for the token: the Splunk dashboards card on the Setup page takes it for one run."));
        }

        return warnings;
    }

    /// <summary>
    /// True when <paramref name="flag"/> is on the command as an option. Only what comes before a <c>--</c> can be one, and a token that is
    /// the value of the option before it does not count: <c>--name-prefix --with-detectors</c> names a prefix, and turns nothing on (the
    /// same rule <see cref="LocalStackReview"/> applies, and <see cref="CommandTiers.IsStandaloneFlag"/> decides).
    /// </summary>
    private static bool Has(IReadOnlyList<string> argv, string flag)
    {
        var options = argv.TakeWhile(a => !string.Equals(a, "--", StringComparison.Ordinal)).ToArray();
        for (var i = 0; i < options.Length; i++)
        {
            if (string.Equals(options[i], flag, StringComparison.Ordinal) && CommandTiers.IsStandaloneFlag(options, i))
            {
                return true;
            }
        }

        return false;
    }
}
