using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The pieces of Windows the Settings page touches and a test must not: the Run registry key, the file picker, the clipboard, Explorer and
/// the Updates window. Each defaults to the real thing (<see cref="Real"/>); a test hands in its own so nothing it does can write the
/// operator's Run key, open a dialog or start a process.
/// </summary>
internal sealed class SettingsPlatform
{
    /// <summary>The real thing: what the running app uses.</summary>
    public static SettingsPlatform Real { get; } = new();

    /// <summary>Whether Windows will start the app at sign-in (<see cref="AutostartManager.IsEnabled"/>).</summary>
    public Func<bool> IsAutostartEnabled { get; init; } = () => AutostartManager.IsEnabled;

    /// <summary>Flips Start with Windows, guarded: a registry that refuses comes back as a result with the reason, never an exception (<see cref="AutostartManager.Toggle()"/>).</summary>
    public Func<AutostartToggleResult> ToggleAutostart { get; init; } = () => AutostartManager.Toggle();

    /// <summary>
    /// The file picker for <c>defenseclaw.exe</c>: the folder to start in (or null), the chosen path (or null when cancelled).
    /// </summary>
    public Func<string?, string?> PickCliExecutable { get; init; } = PickWithDialog;

    /// <summary>Puts text on the clipboard (retrying briefly while another program holds it); a failure is reported by <see cref="Views.Controls.DcClipboard"/>, so this never throws.</summary>
    public Action<string> CopyText { get; init; } = text => Views.Controls.DcClipboard.TrySetText(text);

    /// <summary>Shows a file in Explorer (selected, when the flag is set) or opens a folder; false when Explorer could not be started.</summary>
    public Func<string, bool, bool> Reveal { get; init; } = RevealInExplorer;

    /// <summary>Shows the one-time review of <c>defenseclaw-gateway start</c> when the automatic start is switched on; true only if the operator confirmed it.</summary>
    public Func<CommandReview, bool> ConfirmGatewayAutoStart { get; init; } =
        review => Views.Shell.GatewayActionDialog.Confirm(Application.Current?.MainWindow, review);

    /// <summary>Opens the Updates window (or brings it forward).</summary>
    public Action<AppServices> OpenUpdates { get; init; } = services => Views.Updates.UpdatesWindow.Show(services);

    private static string? PickWithDialog(string? startDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose defenseclaw.exe",
            Filter = "defenseclaw.exe|defenseclaw.exe|Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
        };

        if (!string.IsNullOrEmpty(startDirectory))
        {
            dialog.InitialDirectory = startDirectory;
        }

        var owner = Application.Current?.MainWindow;
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// <c>explorer.exe /select,&lt;file&gt;</c> or <c>explorer.exe &lt;folder&gt;</c>. The caller has checked that a folder is a folder: Explorer
    /// would open (run) a file it is handed without <c>/select</c>.
    /// </summary>
    private static bool RevealInExplorer(string path, bool selectFile)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (selectFile)
            {
                start.ArgumentList.Add("/select,");
            }

            start.ArgumentList.Add(Path.GetFullPath(path));
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
