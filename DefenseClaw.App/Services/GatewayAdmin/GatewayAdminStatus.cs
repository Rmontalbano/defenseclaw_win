using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>Whether this PC can do administrator mode at all, apart from the switch.</summary>
public enum AdminEligibility
{
    /// <summary>Not looked at yet.</summary>
    Unknown,

    /// <summary>A split-token administrator (or an already elevated administrator) on a 1.0 runtime.</summary>
    Eligible,

    /// <summary>The account is a standard user, or UAC does not filter it.</summary>
    NeedsLocalAdministrator,

    /// <summary>The user, profile or Program Files folder could not be determined.</summary>
    LayoutUnavailable,
}

/// <summary>
/// What the Runtime monitoring card and the two toggles show about "Run gateway as administrator". Everything is read without elevation: the
/// administrator copy's access lists and hash are readable by Users, the owner of a file is readable by its user, and a process's token can
/// be queried with a limited handle.
/// </summary>
internal sealed record GatewayAdminStatus
{
    /// <summary>Before the first look: nothing is claimed.</summary>
    public static GatewayAdminStatus Unknown { get; } = new();

    /// <summary>The runtime has what administrator mode is for (AI Discovery runtime planes, DefenseClaw 1.0). False on 0.8.10.</summary>
    public bool Supported { get; init; }

    /// <summary>Why <see cref="Supported"/> is false; empty otherwise.</summary>
    public string UnsupportedReason { get; init; } = string.Empty;

    /// <summary>The switch: <c>connection.gatewayAdministratorMode</c> in the app's settings. Flipping it starts and stops nothing.</summary>
    public bool ModeOn { get; init; }

    public AdminEligibility Eligibility { get; init; }

    /// <summary>Why <see cref="Eligibility"/> is not <see cref="AdminEligibility.Eligible"/>.</summary>
    public string EligibilityReason { get; init; } = string.Empty;

    /// <summary>The helper file next to the app is there (needed for the first elevation).</summary>
    public bool HelperStaged { get; init; }

    /// <summary>The helper is in Program Files.</summary>
    public bool HelperInstalled { get; init; }

    /// <summary>...admin-only and unchanged since it installed itself.</summary>
    public bool HelperTrusted { get; init; }

    /// <summary>The administrator copy of the gateway is in Program Files.</summary>
    public bool CopyPresent { get; init; }

    /// <summary>...admin-only, recorded and unchanged: the helper would run it, and the app sends the gateway token to it.</summary>
    public bool CopyTrusted { get; init; }

    /// <summary>The copy and the installed gateway are the same bytes; null when either could not be read.</summary>
    public bool? CopyMatchesInstalled { get; init; }

    /// <summary>The release the copy was recorded for.</summary>
    public string? CopyVersion { get; init; }

    /// <summary>The running gateway has an elevated token; null when it is not running or Windows would not say.</summary>
    public bool? GatewayRunningElevated { get; init; }

    /// <summary>Files under <c>.defenseclaw</c> are owned by Administrators or another account (the Windows exit 77).</summary>
    public bool OwnershipProblem { get; init; }

    /// <summary>Which of them.</summary>
    public IReadOnlyList<string> OwnershipFiles { get; init; } = Array.Empty<string>();

    /// <summary>Every reason, in words, that the setup is not in order (access lists, links, a missing record).</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    /// <summary>When this was read.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>The mode is on, the runtime supports it and this PC can do it: the gateway actions go through the helper.</summary>
    public bool IsActive => Supported && ModeOn && Eligibility == AdminEligibility.Eligible;

    /// <summary>The switch can be turned on (or off): the runtime supports it and this account can.</summary>
    public bool CanToggle => Supported && Eligibility == AdminEligibility.Eligible;

    /// <summary>The ownership repair is worth offering.</summary>
    public bool CanRepairOwnership => Supported && Eligibility == AdminEligibility.Eligible && OwnershipProblem;

    /// <summary>One line for a headline: where the setup stands.</summary>
    public string Headline
    {
        get
        {
            if (!Supported)
            {
                return UnsupportedReason;
            }

            if (Eligibility == AdminEligibility.NeedsLocalAdministrator || Eligibility == AdminEligibility.LayoutUnavailable)
            {
                return EligibilityReason;
            }

            if (!ModeOn)
            {
                return OwnershipProblem ? OwnershipSentence : "Off. The gateway runs as you.";
            }

            if (OwnershipProblem)
            {
                return OwnershipSentence;
            }

            if (!HelperTrusted)
            {
                return HelperInstalled
                    ? "On, but the administrator helper in Program Files is not the file it installed. It will not be run."
                    : "On. The first start installs the helper and a copy of the gateway in Program Files (Windows asks first).";
            }

            if (!CopyPresent)
            {
                return "On. The first start copies the installed gateway into Program Files (Windows asks first).";
            }

            if (!CopyTrusted)
            {
                return "On, but the administrator copy of the gateway is not the file the helper installed. It will not be run.";
            }

            if (GatewayRunningElevated == true)
            {
                return "On. The gateway is running with administrator rights.";
            }

            return CopyMatchesInstalled == false
                ? "On. The installed gateway differs from the administrator copy: Restart Gateway as Administrator to use it."
                : "On. The administrator copy matches the installed gateway.";
        }
    }

    /// <summary>The Windows exit-77 sentence.</summary>
    public const string OwnershipSentence =
        "This installation has files owned by Administrators because the gateway ran as administrator. Run Repair ownership, or keep running the gateway as administrator.";
}
