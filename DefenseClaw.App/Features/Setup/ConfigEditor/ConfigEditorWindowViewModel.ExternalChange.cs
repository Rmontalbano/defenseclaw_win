using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// The config editor's reaction to config.yaml moving under an open draft, and to a config.yaml it cannot load (CUST-377; the Mac app's draft survives an external
/// change the same way, defenseclaw_mac v1.1.31-v1.1.34).
/// <para>
/// <b>An open draft survives.</b> <see cref="HandleDiskChangeAsync"/> (the existing stale-file rule: signature against <c>_loadedSignature</c>, once per version of the file,
/// never a silent reload of a dirty buffer) raises the "changed on disk" notice; this adds what the operator can do about it besides Reload and Keep: <b>Review the
/// difference</b> (what changed on disk, and what <see cref="ConfigDraftRebase"/> would do with the draft), <b>Rebase my edits</b> (the file as it is now with the draft's
/// keys on it; a key changed in both places keeps the file's value and is listed) and <b>Undo rebase</b> (the draft exactly as it was). Nothing is applied without a click
/// and nothing is lost: the pre-rebase draft is kept until the next edit that makes it moot.
/// </para>
/// <para>
/// A rebase moves the same two things a load does - <c>_loadedSignature</c> and <c>_diskText</c> - so the save-time drift check, the modified flag and the review diff
/// keep their one meaning; it is a load that keeps the draft on top.
/// </para>
/// </summary>
public sealed partial class ConfigEditorWindowViewModel
{
    /// <summary>The runtime's gateway reloads config.yaml in place (<see cref="RuntimeCapability.GatewayConfigReload"/>): a save needs a restart only for the keys it cannot apply live.</summary>
    internal bool GatewayHotReloads => _services.Runtime.Capabilities.Has(RuntimeCapability.GatewayConfigReload);

    private bool HasConfig104Keys => _services.Runtime.Capabilities.Has(RuntimeCapability.Config104Keys);

    // ------------------------------------------------------------------ hot reload

    /// <summary>
    /// After a saved change that queued no restart: when the gateway applies every changed key in place, say so and drop the restart bar (Save alone was
    /// enough). A runtime that does not hot-reload, or a change that could not be compared, keeps the bar.
    /// </summary>
    private void ApplyHotReloadOutcome(string before, string after)
    {
        if (!GatewayHotReloads || !ConfigReloadRules.Classify(before, after).HotOnly)
        {
            return;
        }

        ShowRestartPrompt = false;
        SaveResultMessage = SaveResultMessage.TrimEnd() + " " + HotReloadSentence;
    }

    /// <summary>What the success banner adds when every changed key is applied by the running gateway itself.</summary>
    internal const string HotReloadSentence = "The running gateway applies these settings on its own config reload; no restart is needed.";

    // ------------------------------------------------------------------ an unloadable config.yaml

    /// <summary>Why config.yaml on disk could not be loaded (unreadable, not YAML, refused), with the line and column when the parser gave them; null while it loads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadProblem))]
    [NotifyPropertyChangedFor(nameof(ShowParseErrorBar))]
    private ConfigLoadProblem? _loadProblem;

    /// <summary>One line after a "Open in editor" / "Show in Explorer" that could not be done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadProblemActionMessage))]
    private string? _loadProblemActionMessage;

    public bool HasLoadProblemActionMessage => !string.IsNullOrEmpty(LoadProblemActionMessage);

    public bool HasLoadProblem => LoadProblem is not null;

    /// <summary>The generic "does not parse" bar is for a draft the operator typed; a file that does not load has the card above it.</summary>
    public bool ShowParseErrorBar => ParseError is not null && LoadProblem is null;

    /// <summary>
    /// Opens config.yaml with the program Windows has for it. A seam: the window leaves the default, a test supplies its own. False when nothing could open it (the
    /// message says so).
    /// </summary>
    internal Func<string, bool> OpenFileRequest { get; set; } = OpenWithShell;

    /// <summary>Shows a path in Explorer (selected, for a file); the app's one Explorer launcher. A seam for tests.</summary>
    internal Func<string, bool, bool> RevealRequest { get; set; } = SettingsPlatform.Real.Reveal;

