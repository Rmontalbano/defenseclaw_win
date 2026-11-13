using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>What one read of the Cowork state came back with: the <c>cowork</c> object (null when <c>status --json</c> had none) and why the read failed, if it did.</summary>
internal sealed record CoworkStateRead(CoworkProtection? State, string? Problem);

/// <summary>
/// The Claude Cowork setup wizard (runtime 1.0.8, CUST-445, gap G8C-02). Cowork is protected only once the plugin <c>setup cowork</c> builds has been
/// uploaded in Claude Desktop by hand and a Cowork event has reached DefenseClaw, so this window never turns a clean exit of the command into "done":
/// the badge comes only from the CLI's own <c>cowork</c> state in <c>status --json</c> (<see cref="CoworkProtectionStore"/>), the upload steps are shown
/// with the zip's path from that state, and "Check again" reads it again.
/// <para>
/// The command (<see cref="CoworkProtectionFacts.SetupArgv"/>) runs only through <see cref="DiscoverActionReview"/>. Nothing else here changes anything:
/// copy path puts text on the clipboard and open folder starts Explorer; neither contacts the gateway or Claude.
/// </para>
/// </summary>
public sealed partial class CoworkSetupViewModel : ObservableObject
{
    /// <summary>Said when <c>status --json</c> has no <c>cowork</c> object (cowork.py: no state file and no Claude Desktop, or live health skipped).</summary>
    internal const string NoStateText = "status --json reported no Cowork state: Claude Desktop was not found for this account and no plugin was built.";

    /// <summary>
    /// Said when the CLI reports <c>cowork_plugin_stale</c> (cowork.py@1.0.9:685-692): the plugin was built by an older DefenseClaw. Every Windows plugin built
    /// by 1.0.8 reads this way on 1.0.9. Build again, then upload the new zip.
    /// </summary>
    internal const string PluginStaleText =
        "This plugin was built by an older DefenseClaw and uses an older Windows hook command, so Cowork is not protected. " +
        "Build the plugin again below, then upload the new zip in Claude Desktop (" + CoworkProtectionFacts.UploadPath + ").";

    /// <summary>Said before the run.</summary>
    internal const string BeforeRunText = "Builds the DefenseClaw plugin for Claude Cowork. Sets up Claude Code hooks first if they are off.";

    /// <summary>Said next to "Build the plugin again": each run is a new version (cmd_setup_cowork.py).</summary>
    internal const string RebuildNoteText =
        "Each run builds a new plugin version and clears the earlier confirmation, so Cowork reads as not protected until a Cowork event reaches the new one. " +
        "The old plugin keeps working until the new one is live; if Claude then lists two, remove the older one.";

    /// <summary>Said when the managed-host refusal's own text is not in the output.</summary>
    internal const string ManagedFallbackText =
        "This computer's DefenseClaw is managed by your organization, so per-user Cowork setup is disabled: Cowork is covered by the Claude Code machine policy your administrator deploys. Nothing was changed.";

    private readonly AppServices _services;
    private readonly CoworkProtectionStore _store;

    private CoworkProtection? _state;
    private string? _problem;
    private bool _loaded;
    private bool _buildFailed;
    private bool _justBuilt;
    private int _readSeq;

