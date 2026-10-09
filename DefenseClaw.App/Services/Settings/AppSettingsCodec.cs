using System.Text.Json.Nodes;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Settings;

/// <summary>
/// The wire shape of <see cref="AppSettings"/>: how each section is read out of the file's JSON and written back into it.
/// <para>
/// <b>Reading is forgiving, field by field.</b> A section that is missing, or is not an object, is that section's defaults;
/// inside a section, a field that is missing, of the wrong type or out of range (a number where a name was expected, a name
/// this build does not know, <c>"healthIntervalSeconds": 9000</c>) falls back — or, for a number, clamps — for that one field
/// and leaves its neighbours alone. Nothing here throws.
/// </para>
/// <para>
/// <b>Writing is additive.</b> A section is written into the existing section object, so a member this build does not know
/// (put there by a newer build, or by hand) survives; a null value removes its member instead of writing <c>null</c>.
/// Whole sections this build does not know are never touched — they are not here, they are in the JSON the store keeps.
/// </para>
/// </summary>
internal static class AppSettingsCodec
{
    public const string VersionKey = "schemaVersion";

    public const string AppearanceKey = "appearance";
    public const string MonitoringKey = "monitoring";
    public const string NotificationsKey = "notifications";
    public const string StartupKey = "startup";
    public const string ConnectionKey = "connection";
    public const string UpdatesKey = "updates";
    public const string DeveloperKey = "developer";
    public const string ArchiveKey = "archive";
    public const string PaletteKey = "palette";

    /// <summary>Reads every section out of <paramref name="root"/>. Never throws.</summary>
    public static AppSettings Read(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return new AppSettings(
            ReadAppearance(Section(root, AppearanceKey)),
            ReadMonitoring(Section(root, MonitoringKey)),
            ReadNotifications(Section(root, NotificationsKey)),
            ReadStartup(Section(root, StartupKey)),
            ReadConnection(Section(root, ConnectionKey)),
            ReadUpdates(Section(root, UpdatesKey)),
            ReadDeveloper(Section(root, DeveloperKey)),
            ReadArchive(Section(root, ArchiveKey)),
            ReadPalette(Section(root, PaletteKey)));
    }

    /// <summary>Writes the given sections of <paramref name="settings"/> into <paramref name="root"/>, leaving every other member as it is.</summary>
    public static void Write(JsonObject root, AppSettings settings, AppSettingsSections sections)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(settings);

        if ((sections & AppSettingsSections.Appearance) != 0)
        {
            var target = SectionForWrite(root, AppearanceKey);
            Put(target, "style", ToWire(settings.Appearance.Style));
            Put(target, "mode", ToWire(settings.Appearance.Mode));
        }

        if ((sections & AppSettingsSections.Monitoring) != 0)
        {
            var target = SectionForWrite(root, MonitoringKey);
            Put(target, "healthIntervalSeconds", settings.Monitoring.HealthIntervalSeconds);
            Put(target, "paused", settings.Monitoring.Paused);
        }

        if ((sections & AppSettingsSections.Notifications) != 0)
        {
            var target = SectionForWrite(root, NotificationsKey);
            Put(target, "critical", settings.Notifications.Critical);
            Put(target, "high", settings.Notifications.High);
            Put(target, "gateway", settings.Notifications.Gateway);
            Put(target, "highWaterUnixNano", settings.Notifications.HighWaterUnixNano);
        }

        if ((sections & AppSettingsSections.Startup) != 0)
        {
            var target = SectionForWrite(root, StartupKey);
            Put(target, "gatewayAutoStart", settings.Startup.GatewayAutoStart);
            Put(target, "closeToTray", settings.Startup.CloseToTray);
            Put(target, "rememberLastPanel", settings.Startup.RememberLastPanel);
            Put(target, "lastPanelId", settings.Startup.LastPanelId);
        }

        if ((sections & AppSettingsSections.Connection) != 0)
        {
            var target = SectionForWrite(root, ConnectionKey);
            Put(target, "cliPathOverride", settings.Connection.CliPathOverride);
        }

        if ((sections & AppSettingsSections.Developer) != 0)
        {
            var target = SectionForWrite(root, DeveloperKey);
            Put(target, "enabled", settings.Developer.Enabled);
            Put(target, "kind", ToWire(settings.Developer.Kind));
            Put(target, "cliPath", settings.Developer.CliPath);
            Put(target, "homeDirectory", settings.Developer.HomeDirectory);
            Put(target, "gatewayUrl", settings.Developer.GatewayUrl);
            Put(target, "containerName", settings.Developer.ContainerName);
            Put(target, "hostDataFolder", settings.Developer.HostDataFolder);
        }

        if ((sections & AppSettingsSections.Archive) != 0)
        {
            var target = SectionForWrite(root, ArchiveKey);
            Put(target, "path", settings.Archive.Path);
        }

        if ((sections & AppSettingsSections.Updates) != 0)
        {
            var target = SectionForWrite(root, UpdatesKey);
            Put(target, "lastCheckUnix", settings.Updates.LastCheckUnix);
            Put(target, "dismissedVersion", settings.Updates.DismissedVersion);
            Put(target, "notifiedVersion", settings.Updates.NotifiedVersion);
        }

        if ((sections & AppSettingsSections.Palette) != 0)
        {
            var target = SectionForWrite(root, PaletteKey);
            target["recent"] = new JsonArray(settings.Palette.RecentCommandIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }
    }

    // ------------------------------------------------------------------ sections

