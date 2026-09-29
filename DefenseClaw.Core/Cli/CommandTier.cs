namespace DefenseClaw.Core.Cli;

/// <summary>How much a command changes, which decides how much review it gets before running.</summary>
public enum CommandTier
{
    /// <summary>Only reads state (list/show/status/check/…). Runs without a confirmation step.</summary>
    ReadOnly = 0,

    /// <summary>Changes DefenseClaw state in a way the operator can undo. Confirm with the exact argv.</summary>
    StateChanging,

    /// <summary>
    /// Removes or resets something (remove/uninstall/reset/quarantine/…). Confirm with the exact argv
    /// and a danger-styled primary action.
    /// </summary>
    Destructive,
}

/// <summary>
/// The single classifier every confirmation surface uses, so "is this destructive?" is decided in one
/// place rather than per panel.
/// <para>
/// Only the command path is examined: the leading tokens before the first <c>-</c>-prefixed flag, and
/// at most <see cref="MaxPathTokens"/> of them, so a flag value or a target named e.g. <c>list</c>
/// cannot change the tier. The verb vocabulary mirrors the DefenseClaw 0.8.10 CLI help and the macOS
/// companion's destructive-verb list (remove, delete, reset, uninstall, quarantine, teardown,
/// destroy), plus <c>unset</c> and <c>dismiss</c>, which also discard configuration or findings.
/// The first recognised verb in the path decides between read-only and state-changing, so a target
/// that spells a verb cannot downgrade the tier. Anything unrecognised is
/// <see cref="CommandTier.StateChanging"/> — the safe default is to ask.
/// </para>
/// </summary>
public static class CommandTiers
{
    /// <summary>How many leading positional tokens can hold the verb (e.g. <c>agent discovery scan</c>).</summary>
    public const int MaxPathTokens = 3;

    private static readonly HashSet<string> DestructiveVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "remove", "delete", "reset", "uninstall", "quarantine", "teardown", "destroy", "unset", "dismiss",
    };

    private static readonly HashSet<string> ReadOnlyVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "list", "show", "status", "info", "check", "validate", "version", "search", "doctor", "help",
        "entries", "processes", "components", "plan",
    };

    /// <summary>
    /// Known state-changing verbs. They matter as <i>stops</i>: the first recognised verb in the path
    /// decides, so a positional that merely spells a read-only verb (a registry source named
    /// <c>list</c> in <c>registry sync list</c>) cannot downgrade a mutation to read-only.
    /// </summary>
    private static readonly HashSet<string> StateChangingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "sync", "block", "allow", "unblock", "enable", "disable", "install", "set", "add", "edit", "approve",
        "reject", "require", "scan", "start", "stop", "restart", "acknowledge", "init", "setup", "save",
        "migrate", "discover", "restore", "rotate-token", "apply", "run", "fix", "trigger", "update",
        "upgrade", "import", "export", "test", "quickstart",
    };

    /// <summary>
    /// Flags that turn an otherwise read-only verb into a change: <c>doctor --fix</c> repairs state
    /// (the 0.8.10 catalog found it touching PID files and connector setup).
    /// </summary>
    private static readonly HashSet<string> MutatingFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--fix",
    };

    private static readonly HashSet<string> ReadOnlyFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "--version", "--version-json",

        // A preview by definition: DefenseClaw's --dry-run verbs (alerts acknowledge|dismiss, doctor)
        // document "without mutating". A destructive verb with --dry-run is only a preview of it.
        "--dry-run",
    };

    /// <summary>Classifies an argv as handed to the CLI (without the executable itself).</summary>
    public static CommandTier Classify(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 0)
        {
            return CommandTier.StateChanging;
        }

        // Options end at "--": anything after it is a positional target, so a skill named "--help" or
        // "--dry-run" (skill quarantine -- --help) must not read as a preview flag.
        var options = argv.TakeWhile(a => a != "--").ToArray();
        if (options.Any(ReadOnlyFlags.Contains))
        {
            return CommandTier.ReadOnly;
        }

        var path = argv.TakeWhile(a => !a.StartsWith('-')).Take(MaxPathTokens).ToArray();

        if (path.Any(DestructiveVerbs.Contains))
        {
            return CommandTier.Destructive;
        }

        // The first recognised verb decides; destructive words anywhere in the path already won above
        // (over-warning is the safe direction). Unrecognised paths default to asking.
        var verb = path.FirstOrDefault(t => ReadOnlyVerbs.Contains(t) || StateChangingVerbs.Contains(t));
        return verb is not null && ReadOnlyVerbs.Contains(verb) && !options.Any(MutatingFlags.Contains)
            ? CommandTier.ReadOnly
            : CommandTier.StateChanging;
    }
}
