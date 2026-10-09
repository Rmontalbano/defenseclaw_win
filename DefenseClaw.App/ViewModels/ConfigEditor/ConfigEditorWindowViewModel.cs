using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// Orchestrates the whole config editor window: loads config.yaml and the CLI's masked
/// source view of it (<c>config show --source</c>) once, keeps RAW and FORM in sync through
/// the raw text as the single source of truth, and drives the save pipeline.
/// <para>
/// <b>Why RAW text is the source of truth.</b> A FORM edit never mutates a parallel typed
/// model — it calls <see cref="YamlSectionEditor"/> to patch the relevant section of the
/// *current* raw text, then republishes the result as <see cref="RawText"/>. The RAW
/// editor and the FORM tab are therefore always looking at the same string; the FORM tree
/// just doesn't get rebuilt from it until the FORM tab is next shown (see
/// <see cref="NotifyFormTabSelected"/>), so typing in RAW never fights a live FORM
/// rebuild for focus.
/// </para>
/// <para>
/// <b>The stale-FORM rule.</b> <c>_needsFormRebuild</c> means "RAW has changed since FORM
/// last absorbed it". It is cleared only by a successful parse + rebuild (or by a FORM
/// edit, which publishes into RAW itself) — never by a failed parse. While it is set, FORM
/// edits are <i>refused</i> with an explanation: a FORM edit patches the parsed document's
/// text, and applying one on top of unparsed RAW edits would overwrite them.
/// </para>
/// <para>
/// <b>Load failures.</b> If config.yaml exists but cannot be read, <see cref="LoadError"/>
/// is set and stays set until a later load succeeds: Save is disabled (an empty editor
/// must never be written over a file we merely failed to read) and FORM is unavailable.
/// That is distinct from <see cref="ParseError"/>, which is about the RAW text not being
/// valid YAML.
/// </para>
/// <para>
/// <b>Backups.</b> The Restore action always points at the last-known-<i>good</i> backup:
/// once a save has written a file that failed validation, later saves (which back up that
/// unvalidated file) do not replace the offered backup until a save validates.
/// </para>
/// <para>
/// <b>Unsaved edits are never dropped silently.</b> "Modified" means the RAW text differs from the
/// text last read from, or written to, config.yaml (<c>_diskText</c>) — FORM edits publish into RAW, so
/// one flag covers both tabs — and a save that the CLI rejected stays modified until the next load or
/// edit. Everything that would replace the buffer (the Reload button, Restore, and the window closing
/// or the app exiting, see <see cref="ConfirmCloseAsync"/>) first goes through
/// <see cref="ResolveUnsavedChangesAsync"/>: Save (which must succeed *and* validate before the caller
/// carries on), Discard, or Cancel. The prompt is a seam (<see cref="UnsavedChangesPrompt"/>) and an
/// unanswered one means Cancel, so nothing here can discard by default. A change to config.yaml made
/// behind the editor's back (<see cref="HandleDiskChangeAsync"/>) reloads a clean buffer and, for a dirty
/// one, only raises an inline notice — an automatic reload never overwrites edits.
/// </para>
/// <para>
/// <b>Masked values never come back.</b> The CLI's source view masks secrets, header values and
/// parts of URLs. FORM builds those fields read-only (<see cref="ConfigFormBuilder"/>), a field
/// that is not editable never commits (<see cref="FormField"/>), this view-model refuses a
/// commit whose value is a mask placeholder or that belongs to a secret/masked field, and
/// <see cref="PublishPatchedSection"/> refuses any patch that would leave more mask
/// placeholders in the document than it had before. Four independent guards, one invariant:
/// FORM never writes a masked value into config.yaml.
/// </para>
/// </summary>
public sealed partial class ConfigEditorWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Argv for the FORM source. <c>--source</c> answers with every section of a v8 file (the
    /// <c>--effective</c> view answers with <c>observability</c> only on 0.8.10). Never
    /// <c>--reveal</c>: this app does not ask the CLI for secret values.
    /// </summary>
    internal static readonly string[] SourceArgv = { "config", "show", "--source", "--format", "yaml" };

    /// <summary>Fallback for CLIs old enough not to know <c>--source</c>: same masking, effective view.</summary>
    internal static readonly string[] EffectiveArgv = { "config", "show", "--effective", "--format", "yaml" };

    /// <summary>
    /// Test seam. When set, the masked source view is read from this delegate instead of by running
    /// <c>defenseclaw config show --source</c>, so a test can drive FORM without the CLI. Null in
    /// production, which leaves <see cref="ReadFormSourceAsync"/> exactly as it was.
    /// </summary>
    internal Func<CancellationToken, Task<string>>? FormSourceOverride { get; set; }

    /// <summary>
    /// How the editor asks "save, discard or cancel?". Set by the window (a dialog) and by tests (a canned
    /// answer). Left null — no UI attached — the answer is <see cref="UnsavedChangesChoice.Cancel"/>, so a
    /// view-model nobody wired a prompt to can refuse to drop edits but can never drop them.
    /// </summary>
    internal Func<UnsavedChangesRequest, Task<UnsavedChangesChoice>>? UnsavedChangesPrompt { get; set; }

    /// <summary>
    /// Commits an edit that is still inside a FORM control. FORM boxes commit on <c>LostFocus</c>, so a value that
    /// has been typed but not tabbed out of has not reached this view-model — and would be lost by a close or a
    /// reload that only looked at <see cref="HasUnsavedChanges"/>. The window sets it (it moves focus off the
    /// FORM tab and back); everything that asks "is there anything to lose?" calls it first.
    /// </summary>
    internal Action? CommitPendingEdits { get; set; }

    private readonly AppServices _services;
    private readonly DefenseClawPaths _paths;
    private readonly CliRunner _cli;
    private readonly ConfigSaveService _saveService;

    private FileSignature _loadedSignature;

    /// <summary>
    /// Signature of the config.yaml that our own last save wrote and the CLI then rejected
    /// (or could not validate). While the file on disk still has this signature, Restore is
    /// offered and a backup of "the current file" is a backup of an unvalidated file.
    /// </summary>
    private FileSignature? _unvalidatedSignature;

    private ConfigDocument _document;

    /// <summary>The CLI's masked view of config.yaml as of the last load / successful save. Empty when it could not be fetched.</summary>
    private string _formSourceYaml = string.Empty;
    private bool _suppressRawChangeTracking;
    private bool _needsFormRebuild;

    /// <summary>
    /// What config.yaml held when the editor last read it, or what the editor last wrote to it (whether or not the
    /// CLI then accepted it). <see cref="IsRawModified"/> is "RAW differs from this" — so typing something and
    /// deleting it again is not a change, and a FORM edit that lands in RAW is.
    /// </summary>
    private string _diskText = string.Empty;

    /// <summary>The save in flight, if any: a close or reload that arrives during it waits for the outcome instead of guessing.</summary>
    private Task<SaveOutcome?>? _activeSave;

    /// <summary>True while a save/discard/cancel question is open or being acted on; a second trigger (a second X click, the tray Exit) is refused rather than stacked.</summary>
    private bool _resolvingUnsaved;

    /// <summary>True while <see cref="HandleDiskChangeAsync"/> runs, so a burst of change notifications is one reaction.</summary>
    private bool _diskChangeBusy;

    private bool _watchingDisk;
    private bool _disposed;

    /// <summary>The on-disk signature the "changed on disk" notice was raised for; "Keep my edits" silences that version only.</summary>
    private FileSignature? _externalChangeSignature;

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private bool _isRawModified;

    /// <summary>
    /// Shown, for a buffer with unsaved edits, when config.yaml changes on disk behind the editor. Never replaced by
    /// a silent reload — it offers Reload (drop my edits) or Keep my edits.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExternalChangeBanner))]
    private bool _showExternalChangeNotice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewAndSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmReviewedSaveCommand))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewAndSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmReviewedSaveCommand))]
    private bool _isSaving;

    /// <summary>Set when the RAW text does not parse as YAML — RAW still shows it verbatim; FORM is stale/unavailable until it parses.</summary>
    [ObservableProperty]
    private string? _parseError;

    /// <summary>Set when config.yaml exists but could not be read. Blocks Save and FORM until a load succeeds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadFailed))]
    [NotifyPropertyChangedFor(nameof(IsFormEditable))]
    [NotifyPropertyChangedFor(nameof(ShowFormStaleNotice))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewAndSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmReviewedSaveCommand))]
    private string? _loadError;

    /// <summary>Set when the CLI's source view of the configuration could not be fetched or read — FORM is unavailable, RAW still works.</summary>
    [ObservableProperty]
    private string? _formUnavailableReason;

    [ObservableProperty]
    private bool _hasSecretReferences;

    [ObservableProperty]
    private bool _showDriftBanner;

    [ObservableProperty]
    private string _driftMessage = string.Empty;

    [ObservableProperty]
    private bool _showSaveResultBanner;

    [ObservableProperty]
    private string _saveResultMessage = string.Empty;

    [ObservableProperty]
    private bool _saveResultIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreFromBackupCommand))]
    private string? _lastBackupPath;

    [ObservableProperty]
    private string? _lastBackupSha256;

    [ObservableProperty]
    private string? _fieldErrorMessage;

    [ObservableProperty]
    private string? _onDiskPreviewText;

    [ObservableProperty]
    private bool _showOnDiskPreview;

    /// <summary>
    /// Opens the reviewed <c>defenseclaw-gateway restart</c> (the same review dialog and run the tray and the palette use). Set by
    /// the window; null in a view-model nobody wired it to, which leaves the post-save bar without a restart button instead of one
    /// that does nothing. This view-model never runs the command itself: a restart is state-changing and only ever starts from the
    /// operator's confirm in that review.
    /// </summary>
    internal Func<Task>? RestartGatewayRequest { get; set; }

    /// <summary>True while the pre-save diff is on screen over the editor.</summary>
    [ObservableProperty]
    private bool _isReviewing;

    /// <summary>The masked diff of config.yaml as it is on disk against the edited text; empty unless <see cref="IsReviewing"/>.</summary>
    [ObservableProperty]
    private ConfigDiffReview? _review;

    /// <summary>
    /// Set after a save the CLI validated (or could not run to validate): the gateway reads config.yaml when it starts, so the
    /// running one still has the old settings. Cleared by Dismiss, by the next edit, by a failed save and by a reload.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestartGateway))]
    private bool _showRestartPrompt;

    /// <summary>The restart button on the post-save bar is offered only when something can open the review.</summary>
    public bool CanRestartGateway => ShowRestartPrompt && RestartGatewayRequest is not null;

    public ConfigEditorWindowViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _paths = services.Paths;
        _cli = services.Cli;
        _saveService = new ConfigSaveService(_paths, _cli);
        _document = ConfigStore.Parse(string.Empty);
        Sections.CollectionChanged += (_, _) => RaiseFormEmptyState();
    }

    public ObservableCollection<FormSection> Sections { get; } = new();

    public IReadOnlyList<string> FormWarnings { get; private set; } = Array.Empty<string>();

    public string WindowTitle =>
        HasSecretReferences
            ? "DefenseClaw Config Editor — contains secret references"
            : "DefenseClaw Config Editor";

    public string ConfigFilePath => _paths.ConfigFilePath;

    /// <summary>True while the last attempt to read config.yaml failed — Save and FORM stay disabled until a load succeeds.</summary>
    public bool LoadFailed => LoadError is not null;

    /// <summary>
    /// True when FORM edits may be applied: the file loaded, and FORM reflects the current
    /// RAW text. Bound to the FORM content's <c>IsEnabled</c> so a stale tree is visibly inert.
    /// </summary>
    public bool IsFormEditable => !LoadFailed && !NeedsFormRebuild;

    /// <summary>True when FORM is showing a stale tree because RAW does not parse — drives an explanatory notice on the FORM tab.</summary>
    public bool ShowFormStaleNotice => !LoadFailed && NeedsFormRebuild && ParseError is not null;

    public bool ShowSaveSuccessBanner => ShowSaveResultBanner && !SaveResultIsError;

    /// <summary>The save error banner, except for drift — the drift banner already carries that message, so showing both would say it twice.</summary>
    public bool ShowSaveErrorBanner => ShowSaveResultBanner && SaveResultIsError && !ShowDriftBanner;

    partial void OnShowDriftBannerChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveErrorBanner));
        OnPropertyChanged(nameof(ShowExternalChangeBanner));
    }

    /// <summary>
    /// True when the editor holds edits that are not in config.yaml — typed in RAW or applied from FORM (FORM edits
    /// publish into RAW, so this is the single answer for both tabs). See the class remarks for what "modified" means.
    /// </summary>
    public bool HasUnsavedChanges => IsRawModified;

    /// <summary>The "changed on disk" notice, except while the save-time drift banner is up: that one already says it, with the same buttons.</summary>
    public bool ShowExternalChangeBanner => ShowExternalChangeNotice && !ShowDriftBanner;

    /// <summary>
    /// Restore is offered while the file on disk is one this editor wrote that failed
    /// validation and a known-good backup exists. It does not depend on the save banner
    /// (Reload hides the banner, not the problem) and never applies to a file that changed
    /// externally since — a stale backup must not overwrite someone else's edit.
    /// </summary>
    public bool ShowRestoreAction => _unvalidatedSignature is not null && LastBackupPath is not null;

    /// <summary>"RAW changed since FORM last absorbed it" — see the class remarks.</summary>
    private bool NeedsFormRebuild
    {
        get => _needsFormRebuild;
        set
        {
            if (_needsFormRebuild == value)
            {
                return;
            }

            _needsFormRebuild = value;
            OnPropertyChanged(nameof(IsFormEditable));
            OnPropertyChanged(nameof(ShowFormStaleNotice));
        }
    }

    private FileSignature? UnvalidatedSignature
    {
        get => _unvalidatedSignature;
        set
        {
            _unvalidatedSignature = value;
            OnPropertyChanged(nameof(ShowRestoreAction));
        }
    }

    partial void OnHasSecretReferencesChanged(bool value) => OnPropertyChanged(nameof(WindowTitle));

    partial void OnShowSaveResultBannerChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveSuccessBanner));
        OnPropertyChanged(nameof(ShowSaveErrorBanner));
    }

    partial void OnSaveResultIsErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveSuccessBanner));
        OnPropertyChanged(nameof(ShowSaveErrorBanner));
    }

    partial void OnLastBackupPathChanged(string? value) => OnPropertyChanged(nameof(ShowRestoreAction));

    partial void OnParseErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowFormStaleNotice));
        RaiseFormEmptyState();
    }

    partial void OnIsLoadingChanged(bool value) => RaiseFormEmptyState();

    partial void OnFormUnavailableReasonChanged(string? value) => RaiseFormEmptyState();

    partial void OnLoadErrorChanged(string? value) => RaiseFormEmptyState();

    /// <summary>True when FORM has no sections to draw and nothing is loading — the tab shows an explanation instead of a blank page.</summary>
    public bool ShowFormEmptyState => !IsLoading && Sections.Count == 0;

    /// <summary>Why FORM is empty, in the terms of the state that caused it (mac pattern #1: never one scary "empty").</summary>
    public string FormEmptyTitle =>
        LoadFailed ? "config.yaml could not be loaded"
        : ParseError is not null ? "FORM cannot be built from invalid YAML"
        : FormUnavailableReason is not null ? "FORM is unavailable"
        : "No settings to show";

    public string FormEmptyDetail =>
        LoadFailed ? "FORM needs the file to load. Fix the error above and press Reload."
        : ParseError is not null ? "Fix the YAML in the RAW tab, then switch back to FORM."
        : FormUnavailableReason is not null ? "RAW editing still works — see the notice above for why FORM could not be built."
        : "config.yaml has no sections yet. Add settings in the RAW tab, or use the Setup panel's wizards.";

    private void RaiseFormEmptyState()
    {
        OnPropertyChanged(nameof(ShowFormEmptyState));
        OnPropertyChanged(nameof(FormEmptyTitle));
        OnPropertyChanged(nameof(FormEmptyDetail));
    }

    partial void OnRawTextChanged(string value)
    {
        IsRawModified = !string.Equals(value, _diskText, StringComparison.Ordinal);
        if (IsRawModified)
        {
            ShowRestartPrompt = false;
        }

        // Programmatic publishes (a load, a FORM edit) set this around the assignment; see
        // SetRawTextWithoutRebuildFlag for why the flag is reset there and not here.
        if (_suppressRawChangeTracking)
        {
            return;
        }

        NeedsFormRebuild = true;
    }

    /// <summary>
    /// Assigns <see cref="RawText"/> without marking FORM stale. The suppression flag is
    /// cleared in a <c>finally</c> around the assignment rather than by
    /// <see cref="OnRawTextChanged"/>: the generated setter does not raise the change hook
    /// when the text is unchanged, so a flag reset that lived in the hook would stay set and
    /// swallow the user's next real RAW edit.
    /// </summary>
    private void SetRawTextWithoutRebuildFlag(string text)
    {
        _suppressRawChangeTracking = true;
        try
        {
            RawText = text;
        }
        finally
        {
            _suppressRawChangeTracking = false;
        }
    }

    /// <summary>Loads config.yaml and fetches the CLI's masked source view of it once. Call after the window is constructed.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) => LoadCoreAsync(cancellationToken, keepEditedBuffer: false);

    /// <param name="cancellationToken">Cancels the read of config.yaml.</param>
    /// <param name="keepEditedBuffer">
    /// For a reload nobody asked for (the file changed behind the editor): if unsaved edits appear while the file is
    /// being read, the load stands down — buffer, signature and banners untouched — instead of replacing them.
    /// </param>
    private async Task LoadCoreAsync(CancellationToken cancellationToken, bool keepEditedBuffer)
    {
        IsLoading = true;
        try
        {
            // Signature first, then the read: if the file changes in between, the text is
            // newer than the signature and the next save reports drift (safe) instead of
            // the text being older than the signature and hiding it.
            var previousSignature = _loadedSignature;
            _loadedSignature = _saveService.CaptureSignature();

            // The file on disk is no longer the one our unvalidated save produced (someone
            // else edited it, or it was restored) — its backup is not ours to offer.
            if (_unvalidatedSignature is { } unvalidated && unvalidated != _loadedSignature)
            {
                UnvalidatedSignature = null;
            }

            string rawText;
            try
            {
                rawText = File.Exists(_paths.ConfigFilePath)
                    ? await DefenseClaw.Core.IO.SharedFile.ReadAllTextAsync(_paths.ConfigFilePath, cancellationToken).ConfigureAwait(true)
                    : string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ApplyLoadFailure($"config.yaml could not be read: {ex.Message}");
                return;
            }

            if (keepEditedBuffer && HasUnsavedChanges)
            {
                // Typing that landed while the file was being read wins over a reload nobody asked for. The signature
                // goes back too, so a Save still reports that the file moved.
                _loadedSignature = previousSignature;
                return;
            }

            LoadError = null;

            // Set before the assignment so the change hook compares against the file just read, and cleared
            // again after it: an unchanged text raises no hook, so a sticky "modified" would otherwise survive.
            _diskText = rawText;
            SetRawTextWithoutRebuildFlag(rawText);
            IsRawModified = false;
            ClearExternalChange();

            var parsed = false;
            try
            {
                _document = ConfigStore.Parse(rawText, _paths.ConfigFilePath);
                ParseError = null;
                parsed = true;
            }
            catch (ConfigParseException ex)
            {
                _document = ConfigStore.Parse(string.Empty);
                ParseError = ex.Message;
                Sections.Clear();
            }

            // On a failed parse FORM has nothing (or something stale) to show for this RAW
            // text; keep it marked stale so FORM edits stay refused until RAW parses.
            NeedsFormRebuild = !parsed;

            HasSecretReferences = SensitiveKeyClassifier.ContainsSensitiveReferences(rawText);

            await FetchFormSourceAsync(cancellationToken).ConfigureAwait(true);

            if (_formSourceYaml.Length == 0)
            {
                Sections.Clear();
            }
            else if (parsed)
            {
                RebuildForm();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// The read of config.yaml failed. The editor is emptied and made inert rather than
    /// showing an empty document that looks like a real (blank) config: Save is disabled by
    /// <see cref="LoadFailed"/>, FORM is unavailable, and only a successful <see cref="LoadAsync"/>
    /// (Reload) clears it.
    /// </summary>
    private void ApplyLoadFailure(string message)
    {
        LoadError = message;
        ParseError = null;

        _diskText = string.Empty;
        SetRawTextWithoutRebuildFlag(string.Empty);
        IsRawModified = false;
        ClearExternalChange();

        _document = ConfigStore.Parse(string.Empty);
        _formSourceYaml = string.Empty;
        NeedsFormRebuild = false;
        Sections.Clear();
        FormUnavailableReason = "FORM is unavailable until config.yaml loads successfully (see the error above).";
        HasSecretReferences = false;
    }

    /// <summary>
    /// The FORM tab calls this when it becomes visible; a no-op unless RAW changed underneath
    /// it. On a parse failure the stale flag stays set (FORM edits remain refused and the
    /// FORM tab says why) and the next visit tries again; a successful parse clears both
    /// the flag and <see cref="ParseError"/>.
    /// </summary>
    public void NotifyFormTabSelected()
    {
        if (LoadFailed || _formSourceYaml.Length == 0 || !NeedsFormRebuild)
        {
            return;
        }

        try
        {
            _document = ConfigStore.Parse(RawText, _paths.ConfigFilePath);
            ParseError = null;
            RebuildForm();
            NeedsFormRebuild = false;
        }
        catch (ConfigParseException ex)
        {
            ParseError = ex.Message;
        }
    }

    /// <summary>
    /// The Reload button. With unsaved edits it asks first (save / discard / cancel) — Save must succeed and
    /// validate before the reload goes ahead, Cancel leaves everything as it is.
    /// </summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (!await ResolveUnsavedChangesAsync(UnsavedChangesContext.Reload).ConfigureAwait(true))
        {
            return;
        }

        await ReloadCoreAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The "changed on disk" notice's Reload: the operator has just been told the file moved and chose to take
    /// the file's version over their edits, so there is no second question.
    /// </summary>
    [RelayCommand]
    private Task ReloadDiscardingEditsAsync() => ReloadCoreAsync();

    /// <summary>The "changed on disk" notice's Keep my edits: stop nagging about this version of the file; a Save will still report the drift.</summary>
    [RelayCommand]
    private void KeepEdits() => ShowExternalChangeNotice = false;

    private async Task ReloadCoreAsync(bool keepEditedBuffer = false)
    {
        ShowDriftBanner = false;
        ShowSaveResultBanner = false;
        ShowOnDiskPreview = false;
        ShowRestartPrompt = false;
        await LoadCoreAsync(CancellationToken.None, keepEditedBuffer).ConfigureAwait(true);
    }

    /// <summary>
    /// The prompt-and-act step every operation that would replace the buffer goes through. Returns true when the
    /// caller may carry on: there was nothing to lose, or the operator chose Discard, or chose Save and the save
    /// wrote config.yaml <i>and</i> the CLI validated it. Returns false — the caller must stop and leave the
    /// window as it is — on Cancel, on an unanswered prompt, on a failed save (its error is on the banner), when
    /// the operator typed again while the save ran, and when another question is already open.
    /// <para>
    /// "Saved" means <see cref="SaveOutcome.Success"/>, the same bar the Save button's green banner uses; that
    /// includes a machine with no CLI on PATH, where the write happened but nothing could validate it (the banner
    /// says so).
    /// </para>
    /// </summary>
    internal async Task<bool> ResolveUnsavedChangesAsync(UnsavedChangesContext context)
    {
        if (_resolvingUnsaved)
        {
            return false;
        }

        _resolvingUnsaved = true;
        var proceed = false;
        try
        {
            // A save that is already running decides what "unsaved" means; let it land first.
            if (_activeSave is { IsCompleted: false } inFlight)
            {
                _ = await inFlight.ConfigureAwait(true);
            }

            CommitPendingEdits?.Invoke();

            // Restore replaces the file with the backup, so what would be lost is anything beyond what is already
            // on disk (the text of a save the CLI rejected is on disk and is exactly what Restore is for).
            var needsAnswer = context == UnsavedChangesContext.Restore
                ? !string.Equals(RawText, _diskText, StringComparison.Ordinal)
                : HasUnsavedChanges;
            if (!needsAnswer)
            {
                proceed = true;
                return proceed;
            }

            var fileChanged = !LoadFailed && _saveService.CaptureSignature() != _loadedSignature;
            var request = UnsavedChangesRequest.Create(context, ParseError is not null, fileChanged);
            if (!CanSave())
            {
                request = request with { CanSave = false, SaveLabel = null, DefaultChoice = UnsavedChangesChoice.Cancel };
            }

            var prompt = UnsavedChangesPrompt;
            var choice = prompt is null
                ? UnsavedChangesChoice.Cancel
                : await prompt(request).ConfigureAwait(true);

            switch (choice)
            {
                case UnsavedChangesChoice.Discard:
                    proceed = true;
                    return true;

                case UnsavedChangesChoice.Save when request.CanSave:
                    var outcome = await SaveCoreAsync().ConfigureAwait(true);
                    proceed = outcome is { Success: true } && !HasUnsavedChanges;
                    return proceed;

                default:
                    return false;
            }
        }
        finally
        {
            _resolvingUnsaved = false;

            // Nothing was dropped and nothing was saved: a change to the file that arrived while the question was
            // open was not looked at (see HandleDiskChangeAsync), so look now.
            if (!proceed)
            {
                _ = HandleDiskChangeAsync();
            }
        }
    }

    /// <summary>
    /// Asked before the window closes or the app exits. True: nothing to lose, the operator discarded, or the
    /// edits were saved and validated — go ahead. False: stay open (cancelled, save failed, or another question is
    /// already open).
    /// </summary>
    public Task<bool> ConfirmCloseAsync() => ResolveUnsavedChangesAsync(UnsavedChangesContext.Close);

    /// <summary>
    /// The synchronous half of closing, for a <c>Closing</c> handler that cannot await: commits a FORM edit still
    /// in its box, then says whether the close must be put on hold for <see cref="ConfirmCloseAsync"/> — there are
    /// unsaved edits, a save is running, or a question is already open.
    /// </summary>
    public bool CloseNeedsConfirmation()
    {
        CommitPendingEdits?.Invoke();
        return HasUnsavedChanges || IsSaving || _resolvingUnsaved;
    }

    /// <summary>
    /// Starts reacting to <c>config.yaml</c> changes reported by the shell (<see cref="AppServices.ConfigReloaded"/>,
    /// already marshalled onto the UI thread). Off until asked, so a view-model built without a window — every unit
    /// test — never has a watcher thread poking at it; <see cref="HandleDiskChangeAsync"/> is the reaction and is
    /// called directly there.
    /// </summary>
    public void WatchDiskChanges()
    {
        if (_watchingDisk || _disposed)
        {
            return;
        }

        _watchingDisk = true;
        _services.ConfigReloaded += OnServicesConfigReloaded;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_watchingDisk)
        {
            _watchingDisk = false;
            _services.ConfigReloaded -= OnServicesConfigReloaded;
        }
    }

    private void OnServicesConfigReloaded(object? sender, EventArgs e) => _ = HandleDiskChangeAsync();

    /// <summary>
    /// config.yaml (or .env) changed on disk. Nothing to do when config.yaml is what the editor last read or wrote —
    /// the editor's own save, a .env edit and a duplicate notification all look like that. Otherwise the file was
    /// edited behind the editor's back, and: a clean buffer is reloaded (nothing to lose); a buffer with unsaved
    /// edits is <b>never</b> touched — it raises <see cref="ShowExternalChangeNotice"/> and leaves the choice
    /// (Reload, or Keep my edits) to the operator.
    /// </summary>
    internal async Task HandleDiskChangeAsync()
    {
        // Loading, saving and a pending question all own the buffer right now; the drift check on Save is the
        // backstop for a change that lands in their window, and a cancelled question re-checks (see the finally there).
        if (_disposed || _diskChangeBusy || _resolvingUnsaved || IsLoading || IsSaving || LoadFailed)
        {
            return;
        }

        _diskChangeBusy = true;
        try
        {
            var onDisk = _saveService.CaptureSignature();
            if (onDisk == _loadedSignature || _disposed)
            {
                return;
            }

            CommitPendingEdits?.Invoke();

            if (!HasUnsavedChanges)
            {
                await ReloadCoreAsync(keepEditedBuffer: true).ConfigureAwait(true);

                // Reloaded: done. Edits typed while the file was being read stopped the reload instead, and the
                // notice below is then the answer.
                if (!HasUnsavedChanges)
                {
                    return;
                }
            }

            // Once per version of the file: a Keep my edits answer stands until the file changes again.
            if (_externalChangeSignature != onDisk)
            {
                _externalChangeSignature = onDisk;
                ShowExternalChangeNotice = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // LoadAsync already turns a failed read into LoadError; this only keeps a change notification from
            // faulting the task nobody awaits.
        }
        finally
        {
            _diskChangeBusy = false;
        }
    }

    private void ClearExternalChange()
    {
        _externalChangeSignature = null;
        ShowExternalChangeNotice = false;
    }

    [RelayCommand]
    private async Task ViewOnDiskAsync()
    {
        OnDiskPreviewText = await _saveService.ReadCurrentTextAsync().ConfigureAwait(true);
        ShowOnDiskPreview = true;
    }

    [RelayCommand]
    private void DismissOnDiskPreview() => ShowOnDiskPreview = false;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => _ = await SaveCoreAsync().ConfigureAwait(true);

    /// <summary>
    /// The Save button and Ctrl+S: shows what the save would change (the on-disk text against the edited RAW, secrets masked)
    /// and saves from that view. With nothing changed there is nothing to review and the save runs as it always did (it still
    /// re-validates, which is what Save is for after a rejected one). <see cref="SaveCommand"/> itself is the unreviewed
    /// pipeline the unsaved-changes prompt and the tests use.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ReviewAndSaveAsync()
    {
        if (IsReviewing)
        {
            return;
        }

        CommitPendingEdits?.Invoke();

        if (!HasUnsavedChanges)
        {
            _ = await SaveCoreAsync().ConfigureAwait(true);
            return;
        }

        Review = ConfigDiffReviewBuilder.Build(_diskText, RawText);
        IsReviewing = true;
    }

    /// <summary>"Save" on the review: the reviewed save. Hash check, backup, atomic replace and validate are the unchanged pipeline.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ConfirmReviewedSaveAsync()
    {
        CloseReview();
        _ = await SaveCoreAsync().ConfigureAwait(true);
    }

    /// <summary>"Back to editing" on the review (also Esc): nothing is saved and the buffer is as it was.</summary>
    [RelayCommand]
    private void CancelReview() => CloseReview();

    private void CloseReview()
    {
        IsReviewing = false;
        Review = null;
    }

    /// <summary>The post-save bar's button: asks the window for the reviewed gateway restart. Never runs anything itself.</summary>
    [RelayCommand]
    private async Task RestartGatewayAsync()
    {
        if (RestartGatewayRequest is { } request)
        {
            await request().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void DismissRestartPrompt() => ShowRestartPrompt = false;

    /// <summary>
    /// The save, for the button and for the save/discard/cancel prompt alike. Null when Save is not available right
    /// now; otherwise the pipeline's outcome (also shown on the banner). Tracked in <c>_activeSave</c> so a close or
    /// reload that arrives while it runs can wait for it.
    /// </summary>
    private Task<SaveOutcome?> SaveCoreAsync()
    {
        var save = RunSaveAsync();
        _activeSave = save;
        return save;
    }

    private async Task<SaveOutcome?> RunSaveAsync()
    {
        if (!CanSave())
        {
            return null;
        }

        IsSaving = true;
        ShowSaveResultBanner = false;
        ShowRestartPrompt = false;
        CloseReview();
        try
        {
            var textToSave = RawText;
            var signatureUsed = _loadedSignature;
            var outcome = await _saveService.SaveAsync(textToSave, signatureUsed).ConfigureAwait(true);

            SaveResultMessage = outcome.Message;
            SaveResultIsError = !outcome.Success;
            DriftMessage = outcome.Stage == SaveStage.DriftDetected ? outcome.Message : string.Empty;
            ShowSaveResultBanner = true;
            ShowDriftBanner = outcome.Stage == SaveStage.DriftDetected;

            // Backup bookkeeping. Outcomes without a backup (drift, backup failure) leave the
            // offered backup alone — they never replace it with null. And when the file that
            // was just backed up is itself the unvalidated result of our previous save, that
            // backup holds bad content: keep offering the last-known-good one instead.
            if (outcome.BackupPath is not null)
            {
                var backedUpAnUnvalidatedFile = _unvalidatedSignature is { } unvalidated && unvalidated == signatureUsed;
                if (!backedUpAnUnvalidatedFile)
                {
                    LastBackupPath = outcome.BackupPath;
                    LastBackupSha256 = outcome.BackupSha256Hex;
                }
            }

            // The file on disk is now our text whether or not the CLI liked it. Refresh the
            // signature on validation failure too, or the next save compares against the
            // pre-save file and reports drift for a change we made ourselves.
            if (outcome.Stage is SaveStage.Succeeded or SaveStage.ValidationFailed)
            {
                _loadedSignature = _saveService.CaptureSignature();
                _diskText = textToSave;
                HasSecretReferences = SensitiveKeyClassifier.ContainsSensitiveReferences(textToSave);
                UnvalidatedSignature = outcome.Success ? null : _loadedSignature;
                ClearExternalChange();
            }
            else if (outcome.Stage == SaveStage.DriftDetected)
            {
                // With the signature kept current, drift now means someone else changed the
                // file. Whatever we wrote earlier is gone from disk, so its backup must not
                // be offered as a "restore" that would overwrite their edit.
                UnvalidatedSignature = null;
            }

            if (outcome.Success)
            {
                // The running gateway read config.yaml when it started; the file it will read next is this one.
                ShowRestartPrompt = true;

                // Typing that happened while the save was in flight is not saved yet.
                IsRawModified = !string.Equals(RawText, textToSave, StringComparison.Ordinal);
                ClearParseErrorIfParses(textToSave);

                // Re-base FORM on the file we just wrote (one quiet CLI read; see the method).
                await RefreshFormSourceAfterSaveAsync(textToSave).ConfigureAwait(true);
            }
            else if (outcome.Stage == SaveStage.ValidationFailed)
            {
                // The file on disk is our text but the CLI rejected it: still "not saved", so the badge stays lit
                // and closing asks — until the next load, save or edit says otherwise.
                IsRawModified = true;
            }

            return outcome;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving && !IsLoading && !LoadFailed && ChangesBlockedReason is null;

    /// <summary>
    /// Why Save and Restore are off: the installation is managed or invalid, and this editor writes config.yaml itself. Null while they are on.
    /// Reading, the FORM and the RAW view are not affected.
    /// </summary>
    public string? ChangesBlockedReason => _services.Installation.BlockedReason;

    public bool HasChangesBlockedReason => ChangesBlockedReason is not null;

    /// <summary>The Save button's tooltip: the shortcut, or why it is off.</summary>
    public string SaveToolTip => ChangesBlockedReason ?? "Review and save (Ctrl+S)";

    /// <summary>A saved-and-validated file proves the text parses; drop a stale parse-error banner if our own parser agrees.</summary>
    private void ClearParseErrorIfParses(string text)
    {
        if (ParseError is null)
        {
            return;
        }

        try
        {
            _ = ConfigStore.Parse(text, _paths.ConfigFilePath);
            ParseError = null;
        }
        catch (ConfigParseException)
        {
            // Keep the banner: our parser still disagrees with the text.
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreFromBackupAsync()
    {
        if (LastBackupPath is null)
        {
            return;
        }

        // Restore reloads the editor from the backed-up file, so edits made since the last write would go with it.
        if (!await ResolveUnsavedChangesAsync(UnsavedChangesContext.Restore).ConfigureAwait(true))
        {
            return;
        }

        var outcome = await _saveService.RestoreFromBackupAsync(LastBackupPath).ConfigureAwait(true);
        SaveResultMessage = outcome.Message;
        SaveResultIsError = !outcome.Success;
        ShowSaveResultBanner = true;

        if (outcome.Success)
        {
            UnvalidatedSignature = null;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private bool CanRestore() => LastBackupPath is not null && ChangesBlockedReason is null;

    /// <summary>What a FORM-source fetch produced: the masked YAML, or the reason there is none.</summary>
    internal sealed record FormSourceResult(string? Yaml, string? Failure);

    /// <summary>
    /// Runs a command that must be read-only. The tier comes from <see cref="CommandTiers"/>, the one
    /// classifier every review surface in the app shares: a verb it does not call read-only would need a
    /// confirmation step, which this window does not have (its only prompt is the unsaved-changes question,
    /// which is about the buffer, not a command), so it is refused rather than run.
    /// </summary>
    internal Task<CliInvocation> RunReadOnlyAsync(string[] argv, CancellationToken cancellationToken)
    {
        if (CommandTiers.Classify(argv) != CommandTier.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Refusing to run 'defenseclaw {string.Join(' ', argv)}' without review: it is not a read-only command.");
        }

        // The output is parsed as a whole (it becomes the FORM tree), so it is read with the full-output ceilings:
        // with the ordinary cap a config longer than 2,000 lines / 256 KiB loses its head and silently parses as
        // a shorter config (--effective is already past 1,200 lines).
        return _cli.RunAsync(argv, cancellationToken: cancellationToken, options: CliRunOptions.JsonRead);
    }

    private static string OutputText(CliInvocation invocation, CliStream stream) =>
        string.Join(
            Environment.NewLine,
            invocation.OutputLines.Where(l => l.Stream == stream).Select(l => l.Text));

    /// <summary>
    /// Reads the masked source view of config.yaml. <c>--source</c> first; a CLI too old to know that option
    /// (usage error naming it) is asked for <c>--effective</c> instead, which carries the same masking.
    /// </summary>
    private async Task<FormSourceResult> ReadFormSourceAsync(CancellationToken cancellationToken)
    {
        if (FormSourceOverride is { } supplied)
        {
            return new FormSourceResult(await supplied(cancellationToken).ConfigureAwait(true), null);
        }

        try
        {
            var argv = SourceArgv;
            var invocation = await RunReadOnlyAsync(argv, cancellationToken).ConfigureAwait(true);

            if (string.IsNullOrEmpty(invocation.FailureReason) &&
                invocation.ExitCode is not 0 &&
                OutputText(invocation, CliStream.StandardError).Contains("no such option", StringComparison.OrdinalIgnoreCase))
            {
                argv = EffectiveArgv;
                invocation = await RunReadOnlyAsync(argv, cancellationToken).ConfigureAwait(true);
            }

            return FormSourceFrom(invocation, argv);
        }
        catch (CliNotFoundException)
        {
            return new FormSourceResult(
                null,
                "The defenseclaw CLI was not found on PATH. FORM view needs it to read config.yaml's masked source view — RAW editing still works.");
        }
    }

    /// <summary>What one finished <c>config show</c> read means for the FORM tab: the YAML, or why there is none.</summary>
    internal static FormSourceResult FormSourceFrom(CliInvocation invocation, IReadOnlyList<string> argv)
    {
        if (invocation.FailureReason is { Length: > 0 } failure)
        {
            return new FormSourceResult(null, $"Could not run defenseclaw: {failure}");
        }

        if (invocation.ExitCode is not 0)
        {
            return new FormSourceResult(
                null,
                $"defenseclaw {string.Join(' ', argv)} exited {invocation.ExitCode}: {OutputText(invocation, CliStream.StandardError)}");
        }

        // Even the full-output ceilings (200,000 lines / 16 MiB) drop the head of a runaway output. A source view
        // with its top missing must never become the FORM tree, which would then save over what it left out.
        if (invocation.IsOutputTruncated)
        {
            return new FormSourceResult(
                null,
                $"defenseclaw {string.Join(' ', argv)} printed more than the app keeps ({invocation.DroppedOutputLineCount:N0} lines dropped), " +
                "so FORM view would show only part of config.yaml. RAW editing still works.");
        }

        return new FormSourceResult(OutputText(invocation, CliStream.StandardOutput), null);
    }

    private async Task FetchFormSourceAsync(CancellationToken cancellationToken)
    {
        var result = await ReadFormSourceAsync(cancellationToken).ConfigureAwait(true);
        if (result.Yaml is null)
        {
            FormUnavailableReason = result.Failure;
            _formSourceYaml = string.Empty;
            return;
        }

        _formSourceYaml = result.Yaml;
        FormUnavailableReason = null;
    }

    /// <summary>
    /// After a save that validated, the CLI's source view describes the file we just wrote, so FORM is
    /// re-based on it (sections added or removed in RAW now show up without a Reload). One call, once, on
    /// the user's own action — not polling — and quiet: if it fails the tree already on screen stays and no
    /// banner is raised, because the save itself succeeded.
    /// </summary>
    private async Task RefreshFormSourceAfterSaveAsync(string savedText)
    {
        var result = await ReadFormSourceAsync(CancellationToken.None).ConfigureAwait(true);
        if (result.Yaml is not { Length: > 0 } yaml)
        {
            return;
        }

        _formSourceYaml = yaml;
        FormUnavailableReason = null;

        // If the user typed while the save and the fetch were in flight, RAW is ahead of the file: the
        // tab-selection rebuild will pick up the new source against the newer text.
        if (!string.Equals(RawText, savedText, StringComparison.Ordinal))
        {
            NeedsFormRebuild = true;
            return;
        }

        // A save made after FORM edits saved exactly what the tree already shows (a FORM edit patches
        // _document and RAW together), so rebuilding would only throw the operator's scroll position
        // away. Rebuild only when the text was typed in RAW and the tree has never seen it.
        if (string.Equals(_document.RawText, savedText, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _document = ConfigStore.Parse(savedText, _paths.ConfigFilePath);
        }
        catch (ConfigParseException)
        {
            return;
        }

        RebuildForm();
        NeedsFormRebuild = false;
    }

    private void RebuildForm()
    {
        var result = ConfigFormBuilder.Build(_formSourceYaml, _document, OnFieldCommitted, OnListCommitted);

        Sections.Clear();
        foreach (var section in result.Sections)
        {
            Sections.Add(section);
        }

        FormWarnings = result.Warnings;
        OnPropertyChanged(nameof(FormWarnings));

        // A source the builder could not read at all is "FORM unavailable", not "nothing to show".
        if (result.Sections.Count == 0 && result.Warnings.Count > 0)
        {
            FormUnavailableReason = string.Join(" ", result.Warnings);
        }
    }

    /// <summary>
    /// The gate every FORM edit passes first. Returns <see langword="false"/> — with
    /// <see cref="FieldErrorMessage"/> explaining why — when applying the edit could
    /// overwrite something: config.yaml never loaded, RAW has changes FORM has not parsed
    /// (a FORM edit patches the parsed document's text and would replace those changes), or
    /// the parsed document is somehow not the current RAW text.
    /// </summary>
    private bool CanApplyFormEdit(string displayName)
    {
        FieldErrorMessage = null;

        if (LoadFailed)
        {
            FieldErrorMessage = $"Could not apply '{displayName}': config.yaml was not loaded. Reload it first.";
            return false;
        }

        if (NeedsFormRebuild || !string.Equals(_document.RawText, RawText, StringComparison.Ordinal))
        {
            FieldErrorMessage = ParseError is null
                ? $"Could not apply '{displayName}': the RAW tab has changes FORM has not picked up yet. Switch to RAW and back to FORM, then try again."
                : $"Could not apply '{displayName}': the RAW tab has changes that do not parse as YAML, so FORM would overwrite them. Fix the YAML in RAW first.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Refuses a commit that could carry a masked value into config.yaml — the second of the four guards
    /// (see the class remarks). Independent of how the field was built: a locked, secret or masked field is
    /// refused, and so is any value that is itself a mask placeholder (a pasted "[REDACTED]" included).
    /// </summary>
    private bool RefuseMaskedCommit(FormField field)
    {
        var isMaskedText = (field.Kind is FormFieldKind.String or FormFieldKind.EnvName) &&
                           SensitiveKeyClassifier.IsMaskedValue(field.TextValue);

        if (field.IsMasked || field.Kind == FormFieldKind.Secret || !field.IsEditable || isMaskedText)
        {
            FieldErrorMessage = $"Could not apply '{field.DisplayName}': it holds a secret or a value the CLI masks, and this form never writes masked text back. Edit it in the RAW tab.";
            return true;
        }

        return false;
    }

    private bool RefuseMaskedCommit(FormListField list)
    {
        if (!list.IsEditable || list.Items.Any(SensitiveKeyClassifier.IsMaskedValue))
        {
            FieldErrorMessage = $"Could not apply '{list.DisplayName}': it holds a value the CLI masks, and this form never writes masked text back. Edit it in the RAW tab.";
            return true;
        }

        return false;
    }

    private void OnFieldCommitted(FormField field)
    {
        if (!CanApplyFormEdit(field.DisplayName) || RefuseMaskedCommit(field))
        {
            return;
        }

        var pathSegments = field.Path.Split('.');
        var sectionName = pathSegments[0];
        var sectionText = _document.SectionText(sectionName);
        if (sectionText is null)
        {
            FieldErrorMessage = $"Could not apply '{field.DisplayName}': its section is not present in config.yaml.";
            return;
        }

        var patchedSection = YamlSectionEditor.TrySetScalar(sectionText, pathSegments, field.CurrentRawValue);
        if (patchedSection is null)
        {
            FieldErrorMessage = $"Could not apply '{field.DisplayName}' — edit it in the RAW tab instead.";
            return;
        }

        var expected = ExpectedScalarValue(field);
        PublishPatchedSection(
            sectionName,
            field.DisplayName,
            patchedSection,
            patched => SectionHoldsScalar(patched, pathSegments, expected));
    }

    private void OnListCommitted(FormListField list)
    {
        if (!CanApplyFormEdit(list.DisplayName) || RefuseMaskedCommit(list))
        {
            return;
        }

        var pathSegments = list.Path.Split('.');
        var sectionName = pathSegments[0];
        var sectionText = _document.SectionText(sectionName);
        if (sectionText is null)
        {
            FieldErrorMessage = $"Could not apply '{list.DisplayName}': its section is not present in config.yaml.";
            return;
        }

        var items = list.Items.ToList();
        var patchedSection = YamlSectionEditor.TrySetList(sectionText, pathSegments, items);
        if (patchedSection is null)
        {
            FieldErrorMessage = $"Could not apply '{list.DisplayName}' — edit it in the RAW tab instead.";
            return;
        }

        PublishPatchedSection(
            sectionName,
            list.DisplayName,
            patchedSection,
            patched => SectionHoldsList(patched, pathSegments, items));
    }

    /// <summary>
    /// Publishes a patched section into RAW — but only after proving the result: the whole
    /// document must still parse (this is reached from a <c>LostFocus</c> binding, so a
    /// <see cref="ConfigParseException"/> must not escape to the dispatcher), the patched
    /// section must land intact with every other section byte-identical, and re-reading the
    /// patched section must yield exactly the value the user entered. Any failure sets
    /// <see cref="FieldErrorMessage"/> and leaves RAW untouched — refusing beats guessing.
    /// </summary>
    internal void PublishPatchedSection(
        string sectionName,
        string displayName,
        string patchedSectionText,
        Func<string, bool> patchedSectionYieldsExpectedValue)
    {
        var newRawText = _document.WithSectionReplaced(sectionName, patchedSectionText);

        ConfigDocument newDocument;
        try
        {
            newDocument = ConfigStore.Parse(newRawText, _paths.ConfigFilePath);
        }
        catch (ConfigParseException ex)
        {
            FieldErrorMessage = $"Could not apply '{displayName}': the result would not parse as YAML ({ex.Message}). Edit it in the RAW tab instead.";
            return;
        }

        if (!OnlySectionChanged(_document, newDocument, sectionName, patchedSectionText) ||
            !patchedSectionYieldsExpectedValue(patchedSectionText))
        {
            FieldErrorMessage = $"Could not apply '{displayName}': the patched YAML did not read back as expected — edit it in the RAW tab instead.";
            return;
        }

        // The last guard: whatever the patch was, it must not leave more mask placeholders in the
        // document than it started with. A count that goes up means a masked value went in.
        if (SensitiveKeyClassifier.CountMaskMarkers(newRawText) > SensitiveKeyClassifier.CountMaskMarkers(_document.RawText))
        {
            FieldErrorMessage = $"Could not apply '{displayName}': the change would write masked text into config.yaml. Edit it in the RAW tab instead.";
            return;
        }

        _document = newDocument;
        SetRawTextWithoutRebuildFlag(newRawText);

        // The FORM tree already reflects the edit locally (the field/list VM applied it
        // to itself); only the raw text needed republishing. Clear the rebuild flag so
        // switching tabs right after a FORM edit does not immediately rebuild over it.
        NeedsFormRebuild = false;
    }

    /// <summary>True when re-splitting the patched text gives the new section text and leaves every other section exactly as it was.</summary>
    private static bool OnlySectionChanged(ConfigDocument before, ConfigDocument after, string sectionName, string patchedSectionText)
    {
        // WithSectionReplaced closes an unterminated last line with the file's own line ending, so this
        // has to ask the same question: a hard-coded "\n" would refuse every edit to a CRLF file's
        // final section when that file has no newline at the end.
        var expectedSection = before.WithTrailingLineEnding(patchedSectionText);
        if (!string.Equals(after.SectionText(sectionName), expectedSection, StringComparison.Ordinal))
        {
            return false;
        }

        if (after.Sections.Count != before.Sections.Count)
        {
            return false;
        }

        foreach (var (name, text) in before.Sections)
        {
            if (name == sectionName)
            {
                continue;
            }

            if (!after.Sections.TryGetValue(name, out var afterText) || !string.Equals(afterText, text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The string the YAML scalar should read back as for this field.</summary>
    private static string ExpectedScalarValue(FormField field) => field.Kind switch
    {
        FormFieldKind.Bool => field.BoolValue ? "true" : "false",
        FormFieldKind.Int => ((int)Math.Round(field.NumberValue)).ToString(CultureInfo.InvariantCulture),
        _ => field.TextValue,
    };

    private static bool SectionHoldsScalar(string sectionText, IReadOnlyList<string> path, string expected) =>
        TryNavigate(sectionText, path) is YamlScalarNode scalar &&
        string.Equals(scalar.Value, expected, StringComparison.Ordinal);

    private static bool SectionHoldsList(string sectionText, IReadOnlyList<string> path, IReadOnlyList<string> expected)
    {
        if (TryNavigate(sectionText, path) is not YamlSequenceNode sequence || sequence.Children.Count != expected.Count)
        {
            return false;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (sequence.Children[i] is not YamlScalarNode item ||
                !string.Equals(item.Value, expected[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses one section's text and walks the mapping keys in <paramref name="path"/>.
    /// Null for anything unexpected — including a parse failure, which is exactly the "the
    /// patch did not produce what we meant" signal the caller wants.
    /// </summary>
    private static YamlNode? TryNavigate(string sectionText, IReadOnlyList<string> path)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(sectionText);
            stream.Load(reader);

            if (stream.Documents.Count != 1)
            {
                return null;
            }

            YamlNode node = stream.Documents[0].RootNode;
            foreach (var segment in path)
            {
                if (node is not YamlMappingNode map)
                {
                    return null;
                }

                YamlNode? next = null;
                foreach (var entry in map.Children)
                {
                    if (entry.Key is YamlScalarNode keyNode && string.Equals(keyNode.Value, segment, StringComparison.Ordinal))
                    {
                        next = entry.Value;
                        break;
                    }
                }

                if (next is null)
                {
                    return null;
                }

                node = next;
            }

            return node;
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException)
        {
            return null;
        }
    }
}
