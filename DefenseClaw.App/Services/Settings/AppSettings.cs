using DefenseClaw.App.Services.Appearance;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Settings;

/// <summary>
/// The app's persisted settings, one record per section of <c>%LOCALAPPDATA%\DefenseClaw.App\settings.json</c>. Read them
/// from <see cref="AppSettingsStore.Current"/>, change them with <see cref="AppSettingsStore.Update"/>:
/// <c>store.Update(s =&gt; s with { Monitoring = s.Monitoring with { Paused = true } })</c>.
/// <para>
/// Every record is immutable, has a default that is what a fresh install gets, and cannot hold an out-of-range value: the
/// one numeric setting clamps itself on the way in, so a hand-edited file or a caller's slip never reaches a timer.
/// </para>
/// </summary>
/// <param name="Appearance">Style and light/dark; owned by <see cref="AppearanceService"/>.</param>
/// <param name="Monitoring">How often and whether the gateway is polled.</param>
/// <param name="Notifications">Which tray toasts show, and the high-water mark that keeps a restart from repeating them.</param>
/// <param name="Startup">What the app does when it starts and when its window closes.</param>
/// <param name="Connection">Where the DefenseClaw CLI is, when it is not where the app would look.</param>
/// <param name="Updates">What the update check remembers between runs.</param>
/// <param name="Developer">The developer runtime selector (Settings -> Advanced); off unless a developer turns it on.</param>
/// <param name="Archive">The optional archived audit database the Audit panel can show beside the live one (CUST-299).</param>
/// <param name="Palette">What the command palette remembers between runs: the last few commands it ran (CUST-264).</param>
/// <param name="Seen">Where the operator last looked in each stream panel, so the sidebar can say what is new since (CUST-265).</param>
internal sealed record AppSettings(
    AppearanceSettings Appearance,
    MonitoringSettings Monitoring,
    NotificationSettings Notifications,
    StartupSettings Startup,
    ConnectionSettings Connection,
    UpdateSettings Updates,
    DeveloperSettings Developer,
    ArchiveSettings Archive,
    PaletteSettings Palette,
    SeenSettings Seen)
{
    /// <summary>A fresh install: every section at its defaults.</summary>
    public static AppSettings Defaults { get; } = new(
        AppearanceSettings.Defaults,
        new MonitoringSettings(),
        new NotificationSettings(),
        new StartupSettings(),
        new ConnectionSettings(),
        new UpdateSettings(),
        new DeveloperSettings(),
        new ArchiveSettings(),
        new PaletteSettings(),
        new SeenSettings());
}

/// <summary>The sections of <see cref="AppSettings"/>, as flags: what <see cref="AppSettingsChangedEventArgs.Sections"/> says changed.</summary>
[Flags]
internal enum AppSettingsSections
{
    None = 0,
    Appearance = 1,
    Monitoring = 2,
    Notifications = 4,
    Startup = 8,
    Connection = 16,
    Updates = 32,
    Developer = 64,
    Archive = 128,
    Palette = 256,
    Seen = 512,
    All = Appearance | Monitoring | Notifications | Startup | Connection | Updates | Developer | Archive | Palette | Seen,
}

/// <summary>Monitoring: how often the gateway's health is polled, and whether polling is paused (the Mac's "pulse interval").</summary>
internal sealed record MonitoringSettings
{
    public const int MinHealthIntervalSeconds = 2;
    public const int MaxHealthIntervalSeconds = 60;
    public const int DefaultHealthIntervalSeconds = 5;

    private readonly int _healthIntervalSeconds = DefaultHealthIntervalSeconds;

    /// <summary>Seconds between health polls, always within <see cref="MinHealthIntervalSeconds"/>..<see cref="MaxHealthIntervalSeconds"/> (a value outside is clamped, not refused).</summary>
    public int HealthIntervalSeconds
    {
        get => _healthIntervalSeconds;
        init => _healthIntervalSeconds = Math.Clamp(value, MinHealthIntervalSeconds, MaxHealthIntervalSeconds);
    }

    /// <summary>True while the operator has paused monitoring.</summary>
    public bool Paused { get; init; }

    public TimeSpan HealthInterval => TimeSpan.FromSeconds(HealthIntervalSeconds);
}

/// <summary>Notifications: which events raise a tray toast, and the newest alert already announced.</summary>
internal sealed record NotificationSettings
{
    /// <summary>Toast for a new CRITICAL alert.</summary>
    public bool Critical { get; init; } = true;

