using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Services.Updates.SelfUpdate;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>Starts a new instance of this app. A seam: tests record the request and start nothing.</summary>
internal interface IAppProcessLauncher
{
    /// <exception cref="Win32Exception">The process could not be started.</exception>
    void Start(string executable, IReadOnlyList<string> arguments);
}

/// <summary>
/// The real launcher. <see cref="Process.Start(ProcessStartInfo)"/> cannot ask for <c>CREATE_BREAKAWAY_FROM_JOB</c>, so the new instance
/// goes through the same <see cref="ConsoleInstallerLauncher"/> the install.ps1 runner uses (no new console): if this app ever sits in a
/// kill-on-close job, the successor must not die with it as this process exits. A job that forbids breakaway is retried without the flag.
/// </summary>
internal sealed class BreakawayAppLauncher : IAppProcessLauncher
{
    public void Start(string executable, IReadOnlyList<string> arguments)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name)
            {
                environment[name] = entry.Value as string;
            }
        }

        using var process = new ConsoleInstallerLauncher().Start(new InstallerLaunchRequest(
            executable,
            arguments,
            Path.GetDirectoryName(executable) ?? string.Empty,
            environment,
            NewConsole: false,
            BreakAwayFromJob: true));
    }
}

/// <summary>
/// "Restart this app": starts a new instance of this exe and lets the shell exit (the Updates window's post-upgrade warning and the tray
/// menu both call it).
/// <para>
/// The new instance carries <c>--after-restart &lt;pid&gt;</c> and, before it takes the single-instance guard, waits (bounded) for that pid to
/// exit (<see cref="AcquireAfterRestart"/>). Without the wait it would lose the guard race to the dying instance, hand off to it and exit,
/// leaving no app. When the old process outlives the bound the new one carries on to the guard and takes the ordinary second-launch path.
/// </para>
/// </summary>
internal sealed class AppRestart
{
    /// <summary>The argument that tells a launch which process it replaces.</summary>
    public const string AfterRestartFlag = "--after-restart";

    /// <summary>The argument an autostart launch carries: tray only, no window.</summary>
    public const string MinimizedFlag = "--minimized";

    /// <summary>How long a restarted instance waits for its predecessor to exit before it falls back to the second-launch path.</summary>
    public static readonly TimeSpan DefaultWaitBound = TimeSpan.FromSeconds(20);

    private readonly Func<IReadOnlyList<CliInvocation>> _activity;
    private readonly IAppProcessLauncher _launcher;
    private readonly Func<string?> _processPath;
    private readonly Func<int> _processId;

    public AppRestart(
        Func<IReadOnlyList<CliInvocation>> activity,
        IAppProcessLauncher? launcher = null,
        Func<string?>? processPath = null,
        Func<int>? processId = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _launcher = launcher ?? new BreakawayAppLauncher();
        _processPath = processPath ?? (() => Environment.ProcessPath);
        _processId = processId ?? (() => Environment.ProcessId);
    }

    /// <summary>True when this launch was started with <c>--minimized</c>. Set by the app at start.</summary>
    public bool StartedMinimized { get; set; }

    /// <summary>True while a dashboard window is on screen. Set by the app; null means none.</summary>
    public Func<bool>? WindowShown { get; set; }

    /// <summary>
    /// Raised by <see cref="Request"/> when a restart may go ahead. The shell handles it: it runs its usual exit checks and calls
    /// <see cref="TryLaunch"/> just before it shuts down.
    /// </summary>
    public event EventHandler? RestartRequested;

    /// <summary>
    /// Why a restart is not offered right now, or null. A state-changing run (including the install.ps1 upgrade, which is an Activity entry
    /// of its own) would be cut off, or left running with nobody watching.
    /// </summary>
    public string? BlockedReason
    {
        get
        {
            foreach (var invocation in _activity())
            {
                if (invocation.IsRunning && InstallationGate.TierOf(invocation.Executable, invocation.Argv) != CommandTier.ReadOnly)
                {
                    return "A command that changes DefenseClaw is still running (see Activity). Restart the app when it finishes.";
                }
            }

            return null;
        }
    }

