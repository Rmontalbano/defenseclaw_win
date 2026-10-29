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
/// <b>Where the sections come from.</b> Like the Mac, the runtime's own Setup catalog, dumped through the runtime's Python (CUST-391,
/// <c>ConfigEditorWindowViewModel.Catalog.cs</c>): its sections in its order, and "Other (uncatalogued)" for keys config.yaml sets that it does not
/// describe. Without it (no runtime Python, or the read failed) the built-in layout: one section per top-level key of the runtime's own view of the
/// file (<c>defenseclaw config show --source</c>), which covers every key, so there is no remainder to list. Either way the count is the runtime's
/// answer, never a number written down here, and a runtime upgrade that adds settings shows them without an app update.
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
    /// Where the section list comes from, in a few words, beside Reload (the Mac's <c>catalogSource</c>): <c>runtime catalog 1.0.4 · 24 sections</c> when the
    /// form is laid out in the runtime's own catalog (the count is its sections, without Other, as on the Mac; the version is the probed runtime's), or
    /// <c>built-in catalog · 9 sections</c> when it is the source-driven layout (the tooltip says why). Never a banner.
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

            if (_layoutCatalog is { } catalog)
            {
                var version = _services.Runtime.Current.Identity?.Version;
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "runtime catalog{0} · {1}",
                    string.IsNullOrWhiteSpace(version) ? string.Empty : " " + version,
                    SectionCount(catalog.Sections.Count));
            }

            return "built-in catalog · " + SectionCount(Sections.Count);
        }
    }

    /// <summary>The longer form of <see cref="CatalogLabel"/>, for its tooltip.</summary>
    public string CatalogToolTip =>
        _layoutCatalog is not null
            ? "The sections and fields are the installed runtime's own Setup catalog, read through its Python: settings a runtime upgrade adds appear " +
              "here without an app update. Values are config.yaml's (defenseclaw config show --source, secrets masked); a setting the file does not set " +
              "shows the runtime's value, read-only. Keys the catalog does not describe are under Other (uncatalogued). Reload reads the file and the catalog again."
            : "Built-in catalog: one section per key config.yaml sets, as the installed DefenseClaw reports them (defenseclaw config show --source), secrets " +
              "masked. The runtime's own catalog was not used" + (CatalogUnavailableReason is { Length: > 0 } why ? ": " + why + "." : ".") +
              " Reload tries it again.";

    private static string SectionCount(int count) =>
        string.Format(CultureInfo.CurrentCulture, "{0} section{1}", count, count == 1 ? string.Empty : "s");

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