    [RelayCommand]
    private void OpenConfigInEditor()
    {
        LoadProblemActionMessage = null;
        var path = _paths.ConfigFilePath;
        if (!File.Exists(path))
        {
            LoadProblemActionMessage = "config.yaml does not exist, so there is nothing to open.";
            return;
        }

        if (!OpenFileRequest(path))
        {
            LoadProblemActionMessage = "Windows has no program set to open config.yaml. Use Show in Explorer and open it with the editor you like.";
        }
    }

    [RelayCommand]
    private void ShowConfigInExplorer()
    {
        LoadProblemActionMessage = null;
        var path = _paths.ConfigFilePath;
        var isFile = File.Exists(path);
        var target = isFile ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(target) || (!isFile && !Directory.Exists(target)))
        {
            LoadProblemActionMessage = "The folder that holds config.yaml does not exist.";
            return;
        }

        if (!RevealRequest(target, isFile))
        {
            LoadProblemActionMessage = "Explorer could not be started.";
        }
    }

    private static bool OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ notes (1.0.4 keys)

    /// <summary>What the runtime does with this file that a field cannot say (Splunk local stack, strictest profile): empty for a runtime without the 1.0.4 keys.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormNotes))]
    private IReadOnlyList<string> _formNotes = Array.Empty<string>();

    public bool HasFormNotes => FormNotes.Count > 0;

    private void RefreshFormNotes() => FormNotes = ConfigRuntimeNotes.For(RawText, HasConfig104Keys);

    // ------------------------------------------------------------------ review and rebase of a draft over a changed file

    /// <summary>True while the review of the external change is on screen over the editor.</summary>
    [ObservableProperty]
    private bool _isReviewingExternalChange;

    /// <summary>The masked diff of what changed on disk since the editor last read the file; null when the file could not be read.</summary>
    [ObservableProperty]
    private ConfigDiffReview? _externalChangeDiff;

    /// <summary>One sentence: what moved on disk, what was edited here, how many keys both touched.</summary>
    [ObservableProperty]
    private string _externalChangeSummary = string.Empty;

    /// <summary>Why the draft cannot be rebased right now (the file does not load, the draft is not YAML, a field has an error, the lines cannot be combined); null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRebaseDraft))]
    private string? _rebaseBlockedReason;

    /// <summary>The keys changed in both places, masked, with the value that wins (the file's).</summary>
    public ObservableCollection<RebaseConflict> RebaseConflicts { get; } = new();

    public bool HasRebaseConflicts => RebaseConflicts.Count > 0;

    /// <summary>The review found a rebase that can be applied.</summary>
    public bool CanRebaseDraft => IsReviewingExternalChange && RebaseBlockedReason is null && _externalPlanReady;

    private bool _externalPlanReady;
    private bool _rebasing;

    /// <summary>What the pre-rebase draft stands on, so Undo can put the draft back as it was.</summary>
    private sealed record PreRebase(string Draft);

    private PreRebase? _preRebase;

    /// <summary>The text the last rebase put in the buffer: any other text means the draft moved on, and Undo would throw that away.</summary>
    private string? _rebasedText;

    /// <summary>Called from the RAW-changed hook: an edit after a rebase ends Undo (it would drop that edit), and leaves the notice.</summary>
    private void VoidUndoOnFurtherEdit(string value)
    {
        if (_preRebase is not null && !string.Equals(value, _rebasedText, StringComparison.Ordinal))
        {
            _preRebase = null;
            OnPropertyChanged(nameof(CanUndoRebase));
        }
    }

    /// <summary>After a rebase: what it did, one sentence, plus the conflicts. Cleared by the next edit, by Undo, by Dismiss and by a load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRebaseNotice))]
    private string? _rebaseNotice;

    public bool HasRebaseNotice => RebaseNotice is not null;

    public bool CanUndoRebase => _preRebase is not null;

    partial void OnIsReviewingExternalChangeChanged(bool value) => OnPropertyChanged(nameof(CanRebaseDraft));

    /// <summary>
    /// The notice's "Review the difference": reads config.yaml as it is now and shows what changed on disk since the editor read it, and what a rebase of the draft would
    /// do. Changes nothing; "Back to editing" returns to the draft untouched.
    /// </summary>
    [RelayCommand]
    private async Task ReviewExternalChangeAsync()
    {
        if (IsReviewingExternalChange || IsLoading || IsSaving || _resolvingUnsaved || _rebasing)
        {
            return;
        }

        CommitPendingEdits?.Invoke();

        var read = await ReadDiskForRebaseAsync().ConfigureAwait(true);
        RebaseConflicts.Clear();
        _externalPlanReady = false;

        if (read.Text is null)
        {
            ExternalChangeDiff = null;
            ExternalChangeSummary = "config.yaml on disk could not be read.";
            RebaseBlockedReason = read.Failure;
        }
        else
        {
            ExternalChangeDiff = ConfigDiffReviewBuilder.Build(_diskText, read.Text);
            var plan = ConfigDraftRebase.Plan(_diskText, RawText, read.Text);
            ExternalChangeSummary = plan.Success ? plan.Summary : ExternalChangeDiff.Summary;
            foreach (var conflict in plan.Conflicts)
            {
                RebaseConflicts.Add(conflict);
            }

            _externalPlanReady = plan.Success;
            RebaseBlockedReason = plan.Success
                ? (HasBlockingFieldErrors ? FieldErrorBlocksRebase : null)
                : plan.Failure;
        }

        OnPropertyChanged(nameof(HasRebaseConflicts));
        IsReviewingExternalChange = true;
        OnPropertyChanged(nameof(CanRebaseDraft));
    }

    private const string FieldErrorBlocksRebase =
        "A field in FORM holds a value with an error that is not in your draft yet. Fix it or put the old value back first: a rebase would drop it.";

    /// <summary>"Back to editing" on the review (also Esc).</summary>
    [RelayCommand]
    private void CancelExternalReview() => CloseExternalReview();

    private void CloseExternalReview()
    {
        IsReviewingExternalChange = false;
        ExternalChangeDiff = null;
        RebaseConflicts.Clear();
        OnPropertyChanged(nameof(HasRebaseConflicts));
        _externalPlanReady = false;
    }

    /// <summary>
    /// "Rebase my edits": the file as it is now, with the draft's changed keys on top. Reads the file again (it may have moved since the review), refuses when it cannot be
    /// combined safely, and otherwise moves the editor's baseline to the new file - the same two fields a load moves - and puts the combined text in the buffer as an unsaved
    /// edit. A key changed in both places keeps the file's value and is listed; Undo puts the draft back as it was.
    /// </summary>
    [RelayCommand]
    private async Task RebaseDraftAsync()
    {
        if (IsLoading || IsSaving || _resolvingUnsaved || _rebasing)
        {
            return;
        }

        _rebasing = true;
        try
        {
            CommitPendingEdits?.Invoke();

            // The held-back value of a FORM field with an error is in no text; the rebuild below would drop it.
            if (HasBlockingFieldErrors)
            {
                RebaseBlockedReason = FieldErrorBlocksRebase;
                return;
            }

            var read = await ReadDiskForRebaseAsync().ConfigureAwait(true);
            if (read.Text is null)
            {
                RebaseBlockedReason = read.Failure;
                return;
            }

            // Typing that landed while the file was being read is part of the draft: plan against what the buffer holds now.
            var draft = RawText;
            var plan = ConfigDraftRebase.Plan(_diskText, draft, read.Text);
            if (!plan.Success)
            {
                RebaseBlockedReason = plan.Failure;
                return;
            }

            _preRebase = new PreRebase(draft);
            _rebasedText = plan.Text;

            // The baseline moves to the file just read (signature first, as a load does: a change after it is reported as drift by the next Save).
            _loadedSignature = read.Signature;
            _diskText = read.Text;
            if (_unvalidatedSignature is { } unvalidated && unvalidated != _loadedSignature)
            {
                UnvalidatedSignature = null;
            }

            SetRawTextWithoutRebuildFlag(plan.Text);
            IsRawModified = !string.Equals(RawText, _diskText, StringComparison.Ordinal);
            HasSecretReferences = SensitiveKeyClassifier.ContainsSensitiveReferences(plan.Text);

            ClearExternalChange();
            ShowDriftBanner = false;
            ShowSaveResultBanner = false;
            ShowOnDiskPreview = false;
            ShowRestartPrompt = false;
            CloseExternalReview();
            RebaseBlockedReason = null;

            try
            {
                _document = ConfigStore.Parse(plan.Text, _paths.ConfigFilePath);
                ParseError = null;
                LoadProblem = null;
                NeedsFormRebuild = true;
            }
            catch (ConfigParseException ex)
            {
                ParseError = ex.Message;
                NeedsFormRebuild = true;
            }

            // FORM is built from the CLI's masked view of the file, which now describes the new file.
            await FetchFormSourceAsync(CancellationToken.None).ConfigureAwait(true);
            NotifyFormTabSelected();
            RefreshFormNotes();

            RebaseNotice = DescribeRebase(plan);
            OnPropertyChanged(nameof(CanUndoRebase));
        }
        finally
        {
            _rebasing = false;
        }
    }

    private static string DescribeRebase(RebasePlan plan)
    {
        var keys = $"{plan.YourKeys.Count:N0} edited setting{(plan.YourKeys.Count == 1 ? string.Empty : "s")} kept on top of the new file";
        if (plan.Conflicts.Count == 0)
        {
            return $"Rebased: {keys}. Nothing is saved yet.";
        }

        var list = string.Join("; ", plan.Conflicts.Take(5).Select(static c => $"{c.Path} (yours: {c.Yours}, file: {c.OnDisk})"));
        var more = plan.Conflicts.Count > 5 ? $"; and {plan.Conflicts.Count - 5:N0} more" : string.Empty;
        return $"Rebased: {keys}. {plan.Conflicts.Count:N0} setting{(plan.Conflicts.Count == 1 ? " was" : "s were")} changed in both places and kept the file's value: {list}{more}. Nothing is saved yet.";
    }

    /// <summary>"Undo rebase": the draft exactly as it was before the rebase. The baseline stays the new file, so the review before a save shows the difference.</summary>
    [RelayCommand]
    private void UndoRebase()
    {
        if (_preRebase is not { } before || IsSaving)
        {
            return;
        }

        _preRebase = null;
        SetRawTextWithoutRebuildFlag(before.Draft);
        IsRawModified = !string.Equals(RawText, _diskText, StringComparison.Ordinal);
        HasSecretReferences = SensitiveKeyClassifier.ContainsSensitiveReferences(before.Draft);
        RebaseNotice = null;
        OnPropertyChanged(nameof(CanUndoRebase));

        try
        {
            _document = ConfigStore.Parse(before.Draft, _paths.ConfigFilePath);
            ParseError = null;
        }
        catch (ConfigParseException ex)
        {
            ParseError = ex.Message;
        }

        NeedsFormRebuild = true;
        NotifyFormTabSelected();
        RefreshFormNotes();
    }

    [RelayCommand]
    private void DismissRebaseNotice()
    {
        RebaseNotice = null;
        _preRebase = null;
        OnPropertyChanged(nameof(CanUndoRebase));
    }

    private void ClearRebaseState()
    {
        _preRebase = null;
        RebaseNotice = null;
        CloseExternalReview();
    }

    /// <summary>The file as it is now: its text and the signature taken before the read, or why it could not be read.</summary>
    private async Task<(string? Text, FileSignature Signature, string? Failure)> ReadDiskForRebaseAsync()
    {
        var signature = _saveService.CaptureSignature();
        try
        {
            var text = File.Exists(_paths.ConfigFilePath)
                ? await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(_paths.ConfigFilePath, CancellationToken.None, DefenseClaw.Core.IO.ReadLimits.ConfigYamlBytes).ConfigureAwait(true)
                : string.Empty;
            return (text, signature, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, signature, ConfigLoadProblem.FromRead(ex).Reason);
        }
    }
}
