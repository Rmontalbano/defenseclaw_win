using System.Text;
using DefenseClaw.App.Services.Appearance;

namespace DefenseClaw.App.Services;

/// <summary>One entry of a menu-bar menu: a command row, a separator (which may carry a group heading), or a submenu.</summary>
internal abstract record ShellMenuEntry;

/// <summary>
/// A menu row standing for one registry command (<see cref="ShellCommandRegistry"/>): what it runs, whether it is enabled, why not, and its chord are
/// the command's own, so the menu, the palette and the toolbar can never disagree. Only the label is the menu's (Windows wording, with an access key).
/// </summary>
/// <param name="Command">The registry row, exactly as the registry built it.</param>
/// <param name="Header">The menu label, with <c>_</c> before its access key (<c>E_xit</c>); <c>__</c> is a literal underscore.</param>
/// <param name="IsChecked">True or false for a row that shows a check mark (the style in use, follow system, the sidebar); null for every other.</param>
internal sealed record ShellMenuCommand(ShellCommand Command, string Header, bool? IsChecked = null) : ShellMenuEntry
{
    public string Id => Command.Id;

    /// <summary>The label as spoken and shown without the access-key marker.</summary>
    public string Label => ShellMenuModel.WithoutAccessKey(Header);

    /// <summary>The chord shown at the right of the row (display only: the window's key handler owns every chord).</summary>
    public string? Gesture => Command.Shortcut;

    public bool IsEnabled => Command.IsEnabled;

    /// <summary>The tooltip and help text: what the row does, or why it is unavailable - the palette's line under the title, word for word.</summary>
    public string Detail => Command.IsEnabled ? Command.Description : Command.DisabledReason ?? "Not available right now.";
}

/// <summary>A separator line; with a <paramref name="Heading"/> it opens a group (the Go menu's Monitor, Govern, ...).</summary>
internal sealed record ShellMenuSeparator(string? Heading = null) : ShellMenuEntry;

/// <summary>A submenu (View's Style).</summary>
internal sealed record ShellSubmenu(string Key, string Header, IReadOnlyList<ShellMenuEntry> Entries) : ShellMenuEntry
{
    public string Label => ShellMenuModel.WithoutAccessKey(Header);
}

/// <summary>One menu of the bar (File, View, ...).</summary>
internal sealed record ShellMenu(string Key, string Header, IReadOnlyList<ShellMenuEntry> Entries)
{
    public string Label => ShellMenuModel.WithoutAccessKey(Header);
}

/// <summary>
/// The main window's menu bar (CUST-384): the Mac's command menus (App, View, Monitor, Commands, Go, Help in <c>DefenseClawApp.swift</c>'s
/// <c>.commands</c>) as Windows menus, each row an existing registry command looked up by id. The layout below says only which command goes where
/// and what the row is called; the Go menu is not a list at all - it is the sidebar, read from <see cref="PanelCatalog"/> group by group, so a panel
/// that is added, moved or gated there is in the menu the same way.
/// <para>
/// Built fresh each time a menu opens, from the registry rows of that moment. Building runs nothing: a row runs only when it is chosen.
/// </para>
/// </summary>
internal static class ShellMenuModel
{
    public const string FileKey = "file";

    public const string ViewKey = "view";

    public const string MonitorKey = "monitor";

    public const string CommandsKey = "commands";

    public const string GoKey = "go";

    public const string HelpKey = "help";

    /// <summary>The key of View's Style submenu.</summary>
    public const string StyleKey = "view.style";

    /// <summary>
    /// The bar, left to right. "File" (not the Mac's app-name menu) because that is where Windows puts Exit and where Alt+F looks for it; it holds what
    /// the Mac's app menu does (About, Settings, the update checks) plus the two ways out a tray app has, close to tray and exit.
    /// </summary>
    private static readonly MenuSpec[] Layout =
    {
        new(FileKey, "_File", new Spec[]
        {
            new Item(ShellCommandRegistry.AboutId, "_About DefenseClaw for Windows"),
            new Item("nav.settings", "_Settings"),
            new Rule(),
            new Item(ShellCommandRegistry.AppUpdatesId, "Check for a_pp updates…"),
            new Item(ShellCommandRegistry.RuntimeUpdatesId, "Check for _runtime updates…"),
            new Rule(),
            new Item(ShellCommandRegistry.CloseToTrayId, "_Close to tray"),
            new Item(ShellCommandRegistry.ExitId, "E_xit"),
        }),
        new(ViewKey, "_View", new Spec[]
        {
            new Item("app.refresh-panel", "_Refresh panel"),
            new Item("app.refresh-gateway", "Refresh _gateway status"),
            new Rule(),
            new Item("appearance.toggle", "_Toggle light/dark"),
            new Styles(StyleKey, "St_yle"),
            new Rule(),

            // The two rows with a check mark, together: WPF-UI draws the mark in a column only those rows have.
            new Item("appearance.system", "_Follow system light/dark"),
            new Item(ShellCommandRegistry.SidebarId, "Show sidebar _labels"),
        }),
        new(MonitorKey, "_Monitor", new Spec[]
        {
            new Item("app.run-doctor", "Run _health check…"),
            new Item("app.scan-ai", "_Scan AI components…"),
            new Item("app.cycle-connector", "_Cycle connector filter"),
        }),
        new(CommandsKey, "_Commands", new Spec[]
        {
            new Item(ShellCommandRegistry.PaletteId, "Command _palette…"),
            new Rule(),
            new Item("app.copy-last-output", "_Copy last command output"),
            new Item("app.export-last-output", "_Export last command output…"),
            new Item("app.diagnose", "_Diagnose in background"),
        }),
        new(GoKey, "_Go", new Spec[] { new Destinations() }),
        new(HelpKey, "_Help", new Spec[]
        {
            new Item("app.docs", "DefenseClaw _documentation"),
            new Item("app.shortcuts", "_Keyboard shortcuts"),
        }),
    };

