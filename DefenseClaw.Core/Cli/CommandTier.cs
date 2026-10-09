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
/// <see cref="CommandTier.StateChanging"/> — the safe default is to ask. The exceptions are a short list of
/// reads under <c>setup</c>, named in full (<see cref="IsReadOnlyLeaf"/>, <see cref="ReadOnlySetupResourceLeaves"/>), each read from the CLI's source, and three leaves of
/// <c>defenseclaw-gateway</c> (<c>connector verify</c>, <c>connector list-backups</c>, <c>policy domains</c>), each read from its help.
/// </para>
/// <para>
/// Two flag rules sit on top of the path. A read-only flag (<c>--help</c>, <c>--version</c>,
/// <c>--dry-run</c>) only counts when it stands alone (<see cref="IsStandaloneFlag"/>) — last, or followed by
/// another flag, and not the value of the option before it — because
/// <c>upgrade --version 0.9.0 --yes</c> is an upgrade to 0.9.0 (there <c>--version</c> takes a value), not a
/// version query. And a flag that makes the command print secret values (<see cref="SensitiveFlags"/>:
/// <c>config show --reveal</c>) is never read-only, since that output lands in Activity, exports and the
/// clipboard.
/// </para>
/// </summary>
public static class CommandTiers
{
    /// <summary>
    /// How many leading positional tokens can hold the verb (e.g. <c>agent discovery scan</c>,
    /// <c>setup splunk dashboards destroy</c>).
    /// </summary>
    public const int MaxPathTokens = 4;

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
    /// Leaves under <c>setup</c> that only read, named in full. <c>setup</c> is a state-changing verb and the first recognised verb decides,
    /// so every other <c>setup</c> command is a change; these four are the exception, each read from the DefenseClaw 0.8.10 source
    /// (<c>commands/cmd_setup_local_observability.py</c>, <c>observability/local_stack.py</c>): <c>url</c> and <c>env</c> print constants,
    /// <c>status</c> runs <c>docker compose ps</c> and probes loopback ports, and <c>logs</c> runs <c>docker compose logs --tail 200</c>.
    /// None of them writes config.yaml, starts or stops a container, or keeps an audit record. The rest of that group - <c>up</c>, <c>down</c>,
    /// <c>reset</c> - are not here: they change state, and <c>reset</c> is destructive. Matched on the exact three-token path, so a fourth
    /// token, another group, or a longer spelling never inherits the exemption.
    /// </summary>
    private static readonly HashSet<string> ReadOnlySetupLeaves = new(StringComparer.Ordinal)
    {
        "setup local-observability env", "setup local-observability logs", "setup local-observability status", "setup local-observability url",
    };

    /// <summary>
    /// The reads of the Setup list editors (CUST-270), named in full: <c>setup observability | webhook | trusted-paths list</c> and
    /// <c>setup webhook show</c>. Each prints what config.yaml holds (the CLI cuts a destination's or webhook's address itself) and writes
    /// nothing, read from <c>commands/cmd_setup_observability.py</c>, <c>cmd_setup_webhook.py</c> and the <c>trusted-paths</c> group of
    /// <c>cmd_setup.py</c>; <c>setup observability</c> has no <c>show</c>. Matched on the exact three-token path in <see cref="Classify"/> only -
    /// <see cref="IsReadOnlyLeaf"/> stays the local stack's four - and the options cannot change what they do, apart from the secret-printing
    /// ones, which <see cref="Classify"/> excludes for every read. They are not on <see cref="UnreviewedReadPaths"/>: the editors run them
    /// through their own whole-shape door (<c>SetupResourceArgv.IsRead</c>), and the palette's picks stay reviewed.
    /// </summary>
    private static readonly HashSet<string> ReadOnlySetupResourceLeaves = new(StringComparer.Ordinal)
    {
        "setup observability list", "setup trusted-paths list", "setup webhook list", "setup webhook show",
    };

