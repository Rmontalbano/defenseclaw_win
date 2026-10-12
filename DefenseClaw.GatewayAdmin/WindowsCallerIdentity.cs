using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace DefenseClaw.GatewayAdmin;

/// <summary>
/// Reads who is asking from Windows. The account is the token's, the elevation kind is the token's, and the parent's account is the parent
/// process's token: there is no field a caller can fill in. The profile folder is read from two places (the profile list and the shell's
/// known folder) and must agree.
/// </summary>
internal sealed class WindowsCallerIdentity : ICallerIdentity
{
    private const int ElevationTypeFull = 2;

    public CallerIdentity Read()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value ?? throw new InvalidOperationException("The account has no SID.");
        var token = identity.AccessToken.DangerousGetHandle();

        var elevated = Native.ReadTokenDword(token, Native.TokenElevation) != 0;
        var split = Native.ReadTokenDword(token, Native.TokenElevationType) == ElevationTypeFull;

        var name = identity.Name;
        var userName = name.Contains('\\', StringComparison.Ordinal) ? name[(name.LastIndexOf('\\') + 1)..] : name;

        var environmentNames = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                environmentNames.Add(key);
            }
        }

        return new CallerIdentity(
            userSid,
            userName,
            ReadProfile(userSid),
            elevated,
            split,
            ReadParentUserSid(),
            Environment.ProcessPath ?? throw new InvalidOperationException("The helper's own path is unknown."),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            environmentNames);
    }

    private static string ReadProfile(string sid)
    {
        var known = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string? listed = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid, writable: false);
            if (key?.GetValue("ProfileImagePath") is string raw)
            {
                listed = Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(raw));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            listed = null;
        }

        if (string.IsNullOrWhiteSpace(known) || listed is null || !string.Equals(known, listed, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The profile folder could not be confirmed from the profile list.");
        }

        return known;
    }

    /// <summary>
    /// The account of the process that started the helper. The elevation service starts an elevated process with the requesting process as its
    /// parent, so for a normal prompt this is the app. A parent that has exited, or whose number now belongs to a younger process, is reported as
    /// unknown rather than guessed.
    /// </summary>
    private static string? ReadParentUserSid()
    {
        var info = new Native.ProcessBasicInformation();
        if (Native.NtQueryInformationProcess(Native.GetCurrentProcess(), 0, ref info, Marshal.SizeOf<Native.ProcessBasicInformation>(), out _) != 0)
        {
            return null;
        }

        var parentId = (int)info.InheritedFromUniqueProcessId;
        if (parentId <= 0)
        {
            return null;
        }

        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var self = Process.GetCurrentProcess();
            if (parent.StartTime > self.StartTime)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        // QUERY_INFORMATION as well as the limited right: OpenProcessToken is documented to need the former. An elevated process may open its
        // lower-integrity parent of the same user for it.
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation | 0x0400, false, parentId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!Native.OpenProcessToken(handle, Native.TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                return Native.ReadTokenUserSid(token);
            }
            finally
            {
                _ = Native.CloseHandle(token);
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            _ = Native.CloseHandle(handle);
        }
    }
}
