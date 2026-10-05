using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.ViewModels;

/// <summary>One row of the Setup readiness checklist as the card draws it.</summary>
public sealed class ReadinessRowViewModel
{
    internal ReadinessRowViewModel(ReadinessCheck check, Action<ReadinessCheck> fix)
    {
        Check = check;
        FixCommand = new RelayCommand(() => fix(check), () => check.Fix is not null);
    }

    internal ReadinessCheck Check { get; }

    public string Title => Check.Title;

    public string Detail => Check.Detail;

    public ReadinessStatus Status => Check.Status;

    /// <summary>The word on the badge; the colour is never the only cue.</summary>
    public string StatusText => Check.Status switch
    {
        ReadinessStatus.Pass => "pass",
        ReadinessStatus.Warn => "warn",
        _ => "fail",
    };

    /// <summary>Tone key of the badge (Ok / Warn / Bad).</summary>
    public string StatusKey => Check.Status switch
    {
        ReadinessStatus.Pass => "Ok",
        ReadinessStatus.Warn => "Warn",
        _ => "Bad",
    };

    public bool HasFix => Check.Fix is not null;

    /// <summary>The TUI's command for the row, as text: what the Fix button's tooltip says it stands for.</summary>
    public string FixCommandText => Check.Fix?.CommandText ?? string.Empty;

    public string FixToolTip => Check.Fix switch
    {
        null => string.Empty,
        { Kind: ReadinessFixKind.Terminal } fix => $"Opens a console window running {fix.CommandText} (it prompts for secrets, so it cannot run in this app)",
        { Kind: ReadinessFixKind.Wizard } fix => $"Opens the {fix.WizardTarget} setup wizard (the TUI runs {fix.CommandText})",
        var fix => $"Review, then run: {fix.CommandText}",
    };

    public string FixAutomationName => $"Fix {Check.Title}";

    public IRelayCommand FixCommand { get; }
}

