using DefenseClaw.App.Services.Appearance;

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
internal sealed record AppSettings(
    AppearanceSettings Appearance,
    MonitoringSettings Monitoring,
    NotificationSettings Notifications,
    StartupSettings Startup,
    ConnectionSettings Connection,
    UpdateSettings Updates)
{
    /// <summary>A fresh install: every section at its defaults.</summary>
    public static AppSettings Defaults { get; } = new(
        AppearanceSettings.Defaults,
        new MonitoringSettings(),
        new NotificationSettings(),
        new StartupSettings(),
        new ConnectionSettings(),
        new UpdateSettings());
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
    All = Appearance | Monitoring | Notifications | Startup | Connection | Updates,
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

        return sections;
    }
}