    /// <summary>Toast for a new HIGH alert.</summary>
    public bool High { get; init; } = true;

    /// <summary>Toast when the gateway goes away or comes back.</summary>
    public bool Gateway { get; init; } = true;

    /// <summary>
    /// The timestamp (Unix nanoseconds, as the audit database stores it) of the newest alert already announced; zero when
    /// nothing has been. A restart announces only what is newer than this, so it does not repeat yesterday's toasts.
    /// </summary>
    public long HighWaterUnixNano { get; init; }
}

/// <summary>Startup: the gateway, the window's close button, and which panel opens first.</summary>
internal sealed record StartupSettings
{
    /// <summary>Start the gateway when the app starts and it is not running.</summary>
    public bool GatewayAutoStart { get; init; }

    /// <summary>The window's close button hides it to the tray (today's behaviour) instead of ending the app.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Open on <see cref="LastPanelId"/> instead of the first panel.</summary>
    public bool RememberLastPanel { get; init; }

    /// <summary>The <see cref="PanelDescriptor.Id"/> of the panel last shown; null until one has been remembered.</summary>
    public string? LastPanelId { get; init; }
}

/// <summary>Connection: where to find the CLI.</summary>
internal sealed record ConnectionSettings
{
    private readonly string? _cliPathOverride;

