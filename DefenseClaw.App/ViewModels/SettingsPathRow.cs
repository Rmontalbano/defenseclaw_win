using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One line of Settings → Connection's file list: what the file is, where the app reads it, where that location came from (only the data
/// directory has a story), and the two things to do with it (copy the path, open its folder). Display only: nothing here opens the file.
/// </summary>
public sealed partial class SettingsPathRow : ObservableObject
{
    public SettingsPathRow(string label, string fullPath, bool isDirectory, string? note, IRelayCommand copy, IAsyncRelayCommand openFolder)
    {
        Label = label;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Note = note;
        CopyCommand = copy;
        OpenFolderCommand = openFolder;
    }

    /// <summary>"Config file", "Data directory", ".env file", "Audit database", "Gateway log".</summary>
    public string Label { get; }

    /// <summary>The absolute path, as the app resolved it.</summary>
    public string FullPath { get; }

    /// <summary>True for the data directory: "open folder" opens it rather than the folder it is in.</summary>
    public bool IsDirectory { get; }

    /// <summary>Where the location came from ("From DEFENSECLAW_HOME …"); null when there is nothing to say.</summary>
    public string? Note { get; }

    /// <summary>False when the last look found nothing at the path (the gateway log of a gateway that never ran); true until a look says otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private bool _exists = true;

    /// <summary>The line under the path: where the location came from, and "Not found on disk (yet)" when nothing is there; empty when there is nothing to say.</summary>
    public string DetailText => string.Join(' ', new[] { Note, Exists ? null : "Not found on disk (yet)." }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public IRelayCommand CopyCommand { get; }

    public IAsyncRelayCommand OpenFolderCommand { get; }

    public string CopyAutomationName => $"Copy the {Label} path";

    public string OpenFolderAutomationName => IsDirectory ? $"Open the {Label} in Explorer" : $"Show the {Label} in Explorer";

    /// <summary>What a screen reader reads for the row: the label and the path, not the type name.</summary>
    public override string ToString() => $"{Label}: {FullPath}";
}
