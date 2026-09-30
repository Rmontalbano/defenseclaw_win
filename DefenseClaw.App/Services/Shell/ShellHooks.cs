namespace DefenseClaw.App.Services;

/// <summary>
/// The few things a panel can ask of the shell that live outside <see cref="AppServices"/>, because they belong to the tray (which is built
/// after the services and outlives every window). <see cref="PanelCatalog.Hooks"/> is the one instance; the dashboard window fills it in
/// when it is built, which is also the only time a panel that could use one is on screen. A hook that nobody wired (a test host) is null
/// and the panel says so instead of doing nothing quietly.
/// </summary>
internal sealed class ShellHooks
{
    /// <summary>
    /// Forgets which findings have been announced, so what is outstanding is announced once more: <see cref="TrayIconService.ResetSeenAlertHistoryAsync"/>,
    /// the same call the command palette's "Reset seen-alert history" makes (through <see cref="ShellActions"/>).
    /// </summary>
    public Func<Task>? ResetSeenAlertHistory { get; set; }
}
