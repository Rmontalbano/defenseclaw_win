using System.Diagnostics;
using System.Globalization;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// Raised when a secret would have been placed on the command line. Argv is visible to
/// every process on the box (Task Manager, WMI, ETW) and to DefenseClaw's own hook
/// scanners, so this is a hard failure rather than a warning.
/// </summary>
public sealed class SecretInArgumentException : InvalidOperationException
{
    public SecretInArgumentException(int argumentIndex)
        : base($"Refusing to run: argument at index {argumentIndex} contains a secret. " +
               "Pass secrets via the stdinSecret parameter or the CliRunOptions environment overlay instead — never on the command line.")
    {
        ArgumentIndex = argumentIndex;
    }

    public int ArgumentIndex { get; }
}

/// <summary>
/// Raised when the CLI's own Windows argument expansion (<c>%VAR%</c>, <c>$VAR</c>, <c>~</c>, wildcards) would
/// change an argument, and the caller asked not to run in that case
/// (<see cref="CliRunOptions.RefuseExpandingTargets"/>). What was reviewed is not what would run.
/// </summary>
public sealed class ArgumentExpansionException : InvalidOperationException
{
    public ArgumentExpansionException(IReadOnlyList<ArgvHazard> hazards)
        : base(BuildMessage(hazards))
    {
        Hazards = hazards;
    }

    /// <summary>Every argument that would change, with what it would become.</summary>
    public IReadOnlyList<ArgvHazard> Hazards { get; }

    /// <summary>The one sentence a panel shows when it refuses: what changes, and what to do about it.</summary>
    public static string BuildMessage(IReadOnlyList<ArgvHazard> hazards)
    {
        ArgumentNullException.ThrowIfNull(hazards);
        return "Refusing to run: the DefenseClaw CLI expands %VARIABLES%, $VARIABLES, ~ and wildcards in every argument on Windows, " +
               $"so it would act on something other than what is shown - {string.Join("; ", hazards.Select(h => h.Describe()))}. " +
               "Rename it (avoid * ? [ % $ and a leading ~) and try again.";
    }
}

/// <summary>Thrown when the requested DefenseClaw executable is not on PATH or in the install dir.</summary>
public sealed class CliNotFoundException : FileNotFoundException
{
    public CliNotFoundException(string executableName, IEnumerable<string> probed)
        : base($"Could not find '{executableName}'. Probed: {string.Join("; ", probed)}")
    {
        ExecutableName = executableName;
    }

    public string ExecutableName { get; }
}

/// <summary>
/// Per-call lifecycle options for <see cref="CliRunner"/>. Every property has a default that
/// is right for an ordinary status/list/mutation call, so most call sites pass none of this —
/// the options exist for the few that are legitimately different.
/// <para>
/// <b>Two independent decisions.</b> How long a run may take (<see cref="Timeout"/>) and whether
/// it may be ended by the app exiting (<see cref="SurvivesShutdown"/>) are separate: a wizard
/// that may run for many minutes still <i>should</i> be stopped if the operator quits the app,
/// while an installer that replaces this app's own binaries must not be. The presets below
/// pair them the way the call sites in this repo need.
/// </para>
/// </summary>
public sealed record CliRunOptions
{
    // Declared before the presets below: static initialisers run in textual order, and every preset
    // reads this as the default of EnvironmentOverlay.
    private static readonly IReadOnlyDictionary<string, SecretValue> NoEnvironment =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, SecretValue>(
            new Dictionary<string, SecretValue>(0, StringComparer.OrdinalIgnoreCase));

    /// <summary>Nothing overridden: the runner's inferred timeout, and the run is ended at app exit.</summary>
    public static CliRunOptions Default { get; } = new();

    /// <summary>
    /// For interactive setup flows that can legitimately run for minutes (image pulls,
    /// scanner downloads): <see cref="CliRunner.LongRunningTimeout"/>, still stopped at app exit.
    /// </summary>
    public static CliRunOptions LongRunning { get; } = new() { Timeout = CliRunner.LongRunningTimeout };

