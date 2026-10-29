namespace DefenseClaw.App.Services;

/// <summary>
/// The four areas of the Settings page (CUST-386), in the Mac's order (<c>AppSettingsTab</c> of DefenseClaw for Mac v1.1.34): General,
/// Monitoring, Notifications, Connection. Which one is on screen is presentation only: it is not saved, and every setting keeps the key it
/// always had in the app's <c>settings.json</c>.
/// </summary>
public enum SettingsArea
{
    /// <summary>Startup and the window, appearance, and the two update cards (this app; the DefenseClaw runtime).</summary>
    General,

    /// <summary>The health-poll cadence and the Pause switch.</summary>
    Monitoring,

    /// <summary>The desktop notification switches and the seen-alert history.</summary>
    Notifications,

    /// <summary>Gateway, Installation, Files and the defenseclaw CLI, then the audit archive and this app's own files.</summary>
    Connection,
}

/// <summary>
/// Payload for <c>settings</c>: open the Settings page on <paramref name="Area"/>, and (optionally) bring one block of it into view. The Mac's
/// <c>appState.selectedSettingsTab = .connection</c> before <c>openSettings()</c> (the Overview's "Review Installation"). Nothing is changed by
/// it. A request for <c>settings</c> without a payload (the footer entry, Ctrl+,, the tray) opens on the area last shown in this session.
/// </summary>
/// <param name="Area">The area to show.</param>
/// <param name="Section">A block within it to scroll to (<see cref="InstallationSection"/>), or null for the top of the area.</param>
public sealed record SettingsAreaLink(SettingsArea Area, string? Section = null)
{
    /// <summary>Connection → Installation: what chose the installation, its access, and why it is read only.</summary>
    public const string InstallationSection = "installation";
}