    /// <summary>
    /// Leaves of <c>defenseclaw-gateway</c> that only read, named in full by their two-word path. No read verb in the shared vocabulary says so
    /// (<c>verify</c>, <c>list-backups</c> and <c>domains</c> are not on it, and adding them there would also move any <c>defenseclaw</c> command
    /// that ever had one of those words in its path), so the rule that an unrecognised verb is a change would make each of them one. Each is read
    /// from the DefenseClaw 0.8.10 gateway's help screens: <c>connector verify</c> checks that the connector left no residual state behind (exit 1
    /// is "residual state found", exit 2 "unknown connector"), <c>connector list-backups</c> lists the pristine backups under the data directory,
    /// and <c>policy domains</c> lists the firewall allowlist and blocklist of the active policy. (<c>watchdog status</c> is a read by its verb.)
    /// Their options (<c>--connector</c>, <c>--data-dir</c>, <c>--json</c>) do not change what they do. The rest of those groups are not here:
    /// <c>connector teardown</c> is destructive, and <c>connector reconcile</c>, <c>policy reload</c> and <c>watchdog start | stop</c> change state.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyGatewayLeaves = new(StringComparer.Ordinal)
    {
        "connector list-backups", "connector verify", "policy domains",
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

    /// <summary>
    /// Flags that make a command print secret values it otherwise masks (<c>config show --reveal</c>,
    /// <c>keys list --show-values</c>, <c>setup splunk --show-credentials</c>). The verb is a read, but its
    /// output is copied into the Activity panel, exported logs and the clipboard, so it is reviewed like a
    /// change - see <see cref="PrintsSecrets"/>.
    /// </summary>
    private static readonly HashSet<string> SensitiveFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--reveal", "--show-values", "--show-credentials",
    };

    /// <summary>
    /// The <c>defenseclaw</c> commands, as noun paths with no flags and no target, that may run with <b>no review step</b>
    /// when the app picks them (the command palette, the Overview's Diagnostics and doctor). An explicit list on purpose:
    /// <see cref="Classify"/> decides by the first recognised verb, so a command it calls read-only today can be one that
    /// changes state tomorrow (<c>plan apply</c> and <c>validate fix</c> are both "read-only" by that rule), and the palette's argv comes from
    /// a TUI command registry (<see cref="TuiRegistryCatalogues"/>) that a newer runtime extends and a regeneration can change, so it
    /// would run unreviewed the moment one added one. Every entry is a leaf of the
    /// DefenseClaw 0.8.10 command tree whose help says it lists, shows, checks or validates (or, for the four <c>setup local-observability</c>
    /// reads, prints or tails - read in the CLI's source, see <see cref="IsReadOnlyLeaf"/>); <c>CommandTierTreeTests</c> pins this set to
    /// the reviewed read-only leaves of that tree, so growing it is a decision made in two places, after reading the new command's help.
    /// Anything not on it goes through a review, whatever <see cref="Classify"/> says.
    /// </summary>
    public static IReadOnlyCollection<string> UnreviewedReadPaths => UnreviewedReads;

    private static readonly HashSet<string> UnreviewedReads = new(StringComparer.Ordinal)
    {
        "agent components history", "agent components show", "agent confidence policy show", "agent confidence policy validate",
        "agent discovery status", "agent processes", "agent signatures list", "agent signatures validate",
        "codeguard status", "config show", "config validate", "doctor",
        "guardrail judge list", "guardrail status", "keys check", "keys list", "mcp list", "migrations status",
        "observability plan", "plugin info", "plugin list", "policy list", "policy show", "policy validate",
        "registry entries", "registry list", "registry show", "setup local-observability env", "setup local-observability logs",
        "setup local-observability status", "setup local-observability url", "skill info", "skill list", "skill search",
        "status", "tool list", "tool status", "version",
    };

    /// <summary>
    /// The <c>defenseclaw-gateway</c> commands the app runs with no review: the two reads on the Overview's Diagnostics menu, and the four
    /// reads the command palette offers beside them (<c>watchdog status</c>, <c>connector verify</c>, <c>connector list-backups</c> and
    /// <c>policy domains</c>, each read from the 0.8.10 gateway's help; see <see cref="ReadOnlyGatewayLeaves"/>). The gateway's other verbs
    /// (start, stop, restart, <c>watchdog start | stop</c>, <c>policy reload</c>, <c>connector teardown</c> …) are never on this list.
    /// </summary>
    public static IReadOnlyCollection<string> UnreviewedGatewayReadPaths => UnreviewedGatewayReads;