    /// <summary>
    /// No timeout at all. Use only where the operator watches the run and can cancel it —
    /// an unbounded run with no cancel affordance is exactly the wedge this type exists to prevent.
    /// </summary>
    public static CliRunOptions NoTimeout { get; } = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>
    /// For the in-app upgrade — the Setup installer and the <c>defenseclaw-upgrade.ps1</c>
    /// resolver: no timeout <b>and</b> <see cref="SurvivesShutdown"/>. Killing an installer
    /// mid-install can leave the machine with a half-replaced DefenseClaw, which is strictly
    /// worse than letting it finish after the tray has gone.
    /// </summary>
    public static CliRunOptions Installer { get; } = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        SurvivesShutdown = true,
    };

    /// <summary>
    /// For a call whose stdout is parsed by the app — <c>&lt;noun&gt; list --json</c>, <c>info --json</c>,
    /// <c>status --json</c>: the runner's inferred timeout and <see cref="RetainFullOutput"/>. Without
    /// it a long JSON document (a few dozen skills at ~35 indented lines each is already past the
    /// ordinary 2,000-line cap) loses its head and no longer parses. The Activity panel still shows
    /// the whole thing, since it reads the same transcript.
    /// </summary>
    public static CliRunOptions JsonRead { get; } = new() { RetainFullOutput = true };

    /// <summary>An explicit ceiling for this call, overriding the runner's inferred one.</summary>
    public static CliRunOptions WithTimeout(TimeSpan timeout) => new() { Timeout = timeout };

    /// <summary>
    /// The longest environment variable name the overlay accepts. Far beyond any real one; it only
    /// exists so a runaway string cannot be turned into a child's environment block.
    /// </summary>
    public const int MaxEnvironmentNameLength = 128;

    /// <summary>
    /// True for a name the overlay will set: a letter or underscore, then letters, digits and
    /// underscores. Deliberately stricter than what Windows allows — no <c>=</c>, no spaces, no
    /// punctuation — because the name comes from a table in this app and a value that does not fit
    /// this shape is a bug, not something to pass to <c>CreateProcess</c>.
    /// </summary>
    public static bool IsValidEnvironmentName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxEnvironmentNameLength)
        {
            return false;
        }

        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Environment variables set for <b>this one child</b>, on top of the app's own environment — the
    /// way to hand a CLI a secret it reads from an environment variable without putting the value on
    /// argv (visible to every process on the machine) and without a console to type it into.
    /// <para>
    /// <b>The values never leave the child.</b> They are applied to the child's
    /// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> and nowhere else: the app's own
    /// process environment is not touched (so a later run does not inherit them), the invocation
    /// records only the <i>names</i> (<see cref="CliInvocation.EnvironmentNames"/>), the display
    /// command line is argv alone, and — exactly as for <c>stdinSecret</c> — each value is refused if it
    /// turns up in argv and is scrubbed out of captured output. <see cref="SecretValue.ToString"/> is
    /// redacted, so an accidental log line prints a placeholder.
    /// </para>
    /// <para>
    /// An entry whose value <see cref="SecretValue.IsEmpty"/> is <b>skipped</b>, not set to an empty
    /// string: DefenseClaw's own <c>.env</c> loader never overwrites a variable that is already present,
    /// and an empty one is present — setting it would shadow the value stored in
    /// <c>~/.defenseclaw/.env</c>.
    /// </para>
    /// <para>
    /// Names are matched case-insensitively (as Windows does). Build it with
    /// <see cref="WithEnvironment(string, SecretValue)"/>; <see cref="CliRunner"/> re-validates whatever it
    /// is given and throws <see cref="ArgumentException"/> (naming the variable, never the value) before
    /// anything is recorded or started.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, SecretValue> EnvironmentOverlay { get; init; } = NoEnvironment;

    /// <summary>
    /// A copy of these options that also sets <paramref name="name"/> to <paramref name="value"/> in the
    /// child's environment (replacing an earlier entry of the same name). See <see cref="EnvironmentOverlay"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid variable name.</exception>
    public CliRunOptions WithEnvironment(string name, SecretValue value)
    {
        if (!IsValidEnvironmentName(name))
        {
            throw new ArgumentException(
                "Environment variable names must be letters, digits and underscores, starting with a letter or underscore.",
                nameof(name));
        }

        ArgumentNullException.ThrowIfNull(value);

        var merged = new Dictionary<string, SecretValue>(EnvironmentOverlay, StringComparer.OrdinalIgnoreCase)
        {
            [name] = value,
        };

        return this with { EnvironmentOverlay = new System.Collections.ObjectModel.ReadOnlyDictionary<string, SecretValue>(merged) };
    }

    // The synthesized ToString would list the overlay's contents through its own ToString; naming the
    // variables here keeps a stray log of the options useful without printing a value, redacted or not.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"RetainFullOutput = {RetainFullOutput}, Timeout = {Timeout}, SurvivesShutdown = {SurvivesShutdown}, RefuseExpandingTargets = {RefuseExpandingTargets}, EnvironmentNames = [{string.Join(", ", EnvironmentOverlay.Keys)}]");
        return true;
    }

    /// <summary>
    /// Lifts this invocation's retention caps from the ordinary
    /// <see cref="CliInvocation.MaxRetainedOutputLines"/> / <see cref="CliInvocation.MaxRetainedOutputBytes"/>
    /// (2,000 lines / 256 KiB, oldest dropped) up to <see cref="CliInvocation.MaxFullOutputLines"/> /
    /// <see cref="CliInvocation.MaxFullOutputBytes"/> (200,000 lines / 16 MiB).
    /// <para>
    /// <b>Why it exists.</b> The ordinary cap is right for a status line or a mutation's chatter, and wrong
    /// for output a program has to read whole: dropping the <i>oldest</i> lines of a JSON document
    /// removes the opening bracket, so the parse fails and the panel shows an error for a healthy CLI.
    /// The cap exists to bound memory in a tray app that runs for weeks, not to make machine-parsed
    /// output incomplete, so callers that parse stdout opt in (see <see cref="JsonRead"/>).
    /// </para>
    /// <para>
    /// <b>It is still a ceiling.</b> Past the full-output limits the invocation behaves exactly as an
    /// ordinary one does: drop-oldest, with an explicit <see cref="CliStream.Notice"/> marker, so a
    /// runaway process cannot pin an unbounded transcript. Nothing else changes: the timeout, the
    /// secret scrubbing and shutdown behaviour are independent of this flag.
    /// </para>
    /// </summary>
    public bool RetainFullOutput { get; init; }

    /// <summary>
    /// How long the child may run before its whole process tree is killed.
    /// <para>
    /// <c>null</c> (the default) means "let the runner decide" — <see cref="CliRunner.ResolveTimeout"/>
    /// picks <see cref="CliRunner.DefaultTimeout"/> or a longer tier from the argv.
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> means no timeout. Anything else must
    /// be positive.
    /// </para>
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// True when <see cref="CliRunner.Shutdown"/> / <see cref="CliRunner.Dispose"/> must leave
    /// this child alone, and must not refuse to start it once shutdown has begun.
    /// <para>
    /// This governs <i>app exit only</i>. A caller's own <see cref="CancellationToken"/> is still
    /// honoured — cancelling is an explicit act by whoever holds the token, and it kills the
    /// tree exactly as for any other run.
    /// </para>
    /// </summary>
    public bool SurvivesShutdown { get; init; }

    /// <summary>
    /// Refuse to start (throwing <see cref="ArgumentExpansionException"/>) when the DefenseClaw CLI's own argument
    /// expansion would change a <b>target</b> - an argument after the <c>--</c> terminator, which names something
    /// that came from outside (a skill or server name) - see <see cref="ArgvHazards"/>. A run that acts on a different
    /// target than the one confirmed is worse than no run. Off by default, and it looks at targets only: an
    /// option value an operator typed (<c>--command %USERPROFILE%\x</c>) is <i>meant</i> to expand, and its review
    /// already says what it becomes.
    /// </summary>
    public bool RefuseExpandingTargets { get; init; }

    /// <summary>
    /// Called once, right after the child has been started (<see cref="System.Diagnostics.Process.Start()"/> returned) and before any of its
    /// output is read: the moment a caller that kept something open for the launch lets go of it. The upgrade holds the staged installer open
    /// without write or delete sharing from its last hash until here, so the file that was hashed is the file that starts. Not called when
    /// the child could not be started. It runs on the supervising thread, so it must be quick; whatever it throws is traced and dropped, because
    /// a callback must never be able to orphan a child that is already running.
    /// </summary>
    public Action? OnProcessStarted { get; init; }
}

