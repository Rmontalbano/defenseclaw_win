using System.Text.RegularExpressions;

namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>
/// Every path the helper and the app use, derived from three inputs that the helper reads from the system and never from its command line:
/// the Program Files folder, the invoking user's SID and that user's profile folder. Nothing here touches the disk.
/// <para>
/// The administrator copy is per user (<c>AdminGateway\&lt;SID&gt;</c>), like the Mac helper's <c>uid-&lt;n&gt;</c> folders, so one user's gateway
/// is never run for another.
/// </para>
/// </summary>
public sealed partial class AdminLayout
{
    public const string ProductDirectoryName = "DefenseClaw Companion";
    public const string HelperDirectoryName = "GatewayAdmin";
    public const string HelperFileName = "DefenseClaw.GatewayAdmin.exe";
    public const string AdminGatewayDirectoryName = "AdminGateway";
    public const string GatewayFileName = "defenseclaw-gateway.exe";
    public const string CopyRecordFileName = "copy.json";
    public const string ResultFileName = "last-result.json";
    public const string HelperRecordFileName = "helper.json";

    [GeneratedRegex(@"^S-1-\d{1,3}(-\d{1,10}){1,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex SidShape();

    private AdminLayout(string programFiles, string userSid, string profile)
    {
        ProgramFiles = programFiles;
        UserSid = userSid;
        ProfileDirectory = profile;

        ProductRoot = Path.Combine(programFiles, ProductDirectoryName);
        HelperDirectory = Path.Combine(ProductRoot, HelperDirectoryName);
        HelperExe = Path.Combine(HelperDirectory, HelperFileName);
        HelperRecordPath = Path.Combine(HelperDirectory, HelperRecordFileName);
        AdminGatewayRoot = Path.Combine(ProductRoot, AdminGatewayDirectoryName);
        UserDirectory = Path.Combine(AdminGatewayRoot, userSid);
        GatewayCopyPath = Path.Combine(UserDirectory, GatewayFileName);
        CopyRecordPath = Path.Combine(UserDirectory, CopyRecordFileName);
        ResultPath = Path.Combine(UserDirectory, ResultFileName);

        InstalledGatewayPath = Path.Combine(profile, ".local", "bin", GatewayFileName);
        DataDirectory = Path.Combine(profile, ".defenseclaw");
        ManifestDirectory = Path.Combine(profile, "AppData", "Local", "DefenseClaw.App", "admin-gateway");
        ManifestPath = Path.Combine(ManifestDirectory, "manifest.json");
    }

    /// <summary>True when <paramref name="sid"/> has the shape of a SID string (so it is safe to use as a folder name).</summary>
    public static bool IsValidSid(string? sid) => sid is { Length: > 0 and <= 184 } && SidShape().IsMatch(sid);

    /// <summary>
    /// Builds the layout. Throws <see cref="ArgumentException"/> for a SID that is not a SID, for a Program Files or profile folder that is not
    /// an absolute, already-normalised path, or for a profile that sits inside Program Files.
    /// </summary>
    public static AdminLayout Create(string programFiles, string userSid, string profileDirectory)
    {
        if (!IsValidSid(userSid))
        {
            throw new ArgumentException("Not a SID.", nameof(userSid));
        }

        var pf = Normalise(programFiles, nameof(programFiles));
        var profile = Normalise(profileDirectory, nameof(profileDirectory));
        if (IsInside(pf, profile) || IsInside(profile, pf))
        {
            throw new ArgumentException("The profile and Program Files must not contain each other.", nameof(profileDirectory));
        }

        return new AdminLayout(pf, userSid, profile);
    }

    public string ProgramFiles { get; }

    public string UserSid { get; }

    public string ProfileDirectory { get; }

    /// <summary><c>%ProgramFiles%\DefenseClaw Companion</c>.</summary>
    public string ProductRoot { get; }

    public string HelperDirectory { get; }

    /// <summary>Where the helper runs from once installed.</summary>
    public string HelperExe { get; }

    /// <summary>The hash the helper wrote when it installed itself (admin-only, in the helper's folder).</summary>
    public string HelperRecordPath { get; }

    public string AdminGatewayRoot { get; }

    /// <summary><c>AdminGateway\&lt;SID&gt;</c>: this user's copy, record and last result.</summary>
    public string UserDirectory { get; }

    /// <summary>The administrator copy of <c>defenseclaw-gateway.exe</c>.</summary>
    public string GatewayCopyPath { get; }

    public string CopyRecordPath { get; }

    /// <summary>What the last helper run printed, for the app to show. Written by the helper into its own admin-only folder, never into the user's.</summary>
    public string ResultPath { get; }

    /// <summary>The user's installed gateway: the only source of the copy.</summary>
    public string InstalledGatewayPath { get; }

    /// <summary><c>%USERPROFILE%\.defenseclaw</c>.</summary>
    public string DataDirectory { get; }

    /// <summary>User-writable folder the app puts the manifest in. The helper only reads it.</summary>
    public string ManifestDirectory { get; }

    public string ManifestPath { get; }

    /// <summary>The folders from Program Files down to the copy's folder, outermost first: each must be admin-only and not a reparse point.</summary>
    public IReadOnlyList<string> CopyChain => new[] { ProgramFiles, ProductRoot, AdminGatewayRoot, UserDirectory };

    /// <summary>The folders from Program Files down to the helper's folder, outermost first.</summary>
    public IReadOnlyList<string> HelperChain => new[] { ProgramFiles, ProductRoot, HelperDirectory };

    /// <summary>The folders from the profile root down to the installed gateway's folder (to be checked for reparse points).</summary>
    public IReadOnlyList<string> InstalledGatewayChain => new[]
    {
        ProfileDirectory,
        Path.Combine(ProfileDirectory, ".local"),
        Path.Combine(ProfileDirectory, ".local", "bin"),
    };

    /// <summary>True when <paramref name="path"/> names the same location as the administrator copy (case-insensitive, normalised).</summary>
    public bool IsGatewayCopy(string? path) => SamePath(path, GatewayCopyPath);

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> or below it.</summary>
    public static bool IsInside(string parent, string child)
    {
        var p = Path.TrimEndingDirectorySeparator(parent);
        var c = Path.TrimEndingDirectorySeparator(child);
        return string.Equals(p, c, StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute path is required.", name);
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must be a normalised local path.", name);
        }

        return full;
    }
}
