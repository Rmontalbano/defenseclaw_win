using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>
/// The app-level actions the shell can trigger from the keyboard or the command palette, each
/// routed through the path the rest of the app already uses: the tray service for gateway control
/// and autostart (so the review step, the Activity record and the toasts are identical), the
/// windows' own <c>Show</c> entry points for the config editor and the update check, and the
/// panel's own <c>RefreshCommand</c> for F5.
/// <para>
/// Nothing here runs arbitrary text. The palette is a fixed list of these named actions plus
/// navigation; there is no free-form command entry.
/// </para>
/// </summary>
internal sealed class ShellActions
{
    private readonly AppServices _services;
    private readonly PanelCatalog _catalog;
    private readonly TrayIconService _tray;
    private readonly Func<Window?> _owner;

    /// <param name="owner">The dashboard window, as the owner of review dialogs (null-safe).</param>
    public ShellActions(AppServices services, PanelCatalog catalog, TrayIconService tray, Func<Window?> owner)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>The gateway snapshot the availability rules are evaluated against.</summary>
    public GatewaySnapshot Snapshot => _services.Monitor.Current;

    /// <summary>
    /// The current panel's <c>RefreshCommand</c> (by convention every panel view-model exposes one),
    /// or null when the panel on screen has none. Found by name so a panel needs no shell-specific
    /// interface: GA's F5 binds to whatever the panel already calls its refresh.
    /// </summary>
    private ICommand? CurrentRefreshCommand =>
        _catalog.ActiveViewModel is { } viewModel
            ? viewModel.GetType().GetProperty("RefreshCommand", BindingFlags.Instance | BindingFlags.Public)?.GetValue(viewModel) as ICommand
            : null;

    /// <summary>True when F5 / "Refresh current panel" would do something right now.</summary>
    public bool CanRefreshCurrentPanel => CurrentRefreshCommand is { } command && command.CanExecute(null);

