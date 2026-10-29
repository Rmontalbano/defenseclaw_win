namespace DefenseClaw.App.Services;

/// <summary>
/// What the dashboard window itself does for its menu bar (CUST-384): the things only a window can do - open its own palette, hide to the tray,
/// ask the app to exit, show or hide its sidebar. Each delegate is the window's existing path for that (the palette button's, the close button's,
/// the close-with-"keep in tray"-off exit, the sidebar's pane toggle); nothing here is a second way to do it.
/// </summary>
/// <param name="OpenPalette">Opens the command palette, as Ctrl+K and the status strip's Search button do.</param>
/// <param name="CloseToTray">Hides the window to the tray, as the close button does while "Closing the window keeps DefenseClaw in the tray" is on.</param>
/// <param name="Exit">Asks the app to exit through the one Exit path (the tray's), with its upgrade-running and unsaved-editor questions.</param>
/// <param name="IsSidebarOpen">Whether the sidebar is open (its labels showing) right now.</param>
/// <param name="ToggleSidebar">Opens or collapses the sidebar, as its pane toggle does.</param>
internal sealed record ShellWindowCommands(
    Action OpenPalette,
    Action CloseToTray,
    Action Exit,
    Func<bool> IsSidebarOpen,
    Action ToggleSidebar);

/// <summary>The rows only the main window's menu bar lists (CUST-384); the palette's own rows are <see cref="Build"/>'s.</summary>
internal static partial class ShellCommandRegistry
{
    /// <summary>The id of "Command palette…" (Ctrl+K). A palette row that opened the palette would make no sense, so only the menu lists it.</summary>
    public const string PaletteId = "app.palette";

    /// <summary>The id of "About DefenseClaw for Windows".</summary>
    public const string AboutId = "app.about";

    /// <summary>The id of "Check for app updates…": this app's own release (Rmontalbano/defenseclaw_win), the tray's "Check for app updates…".</summary>
    public const string AppUpdatesId = "app.check-app-updates";

    /// <summary>The id of the palette's "Check for updates": the DefenseClaw runtime's release, in the Updates window.</summary>
    public const string RuntimeUpdatesId = "app.check-updates";

    /// <summary>The id of "Close to tray": the window hides, the app keeps watching from the tray.</summary>
    public const string CloseToTrayId = "app.close-to-tray";

    /// <summary>The id of "Exit": the app ends, tray icon and all, through the tray's Exit path.</summary>
    public const string ExitId = "app.exit";

    /// <summary>The id of "Show sidebar labels": opens or collapses the sidebar (its pane toggle).</summary>
    public const string SidebarId = "app.toggle-sidebar";

    /// <summary>
    /// The menu bar's own rows: the window's (<paramref name="window"/>) and the two app actions the palette does not list, About and this app's update
    /// check. Built when a menu opens, like <see cref="Build"/>, so the enabled states are the truth at that moment. Building them runs nothing.
    /// </summary>
    internal static List<ShellCommand> BuildMenuOnlyCommands(ShellActions actions, ShellWindowCommands window)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(window);

        var commands = new List<ShellCommand>
        {
            new(
                Id: PaletteId,
                Title: "Command palette",
                Category: AppCategory,
                Description: "Search every panel, action and CLI command, and run one.",
                Shortcut: ShellShortcuts.PaletteText,
                Keywords: "palette search commands find run",
                IsEnabled: true,
                DisabledReason: null,
                Run: window.OpenPalette),
            new(
                Id: AboutId,
                Title: "About DefenseClaw for Windows",
                Category: AppCategory,
                Description: "This app's version, the DefenseClaw runtime it found, and where the docs are.",
                Shortcut: null,
                Keywords: "about version build license",
                IsEnabled: true,
                DisabledReason: null,
                Run: actions.ShowAbout),
        };

        // This app's own update check (the tray's "Check for app updates…"): off while one is already running or an update is being installed.
        var (canCheckApp, appCheckReason) = actions.AppUpdateCheck;
        commands.Add(new ShellCommand(
            Id: AppUpdatesId,
            Title: "Check for app updates",
            Category: AppCategory,
            Description: "Look for a newer DefenseClaw for Windows (this app) on GitHub now. A newer release opens its review; nothing installs until you confirm.",
            Shortcut: null,
            Keywords: "update upgrade app companion windows self release",
            IsEnabled: canCheckApp,
            DisabledReason: appCheckReason,
            Run: actions.CheckForAppUpdates));

        commands.Add(new ShellCommand(
            Id: CloseToTrayId,
            Title: "Close to tray",
            Category: AppCategory,
            Description: "Hide this window. DefenseClaw keeps watching the gateway from the tray; click the shield to come back.",
            Shortcut: null,
            Keywords: "hide minimize tray close window",
            IsEnabled: true,
            DisabledReason: null,
            Run: window.CloseToTray));

        commands.Add(new ShellCommand(
            Id: ExitId,
            Title: "Exit",
            Category: AppCategory,
            Description: "Quit DefenseClaw for Windows, tray icon included. Asks first while an upgrade is running or the config editor has unsaved changes.",
            Shortcut: null,
            Keywords: "quit exit close end shutdown",
            IsEnabled: true,
            DisabledReason: null,
            Run: window.Exit));

        // A check-mark row in the View menu: checked while the sidebar is open. The description says which way a click goes.
        var sidebarOpen = window.IsSidebarOpen();
        commands.Add(new ShellCommand(
            Id: SidebarId,
            Title: "Show sidebar labels",
            Category: AppCategory,
            Description: sidebarOpen
                ? "The sidebar is open; choose this to collapse it to its icons."
                : "The sidebar shows only icons; choose this to open it so every panel shows its name.",
            Shortcut: null,
            Keywords: "sidebar navigation pane collapse expand",
            IsEnabled: true,
            DisabledReason: null,
            Run: window.ToggleSidebar));

        return commands;
    }
}
