using DefenseClaw.App.Services.Appearance;

namespace DefenseClaw.App.Services;

/// <summary>
/// Builds the command palette's list: a "Go to" entry for every panel, the app actions, and the
/// gateway controls. A fixed vocabulary — the palette never runs free-form text, so there is
/// nothing in it that can reach the CLI except through the same reviewed paths the tray uses.
/// </summary>
internal static class ShellCommandRegistry
{
    public const string PanelCategory = "Panel";

    public const string AppCategory = "App";

    public const string GatewayCategory = "Gateway";

    /// <summary>
    /// The "Go to" entry for every panel, in sidebar order, so the first screen of an empty search
    /// matches the sidebar and each row shows the Ctrl+N chord that jumps there (Settings, last, shows Ctrl+,). Split out of
    /// <see cref="Build"/> unchanged so it can be built without the tray-backed <see cref="ShellActions"/>.
    /// </summary>
    internal static List<ShellCommand> BuildPanelCommands(PanelCatalog catalog, Action<PanelDescriptor> navigateTo)
    {
        var commands = new List<ShellCommand>();
        for (var i = 0; i < catalog.SidebarOrder.Count; i++)
        {
            commands.Add(GoTo(catalog.SidebarOrder[i], ShellShortcuts.PanelChordText(i), navigateTo));
        }

        // The footer panel (Settings) is reached by Ctrl+, rather than a number.
        foreach (var panel in catalog.FooterPanels)
        {
            commands.Add(GoTo(panel, string.Equals(panel.Id, "settings", StringComparison.Ordinal) ? ShellShortcuts.SettingsText : null, navigateTo));
        }

        return commands;
    }

    private static ShellCommand GoTo(PanelDescriptor panel, string? chord, Action<PanelDescriptor> navigateTo) =>
        new(
            Id: $"nav.{panel.Id}",
            Title: $"Go to {panel.Title}",
            Category: PanelCategory,
            Description: $"{panel.Group} · opens the {panel.Title} panel",
            Shortcut: chord,
            Keywords: $"open navigate {panel.Id} {panel.Group}" + (string.Equals(panel.Id, "settings", StringComparison.Ordinal) ? " preferences options" : string.Empty),
            IsEnabled: true,
            DisabledReason: null,
            Run: () => navigateTo(panel));

    /// <summary>
    /// The appearance commands: one per style, the light/dark toggle, and "follow system". Built from the service's state
    /// at that moment, so the toggle says which way it goes and the row for what is already chosen says so instead of
    /// pretending to do something. Separate from <see cref="Build"/> so it needs no tray-backed <see cref="ShellActions"/>.
    /// </summary>
    internal static List<ShellCommand> BuildAppearanceCommands(IAppearanceControl appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        var commands = new List<ShellCommand>();
        foreach (var style in AppearanceCatalog.Styles)
        {
            var captured = style;
            var name = AppearanceCatalog.Name(style);
            commands.Add(new ShellCommand(
                Id: $"appearance.{name.ToLowerInvariant()}",
                Title: $"Appearance: {name}",
                Category: AppCategory,
                Description: AppearanceCatalog.Description(style) + " Keeps light or dark as it is.",
                Shortcut: null,
                Keywords: "theme style look skin appearance colors " + style switch
                {
                    AppearanceStyle.Linear => "product indigo clean compact",
                    AppearanceStyle.Tui => "terminal console cli monospace",
                    AppearanceStyle.Cisco => "mac macos brand blue rounded",
                    _ => "fluent mica windows",
                },
                IsEnabled: appearance.Style != style,
                DisabledReason: $"The {name} style is already in use.",
                Run: () => appearance.SetStyle(captured)));
        }

        commands.Add(new ShellCommand(
            Id: "appearance.toggle",
            Title: "Toggle light/dark",
            Category: AppCategory,
            Description: appearance.IsDark
                ? "Switch to the light look (fixed, no longer following Windows)."
                : "Switch to the dark look (fixed, no longer following Windows).",
            Shortcut: ShellShortcuts.ToggleThemeText,
            Keywords: "theme dark light night day mode switch flip",
            IsEnabled: true,
            DisabledReason: null,
            Run: appearance.ToggleLightDark));

        commands.Add(new ShellCommand(
            Id: "appearance.system",
            Title: "Mode: follow system",
            Category: AppCategory,
            Description: "Follow Windows' light or dark setting, and switch when it does.",
            Shortcut: null,
            Keywords: "theme dark light auto automatic windows os mode",
            IsEnabled: appearance.Mode != AppearanceMode.System,
            DisabledReason: "Already following the system.",
            Run: () => appearance.SetMode(AppearanceMode.System)));

        return commands;
    }