    /// <summary>
    /// The menus for <paramref name="commands"/> (the registry rows of this moment: <see cref="ShellCommandRegistry.Build"/> plus
    /// <see cref="ShellCommandRegistry.BuildMenuOnlyCommands"/>). A row whose command is not among them is left out, as the palette leaves it out (no
    /// appearance service: no look rows; no connector scope: no cycle), and a separator with nothing left to separate goes with it.
    /// </summary>
    /// <param name="commands">The registry rows the menus are made of.</param>
    /// <param name="catalog">The sidebar the Go menu follows.</param>
    /// <param name="isChecked">The check mark of a row by command id, or null for a row that has none (see <see cref="CheckedState"/>).</param>
    public static IReadOnlyList<ShellMenu> Build(IReadOnlyList<ShellCommand> commands, PanelCatalog catalog, Func<string, bool?>? isChecked = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(catalog);

        var byId = new Dictionary<string, ShellCommand>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            _ = byId.TryAdd(command.Id, command);
        }

        ShellMenuCommand Row(ShellCommand command, string header) => new(command, header, isChecked?.Invoke(command.Id));

        var menus = new List<ShellMenu>(Layout.Length);
        foreach (var menu in Layout)
        {
            var entries = new List<ShellMenuEntry>();
            foreach (var spec in menu.Entries)
            {
                switch (spec)
                {
                    case Item item when byId.TryGetValue(item.Id, out var command):
                        entries.Add(Row(command, item.Header));
                        break;
                    case Rule:
                        entries.Add(new ShellMenuSeparator());
                        break;
                    case Styles styles:
                        var looks = AppearanceCatalog.Styles
                            .Select(style => byId.TryGetValue(StyleId(style), out var look) ? Row(look, Escape(AppearanceCatalog.Name(style))) : null)
                            .OfType<ShellMenuCommand>()
                            .ToList();
                        if (looks.Count > 0)
                        {
                            entries.Add(new ShellSubmenu(styles.Key, styles.Header, WithAccessKeys(looks)));
                        }

                        break;
                    case Destinations:
                        entries.AddRange(GoEntries(catalog, byId, Row));
                        break;
                }
            }

            menus.Add(new ShellMenu(menu.Key, menu.Header, Tidy(entries)));
        }

