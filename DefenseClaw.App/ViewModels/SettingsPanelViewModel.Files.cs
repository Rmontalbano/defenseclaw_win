using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Settings → Connection → Files (CUST-386): the installation's files as the Mac's six roles, in its order — Config, Data, Environment, Audit DB,
/// Virtual environment, Gateway log (<c>ConnectionSettings.pathRow</c> in <c>AppSettingsView.swift</c>) — every path from what the app resolved at
/// start (<see cref="DefenseClawPaths"/> over the selected <see cref="InstallationContext"/>), never a fixed or POSIX path. Each row says where its
/// path came from and offers Copy path and Open folder; the virtual environment is a role only the installer script's layout has, so on a Setup
/// install the row says it is not there and why, and offers neither.
/// <para>
/// Beside it, and kept apart on purpose, <b>Companion app files</b>: this app's own settings file and folder under <c>%LOCALAPPDATA%</c>, which
/// are not DefenseClaw's install, data, logs or backups. Nothing here reads a file's contents (the .env file and the token never are), and
/// nothing moves or renames a folder.
/// </para>
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    /// <summary>Config, Data, Environment, Audit DB, Virtual environment, Gateway log: where DefenseClaw keeps them, each with Copy and Open folder.</summary>
    public ObservableCollection<SettingsPathRow> Files { get; }

    /// <summary>This app's own settings file and folder: separate from DefenseClaw's files above.</summary>
    public ObservableCollection<SettingsPathRow> CompanionFiles { get; }

    private SettingsPathRow? _venvRow;

    private IEnumerable<SettingsPathRow> BuildFiles()
    {
        var paths = Services.Paths;
        yield return NewRow(MessageCard.Files, "Config", "config.yaml", paths.ConfigFilePath, isDirectory: false, ConfigSource(paths));
        yield return NewRow(MessageCard.Files, "Data", "data directory", paths.DataDirectory, isDirectory: true, paths.DataDirectoryOrigin.Description);
        yield return NewRow(MessageCard.Files, "Environment", ".env file", paths.EnvFilePath, isDirectory: false, "In the data directory. This page shows where it is and never opens it.");
        yield return NewRow(MessageCard.Files, "Audit DB", "audit database", paths.AuditDatabasePath, isDirectory: false, "In the data directory: what Alerts, Audit and Logs read.");

        // The installer script's layout keeps its Python in <data>\.venv; the Setup layout runs its own beside the program folder (CUST-329, SG15).
        _venvRow = NewRow(MessageCard.Files, "Virtual environment", "virtual environment", VenvDirectory(paths), isDirectory: true, note: null);
        ShowVenv(_venvRow, paths, venvExists: File.Exists(paths.VenvPythonPath), setupPythonExists: File.Exists(paths.SetupPythonPath));
        yield return _venvRow;

        yield return NewRow(MessageCard.Files, "Gateway log", "gateway log", paths.GatewayLogPath, isDirectory: false, "In the data directory, where the gateway writes it.");
    }

    private IEnumerable<SettingsPathRow> BuildCompanionFiles()
    {
        var settingsFile = Services.Settings.FilePath;
        yield return NewRow(
            MessageCard.Companion,
            "Settings file",
            "settings file",
            settingsFile,
            isDirectory: false,
            "This app's preferences: every switch on this page, and the appearance. Not DefenseClaw's configuration.");

        if (Path.GetDirectoryName(settingsFile) is { Length: > 0 } folder)
        {
            yield return NewRow(
                MessageCard.Companion,
                "App folder",
                "app folder",
                folder,
                isDirectory: true,
                "This app's own folder: its settings, caches, logs and downloaded updates. Separate from DefenseClaw's installation, data and logs.");
        }
    }

    /// <summary>Where config.yaml came from: <c>DEFENSECLAW_CONFIG</c>, the managed layout, or the data directory.</summary>
    private static string ConfigSource(DefenseClawPaths paths)
    {
        var installation = paths.Installation;
        if (installation.Source == InstallationSource.EnvironmentConfig)
        {
            return $"From {InstallationContext.ConfigVariable}, which the CLI reads ahead of the data directory.";
        }

        var folder = Path.GetDirectoryName(paths.ConfigFilePath);
        return folder is not null && string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(paths.DataDirectory), StringComparison.OrdinalIgnoreCase)
            ? "In the data directory."
            : $"Chosen by the installation selection ({installation.SourceLabel}).";
    }

    /// <summary><c>&lt;data directory&gt;\.venv</c>: the folder that holds <see cref="DefenseClawPaths.VenvPythonPath"/>.</summary>
    private static string VenvDirectory(DefenseClawPaths paths) =>
        Path.GetDirectoryName(Path.GetDirectoryName(paths.VenvPythonPath)) ?? Path.Combine(paths.DataDirectory, ".venv");

    /// <summary>The virtual environment row as the disk has it: there (and whether it is the Python DefenseClaw runs), or not on this installation.</summary>
    private static void ShowVenv(SettingsPathRow row, DefenseClawPaths paths, bool venvExists, bool setupPythonExists)
    {
        row.IsAvailable = venvExists;
        row.Note = venvExists && setupPythonExists
            ? "The installer script's .venv under the data directory. The Setup install's own Python is the one DefenseClaw runs."
            : "The installer script's .venv under the data directory: the Python DefenseClaw runs.";
        row.UnavailableNote = setupPythonExists
            ? $"This installation has no virtual environment: DefenseClaw runs the Setup install's own Python, {paths.SetupPythonPath}."
            : "No .venv under the data directory, and no Setup runtime either.";
    }

    private SettingsPathRow NewRow(MessageCard card, string label, string noun, string path, bool isDirectory, string? note)
    {
        SettingsPathRow? row = null;
        row = new SettingsPathRow(
            label,
            noun,
            path,
            isDirectory,
            note,
            new RelayCommand(() => CopyText(card, row!.FullPath, $"Copied the {noun} path."), () => row!.IsAvailable),
            new AsyncRelayCommand(() => OpenFolderAsync(card, row!), () => row!.IsAvailable));
        return row;
    }

    /// <summary>Looks at the disk, off the UI thread: whether each file is there, and whether this installation has a virtual environment.</summary>
    private async Task RefreshFilesAsync()
    {
        var paths = Services.Paths;
        var rows = Files.Concat(CompanionFiles).ToArray();
        var (exists, venv, setupPython) = await Task.Run(() => (
            rows.Select(row => row.IsDirectory ? Directory.Exists(row.FullPath) : File.Exists(row.FullPath)).ToArray(),
            File.Exists(paths.VenvPythonPath),
            File.Exists(paths.SetupPythonPath))).ConfigureAwait(true);

        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].Exists = exists[i];
        }

        if (_venvRow is not null)
        {
            ShowVenv(_venvRow, paths, venv, setupPython);
        }
    }

    private void CopyText(MessageCard card, string text, string done)
    {
        try
        {
            _platform.CopyText(text);
            Say(card, done);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holds the clipboard: say so rather than pretend.
            Say(card, "Could not copy: another program is holding the clipboard. Try again.");
        }
    }

    /// <summary>
    /// Opens the row's folder in Explorer: the folder itself for a directory role; for a file, the file selected in its folder, or the folder when
    /// the file is not there (yet). Decided off the UI thread (a path under a network home can be slow to answer).
    /// </summary>
    private async Task OpenFolderAsync(MessageCard card, SettingsPathRow row)
    {
        if (!row.IsAvailable)
        {
            return;
        }

        var (target, select) = await Task.Run(() => ResolveRevealTarget(row.FullPath, row.IsDirectory)).ConfigureAwait(true);
        if (target is null)
        {
            Say(card, $"Could not open the {row.Noun}'s folder: it does not exist.");
            return;
        }

        Say(card, _platform.Reveal(target, select)
            ? $"Opened the {row.Noun}'s folder in Explorer."
            : "Could not start Explorer.");
    }

    /// <summary>What to hand Explorer for <paramref name="path"/>: the file to select, else the folder to open; null when neither exists.</summary>
    internal static (string? Target, bool SelectFile) ResolveRevealTarget(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return Directory.Exists(path) ? (path, false) : (null, false);
        }

        if (File.Exists(path))
        {
            return (path, true);
        }

        var parent = Path.GetDirectoryName(path);
        return parent is { Length: > 0 } && Directory.Exists(parent) ? (parent, false) : (null, false);
    }
}
