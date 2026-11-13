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

    /// <summary>
    /// What the source shows about the installer (install.ps1@1.0.9:662,728,915-918): it refuses to change version while a recovery receipt remains, prints this
    /// command, and does not run it. The command's help (connector_cmd.go@1.0.9:133-143) says to run it with the same --data-dir and then run the rollback or
    /// upgrade again; neither says whether the agent should be closed first, nor whether the gateway should be stopped, so this says neither.
    /// </summary>
    public const string InstallerSentence =
        "The installer refuses to change version while these recovery files remain and prints this command for you to run; it does not run it.";

    /// <summary>The argv after <c>defenseclaw-gateway</c>. <c>--data-dir</c> is passed on purpose: the command is config-optional and has no other way to find the folder.</summary>
    public static IReadOnlyList<string> Argv(string dataDirectory) => new[] { "connector", "recover-config", "--data-dir", dataDirectory };

    /// <summary>
    /// The command as a person would type it in PowerShell or in a Command Prompt. The folder is double-quoted when it holds anything but letters, digits and
    /// <c>\ : . _ - /</c>; PowerShell's single quotes are not used because cmd.exe passes them on as part of the path (and the command then finds no folder).
    /// A Windows path cannot hold a double quote.
    /// </summary>
    public static string CommandText(string dataDirectory) =>
        "defenseclaw-gateway " + CommandName + " --data-dir " + QuotePath(dataDirectory);

    /// <summary><paramref name="path"/> as one argument for both shells: bare when it is plain, double-quoted otherwise; a trailing backslash is dropped (before a closing quote it would escape it).</summary>
    internal static string QuotePath(string path)
    {
        var text = path.Length > 3 && path.EndsWith('\\') ? path.TrimEnd('\\') : path;
        return text.Length > 0 && text.All(IsPlainPathChar) ? text : "\"" + text + "\"";
    }

    private static bool IsPlainPathChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '\\' or ':' or '.' or '_' or '-' or '/';

    /// <summary>True when the folder holds a character that PowerShell (<c>$</c>, backtick) or cmd.exe (<c>%</c>) reads inside double quotes, so no single spelling is safe in both.</summary>
    internal static bool PathReadSpecially(string path) => path.IndexOfAny(new[] { '$', '`', '%' }) >= 0;

    /// <summary>What the command does, for the Overview review.</summary>
    public const string RunsWhat =
        "Finishes the interrupted agent config change with the same recovery DefenseClaw runs before any later change to that file. " +
        "A change it cannot prove is kept and named, and a file you saved is never replaced.";

    /// <summary>
    /// What it prints (connector_cmd.go@1.0.9:1319-1320: "finished %d interrupted agent config change(s) in %s"). It also prints 0 when the folder does not exist
    /// (config_transform_recovery_windows.go@1.0.9:23-25), so a 0 is not proof that nothing is left.
    /// </summary>
    public const string SuccessText =
        "On success it prints \"finished N interrupted agent config change(s) in <folder>\". finished 0 means nothing was finished; " +
        "if the Overview still shows a stuck write, check that the folder it prints is your data folder.";

    /// <summary>
    /// What stays true when it cannot finish a receipt (config_transform_recovery_windows.go@1.0.9:22-79, connector_cmd.go@1.0.9:1312; install.ps1@1.0.9:915-918):
    /// the files are kept in the data folder, the output names the config, and the installer says to finish it with this command, retry, and not remove or replay
    /// the receipts. Neither the help nor the installer gives another step, so none is invented here.
    /// </summary>
    public const string KeptSentence =
        "If it names an agent config it cannot finish, the recovery files (the .defenseclaw-cas-v2-* receipts in the data folder) are kept and the upgrade stays refused until that config can be finished; " +
        "DefenseClaw's installer and help give no other step.";

    public const string FailureText =
        "If it fails, its output says \"recovery files are kept in <folder>\" and names each config it could not finish. " + KeptSentence;

    /// <summary>The hint under a refused upgrade, installed DefenseClaw 1.0.9 or later.</summary>
    public const string UpgradeHint =
        "Run the recovery, then run the upgrade again: use \"" + ActionLabel + "\" in Overview, or the command the installer printed (defenseclaw-gateway " + CommandName + "). " +
        "Do not remove or move the receipts yourself. " + KeptSentence;

    /// <summary>Added to today's hint when the refusal line names the 1.0.9 command and the installed DefenseClaw does not have it.</summary>
    public const string OlderRuntimeNote =
        "The command the installer prints (" + CommandName + ") needs DefenseClaw 1.0.9, and the DefenseClaw installed now is older and does not have it.";

    /// <summary>True when the installer's refusal line is the 1.0.9 one: it names the command, or the sentence that comes before it.</summary>
    public static bool LineNamesCommand(string reason) =>
        reason.Contains(CommandName, StringComparison.OrdinalIgnoreCase) || reason.Contains(InstallerFinishPhrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Overview row's sentence about the command, replacing "DefenseClaw has no command to recover from this".</summary>
    public const string OverviewSentence =
        "Finish the interrupted change runs defenseclaw-gateway " + CommandName + ", which finishes it with the same recovery DefenseClaw runs before any later change to that file. " +
        "If it names a config it cannot finish, the files stay in the data folder and the upgrade stays refused until that config can be finished.";

    /// <summary>The pre-flight sentence for receipts the app does not list as a pending write.</summary>
    public static string ReceiptsOnlyStep(string dataDirectory) =>
        "Run " + CommandText(dataDirectory) + " in PowerShell or a Command Prompt, then check again. It finishes what it can prove and names what it cannot. Do not remove or move the files yourself." +
        (PathReadSpecially(dataDirectory)
            ? " The folder name has a character ($, a backtick or %) that PowerShell or Command Prompt can read as its own; check that the command prints this folder."
            : string.Empty);
}
