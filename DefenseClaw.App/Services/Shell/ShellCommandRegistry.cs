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
    /// Builds the list for the moment the palette opens. Enabled states and toggle titles are read
    /// now, so the palette is never showing launch-time state.
    /// </summary>
    /// <param name="catalog">Source of the panel entries and their chord order.</param>
    /// <param name="actions">The app actions and the availability rules.</param>
    /// <param name="navigateTo">Selects a panel in the sidebar.</param>
    /// <param name="showShortcuts">Opens the keyboard-shortcuts overlay.</param>
    public static IReadOnlyList<ShellCommand> Build(
        PanelCatalog catalog,
        ShellActions actions,
        Action<PanelDescriptor> navigateTo,
        Action showShortcuts)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(navigateTo);
        ArgumentNullException.ThrowIfNull(showShortcuts);

        var commands = new List<ShellCommand>();

        // Panels, in sidebar order, so the first screen of an empty search matches the sidebar and
        // each row shows the Ctrl+N chord that jumps there.
        for (var i = 0; i < catalog.SidebarOrder.Count; i++)
        {
            var panel = catalog.SidebarOrder[i];
            commands.Add(new ShellCommand(
                Id: $"nav.{panel.Id}",
                Title: $"Go to {panel.Title}",
                Category: PanelCategory,
                Description: $"{panel.Group} · opens the {panel.Title} panel",
                Shortcut: ShellShortcuts.PanelChordText(i),
                Keywords: $"open navigate {panel.Id} {panel.Group}",
                IsEnabled: true,
                DisabledReason: null,
                Run: () => navigateTo(panel)));
        }

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
            Id: "app.check-updates",
            Title: "Check for updates",
            Category: AppCategory,
            Description: "Open the Updates window, which checks for a newer DefenseClaw release.",
            Shortcut: null,
            Keywords: "update upgrade version release new",
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
            Id: "app.shortcuts",
            Title: "Show keyboard shortcuts",
            Category: AppCategory,
            Description: "The list of every shortcut in the dashboard.",
            Shortcut: ShellShortcuts.HelpText,
            Keywords: "keys keyboard help hotkeys ?",
            IsEnabled: true,
            DisabledReason: null,
            Run: showShortcuts));

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
