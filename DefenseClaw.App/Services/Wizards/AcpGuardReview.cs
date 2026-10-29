namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// What the review of an <c>acp setup</c> command says beyond its argv (CUST-294). Judged on the argv, which is what runs, so it cannot drift from the
/// command the operator confirms. The facts are <c>defenseclaw acp setup</c>'s at 1.0.4 (<c>cli/defenseclaw/commands/cmd_acp.py</c>):
/// <list type="bullet">
///   <item><b>Action mode</b> (<c>--activate</c>): the pair's profile is set to <c>mode: action</c>, <c>fail_mode: closed</c> (line 1208), so the guard blocks
///     what policy denies and refuses a step it cannot check.</item>
///   <item><b>The editor's file.</b> Setup rewrites the editor's settings as plain JSON (<c>_write_json</c>; Zed's comments after the opening block and
///     trailing commas are dropped, as the ACP guard docs say), at <c>%APPDATA%\Zed\settings.json</c> or <c>%USERPROFILE%\.jetbrains\acp.json</c>
///     (<c>_client_path</c>, line 46), and writes a private token and a contract lock under the data directory's <c>acp</c> folder. It runs as the
///     operator: no administrator rights are involved.</item>
///   <item><b>Managed enrollment</b> (<c>--managed</c>): user-side files only; it needs the administrator to have enrolled the pair first
///     (<c>defenseclaw-gateway enterprise acp enroll</c>, run elevated: <c>internal/cli/enterprise_acp.go</c>), which publishes the token file and fixes
///     the central profile. A standalone managed host with no Python CLI is told to run <c>defenseclaw-gateway enterprise acp setup</c> instead
///     (<c>enterpriseACPSetupCommand</c>).</item>
/// </list>
/// </summary>
public static class AcpGuardReview
{
    /// <summary>The heading of the action-mode bar.</summary>
    public const string ActionModeTitle = "Blocks denied operations";

    /// <summary>The heading of the editor-file bar.</summary>
    public const string EditorFileTitle = "Rewrites the editor's settings";

    /// <summary>The heading of the managed-enrollment bar.</summary>
    public const string ManagedTitle = "Administrator-provisioned enrollment";

    /// <summary>True for <c>acp setup …</c>.</summary>
    public static bool IsAcpSetup(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return argv.Count >= 2 &&
               string.Equals(argv[0], "acp", StringComparison.Ordinal) &&
               string.Equals(argv[1], "setup", StringComparison.Ordinal);
    }

    /// <summary>The bars to show beside the review of <paramref name="argv"/>; none for any other command.</summary>
    public static IReadOnlyList<CommandReviewWarning> Warnings(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (!IsAcpSetup(argv))
        {
            return Array.Empty<CommandReviewWarning>();
        }

        var options = argv.TakeWhile(a => !string.Equals(a, "--", StringComparison.Ordinal)).ToArray();
        var client = ValueOf(options, AcpGuardArgv.ClientFlag);
        var agent = ValueOf(options, AcpGuardArgv.AgentFlag);
        var managed = HasSwitch(options, AcpGuardArgv.ManagedFlag);
        var warnings = new List<CommandReviewWarning>();

        if (HasSwitch(options, AcpGuardArgv.ActivateFlag))
        {
            warnings.Add(new CommandReviewWarning(
                ActionModeTitle,
                "Action mode: the guard blocks any prompt, agent output, permission, file or terminal step that policy denies, and a step it cannot check is " +
                "refused (the profile fails closed). A blocked prompt ends that turn in the editor. Run observe mode first if you have not seen what this policy " +
                "would block." + (managed ? " The central profile must already be in action mode, or the CLI refuses." : string.Empty)));
        }

        var file = client switch
        {
            "zed" => "%APPDATA%\\Zed\\settings.json, rewritten as plain JSON (comments after the opening block and trailing commas are dropped)",
            "jetbrains" => "%USERPROFILE%\\.jetbrains\\acp.json",
            _ => "the editor's settings file",
        };
        warnings.Add(new CommandReviewWarning(
            EditorFileTitle,
            "Writes a DefenseClaw entry for " + (agent.Length > 0 ? agent : "the agent") + " into " + file + ", and " +
            (managed ? "a contract lock beside the administrator's token" : "a private token and a contract lock in the data directory's acp folder") +
            "; a failed write rolls all of it back. It runs as you: no administrator rights are needed."));

        if (managed)
        {
            warnings.Add(new CommandReviewWarning(
                ManagedTitle,
                "Writes only your user-side files and changes no policy. Your administrator must already have enrolled this editor and agent " +
                "(defenseclaw-gateway enterprise acp enroll, run elevated), which is what put the token file in place; the CLI reads that file itself and its " +
                "contents never appear here. If the enrollment told you to run defenseclaw-gateway enterprise acp setup instead, run that: this app does not."));
        }

        return warnings;
    }

    /// <summary>
    /// The line above the review's list of choices: what <c>acp setup</c> does, since its answers are not edits of a configured value. Null for any other command.
    /// </summary>
    public static string? Summary(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (!IsAcpSetup(argv))
        {
            return null;
        }

        var options = argv.TakeWhile(a => !string.Equals(a, "--", StringComparison.Ordinal)).ToArray();
        var mode = HasSwitch(options, AcpGuardArgv.ActivateFlag) ? "action mode" : "observe mode";
        return HasSwitch(options, AcpGuardArgv.ManagedFlag)
            ? "Enrolls this editor and agent in " + mode + " under your administrator's policy."
            : "Routes this editor and agent through the ACP guard in " + mode + ", and saves the pair's policy in config.yaml.";
    }

    private static string ValueOf(IReadOnlyList<string> options, string flag)
    {
        for (var i = 0; i + 1 < options.Count; i++)
        {
            if (string.Equals(options[i], flag, StringComparison.Ordinal))
            {
                return options[i + 1];
            }
        }

        return string.Empty;
    }

    /// <summary>True when <paramref name="flag"/> is a flag on the command, not the value of the option before it.</summary>
    private static bool HasSwitch(IReadOnlyList<string> options, string flag)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i], flag, StringComparison.Ordinal) &&
                (i == 0 || !TakesValue(options[i - 1])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TakesValue(string option) => option is AcpGuardArgv.ClientFlag or AcpGuardArgv.AgentFlag or AcpGuardArgv.ProfileFlag
        or AcpGuardArgv.RuntimeDataDirFlag or AcpGuardArgv.TokenFileFlag;
}