/// <summary>
/// Runs the DefenseClaw CLIs and records every invocation.
/// <para>
/// This is the app's only write path: the GUI never edits DefenseClaw state directly, so
/// each mutation is a subprocess whose exact argv, live output and exit code land in
/// <see cref="Activity"/> for the Activity panel to show.
/// </para>
/// <para>
/// Secrets are accepted only through <c>stdinSecret</c> or the per-run environment overlay
/// (<see cref="CliRunOptions.EnvironmentOverlay"/>). Anything that would put one in
/// argv throws <see cref="SecretInArgumentException"/>. On the way out, the run's own
/// <c>stdinSecret</c>, its overlay values and every secret passed to <see cref="RegisterSecret"/> are scrubbed from
/// captured output before it is stored or streamed — the wizards promise the value "will not
/// appear in the Activity panel or in captured output", and a child that echoes what it read on
/// stdin would otherwise break that promise.
/// </para>
/// <para>
/// <b>Two independent caps.</b> This class bounds the ring by entry count
/// (<see cref="ActivityCapacity"/>); <see cref="CliInvocation"/> separately bounds each
/// entry's retained transcript in lines and bytes. Both are needed — an entry cap alone
/// leaves total memory a function of how chatty the commands were, which is exactly the
/// unbounded case for a tray app that stays resident for weeks. Argv, exit code and
/// failure reason are never trimmed by either cap. A call whose output is machine-parsed opts
/// into higher per-invocation ceilings with <see cref="CliRunOptions.RetainFullOutput"/>
/// (<see cref="CliRunOptions.JsonRead"/>); it is still bounded, just far above the ordinary cap.
/// </para>
/// <para>
/// <b>Every child has a bounded life.</b> A child that never exits used to wedge whatever
/// awaited it — a panel's busy flag, the tray's Start/Stop Gateway latch — for as long as the
/// app ran, because most call sites pass no <see cref="CancellationToken"/>. So the runner
/// applies its own ceiling to every run (<see cref="ResolveTimeout"/>), and there are four
/// ways a run can be ended early: the timeout, the caller's token, an operator's
/// <see cref="Cancel(CliInvocation, out string)"/> (the Activity panel's button, which holds
/// the invocation and not the token), and app exit (<see cref="Shutdown"/>). All four do the
/// same thing: <see cref="Process.Kill(bool)"/> with
/// <c>entireProcessTree: true</c> — the CLIs are Python launchers and PowerShell wrappers, so
/// killing only the direct child would leave the interpreter or installer it spawned running —
/// then finish the invocation with a <see cref="CliInvocation.FailureReason"/> that says which
/// of the three it was. An invocation is never left <see cref="CliInvocation.IsRunning"/> by any
/// of them; <see cref="InvocationCompleted"/> is raised exactly once per recorded invocation.
/// </para>
/// <para>
/// <b>The exemption.</b> Some children must outlive all of that: the upgrade installer can
/// legitimately run for minutes, and killing it mid-install can corrupt the installation. Those
/// pass <see cref="CliRunOptions.Installer"/> (no timeout, survives shutdown). See
/// <see cref="CliRunOptions"/> for the presets and <see cref="Shutdown"/> for what survival means.
/// </para>
/// </summary>
public sealed class CliRunner : IDisposable
{
    /// <summary>
    /// Ceiling applied to a run that names none: 120 s.
    /// <para>
    /// Sized against what the verbs actually do. Nearly every call is a status, list or
    /// allow/block mutation that finishes in seconds — Python start-up (~0.8 s) dominates — and
    /// even <c>defenseclaw-gateway start</c>/<c>stop</c>, the slowest of the routine ones, settle
    /// in well under half a minute. Two minutes therefore has a wide margin over any healthy
    /// run while still bounding a wedged one to something an operator will sit through; the
    /// worst case it caps is the tray's gateway toggle staying disabled, which is the reason
    /// this value is not larger. Verbs that legitimately need longer are inferred into the
    /// tiers below by <see cref="ResolveTimeout"/>, or pass a <see cref="CliRunOptions"/>.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Ceiling for verbs that do real work rather than reporting state — <c>doctor</c>,
    /// <c>agent discover</c> and anything of the form <c>&lt;noun&gt; install</c> (downloads and
    /// scans): 10 minutes.
    /// </summary>
    public static readonly TimeSpan ExtendedTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ceiling for <c>defenseclaw setup …</c>, the wizard flows: 30 minutes. These can pull
    /// container images or download scanners. The wizard's Cancel stops a run the operator is
    /// watching, but an unattended hung one still needs a ceiling — and it has to be far beyond
    /// any real download.
    /// </summary>
    public static readonly TimeSpan LongRunningTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long <see cref="Shutdown"/> waits, by default, for killed children to finish
    /// reporting: 3 s. App exit must never hang on a child, so this is a bound, not a promise.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Largest explicit timeout accepted. <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// tops out near 49 days; rejecting up front turns a typo into a synchronous argument error
    /// instead of a failure deep inside a recorded run.
    /// </summary>
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(30);

    /// <summary>How long to wait for the stdout/stderr callbacks to see EOF after the child exits.</summary>
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// After a kill, how long to wait for the process to be reaped and its last output lines to
    /// land. Bounded because a descendant that escaped the tree kill can hold the pipes open.
    /// </summary>
    private static readonly TimeSpan KillSettleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Substring checks only kick in for secrets at least this long — shorter values
    /// produce false positives against ordinary arguments.
    /// </summary>
    private const int MinimumSubstringGuardLength = 8;

    private readonly DefenseClawPaths _paths;
    private readonly string _neutralWorkingDirectory;
    private readonly object _gate = new();
    private readonly LinkedList<CliInvocation> _activity = new();
    private readonly List<SecretValue> _knownSecrets = new();

    /// <summary>Runs between registration and completion. Guarded by <see cref="_gate"/>.</summary>
    private readonly List<InFlightRun> _inFlight = new();

    /// <summary>
    /// The ambient shutdown signal, linked into every run that is not exempt. Deliberately never
    /// disposed: a run that starts in the same instant as <see cref="Dispose"/> would otherwise
    /// race a disposed source and throw from <c>CreateLinkedTokenSource</c>. It owns no timer and
    /// no wait handle, so there is nothing to leak.
    /// </summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Set once by <see cref="Shutdown"/>. Guarded by <see cref="_gate"/>.</summary>
    private bool _shuttingDown;

    /// <param name="paths">Where the DefenseClaw install lives.</param>
    /// <param name="activityCapacity">Ring size of <see cref="Activity"/>.</param>
    /// <param name="neutralWorkingDirectory">
    /// The empty directory the Python <c>defenseclaw</c> CLI runs in (see <see cref="CliWorkingDirectory"/>);
    /// <c>null</c> is <see cref="CliWorkingDirectory.DefaultPath"/>. Injectable so a test never touches the real one.
    /// </param>
    public CliRunner(DefenseClawPaths paths, int activityCapacity = 200, string? neutralWorkingDirectory = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ActivityCapacity = activityCapacity > 0 ? activityCapacity : 200;
        _neutralWorkingDirectory = neutralWorkingDirectory ?? CliWorkingDirectory.DefaultPath;
    }

    /// <summary>
    /// Bounded ring size for <see cref="Activity"/>. Bounds entries, not bytes — each
    /// entry's transcript is capped separately by
    /// <see cref="CliInvocation.MaxRetainedOutputLines"/> and
    /// <see cref="CliInvocation.MaxRetainedOutputBytes"/>, which together put a ceiling on
    /// retained CLI text of roughly <c>ActivityCapacity x 256 KiB</c>.
    /// </summary>
    public int ActivityCapacity { get; }

    /// <summary>
    /// True while a run marked <see cref="CliRunOptions.SurvivesShutdown"/> is in flight — in
    /// practice the in-app upgrade. <see cref="Shutdown"/> deliberately leaves such a run alone,
    /// so the shell asks before quitting over one rather than silently orphaning it.
    /// </summary>
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool HasShutdownSurvivingRun
    {
        get
        {
            lock (_gate)
            {
                return _inFlight.Any(r => r.SurvivesShutdown);
            }
        }
    }

    /// <summary>True once <see cref="Shutdown"/> or <see cref="Dispose"/> has been called.</summary>
    public bool IsShutDown
    {
        get
        {
            lock (_gate)
            {
                return _shuttingDown;
            }
        }
    }

    /// <summary>
    /// Fires per output line, as it arrives, for live wizard consoles. Every captured line
    /// is raised exactly once regardless of retention: the per-invocation cap governs what
    /// is kept for later re-reads, not what is streamed out at capture time. Raised on the
    /// process's stdout/stderr callback threads, and it carries no invocation id — that is
    /// why the live consoles bind to a <see cref="CliInvocation"/> instead.
    /// </summary>
    public event EventHandler<CliOutputLine>? OutputReceived;

    public event EventHandler<CliInvocation>? InvocationStarted;

    /// <summary>
    /// Raised exactly once per recorded invocation, after <see cref="CliInvocation.FinishedAt"/>
    /// is set — including when the run timed out, was cancelled, was refused because the app is
    /// exiting, or failed to start. Raised on a thread-pool thread, and during
    /// <see cref="Shutdown"/> possibly while the UI thread is blocked waiting for it, so a
    /// handler must marshal with <c>BeginInvoke</c> and never <c>Invoke</c>.
    /// </summary>
    public event EventHandler<CliInvocation>? InvocationCompleted;

