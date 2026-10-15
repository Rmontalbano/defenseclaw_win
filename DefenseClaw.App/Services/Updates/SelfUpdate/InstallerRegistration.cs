using System.IO;
using Microsoft.Win32;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>The per-user Add/Remove Programs entries, behind a seam: tests never touch the real HKCU.</summary>
internal interface IUninstallKeyStore
{
    /// <summary>A string value of <c>HKCU\...\Uninstall\&lt;subKey&gt;</c>, or null when the key or value is absent.</summary>
    string? GetString(string subKey, string name);

    /// <summary>Sets a string value of an existing key. False when the key does not exist or cannot be written.</summary>
    bool SetString(string subKey, string name, string value);
}

internal sealed class RegistryUninstallKeyStore : IUninstallKeyStore
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public string? GetString(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallPath + "\\" + subKey);
        return key?.GetValue(name) as string;
    }

    public bool SetString(string subKey, string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallPath + "\\" + subKey, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(name, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Keeps the Inno installer's Add/Remove Programs <c>DisplayVersion</c> in step with a self-update. Only an entry this installer made for
/// this very folder is touched: the key is the contract's AppId (never Cisco's <c>DefenseClaw</c> key) and its <c>InstallLocation</c> must be the
/// folder the app runs from.
/// </summary>
internal static class InstallerRegistration
{
    /// <summary>The installer's fixed AppId (the release contract). Inno names the key <c>{AppId}_is1</c>, with the braces.</summary>
    public const string AppId = "{290C7D59-D43A-4CF8-BE38-60332DD563EC}";

    public static string SubKey => AppId + "_is1";

    /// <summary>True when DisplayVersion was updated; false when there is no matching installer entry or it could not be written.</summary>
    public static bool UpdateDisplayVersion(IUninstallKeyStore store, string appDirectory, AppVersion version)
    {
        ArgumentNullException.ThrowIfNull(store);

        var location = store.GetString(SubKey, "InstallLocation");
        if (string.IsNullOrWhiteSpace(location) || !SameFolder(location, appDirectory))
        {
            return false;
        }

        return store.SetString(SubKey, "DisplayVersion", version.ToString());
    }

    private static bool SameFolder(string a, string b)
    {
        static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