    /// <summary>Runs the current panel's refresh. False when there is none or it is already running.</summary>
    public bool TryRefreshCurrentPanel()
    {
        if (CurrentRefreshCommand is not { } command || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    /// <summary>
    /// Shows <paramref name="panelId"/>, telling it <paramref name="payload"/> if there is one: the deep link behind the
    /// status strip's chips and the palette's "open Alerts on the critical ones". The window is brought up if it is in the
    /// tray. See <see cref="ShellNavigation"/>.
    /// </summary>
    public void OpenPanel(string panelId, object? payload = null) => _services.Navigation.Request(panelId, payload);

    /// <summary>One gateway poll now, the same as the status strip's Refresh button.</summary>
    public void RefreshGatewayStatus() => _ = RefreshGatewayStatusAsync();

    private async Task RefreshGatewayStatusAsync()
    {
        try
        {
            _ = await _services.Monitor.RefreshAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed poll is reported by the strip itself; this is fire-and-forget.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Gateway refresh from the shell failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    public void OpenConfigEditor() => Views.ConfigEditor.ConfigEditorWindow.Show(_services);

    /// <summary>
    /// Opens the Updates window, which runs the release check as it opens, and has the background watcher look again (through the same
    /// 24 h cache) so the dashboard's banner and the window agree. The upgrade itself stays in the window.
    /// </summary>
    public void CheckForUpdates()
    {
        _ = _services.UpdateWatcher.CheckNowAsync();
        _ = Views.Updates.UpdatesWindow.Show(_services);
    }

    /// <summary>Start / stop / restart through the tray's path: review, run via the CLI runner, toast.</summary>
    public Task RunGatewayActionAsync(GatewayAction action) => _tray.RunGatewayActionAsync(action, _owner());

    public bool IsAutostartEnabled => AutostartManager.IsEnabled;

    public void ToggleAutostart() => _ = _tray.ToggleAutostart();

    // ---- The Mac's Monitor / Commands menus (CUST-224) ----

    private CuratedCommandCatalog? _curated;
    private int _diagnosing;
    private int _curatedRunning;

    /// <summary>Test seam: where copied text goes (default: the Windows clipboard).</summary>
    internal Action<string>? ClipboardWriter { get; set; }

    /// <summary>Test seam: asks for a save path given a suggested file name; null cancels (default: a SaveFileDialog).</summary>
    internal Func<string, string?>? SavePathPicker { get; set; }

    /// <summary>Test seam: shows a result toast (default: the tray's notification).</summary>
    internal Action<string, string>? Toast { get; set; }

    /// <summary>Test seam: shows a review and says whether the operator confirmed it (default: the gateway-action dialog window).</summary>
    internal Func<CommandReview, bool>? Confirmer { get; set; }

    /// <summary>Test seam: the curated-commands catalog (default: one over the shared Setup catalog's help probe).</summary>
    internal CuratedCommandCatalog Curated
    {
        get => _curated ??= new CuratedCommandCatalog(WizardCatalog.Shared(_services).Probe);
        set => _curated = value;
    }

    private void ShowToast(string title, string message)
    {
        if (Toast is { } toast)
        {
            toast(title, message);
        }
        else
        {
            _tray.Notify(title, message);
        }
    }

    /// <summary>
    /// Run health check (Ctrl+Shift+H): the Overview's Doctor flow, which shows the card and puts focus on its Run doctor button.
    /// Nothing runs until the operator presses it, so the run itself is the reviewed one the panel already has.
    /// </summary>
    public void RunHealthCheck() => OpenPanel("overview", new OverviewFocus(OverviewFocus.DoctorSection));

    /// <summary>Scan AI components (Ctrl+Shift+A): opens AI Discovery on its reviewed "Run an AI discovery scan?" dialog.</summary>
    public void ScanAiComponents() => OpenPanel("ai-discovery", new AiDiscoveryScan());

    /// <summary>The argv Background diagnose runs: plain <c>doctor</c>, on the allow-list of reads (<see cref="CommandTiers.UnreviewedReadPaths"/>; never <c>--fix</c>).</summary>
    internal static readonly string[] DiagnoseArgv = { "doctor" };

    /// <summary>
    /// Diagnose in Background (Ctrl+Shift+D): <c>defenseclaw doctor</c> without a window or a review, because it is on the explicit
    /// allow-list of read-only commands (checked here, so a change to the argv that took it off the list would refuse to run), then a toast
    /// with the result. The run is in the Activity panel like every other. One at a time.
    /// </summary>
    public async Task DiagnoseInBackgroundAsync()
    {
        if (!CommandReview.MayRunUnreviewed(DiagnoseArgv))
        {
            ShowToast("Diagnose", "Refused: the diagnostic command is not on the list of read-only commands.");
            return;
        }

        if (Interlocked.Exchange(ref _diagnosing, 1) == 1)
        {
            ShowToast("Diagnose", "A diagnosis is already running.");
            return;
        }

        try
        {
            ShowToast("Diagnose", "Running the doctor in the background. Nothing is changed.");
            var invocation = await _services.Cli.RunAsync(DiagnoseArgv).ConfigureAwait(true);
            ShowToast(
                "Diagnose",
                invocation.ExitCode == 0
                    ? "The doctor finished with no failures. Details are in the Activity panel."
                    : invocation.ExitCode is { } code
                        ? $"The doctor reported problems (exit code {code}). Details are in the Activity panel."
                        : $"{invocation.FailureReason ?? "The doctor did not finish"}. See the Activity panel.");
        }
#pragma warning disable CA1031 // A diagnosis that cannot start (no CLI) is reported, not thrown into a key handler.
        catch (Exception ex)
        {
            ShowToast("Diagnose", $"Could not run the doctor: {ex.Message}");
        }
#pragma warning restore CA1031
        finally
        {
            _ = Interlocked.Exchange(ref _diagnosing, 0);
        }
    }

    /// <summary>Copy Last Command Output (Ctrl+Shift+Y): the newest Activity entry's output as plain text. False when there is none.</summary>
    public bool CopyLastOutput()
    {
        if (LastCommandOutput.Latest(_services.Cli.Activity) is not { } invocation || invocation.OutputLines.Count == 0)
        {
            ShowToast("Copy last output", "There is no command output to copy yet.");
            return false;
        }

        WriteClipboard(LastCommandOutput.Text(invocation));
        ShowToast("Copy last output", $"Copied the output of {invocation.CommandLine}.");
        return true;
    }

    /// <summary>Export Last Command Output (Ctrl+Shift+E): the newest entry (command, outcome, output) to a file the operator picks.</summary>
    public bool ExportLastOutput()
    {
        if (LastCommandOutput.Latest(_services.Cli.Activity) is not { } invocation)
        {
            ShowToast("Export last output", "There is no command output to export yet.");
            return false;
        }

        var path = SavePathPicker is { } pick ? pick(LastCommandOutput.SuggestFileName(invocation)) : PickSavePath(LastCommandOutput.SuggestFileName(invocation));
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            System.IO.File.WriteAllText(path, LastCommandOutput.ExportText(invocation), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ShowToast("Export last output", $"Exported to {path}.");
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowToast("Export last output", $"Could not write {path}: {ex.Message}");
            return false;
        }
    }

    private string? PickSavePath(string suggested)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export last command output",
            FileName = suggested,
            DefaultExt = ".log",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };

        var owner = _owner();
        var chosen = owner is { IsVisible: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return chosen == true ? dialog.FileName : null;
    }

    private void WriteClipboard(string text)
    {
        if (ClipboardWriter is { } write)
        {
            write(text);
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Another process holds the clipboard; say so rather than pretend it was copied.
            ShowToast("Clipboard", $"Could not use the clipboard: {ex.Message}");
        }
    }

    /// <summary>Copies a curated command as text that is safe to paste into PowerShell.</summary>
    public void CopyCurated(CuratedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        WriteClipboard(command.ClipboardText);
    }

    /// <summary>
    /// Runs a curated CLI command from the palette. The argv is the noun path the CLI's own help listed (checked again here for
    /// secret-carrying flags), run through the runner with no shell. Only a command on the explicit allow-list of known reads
    /// (<see cref="CommandTiers.UnreviewedReadPaths"/>) runs straight away; anything else - including a command the tier classifier calls
    /// read-only by its first verb, such as <c>plan apply</c>, or a verb a newer CLI added - is shown in the review first and runs only
    /// once confirmed. A command that cannot run without arguments is copied for the operator to complete instead.
    /// </summary>
    public async Task RunCuratedAsync(CuratedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CuratedCommandCatalog.Refuses(command.Argv))
        {
            ShowToast(command.Title, "Refused: this command would carry a secret or shell syntax.");
            return;
        }

        if (command.NeedsArguments)
        {
            CopyCurated(command);
            ShowToast(command.Title, $"Needs {string.Join(", ", command.RequiredArguments)}. Copied the command for you to complete in a terminal.");
            return;
        }

        if (Interlocked.Exchange(ref _curatedRunning, 1) == 1)
        {
            ShowToast(command.Title, "Another command from the palette is still running.");
            return;
        }

        try
        {
            if (!command.RunsWithoutReview)
            {
                var restarts = CommandReview.RestartsGatewayFor(command.Argv);

                // The classifier reads the first verb of the path, and calls "plan apply" a read; the review is not allowed to be lower than
                // a change, and says why a command that sounds harmless is being asked about.
                var notListed = CommandTiers.Classify(command.Argv) == CommandTier.ReadOnly
                    ? " It is not on DefenseClaw for Windows' list of commands known to be read-only, so it is reviewed first."
                    : string.Empty;
                var review = new CommandReview
                {
                    Title = $"Run {command.Title}?",
                    Summary = (command.Summary + notListed).Trim(),
                    Steps = new[] { new CommandReviewStep(command.Argv, floor: CommandReview.Stricter(command.Tier, CommandTier.StateChanging)) },
                    RestartsGateway = restarts,
                    Warnings = restarts ? new[] { CommandReviewWarning.GatewayRestart() } : Array.Empty<CommandReviewWarning>(),
                };

                var confirmed = Confirmer is { } confirm ? confirm(review) : GatewayActionDialog.Confirm(_owner(), review);
                if (!confirmed)
                {
                    return;
                }
            }

            var invocation = await _services.Cli.RunAsync(command.Argv).ConfigureAwait(true);
            ShowToast(
                command.Title,
                invocation.ExitCode == 0
                    ? "Finished. Output is in the Activity panel."
                    : invocation.ExitCode is { } code
                        ? $"Exit code {code}. See the Activity panel for output."
                        : $"{invocation.FailureReason ?? "The command did not finish"}. See the Activity panel.");
        }
#pragma warning disable CA1031 // A command that cannot start (no CLI, refused argument) is reported, not thrown into the palette.
        catch (Exception ex)
        {
            ShowToast(command.Title, $"Could not run it: {ex.Message}");
        }
#pragma warning restore CA1031
        finally
        {
            _ = Interlocked.Exchange(ref _curatedRunning, 0);
        }
    }

    /// <summary>Forgets which findings have been announced, so what is outstanding is announced once more (see <see cref="TrayIconService.ResetSeenAlertHistoryAsync"/>).</summary>
    public void ResetSeenAlertHistory() => _ = _tray.ResetSeenAlertHistoryAsync();
}