    /// <summary>
    /// Builds the list for the moment the palette opens. Enabled states and toggle titles are read
    /// now, so the palette is never showing launch-time state.
    /// </summary>
    /// <param name="catalog">Source of the panel entries and their chord order.</param>
    /// <param name="actions">The app actions and the availability rules.</param>
    /// <param name="navigateTo">Selects a panel in the sidebar.</param>
    /// <param name="showShortcuts">Opens the keyboard-shortcuts overlay.</param>
    /// <param name="appearance">The look controls; null (a test without a service) leaves the appearance commands out.</param>
    public static IReadOnlyList<ShellCommand> Build(
        PanelCatalog catalog,
        ShellActions actions,
        Action<PanelDescriptor> navigateTo,
        Action showShortcuts,
        IAppearanceControl? appearance = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(navigateTo);
        ArgumentNullException.ThrowIfNull(showShortcuts);

        var commands = BuildPanelCommands(catalog, navigateTo);

        var canRefresh = actions.CanRefreshCurrentPanel;
        commands.Add(new ShellCommand(
            Id: "app.refresh-panel",
            Title: "Refresh current panel",
            Category: AppCategory,
            Description: "Reload the panel you are looking at.",
            Shortcut: ShellShortcuts.RefreshText,
            Keywords: "reload update data",
            IsEnabled: canRefresh,
            DisabledReason: "The current panel has no refresh action, or it is already refreshing.",
            Run: () => actions.TryRefreshCurrentPanel()));

        commands.Add(new ShellCommand(
            Id: "app.refresh-gateway",
            Title: "Refresh gateway status",
            Category: AppCategory,
            Description: "Poll the gateway now instead of waiting for the next automatic check.",
            Shortcut: null,
            Keywords: "poll health reload status strip",
            IsEnabled: true,
            DisabledReason: null,
            Run: actions.RefreshGatewayStatus));

        commands.Add(new ShellCommand(
            Id: "app.config-editor",
            Title: "Open config editor",
            Category: AppCategory,
            Description: "Edit ~/.defenseclaw/config.yaml with validation and a backup on save.",
            Shortcut: null,
            Keywords: "config yaml settings edit configuration file",
            IsEnabled: true,
            DisabledReason: null,
            Run: actions.OpenConfigEditor));

        commands.Add(new ShellCommand(
            Id: "app.run-doctor",
            Title: "Run doctor",
            Category: AppCategory,
            Description: "Open the Overview's Doctor card on its Run doctor button. Nothing runs until you press it.",
            Shortcut: null,
            Keywords: "doctor health check diagnose probe fix problems keys credentials",
            IsEnabled: true,
            DisabledReason: null,
            Run: () => actions.OpenPanel("overview", new OverviewFocus(OverviewFocus.DoctorSection))));

        commands.Add(new ShellCommand(
            Id: "app.check-updates",
            Title: "Check for updates",
            Category: AppCategory,
            Description: "Look for a newer DefenseClaw release now and open the Updates window to review it.",
            Shortcut: null,
            Keywords: "update upgrade version release new banner available",
            IsEnabled: true,
            DisabledReason: null,
            Run: actions.CheckForUpdates));

        var autostart = actions.IsAutostartEnabled;
        commands.Add(new ShellCommand(
            Id: "app.toggle-autostart",
            Title: autostart ? "Turn off Start with Windows" : "Turn on Start with Windows",
            Category: AppCategory,
            Description: autostart
                ? "Stop launching DefenseClaw to the tray when you sign in."
                : "Launch DefenseClaw minimized to the tray when you sign in.",
            Shortcut: null,
            Keywords: "autostart startup login sign in boot toggle",
            IsEnabled: true,
            DisabledReason: null,
            Run: actions.ToggleAutostart));

        commands.Add(new ShellCommand(
            Id: "app.reset-seen-alerts",
            Title: "Reset seen-alert history",
            Category: AppCategory,
            Description: "Announce the CRITICAL and HIGH findings that are still unacknowledged once more, as one notification.",
            Shortcut: null,
            Keywords: "notifications toast alerts seen announced forget repeat high-water mark findings",
            IsEnabled: true,
            DisabledReason: null,
            Run: actions.ResetSeenAlertHistory));

        commands.Add(new ShellCommand(
            Id: "app.shortcuts",
            Title: "Show keyboard shortcuts",
            Category: AppCategory,
            Description: "The list of every shortcut in the dashboard.",
            Shortcut: ShellShortcuts.HelpText,
            Keywords: "keys keyboard help hotkeys ?",
            IsEnabled: true,
            DisabledReason: null,
            Run: showShortcuts));

        if (appearance is not null)
        {
            commands.AddRange(BuildAppearanceCommands(appearance));
        }

        // Gateway controls. Each one opens the review dialog (exact argv, then confirm) before
        // anything runs, exactly like the tray menu's.
        var snapshot = actions.Snapshot;
        foreach (var action in new[] { GatewayAction.Start, GatewayAction.Stop, GatewayAction.Restart })
        {
            var (allowed, reason) = GatewayControl.Availability(action, snapshot);
            var captured = action;
            commands.Add(new ShellCommand(
                Id: $"gateway.{GatewayControl.Verb(action)}",
                Title: GatewayControl.Title(action),
                Category: GatewayCategory,
                Description: GatewayControl.Summary(action) + " You review the command first.",
                Shortcut: null,
                Keywords: "daemon sidecar service defenseclaw-gateway",
                IsEnabled: allowed,
                DisabledReason: reason,
                Run: () => _ = actions.RunGatewayActionAsync(captured)));
        }

        return commands;
    }
}