    private static readonly HashSet<string> UnreviewedGatewayReads = new(StringComparer.Ordinal)
    {
        "status", "provenance show", "watchdog status", "connector list-backups", "connector verify", "policy domains",
    };

    /// <summary>
    /// True when <paramref name="argv"/> is exactly one of <see cref="UnreviewedReadPaths"/>: those nouns and nothing else (no flag, no
    /// target), so <c>doctor --fix</c> and <c>config show --reveal</c> are not on the list even though their verbs are.
    /// </summary>
    public static bool IsUnreviewedRead(IReadOnlyList<string> argv) => IsOnList(argv, UnreviewedReads);

    /// <summary>The same for <c>defenseclaw-gateway</c>: exactly one of <see cref="UnreviewedGatewayReadPaths"/>.</summary>
    public static bool IsUnreviewedGatewayRead(IReadOnlyList<string> argv) => IsOnList(argv, UnreviewedGatewayReads);

    private static bool IsOnList(IReadOnlyList<string> argv, HashSet<string> list)
    {
        ArgumentNullException.ThrowIfNull(argv);

        // Token by token, never a joined line: one argument that spells "doctor --fix" is not the path "doctor".
        foreach (var token in argv)
        {
            if (string.IsNullOrEmpty(token) || token.StartsWith('-') || token.Any(char.IsWhiteSpace))
            {
                return false;
            }
        }

        return argv.Count > 0 && list.Contains(string.Join(' ', argv));
    }

    /// <summary>
    /// True when <paramref name="argv"/> asks the command to print secret values (any of
    /// <c>--reveal</c>, <c>--show-values</c>, <c>--show-credentials</c> before a <c>--</c>). A review says so in
    /// a warning of its own: the tier alone only says "Changes state".
    /// </summary>
    public static bool PrintsSecrets(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return OptionsOf(argv).Any(SensitiveFlags.Contains);
    }

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
        var options = OptionsOf(argv);
        if (HasStandaloneReadOnlyFlag(options))
        {
            return CommandTier.ReadOnly;
        }

        var path = argv.TakeWhile(a => !a.StartsWith('-')).Take(MaxPathTokens).ToArray();

        if (path.Any(DestructiveVerbs.Contains))
        {
            return CommandTier.Destructive;
        }

        // A read under "setup", named in full (the local stack's status, logs, url, env; the list editors' lists and webhook show): "setup" would
        // make it a change. So is one of the gateway's reads that the verb vocabulary does not know (connector verify, connector list-backups, policy domains).
        if ((IsReadOnlyLeafPath(path) || IsReadOnlySetupResourcePath(path) || IsReadOnlyGatewayLeafPath(path)) && !options.Any(MutatingFlags.Contains) && !options.Any(SensitiveFlags.Contains))
        {
            return CommandTier.ReadOnly;
        }