    private static DeveloperSettings ReadDeveloper(JsonObject? section) =>
        section is null
            ? new DeveloperSettings()
            : new DeveloperSettings
            {
                Enabled = ReadBool(section["enabled"], false),
                Kind = ReadEnum(section["kind"], RuntimeKind.Installed),
                CliPath = ReadString(section["cliPath"]),
                HomeDirectory = ReadString(section["homeDirectory"]),
                GatewayUrl = ReadString(section["gatewayUrl"]),
                ContainerName = ReadString(section["containerName"]),
                HostDataFolder = ReadString(section["hostDataFolder"]),
            };

    private static ArchiveSettings ReadArchive(JsonObject? section) =>
        section is null
            ? new ArchiveSettings()
            : new ArchiveSettings { Path = ReadString(section["path"]) };

    /// <summary>The remembered ids: the strings of the array, in order. Anything else in it (a number, an object), or a list that is not an array, is skipped.</summary>
    private static PaletteSettings ReadPalette(JsonObject? section) =>
        section is null || section["recent"] is not JsonArray recent
            ? new PaletteSettings()
            : new PaletteSettings { RecentCommandIds = recent.Select(item => ReadString(item)).OfType<string>().ToArray() };

    private static AppearanceSettings ReadAppearance(JsonObject? section) =>
        section is null
            ? AppearanceSettings.Defaults
            : new AppearanceSettings(
                ReadEnum(section["style"], AppearanceSettings.Defaults.Style),
                ReadEnum(section["mode"], AppearanceSettings.Defaults.Mode));

    private static MonitoringSettings ReadMonitoring(JsonObject? section) =>
        section is null
            ? new MonitoringSettings()
            : new MonitoringSettings
            {
                HealthIntervalSeconds = ReadInt(section["healthIntervalSeconds"], MonitoringSettings.DefaultHealthIntervalSeconds),
                Paused = ReadBool(section["paused"], false),
            };

    private static NotificationSettings ReadNotifications(JsonObject? section)
    {
        var defaults = new NotificationSettings();
        return section is null
            ? defaults
            : new NotificationSettings
            {
                Critical = ReadBool(section["critical"], defaults.Critical),
                High = ReadBool(section["high"], defaults.High),
                Gateway = ReadBool(section["gateway"], defaults.Gateway),
                HighWaterUnixNano = Math.Max(0, ReadLong(section["highWaterUnixNano"], 0)),
            };
    }

    private static StartupSettings ReadStartup(JsonObject? section)
    {
        var defaults = new StartupSettings();
        return section is null
            ? defaults
            : new StartupSettings
            {
                GatewayAutoStart = ReadBool(section["gatewayAutoStart"], defaults.GatewayAutoStart),
                CloseToTray = ReadBool(section["closeToTray"], defaults.CloseToTray),
                RememberLastPanel = ReadBool(section["rememberLastPanel"], defaults.RememberLastPanel),
                LastPanelId = ReadString(section["lastPanelId"]),
            };
    }

    private static ConnectionSettings ReadConnection(JsonObject? section) =>
        section is null
            ? new ConnectionSettings()
            : new ConnectionSettings { CliPathOverride = ReadString(section["cliPathOverride"]) };

    private static UpdateSettings ReadUpdates(JsonObject? section) =>
        section is null
            ? new UpdateSettings()
            : new UpdateSettings
            {
                LastCheckUnix = Math.Max(0, ReadLong(section["lastCheckUnix"], 0)),
                DismissedVersion = ReadString(section["dismissedVersion"]),
                NotifiedVersion = ReadString(section["notifiedVersion"]),
            };

    // ------------------------------------------------------------------ JSON plumbing

    /// <summary>The section as an object, or null when it is absent or is not one.</summary>
    private static JsonObject? Section(JsonObject root, string name) => root[name] as JsonObject;

    /// <summary>The section's existing object, or a new one put in its place (when it was absent, or was not an object).</summary>
    private static JsonObject SectionForWrite(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    /// <summary>Sets <paramref name="key"/>, or removes it for a null value, keeping every other member where it was.</summary>
    private static void Put(JsonObject target, string key, JsonNode? value)
    {
        if (value is null)
        {
            _ = target.Remove(key);
        }
        else
        {
            target[key] = value;
        }
    }

    private static void Put(JsonObject target, string key, string? value) => Put(target, key, value is null ? null : JsonValue.Create(value));

    private static void Put(JsonObject target, string key, bool value) => Put(target, key, JsonValue.Create(value));

    private static void Put(JsonObject target, string key, int value) => Put(target, key, JsonValue.Create(value));

    private static void Put(JsonObject target, string key, long value) => Put(target, key, JsonValue.Create(value));

    private static bool ReadBool(JsonNode? node, bool fallback) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    /// <summary>An integer; a whole-valued number written with a fraction (<c>5.0</c>) counts, a fractional one is rounded, anything else is the fallback.</summary>
    private static int ReadInt(JsonNode? node, int fallback)
    {
        var number = ReadLong(node, long.MinValue);
        if (number == long.MinValue)
        {
            return fallback;
        }

        return (int)Math.Clamp(number, int.MinValue, int.MaxValue);
    }

    private static long ReadLong(JsonNode? node, long fallback)
    {
        if (node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<long>(out var whole))
        {
            return whole;
        }

        if (value.TryGetValue<double>(out var fractional) && double.IsFinite(fractional))
        {
            return (long)Math.Clamp(Math.Round(fractional), long.MinValue / 2d, long.MaxValue / 2d);
        }

        return fallback;
    }

    /// <summary>A non-blank string, trimmed; null for anything else (absent, null, blank, another JSON type).</summary>
    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

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

    /// <summary>The schema version a parsed file claims: its <c>schemaVersion</c> when that is a whole number, else zero.</summary>
    public static int VersionOf(JsonObject root) =>
        root[VersionKey] is JsonValue version && version.TryGetValue<int>(out var number) ? number : 0;
}
