using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>
/// A semantic version as this app and its release tags spell it: <c>MAJOR.MINOR.PATCH</c>, an optional <c>-prerelease</c>, an ignored
/// <c>+build</c> (the CI's <c>+&lt;sha&gt;</c>), and a leading <c>v</c> on a tag. A build without a tag carries <c>-dev</c>
/// (<see cref="IsDev"/>), so it never looks newer than the release it was cut from.
/// </summary>
internal readonly record struct AppVersion(int Major, int Minor, int Patch, string? Pre) : IComparable<AppVersion>
{
    private static readonly Regex Pattern = new(
        @"^[vV]?(?<a>\d{1,9})\.(?<b>\d{1,9})\.(?<c>\d{1,9})(?:-(?<pre>[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsPrerelease => Pre is not null;

    /// <summary>An untagged development build: <c>-dev</c>, or <c>-dev.N</c>.</summary>
    public bool IsDev => (Major == 0 && Minor == 0 && Patch == 0) || Pre is not null && (Pre.Equals("dev", StringComparison.OrdinalIgnoreCase) || Pre.StartsWith("dev.", StringComparison.OrdinalIgnoreCase));

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128)
        {
            return false;
        }

        var match = Pattern.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        version = new AppVersion(
            int.Parse(match.Groups["a"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["c"].Value, CultureInfo.InvariantCulture),
            match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
        return true;
    }

    /// <summary>Semantic precedence: the numbers, then a release over any prerelease of it, then prerelease identifiers (numbers numerically).</summary>
    public int CompareTo(AppVersion other)
    {
        var byNumbers = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byNumbers != 0)
        {
            return byNumbers;
        }

        if (Pre is null || other.Pre is null)
        {
            return (Pre is null ? 1 : 0) - (other.Pre is null ? 1 : 0);
        }

        var left = Pre.Split('.', '-');
        var right = other.Pre.Split('.', '-');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l);
            var rightNumeric = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r);
            var step = leftNumeric && rightNumeric
                ? l.CompareTo(r)
                : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(left[i], right[i]);
            if (step != 0)
            {
                return step;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    /// <summary><c>1.2.3</c> or <c>1.2.3-dev</c>; no <c>v</c>, no build metadata.</summary>
    public override string ToString() => Pre is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";

    /// <summary>This app's own version from its assembly InformationalVersion (<c>+sha</c> stripped); null when it cannot be read or parsed.</summary>
    public static AppVersion? Running()
    {
        var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParse(informational, out var version) ? version : null;
    }
}
