using Microsoft.Win32;

namespace DefenseClaw.App.Services;

/// <summary>
/// Start-with-Windows via the per-user Run key — no admin rights, no scheduled task,
/// removable from Task Manager's Startup tab like any well-behaved tray app.
/// </summary>
internal static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DefenseClawWin";

    /// <summary>The value written: this exe, minimized to the tray.</summary>
    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>Flips the setting; returns the new state.</summary>
    public static bool Toggle()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(ValueName) is string)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return false;
        }

        key.SetValue(ValueName, Command);
        return true;
    }
}
