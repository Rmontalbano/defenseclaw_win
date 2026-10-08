using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Settings -> Audit archive (CUST-299): the optional path of an archived <c>audit.db</c> the Audit panel can show beside the live
/// one (its Live | Archive switch). The path is checked before it is saved (<see cref="AuditArchive.InspectAsync"/>: it exists, is
/// SQLite, its audit tables read, and it is <b>not</b> inside the live DefenseClaw folder); a path that fails is refused with the
/// reason and nothing is saved. The check opens the file read-only and immutable: it is never written, and no sidecar file appears.
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    /// <summary>The path as typed.</summary>
    [ObservableProperty]
    private string _archivePathText = string.Empty;

    /// <summary>Why the path was refused, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchiveProblem))]
    private string _archiveProblem = string.Empty;

    /// <summary>What the last Apply did.</summary>
    [ObservableProperty]
    private string _archiveMessage = string.Empty;

    /// <summary>The archive saved in the settings, or empty; what Clear is enabled by.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearArchiveCommand))]
    private string _savedArchivePath = string.Empty;

    public bool HasArchiveProblem => ArchiveProblem.Length > 0;

    /// <summary>The most recent Apply (a finished one once it has settled); tests await it.</summary>
    internal Task LastArchiveApply { get; private set; } = Task.CompletedTask;

    private void LoadArchiveFromSettings()
    {
        var path = Services.Settings.Current.Archive.Path ?? string.Empty;
        Sync(() =>
        {
            ArchivePathText = path;
            SavedArchivePath = path;
        });
    }

    [RelayCommand]
    private void BrowseArchive()
    {
        var current = ArchivePathText.Trim();
        var start = current.Length > 0 ? Path.GetDirectoryName(current) : null;
        if (_platform.PickArchiveDatabase(start) is { } picked)
        {
            ArchivePathText = picked;
        }
    }

    /// <summary>Checks the typed path and, if it passes, saves it as <c>archive.path</c>. A refused path says why and saves nothing.</summary>
    [RelayCommand]
    private Task ApplyArchiveAsync() => LastArchiveApply = ApplyArchiveCoreAsync();

    private async Task ApplyArchiveCoreAsync()
    {
        ArchiveMessage = string.Empty;
        var typed = ArchivePathText;
        if (string.IsNullOrWhiteSpace(typed))
        {
            ArchiveProblem = "Choose the archived audit database file, or use Clear to remove the archive.";
            return;
        }

        ArchiveProblem = string.Empty;
        ArchiveMessage = "Checking the archive…";
        var dataDirectory = Services.Paths.DataDirectory;
        var check = await Task.Run(() => AuditArchive.InspectAsync(typed, dataDirectory));

        if (!check.IsUsable)
        {
            ArchiveMessage = string.Empty;
            ArchiveProblem = check.Problem ?? "The archive could not be read.";
            return;
        }

        // The full path, so a relative-looking spelling is not what gets stored.
        Write(settings => settings with { Archive = settings.Archive with { Path = check.FullPath } });
        LoadArchiveFromSettings();
        ArchiveMessage = check.Newest is { } newest
            ? "Saved. The Audit panel now has a Live | Archive switch. Newest archived event: " +
              newest.ToLocalTime().ToString("f", CultureInfo.CurrentCulture) + "."
            : "Saved. The archive holds no events.";
    }

    [RelayCommand(CanExecute = nameof(HasSavedArchive))]
    private void ClearArchive()
    {
        ArchiveProblem = string.Empty;
        Write(settings => settings with { Archive = settings.Archive with { Path = null } });
        LoadArchiveFromSettings();
        ArchiveMessage = "The archive is no longer used. The file itself was not touched.";
    }

    private bool HasSavedArchive() => SavedArchivePath.Length > 0;
}
