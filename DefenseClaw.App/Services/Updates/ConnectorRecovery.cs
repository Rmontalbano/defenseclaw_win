namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// The words about <c>defenseclaw-gateway connector recover-config --data-dir &lt;dir&gt;</c> (DefenseClaw 1.0.9; internal/cli/connector_cmd.go@1.0.9:132-144): the one
/// way to finish the agent config changes an interrupted run left behind. The installer refuses to change version while their receipts remain and, from
/// 1.0.9, names this command. It is absent from 1.0.8 and older, whose only way out is the move-aside this app offers. Pure text; nothing here runs anything.
/// </summary>
internal static class ConnectorRecovery
{
    /// <summary>The subcommand as the installer's refusal prints it.</summary>
    public const string CommandName = "connector recover-config";

    /// <summary>The Overview button, and the name the upgrade card and the pre-flight use for it.</summary>
    public const string ActionLabel = "Finish the interrupted change";

    /// <summary>The words the 1.0.9 installer adds to its refusal before the command (install.ps1@1.0.9:915-918); the command itself can fall past the cap on the reason.</summary>
    public const string InstallerFinishPhrase = "Finish it with the current DefenseClaw version";

    /// <summary>The installer asks for this command at :662 before it stops the gateway, and again after (install.ps1@1.0.9:662,728); the help says nothing more.</summary>
    public const string GatewaySentence = "The installer asks for this command before it stops the gateway, so the gateway can keep running.";

    /// <summary>The argv after <c>defenseclaw-gateway</c>. <c>--data-dir</c> is passed on purpose: the command is config-optional and has no other way to find the folder.</summary>
    public static IReadOnlyList<string> Argv(string dataDirectory) => new[] { "connector", "recover-config", "--data-dir", dataDirectory };

    /// <summary>The command as a person would type it in a terminal.</summary>
    public static string CommandText(string dataDirectory) =>
        "defenseclaw-gateway " + CommandName + " --data-dir '" + dataDirectory.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>What the command does, for the Overview review.</summary>
    public const string RunsWhat =
        "Finishes the interrupted agent config change with the same recovery DefenseClaw runs before any later change to that file. " +
        "A change it cannot prove is kept and named, and a file you saved is never replaced.";

    public const string SuccessText =
        "On success it prints \"finished N interrupted agent config change(s)\"; 0 means nothing was left to finish.";

    public const string FailureText =
        "If it fails, the recovery files are kept and its output names each config it could not finish.";

    /// <summary>The hint under a refused upgrade, installed DefenseClaw 1.0.9 or later.</summary>
    public const string UpgradeHint =
        "Run the recovery, then run the upgrade again: use \"" + ActionLabel + "\" in Overview, or the command the installer printed (defenseclaw-gateway " + CommandName + "). " +
        "Do not remove or move the receipts yourself.";

    /// <summary>Added to today's hint when the refusal line names the 1.0.9 command and the installed DefenseClaw does not have it.</summary>
    public const string OlderRuntimeNote =
        "The command the installer prints (" + CommandName + ") needs DefenseClaw 1.0.9, and the DefenseClaw installed now is older and does not have it.";

    /// <summary>True when the installer's refusal line is the 1.0.9 one: it names the command, or the sentence that comes before it.</summary>
    public static bool LineNamesCommand(string reason) =>
        reason.Contains(CommandName, StringComparison.OrdinalIgnoreCase) || reason.Contains(InstallerFinishPhrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Overview row's sentence about the command, replacing "DefenseClaw has no command to recover from this".</summary>
    public const string OverviewSentence =
        "Finish the interrupted change runs defenseclaw-gateway " + CommandName + ", which finishes it with the same recovery DefenseClaw runs before any later change to that file.";

    /// <summary>The pre-flight sentence for receipts the app does not list as a pending write.</summary>
    public static string ReceiptsOnlyStep(string dataDirectory) =>
        "Run " + CommandText(dataDirectory) + " in a terminal, then check again. It finishes what it can prove and names what it cannot. Do not remove or move the files yourself.";
}
