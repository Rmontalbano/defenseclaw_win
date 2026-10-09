using System.Diagnostics;
using System.Windows;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>How the one automatic start of a launch ended (<see cref="GatewayAutoStart.LastOutcome"/>).</summary>
public enum GatewayAutoStartOutcome
{
    /// <summary>No attempt was made (yet, or ever this launch): see <see cref="GatewayAutoStart.LastReason"/>.</summary>
    None = 0,

    /// <summary><c>defenseclaw-gateway start</c> ran and exited 0.</summary>
    Started,

    /// <summary>The command ran and failed, did not finish, or could not be launched. Reported once, never retried.</summary>
    Failed,
}

/// <summary>The toast an automatic start asks for (<see cref="GatewayAutoStart.NoticeRaised"/>).</summary>
public sealed class GatewayAutoStartNoticeEventArgs : EventArgs
{
    public GatewayAutoStartNoticeEventArgs(string title, string body, bool isFailure)
    {
        Title = title;
        Body = body;
        IsFailure = isFailure;
    }

    public string Title { get; }

    public string Body { get; }

    public bool IsFailure { get; }
}

/// <summary>
/// Opt-in "start the gateway automatically" (CUST-211; the Mac's v1.1.25 headline feature). After a reboot the gateway, a background daemon
/// and not a service, stays down until someone starts it; with <c>startup.autoStartGateway</c> on, the app starts it once.
/// <para>
/// <b>One decision per launch.</b> The first <i>settled</i> snapshot from the monitor (anything but <see cref="AppGatewayState.Unknown"/>) decides,
/// and nothing after it ever does: a gateway that stops later in the session is not restarted behind the operator's back. The decision is
/// "start" only when all of these hold:
/// the setting is on; the state is <see cref="AppGatewayState.GatewayStopped"/> <i>because the port refused the connection</i>
/// (<see cref="GatewaySnapshot.PortRefused"/>), so never on a timeout, a 401, a malformed <c>/health</c> (those are Degraded or an
/// unreachable-but-not-refused stop) or a WSL relay holding the port; monitoring is not paused; the operator has not stopped the gateway
/// themselves this session (<see cref="MarkUserStopped"/>); and <c>defenseclaw-gateway</c> was found.
/// </para>
/// <para>
/// <b>The run is an ordinary CLI run:</b> <c>defenseclaw-gateway start</c>, the exact argv the Start action uses (<see cref="GatewayControl"/>),
/// through <see cref="CliRunner"/>, so it is in the Activity panel. It is tier StateChanging, which is why the setting asks for a one-time review
/// of it (<see cref="ConsentReview"/>) when it is switched on. The outcome is recorded and toasted (when gateway notifications are on); a failure
/// is reported once and not retried. A bare <c>defenseclaw-gateway</c> would start a second daemon: only the explicit verb is ever run.
/// </para>
/// </summary>
internal sealed class GatewayAutoStart : IDisposable
{
    /// <summary>The policy the consent review shows: when it will start the gateway, and when it will not.</summary>
    public const string PolicyText =
        "Once per launch, when DefenseClaw first looks at the gateway and nothing is listening on its port, it runs the command below. " +
        "It does not start the gateway when the check times out, is refused credentials or gets an answer it cannot read, when a WSL " +
        "gateway holds the port, while monitoring is paused, or after you stop the gateway yourself in this session. " +
        "Each run shows in Activity; a failed start is reported once and not retried.";

    private readonly Settings.AppSettingsStore _settings;
    private readonly IGatewaySnapshotSource _source;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<CliInvocation>> _run;
    private readonly Func<bool> _executableFound;
    private readonly Func<Task>? _afterRun;
    private readonly Action<Action> _post;
    private readonly Func<string?>? _installationBlockedReason;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();

    private bool _started;
    private bool _decided;
    private bool _userStopped;
    private bool _disposed;
    private GatewayAutoStartOutcome _outcome;
    private string _reason = "Not evaluated yet.";

