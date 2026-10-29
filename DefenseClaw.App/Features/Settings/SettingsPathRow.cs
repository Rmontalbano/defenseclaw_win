using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One line of Settings → Connection → Files (and of Companion app files): the role (Config, Data, Environment, Audit DB, Virtual environment,
/// Gateway log, after the Mac's <c>pathRow</c>s), where the app resolved it, where that location came from, and the two things to do with it
/// (copy the path, open its folder). A role this installation does not have (no virtual environment on a Setup install) is
/// <see cref="IsAvailable"/> false: it says why and offers neither action. Display only: nothing here opens or reads the file.
/// </summary>
public sealed partial class SettingsPathRow : ObservableObject
{
    /// <summary>What the path box shows for a role this installation does not have.</summary>
    public const string UnavailablePathText = "Not on this installation";

    /// <param name="label">The role, as the row's caption says it ("Config", "Audit DB").</param>
    /// <param name="noun">The role in a sentence ("config.yaml", "audit database"): what the buttons' names and the messages say.</param>
    /// <param name="fullPath">The absolute path, as the app resolved it.</param>
    /// <param name="isDirectory">True for a folder: "open folder" opens it rather than the folder it is in.</param>
    /// <param name="note">Where the location came from; null when there is nothing to say.</param>
    /// <param name="copy">Copy path.</param>
    /// <param name="openFolder">Open folder.</param>
    public SettingsPathRow(string label, string noun, string fullPath, bool isDirectory, string? note, IRelayCommand copy, IAsyncRelayCommand openFolder)
    {
        Label = label;
        Noun = noun;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        _note = note;
        CopyCommand = copy;
        OpenFolderCommand = openFolder;
    }

    /// <summary>The role: "Config", "Data", "Environment", "Audit DB", "Virtual environment", "Gateway log", "Settings file", "App folder".</summary>
    public string Label { get; }

    /// <summary>The role in a sentence: "config.yaml", "data directory", ".env file".</summary>
    public string Noun { get; }

    /// <summary>The absolute path, as the app resolved it.</summary>
    public string FullPath { get; }

    /// <summary>True for a folder: "open folder" opens it rather than the folder it is in.</summary>
    public bool IsDirectory { get; }

    /// <summary>Where the location came from ("From DEFENSECLAW_HOME …"); null when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string? _note;

    /// <summary>What the row says while <see cref="IsAvailable"/> is false (why this installation has no such role); null for a role every installation has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string? _unavailableNote;

    /// <summary>False when the last look found nothing at the path (the gateway log of a gateway that never ran); true until a look says otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private bool _exists = true;

    /// <summary>
    /// False when this installation does not have the role at all (a virtual environment on a Setup install, which runs its own Python): the
    /// path box says so, Copy and Open folder are off, and the line under it says why. True for every role that always applies.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyPropertyChangedFor(nameof(DisplayPath))]
    [NotifyPropertyChangedFor(nameof(PathToolTip))]
    private bool _isAvailable = true;

    partial void OnIsAvailableChanged(bool value)
    {
        CopyCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the path box shows: the path, or <see cref="UnavailablePathText"/>.</summary>
    public string DisplayPath => IsAvailable ? FullPath : UnavailablePathText;

    /// <summary>The path in full on hover (a long one wraps in the box); null for an unavailable role.</summary>
    public string? PathToolTip => IsAvailable ? FullPath : null;

    /// <summary>
    /// The line under the path: where the location came from, and "Not found on disk (yet)" when nothing is there; for an unavailable role, why
    /// it is not there. Empty when there is nothing to say.
    /// </summary>
    public string DetailText => IsAvailable
        ? string.Join(' ', new[] { Note, Exists ? null : "Not found on disk (yet)." }.Where(part => !string.IsNullOrWhiteSpace(part)))
        : UnavailableNote ?? string.Empty;

    public IRelayCommand CopyCommand { get; }

    public IAsyncRelayCommand OpenFolderCommand { get; }

    public string CopyAutomationName => $"Copy the {Noun} path";

    public string OpenFolderAutomationName => IsDirectory ? $"Open the {Noun} in Explorer" : $"Show the {Noun} in Explorer";

    /// <summary>What a screen reader reads for the row: the role and the path, not the type name.</summary>
    public override string ToString() => $"{Label}: {DisplayPath}";
}