        // The first recognised verb decides; destructive words anywhere in the path already won above
        // (over-warning is the safe direction). Unrecognised paths default to asking.
        var verb = path.FirstOrDefault(t => ReadOnlyVerbs.Contains(t) || StateChangingVerbs.Contains(t));
        return verb is not null && ReadOnlyVerbs.Contains(verb) &&
               !options.Any(MutatingFlags.Contains) && !options.Any(SensitiveFlags.Contains)
            ? CommandTier.ReadOnly
            : CommandTier.StateChanging;
    }

    /// <summary>
    /// True when the path of <paramref name="argv"/> (its leading tokens before the first flag) is exactly one of the leaves under
    /// <c>setup</c> that only read (<c>setup local-observability status | logs | url | env</c>, read from the 0.8.10 source). By path alone:
    /// no flag, however spelled, can make a path into one of these, and a flag value never reaches the path. What a surface that builds the
    /// command itself (a wizard, whose floor is otherwise "a change") uses to know the command it builds is one of these reads.
    /// </summary>
    public static bool IsReadOnlyLeaf(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        // Options end at "--", like the classifier's: a positional after it is a target, never part of the path.
        return IsReadOnlyLeafPath(OptionsOf(argv).TakeWhile(a => !a.StartsWith('-')).Take(MaxPathTokens).ToArray());
    }

    private static bool IsReadOnlyLeafPath(string[] path) =>
        path.Length == 3 && ReadOnlySetupLeaves.Contains(string.Join(' ', path));

    private static bool IsReadOnlySetupResourcePath(string[] path) =>
        path.Length == 3 && ReadOnlySetupResourceLeaves.Contains(string.Join(' ', path));

    private static bool IsReadOnlyGatewayLeafPath(string[] path) =>
        path.Length == 2 && ReadOnlyGatewayLeaves.Contains(string.Join(' ', path));

    private static string[] OptionsOf(IReadOnlyList<string> argv) => argv.TakeWhile(a => a != "--").ToArray();

    /// <summary>
    /// A read-only flag counts only as a flag: the last option, or one followed by another flag. Followed by
    /// a plain word it is the value of a value-taking option - <c>--version 0.9.0</c> on <c>upgrade</c> - and
    /// says nothing about whether the command changes state.
    /// </summary>
    private static bool HasStandaloneReadOnlyFlag(string[] options)
    {
        for (var i = 0; i < options.Length; i++)
        {
            if (ReadOnlyFlags.Contains(options[i]) && IsStandaloneFlag(options, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <c>tokens[index]</c>, a token that spells a flag, really is one: not the value of the option before
    /// it (<c>--name --dry-run</c>: a name), and not followed by a plain word (<c>--version 0.9.0</c>: the flag is
    /// itself an option that takes that word). Only what comes before a <c>--</c> should be passed.
    /// <para>
    /// Whether the option before it takes a value is not known here (that is per command), so it is judged by its
    /// shape: <c>--opt=value</c> is self-contained, a <see cref="IsSwitch"/> takes nothing, and any other option
    /// is assumed to take the next token. That errs toward "a value": the flag stops counting, and a read-only
    /// flag that stops counting only ever makes a command look <i>less</i> harmless.
    /// </para>
    /// </summary>
    public static bool IsStandaloneFlag(IReadOnlyList<string> tokens, int index)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (index + 1 < tokens.Count && !tokens[index + 1].StartsWith('-'))
        {
            return false;
        }

        if (index == 0)
        {
            return true;
        }

        var previous = tokens[index - 1];
        return !previous.StartsWith('-') || previous.Contains('=', StringComparison.Ordinal) || IsSwitch(previous);
    }

    /// <summary>
    /// Options known to take no value, so a flag right after one is a flag and not its argument: the read-only
    /// flags themselves, the confirm/format switches every wizard and list verb has, and anything spelled
    /// <c>--no-…</c> or <c>--skip-…</c> (a CLI convention for booleans).
    /// </summary>
    public static bool IsSwitch(string option) =>
        // Not --version: it is a bare flag on the root command but takes a value on `upgrade`.
        option.Equals("--help", StringComparison.OrdinalIgnoreCase) || option.Equals("--dry-run", StringComparison.OrdinalIgnoreCase) ||
        option.Equals("--version-json", StringComparison.OrdinalIgnoreCase) ||
        SensitiveFlags.Contains(option) || MutatingFlags.Contains(option) || CommonSwitches.Contains(option) ||
        option.StartsWith("--no-", StringComparison.Ordinal) || option.StartsWith("--skip-", StringComparison.Ordinal);

    private static readonly HashSet<string> CommonSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "--yes", "-y", "--json", "--json-output", "--json-summary", "--non-interactive", "--restart", "--verbose", "-v",
        "--quiet", "--force", "--all", "--refresh", "--scan", "--replace", "--summary", "--effective", "--provenance",
        "--follow", "--enable", "--disable", "--remove", "--verify", "--clear", "--show-entries", "--show-gone",
    };
}
