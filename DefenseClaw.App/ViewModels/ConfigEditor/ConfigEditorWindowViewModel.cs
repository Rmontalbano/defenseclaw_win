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
/// Orchestrates the whole config editor window: loads config.yaml and the effective
/// configuration once, keeps RAW and FORM in sync through the raw text as the single
/// source of truth, and drives the save pipeline.
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
/// </summary>
public sealed partial class ConfigEditorWindowViewModel : ObservableObject
{
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
    private string _effectiveYaml = string.Empty;
    private bool _suppressRawChangeTracking;
    private bool _needsFormRebuild;

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    private bool _isRawModified;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
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
    private string? _loadError;

    /// <summary>Set when the effective configuration could not be fetched — FORM is unavailable, RAW still works.</summary>
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

    public ConfigEditorWindowViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _paths = services.Paths;
        _cli = services.Cli;
        _saveService = new ConfigSaveService(_paths, _cli);
        _document = ConfigStore.Parse(string.Empty);
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

    public bool ShowSaveErrorBanner => ShowSaveResultBanner && SaveResultIsError;

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

    partial void OnParseErrorChanged(string? value) => OnPropertyChanged(nameof(ShowFormStaleNotice));

    partial void OnRawTextChanged(string value)
    {
        IsRawModified = true;

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

    /// <summary>Loads config.yaml and fetches the effective configuration once. Call after the window is constructed.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            // Signature first, then the read: if the file changes in between, the text is
            // newer than the signature and the next save reports drift (safe) instead of
            // the text being older than the signature and hiding it.
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
                    ? await File.ReadAllTextAsync(_paths.ConfigFilePath, cancellationToken).ConfigureAwait(true)
                    : string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ApplyLoadFailure($"config.yaml could not be read: {ex.Message}");
                return;
            }

            LoadError = null;

            SetRawTextWithoutRebuildFlag(rawText);
            IsRawModified = false;

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

            await FetchEffectiveConfigAsync(cancellationToken).ConfigureAwait(true);

            if (_effectiveYaml.Length == 0)
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

        SetRawTextWithoutRebuildFlag(string.Empty);
        IsRawModified = false;

        _document = ConfigStore.Parse(string.Empty);
        _effectiveYaml = string.Empty;
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
        if (LoadFailed || _effectiveYaml.Length == 0 || !NeedsFormRebuild)
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

    [RelayCommand]
    private async Task ReloadAsync()
    {
        ShowDriftBanner = false;
        ShowSaveResultBanner = false;
        ShowOnDiskPreview = false;
        await LoadAsync().ConfigureAwait(true);
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
    private async Task SaveAsync()
    {
        IsSaving = true;
        ShowSaveResultBanner = false;
        try
        {
            var textToSave = RawText;
            var signatureUsed = _loadedSignature;
            var outcome = await _saveService.SaveAsync(textToSave, signatureUsed).ConfigureAwait(true);

            SaveResultMessage = outcome.Message;
            SaveResultIsError = !outcome.Success;
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
                HasSecretReferences = SensitiveKeyClassifier.ContainsSensitiveReferences(textToSave);
                UnvalidatedSignature = outcome.Success ? null : _loadedSignature;
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
                // Typing that happened while the save was in flight is not saved yet.
                IsRawModified = !string.Equals(RawText, textToSave, StringComparison.Ordinal);
                ClearParseErrorIfParses(textToSave);
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving && !IsLoading && !LoadFailed;

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

    private bool CanRestore() => LastBackupPath is not null;

    private async Task FetchEffectiveConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            var invocation = await _cli
                .RunAsync(new[] { "config", "show", "--effective", "--format", "yaml" }, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            if (invocation.FailureReason is { Length: > 0 } failure)
            {
                FormUnavailableReason = $"Could not run defenseclaw: {failure}";
                _effectiveYaml = string.Empty;
                return;
            }

            if (invocation.ExitCode is not 0)
            {
                var stderr = string.Join(
                    Environment.NewLine,
                    invocation.OutputLines.Where(l => l.Stream == CliStream.StandardError).Select(l => l.Text));
                FormUnavailableReason = $"defenseclaw config show --effective exited {invocation.ExitCode}: {stderr}";
                _effectiveYaml = string.Empty;
                return;
            }

            _effectiveYaml = string.Join(
                Environment.NewLine,
                invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));
            FormUnavailableReason = null;
        }
        catch (CliNotFoundException)
        {
            FormUnavailableReason = "The defenseclaw CLI was not found on PATH. FORM view needs it to read the effective configuration — RAW editing still works.";
            _effectiveYaml = string.Empty;
        }
    }

    private void RebuildForm()
    {
        var result = ConfigFormBuilder.Build(_effectiveYaml, _document, OnFieldCommitted, OnListCommitted);

        Sections.Clear();
        foreach (var section in result.Sections)
        {
            Sections.Add(section);
        }

        FormWarnings = result.Warnings;
        OnPropertyChanged(nameof(FormWarnings));
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

    private void OnFieldCommitted(FormField field)
    {
        if (!CanApplyFormEdit(field.DisplayName))
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
        if (!CanApplyFormEdit(list.DisplayName))
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
    private void PublishPatchedSection(
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
        var expectedSection = patchedSectionText.EndsWith('\n') ? patchedSectionText : patchedSectionText + "\n";
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