        return menus;
    }

    /// <summary>
    /// The Go menu: every sidebar destination in sidebar order (<see cref="PanelCatalog.SidebarOrder"/>), each group under its own heading, then the
    /// footer (<see cref="PanelCatalog.FooterPanels"/>: Settings) under a plain separator. Each row is the palette's "Go to" row (<c>nav.{id}</c>), so a
    /// panel the runtime does not offer is greyed out with the palette's sentence and shows no chord, as there.
    /// </summary>
    private static IEnumerable<ShellMenuEntry> GoEntries(PanelCatalog catalog, Dictionary<string, ShellCommand> byId, Func<ShellCommand, string, ShellMenuCommand> row)
    {
        string? group = null;
        foreach (var panel in catalog.SidebarOrder)
        {
            if (!string.Equals(panel.Group, group, StringComparison.Ordinal))
            {
                group = panel.Group;
                yield return new ShellMenuSeparator(group);
            }

            if (byId.TryGetValue(NavId(panel), out var command))
            {
                yield return row(command, Escape(panel.Title));
            }
        }

        var first = true;
        foreach (var panel in catalog.FooterPanels)
        {
            if (byId.TryGetValue(NavId(panel), out var command))
            {
                if (first)
                {
                    yield return new ShellMenuSeparator();
                    first = false;
                }

                yield return row(command, Escape(panel.Title));
            }
        }
    }

    /// <summary>The registry id of a panel's "Go to" row (<see cref="ShellCommandRegistry.BuildPanelCommands"/>).</summary>
    internal static string NavId(PanelDescriptor panel) => $"nav.{panel.Id}";

    /// <summary>The registry id of a style's row (<see cref="ShellCommandRegistry.BuildAppearanceCommands"/>: <c>appearance.linear</c>).</summary>
    internal static string StyleId(AppearanceStyle style) => "appearance." + AppearanceCatalog.Name(style).ToLowerInvariant();

    /// <summary>
    /// The check mark of the rows that have one: the style in use, "follow system" while the mode is System, and the sidebar while it is open. Null for
    /// every other row (and for the look rows when there is no appearance service).
    /// </summary>
    public static bool? CheckedState(string id, IAppearanceControl? appearance, bool sidebarOpen)
    {
        if (string.Equals(id, ShellCommandRegistry.SidebarId, StringComparison.Ordinal))
        {
            return sidebarOpen;
        }

        if (appearance is null)
        {
            return null;
        }

        if (string.Equals(id, "appearance.system", StringComparison.Ordinal))
        {
            return appearance.Mode == AppearanceMode.System;
        }

        foreach (var style in AppearanceCatalog.Styles)
        {
            if (string.Equals(id, StyleId(style), StringComparison.Ordinal))
            {
                return appearance.Style == style;
            }
        }

        return null;
    }

    /// <summary>Every command row of <paramref name="entries"/>, submenus included, in menu order.</summary>
    public static IEnumerable<ShellMenuCommand> Rows(IEnumerable<ShellMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ShellMenuCommand row:
                    yield return row;
                    break;
                case ShellSubmenu submenu:
                    foreach (var nested in Rows(submenu.Entries))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    /// <summary>The label without its access-key marker: <c>E_xit</c> reads "Exit", <c>a__b</c> reads "a_b".</summary>
    public static string WithoutAccessKey(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var text = new StringBuilder(header.Length);
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] == '_')
            {
                if (i + 1 < header.Length && header[i + 1] == '_')
                {
                    _ = text.Append('_');
                    i++;
                }

                continue;
            }

            _ = text.Append(header[i]);
        }

        return text.ToString();
    }

    /// <summary>The access key of <paramref name="header"/> (upper case), or null when it has none.</summary>
    public static char? AccessKeyOf(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        for (var i = 0; i < header.Length - 1; i++)
        {
            if (header[i] != '_')
            {
                continue;
            }

            if (header[i + 1] == '_')
            {
                i++;
                continue;
            }

            return char.ToUpperInvariant(header[i + 1]);
        }

        return null;
    }

    /// <summary>A title used as a label as it is: an underscore in it is shown, not taken for an access key.</summary>
    private static string Escape(string title) => title.Replace("_", "__", StringComparison.Ordinal);

    /// <summary>
    /// Gives each generated row (the styles) an access key: the first letter of its label that no other row of the submenu has taken, so a style can
    /// be picked by typing its letter. A row with no free letter keeps none.
    /// </summary>
    private static List<ShellMenuEntry> WithAccessKeys(List<ShellMenuCommand> rows)
    {
        var used = new HashSet<char>(rows.Select(r => AccessKeyOf(r.Header)).OfType<char>());
        var keyed = new List<ShellMenuEntry>(rows.Count);
        foreach (var row in rows)
        {
            if (AccessKeyOf(row.Header) is not null)
            {
                keyed.Add(row);
                continue;
            }

            var header = row.Header;
            var at = -1;
            for (var i = 0; i < header.Length; i++)
            {
                if (char.IsLetterOrDigit(header[i]) && (i == 0 || header[i - 1] != '_') && !used.Contains(char.ToUpperInvariant(header[i])))
                {
                    at = i;
                    break;
                }
            }

            if (at < 0)
            {
                keyed.Add(row);
                continue;
            }

            _ = used.Add(char.ToUpperInvariant(header[at]));
            keyed.Add(row with { Header = header.Insert(at, "_") });
        }

        return keyed;
    }

    /// <summary>
    /// No plain separator first, last, twice in a row, or just before or after a group heading (the heading already separates), and no heading with
    /// nothing under it.
    /// </summary>
    internal static List<ShellMenuEntry> Tidy(List<ShellMenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var tidy = new List<ShellMenuEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is ShellMenuSeparator separator)
            {
                // A plain separator before this one separates nothing now; a heading before another heading heads an empty group.
                while (tidy.Count > 0 && tidy[^1] is ShellMenuSeparator previous && (previous.Heading is null || separator.Heading is not null))
                {
                    tidy.RemoveAt(tidy.Count - 1);
                }

                if (separator.Heading is null && (tidy.Count == 0 || tidy[^1] is ShellMenuSeparator))
                {
                    continue;
                }
            }

            tidy.Add(entry);
        }

        while (tidy.Count > 0 && tidy[^1] is ShellMenuSeparator)
        {
            tidy.RemoveAt(tidy.Count - 1);
        }

        return tidy;
    }

    private abstract record Spec;

    /// <summary>The registry row with this id, under this label.</summary>
    private sealed record Item(string Id, string Header) : Spec;

    /// <summary>A plain separator.</summary>
    private sealed record Rule : Spec;

    /// <summary>The appearance styles, as a submenu of their own.</summary>
    private sealed record Styles(string Key, string Header) : Spec;

    /// <summary>The sidebar's destinations, group by group, then the footer.</summary>
    private sealed record Destinations : Spec;

    private sealed record MenuSpec(string Key, string Header, Spec[] Entries);
}
