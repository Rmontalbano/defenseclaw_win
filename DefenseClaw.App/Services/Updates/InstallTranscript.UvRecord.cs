using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Updates;

/// <summary>
/// install.ps1 1.0.10 <c>Repair-DamagedPackages</c> (scripts/install.ps1@1.0.10:2380-2410): after it installs the Python packages it compares each with its wheel
/// RECORD, downloads a damaged one again, and checks again. Two <c>x</c> lines say it could not finish. In the staging environment the run then stops with
/// <c>Could not install the DefenseClaw &lt;version&gt; Python package; nothing was changed</c> (:5788-5791, printed by the top-level catch, :6134), which is the last
/// <c>x</c> line and a "nothing was changed" refusal. In the final environment the run restores the previous install (:5978-5986), which is not a refusal.
/// </summary>
public static partial class InstallTranscript
{
    // Its own field, declared before the patterns: the order of static initializers across the parts of a partial class is not defined.
    private static readonly TimeSpan UvRecordTimeout = TimeSpan.FromSeconds(1);

    // :2408: "The Python packages {names} in {where} still differ from their wheels after a fresh download; delete the uv cache {UV_CACHE_DIR} and run the installer again".
    private static readonly Regex UvStillDiffer = new(
        @"^The Python packages (?<names>[A-Za-z0-9][A-Za-z0-9._, -]*?) in (?<where>.+?) still differ from their wheels after a fresh download; delete the uv cache(?: (?<cache>.*))? and run the installer again$",
        RegexOptions.CultureInvariant,
        UvRecordTimeout);

    // :2394: "Could not check the Python packages in {where} against their wheels (exit N)".
    private static readonly Regex UvCouldNotCheck = new(
        @"^Could not check the Python packages in .+? against their wheels \(exit -?\d+\)$",
        RegexOptions.CultureInvariant,
        UvRecordTimeout);

    private const int MaxUvNamesChars = 400;
    private const int MaxUvCachePathChars = 400;
    private const int MaxUvWhereChars = 100;
    private const string UvCacheNamedInLog = "the uv cache folder named in the install log";

    /// <summary>
    /// The refusal for a last <c>x</c> line that says nothing was changed, when an earlier <c>x</c> line of the same run is one of the uv RECORD check's two
    /// failures; null when neither is there (the caller keeps its plain refusal).
    /// </summary>
    private static InstallerRefusal? UvRecordRefusal(IReadOnlyList<string> lines, int at, string last)
    {
        for (var i = at - 1; i >= 0; i--)
        {
            if (!FailureLine.IsMatch(lines[i]))
            {
                continue;
            }

            var raw = FailureLine.Match(lines[i]).Groups["r"].Value;
            if (UvStillDiffer.Match(Sanitize(raw, shortenHome: false)) is { Success: true } differ)
            {
                return UvCacheDamagedRefusal(differ);
            }

            var line = Sanitize(raw);
            if (UvCouldNotCheck.IsMatch(line))
            {
                var reason = string.Equals(line, last, StringComparison.Ordinal) ? line : line + " — " + last;
                return new InstallerRefusal(InstallerRefusalKind.NothingChanged, Cap(reason), GenericNextStep);
            }
        }

        return null;
    }

    private static InstallerRefusal UvCacheDamagedRefusal(Match differ)
    {
        var names = differ.Groups["names"].Value.Trim();
        var place = differ.Groups["where"].Value.Trim();
        var cache = differ.Groups["cache"].Value.Trim();
        if (names.Length == 0 || names.Length > MaxUvNamesChars)
        {
            names = "named in the install log";
        }

        if (place.Length == 0 || place.Length > MaxUvWhereChars || place.Any(char.IsControl))
        {
            place = "the environment";
        }

        var usable = cache.Length is > 0 and <= MaxUvCachePathChars && !cache.Any(char.IsControl);
        var reasonCache = usable ? "the uv cache " + cache : UvCacheNamedInLog;
        var stepCache = usable ? "the uv cache folder " + cache : UvCacheNamedInLog;
        var reason = $"The Python packages {names} in {place} still differ from their wheels after a fresh download; delete {reasonCache}. Nothing was changed.";
        var step = $"Delete {stepCache}, then upgrade again. uv downloads the packages again on the next run.";
        return new InstallerRefusal(InstallerRefusalKind.UvCacheDamaged, reason, step);
    }
}