    /// <summary>
    /// Most recent invocations, newest first, capped at <see cref="ActivityCapacity"/>.
    /// These are the live instances, still being appended to while their processes run —
    /// call <see cref="CliInvocation.Snapshot"/> or <see cref="CliInvocation.CopyNewLines"/>
    /// before reading one from another thread.
    /// </summary>
    public IReadOnlyList<CliInvocation> Activity
    {
        get
        {
            lock (_gate)
            {
                return _activity.ToArray();
            }
        }
    }

    /// <summary>
    /// Registers a secret the runner should refuse to see in argv and should scrub out of
    /// captured output — e.g. the resolved gateway token.
    /// </summary>
    public void RegisterSecret(SecretValue secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            _knownSecrets.Add(secret);
        }
    }

    /// <summary>
    /// Drops the finished invocations. A run still in flight keeps its entry, as the TUI's Clear keeps the running one:
    /// without it the run would carry on with no row to show its output, its elapsed time or a Cancel button.
    /// </summary>
    public void ClearActivity()
    {
        lock (_gate)
        {
            var node = _activity.First;
            while (node is not null)
            {
                var next = node.Next;
                if (!node.Value.IsRunning)
                {
                    _activity.Remove(node);
                }

                node = next;
            }
        }
    }

    /// <summary>
    /// The ceiling a run gets, or <c>null</c> for none.
    /// <para>
    /// An explicit <see cref="CliRunOptions.Timeout"/> always wins. Otherwise the tier is inferred
    /// from the argv, and <b>only</b> for the <c>defenseclaw</c> CLI — the gateway binary is left
    /// at <see cref="DefaultTimeout"/>, because a hung <c>defenseclaw-gateway start</c> is the
    /// tray-toggle wedge this exists to bound and it should not get a longer leash:
    /// <list type="bullet">
    /// <item><description><c>setup …</c> — <see cref="LongRunningTimeout"/> (the wizards).</description></item>
    /// <item><description><c>doctor</c>, <c>agent discover</c>, <c>&lt;noun&gt; install</c> — <see cref="ExtendedTimeout"/>.</description></item>
    /// <item><description>everything else — <see cref="DefaultTimeout"/>.</description></item>
    /// </list>
    /// This is a fallback for call sites that pass no options, not a policy engine: it is a
    /// handful of verbs, kept here so a wizard or panel that has not opted in is still covered.
    /// A call site that knows better should pass a <see cref="CliRunOptions"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An explicit timeout that is neither <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// nor a positive value of at most 30 days.
    /// </exception>
    public static TimeSpan? ResolveTimeout(
        string executablePath,
        IReadOnlyList<string> args,
        CliRunOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentNullException.ThrowIfNull(args);

        if (options?.Timeout is { } requested)
        {
            if (requested == Timeout.InfiniteTimeSpan)
            {
                return null;
            }

            if (requested <= TimeSpan.Zero || requested > MaxTimeout)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    requested,
                    "Timeout must be positive and at most 30 days, or Timeout.InfiniteTimeSpan for none.");
            }

            return requested;
        }

        return InferTimeout(executablePath, args);
    }

    private static TimeSpan InferTimeout(string executablePath, IReadOnlyList<string> args)
    {
        if (args.Count == 0 ||
            !string.Equals(Path.GetFileNameWithoutExtension(executablePath), "defenseclaw", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultTimeout;
        }

        if (IsToken(args[0], "setup"))
        {
            return LongRunningTimeout;
        }

        if (IsToken(args[0], "doctor") ||
            (args.Count > 1 && IsToken(args[0], "agent") && IsToken(args[1], "discover")) ||
            (args.Count > 1 && IsToken(args[1], "install")))
        {
            return ExtendedTimeout;
        }

        return DefaultTimeout;
    }

    private static bool IsToken(string arg, string expected) =>
        string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs <c>defenseclaw</c>.</summary>
    public Task<CliInvocation> RunAsync(
        IReadOnlyList<string> args,
        SecretValue? stdinSecret = null,
        CancellationToken cancellationToken = default,
        CliRunOptions? options = null) =>
        RunNamedAsync("defenseclaw", args, stdinSecret, cancellationToken, options);

    /// <summary>Runs <c>defenseclaw-gateway</c>.</summary>
    public Task<CliInvocation> RunGatewayAsync(
        IReadOnlyList<string> args,
        SecretValue? stdinSecret = null,
        CancellationToken cancellationToken = default,
        CliRunOptions? options = null) =>
        RunNamedAsync("defenseclaw-gateway", args, stdinSecret, cancellationToken, options);

    /// <summary>
    /// Records, in <see cref="Activity"/>, a command this app handed to a console window the operator types into (<c>keys set</c> and
    /// <c>keys fill-missing</c> read a hidden prompt from the console, so they cannot run here). Nothing is started: the entry is born
    /// finished, has no exit code (the app does not observe the run), and carries <paramref name="note"/> as its only output.
    /// Only the argv is stored, and it is checked like any run's (a secret on it is refused), so the entry cannot hold a value.
    /// </summary>
    public CliInvocation RecordHandOff(string executable, IReadOnlyList<string> argv, string note)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(argv);

        GuardArguments(argv, null, Array.Empty<EnvironmentEntry>());

        var now = DateTimeOffset.UtcNow;
        var invocation = new CliInvocation(executable, argv.ToArray(), now);
        invocation.Append(new CliOutputLine(now, CliStream.Notice, note ?? string.Empty));
        invocation.FinishedAt = now;
        Record(invocation);
        InvocationStarted?.Invoke(this, invocation);
        InvocationCompleted?.Invoke(this, invocation);
        return invocation;
    }

    /// <summary>
    /// Resolves <paramref name="executableName"/> through PATH then the install bin dir.
    /// <para>
    /// The resolution happens on a pool thread, inside the returned task: a PATH scan that meets a dead network
    /// entry can take the better part of a minute, and this method is called from UI handlers that must return
    /// at once. So <see cref="CliNotFoundException"/> now surfaces when the task is awaited, not from this call.
    /// </para>
    /// </summary>
    public async Task<CliInvocation> RunNamedAsync(
        string executableName,
        IReadOnlyList<string> args,
        SecretValue? stdinSecret = null,
        CancellationToken cancellationToken = default,
        CliRunOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executableName);

        // Resumes on the caller's context, as it always did: InvocationStarted is raised from the start of
        // RunExecutableAsync on that thread, and a UI subscriber that adds a row there relies on it.
        // FindExecutableAsync scans on the pool and joins a scan already in flight for the same name.
        var path = await _paths.FindExecutableAsync(executableName).ConfigureAwait(true)
            ?? throw new CliNotFoundException(executableName, _paths.CandidatesFor(executableName));

        return await RunExecutableAsync(path, args, stdinSecret, cancellationToken, options).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs an explicit executable path. Uses <see cref="ProcessStartInfo.ArgumentList"/>,
    /// so no shell is involved and no quoting is required or performed.
    /// <para>
    /// <b>How a run can end.</b> Normally the child exits and <see cref="CliInvocation.ExitCode"/>
    /// is set. Otherwise the run is stopped, its whole process tree is killed, and
    /// <see cref="CliInvocation.FailureReason"/> says why while <see cref="CliInvocation.ExitCode"/>
    /// stays <c>null</c>: <c>timed out after N s</c> (see <see cref="ResolveTimeout"/>),
    /// <c>cancelled</c> (the caller's token), <c>cancelled: … exiting</c> (<see cref="Shutdown"/>),
    /// or <c>not started</c> when the app was already exiting. None of them throws
    /// <see cref="OperationCanceledException"/> — the outcome is data on the returned invocation.
    /// </para>
    /// </summary>
    /// <param name="options">
    /// Timeout and app-exit behaviour for this call; <c>null</c> is <see cref="CliRunOptions.Default"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">An invalid explicit timeout in <paramref name="options"/>.</exception>
    public async Task<CliInvocation> RunExecutableAsync(
        string executablePath,
        IReadOnlyList<string> args,
        SecretValue? stdinSecret = null,
        CancellationToken cancellationToken = default,
        CliRunOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentNullException.ThrowIfNull(args);

        // Resolved before anything is recorded so a bad option is a plain argument error and
        // leaves no half-recorded invocation behind.
        var environment = ResolveEnvironment(options);
        GuardArguments(args, stdinSecret, environment);
        GuardExpansion(executablePath, args, options);

        var timeout = ResolveTimeout(executablePath, args, options);
        var survivesShutdown = options?.SurvivesShutdown ?? false;

        var invocation = new CliInvocation(
            executablePath,
            args.ToArray(),
            DateTimeOffset.UtcNow,
            retainFullOutput: options?.RetainFullOutput ?? false)
        {
            UsedStdinSecret = stdinSecret is { IsEmpty: false },
            SurvivesShutdown = survivesShutdown,
            EnvironmentNames = environment.Select(e => e.Name).ToArray(),
        };

        Record(invocation);

        // Registered before the started event so that Shutdown, which reads this list, cannot
        // slip between "recorded" and "tracked" and miss a run. It is also what Cancel looks a
        // run up in, so the invocation travels with it.
        var run = TryRegister(survivesShutdown, invocation);

        try
        {
            InvocationStarted?.Invoke(this, invocation);

            if (run is null)
            {
                invocation.FailureReason =
                    "not started — DefenseClaw for Windows is exiting, so no new commands are launched";
            }
            else
            {
                await SuperviseAsync(invocation, run, executablePath, args, stdinSecret, environment, cancellationToken, timeout, options?.OnProcessStarted)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever happened above — a normal exit, a kill, a start failure, a throwing
            // event handler — the entry is finished and the completion event is raised once.
            // An entry stuck "running" forever is the failure this whole block exists to prevent.
            try
            {
                invocation.FinishedAt = DateTimeOffset.UtcNow;
                InvocationCompleted?.Invoke(this, invocation);
            }
            finally
            {
                if (run is not null)
                {
                    lock (_gate)
                    {
                        _ = _inFlight.Remove(run);
                    }

                    // Signalled only after the completion event, so Shutdown's bounded wait
                    // covers the whole tail of the run and not just the process kill.
                    _ = run.Completed.TrySetResult();
                }
            }
        }

        return invocation;
    }

    /// <summary>
    /// Ends the runner's life for app exit: refuses new runs, and cancels and kills the process
    /// tree of every in-flight run that is not <see cref="CliRunOptions.SurvivesShutdown"/>.
    /// <para>
    /// <b>Bounded.</b> Blocks the calling thread for at most <paramref name="wait"/> (default
    /// <see cref="DefaultShutdownWait"/>) while the killed runs finish reporting — enough for
    /// their invocations to be marked finished and <see cref="InvocationCompleted"/> raised, not
    /// enough for a stuck child to hold the app open. The kill itself does not depend on that
    /// wait: this method terminates each tracked process directly before waiting, so the children
    /// are gone even if a pool thread is slow to run the continuation.
    /// </para>
    /// <para>
    /// <b>Survivors.</b> A run started with <see cref="CliRunOptions.SurvivesShutdown"/> — the
    /// upgrade installer and resolver — is not touched: it keeps running, it is not cancelled by
    /// this call, and it may still be started after it. Only its caller's own token can end it.
    /// After the app process exits it simply carries on unsupervised; a child is not part of a
    /// Windows job that dies with its parent.
    /// </para>
    /// <para>
    /// Idempotent. Safe to call from <c>OnExit</c> and again from a <c>Dispose</c> that follows;
    /// the second call finds nothing left to kill and returns at once.
    /// </para>
    /// </summary>
    /// <param name="wait">Upper bound on the wait; <c>null</c> uses <see cref="DefaultShutdownWait"/>.</param>
    /// <returns>
    /// <c>true</c> when every non-exempt run had finished by the time this returned;
    /// <c>false</c> when the wait expired first (the kills were still issued).
    /// </returns>
    public bool Shutdown(TimeSpan? wait = null)
    {
        var budget = wait ?? DefaultShutdownWait;
        if (budget < TimeSpan.Zero)
        {
            budget = TimeSpan.Zero;
        }

        InFlightRun[] victims;
        lock (_gate)
        {
            _shuttingDown = true;
            victims = _inFlight.Where(r => !r.SurvivesShutdown).ToArray();
        }

        // Cancelling first means every victim, when it observes its own cancellation, can see
        // that shutdown is the reason and report it as such.
        _shutdown.Cancel();

        foreach (var victim in victims)
        {
            if (victim.CurrentProcess is { } process)
            {
                TryKill(process, victim.Job);
            }
        }

        return victims.Length == 0 ||
               Task.WhenAll(victims.Select(v => v.Completed.Task)).Wait(budget);
    }

    /// <summary>
    /// Same as <see cref="Shutdown"/> with the default bound. Lets <c>AppServices.Dispose</c>
    /// own the runner like every other service it tears down.
    /// </summary>
    public void Dispose() => _ = Shutdown();

    /// <summary>
    /// Stops one run in flight on the operator's say-so — the Activity panel's Cancel button. The
    /// whole process tree is killed (<see cref="Process.Kill(bool)"/> with
    /// <c>entireProcessTree: true</c>, as for a timeout or shutdown) and the invocation finishes
    /// with a <see cref="CliInvocation.FailureReason"/> starting <c>cancelled</c> and no exit code,
    /// exactly like a caller-token cancellation. The run's own <c>await</c> returns normally; this
    /// method does not wait for that.
    /// <para>
    /// <b>Never throws for an ordinary "no".</b> Returns <c>false</c> with a human sentence in
    /// <paramref name="reason"/> when there is nothing to cancel:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the invocation has already finished (a no-op — nothing is touched, and
    /// its recorded outcome stays exactly as it was);</description></item>
    /// <item><description>the run was started with <see cref="CliRunOptions.SurvivesShutdown"/> —
    /// the upgrade installer, which is refused because killing it part-way can leave the machine
    /// with a half-replaced DefenseClaw. Only the token its own caller holds ends such a run;</description></item>
    /// <item><description>the invocation is not one this runner is running (for example a
    /// <see cref="CliInvocation.Snapshot"/> copy, or an invocation from another runner).</description></item>
    /// </list>
    /// Cancelling a run that is not the given one never happens: runs are matched by reference,
    /// so a sibling started at the same moment is left alone.
    /// </summary>
    /// <param name="invocation">The live instance from <see cref="Activity"/> or <see cref="InvocationStarted"/>.</param>
    /// <param name="reason">
    /// Why nothing was cancelled, or — on success — a short confirmation. Never <c>null</c>.
    /// </param>
    /// <returns><c>true</c> when the cancel was accepted and the kill has been issued.</returns>
    public bool Cancel(CliInvocation invocation, out string reason)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        // A run that has finished — or is finishing: FinishedAt is set before the completion event —
        // has nothing left to cancel, and must not be touched or re-labelled.
        if (!invocation.IsRunning)
        {
            reason = "Already finished — there is nothing to cancel.";
            return false;
        }

        InFlightRun? run;
        lock (_gate)
        {
            run = _inFlight.FirstOrDefault(r => ReferenceEquals(r.Invocation, invocation));
        }

        if (run is null)
        {
            reason = "This invocation is not running under this runner, so it cannot be cancelled from here.";
            return false;
        }

        if (run.SurvivesShutdown)
        {
            reason =
                "This run survives app shutdown (it is the DefenseClaw upgrade installer). Killing it part-way " +
                "can leave DefenseClaw half-installed, so it cannot be cancelled from here — let it finish.";
            return false;
        }

        // Order matters: the request is signalled first (it also flags the run as "cancelled by an
        // operator" for the recorded reason), so the supervising task sees its own cancellation
        // before it can see the child's exit and misfile a killed process as "exit -1". The direct
        // kill after it is the same belt-and-braces Shutdown uses: the child is gone even if the
        // continuation is slow to be scheduled. It runs on the pool, not here - enumerating and
        // killing a process tree takes real time and this is called from a button click.
        invocation.CancelRequested = true;
        run.RequestCancel();
        if (run.CurrentProcess is { } process)
        {
            var job = run.Job;
            _ = Task.Run(() => TryKill(process, job));
        }

        reason = "Cancel requested — the process tree is being killed.";
        return true;
    }

    /// <summary>
    /// <see cref="Cancel(CliInvocation, out string)"/> by <see cref="CliInvocation.Id"/>, for a
    /// caller that kept the id rather than the instance. An id that is not in
    /// <see cref="Activity"/> (never existed, or aged out of the ring) is a <c>false</c> with a reason.
    /// </summary>
    public bool Cancel(string invocationId, out string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(invocationId);

        CliInvocation? match;
        lock (_gate)
        {
            match = _activity.FirstOrDefault(i => string.Equals(i.Id, invocationId, StringComparison.Ordinal));
        }

        if (match is null)
        {
            reason = "No invocation with that id is in the activity list (it never existed, or has aged out).";
            return false;
        }

        return Cancel(match, out reason);
    }

    /// <summary>
    /// Adds a run to the in-flight set, or returns <c>null</c> when shutdown has begun and this
    /// run is not exempt — in which case nothing may be started. Exempt runs are always tracked
    /// (so that a later diagnostic can see them, and so <see cref="Cancel(CliInvocation, out string)"/>
    /// can say why it refuses them) but are never selected as shutdown victims.
    /// </summary>
    private InFlightRun? TryRegister(bool survivesShutdown, CliInvocation invocation)
    {
        lock (_gate)
        {
            if (_shuttingDown && !survivesShutdown)
            {
                return null;
            }

            var run = new InFlightRun(survivesShutdown, invocation);
            _inFlight.Add(run);
            return run;
        }
    }

    /// <summary>
    /// Starts the child and holds it to <paramref name="timeout"/>, <paramref name="callerToken"/>
    /// and — unless exempt — the ambient shutdown signal. Fills in the invocation's outcome; the
    /// caller finishes it.
    /// </summary>
    private async Task SuperviseAsync(
        CliInvocation invocation,
        InFlightRun run,
        string executablePath,
        IReadOnlyList<string> args,
        SecretValue? stdinSecret,
        IReadOnlyList<EnvironmentEntry> environment,
        CancellationToken callerToken,
        TimeSpan? timeout,
        Action? onProcessStarted)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,

            // DefenseClaw's CLI (Python) and gateway (Go) both write UTF-8 when piped — measured on 0.8.10:
            // `guardrail status` emits e2 80 a2 for its bullet. Left unset, .NET decodes with the legacy
            // console code page and every non-ASCII glyph (•, —, ✓) turns into mojibake in Activity and
            // in anything that parses the text. No BOM on stdin: a BOM would prefix the first line read.
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
            WorkingDirectory = WorkingDirectoryFor(executablePath),
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Touched only when there is something to overlay: reading Environment copies the whole
        // parent environment into the start info, which a plain run has no reason to do. This sets
        // the child's block and nothing else — Environment.SetEnvironmentVariable is never called, so
        // the value cannot reach a later run, another panel's child, or this process's own environment.
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Name] = entry.Value.Reveal();
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // The child and everything it starts go in a kill-on-close job, so they die with this process even when it is
        // killed outright (see WindowsJob). The upgrade installer is exempt on purpose: it must outlive the app, and a job
        // would end it. No job (not created, or not assignable) leaves the tree kill below as the only guard.
        using var job = run.SurvivesShutdown ? null : WindowsJob.TryCreate();

        // Both streams complete asynchronously; wait for them so no trailing output is lost.
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The per-call secrets — the stdin one and every environment value — are scrubbed from this
        // run's output alongside the registered ones. They are not in _knownSecrets (they belong to
        // this one call), so without passing them down a child that echoes what it was given — a CLI
        // reporting "invalid key: <key>" — would put the very value the wizard promised to keep out
        // of the Activity panel straight into it.
        var callSecrets = CallSecrets(stdinSecret, environment);
        process.OutputDataReceived += (_, e) => Capture(invocation, CliStream.StandardOutput, e.Data, stdoutDone, callSecrets);
        process.ErrorDataReceived += (_, e) => Capture(invocation, CliStream.StandardError, e.Data, stderrDone, callSecrets);

        // One token for everything that may end this run early. The timeout is armed here, before
        // the launch, so the clock covers the whole life of the child. Shutdown is linked in only
        // for runs that are allowed to be ended by it — an exempt run's stop token is its
        // caller's and its timeout, nothing else. The run's own cancel source is linked for every
        // run, but Cancel refuses to fire it for an exempt one.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            run.SurvivesShutdown ? CancellationToken.None : _shutdown.Token,
            run.CancelToken);

        if (timeout is { } limit)
        {
            stop.CancelAfter(limit);
        }

        try
        {
            // Never launch a child for a run that is already over — a token cancelled before the
            // call, or a shutdown that began between registration and here.
            stop.Token.ThrowIfCancellationRequested();
            process.Start();
            if (job is not null && job.TryAssign(process))
            {
                run.AttachJob(job);
            }

            // The child has its own copy of the block now. Dropping ours keeps the plaintext from
            // sitting in the start info for the rest of a run that can last half an hour.
            foreach (var entry in environment)
            {
                _ = startInfo.Environment.Remove(entry.Name);
            }

            NotifyStarted(onProcessStarted);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            invocation.FailureReason = ex.Message;
            return;
        }
        catch (OperationCanceledException)
        {
            invocation.FailureReason = DescribeStop(ClassifyStop(callerToken, run), timeout, processStarted: false);
            return;
        }

        run.Attach(process);
        var settled = false;

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Inside the supervised section on purpose: a child that never reads stdin leaves this
            // write blocked once the pipe is full, and a cancellation or timeout that lands then
            // surfaces as an OperationCanceledException from here. It takes the same catch below
            // as one from WaitForExitAsync — kill the tree, record why — rather than escaping.
            await WriteStdinAsync(process, stdinSecret, stop.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(StreamDrainTimeout).ConfigureAwait(false);
            invocation.ExitCode = process.ExitCode;
            settled = true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            invocation.FailureReason = DescribeStop(ClassifyStop(callerToken, run), timeout, processStarted: true);
            await KillAndSettleAsync(process, run.Job, stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);
            settled = true;
        }
        catch (TimeoutException)
        {
            // Streams did not close cleanly; the exit code is still meaningful.
            invocation.ExitCode = process.HasExited ? process.ExitCode : null;
            settled = true;
        }
        finally
        {
            // Detached before the Process is disposed at the end of this method, so Shutdown
            // never reaches for a handle that is about to be closed.
            run.Detach();

            if (!settled)
            {
                // An exception nobody here anticipates is on its way out. Whatever it is, it
                // must not leave the child running with no one left holding a reference to it.
                TryKill(process, run.Job);
            }

            run.DetachJob();
        }
    }

    /// <summary>
    /// Tells the caller the child is running (<see cref="CliRunOptions.OnProcessStarted"/>). Nothing it does can fail the run: the child exists
    /// by now, and a throwing callback must not leave it running with no one supervising it.
    /// </summary>
    private static void NotifyStarted(Action? onProcessStarted)
    {
        if (onProcessStarted is null)
        {
            return;
        }

#pragma warning disable CA1031 // A caller's callback cannot be allowed to orphan a started process.
        try
        {
            onProcessStarted();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"OnProcessStarted callback failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Writes the secret (if any) and closes stdin. A child that exited before reading it is not
    /// an error. Cancellation is not swallowed — it propagates to <see cref="SuperviseAsync"/>,
    /// which owns turning it into a kill and a recorded reason.
    /// </summary>
    private static async Task WriteStdinAsync(Process process, SecretValue? stdinSecret, CancellationToken cancellationToken)
    {
        try
        {
            if (stdinSecret is { IsEmpty: false })
            {
                await process.StandardInput.WriteAsync(stdinSecret.Reveal().AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The child may have exited before reading stdin; not fatal.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Kills the tree, then gives the process a moment to be reaped and its last lines of output
    /// to arrive, so the transcript shows what the child said before it was stopped. Bounded by
    /// <see cref="KillSettleTimeout"/> in total: a descendant that survived the kill and still
    /// holds the pipes must not turn a stop into another hang.
    /// </summary>
    private static async Task KillAndSettleAsync(Process process, WindowsJob? job, Task stdoutDone, Task stderrDone)
    {
        TryKill(process, job);

        using var settle = new CancellationTokenSource(KillSettleTimeout);
        try
        {
            await process.WaitForExitAsync(settle.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutDone, stderrDone).WaitAsync(settle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Did not settle in time; the reason is already recorded, and that is what matters.
        }
        catch (InvalidOperationException)
        {
            // The process object is no longer usable, i.e. it is gone.
        }
    }

    /// <summary>Why a run was stopped, decided from which of its three sources actually fired.</summary>
    private enum StopKind
    {
        Cancelled,
        Shutdown,
        TimedOut,
    }

    /// <summary>
    /// The caller's token and an operator's <see cref="Cancel(CliInvocation, out string)"/> outrank
    /// shutdown, which outranks the timeout: someone who explicitly cancelled should be told they
    /// did, and an app that is exiting is a better explanation than a clock that happened to
    /// expire in the same moment.
    /// </summary>
    private StopKind ClassifyStop(CancellationToken callerToken, InFlightRun run)
    {
        if (callerToken.IsCancellationRequested || run.CancelRequested)
        {
            return StopKind.Cancelled;
        }

        return !run.SurvivesShutdown && _shutdown.IsCancellationRequested
            ? StopKind.Shutdown
            : StopKind.TimedOut;
    }

    private static string DescribeStop(StopKind kind, TimeSpan? timeout, bool processStarted)
    {
        // "process tree killed" is only said when there was a process to kill.
        var tail = processStarted ? " — process tree killed" : " — nothing was started";

        // "120", "2", "0.5": whole seconds print bare, so the reason reads "timed out after 120 s".
        var seconds = (timeout ?? TimeSpan.Zero).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        return kind switch
        {
            StopKind.TimedOut => $"timed out after {seconds} s{tail}",
            StopKind.Shutdown => $"cancelled: DefenseClaw for Windows is exiting{tail}",
            _ => $"cancelled{tail}",
        };
    }

    /// <summary>
    /// Rejects any argument that carries a secret. Checks the per-call
    /// <paramref name="stdinSecret"/> plus everything passed to
    /// <see cref="RegisterSecret"/>.
    /// </summary>
    private void GuardArguments(IReadOnlyList<string> args, SecretValue? stdinSecret, IReadOnlyList<EnvironmentEntry> environment)
    {
        List<SecretValue> secrets;
        lock (_gate)
        {
            secrets = new List<SecretValue>(_knownSecrets);
        }

        if (stdinSecret is not null)
        {
            secrets.Add(stdinSecret);
        }

        // A value handed over in the environment must not also be on the command line.
        secrets.AddRange(environment.Select(e => e.Value));

        if (secrets.Count == 0)
        {
            return;
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg))
            {
                continue;
            }

            foreach (var secret in secrets)
            {
                if (secret.IsEmpty)
                {
                    continue;
                }

                if (secret.Matches(arg) ||
                    (secret.Length >= MinimumSubstringGuardLength && secret.AppearsIn(arg)))
                {
                    // Note: the message names only the index — never the value.
                    throw new SecretInArgumentException(i);
                }
            }
        }
    }

    /// <summary>
    /// Where a child runs. The Python <c>defenseclaw</c> CLI gets <see cref="CliWorkingDirectory"/>: an empty
    /// directory of the app's own, because Click on Windows glob-expands every argument against the working
    /// directory (<c>config.y?ml</c> becoming <c>config.yaml</c> is what running in <c>~/.defenseclaw</c> did)
    /// and a relative path an operator types (<c>plugin install ./x</c>, <c>mcp set --command ./x</c>) would
    /// otherwise resolve inside the DefenseClaw data directory. Nothing in the CLI needs that directory as its
    /// cwd: it addresses its files absolutely (<c>~/.defenseclaw/...</c>), and the read-only commands the app
    /// runs print the same thing from either. Every other child - the Go gateway, the installers - keeps the
    /// data directory as before: they do not expand arguments, so there is nothing to gain and nothing verified
    /// about running them elsewhere.
    /// </summary>
    private string WorkingDirectoryFor(string executablePath)
    {
        if (ArgvHazards.AppliesTo(executablePath) && CliWorkingDirectory.TryEnsure(_neutralWorkingDirectory))
        {
            return _neutralWorkingDirectory;
        }

        return _paths.DataDirectoryExists ? _paths.DataDirectory : Environment.CurrentDirectory;
    }

    /// <summary>
    /// Refuses a run whose targets the CLI would rewrite, when the caller asked for that
    /// (<see cref="CliRunOptions.RefuseExpandingTargets"/>).
    /// </summary>
    private void GuardExpansion(string executablePath, IReadOnlyList<string> args, CliRunOptions? options)
    {
        if (options is not { RefuseExpandingTargets: true } || !ArgvHazards.AppliesTo(executablePath))
        {
            return;
        }

        var changes = ArgvHazards.FindChangedTargets(args, WorkingDirectoryFor(executablePath));
        if (changes.Count > 0)
        {
            throw new ArgumentExpansionException(changes);
        }
    }

    /// <summary>One validated, non-empty overlay entry: a variable name and the value it is set to.</summary>
    private readonly record struct EnvironmentEntry(string Name, SecretValue Value);

    /// <summary>
    /// Checks <see cref="CliRunOptions.EnvironmentOverlay"/> and returns the entries that will be set,
    /// ordered by name. Empty values are dropped (see the option's remarks). Messages name the
    /// variable, never its value.
    /// </summary>
    /// <exception cref="ArgumentException">An invalid or duplicated name, or a null value.</exception>
    private static EnvironmentEntry[] ResolveEnvironment(CliRunOptions? options)
    {
        var overlay = options?.EnvironmentOverlay;
        if (overlay is null || overlay.Count == 0)
        {
            return Array.Empty<EnvironmentEntry>();
        }

        var entries = new List<EnvironmentEntry>(overlay.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in overlay)
        {
            if (!CliRunOptions.IsValidEnvironmentName(name))
            {
                throw new ArgumentException(
                    "An environment variable name in the overlay is not valid: names are letters, digits and underscores, " +
                    "starting with a letter or underscore.",
                    nameof(options));
            }

            if (value is null)
            {
                throw new ArgumentException($"Environment variable {name} has no value object.", nameof(options));
            }

            if (!seen.Add(name))
            {
                throw new ArgumentException($"Environment variable {name} appears twice in the overlay.", nameof(options));
            }

            if (!value.IsEmpty)
            {
                entries.Add(new EnvironmentEntry(name, value));
            }
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return entries.ToArray();
    }

    private static SecretValue[] CallSecrets(SecretValue? stdinSecret, IReadOnlyList<EnvironmentEntry> environment)
    {
        var secrets = new List<SecretValue>(environment.Count + 1);
        if (stdinSecret is { IsEmpty: false })
        {
            secrets.Add(stdinSecret);
        }

        secrets.AddRange(environment.Select(e => e.Value));
        return secrets.ToArray();
    }

    private void Capture(
        CliInvocation invocation,
        CliStream stream,
        string? data,
        TaskCompletionSource completion,
        IReadOnlyList<SecretValue> callSecrets)
    {
        if (data is null)
        {
            completion.TrySetResult();
            return;
        }

        var line = new CliOutputLine(DateTimeOffset.UtcNow, stream, Scrub(data, callSecrets));
        invocation.Append(line);
        OutputReceived?.Invoke(this, line);
    }

    /// <summary>
    /// Removes every secret that leaked into subprocess output: each one passed to
    /// <see cref="RegisterSecret"/>, plus <paramref name="callSecrets"/> — the per-call
    /// <c>stdinSecret</c> and environment values of the run this line belongs to.
    /// <para>
    /// Occurrences are replaced wherever they appear in a line, with no minimum length (unlike the
    /// argv guard, which skips substring checks below <see cref="MinimumSubstringGuardLength"/>):
    /// a false positive here only garbles a diagnostic, while a miss discloses a secret. Matching
    /// is per line, so a secret that itself contains a newline is not recognised once the child
    /// splits it across lines; the secrets this app pipes in (tokens, API keys) are single-line.
    /// </para>
    /// </summary>
    private string Scrub(string text, IReadOnlyList<SecretValue> callSecrets)
    {
        List<SecretValue>? secrets = null;
        lock (_gate)
        {
            if (_knownSecrets.Count > 0)
            {
                secrets = new List<SecretValue>(_knownSecrets);
            }
        }

        if (secrets is not null)
        {
            foreach (var secret in secrets)
            {
                text = secret.Scrub(text);
            }
        }

        foreach (var secret in callSecrets)
        {
            text = secret.Scrub(text);
        }

        return text;
    }

    private void Record(CliInvocation invocation)
    {
        lock (_gate)
        {
            _activity.AddFirst(invocation);
            while (_activity.Count > ActivityCapacity)
            {
                _activity.RemoveLast();
            }
        }
    }

    /// <summary>
    /// Best-effort kill of the process and every descendant. Never throws, and deliberately does
    /// not pre-check <see cref="Process.HasExited"/>: a parent that already exited can still have
    /// descendants worth killing, and the framework skips whatever is not there.
    /// <para>
    /// Every failure is swallowed because there is nobody to tell — this runs from a timeout, a
    /// cancellation and app exit, and all three are already reporting a stop. The realistic ones
    /// are the process having just exited (<see cref="InvalidOperationException"/>), a descendant
    /// that ended between the snapshot and the kill (<see cref="System.ComponentModel.Win32Exception"/>,
    /// <see cref="AggregateException"/>), and a <see cref="Process"/> disposed by its own run a
    /// moment before <see cref="Shutdown"/> got to it.
    /// </para>
    /// </summary>
    private static void TryKill(Process process, WindowsJob? job = null)
    {
        // The job first: one call ends everything in it, including a descendant that appeared after the tree was walked.
        // The tree walk after it covers what the job cannot - a grandchild started before the child was assigned to the
        // job - and is the whole kill when there is no job.
        _ = job?.Terminate();

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // See the method comment: best effort, and nobody to report to.
        }
    }

    /// <summary>
    /// One run's handle for <see cref="Shutdown"/>: whether it is exempt, which process to kill
    /// once there is one, and a signal for when the run has fully finished reporting.
    /// </summary>
    private sealed class InFlightRun
    {
        private Process? _process;
        private WindowsJob? _job;
        private int _cancelRequested;

        /// <summary>
        /// Fired only by <see cref="RequestCancel"/>. Deliberately never disposed, for the same
        /// reason <c>_shutdown</c> is not: a <see cref="Cancel(CliInvocation, out string)"/> that
        /// lands in the instant the run finishes would otherwise race a disposed source. It owns no
        /// timer and no wait handle, so there is nothing to leak; the linked source that does
        /// register on it is disposed with the run and unregisters.
        /// </summary>
        private readonly CancellationTokenSource _cancel = new();

        public InFlightRun(bool survivesShutdown, CliInvocation invocation)
        {
            SurvivesShutdown = survivesShutdown;
            Invocation = invocation;
        }

        public bool SurvivesShutdown { get; }

        /// <summary>The live invocation this run is producing; how <c>Cancel</c> finds it by reference.</summary>
        public CliInvocation Invocation { get; }

        public CancellationToken CancelToken => _cancel.Token;

        /// <summary>True once an operator cancel was accepted; read to word the recorded reason.</summary>
        public bool CancelRequested => Volatile.Read(ref _cancelRequested) != 0;

        /// <summary>
        /// Flags the run as cancelled by an operator <b>before</b> firing the token, so whoever
        /// observes the cancellation always finds the flag already set.
        /// </summary>
        public void RequestCancel()
        {
            Volatile.Write(ref _cancelRequested, 1);
            _cancel.Cancel();
        }

        /// <summary>Completed by the run's own <c>finally</c>, after the completion event.</summary>
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Null before the child is launched and again once the run lets go of it.</summary>
        public Process? CurrentProcess => Volatile.Read(ref _process);

        /// <summary>The kill-on-close job the child runs in, or null (exempt run, or none could be created).</summary>
        public WindowsJob? Job => Volatile.Read(ref _job);

        public void Attach(Process process) => Volatile.Write(ref _process, process);

        public void AttachJob(WindowsJob job) => Volatile.Write(ref _job, job);

        public void Detach() => Volatile.Write(ref _process, null);

        /// <summary>Called before the run disposes the job, so a late Cancel or Shutdown does not reach for a closed handle.</summary>
        public void DetachJob() => Volatile.Write(ref _job, null);
    }
}