    /// <param name="settings">Where <c>startup.autoStartGateway</c> and the gateway notification switch live.</param>
    /// <param name="source">The gateway monitor, or a fake.</param>
    /// <param name="run">Runs <c>defenseclaw-gateway</c> with an argv: <c>CliRunner.RunGatewayAsync</c>, or a fake. Never a real start in a test.</param>
    /// <param name="executableFound">Whether <c>defenseclaw-gateway</c> is installed (<c>DefenseClawPaths.GatewayCliPath</c>); called off the UI thread.</param>
    /// <param name="afterRun">Polls the monitor once the command has returned, so the shell follows the new state.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null.</param>
    /// <param name="installationBlockedReason">
    /// Why nothing may be changed on this installation (it is managed or invalid), asked when the one decision is taken; null, or one that answers
    /// null, means the installation may be changed. A start is a change, so a read-only installation is never started automatically.
    /// </param>
    public GatewayAutoStart(
        Settings.AppSettingsStore settings,
        IGatewaySnapshotSource source,
        Func<IReadOnlyList<string>, CancellationToken, Task<CliInvocation>> run,
        Func<bool> executableFound,
        Func<Task>? afterRun = null,
        Action<Action>? post = null,
        Func<string?>? installationBlockedReason = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _executableFound = executableFound ?? throw new ArgumentNullException(nameof(executableFound));
        _afterRun = afterRun;
        _post = post ?? PostToDispatcher;
        _installationBlockedReason = installationBlockedReason;
    }

    /// <summary>The service for the running app: the app's runner, paths, monitor and settings. Nothing runs until <see cref="Start"/>.</summary>
    internal static GatewayAutoStart Create(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return new GatewayAutoStart(
            services.Settings,
            services.Monitor,
            (argv, token) => services.Cli.RunGatewayAsync(argv, cancellationToken: token),
            () => services.Paths.GatewayCliPath is not null,
            async () => _ = await services.Monitor.RefreshAsync().ConfigureAwait(false),
            installationBlockedReason: () => services.Installation.BlockedReason);
    }

    /// <summary>
    /// Raised on the UI thread after an automatic start, and only when the operator has gateway notifications on
    /// (<c>notifications.gateway</c>): the toast the tray shows.
    /// </summary>
    public event EventHandler<GatewayAutoStartNoticeEventArgs>? NoticeRaised;

    /// <summary>How the attempt ended; <see cref="GatewayAutoStartOutcome.None"/> when none was made.</summary>
    public GatewayAutoStartOutcome LastOutcome
    {
        get
        {
            lock (_lock)
            {
                return _outcome;
            }
        }
    }

    /// <summary>Why it did, or did not, start: a sentence for the log and the tests.</summary>
    public string LastReason
    {
        get
        {
            lock (_lock)
            {
                return _reason;
            }
        }
    }

    /// <summary>True once the one decision of this launch has been taken (to start, or not to).</summary>
    public bool HasDecided
    {
        get
        {
            lock (_lock)
            {
                return _decided;
            }
        }
    }

    /// <summary>
    /// The operator stopped the gateway themselves (the Stop dialog, the tray, the Overview): for the rest of this session that wins, and no
    /// automatic start follows. Safe to call from any thread, before or after the decision.
    /// </summary>
    public void MarkUserStopped()
    {
        lock (_lock)
        {
            _userStopped = true;
        }
    }

    /// <summary>True after <see cref="MarkUserStopped"/>.</summary>
    public bool UserStopped
    {
        get
        {
            lock (_lock)
            {
                return _userStopped;
            }
        }
    }

    /// <summary>
    /// The review the Settings page shows when the operator switches the setting on: the exact command (<c>defenseclaw-gateway start</c>) and
    /// <see cref="PolicyText"/>. Declining it leaves the setting off.
    /// </summary>
    internal static CommandReview ConsentReview() => new()
    {
        Title = "Start the gateway automatically?",
        Summary = PolicyText,
        Steps = new[] { new CommandReviewStep(GatewayControl.Argv(GatewayAction.Start), executable: GatewayControl.Executable) },
        ConfirmLabel = "Turn on",
    };

