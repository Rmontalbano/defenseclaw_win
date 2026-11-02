using System.Collections;

namespace DefenseClaw.Core.Config;

/// <summary>
/// <c>guardrail.protected_files</c> as config.yaml says it (internal/config/protected_files.go at tag 1.0.6), for showing it. The app never writes it from here:
/// a change is a reviewed <c>config set</c> or <c>config unset</c>.
/// </summary>
/// <param name="OnChange">What the gateway does with it: <see cref="Alert"/>, <see cref="Restore"/> or <see cref="Off"/> (<c>EffectiveOnChange</c>: anything else, or unset, is alert).</param>
/// <param name="IsSet">True when the file sets a non-empty <c>on_change</c>.</param>
/// <param name="Unrecognized">The value as written when it is none of the three; null otherwise. The runtime's validation refuses such a file.</param>
/// <param name="Paths">The configured home-relative names (empty means the built-in list).</param>
public sealed record ProtectedFilesSettings(string OnChange, bool IsSet, string? Unrecognized, IReadOnlyList<string> Paths)
{
    public const string Alert = "alert";
    public const string Restore = "restore";
    public const string Off = "off";

    /// <summary>
    /// What the gateway watches on Windows when <c>paths</c> is unset (<c>DefaultPaths</c>, internal/protectedfiles/watch.go at tag 1.0.6: <c>case "windows": return keys</c>).
    /// Shell startup files and autostart entries are not on it.
    /// </summary>
    public static IReadOnlyList<string> WindowsBuiltInPaths { get; } = new[] { ".ssh/authorized_keys", ".ssh/authorized_keys2" };

    /// <summary>Unset: alert, on the built-in list.</summary>
    public static ProtectedFilesSettings Default { get; } = new(Alert, false, null, Array.Empty<string>());

    /// <summary>The names the gateway watches given these settings: the configured ones, or the built-in list. A mode of off watches nothing, which <see cref="OnChange"/> says.</summary>
    public IReadOnlyList<string> WatchedPaths => Paths.Count > 0 ? Paths : WindowsBuiltInPaths;

    /// <summary>True when the configured <c>paths</c> replace the built-in list.</summary>
    public bool HasConfiguredPaths => Paths.Count > 0;

    /// <summary>Reads the block as the YAML parser left it (a map, or anything else when the file has the wrong shape). Never throws: a shape it cannot read is unset.</summary>
    public static ProtectedFilesSettings From(object? raw)
    {
        if (raw is not IDictionary map)
        {
            return Default;
        }

        var written = (map.Contains("on_change") ? map["on_change"] as string : null)?.Trim() ?? string.Empty;
        var folded = written.ToLowerInvariant();
        var (mode, unrecognized) = folded switch
        {
            Restore => (Restore, (string?)null),
            Off => (Off, null),
            Alert or "" => (Alert, null),
            _ => (Alert, written),
        };

        var paths = new List<string>();
        if (map.Contains("paths") && map["paths"] is IEnumerable list and not string)
        {
            foreach (var item in list)
            {
                if (item is string name && name.Trim().Length > 0)
                {
                    paths.Add(name.Trim());
                }
            }
        }

        return new ProtectedFilesSettings(mode, written.Length > 0, unrecognized, paths);
    }
}
