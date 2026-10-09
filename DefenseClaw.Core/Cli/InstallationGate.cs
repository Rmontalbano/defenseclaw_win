using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Core.Redaction;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// The one rule that decides whether a run may happen while the installation is read-only (managed or invalid; see
/// <see cref="InstallationContext"/>). <see cref="CliRunner"/> asks it before it records or starts anything, so there is exactly one enforcement
/// point: a panel that forgets to disable a button, a keyboard shortcut or a palette row cannot change a managed installation, it gets a
/// refusal in Activity instead. Disabled buttons and reviews are courtesy; this is the guard.
/// <para>
/// <b>The rule.</b> A run is allowed when its tier is <see cref="CommandTier.ReadOnly"/> and refused otherwise. For <c>defenseclaw</c> and
/// <c>defenseclaw-gateway</c> the tier is <see cref="CommandTiers.Classify"/> over the argv up to a <c>--</c> (the same classifier every review
/// uses, so a preview with a standalone <c>--dry-run</c> and a <c>list</c>, <c>show</c> or <c>status</c> read still run), plus the reads that the
/// classifier cannot prove but that a Core module recognises by the whole shape of the argv (the Policies catalog reads, the redaction
/// reads and previews, the Runtime panel's permissions read). The Setup editors' <c>list --json</c> and <c>webhook show --json</c> are reads by the classifier itself (named leaves). <b>Any other program is a change</b>: an installer, a script, a tool the app does
/// not know. Unknown is never a read.
/// </para>
/// <para>
/// <b>Not enforced here.</b> The first-verb classifier calls a few commands read-only that a newer CLI could make write (<c>plan apply</c>);
/// <see cref="CommandTiers.UnreviewedReadPaths"/> and its tree test exist for that. The gate is as strong as the tiers, and says so.
/// </para>
/// </summary>
public static class InstallationGate
{
    /// <summary>
    /// The tier of running <paramref name="argv"/> on <paramref name="executable"/> (a name or a path).
    /// </summary>
    public static CommandTier TierOf(string executable, IReadOnlyList<string> argv)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(argv);

        var name = Path.GetFileNameWithoutExtension(executable);
        var isCli = string.Equals(name, "defenseclaw", StringComparison.OrdinalIgnoreCase);
        if (!isCli && !string.Equals(name, "defenseclaw-gateway", StringComparison.OrdinalIgnoreCase))
        {
            return CommandTier.StateChanging;
        }

        // Only what comes before a "--" is classified: everything after it is a target, so one named --help is not a preview flag.
        var terminator = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], "--", StringComparison.Ordinal))
            {
                terminator = i;
                break;
            }
        }

        // "setup" is a verb of defenseclaw only: the gateway has none, so those words handed to it are not the CLI's reads.
        if (!isCli && argv.Count > 0 && string.Equals(argv[0], "setup", StringComparison.Ordinal))
        {
            return CommandTier.StateChanging;
        }

        var tier = CommandTiers.Classify(terminator < 0 ? argv : argv.Take(terminator).ToArray());
        return tier != CommandTier.ReadOnly && isCli && IsKnownRead(argv) ? CommandTier.ReadOnly : tier;
    }

    /// <summary>True when the run may go ahead on a read-only installation.</summary>
    public static bool IsReadOnly(string executable, IReadOnlyList<string> argv) => TierOf(executable, argv) == CommandTier.ReadOnly;

    /// <summary>
    /// Null when the run may go ahead on this installation; otherwise the one-sentence reason it may not (the installation's own). A writable
    /// installation allows everything.
    /// </summary>
    public static string? RefusalFor(InstallationContext context, string executable, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.IsMutable || IsReadOnly(executable, argv) ? null : context.BlockedReason;
    }

    /// <summary>
    /// The reason a refused run is recorded with (<see cref="CliRunner.RecordRefusal"/>, which puts it in Activity as "refused - &lt;reason&gt;" and
    /// says nothing was run): that reads still run here, then the installation's own sentence.
    /// </summary>
    public static string RefusalReason(InstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"DefenseClaw for Windows runs only read-only commands against this installation. {context.BlockedReason}";
    }

    /// <summary>
    /// The reads the first-verb classifier cannot prove: each is recognised by a Core module that builds that argv itself and matches the
    /// <i>whole</i> shape, never a prefix.
    /// </summary>
    private static bool IsKnownRead(IReadOnlyList<string> argv) =>
        PolicyActionGuard.IsAllowedRead(argv) ||
        RedactionArgv.IsRead(argv) ||
        RedactionArgv.IsPreview(argv) ||
        AiRuntimeCommands.IsPermissionsRead(argv);
}