    public CoworkSetupViewModel(AppServices services, DiscoverActionReview review)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Review = review ?? throw new ArgumentNullException(nameof(review));
        _store = CoworkProtectionStore.For(services);
        ReadState = ReadFromStoreAsync;
        CopyText = CopyToClipboard;
        RevealFile = RevealInExplorer;
        Render();
    }

    /// <summary>The shared review the window shows over itself.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>Test seam: reads the state. The default is the store's read-only <c>status --json</c>.</summary>
    internal Func<CancellationToken, Task<CoworkStateRead>> ReadState { get; set; }

    /// <summary>Test seam: puts text on the clipboard.</summary>
    internal Action<string> CopyText { get; set; }

    /// <summary>Test seam: shows a file in Explorer (the zip), or its folder when it is not there.</summary>
    internal Action<string> RevealFile { get; set; }

    [ObservableProperty]
    private string _badgeText = "Checking…";

    [ObservableProperty]
    private string _badgeKey = "Neutral";

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _nextText = string.Empty;

    [ObservableProperty]
    private string _protectedText = string.Empty;

    [ObservableProperty]
    private string _buildText = "Build the plugin";

    [ObservableProperty]
    private string _rebuildNote = string.Empty;

    [ObservableProperty]
    private string? _blockedReason;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _resultKey = "Neutral";

    [ObservableProperty]
    private bool _showUpload;

    [ObservableProperty]
    private IReadOnlyList<string> _uploadSteps = Array.Empty<string>();

    [ObservableProperty]
    private string _zipPath = string.Empty;

    [ObservableProperty]
    private string _zipNote = string.Empty;

    /// <summary>What runs, before it runs.</summary>
    public string BeforeRun => BeforeRunText;

    /// <summary>Why the Claude Code settings matter to Cowork.</summary>
    public string SettingsNote => CoworkProtectionFacts.ClaudeCodeSettingsApply;

    public bool HasDetail => DetailText.Length > 0;

    public bool HasNext => NextText.Length > 0;

    public bool HasProtectedText => ProtectedText.Length > 0;

    public bool HasRebuildNote => RebuildNote.Length > 0;

    public bool HasResult => ResultText.Length > 0;

    public bool HasZipNote => ZipNote.Length > 0;

    public bool HasBlock => BlockedReason is not null;

    /// <summary>The build button is live: not managed, not read-only, and no review running.</summary>
    public bool CanBuild => BlockedReason is null && !Review.IsRunning;

    /// <summary>The state as last read; null before a read or when there was none.</summary>
    internal CoworkProtection? State => _state;

    partial void OnDetailTextChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    partial void OnNextTextChanged(string value) => OnPropertyChanged(nameof(HasNext));

    partial void OnProtectedTextChanged(string value) => OnPropertyChanged(nameof(HasProtectedText));

    partial void OnRebuildNoteChanged(string value) => OnPropertyChanged(nameof(HasRebuildNote));

    partial void OnResultTextChanged(string value) => OnPropertyChanged(nameof(HasResult));

    partial void OnZipNoteChanged(string value) => OnPropertyChanged(nameof(HasZipNote));

    partial void OnBlockedReasonChanged(string? value)
    {
        OnPropertyChanged(nameof(HasBlock));
        OnPropertyChanged(nameof(CanBuild));
        BuildPluginCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the state when the window opens.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) => ReadAsync(cancellationToken);

    /// <summary>Reads the state again ("Check again").</summary>
    [RelayCommand]
    private Task CheckAgainAsync() => ReadAsync(CancellationToken.None);

    /// <summary>Esc: closes the review when one is open. True when it was consumed.</summary>
    public bool HandleEscape() => Review.HandleEscape();

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var seq = Interlocked.Increment(ref _readSeq);
        IsChecking = true;
        Render();
        try
        {
            var read = await ReadState(cancellationToken).ConfigureAwait(true);
            if (seq != Volatile.Read(ref _readSeq))
            {
                return;
            }

            _state = read.State;
            _problem = read.Problem;
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsChecking = false;
        }

        if (_state is { Protected: true })
        {
            _buildFailed = false;
        }

        Render();
    }

    private async Task<CoworkStateRead> ReadFromStoreAsync(CancellationToken cancellationToken)
    {
        var state = await _store.RefreshAsync(cancellationToken).ConfigureAwait(true);
        return new CoworkStateRead(state, _store.Problem);
    }

    /// <summary>Draws every line from the state: the badge says protected only when the CLI said so.</summary>
    internal void Render()
    {
        var state = _state;

        if (IsChecking || !_loaded)
        {
            BadgeText = "Checking…";
            BadgeKey = "Neutral";
            DetailText = string.Empty;
            NextText = string.Empty;
        }
        else if (_problem is { Length: > 0 } problem)
        {
            BadgeText = "Unknown";
            BadgeKey = "Neutral";
            DetailText = $"The Cowork state could not be read: {problem}.";
            NextText = string.Empty;
        }
        else if (state is null)
        {
            if (_buildFailed)
            {
                BadgeText = "Not protected";
                BadgeKey = "Warn";
                DetailText = string.Empty;
            }
            else
            {
                BadgeText = "Unknown";
                BadgeKey = "Neutral";
                DetailText = NoStateText;
            }

            NextText = string.Empty;
        }
        else
        {
            BadgeText = state.Protected ? "Protected" : state.AwaitsUpload ? "Not protected yet" : state.IsPluginStale ? "Plugin out of date" : "Not protected";
            BadgeKey = state.Protected ? "Ok" : "Warn";
            DetailText = state.IsPluginStale ? (state.Detail + " " + PluginStaleText).Trim() : state.Detail;
            NextText = !state.Protected && state.Step.Length > 0 ? $"Next: {state.Step}" : string.Empty;
        }

        ProtectedText = state is { Protected: true } && _loaded && !IsChecking && _problem is null
            ? $"Cowork is protected. {state.Detail}"
            : string.Empty;

        BuildText = state?.PluginVersion is not null ? "Build the plugin again" : "Build the plugin";
        RebuildNote = state?.PluginVersion is not null ? RebuildNoteText : string.Empty;

        BlockedReason = BlockReason();
        OnPropertyChanged(nameof(CanBuild));
        BuildPluginCommand.NotifyCanExecuteChanged();

        RenderUpload();
    }

    private void RenderUpload()
    {
        var state = _state;
        var notProtected = state is not { Protected: true };
        // A stale plugin's old zip is not the one to upload: the steps come up once the plugin was built again (the next read then says it awaits upload).
        var show = _loaded && !IsChecking && _problem is null && notProtected &&
                   (state is not { IsPluginStale: true } || _justBuilt) &&
                   (state?.PluginZip is not null || _justBuilt || state is { AwaitsUpload: true });
        ShowUpload = show;
        if (!show)
        {
            return;
        }

        string zip;
        if (state?.PluginZip is { Length: > 0 } reported)
        {
            zip = reported;
            ZipNote = string.Empty;
        }
        else
        {
            zip = CoworkProtectionFacts.PluginZipPath(_services.Paths.DataDirectory);
            ZipNote = "status --json did not give the zip's path, so this is where setup cowork writes it by default.";
        }

        ZipPath = zip;
        UploadSteps = CoworkProtectionFacts.UploadSteps(zip);
    }

    private string? BlockReason()
    {
        if (_services.Installation.Context.IsManaged)
        {
            return "This installation is managed by your organization, so per-user Cowork setup is disabled (setup cowork exits " +
                   CoworkProtectionFacts.ManagedHostExitCode.ToString(CultureInfo.InvariantCulture) +
                   " here). Cowork is covered by the Claude Code machine policy your administrator deploys.";
        }

        return _services.Installation.BlockedReason;
    }

    /// <summary>Opens the review of <c>setup cowork</c>. Nothing runs until it is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanBuild))]
    private void BuildPlugin()
    {
        if (BlockReason() is not null)
        {
            Render();
            return;
        }

        var argv = CoworkProtectionFacts.SetupArgv;
        var again = _state?.PluginVersion is not null;
        var explanation = BeforeRunText + " It opens nothing and does not wait: upload the plugin in Claude Desktop afterwards." +
                          (again ? " " + RebuildNoteText : string.Empty);
        Review.Open(
            "Build the Cowork plugin?",
            explanation,
            new[]
            {
                new DiscoverStep(
                    argv,
                    "Build the Cowork plugin zip (and set up Claude Code first if it is off). Opens nothing and does not wait.",
                    CommandTier.StateChanging,
                    CliRunner.LongRunningTimeout),
            },
            onFinished: OnFinishedAsync,
            restartsGateway: CommandReview.RestartsGatewayFor(argv),
            primaryText: again ? "Build the plugin again" : "Build the plugin",
            extraWarnings: ReviewWarnings());
    }

    /// <summary>
    /// What the review says beyond the generic bars: when Claude Code is not a configured connector, <c>setup cowork --yes</c> turns it on first
    /// (cmd_setup_cowork.py:56-74, 230-234 at 1.0.8), adding Claude Code's hooks and restarting the gateway. A user of another agent only must read that before confirming.
    /// </summary>
    internal IReadOnlyList<CommandReviewWarning> ReviewWarnings() =>
        CoworkProtectionFacts.ClaudeCodeConfigured(_services.Config)
            ? Array.Empty<CommandReviewWarning>()
            : new[] { new CommandReviewWarning(CoworkProtectionFacts.ClaudeCodeToo, CoworkProtectionFacts.ClaudeCodeTooMessage) };

    /// <summary>What the run did. Exit 0 is never shown as success: the state is read again and the upload block comes up.</summary>
    internal async Task OnFinishedAsync(DiscoverReviewResult result)
    {
        var run = result.Invocations.LastOrDefault();
        var exit = run?.ExitCode;
        var lines = run is null
            ? Array.Empty<CliOutputLine>()
            : run.OutputLines.Where(static l => l.Stream != CliStream.Notice && l.Text.Trim().Length > 0).ToArray();

        if (exit == CoworkProtectionFacts.ManagedHostExitCode)
        {
            _buildFailed = true;
            _justBuilt = false;
            ResultKey = "Warn";
            ResultText = ManagedMessage(lines);
            Render();
            return;
        }

        if (exit != 0 || run?.FailureReason is { Length: > 0 })
        {
            _buildFailed = true;
            _justBuilt = false;
            ResultKey = "Bad";
            ResultText = FailureText(run, exit, lines);
            Render();
            return;
        }

        _buildFailed = false;
        _justBuilt = true;
        ResultKey = "Warn";
        ResultText = "setup cowork finished and built the plugin. Cowork is not protected until you upload it in Claude Desktop and a Cowork event reaches DefenseClaw.";

        // The review's own "Done." speaks of the command, not of Cowork: close it so the window's state and upload steps are what the user reads.
        if (Review.DismissCommand.CanExecute(null))
        {
            Review.DismissCommand.Execute(null);
        }

        await ReadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private static string ManagedMessage(IReadOnlyList<CliOutputLine> lines)
    {
        var text = lines.Select(static l => l.Text.Trim())
            .FirstOrDefault(static t => t.Contains("managed by your organization", StringComparison.OrdinalIgnoreCase));
        return text ?? ManagedFallbackText;
    }

    private static string FailureText(CliInvocation? run, int? exit, IReadOnlyList<CliOutputLine> lines)
    {
        if (run?.FailureReason is { Length: > 0 } reason)
        {
            return $"setup cowork did not finish: {reason}";
        }

        var error = lines.Where(static l => l.Stream == CliStream.StandardError).Select(static l => l.Text.Trim()).TakeLast(3).ToList();
        if (error.Count == 0)
        {
            error = lines.Select(static l => l.Text.Trim()).TakeLast(3).ToList();
        }

        var code = exit?.ToString(CultureInfo.InvariantCulture) ?? "?";
        return error.Count == 0
            ? $"setup cowork failed (exit {code})."
            : $"setup cowork failed (exit {code}): {string.Join(" ", error)}";
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (ZipPath.Length > 0)
        {
            CopyText(ZipPath);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (ZipPath.Length > 0)
        {
            RevealFile(ZipPath);
        }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is held by another program: nothing to do, the path is on screen.
        }
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
            {
                start.ArgumentList.Add("/select,");
                start.ArgumentList.Add(full);
            }
            else if (Path.GetDirectoryName(full) is { Length: > 0 } folder && Directory.Exists(folder))
            {
                start.ArgumentList.Add(folder);
            }
            else
            {
                return;
            }

            _ = ChildEnvironment.StripSecrets(start);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Explorer could not start: the path is on screen.
        }
    }
}
