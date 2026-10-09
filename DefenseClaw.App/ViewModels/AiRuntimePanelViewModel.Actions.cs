using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Poll now, Enable and Disable (the CLI's <c>agent discovery runtime scan | enable | disable</c>). Every one opens the shared review first and runs
/// through <see cref="CliRunner"/>, so the exact argv is shown, confirmed and recorded in Activity; none starts on its own. Each is off while the
/// snapshot is stale, the gateway does not serve the planes, the runtime does not list the command, or the data folder is read-only.
/// </summary>
public sealed partial class AiRuntimePanelViewModel
{
    public const string PollExplanation =
        "Asks the running DefenseClaw gateway to poll the runtime planes right now instead of waiting for its interval, and replaces the snapshot " +
        "on this page with the result. A poll reads this PC's process table and connection table (and, with plane C on, the Security-log events " +
        "it has collected). It changes no setting.\n\n" +
        "It needs the gateway running with the runtime planes enabled; otherwise the command fails (HTTP 503) and nothing changes.";

    public const string EnableExplanation =
        "Turns the AI Discovery runtime planes on in config.yaml (ai_discovery.runtime) and, unless you untick the restart, restarts the gateway so " +
        "they start. Plane A (inference heartbeat) and plane B (shadow egress) read this PC's process and connection tables and need no elevation " +
        "for your own processes. Plane C (agent actions) reads kernel and Security-log events; it needs an elevated gateway and the Advanced Audit " +
        "Policy (see Prerequisites below) and is only turned on if you choose it here. A plane that cannot run says why on this page.";

    public const string DisableExplanation =
        "Turns the runtime planes off in config.yaml (ai_discovery.runtime) and, unless you untick the restart, restarts the gateway so they stop. " +
        "Findings already recorded are kept. While they are off, nothing is watching for shadow AI network traffic or unattributed agent actions.";

    public const string PlaneCWarning =
        "Plane C reads kernel process, file and identity events, and every signal it raises is gated on an AI agent in the process lineage. " +
        "Without an elevated gateway and the audit policy it stays blind and reports why; it never reports a quiet host in its place.";

    private int _hostPlaneIndex;
    private int _dnsCaptureIndex;

    /// <summary>0 keep as it is, 1 on, 2 off: plane C (<c>--enable-host-plane</c> / <c>--no-enable-host-plane</c>).</summary>
    public int HostPlaneIndex
    {
        get => _hostPlaneIndex;
        set => SetProperty(ref _hostPlaneIndex, Math.Clamp(value, 0, 2));
    }

    /// <summary>0 keep as it is, 1 on, 2 off: passive DNS observation (<c>--dns-capture</c> / <c>--no-dns-capture</c>).</summary>
    public int DnsCaptureIndex
    {
        get => _dnsCaptureIndex;
        set => SetProperty(ref _dnsCaptureIndex, Math.Clamp(value, 0, 2));
    }

    /// <summary>Seconds between polls, blank to keep the current one (<c>--poll-interval-s</c>, 5 to 3600).</summary>
    [ObservableProperty]
    private string _pollIntervalText = string.Empty;

    /// <summary>The score a finding must reach to be reported, blank to keep the current one (<c>--min-risk-to-report</c>, 1 to 100).</summary>
    [ObservableProperty]
    private string _minRiskText = string.Empty;

    /// <summary>Restart the gateway to apply (the CLI's default). Unticked adds <c>--no-restart</c>: the setting is saved and applies at the next restart.</summary>
    [ObservableProperty]
    private bool _restartOnChange = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOptionsError))]
    private string _optionsError = string.Empty;

    public bool HasOptionsError => OptionsError.Length > 0;

    public bool HasLastRun => LastRunSummary.Length > 0;

    /// <summary>Why every change is off, whatever the snapshot says (a read-only data folder); null when nothing forbids changes.</summary>
    public string? ReadOnlyReason =>
        Services.Paths.DataDirectoryReadOnly
            ? "This runtime's data folder is a read-only copy (the developer container runtime), so changes from here are off."
            : null;

    public string? PollBlockedReason => BlockedReason(AiRuntimeCommands.ScanCommand, requiresEnabled: true);

    /// <summary>Enable is also how plane settings are changed while the planes are on, so it only needs a fresh snapshot, not a disabled one.</summary>
    public string? EnableBlockedReason => BlockedReason(AiRuntimeCommands.EnableCommand, requiresEnabled: false);

    public string? DisableBlockedReason => BlockedReason(AiRuntimeCommands.DisableCommand, requiresEnabled: true);

    public bool CanPollNow => PollBlockedReason is null;

    public bool CanEnable => EnableBlockedReason is null;

    public bool CanDisable => DisableBlockedReason is null;

    /// <summary>"Enable…" while the planes are off, "Change settings…" while they are on (the same command: it applies what is chosen).</summary>
    public string EnableButtonText => _snapshot is { Enabled: true } ? "Change settings…" : "Enable…";

    /// <summary>
    /// The sentence under the buttons when any is off: the one reason when they share it, else each button's own. Visible text, not only a
    /// tooltip, so a disabled button never leaves the reader guessing.
    /// </summary>
    public string ActionsNote
    {
        get
        {
            var reasons = new (string Button, string? Reason)[]
            {
                ("Poll now", PollBlockedReason),
                ("Enable", EnableBlockedReason),
                ("Disable", DisableBlockedReason),
            }.Where(r => r.Reason is not null).ToArray();

            if (reasons.Length == 0)
            {
                return string.Empty;
            }

            // Every button off for one reason: say the reason once. Otherwise group the buttons that share one, so the same sentence is not repeated.
            if (reasons.Length == 3 && reasons.Select(r => r.Reason).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                return reasons[0].Reason!;
            }

            return string.Join(
                " ",
                reasons.GroupBy(r => r.Reason, StringComparer.Ordinal).Select(group =>
                {
                    var buttons = group.Select(r => r.Button).ToArray();
                    var subject = buttons.Length == 1 ? buttons[0] + " is" : string.Join(", ", buttons[..^1]) + " and " + buttons[^1] + " are";
                    return $"{subject} off: {group.Key}";
                }));
        }
    }

    public bool HasActionsNote => ActionsNote.Length > 0;

    private string? BlockedReason(string command, bool requiresEnabled)
    {
        if (ReadOnlyReason is { } readOnly)
        {
            return readOnly;
        }

        if (!Services.Runtime.Capabilities.HasAiRuntimeCommand(command))
        {
            return $"This DefenseClaw runtime does not list 'agent discovery runtime {command}'.";
        }

        if (State == AiRuntimeState.Unsupported)
        {
            return "This gateway does not serve the runtime planes.";
        }

        if (_snapshot is null)
        {
            return IsLoading ? "The snapshot is still being read." : "No snapshot has been read yet, so the state of the planes is not known.";
        }

        if (IsStale)
        {
            return "The snapshot on screen is stale" + (StaleReason.Length > 0 ? " (" + StaleReason.TrimEnd('.') + ")" : string.Empty) + "; refresh it first.";
        }

        if (IsLoading)
        {
            return "The snapshot is being read again.";
        }

        if (Review.IsOpen || Review.IsRunning)
        {
            return "Another change is open.";
        }

        if (requiresEnabled && !_snapshot.Enabled)
        {
            return "The runtime planes are disabled; enable them first.";
        }

        return null;
    }

    /// <summary>Recomputes which buttons are live and the sentence under them. Called whenever anything they depend on changed.</summary>
    internal void RaiseActionState()
    {
        OnPropertyChanged(nameof(ReadOnlyReason));
        OnPropertyChanged(nameof(PollBlockedReason));
        OnPropertyChanged(nameof(EnableBlockedReason));
        OnPropertyChanged(nameof(DisableBlockedReason));
        OnPropertyChanged(nameof(CanPollNow));
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(CanDisable));
        OnPropertyChanged(nameof(EnableButtonText));
        OnPropertyChanged(nameof(ActionsNote));
        OnPropertyChanged(nameof(HasActionsNote));
        PollNowCommand.NotifyCanExecuteChanged();
        EnablePlanesCommand.NotifyCanExecuteChanged();
        DisablePlanesCommand.NotifyCanExecuteChanged();
    }

    // ---- Poll now ---------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanPollNow))]
    private void PollNow()
    {
        if (PollBlockedReason is not null)
        {
            return;
        }

        var argv = AiRuntimeCommands.PollNow;
        Review.Open(
            "Poll the runtime planes now?",
            PollExplanation,
            new[]
            {
                new DiscoverStep(argv, "Ask the gateway to poll the runtime planes immediately.", CommandTier.StateChanging, TimeSpan.FromMinutes(3)),
            },
            result => AfterRunAsync(result, argv),
            primaryText: "Poll now");
    }

    // ---- Enable / change settings ------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private void EnablePlanes()
    {
        if (EnableBlockedReason is not null)
        {
            return;
        }

        if (!TryReadOptions(out var options, out var error))
        {
            OptionsError = error;
            return;
        }

        OptionsError = string.Empty;
        var argv = AiRuntimeCommands.Enable(options);
        var turningOn = _snapshot is not { Enabled: true };

        Review.Open(
            turningOn ? "Turn on the runtime planes?" : "Apply these runtime plane settings?",
            EnableExplanation + "\n\n" + Describe(options),
            new[]
            {
                new DiscoverStep(
                    argv,
                    turningOn ? "Turn on the AI Discovery runtime planes." : "Apply the runtime plane settings.",
                    CommandTier.StateChanging,
                    TimeSpan.FromMinutes(5)),
            },
            result => AfterRunAsync(result, argv),
            restartsGateway: options.Restart,
            warning: options.HostPlane == true ? PlaneCWarning : null,
            primaryText: turningOn ? "Turn on" : "Apply");
    }

    /// <summary>Reads the form into options; false with a sentence when a number is not one the CLI accepts. Blank means "leave it".</summary>
    internal bool TryReadOptions(out AiRuntimeEnableOptions options, out string error)
    {
        options = new AiRuntimeEnableOptions();
        error = string.Empty;

        if (!TryReadNumber(PollIntervalText, "poll interval", out var interval, out error) ||
            !TryReadNumber(MinRiskText, "reporting floor", out var floor, out error))
        {
            return false;
        }

        var candidate = new AiRuntimeEnableOptions(
            HostPlane: Tri(HostPlaneIndex),
            DnsCapture: Tri(DnsCaptureIndex),
            PollIntervalSeconds: interval,
            MinRiskToReport: floor,
            Restart: RestartOnChange);

        if (AiRuntimeCommands.Validate(candidate) is { } problem)
        {
            error = problem;
            return false;
        }

        options = candidate;
        return true;
    }

    private static bool? Tri(int index) => index switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    private static bool TryReadNumber(string text, string what, out int? value, out string error)
    {
        value = null;
        error = string.Empty;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"The {what} must be a whole number, or blank to keep the current one.";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>The choices as sentences, so the review says what each flag does before the command line does.</summary>
    internal static string Describe(AiRuntimeEnableOptions options)
    {
        var text = new StringBuilder("What this sets:");
        text.Append("\n- Plane C (agent actions): ").Append(options.HostPlane switch
        {
            true => "turned on.",
            false => "turned off.",
            _ => "left as it is (off unless it was turned on before).",
        });
        text.Append("\n- DNS capture: ").Append(options.DnsCapture switch
        {
            true => "turned on, so peers are named from the answers this PC resolved.",
            false => "turned off, so peers are named by reverse DNS (less direct).",
            _ => "left as it is.",
        });
        text.Append("\n- Poll interval: ").Append(options.PollIntervalSeconds is { } interval
            ? interval.ToString(CultureInfo.InvariantCulture) + " seconds."
            : "left as it is.");
        text.Append("\n- Reporting floor: ").Append(options.MinRiskToReport is { } floor
            ? "findings need a score of " + floor.ToString(CultureInfo.InvariantCulture) + " or more."
            : "left as it is.");
        text.Append("\n- Gateway restart: ").Append(options.Restart
            ? "yes, so the change takes effect now."
            : "no (--no-restart): the setting is saved and applies the next time the gateway restarts.");
        return text.ToString();
    }

    // ---- Disable ----------------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private void DisablePlanes()
    {
        if (DisableBlockedReason is not null)
        {
            return;
        }

        var restart = RestartOnChange;
        var argv = AiRuntimeCommands.Disable(restart);
        Review.Open(
            "Turn off the runtime planes?",
            DisableExplanation,
            new[]
            {
                new DiscoverStep(argv, "Turn off the AI Discovery runtime planes.", CommandTier.StateChanging, TimeSpan.FromMinutes(5)),
            },
            result => AfterRunAsync(result, argv),
            restartsGateway: restart,
            primaryText: "Turn off");
    }

    // ---- After a run ------------------------------------------------------------------------------------------------------

    /// <summary>Summarises the finished run on the panel and reads the snapshot again: a poll, an enable and a disable each change what it says.</summary>
    private Task AfterRunAsync(DiscoverReviewResult result, IReadOnlyList<string> argv)
    {
        var command = DiscoverCli.CommandLine(argv);
        var last = result.Invocations.Count > 0 ? result.Invocations[^1] : null;

        LastRunSummary = last is null
            ? $"{command} did not start."
            : last.FailureReason is { Length: > 0 } reason
                ? $"{command} did not complete: {reason}"
                : $"{command} finished at {last.FinishedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} " +
                  $"(exit {last.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}). See Activity for the full output.";
        OnPropertyChanged(nameof(HasLastRun));

        return LoadAsync();
    }

    partial void OnLastRunSummaryChanged(string value) => OnPropertyChanged(nameof(HasLastRun));
}
