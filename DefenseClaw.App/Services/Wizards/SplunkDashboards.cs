namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// <c>defenseclaw setup splunk dashboards plan | apply | destroy</c>: the Terraform-backed installer of the DefenseClaw dashboards (and,
/// on request, detectors) in Splunk Observability Cloud. What this app knows about it, in one place, with the evidence (DefenseClaw
/// 0.8.10, <c>commands/cmd_setup_splunk_o11y_dashboards.py</c>; the pinned source commit's copy of that file is identical):
/// <list type="bullet">
///   <item><b>It is a nested group</b> under <c>setup splunk</c>, so the Setup hub's roster (<c>setup --help</c>) does not list it. The
///     hub gives it a card of its own (<see cref="Target"/> is the noun path after <c>setup</c>), derived from the <c>splunk</c> card
///     being there, and the card is available only while Terraform is (<see cref="TerraformAvailability"/>).</item>
///   <item><b>It drives Terraform.</b> The CLI copies the bundled module into <c>~/.defenseclaw/splunk-o11y-dashboards/terraform</c>, runs
///     <c>terraform init</c> and <c>validate</c>, adopts same-named objects that already exist in Splunk (<c>terraform import</c>, which
///     only writes the local state file), then <c>plan</c>, and — for <c>apply</c> and <c>destroy</c> — <c>apply -auto-approve</c> or
///     <c>destroy -auto-approve</c>. <c>--yes</c> only skips the question between the plan and that step. The executable is
///     <c>--terraform-bin</c> (default <c>terraform</c>, bound to <c>TERRAFORM_BIN</c>); the module needs Terraform 1.5.0 or later.</item>
///   <item><b>The Splunk Observability Cloud API token</b> is <c>--o11y-api-token</c>, bound to <see cref="TokenVariable"/>; the CLI's own
///     help says to prefer the variable "so the secret never appears in shell history or process listings". It is the user API access
///     token, not the ingest token, and the CLI never stores it: it sets <c>TF_VAR_signalfx_auth_token</c> for its Terraform children.
///     This app delivers a typed token as that variable in the child's environment and nowhere else (<see cref="SecretRoutes"/>).</item>
///   <item><b>Only <c>destroy</c> deletes dashboards.</b> <c>apply</c> can also delete detectors: the module creates them
///     <c>for_each</c> over <c>var.create_detectors</c>, so an <c>apply</c> without <c>--with-detectors</c> plans to remove ones an earlier
///     run created from the same state. None of the three writes config.yaml, so none restarts the gateway.</item>
/// </list>
/// </summary>
public static class SplunkDashboards
{
    /// <summary>The noun path after <c>setup</c> that names it (<c>defenseclaw setup splunk dashboards</c>); also the card's identity.</summary>
    public const string Target = "splunk dashboards";

    /// <summary>The setup target it hangs off. The card is derived from this one being in the roster.</summary>
    public const string ParentTarget = "splunk";

    /// <summary>What the card and the wizard are called.</summary>
    public const string Title = "Splunk dashboards";

    /// <summary>
    /// The card's blurb: two lines of a 233-pixel tile (the CLI's own summaries are on the wizard's pages). Says "Terraform" so a search for it
    /// finds the card, and what the two verbs that matter do, in the Mac tile's words.
    /// </summary>
    public const string Description = "Apply or destroy the Splunk O11y dashboards with Terraform.";

    /// <summary>Shows what Terraform would change.</summary>
    public const string Plan = "plan";

    /// <summary>Creates or updates the dashboards (and detectors).</summary>
    public const string Apply = "apply";

    /// <summary>Deletes everything the Terraform state manages.</summary>
    public const string Destroy = "destroy";

    /// <summary>
    /// The verbs this app has read the source of, in the order the wizard offers them: the preview first, the deleting one last. A verb a
    /// newer CLI adds is not offered until somebody has read what it does — this one can delete things in a cloud account.
    /// </summary>
    public static IReadOnlyList<string> Verbs { get; } = new[] { Plan, Apply, Destroy };

    /// <summary>The variable the CLI reads the token from in place of <see cref="TokenFlag"/>.</summary>
    public const string TokenVariable = "SFX_AUTH_TOKEN";

    /// <summary>The flag that takes the token itself; a secret, so it never reaches argv.</summary>
    public const string TokenFlag = "--o11y-api-token";

    /// <summary>
    /// The flag that names the Terraform executable. Not offered: the look at Terraform (<see cref="TerraformProbe"/>) asked the one
    /// <c>TERRAFORM_BIN</c> or PATH names, and a different one on the command line is a Terraform nobody looked at.
    /// </summary>
    public const string TerraformBinFlag = "--terraform-bin";

    /// <summary>True for the card's target (<see cref="Target"/>).</summary>
    public static bool IsTarget(string? target) => string.Equals(target, Target, StringComparison.Ordinal);

    /// <summary>True for a verb whose source was read for this app (<see cref="Verbs"/>).</summary>
    public static bool IsReviewedVerb(string? verb) => Verbs.Contains(verb ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// True for a command under <c>setup splunk dashboards</c> that names a verb (<paramref name="verb"/>): <c>setup splunk dashboards
    /// apply --yes</c>. The group on its own, or with only an option (<c>--help</c>), prints help and is not one.
    /// </summary>
    public static bool IsCommand(IReadOnlyList<string> argv, out string verb)
    {
        ArgumentNullException.ThrowIfNull(argv);

        verb = string.Empty;
        if (argv.Count < 4 ||
            !string.Equals(argv[0], "setup", StringComparison.Ordinal) ||
            !string.Equals(argv[1], ParentTarget, StringComparison.Ordinal) ||
            !string.Equals(argv[2], "dashboards", StringComparison.Ordinal) ||
            argv[3].StartsWith('-'))
        {
            return false;
        }

        verb = argv[3];
        return true;
    }

    /// <summary>
    /// True when the group's help lists at least one verb this app has read. A CLI that predates the group answers
    /// <c>setup splunk dashboards --help</c> with the usage of <c>setup splunk</c> and an error, which parses to no verbs at all.
    /// </summary>
    public static bool IsPresentIn(ParsedHelp group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Commands.Any(c => IsReviewedVerb(c.Name));
    }

    /// <summary>The phase-one card: everything known before the group's own help has been read.</summary>
    public static WizardDefinition Stub() => new()
    {
        Target = Target,
        Title = Title,
        Group = WizardGroups.Observability,
        Description = Description,

        // Like the other flows that are not connectors: nothing to certify, so a neutral badge and no counting as certified.
        PlatformStatus = PlatformStatus.NotApplicable,
        Steps = Array.Empty<WizardStep>(),
        IsDetailLoaded = false,
    };

    /// <summary>The card when the installed CLI has no <c>setup splunk dashboards</c>: shown, disabled, saying so.</summary>
    public static WizardDefinition Missing() => new()
    {
        Target = Target,
        Title = Title,
        Group = WizardGroups.Observability,
        Description = Description,
        PlatformStatus = PlatformStatus.NotApplicable,
        Steps = Array.Empty<WizardStep>(),
        IsDetailLoaded = true,
        UnavailableReason =
            "The installed DefenseClaw has no \"setup splunk dashboards\" command (its help lists none of " +
            string.Join(", ", Verbs) + "), so there is nothing to run. It is part of DefenseClaw 0.8.10; upgrade, then re-read the catalog.",
    };
}
