using System.Globalization;
using System.IO;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// The 1.0.9 installer's uv cache check (install.ps1@1.0.9:1896-1920, called at :5688-5691, before anything is created): uv builds one package
/// (win-unicode-console) from source in its cache and needs 151 more characters than the cache folder's own path, so a cache path longer than 108
/// characters stops the install with "Nothing was changed" unless Windows long paths are on. The installer reads <c>UV_CACHE_DIR</c> from its own
/// environment, which is this app's environment; a variable set after the app started is not seen until the app restarts.
/// </summary>
internal static class UvCacheDepth
{
    public const string RowId = "uv-cache";

    /// <summary>The first release whose installer has the check.</summary>
    public static readonly Version FirstRelease = new(1, 0, 9);

    /// <summary>What uv needs after the cache path (install.ps1@1.0.9:1914).</summary>
    public const int Extra = 151;

    /// <summary>The longest total the installer lets through: length + 151 must not pass 259.</summary>
    public const int Limit = 259;

    public const string NextStep =
        "Set the user environment variable UV_CACHE_DIR (for your account, not the machine) to a short folder that only your account can write to, for example C:\\dcuv, " +
        "then restart this app, because the installer reads this app's environment and a variable set after it started is not seen. Then upgrade again. " +
        "Or have an administrator turn on Windows long path support (LongPathsEnabled).";

    /// <summary>The installer's cache folder for this environment value and data directory, in its own arithmetic: absolute, no trailing backslash; null when the path is not valid.</summary>
    public static string? CacheFolder(string? environmentValue, string dataDirectory)
    {
        try
        {
            var cache = string.IsNullOrWhiteSpace(environmentValue) ? Path.Combine(dataDirectory, ".uv", "cache") : environmentValue.Trim();
            return Path.GetFullPath(cache).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    public static bool TooDeep(string cacheFolder) => cacheFolder.Length + Extra > Limit;

    /// <summary>A pre-flight row, or null: a target before 1.0.9 (its installer has no check), a path that fits, or long paths on.</summary>
    public static PreflightRow? Row(IUpgradeProbes probes, string? targetVersion, string dataDirectory)
    {
        if (!InstallScriptPlan.TryNormalizeVersion(targetVersion, out var target) || !Version.TryParse(target, out var version) || version < FirstRelease)
        {
            return null;
        }

        string? cache;
        try
        {
            cache = CacheFolder(probes.UvCacheDirEnvironment(), dataDirectory);
            if (cache is null || !TooDeep(cache) || probes.LongPathsEnabled() == true)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return null;
        }

        var length = cache.Length.ToString(CultureInfo.InvariantCulture);
        return new PreflightRow(RowId, "uv cache path", PreflightStatus.Warn,
            $"The uv cache folder {cache} is {length} characters long. uv builds one package (win-unicode-console) from source in it and needs {Extra} more, " +
            $"which is past the Windows limit of 260 characters, so DefenseClaw {target}'s installer would stop with nothing changed (WinError 206). " +
            NextStep + " A UV_CACHE_DIR set after this app started is not seen until you restart it.");
    }
}