    /// <summary>Begins following the monitor and looks at the snapshot it already has. Returns at once; a second call does nothing.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _source.StateChanged += OnStateChanged;
        Consider(_source.Current);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _source.StateChanged -= OnStateChanged;
        _stop.Cancel();
        _stop.Dispose();
    }

    private void OnStateChanged(object? sender, GatewaySnapshotEventArgs e) => Consider(e.Snapshot);

    /// <summary>The decision, taken at most once: on the first snapshot that says something (not <see cref="AppGatewayState.Unknown"/>).</summary>
    internal void Consider(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? skip;
        lock (_lock)
        {
            // Nothing is known yet (before the first poll, or a paused launch that has not polled): not a decision, wait for one.
            if (_decided || _disposed || snapshot.State == AppGatewayState.Unknown)
            {
                return;
            }

            _decided = true;
            skip = WhyNot(snapshot);
            if (skip is not null)
            {
                _reason = skip;
                return;
            }

            _reason = "Starting the gateway.";
        }

        _ = Task.Run(() => AttemptAsync(_stop.Token));
    }

    /// <summary>Null when every condition for a start holds; otherwise why not. Called under the lock.</summary>
    private string? WhyNot(GatewaySnapshot snapshot)
    {
        if (!_settings.Current.Startup.GatewayAutoStart)
        {
            return "Automatic gateway start is off.";
        }

        if (snapshot.IsPaused)
        {
            return "Monitoring is paused.";
        }

        if (_installationBlockedReason?.Invoke() is { } readOnly)
        {
            return "The installation is read-only, and a start is a change. " + readOnly;
        }

        if (_userStopped)
        {
            return "The gateway was stopped by the operator this session.";
        }

        if (snapshot.WslGatewayDetected || snapshot.State == AppGatewayState.WslGatewayDetected)
        {
            return "A WSL gateway holds the port.";
        }

        if (snapshot.State != AppGatewayState.GatewayStopped)
        {
            return $"The gateway is not stopped ({snapshot.StateLabel}).";
        }

        if (!snapshot.PortRefused)
        {
            return "The port did not refuse the connection (timed out or failed another way).";
        }

        return null;
    }

    private async Task AttemptAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_executableFound())
            {
                Record(GatewayAutoStartOutcome.None, "defenseclaw-gateway was not found.");
                return;
            }

            // The operator may have stopped it while the lookup ran.
            if (UserStopped)
            {
                Record(GatewayAutoStartOutcome.None, "The gateway was stopped by the operator this session.");
                return;
            }

            var argv = GatewayControl.Argv(GatewayAction.Start);
            var invocation = await _run(argv, cancellationToken).ConfigureAwait(false);

            if (invocation.ExitCode == 0)
            {
                Record(GatewayAutoStartOutcome.Started, "Started by defenseclaw-gateway start.");
                Raise("Gateway started", "DefenseClaw started the gateway automatically, as you asked in Settings.", isFailure: false);
            }
            else
            {
                var why = invocation.ExitCode is { } code
                    ? $"exit code {code}"
                    : invocation.FailureReason ?? "the command did not finish";
                Record(GatewayAutoStartOutcome.Failed, $"defenseclaw-gateway start failed: {why}.");
                Raise("Gateway did not start", $"The automatic start failed ({why}). See the Activity panel. It will not be retried.", isFailure: true);
            }

            if (_afterRun is not null)
            {
                await _afterRun().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
#pragma warning disable CA1031 // A start that cannot even be launched (CLI missing, process refused) is reported once, like any failed start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"gateway auto-start: {ex.GetType().Name}: {ex.Message}");
            Record(GatewayAutoStartOutcome.Failed, $"defenseclaw-gateway start could not run: {ex.Message}");
            Raise("Gateway did not start", $"The automatic start could not run: {ex.Message} It will not be retried.", isFailure: true);
        }
    }

    private void Record(GatewayAutoStartOutcome outcome, string reason)
    {
        lock (_lock)
        {
            _outcome = outcome;
            _reason = reason;
        }
    }

    private void Raise(string title, string body, bool isFailure)
    {
        if (!_settings.Current.Notifications.Gateway)
        {
            return;
        }

        _post(() => NoticeRaised?.Invoke(this, new GatewayAutoStartNoticeEventArgs(title, body, isFailure)));
    }

    private static void PostToDispatcher(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Shutdown began between the check and the post.
        }
    }
}
