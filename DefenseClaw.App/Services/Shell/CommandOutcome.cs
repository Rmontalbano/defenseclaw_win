using System.IO;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>
/// What a finished command did, in the words of the TUI's Activity footer (<c>activity.py: ActivityEntry.meta_footer</c>): which side effects it
/// had and what to do next - <c>config reloaded · gateway restarted · doctor cache refreshed · next: rerun readiness</c>. The Activity row shows it
/// under the command, so an operator sees at a glance whether a command changed anything and where to go from there, without reading the output.
/// <para>
/// <b>Each side effect is claimed only on evidence</b> (the TUI's own rule: "only flip when the caller positively observed the side effect"):
/// </para>
/// <list type="bullet">
/// <item><description><b>config reloaded</b> - the run exited 0 and was a <c>defenseclaw setup | guardrail | settings | init | registry</c> command
/// (the TUI's list), unless it only reads (<see cref="InstallationGate"/> calls it a read, by <see cref="CommandTiers"/> or by the whole shape of the
/// argv: <c>guardrail status</c>, <c>registry list</c>, <c>--help</c>, <c>--dry-run</c>, a Setup editor's <c>setup observability list --json</c>, which
/// <see cref="CommandTiers"/> names as a read leaf), or a
/// gateway restart - which re-reads the config - happened.</description></item>
/// <item><description><b>gateway restarted</b> - the run exited 0 and restarts the gateway by the same rule the review states before it runs
/// (<see cref="CommandReview.RestartsGatewayFor"/>: a <c>setup</c> or <c>guardrail</c> verb that writes config.yaml, unless told not to), or it was
/// <c>defenseclaw-gateway restart</c> itself.</description></item>
/// <item><description><b>doctor cache refreshed</b> - a <c>doctor</c> run, and <c>doctor_cache.json</c> was written while it ran (its modified
/// time is between the run's start and its end): the cache is the file <c>doctor</c> rewrites at the end of every run, passing or failing, and
/// nothing else writes it.</description></item>
/// <item><description><b>next: …</b> - <see cref="CommandNextAction"/>, the TUI's hint table. None after a cancel (the operator's own doing).</description></item>
/// </list>
/// A run that never started (refused by <see cref="CliRunner.RecordRefusal"/>) or was handed to a terminal has nothing to say, and neither has an
/// entry for anything but DefenseClaw's own commands (the signature checks, the installer).
/// </summary>
/// <param name="ConfigReloaded">The command rewrote or re-read the configuration.</param>
/// <param name="GatewayRestarted">The command restarted the gateway.</param>
/// <param name="DoctorCacheRefreshed">The doctor rewrote <c>doctor_cache.json</c>.</param>
/// <param name="NextAction">The one-line hint, or empty.</param>
internal sealed record CommandOutcome(bool ConfigReloaded, bool GatewayRestarted, bool DoctorCacheRefreshed, string NextAction)
{
    /// <summary>The words of the TUI's footer, in its order: state changes first, the hint last, where the eye lands.</summary>
    public const string ConfigReloadedText = "config reloaded";

    public const string GatewayRestartedText = "gateway restarted";

    public const string DoctorCacheRefreshedText = "doctor cache refreshed";

    /// <summary>The prefix of the hint: <c>next: review readiness</c>.</summary>
    public const string NextPrefix = "next: ";

    /// <summary>The separator of the footer's parts.</summary>
    public const string Separator = " · ";

    /// <summary>
    /// How far after a run's end a write of the doctor cache still counts as the run's: the file is written before the process exits, but the
    /// end of the run is stamped after its output has drained, and a file system's clock is coarser than the process's.
    /// </summary>
    public static readonly TimeSpan CacheWriteSlack = TimeSpan.FromSeconds(2);

    /// <summary>The <c>defenseclaw</c> commands (by first word) whose success means the configuration was rewritten, as the TUI lists them.</summary>
    private static readonly HashSet<string> ConfigCommands = new(StringComparer.Ordinal) { "setup", "guardrail", "settings", "init", "registry" };

    public static CommandOutcome None { get; } = new(false, false, false, string.Empty);

    /// <summary>The footer text, or an empty string when there is nothing to say.</summary>
    public string Text
    {
        get
        {
            var parts = new List<string>(4);
            if (ConfigReloaded)
            {
                parts.Add(ConfigReloadedText);
            }

            if (GatewayRestarted)
            {
                parts.Add(GatewayRestartedText);
            }

            if (DoctorCacheRefreshed)
            {
                parts.Add(DoctorCacheRefreshedText);
            }

            if (NextAction.Length > 0)
            {
                parts.Add(NextPrefix + NextAction);
            }

            return string.Join(Separator, parts);
        }
    }

    /// <summary>
    /// The outcome of <paramref name="invocation"/>. Empty while it runs.
    /// </summary>
    /// <param name="invocation">The entry.</param>
    /// <param name="doctorCacheWrittenUtc">
    /// When <c>doctor_cache.json</c> was last written (<see cref="StampOf"/>), or null when it is not there or the caller did not look. Only
    /// read for a <c>doctor</c> run.
    /// </param>
    public static CommandOutcome Of(CliInvocation invocation, DateTime? doctorCacheWrittenUtc = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (invocation.IsRunning || CommandRerun.ToolOf(invocation) is not { } tool)
        {
            return None;
        }

        // Refused before it started, or handed to a terminal: nothing ran, so there is nothing to describe.
        var reason = invocation.FailureReason;
        if ((reason is not null && reason.StartsWith(CliRunner.RefusedPrefix, StringComparison.Ordinal)) || (invocation.ExitCode is null && reason is null))
        {
            return None;
        }

        var argv = invocation.Argv;
        var cli = string.Equals(tool, CommandReview.DefaultExecutable, StringComparison.Ordinal);
        var succeeded = invocation.ExitCode == 0 && reason is null;
        var cancelled = reason is not null && reason.StartsWith("cancelled", StringComparison.Ordinal);

        var restarted = succeeded && (cli ? CommandReview.RestartsGatewayFor(argv) : argv.Count == 1 && argv[0] == "restart");
        // A read is a read by the guard's word, which knows the whole-shape reads the first-verb classifier cannot prove (a Setup editor's list, the
        // redaction status): a list that was only looked at reloaded nothing.
        var reloaded = restarted ||
                       (succeeded && cli && argv.Count > 0 && ConfigCommands.Contains(argv[0]) && !InstallationGate.IsReadOnly(tool, argv));
        var doctor = cli && argv.Count > 0 && argv[0] == "doctor" && doctorCacheWrittenUtc is { } written && WroteDuring(invocation, written);

        // A run that was stopped has no exit code; the hint treats it as the failure it is (and says nothing for the operator's own cancel).
        var next = cancelled ? string.Empty : CommandNextAction.For(invocation.Executable, argv, succeeded ? 0 : invocation.ExitCode ?? 1);

        return new CommandOutcome(reloaded, restarted, doctor, next);
    }

    /// <summary>True when a file written at <paramref name="writtenUtc"/> was written while <paramref name="invocation"/> ran.</summary>
    internal static bool WroteDuring(CliInvocation invocation, DateTime writtenUtc)
    {
        var started = invocation.StartedAt.UtcDateTime;
        var finished = (invocation.FinishedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        return writtenUtc >= started && writtenUtc <= finished + CacheWriteSlack;
    }

    /// <summary>
    /// When the file at <paramref name="path"/> was last written (UTC), or null when it is not there or cannot be looked at. A stat: nothing is
    /// opened or read.
    /// </summary>
    public static DateTime? StampOf(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
