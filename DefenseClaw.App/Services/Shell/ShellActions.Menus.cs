using System.Diagnostics;
using System.Text;
using DefenseClaw.App.Services.Updates.SelfUpdate;

namespace DefenseClaw.App.Services;

/// <summary>
/// The menu bar's part of the shell actions (CUST-384): the availability the Commands menu and the palette share for diagnose, copy and export,
/// this app's own update check, and About. Every action here is an existing path: the tray's update check, the runner's activity ring, the
/// diagnose the palette already runs. Reading any of these states runs nothing.
/// </summary>
internal sealed partial class ShellActions
{
    /// <summary>Whether "Diagnose in background" can start now: one diagnosis at a time (the second would only say so in a toast).</summary>
    public (bool Allowed, string? Reason) DiagnoseState =>
        Volatile.Read(ref _diagnosing) == 1 ? (false, "A diagnosis is already running.") : (true, null);

    /// <summary>Whether "Copy last command output" has something to copy: the newest Activity entry, with output.</summary>
    public (bool Allowed, string? Reason) CopyOutputState =>
        LastCommandOutput.Latest(Activity) is { OutputLines.Count: > 0 }
            ? (true, null)
            : (false, "There is no command output to copy yet.");

    /// <summary>Whether "Export last command output" has something to export: the newest Activity entry (its command and outcome, even with no output).</summary>
    public (bool Allowed, string? Reason) ExportOutputState =>
        LastCommandOutput.Latest(Activity) is not null
            ? (true, null)
            : (false, "There is no command output to export yet.");

    // ---- this app's own update check ----

    /// <summary>Test seam: runs this app's update check (default: the tray's "Check for app updates…", which opens the review or says the result in a toast).</summary>
    internal Func<Task>? AppUpdateChecker { get; set; }

    /// <summary>
    /// Whether "Check for app updates…" can run now: not while a check is already running (the Settings button's own rule) or an update is being
    /// prepared, installed or restarted into (its window shows that progress).
    /// </summary>
    public (bool Allowed, string? Reason) AppUpdateCheck
    {
        get
        {
            var update = _services.AppUpdate.ViewModel;
            if (update.IsChecking)
            {
                return (false, "Already checking for app updates.");
            }

            return update.IsBusy
                ? (false, "An app update is being prepared or installed; its window shows the progress.")
                : (true, null);
        }
    }

    /// <summary>"Check for app updates…": the tray's own check of this app's releases (Rmontalbano/defenseclaw_win), not the DefenseClaw runtime's.</summary>
    public void CheckForAppUpdates() => _ = (AppUpdateChecker ?? _tray.CheckAppUpdatesAsync)();

    // ---- About ----

    internal const string AboutTitle = "About DefenseClaw for Windows";

    /// <summary>Test seam: shows About's title and text (default: a WPF-UI message box over the dashboard).</summary>
    internal Action<string, string>? AboutPresenter { get; set; }

    /// <summary>
    /// What About says: this app's version, the runtime the probes found (or why none is known), the docs address, and that the app is a community one.
    /// Read from what the app already knows (<see cref="AppVersion.Running"/>, <see cref="RuntimeService.Current"/>); nothing is probed for it.
    /// </summary>
    public string AboutText
    {
        get
        {
            var runtime = _services.Runtime.Current;
            var text = new StringBuilder()
                .Append("DefenseClaw for Windows ").AppendLine(AppVersion.Running()?.ToString() ?? "(version unknown)")
                .AppendLine("A community Windows companion for Cisco DefenseClaw. Not an official Cisco product.")
                .AppendLine()
                .Append("Runtime: ").AppendLine(runtime.Identity?.Display ?? runtime.UnknownReason ?? "Not detected")
                .Append("Docs: ").AppendLine(DocsUrl)
                .AppendLine()
                .Append("Licensed under the Apache License, Version 2.0.");
            return text.ToString();
        }
    }

    /// <summary>Shows About over the dashboard. Only a message: it has one button, and nothing behind it runs.</summary>
    public void ShowAbout()
    {
        if (AboutPresenter is { } present)
        {
            present(AboutTitle, AboutText);
            return;
        }

        _ = ShowAboutAsync(AboutText);
    }

    private async Task ShowAboutAsync(string text)
    {
        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = AboutTitle,
                Content = text,
                CloseButtonText = "OK",
            };

            if (_owner() is { } owner)
            {
                box.Owner = owner;
                box.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            }

            _ = await box.ShowDialogAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // About must never take the dashboard down: say it in a toast instead.
        catch (Exception ex)
        {
            Trace.TraceWarning($"About could not be shown: {ex.Message}");
            ShowToast(AboutTitle, text);
        }
#pragma warning restore CA1031
    }
}