    /// <summary>Asks the shell to restart. Returns why not, or null when the request was raised.</summary>
    public string? Request()
    {
        if (BlockedReason is { } reason)
        {
            return reason;
        }

        var handler = RestartRequested;
        if (handler is null)
        {
            return "Restarting is not available here.";
        }

        handler(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// Set by a self-update once the new files are in place: the next launch starts the new exe with the hand-off flag, waits for it to say
    /// it started, and restores the old files (and stays up) when it does not. Cleared when the launch is over.
    /// </summary>
    internal UpdateHandoff? PendingHandoff { get; set; }

    /// <summary>
    /// Starts the successor. Called by the shell once it has decided to exit; false (with the reason) when nothing was started, in which
    /// case the shell stays up. With a <see cref="PendingHandoff"/> this also waits (bounded) for the successor to report it started.
    /// </summary>
    public bool TryLaunch(out string? error)
    {
        var plan = Prepare(out error);
        return plan is not null && Launch(plan, out error);
    }

    /// <summary>
    /// <see cref="TryLaunch"/> without holding the UI thread through the hand-off wait: what reads the window runs on the caller, the start and
    /// the wait on the pool.
    /// </summary>
    public async Task<(bool Launched, string? Error)> TryLaunchAsync()
    {
        var plan = Prepare(out var error);
        if (plan is null)
        {
            return (false, error);
        }

        return await Task.Run(() =>
        {
            var launched = Launch(plan, out var launchError);
            return (launched, launchError);
        }).ConfigureAwait(true);
    }

    private sealed record LaunchPlan(string Path, bool Minimized, int ProcessId, UpdateHandoff? Handoff);

    private LaunchPlan? Prepare(out string? error)
    {
        error = BlockedReason;
        if (error is not null)
        {
            return null;
        }

        var handoff = PendingHandoff;
        var path = handoff?.ExecutablePath ?? _processPath();
        if (string.IsNullOrEmpty(path))
        {
            error = "This app cannot tell where its own program file is, so it cannot start a new copy.";
            return null;
        }

        return new LaunchPlan(path, StartedMinimized && !(WindowShown?.Invoke() ?? false), _processId(), handoff);
    }

    private bool Launch(LaunchPlan plan, out string? error)
    {
        error = null;
        var arguments = BuildArguments(plan.Minimized, plan.ProcessId).ToList();
        if (plan.Handoff is { } handoff)
        {
            arguments.Add(SelfUpdateHandshake.Flag);
            arguments.Add(handoff.Signal.Name);
        }

        try
        {
            _launcher.Start(plan.Path, arguments);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            error = "A new copy of the app could not be started: " + ex.Message;
            return plan.Handoff is not null ? FailHandoff(plan.Handoff, ref error) : false;
        }

        if (plan.Handoff is { } waiting)
        {
            if (!waiting.Signal.Wait(waiting.Timeout))
            {
                error = $"The new version did not report that it started within {(int)waiting.Timeout.TotalSeconds} seconds.";
                return FailHandoff(waiting, ref error);
            }

            PendingHandoff = null;
            waiting.Signal.Dispose();
        }

        return true;
    }

    private bool FailHandoff(UpdateHandoff handoff, ref string? error)
    {
        var problems = handoff.Rollback();
        PendingHandoff = null;
        handoff.Signal.Dispose();
        error += problems.Count == 0
            ? " The previous version was restored and keeps running."
            : " Putting the previous version back failed for: " + string.Join("; ", problems);
        return false;
    }

    /// <summary>The successor's arguments: <c>--minimized</c> only when it should stay in the tray, then <c>--after-restart &lt;pid&gt;</c>.</summary>
    public static IReadOnlyList<string> BuildArguments(bool minimized, int currentProcessId)
    {
        var arguments = new List<string>();
        if (minimized)
        {
            arguments.Add(MinimizedFlag);
        }

        arguments.Add(AfterRestartFlag);
        arguments.Add(currentProcessId.ToString(CultureInfo.InvariantCulture));
        return arguments;
    }

    /// <summary>
    /// The pid after <c>--after-restart</c>, or null when the flag is absent, has no value, or the value is not a plain positive integer
    /// (no sign, no spaces, no hex). Anything else is ignored: the launch is then an ordinary one.
    /// </summary>
    public static int? ParseAfterRestart(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], AfterRestartFlag, StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Waits for the replaced instance (when <paramref name="args"/> name one), then takes the guard: the order is the point. Returns what
    /// <paramref name="acquire"/> returned; a predecessor that is still alive after <paramref name="bound"/> is not an error here, the
    /// guard then reports itself as not first and the second-launch handshake says what it has to.
    /// </summary>
    public static T AcquireAfterRestart<T>(IReadOnlyList<string> args, Func<int, TimeSpan, bool> waitForExit, Func<T> acquire, TimeSpan? bound = null)
    {
        ArgumentNullException.ThrowIfNull(waitForExit);
        ArgumentNullException.ThrowIfNull(acquire);

        if (ParseAfterRestart(args) is { } pid)
        {
            _ = waitForExit(pid, bound ?? DefaultWaitBound);
        }

        return acquire();
    }

    /// <summary>The real wait: true when the process is gone (or never existed, or is this one) within <paramref name="bound"/>.</summary>
    public static bool WaitForProcessExit(int pid, TimeSpan bound)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(bound);
        }
        catch (ArgumentException)
        {
            // No such process: it has already gone.
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