    /// <summary>
    /// A full path to <c>defenseclaw.exe</c> that replaces the lookup on PATH and in the install directory. Null (the
    /// default) means "look it up"; a blank value is null.
    /// </summary>
    public string? CliPathOverride
    {
        get => _cliPathOverride;
        init => _cliPathOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>Updates: what the release check remembers.</summary>
internal sealed record UpdateSettings
{
    /// <summary>When the release check last ran, in Unix seconds; zero when it never has.</summary>
    public long LastCheckUnix { get; init; }

    /// <summary>The release version the operator chose to stop being told about; null when none.</summary>
    public string? DismissedVersion { get; init; }

    /// <summary>
    /// The newest release version already announced with a tray toast; null when none has been. Kept so a restart does not
    /// repeat the toast for a release the operator has not acted on yet (the banner still shows it).
    /// </summary>
    public string? NotifiedVersion { get; init; }
}

/// <summary>
/// Developer: which DefenseClaw the app drives. <b>Off by default, and off means exactly the installed runtime</b>: with
/// <see cref="Enabled"/> false every other field is ignored, so a half-filled form can never change what a normal user runs.
/// The fields are kept when the switch goes off so a developer's second session does not start from blank. A change takes
/// effect the next time the app starts (the data directory and the gateway client are built once, at launch).
/// </summary>
internal sealed record DeveloperSettings
{
    private readonly string? _cliPath;
    private readonly string? _homeDirectory;
    private readonly string? _gatewayUrl;
    private readonly string? _containerName;
    private readonly string? _hostDataFolder;

    /// <summary>The selector is on. Default false.</summary>
    public bool Enabled { get; init; }

    /// <summary>Which runtime, while <see cref="Enabled"/>. <see cref="RuntimeKind.Installed"/> is the default.</summary>
    public RuntimeKind Kind { get; init; }

    /// <summary>Full path to the side-by-side <c>defenseclaw.exe</c>.</summary>
    public string? CliPath { get => _cliPath; init => _cliPath = Clean(value); }

    /// <summary>The <c>DEFENSECLAW_HOME</c> for that CLI.</summary>
    public string? HomeDirectory { get => _homeDirectory; init => _homeDirectory = Clean(value); }

    /// <summary>The loopback gateway address (a CLI: optional; a container: required).</summary>
    public string? GatewayUrl { get => _gatewayUrl; init => _gatewayUrl = Clean(value); }

    /// <summary>The Docker container name.</summary>
    public string? ContainerName { get => _containerName; init => _containerName = Clean(value); }

    /// <summary>The host folder holding a copy of the container's data directory (read-only to the app).</summary>
    public string? HostDataFolder { get => _hostDataFolder; init => _hostDataFolder = Clean(value); }

    /// <summary>
    /// The selection these settings ask for: <see cref="RuntimeSelection.Installed"/> unless the selector is on, a non-default
    /// kind is chosen <b>and</b> the choice is valid. Anything else (off, incomplete, a file that has gone) is the installed
    /// runtime, so the app always starts.
    /// </summary>
    public RuntimeSelection ToSelection()
    {
        if (!Enabled)
        {
            return RuntimeSelection.Installed;
        }

        var selection = Raw();
        return selection.Validate() is null ? selection : RuntimeSelection.Installed;
    }

    /// <summary>The selection as typed, valid or not (what the Settings form validates and shows).</summary>
    public RuntimeSelection Raw() => Kind switch
    {
        RuntimeKind.Cli => RuntimeSelection.ForCli(CliPath, HomeDirectory, GatewayUrl),
        RuntimeKind.Container => RuntimeSelection.ForContainer(ContainerName, GatewayUrl, HostDataFolder),
        _ => RuntimeSelection.Installed,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Archive: where the archived audit database is (CUST-299), a copy of an earlier DefenseClaw's <c>audit.db</c> kept outside the
/// live data folder. Null (the default) means there is none and the Audit panel shows no Live | Archive switch. The form validates
/// before it saves (<see cref="Core.Audit.AuditArchive"/>), and the panel checks again each time it opens the archive: a file that
/// has since moved or been damaged is an error state in the panel, not a crash.
/// </summary>
internal sealed record ArchiveSettings
{
    private readonly string? _path;

    /// <summary>Full path to the archived <c>audit.db</c>; a blank value is null.</summary>
    public string? Path
    {
        get => _path;
        init => _path = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>
/// Palette: the commands the operator last ran from the command palette, newest first, by their stable ids (<c>cli.skill.list</c>,
/// <c>nav.alerts</c>, <c>gateway.restart</c>), so the palette can show them first when its search is empty. Ids, not titles or command
/// lines: a title changes with the runtime's registry and a command line can hold a name the operator typed; an id that no longer names a row
/// is simply not shown. At most <see cref="MaxRecent"/>; the list cleans itself on the way in (blank and repeated ids dropped, the oldest cut).
/// </summary>
internal sealed record PaletteSettings
{
    /// <summary>How many commands the palette remembers.</summary>
    public const int MaxRecent = 5;

    /// <summary>The longest id kept. Real ids are well under half of this; a longer string in a hand-edited file is not one.</summary>
    public const int MaxIdLength = 200;

    private readonly IReadOnlyList<string> _recent = Array.Empty<string>();

    /// <summary>The ids of the last commands run from the palette, most recent first; never more than <see cref="MaxRecent"/>, never repeated.</summary>
    public IReadOnlyList<string> RecentCommandIds
    {
        get => _recent;
        init => _recent = Clean(value);
    }

    /// <summary>
    /// Record equality would compare the list by reference, and two settings holding the same ids are the same settings: a change that
    /// writes the list it already had must not look like a change (no write, no <see cref="AppSettingsStore.Changed"/>).
    /// </summary>
    public bool Equals(PaletteSettings? other) =>
        other is not null && _recent.SequenceEqual(other._recent, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in _recent)
        {
            hash.Add(id, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    internal static IReadOnlyList<string> Clean(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return Array.Empty<string>();
        }

        return ids
            .Select(id => id?.Trim() ?? string.Empty)
            .Where(id => id.Length > 0 && id.Length <= MaxIdLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxRecent)
            .ToArray();
    }
}

/// <summary>
/// Seen: where the operator last looked in each stream panel (CUST-265), by panel id (<c>audit</c>, <c>activity</c>, <c>ai-discovery</c>), so
/// the sidebar can show "N new since last visit" and still mean it after a restart. The TUI keeps a <em>count</em> per panel
/// (<c>panel_seen_counts</c>), which stops working the moment a panel's list is capped (its Audit list is the newest 500) or trimmed; this keeps
/// a <em>position</em>, whose meaning belongs to the panel: the newest <c>rowid</c> of <c>audit_events</c> for Audit, and a UTC instant (ticks) for
/// the two panels whose data is dated (Activity: when its runs started; AI Discovery: when a component was first seen). A panel with no entry has
/// not been looked at yet, which is not the same as 0 (an empty table that was looked at). Never a secret, a path or a name: numbers under ids.
/// <para>
/// At most <see cref="MaxPanels"/> entries, ids at most <see cref="MaxIdLength"/> long and values never negative; the dictionary cleans itself on the
/// way in, so a hand-edited file cannot grow it or put a nonsense marker in front of a reader. Equality is by content, so a write of the markers
/// the store already holds is not a change (no write, no <see cref="AppSettingsStore.Changed"/>).
/// </para>
/// </summary>
internal sealed record SeenSettings
{
    /// <summary>How many panels are remembered. The app tracks three; the rest is room for a build that tracks more.</summary>
    public const int MaxPanels = 32;

    /// <summary>The longest panel id kept. Real ids are a dozen characters.</summary>
    public const int MaxIdLength = 64;

    private static readonly IReadOnlyDictionary<string, long> NoMarkers = new Dictionary<string, long>(0, StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, long> _markers = NoMarkers;

    /// <summary>The marker of each panel that has one.</summary>
    public IReadOnlyDictionary<string, long> Markers
    {
        get => _markers;
        init => _markers = Clean(value);
    }

    /// <summary>The marker of <paramref name="panelId"/>; false when the panel has not been looked at (or the store has never heard of it).</summary>
    public bool TryGet(string panelId, out long marker) => _markers.TryGetValue(panelId, out marker);

    /// <summary>These markers with <paramref name="panelId"/>'s set to <paramref name="marker"/>; this instance when it already is.</summary>
    public SeenSettings With(string panelId, long marker)
    {
        if (_markers.TryGetValue(panelId, out var existing) && existing == marker)
        {
            return this;
        }

        var next = new Dictionary<string, long>(_markers, StringComparer.Ordinal) { [panelId] = marker };
        return new SeenSettings { Markers = next };
    }

    /// <summary>Record equality would compare the dictionary by reference, and two settings holding the same markers are the same settings.</summary>
    public bool Equals(SeenSettings? other)
    {
        if (other is null || _markers.Count != other._markers.Count)
        {
            return false;
        }

        foreach (var (panel, marker) in _markers)
        {
            if (!other._markers.TryGetValue(panel, out var theirs) || theirs != marker)
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        // Order-insensitive: the same markers hash alike however they were inserted.
        var hash = 0;
        foreach (var (panel, marker) in _markers)
        {
            hash ^= HashCode.Combine(panel, marker);
        }

        return hash;
    }

    internal static IReadOnlyDictionary<string, long> Clean(IEnumerable<KeyValuePair<string, long>>? markers)
    {
        if (markers is null)
        {
            return NoMarkers;
        }

        var cleaned = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (rawId, marker) in markers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var id = rawId?.Trim() ?? string.Empty;
            if (id.Length == 0 || id.Length > MaxIdLength || marker < 0 || cleaned.Count >= MaxPanels)
            {
                continue;
            }

            cleaned[id] = marker;
        }

        return cleaned.Count == 0 ? NoMarkers : cleaned;
    }
}

/// <summary>What <see cref="AppSettingsStore.Changed"/> reports.</summary>
internal sealed class AppSettingsChangedEventArgs : EventArgs
{
    public AppSettingsChangedEventArgs(AppSettings previous, AppSettings current)
    {
        Previous = previous;
        Current = current;
        Sections = Diff(previous, current);
    }

    /// <summary>The settings before the change.</summary>
    public AppSettings Previous { get; }

    /// <summary>The settings after it.</summary>
    public AppSettings Current { get; }

    /// <summary>The sections that differ between <see cref="Previous"/> and <see cref="Current"/>; never <see cref="AppSettingsSections.None"/>.</summary>
    public AppSettingsSections Sections { get; }

    /// <summary>True when <paramref name="section"/> is among <see cref="Sections"/>.</summary>
    public bool Affects(AppSettingsSections section) => (Sections & section) != 0;

    internal static AppSettingsSections Diff(AppSettings before, AppSettings after)
    {
        var sections = AppSettingsSections.None;
        if (before.Appearance != after.Appearance)
        {
            sections |= AppSettingsSections.Appearance;
        }

        if (before.Monitoring != after.Monitoring)
        {
            sections |= AppSettingsSections.Monitoring;
        }

        if (before.Notifications != after.Notifications)
        {
            sections |= AppSettingsSections.Notifications;
        }

        if (before.Startup != after.Startup)
        {
            sections |= AppSettingsSections.Startup;
        }

        if (before.Connection != after.Connection)
        {
            sections |= AppSettingsSections.Connection;
        }

        if (before.Updates != after.Updates)
        {
            sections |= AppSettingsSections.Updates;
        }

        if (before.Developer != after.Developer)
        {
            sections |= AppSettingsSections.Developer;
        }

        if (before.Archive != after.Archive)
        {
            sections |= AppSettingsSections.Archive;
        }

        if (before.Palette != after.Palette)
        {
            sections |= AppSettingsSections.Palette;
        }

        if (before.Seen != after.Seen)
        {
            sections |= AppSettingsSections.Seen;
        }

        return sections;
    }
}
