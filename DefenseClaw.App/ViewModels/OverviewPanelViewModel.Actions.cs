using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>One read-only command of the Diagnostics menu: what it is called, what it runs, and what it runs on.</summary>
/// <param name="Title">The menu item, and the heading of the result line.</param>
/// <param name="Executable">What <paramref name="Argv"/> is handed to: <c>defenseclaw</c> or <c>defenseclaw-gateway</c>.</param>
/// <param name="Argv">The arguments, without the executable.</param>
/// <param name="Summary">What it does, for the tooltip.</param>
public sealed record DiagnosticCommand(string Title, string Executable, IReadOnlyList<string> Argv, string Summary)
{
    /// <summary>The whole command as a reviewer reads it.</summary>
    public string CommandText => CommandReview.CommandLine(Executable, Argv);
}

/// <summary>
/// Quick Actions (CUST-209): the Mac's row of buttons, through this app's safety model. Nothing here runs by itself and nothing changes
/// state without the shared review: Scan Skills and every gateway action open the in-panel <see cref="CommandReview"/> overlay (the exact argv,
/// its tier from <see cref="CommandTiers"/>, a confirm that must be pressed); the Diagnostics commands are read-only by the same classifier,
/// run straight away, and leave their full output in the Activity panel (the runner records every run). The Mac's "Run gateway as
/// administrator" is not offered.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The read-only Diagnostics menu, in the Mac's order. Each is held to <see cref="CommandTier.ReadOnly"/> by a test and again when it runs.</summary>
    internal static readonly IReadOnlyList<DiagnosticCommand> DiagnosticCommands = new DiagnosticCommand[]
    {
        new("Validate configuration", CommandReview.DefaultExecutable, new[] { "config", "validate" },
            "Check that config.yaml parses and names valid values."),
        new("Check credentials", CommandReview.DefaultExecutable, new[] { "keys", "check" },
            "Exit 0 when every required key is set."),
        new("Gateway status", GatewayControl.Executable, new[] { "status" },
            "Ask the running gateway for the health of its subsystems."),
        new("Show provenance", GatewayControl.Executable, new[] { "provenance", "show" },
            "Print the gateway's schema version, content hash, generation and binary version."),
    };

    /// <summary>What Scan Skills runs: every configured skill (<c>--all</c> is the CLI's explicit alias for that).</summary>
    internal static readonly string[] ScanSkillsArgv = { "skill", "scan", "--all" };

    /// <summary>Lines of a diagnostic's output the result line keeps; the whole of it is in Activity.</summary>
    private const int DiagnosticOutputLines = 8;

    private const int DiagnosticLineLimit = 240;

    private CancellationTokenSource? _diagnosticCts;

    /// <summary>The confirm-and-run overlay Scan Skills and the gateway actions go through (the same control the Discover panels use).</summary>
    public DiscoverActionReview Review { get; }

    [ObservableProperty]
    private bool _isDiagnosticRunning;

    [ObservableProperty]
    private string _diagnosticTitle = string.Empty;

    [ObservableProperty]
    private string _diagnosticMessage = string.Empty;

    [ObservableProperty]
    private bool _hasDiagnosticMessage;

    /// <summary>Ok / Bad / Warn / Neutral: the tone of the result badge.</summary>
    [ObservableProperty]
    private string _diagnosticKey = "Neutral";

    [ObservableProperty]
    private string _diagnosticOutput = string.Empty;

    [ObservableProperty]
    private bool _hasDiagnosticOutput;

    /// <summary>"Restart Gateway" while it is up, "Start Gateway" while it is not: the button the Mac shows in the same place.</summary>
    [ObservableProperty]
    private string _gatewayActionLabel = "Start Gateway";

    /// <summary>Why the start/restart button is off (shown as its tooltip), or what it does when it is on.</summary>
    [ObservableProperty]
    private string _gatewayActionTip = string.Empty;

    [ObservableProperty]
    private string _stopGatewayTip = string.Empty;

    private GatewayAction _gatewayAction = GatewayAction.Start;

    private bool _gatewayActionAllowed;

    private bool _stopAllowed;

    /// <summary>The Diagnostics items, for the menu.</summary>
    public IReadOnlyList<DiagnosticCommand> Diagnostics => DiagnosticCommands;

    /// <summary>Esc closes the review dialog. True when it consumed the key (the dialog was open, or is busy and must stay).</summary>
    public bool HandleEscape() => Review.IsOpen && Review.HandleEscape();

    // ---- Scan Skills ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Scan Skills: <c>defenseclaw skill scan --all</c>, which runs the scanners and records findings, so it is reviewed first (the Mac runs it
    /// on the click). The scan can take minutes; the review allows ten.
    /// </summary>
    [RelayCommand]
    private void ScanSkills() =>
        Review.Open(
            "Scan all skills?",
            "This runs the skill scanner over every configured skill and records what it finds. It changes scan results and findings; it does not change any skill.",
            new[]
            {
                new DiscoverStep(
                    ScanSkillsArgv,
                    "Scan every configured skill.",
                    CommandTier.StateChanging,
                    CliRunner.ExtendedTimeout),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            primaryText: "Scan skills");

    /// <summary>Open Inventory: the Discover panel, unfiltered. Nothing runs (the Mac's auto-scan on entry is not copied).</summary>
    [RelayCommand]
    private void OpenInventory() => RequestNavigation("inventory");

    // ---- Gateway start / restart / stop --------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRunGatewayAction))]
    private void RunGatewayAction() => OpenGatewayReview(_gatewayAction);

    [RelayCommand(CanExecute = nameof(CanStopGateway))]
    private void StopGateway() => OpenGatewayReview(GatewayAction.Stop);

    [RelayCommand(CanExecute = nameof(CanRestartFromMenu))]
    private void RestartGateway() => OpenGatewayReview(GatewayAction.Restart);

    private bool CanRunGatewayAction() => _gatewayActionAllowed;

    private bool CanStopGateway() => _stopAllowed;

    private bool CanRestartFromMenu() => GatewayControl.Availability(GatewayAction.Restart, _snapshot).Allowed;

    /// <summary>
    /// Start, stop or restart goes through the same review as the tray and the palette: the argv exactly as it will run
    /// (<c>defenseclaw-gateway restart</c>), what it does, and a button that must be pressed. The run is a <see cref="CliRunner"/> call, so its
    /// output is in Activity; the monitor is polled again when it finishes so the headline follows.
    /// </summary>
    private void OpenGatewayReview(GatewayAction action)
    {
        var (allowed, reason) = GatewayControl.Availability(action, _snapshot);
        if (!allowed)
        {
            ShowDiagnosticMessage(GatewayControl.Title(action), "Neutral", reason ?? "Not available right now.", string.Empty);
            return;
        }

        Review.Open(
            GatewayControl.Title(action) + "?",
            GatewayControl.ReviewNote(action),
            new[]
            {
                new DiscoverStep(
                    GatewayControl.Argv(action),
                    GatewayControl.Summary(action),
                    CommandTier.StateChanging,
                    Executable: GatewayControl.Executable),
            },
            onFinished: _ =>
            {
                // A Stop the operator ran is theirs for the session: no automatic start follows it.
                if (action == GatewayAction.Stop)
                {
                    Services.GatewayAutoStart.MarkUserStopped();
                }

                return RefreshAfterActionAsync();
            },
            primaryText: GatewayControl.Title(action));
    }

    private async Task RefreshAfterActionAsync()
    {
        _ = await Services.Monitor.RefreshAsync().ConfigureAwait(true);
        await RefreshMetricsAsync(force: true, ActiveToken).ConfigureAwait(true);
    }

    /// <summary>Keeps the start/restart and stop buttons to what <see cref="GatewayControl.Availability"/> says of the snapshot, with the reason as a tooltip.</summary>
    private void ApplyGatewayActions(GatewaySnapshot snapshot)
    {
        _gatewayAction = snapshot.IsRunning ? GatewayAction.Restart : GatewayAction.Start;
        var (allowed, reason) = GatewayControl.Availability(_gatewayAction, snapshot);
        var (stopAllowed, stopReason) = GatewayControl.Availability(GatewayAction.Stop, snapshot);

        GatewayActionLabel = GatewayControl.Title(_gatewayAction).Replace("gateway", "Gateway", StringComparison.Ordinal);
        GatewayActionTip = allowed ? GatewayControl.Summary(_gatewayAction) + " Asks for confirmation first." : reason ?? string.Empty;
        StopGatewayTip = stopAllowed ? GatewayControl.Summary(GatewayAction.Stop) + " Asks for confirmation first." : stopReason ?? string.Empty;

        var changed = allowed != _gatewayActionAllowed || stopAllowed != _stopAllowed;
        _gatewayActionAllowed = allowed;
        _stopAllowed = stopAllowed;
        if (changed)
        {
            RunGatewayActionCommand.NotifyCanExecuteChanged();
            StopGatewayCommand.NotifyCanExecuteChanged();
            RestartGatewayCommand.NotifyCanExecuteChanged();
        }
    }

    // ---- Diagnostics (read-only) ---------------------------------------------------------------------------------

    /// <summary>
    /// Runs one Diagnostics command. It must classify as read-only (<see cref="CommandTiers"/>), or it refuses rather than run unreviewed;
    /// the run goes through <see cref="CliRunner"/>, which puts the exact argv, the output and the exit code in the Activity panel.
    /// </summary>
    [RelayCommand]
    private async Task RunDiagnosticAsync(DiagnosticCommand? command)
    {
        if (command is null || IsDiagnosticRunning)
        {
            return;
        }

        if (CommandReview.ResolveTier(command.Argv) != CommandTier.ReadOnly)
        {
            ShowDiagnosticMessage(command.Title, "Bad", $"'{command.CommandText}' is no longer classified read-only, so it will not run without a review step.", string.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _diagnosticCts = cts;
        IsDiagnosticRunning = true;
        ShowDiagnosticMessage(command.Title, "Neutral", $"Running '{command.CommandText}'…", string.Empty);

        try
        {
            var invocation = await Services.Cli
                .RunNamedAsync(command.Executable, command.Argv, cancellationToken: cts.Token)
                .ConfigureAwait(true);

            var tail = Tail(invocation);
            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                ShowDiagnosticMessage(command.Title, "Bad", $"'{command.CommandText}' did not finish: {reason}. The Activity panel has what it printed.", tail);
            }
            else if (invocation.ExitCode == 0)
            {
                ShowDiagnosticMessage(command.Title, "Ok", $"'{command.CommandText}' succeeded (exit 0). The full output is in the Activity panel.", tail);
            }
            else
            {
                ShowDiagnosticMessage(
                    command.Title,
                    "Warn",
                    $"'{command.CommandText}' exited {invocation.ExitCode?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "?"}. The full output is in the Activity panel.",
                    tail);
            }
        }
        catch (CliNotFoundException ex)
        {
            ShowDiagnosticMessage(command.Title, "Bad", $"'{ex.ExecutableName}' was not found: {ex.Message}", string.Empty);
        }
        catch (SecretInArgumentException ex)
        {
            ShowDiagnosticMessage(command.Title, "Bad", ex.Message, string.Empty);
        }
        finally
        {
            IsDiagnosticRunning = false;
            _diagnosticCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Stops a diagnostic in flight; the runner kills its whole process tree.</summary>
    [RelayCommand]
    private void CancelDiagnostic()
    {
        try
        {
            _diagnosticCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished in the instant the button was pressed.
        }
    }

    /// <summary>Open Activity, where every command this panel ran is recorded with its full output.</summary>
    [RelayCommand]
    private void OpenActivity() => RequestNavigation("activity");

    /// <summary>Open the command palette (the Mac's last Diagnostics item).</summary>
    [RelayCommand]
    private void OpenCommandPalette() => Services.Navigation.RequestPalette();

    private void ShowDiagnosticMessage(string title, string key, string message, string output)
    {
        DiagnosticTitle = title;
        DiagnosticKey = key;
        DiagnosticMessage = message;
        DiagnosticOutput = output;
        HasDiagnosticOutput = output.Length > 0;
        HasDiagnosticMessage = message.Length > 0;
    }

    /// <summary>The last few non-empty lines of what the command printed, each cut to a sensible width: the result line's excerpt.</summary>
    private static string Tail(CliInvocation invocation)
    {
        var lines = invocation.OutputLines
            .Select(static l => l.Text)
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var shown = lines.Skip(Math.Max(0, lines.Count - DiagnosticOutputLines))
            .Select(static l => l.Length > DiagnosticLineLimit ? l[..DiagnosticLineLimit] + "…" : l);
        var omitted = lines.Count - Math.Min(lines.Count, DiagnosticOutputLines);
        return (omitted > 0 ? $"… {omitted} earlier line(s) in Activity{Environment.NewLine}" : string.Empty) + string.Join(Environment.NewLine, shown);
    }
}