/// <summary>
/// The Setup panel's readiness checklist: <see cref="SetupReadiness"/> over what the panel already holds, one Fix per failing row.
/// <para>
/// <b>Nothing new is polled.</b> The inputs are the loaded config.yaml, the gateway monitor's last snapshot, the Credentials card's last read
/// and the doctor cache file (read once when the panel activates or refreshes, like the Overview does; the Overview's parsed copy is private
/// to it). The rows are rebuilt when the panel activates, on Refresh, when the gateway state changes and when the credential read lands - never
/// on a timer, and nothing here runs while the panel is hidden.
/// </para>
/// <para>
/// <b>Fixes never run by themselves.</b> A <see cref="ReadinessFixKind.Review"/> fix opens the shared review (the exact argv, the tier from
/// <c>CommandTiers</c>, a restart warning where one applies) and runs through <c>CliRunner</c>, so it is in Activity; a
/// <see cref="ReadinessFixKind.Terminal"/> fix opens a console for a command that prompts for a secret; a <see cref="ReadinessFixKind.Wizard"/>
/// fix opens the Setup wizard whose own review shows the flags.
/// </para>
/// </summary>
public sealed partial class ReadinessViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly CredentialsViewModel _credentials;
    private readonly DiscoverActionReview _review;
    private IReadOnlyList<string> _doctorMissing = Array.Empty<string>();
    private string _restartReason = string.Empty;

    public ReadinessViewModel(AppServices services, CredentialsViewModel credentials, DiscoverActionReview review)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _review = review ?? throw new ArgumentNullException(nameof(review));
        OpenWizard = DefaultOpenWizardAsync;
    }

    public ObservableCollection<ReadinessRowViewModel> Rows { get; } = new();

    /// <summary>Opens the Setup wizard for a target (<c>llm</c>); a test replaces it so no window is built.</summary>
    internal Func<string, Task> OpenWizard { get; set; }

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Number of rows that are not passing.</summary>
    [ObservableProperty]
    private int _attentionCount;

    /// <summary>
    /// Queues a gateway restart (the TUI's <c>RestartQueue</c>): "Restart Pending" shows a Fix until a restart runs. The panel queues one when a
    /// change it made (a guardrail verb run with <c>--no-restart</c>) is saved but not yet in effect.
    /// </summary>
    public void QueueRestart(string reason)
    {
        _restartReason = reason ?? string.Empty;
        Rebuild();
    }

    /// <summary>Re-reads the doctor cache (a small file) and rebuilds. For activation and Refresh, not for a timer.</summary>
    public async Task ReloadAsync()
    {
        _doctorMissing = await ReadDoctorMissingAsync().ConfigureAwait(true);
        Rebuild();
    }

    private async Task<IReadOnlyList<string>> ReadDoctorMissingAsync()
    {
        var path = _services.Paths.DoctorCachePath;
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            var json = await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(path, CancellationToken.None).ConfigureAwait(true);
            return DoctorReconciliation.MissingRequiredCredentials(DoctorCacheReader.Parse(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable cache is the Overview's to report; here it just adds nothing to the credential row.
            return Array.Empty<string>();
        }
    }

    /// <summary>Builds the inputs from what is already in memory and fills <see cref="Rows"/>.</summary>
    public void Rebuild()
    {
        var snapshot = _services.Monitor.Current;
        var health = snapshot.Health;
        var guardrail = _services.Config.Config.Guardrail;

        var inputs = new ReadinessInputs
        {
            ActiveConnectors = snapshot.ActiveConnectors,
            Gateway = snapshot.State switch
            {
                AppGatewayState.Running or AppGatewayState.Degraded => AppGatewayStateKind.Reachable,
                AppGatewayState.GatewayStopped => AppGatewayStateKind.Stopped,
                AppGatewayState.WslGatewayDetected or AppGatewayState.NotInstalled or AppGatewayState.NotInitialized => AppGatewayStateKind.NotRunnableHere,
                _ => AppGatewayStateKind.Unknown,
            },
            GatewayDetail = snapshot.Detail,
            GatewaySubsystemState = health?.FleetUplink?.State ?? string.Empty,
            ApiState = health?.Api?.State ?? string.Empty,
            GuardrailEnabled = guardrail.Enabled,
            // The TUI reads one guardrail.mode; 0.8.10 keeps the mode per connector, so name the first one's.
            GuardrailMode = guardrail.Connectors.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value.Mode).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? string.Empty,
            Credentials = _credentials.Snapshot,
            DoctorMissingCredentials = _doctorMissing,
            Config = ReadinessConfig.From(_services.Config),
            RestartReason = _restartReason,
        };

        Apply(SetupReadiness.Build(inputs));
    }

    /// <summary>Shows <paramref name="checks"/> (built from the inputs, or supplied by a test).</summary>
    internal void Apply(IReadOnlyList<ReadinessCheck> checks)
    {
        Rows.Clear();
        foreach (var check in checks)
        {
            Rows.Add(new ReadinessRowViewModel(check, OnFix));
        }

        AttentionCount = checks.Count(c => c.Status != ReadinessStatus.Pass);
        Summary = AttentionCount == 0
            ? "Everything the TUI checks is ready."
            : AttentionCount == 1 ? "1 item needs attention." : $"{AttentionCount} items need attention.";
    }

    private void OnFix(ReadinessCheck check)
    {
        if (check.Fix is not { } fix)
        {
            return;
        }

        switch (fix.Kind)
        {
            case ReadinessFixKind.Terminal:
                _ = _credentials.OpenInTerminalAsync(fix.Steps[0].Argv);
                break;

            case ReadinessFixKind.Wizard:
                _ = OpenWizard(fix.WizardTarget ?? string.Empty);
                break;

            default:
                OpenReview(check, fix);
                break;
        }
    }

    private void OpenReview(ReadinessCheck check, ReadinessFix fix)
    {
        var steps = fix.Steps
            .Select(s => new DiscoverStep(s.Argv, s.Purpose, Executable: s.Executable))
            .ToArray();

        _review.Open(
            $"Fix: {check.Title}",
            check.Detail,
            steps,
            onFinished: result => AfterFixAsync(fix, result),
            restartsGateway: fix.RestartsGateway);
    }

    private async Task AfterFixAsync(ReadinessFix fix, DiscoverReviewResult result)
    {
        if (result.Succeeded && fix.RestartsGateway)
        {
            _restartReason = string.Empty;
        }

        // A fix may change the credential list (doctor --fix) and the config the rows read: take both again.
        await _credentials.RefreshAsync().ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task DefaultOpenWizardAsync(string target)
    {
        if (target.Length == 0)
        {
            return;
        }

        await WizardLauncher.ShowAsync(_services, target, Application.Current?.MainWindow).ConfigureAwait(true);
        Rebuild();
    }
}
