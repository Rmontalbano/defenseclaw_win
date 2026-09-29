using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace DefenseClaw.App.Services;

/// <summary>
/// Start-with-Windows via the per-user Run key — no admin rights, no scheduled task,
/// removable from Task Manager's Startup tab like any well-behaved tray app.
/// <para>
/// <b>Two registry facts decide whether it will actually start.</b> The Run value has to be
/// present <i>and</i> Explorer must not have it marked disabled: Task Manager's Startup tab
/// and Settings → Apps → Startup never touch the Run value, they write a flag next to it under
/// <c>Explorer\StartupApproved\Run</c>, and Windows skips a Run entry whose flag says
/// disabled. Treating "the Run value exists" as "autostart is on" therefore reports a checked
/// menu item for an app that will not start.
/// </para>
/// <para>
/// <b>The StartupApproved format</b> (undocumented by Microsoft; this is the convention Autoruns
/// and every startup manager rely on): a <c>REG_BINARY</c> of 12 bytes named after the Run
/// value. The low bit of byte 0 is the disabled flag — <c>0x02</c>/<c>0x06</c> enabled,
/// <c>0x03</c>/<c>0x07</c> disabled — and bytes 4-11 are the FILETIME of when it was disabled.
/// This class <b>only reads that byte</b>. It never writes the blob: to undo a disable it
/// deletes the value, and an absent value is the default, enabled. That keeps the write path
/// independent of the format being exactly as described.
/// </para>
/// </summary>
internal static class AutostartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "DefenseClawWin";

    /// <summary>The value written: this exe, minimized to the tray.</summary>
    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    /// <summary>
    /// True when Windows will start the app at sign-in: a Run value is present and the user
    /// has not disabled it in Task Manager / Settings.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string { Length: > 0 } && !IsDisabledByUser();
        }
    }

    /// <summary>Flips the setting; returns the new state.</summary>
    public static bool Toggle()
    {
        if (IsEnabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return false;
        }

        // Also the path taken when a Run value exists but Task Manager disabled it: turning it
        // on here must actually turn it on, so the disable flag is cleared along with the write.
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            key.SetValue(ValueName, Command);
        }

        ClearUserDisable();
        return true;
    }

    /// <summary>
    /// Rewrites the Run value to launch this exe when it is enabled but stale — it points at an
    /// executable that no longer exists, which is what an app that was moved or reinstalled
    /// elsewhere leaves behind, and which silently stops autostart working while the menu
    /// item still shows a check. Call once at startup.
    /// <para>
    /// <b>Only a value that cannot work is repaired.</b> One that names a <i>different</i> exe
    /// that still exists (an installed copy while a dev build is running, or the reverse) is
    /// left alone: the two would otherwise fight over the value on every launch. A value in a
    /// shape this does not recognize is left alone too. The user's Task Manager disable flag is
    /// never touched here — repairing the path is not re-enabling.
    /// </para>
    /// </summary>
    public static void RepairIfStale()
    {
        if (string.IsNullOrEmpty(Environment.ProcessPath))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string { Length: > 0 } stored ||
                string.Equals(stored, Command, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (QuotedExecutable(stored) is not { } stale || File.Exists(stale))
            {
                return;
            }

            key.SetValue(ValueName, Command);
            Trace.TraceInformation($"autostart: repaired a Run entry that pointed at the missing '{stale}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Best effort: a policy-locked Run key just means the entry stays as it was.
            Trace.TraceWarning($"autostart: could not check or repair the Run entry: {ex.Message}");
        }
    }

    /// <summary>
    /// The exe path from a value shaped like <c>"C:\path\app.exe" args</c> — the form this class
    /// writes. Null for anything else, including an unquoted command line whose exe cannot be
    /// told apart from its arguments.
    /// </summary>
    private static string? QuotedExecutable(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.Length < 3 || trimmed[0] != '"')
        {
            return null;
        }

        var close = trimmed.IndexOf('"', 1);
        return close > 1 ? trimmed[1..close] : null;
    }

    /// <summary>True when Task Manager / Settings has flagged this Run value as disabled.</summary>
    private static bool IsDisabledByUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Unreadable flag: assume the documented default, enabled.
            return false;
        }
    }

    /// <summary>
    /// Removes a disable flag for this Run value, if there is one. Deleting the value — rather
    /// than writing an "enabled" blob — is deliberate; see the type documentation. Best effort:
    /// the Run value is already written by the time this runs.
    /// </summary>
    private static void ClearUserDisable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            if (key?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.TraceWarning($"autostart: could not clear the Task Manager disable flag: {ex.Message}");
        }
    }
}
