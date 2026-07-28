using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// </summary>
public sealed partial class ConfigEditorWindowViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DefenseClawPaths _paths;
    private readonly CliRunner _cli;
    private readonly ConfigSaveService _saveService;

    private FileSignature _loadedSignature;
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

    /// <summary>Set when config.yaml exists but failed to parse — RAW still shows it verbatim; FORM is unavailable.</summary>
    [ObservableProperty]
    private string? _parseError;

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

    public bool ShowSaveSuccessBanner => ShowSaveResultBanner && !SaveResultIsError;

    public bool ShowSaveErrorBanner => ShowSaveResultBanner && SaveResultIsError;

    public bool ShowRestoreAction => SaveResultIsError && LastBackupPath is not null;

    partial void OnHasSecretReferencesChanged(bool value) => OnPropertyChanged(nameof(WindowTitle));

    partial void OnShowSaveResultBannerChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveSuccessBanner));
        OnPropertyChanged(nameof(ShowSaveErrorBanner));
        OnPropertyChanged(nameof(ShowRestoreAction));
    }

    partial void OnSaveResultIsErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSaveSuccessBanner));
        OnPropertyChanged(nameof(ShowSaveErrorBanner));
        OnPropertyChanged(nameof(ShowRestoreAction));
    }

    partial void OnLastBackupPathChanged(string? value) => OnPropertyChanged(nameof(ShowRestoreAction));

    partial void OnRawTextChanged(string value)
    {
        IsRawModified = true;

        if (_suppressRawChangeTracking)
        {
            _suppressRawChangeTracking = false;
            return;
        }

        _needsFormRebuild = true;
    }

    /// <summary>Loads config.yaml and fetches the effective configuration once. Call after the window is constructed.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            string rawText;
            try
            {
                rawText = File.Exists(_paths.ConfigFilePath)
                    ? await File.ReadAllTextAsync(_paths.ConfigFilePath, cancellationToken).ConfigureAwait(true)
                    : string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rawText = string.Empty;
                ParseError = $"config.yaml could not be read: {ex.Message}";
            }

            _loadedSignature = _saveService.CaptureSignature();

            _suppressRawChangeTracking = true;
            RawText = rawText;
            IsRawModified = false;

            try
            {
                _document = ConfigStore.Parse(rawText, _paths.ConfigFilePath);
                ParseError = null;
            }
            catch (ConfigParseException ex)
            {
                _document = ConfigStore.Parse(string.Empty);
                ParseError = ex.Message;
            }

            HasSecretReferences = SensitiveKeyClassifier.ContainsSensitiveReferences(rawText);

            await FetchEffectiveConfigAsync(cancellationToken).ConfigureAwait(true);

            if (ParseError is null && _effectiveYaml.Length > 0)
            {
                RebuildForm();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>The FORM tab calls this when it becomes visible; a no-op unless RAW changed underneath it.</summary>
    public void NotifyFormTabSelected()
    {
        if (!_needsFormRebuild || ParseError is not null || _effectiveYaml.Length == 0)
        {
            return;
        }

        try
        {
            _document = ConfigStore.Parse(RawText, _paths.ConfigFilePath);
            ParseError = null;
            RebuildForm();
        }
        catch (ConfigParseException ex)
        {
            ParseError = ex.Message;
        }

        _needsFormRebuild = false;
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
            var outcome = await _saveService.SaveAsync(RawText, _loadedSignature).ConfigureAwait(true);

            SaveResultMessage = outcome.Message;
            SaveResultIsError = !outcome.Success;
            ShowSaveResultBanner = true;
            LastBackupPath = outcome.BackupPath;
            LastBackupSha256 = outcome.BackupSha256Hex;
            ShowDriftBanner = outcome.Stage == SaveStage.DriftDetected;

            if (outcome.Success)
            {
                IsRawModified = false;
                _loadedSignature = _saveService.CaptureSignature();
                HasSecretReferences = SensitiveKeyClassifier.ContainsSensitiveReferences(RawText);
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving && !IsLoading;

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

    private void OnFieldCommitted(FormField field)
    {
        FieldErrorMessage = null;
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

        PublishPatchedSection(sectionName, patchedSection);
    }

    private void OnListCommitted(FormListField list)
    {
        FieldErrorMessage = null;
        var pathSegments = list.Path.Split('.');
        var sectionName = pathSegments[0];
        var sectionText = _document.SectionText(sectionName);
        if (sectionText is null)
        {
            FieldErrorMessage = $"Could not apply '{list.DisplayName}': its section is not present in config.yaml.";
            return;
        }

        var patchedSection = YamlSectionEditor.TrySetList(sectionText, pathSegments, list.Items);
        if (patchedSection is null)
        {
            FieldErrorMessage = $"Could not apply '{list.DisplayName}' — edit it in the RAW tab instead.";
            return;
        }

        PublishPatchedSection(sectionName, patchedSection);
    }

    private void PublishPatchedSection(string sectionName, string patchedSectionText)
    {
        var newRawText = _document.WithSectionReplaced(sectionName, patchedSectionText);
        _document = ConfigStore.Parse(newRawText, _paths.ConfigFilePath);

        _suppressRawChangeTracking = true;
        RawText = newRawText;

        // The FORM tree already reflects the edit locally (the field/list VM applied it
        // to itself); only the raw text needed republishing. Clear the rebuild flag so
        // switching tabs right after a FORM edit does not immediately rebuild over it.
        _needsFormRebuild = false;
    }
}
