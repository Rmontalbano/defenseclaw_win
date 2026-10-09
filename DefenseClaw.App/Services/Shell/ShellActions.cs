using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;

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

    /// <summary>Test seam: runs the gateway's start, stop or restart a registry row stands for (default: the tray's, which reviews and toasts).</summary>
    internal Func<GatewayAction, Task>? GatewayActionRunner { get; set; }

    /// <summary>
    /// The CLI commands the palette offers for the runtime the app is connected to: that runtime's TUI command registry, minus what it
    /// does not run on Windows. Chosen from what the runtime has reported about itself (<see cref="CuratedCommandCatalog.For"/>), so it
    /// is the installed 0.8.10's until a runtime shows it has the larger registry, and it changes when the answer does. Nothing is run to
    /// build it.
    /// </summary>
    public CuratedCommandCatalog CliCatalogue => CuratedCommandCatalog.For(_services.Runtime.Capabilities);

    /// <summary>
    /// What <see cref="CliCatalogue"/> puts in the palette: empty while DefenseClaw is not installed on this machine (there is no CLI to
    /// run them with, so the palette lists none, as it never listed a CLI it could not read), the catalogue's commands otherwise.
    /// </summary>
    public IReadOnlyList<CuratedCommand> CliCommands =>
        OffersCliCommands(Snapshot) ? CliCatalogue.Commands : Array.Empty<CuratedCommand>();

    /// <summary>The note and tooltip for the registry entries Windows does not run - empty when no commands are listed or none is hidden.</summary>
    public (string Note, string Detail) HiddenCommands =>
        OffersCliCommands(Snapshot) ? (CliCatalogue.HiddenNote, CliCatalogue.HiddenDetail) : (string.Empty, string.Empty);

    /// <summary>
    /// True unless DefenseClaw is known not to be installed (still checking is not "not installed": the commands are there to see as the
    /// window opens, and a run that finds no CLI says so).
    /// </summary>
    internal static bool OffersCliCommands(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Install != InstallState.NotInstalled;
    }

    /// <summary>
    /// The shared Docker look the gated palette rows (<c>setup local-observability up</c>, ...) apply
    /// (<see cref="ShellCommandRegistry.BuildCliCommands"/>): the same one the Setup hub's card uses.
    /// </summary>
    internal LocalStackAvailability LocalStack => _services.LocalStack;

    /// <summary>
    /// Makes sure the answer the gated rows apply is not stale, when the palette lists a row that needs it (the registry's
    /// <c>setup local-observability</c> rows, which are listed unless DefenseClaw is not installed): a palette that has no such row never
    /// starts a Docker look, and one that has asks at most once per freshness window
    /// (<see cref="LocalStackAvailability.FreshWhenAvailable"/> / <see cref="LocalStackAvailability.FreshWhenNot"/>). Called when the palette
    /// opens; returns the look so a test can wait for it.
    /// </summary>
    public Task CheckLocalStack() =>
        CliCommands.Any(c => WizardWindowsPolicy.CommandNeedsDocker(c.Argv))
            ? LocalStack.EnsureFreshAsync()
            : Task.CompletedTask;

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

        // Say so when another process holds the clipboard, rather than pretend it was copied.
        switch (Views.Controls.DcClipboard.TryCopy(text, report: false))
        {
            case Views.Controls.ClipboardResult.Failed:
                ShowToast("Clipboard", Views.Controls.DcClipboard.FailureText);
                break;
            case Views.Controls.ClipboardResult.Truncated:
                ShowToast("Clipboard", Views.Controls.DcClipboard.TruncatedText);
                break;
        }
    }

    /// <summary>Copies a curated command as text that is safe to paste into PowerShell - with the value the operator typed for it, when there is one.</summary>
    public void CopyCurated(CuratedCommand command, string? argument = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        WriteClipboard(argument is null ? command.ClipboardText : command.ClipboardTextWith(argument));
    }

    /// <summary>
    /// Runs a curated CLI command from the palette: an entry of the connected runtime's TUI registry, as the argv the TUI itself runs
    /// (checked again here against the reviewed options), run through the runner with no shell. Only a command on the explicit allow-list
    /// of known reads (<see cref="CommandTiers.UnreviewedReadPaths"/>, or the gateway's two) runs straight away; anything else - including a
    /// command the tier classifier calls read-only by its first verb, such as <c>plan apply</c>, or a verb a newer CLI added - is shown in the
    /// review first and runs only once confirmed.
    /// <para>
    /// Three kinds of entry are not simply run. The gateway's <c>start</c>, <c>stop</c> and <c>restart</c> go the way the tray's and the
    /// palette's Gateway rows go (<see cref="RunGatewayActionAsync"/>). A command that has to be answered at a prompt is copied for a
    /// terminal. A command that needs a value is run with the one <paramref name="argument"/> its form took, added after <c>--</c> (a name that
    /// the CLI would rewrite - <c>a*</c>, <c>%X%</c>, a leading <c>~</c> - is refused before any review), or copied for the operator to complete
    /// when it needs more than a form takes.
    /// </para>
    /// </summary>
    /// <param name="command">The row to run.</param>
    /// <param name="argument">The value typed for a command that takes one; null for every other.</param>
    public async Task RunCuratedAsync(CuratedCommand command, string? argument = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (CuratedCommandCatalog.Refuses(command.Argv))
        {
            ShowToast(command.Title, "Refused: this command would carry a secret or shell syntax.");
            return;
        }

        if (command.LifecycleAction is { } lifecycle)
        {
            await (GatewayActionRunner is { } runLifecycle ? runLifecycle(lifecycle) : RunGatewayActionAsync(lifecycle)).ConfigureAwait(true);
            return;
        }

        if (command.NeedsTerminal)
        {
            CopyCurated(command);
            ShowToast(
                command.Title,
                "It asks questions at a prompt, which needs a terminal. Copied the command for you to paste into one" +
                (command.NeedsArguments ? $", then add {string.Join(", ", command.RequiredArguments)}." : "."));
            return;
        }

        var argv = command.Argv;
        if (command.NeedsArguments)
        {
            if (command.Form is not { } form || argument is null)
            {
                CopyCurated(command);
                ShowToast(command.Title, $"Needs {string.Join(", ", command.RequiredArguments)}. Copied the command for you to complete in a terminal.");
                return;
            }

            if (form.Check(argument, out var value) is { } problem)
            {
                ShowToast(command.Title, problem);
                return;
            }

            argv = command.ArgvWith(value);
            if (ArgvHazards.AppliesTo(command.Executable) &&
                ArgvHazards.FindChangedTargets(argv, CliWorkingDirectory.DefaultPath) is { Count: > 0 } changes)
            {
                ShowToast(command.Title, ArgumentExpansionException.BuildMessage(changes));
                return;
            }
        }

        if (Interlocked.Exchange(ref _curatedRunning, 1) == 1)
        {
            ShowToast(command.Title, "Another command from the palette is still running.");
            return;
        }

        try
        {
            // Judged on the argv that will run: a command with a value on it is never one of the listed bare reads.
            if (!CommandReview.MayRunUnreviewed(command.Executable, argv))
            {
                // The setup and guardrail verbs restart the gateway by rule (the local stack's, by what each verb really rewrites: see
                // LocalStackReview); a registry description that says the command restarts it (agent discovery enable ... "save config,
                // restart, and scan") is the TUI's own word for the same consequence.
                var restarts = ArgvHazards.AppliesTo(command.Executable) &&
                               (CommandReview.RestartsGatewayFor(argv) || command.Summary.Contains("restart", StringComparison.OrdinalIgnoreCase));

                // The classifier reads the first verb of the path, and calls "plan apply" a read; the review is not allowed to be lower than
                // a change, and says why a command that sounds harmless is being asked about.
                var notListed = CommandReview.ResolveTier(argv) == CommandTier.ReadOnly
                    ? command.NeedsArguments
                        ? " It names something you typed, so it is reviewed first, even where the command alone is on DefenseClaw for Windows' list of read-only commands."
                        : " It is not on DefenseClaw for Windows' list of commands known to be read-only, so it is reviewed first."
                    : string.Empty;

                // The local stack's rows say what the verb really does (the same sentences the Setup wizard's review uses): the registry's one-line
                // description of the bare `setup local-observability` row is "Show local observability commands", and run bare it starts the stack.
                var summary = LocalStackReview.Summary(argv) is { Length: > 0 } stack ? stack : command.Summary;
                var review = new CommandReview
                {
                    Title = $"Run {CommandReview.CommandLine(command.Executable, argv)}?",
                    Summary = (summary + notListed).Trim(),
                    Steps = new[] { new CommandReviewStep(argv, floor: CommandReview.Stricter(command.Tier, CommandTier.StateChanging), executable: command.Executable) },
                    RestartsGateway = restarts,
                    Warnings = (restarts ? new[] { CommandReviewWarning.GatewayRestart() } : Array.Empty<CommandReviewWarning>())
                        .Concat(LocalStackReview.Warnings(argv, LocalStack.Status))
                        .ToArray(),
                };

                var confirmed = Confirmer is { } confirm ? confirm(review) : GatewayActionDialog.Confirm(_owner(), review);
                if (!confirmed)
                {
                    return;
                }
            }

            // A value typed here is a target like the Govern panels': the runner refuses to run it if the CLI would rewrite it after the review.
            var options = command.NeedsArguments ? ExactTargets : null;
            var invocation = string.Equals(command.Executable, GatewayControl.Executable, StringComparison.Ordinal)
                ? await _services.Cli.RunGatewayAsync(argv, options: options).ConfigureAwait(true)
                : await _services.Cli.RunAsync(argv, options: options).ConfigureAwait(true);
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

    private static readonly CliRunOptions ExactTargets = new() { RefuseExpandingTargets = true };

    /// <summary>Forgets which findings have been announced, so what is outstanding is announced once more (see <see cref="TrayIconService.ResetSeenAlertHistoryAsync"/>).</summary>
    public void ResetSeenAlertHistory() => _ = _tray.ResetSeenAlertHistoryAsync();
}
