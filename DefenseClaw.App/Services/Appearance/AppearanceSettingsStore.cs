using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DefenseClaw.App.Services.Appearance;

/// <summary>Where the appearance choice lives between runs.</summary>
internal interface IAppearanceSettingsStore
{
    /// <summary>The saved choice, or the defaults when there is none or it cannot be read. Never throws.</summary>
    AppearanceSettings Load();

    /// <summary>Saves the choice. Failures are logged, not thrown: a setting that cannot be saved must not break the app.</summary>
    void Save(AppearanceSettings settings);
}

/// <summary>
/// The app's own settings file, <c>%LOCALAPPDATA%\DefenseClaw.App\settings.json</c> - the same folder as its update
/// cache and crash log, and never anything under <c>~\.defenseclaw</c>, which belongs to the CLI.
/// <para>
/// <b>Shape.</b> <c>{ "schemaVersion": 1, "appearance": { "style": "linear", "mode": "dark" } }</c>. The file is the app's
/// general settings home, so the appearance is one section of it: saving re-reads the file and rewrites only that
/// section, keeping every other member (and a higher <c>schemaVersion</c>) exactly as found.
/// </para>
/// <para>
/// <b>Tolerance.</b> A missing, empty, truncated or non-JSON file yields the defaults; a style or mode this build does
/// not know (a typo, or a value from a newer build) falls back to the default for that one field and keeps the other.
/// Nothing is ever thrown at the caller. <b>Atomicity.</b> Saves write a temp file beside the target and move it over,
/// so a crash mid-write leaves the previous file intact, never half of a new one.
/// </para>
/// </summary>
internal sealed class FileAppearanceSettingsStore : IAppearanceSettingsStore
{
    /// <summary>The version this build writes. A file that already says more keeps its number.</summary>
    public const int SchemaVersion = 1;

    private const string AppearanceKey = "appearance";
    private const string VersionKey = "schemaVersion";

    private readonly string _path;

    public FileAppearanceSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DefenseClaw.App",
        "settings.json");

    public AppearanceSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return AppearanceSettings.Defaults;
            }

            return Parse(File.ReadAllText(_path));
        }
#pragma warning disable CA1031 // Unreadable settings mean the defaults, whatever the reason.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Appearance settings could not be read from {_path}: {ex.Message}");
            return AppearanceSettings.Defaults;
        }
#pragma warning restore CA1031
    }

    public void Save(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? temp = null;
        try
        {
            var root = ReadRootOrEmpty();
            var existingVersion = root[VersionKey] is JsonValue version && version.TryGetValue<int>(out var number) ? number : 0;
            root[VersionKey] = Math.Max(existingVersion, SchemaVersion);
            root[AppearanceKey] = new JsonObject
            {
                ["style"] = ToWire(settings.Style),
                ["mode"] = ToWire(settings.Mode),
            };

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
            temp = null;
        }
#pragma warning disable CA1031 // A setting that cannot be saved must not take the app down.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Appearance settings could not be saved to {_path}: {ex.Message}");
        }
#pragma warning restore CA1031
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
#pragma warning disable CA1031 // Best-effort cleanup of a temp file.
                catch (Exception)
                {
                }
#pragma warning restore CA1031
            }
        }
    }

    /// <summary>Parses the file's text; internal so the tests can feed it every kind of damaged input.</summary>
    internal static AppearanceSettings Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return AppearanceSettings.Defaults;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return AppearanceSettings.Defaults;
        }

        if (root is not JsonObject obj || obj[AppearanceKey] is not JsonObject appearance)
        {
            return AppearanceSettings.Defaults;
        }

        return new AppearanceSettings(
            ReadEnum(appearance["style"], AppearanceSettings.Defaults.Style),
            ReadEnum(appearance["mode"], AppearanceSettings.Defaults.Mode));
    }

    /// <summary>The existing file's object, so unrelated sections survive; an empty one when there is none or it is damaged.</summary>
    private JsonObject ReadRootOrEmpty()
    {
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject existing)
            {
                return existing;
            }
        }
        catch (JsonException)
        {
            // A damaged file is replaced, not repaired: there is nothing in it worth keeping.
        }

        return new JsonObject();
    }

    private static T ReadEnum<T>(JsonNode? node, T fallback)
        where T : struct, Enum
    {
        // A name, not a number: Enum.TryParse would happily take "1" as the second style.
        if (node is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            text.Length > 0 &&
            char.IsLetter(text[0]) &&
            Enum.TryParse<T>(text, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string ToWire<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
