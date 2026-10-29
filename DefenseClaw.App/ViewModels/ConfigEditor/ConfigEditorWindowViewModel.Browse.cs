using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>One setting the cross-section search found: the section it is in, and the field itself (the same object the form shows).</summary>
/// <param name="SectionName">The display name of the top-level section the field belongs to.</param>
/// <param name="Field">The field; editing it here is editing it in the form.</param>
public sealed record ConfigFieldMatch(string SectionName, FormField Field)
{
    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() => $"{Field.DisplayName}, in {SectionName}";
}

/// <summary>
/// The Mac's editor layout over this editor (CUST-385; <c>SetupView.swift</c> <c>ConfigEditorView</c>): a search across every section, the
/// label that says where the sections came from, and Revert. It is the same draft whichever view shows it (the Setup panel's inline editor and
/// the detached window share one instance, see <c>ConfigEditorSession</c>), so nothing here is per view.
/// <para>
/// <b>Where the sections come from.</b> The Mac dumps the TUI's own section catalog through the runtime's Python; this editor builds its
/// sections from the installed runtime's own view of the file (<c>defenseclaw config show --source</c>), which lists every key config.yaml sets.
/// Either way the count is the runtime's answer, never a number written down here, and a runtime upgrade that adds settings shows them without
/// an app update. Because the source view is the whole file, there is no "uncatalogued" remainder to list.
/// </para>
/// </summary>
public sealed partial class ConfigEditorWindowViewModel
{
    /// <summary>The text of the setting search. Empty shows the section list and the form; anything else shows the matching settings of every section.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFieldSearch))]
    [NotifyPropertyChangedFor(nameof(ShowSectionBrowser))]
    private string _fieldSearch = string.Empty;

    /// <summary>The settings <see cref="FieldSearch"/> matches, in form order (label, dotted key or hint, without case).</summary>
    public ObservableCollection<ConfigFieldMatch> SearchMatches { get; } = new();

    /// <summary>True while the search box has text: the form gives way to <see cref="SearchMatches"/>.</summary>
    public bool HasFieldSearch => FieldSearch.Trim().Length > 0;

    /// <summary>The section list and the form are on screen (no search).</summary>
    public bool ShowSectionBrowser => !HasFieldSearch;

    /// <summary>The search found nothing: the results area says so instead of staying blank.</summary>
    public bool ShowNoSearchMatches => HasFieldSearch && SearchMatches.Count == 0;

    /// <summary>"No settings match “x”."</summary>
    public string NoSearchMatchesText => string.Format(CultureInfo.CurrentCulture, "No settings match “{0}”.", FieldSearch.Trim());

    /// <summary>
    /// True once something asked for the first load (<see cref="LoadAsync"/>). The views share this view-model and each asks on the way in; only
    /// the first one reads the file, so a second view never replaces the draft the first one holds.
    /// </summary>
    internal bool HasLoadStarted { get; private set; }

    /// <summary>True while a save / discard / cancel question is open or being acted on: an exit that arrives then is told the editor is busy.</summary>
    internal bool IsAskingAboutUnsavedChanges => _resolvingUnsaved;

    /// <summary>True while this view-model reacts to the shell's "config.yaml changed" notice (one subscription at most).</summary>
    internal bool IsWatchingDiskChanges => _watchingDisk;

    /// <summary>
    /// Where the section list comes from, in a few words, beside Reload: <c>runtime 1.0.4 · 9 sections</c>. The count is the runtime's answer for this
    /// file; the version is the probed runtime's.
    /// </summary>
    public string CatalogLabel
    {
        get
        {
            if (IsLoading)
            {
                return "reading config.yaml…";
            }

            if (Sections.Count == 0)
            {
                return "runtime view unavailable";
            }

            var version = _services.Runtime.Current.Identity?.Version;
            var runtime = string.IsNullOrWhiteSpace(version) ? "runtime" : "runtime " + version;
            return string.Format(
                CultureInfo.CurrentCulture,
                "{0} · {1} section{2}",
                runtime,
                Sections.Count,
                Sections.Count == 1 ? string.Empty : "s");
        }
    }

    /// <summary>The longer form of <see cref="CatalogLabel"/>, for its tooltip.</summary>
    public string CatalogToolTip =>
        "The sections are the ones the installed DefenseClaw reports for config.yaml (defenseclaw config show --source): every setting the file sets, " +
        "secrets masked. Settings a runtime upgrade adds appear here without an app update. Reload reads the file and the runtime's view again.";

    /// <summary>Clears the setting search (the search box's Clear, and Esc in it).</summary>
    [RelayCommand]
    private void ClearFieldSearch() => FieldSearch = string.Empty;

    /// <summary>
    /// Revert: drops every unsaved edit and reads config.yaml again - the operator just asked for exactly that, so there is no second question. A FORM
    /// value held back for its error goes with the rest. Nothing is written.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private Task RevertAsync() => ReloadCoreAsync();

    private bool CanRevert() => HasUnsavedChanges && !IsSaving && !IsLoading && !_resolvingUnsaved;

    /// <summary>
    /// Stops reacting to the shell's "config.yaml changed" notice; <see cref="WatchDiskChanges"/> starts again. The shared session watches only while a
    /// view is on screen (it catches up with <see cref="HandleDiskChangeAsync"/> when one comes back), so a hidden editor never re-reads the runtime for nobody.
    /// </summary>
    internal void StopWatchingDiskChanges()
    {
        if (!_watchingDisk)
        {
            return;
        }

        _watchingDisk = false;
        _services.ConfigReloaded -= OnServicesConfigReloaded;
    }

    partial void OnFieldSearchChanged(string value) => RefreshSearchMatches();

    private void OnSectionsChanged()
    {
        OnPropertyChanged(nameof(CatalogLabel));
        RefreshSearchMatches();
    }

    /// <summary>The one place the browse state listens to the rest of the view-model (see the constructor).</summary>
    private void OnBrowseStateChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(IsLoading):
                OnPropertyChanged(nameof(CatalogLabel));
                RevertCommand.NotifyCanExecuteChanged();
                break;
            case nameof(IsSaving):
            case nameof(IsRawModified):
            case nameof(HasUnsavedChanges):
            case nameof(HasBlockingFieldErrors):
                RevertCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void RefreshSearchMatches()
    {
        var needle = FieldSearch.Trim();
        var matches = new List<ConfigFieldMatch>();
        if (needle.Length > 0)
        {
            foreach (var section in Sections)
            {
                Collect(section, section.DisplayName, needle, matches);
            }
        }

        SearchMatches.Clear();
        foreach (var match in matches)
        {
            SearchMatches.Add(match);
        }

        OnPropertyChanged(nameof(ShowNoSearchMatches));
        OnPropertyChanged(nameof(NoSearchMatchesText));
    }

    private static void Collect(FormGroup group, string sectionName, string needle, List<ConfigFieldMatch> into)
    {
        foreach (var field in group.Fields)
        {
            if (Matches(field.DisplayName, needle) || Matches(field.Path, needle) || Matches(field.Hint, needle))
            {
                into.Add(new ConfigFieldMatch(sectionName, field));
            }
        }

        foreach (var nested in group.SubGroups)
        {
            Collect(nested, sectionName, needle, into);
        }
    }

    private static bool Matches(string? text, string needle) =>
        text is not null && text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
