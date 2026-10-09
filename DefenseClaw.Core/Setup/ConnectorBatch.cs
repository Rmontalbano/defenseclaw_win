namespace DefenseClaw.Core.Setup;

/// <summary>Which connectors the batch adds beyond the ones ticked: the CLI's <c>--detected</c> and <c>--all</c>.</summary>
public enum BatchExtra
{
    /// <summary>Only the ticked connectors.</summary>
    None,

    /// <summary>Also every connector the CLI detects on this machine (<c>--detected</c>).</summary>
    Detected,

    /// <summary>Also every supported hook connector (<c>--all</c>).</summary>
    All,
}

/// <summary>
/// The batch form of <c>defenseclaw setup</c> (CUST-271): <c>setup --yes --connector X --connector Y [--detected | --all] --mode observe|action
/// [--no-restart]</c> - several connectors configured by one command, the scriptable form of the TUI's connector picker. Pure; the dialog
/// that builds it checks every flag against the installed CLI's own <c>setup --help</c> first.
/// <para>
/// Verified against 0.8.10 <c>setup --help</c> (<c>Fixtures/runtime-0.8.10/setup.txt</c>): <c>-c/--connector TEXT</c> (repeatable), <c>--detected</c>,
/// <c>--all</c>, <c>--mode [observe|action]</c> (default observe), <c>--restart / --no-restart</c> (default restart), <c>-y/--yes</c>. All of them
/// say "(no subcommand)": they act only when no connector subcommand follows, so this argv has none.
/// </para>
/// <para>
/// <b>It sets the roster, not adds to it.</b> The batch configures the connectors it selects; a connector that is configured now and not selected is
/// not carried over (the TUI pre-checks the active ones for that reason). The dialog starts with the configured connectors ticked and warns
/// when one is unticked.
/// </para>
/// </summary>
public static class ConnectorBatch
{
    public const string ObserveMode = "observe";

    public const string ActionMode = "action";

    /// <summary>The modes the CLI accepts, the default first.</summary>
    public static IReadOnlyList<string> Modes { get; } = new[] { ObserveMode, ActionMode };

    /// <summary>The flags the argv can carry, for the check against the CLI's help.</summary>
    public static IReadOnlyList<string> Flags { get; } = new[] { "--yes", "--connector", "--detected", "--all", "--mode", "--no-restart" };

    /// <summary>
    /// The batch command, or null when it selects nothing (no connector and no <c>--detected</c>/<c>--all</c>: the CLI would open its picker,
    /// which this app cannot answer). Connector ids are the CLI's own (<c>claudecode</c>, not <c>claude-code</c>), each once, in the order given.
    /// </summary>
    public static IReadOnlyList<string>? Argv(IEnumerable<string> connectors, BatchExtra extra, string mode, bool restart)
    {
        ArgumentNullException.ThrowIfNull(connectors);

        var ids = connectors
            .Select(static c => (c ?? string.Empty).Trim())
            .Where(static c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0 && extra == BatchExtra.None)
        {
            return null;
        }

        var argv = new List<string> { "setup", "--yes" };
        foreach (var id in ids)
        {
            argv.Add("--connector");
            argv.Add(id);
        }

        switch (extra)
        {
            case BatchExtra.Detected:
                argv.Add("--detected");
                break;
            case BatchExtra.All:
                argv.Add("--all");
                break;
        }

        argv.Add("--mode");
        argv.Add(string.Equals(mode, ActionMode, StringComparison.Ordinal) ? ActionMode : ObserveMode);
        if (!restart)
        {
            argv.Add("--no-restart");
        }

        return argv;
    }
}
